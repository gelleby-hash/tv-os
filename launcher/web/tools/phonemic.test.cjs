// Runs PageScripts.PhoneMic (read from the C# file, so there is one copy) against a fake YouTube page: fake
// navigator.mediaDevices and navigator.permissions, a fake AudioContext with a stream destination and buffer sources,
// and a fake chrome.webview carrying the launcher's messages. YouTube's calls are the ones seen in the spike
// (2026-10-31).
'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

const source = fs.readFileSync(path.join(__dirname, '..', '..', 'src', 'TvLauncher', 'Apps', 'PageScripts.cs'), 'utf8');
const script = (() => {
  const m = source.match(/PhoneMic = """\r?\n([\s\S]*?)\r?\n\s*""";/);
  assert.ok(m, 'PhoneMic raw string found in PageScripts.cs');
  return m[1];
})();

// What YouTube asks for when its microphone is pressed (spike, item 1).
const YT_AUDIO = { audio: { sampleSize: 16, channelCount: 1 }, video: false };

class FakeTrack {
  constructor() { this.kind = 'audio'; this.readyState = 'live'; }
  stop() { this.readyState = 'ended'; }
}

function fakePage({ webview = true, devices = [{ kind: 'videoinput' }, { kind: 'audiooutput' }], enumerate } = {}) {
  const contexts = [];
  const realCalls = [];
  const posted = [];
  const listeners = [];
  const timers = [];
  const warnings = [];

  class AudioContext {
    constructor(opts) {
      this.opts = opts;
      this.sampleRate = opts && opts.sampleRate;
      this.currentTime = 0;
      this.state = 'running';
      this.closed = false;
      this.started = [];
      this.destinations = [];
      contexts.push(this);
    }
    createMediaStreamDestination() {
      const track = new FakeTrack();
      const dest = { stream: { tracks: [track], getTracks() { return this.tracks.slice(); }, getAudioTracks() { return this.tracks.slice(); } } };
      this.destinations.push(dest);
      return dest;
    }
    createBuffer(channels, length, rate) {
      const data = new Float32Array(length);
      return { numberOfChannels: channels, length, sampleRate: rate, duration: length / rate, getChannelData: () => data };
    }
    createBufferSource() {
      const ctx = this;
      return { buffer: null, to: null, connect(d) { this.to = d; }, start(at) { this.at = at; ctx.started.push(this); } };
    }
    resume() { this.state = 'running'; return Promise.resolve(); }
    close() { this.closed = true; this.state = 'closed'; return Promise.resolve(); }
  }
  class MediaDeviceInfo {}

  const mediaDevices = {
    getUserMedia(c) { realCalls.push(c); return Promise.resolve('real stream'); },
    enumerateDevices: enumerate || (() => Promise.resolve(devices.slice())),
  };
  const permissions = { query(d) { return Promise.resolve({ name: d.name, state: 'prompt', real: true }); } };
  const originals = { getUserMedia: mediaDevices.getUserMedia, enumerateDevices: mediaDevices.enumerateDevices, query: permissions.query };
  const window = webview
    ? { chrome: { webview: { addEventListener: (type, fn) => listeners.push({ type, fn }), postMessage: (m) => posted.push(m) } } }
    : {};

  const ctx = vm.createContext({
    window,
    navigator: { mediaDevices, permissions },
    AudioContext,
    MediaDeviceInfo,
    atob,
    setTimeout: (...a) => { timers.push(a); },
    setInterval: (...a) => { timers.push(a); },
    console: { warn: (...a) => warnings.push(a) },
  });
  vm.runInContext(script, ctx, { filename: 'PhoneMic' });

  const send = (data) => { for (const l of listeners) if (l.type === 'message') l.fn({ data }); };
  return { window, mediaDevices, permissions, originals, contexts, realCalls, posted, listeners, timers, warnings, send, MediaDeviceInfo };
}

// Base64 of PCM16 little-endian samples, as the launcher sends them.
function pcm(samples) {
  const b = Buffer.alloc(samples.length * 2);
  samples.forEach((s, i) => b.writeInt16LE(s, i * 2));
  return b.toString('base64');
}
const silence = (n) => pcm(new Array(n).fill(0));

test('mic: listens for the launcher\'s messages and does no work while idle', () => {
  const p = fakePage();
  assert.equal(p.listeners.length, 1);
  assert.equal(p.listeners[0].type, 'message');
  assert.equal(p.timers.length, 0, 'no timers');
  assert.equal(p.contexts.length, 0, 'no audio context until YouTube asks during a session');
  assert.deepEqual(p.posted, []);
});

test('mic: returnsPhoneStreamDuringSession', async () => {
  const p = fakePage();
  p.send({ tvMic: 'start' });
  const stream = await p.mediaDevices.getUserMedia(YT_AUDIO);
  assert.equal(p.realCalls.length, 0, 'the real getUserMedia is not called');
  assert.equal(p.contexts.length, 1);
  assert.equal(p.contexts[0].sampleRate, 16000);
  assert.equal(stream, p.contexts[0].destinations[0].stream, 'the destination node\'s stream');
  assert.deepEqual(p.posted, []);
});

test('mic: fallsBackOutsideSession', async () => {
  const p = fakePage();
  assert.equal(await p.mediaDevices.getUserMedia(YT_AUDIO), 'real stream', 'before start');
  p.send({ tvMic: 'start' });
  await p.mediaDevices.getUserMedia(YT_AUDIO);
  p.send({ tvMic: 'end' });
  assert.equal(await p.mediaDevices.getUserMedia(YT_AUDIO), 'real stream', 'after end');
  assert.equal(p.realCalls.length, 2);
  assert.equal(p.realCalls[1], YT_AUDIO, 'the constraints are passed on unchanged');
});

test('mic: a start arms one request only, and only an audio request without video', async () => {
  const p = fakePage();
  p.send({ tvMic: 'start' });
  assert.equal(await p.mediaDevices.getUserMedia({ audio: true, video: true }), 'real stream', 'a camera request is not ours');
  await p.mediaDevices.getUserMedia(YT_AUDIO);
  assert.equal(await p.mediaDevices.getUserMedia(YT_AUDIO), 'real stream', 'the next request after the phone\'s');
  assert.equal(p.contexts.length, 1);
});

test('mic: audioIsQueuedInOrder', async () => {
  const p = fakePage();
  p.send({ tvMic: 'start' });
  const stream = await p.mediaDevices.getUserMedia(YT_AUDIO);
  const ctx = p.contexts[0];
  ctx.currentTime = 2;
  // About 3200 bytes per message (the launcher's chunk): 1600 samples, 0.1 s.
  for (let i = 0; i < 3; i++) p.send({ tvMic: 'audio', pcm: silence(1600) });
  assert.equal(ctx.started.length, 3);
  assert.equal(ctx.started[0].at, 2, 'the first plays at once');
  for (let i = 1; i < 3; i++) {
    const prev = ctx.started[i - 1];
    assert.equal(ctx.started[i].at, prev.at + prev.buffer.duration, 'each starts where the previous ends');
  }
  for (const s of ctx.started) assert.equal(s.to.stream, stream, 'played into the stream YouTube has');

  // After a gap in the network the next one starts now, not in the past.
  ctx.currentTime = 10;
  p.send({ tvMic: 'audio', pcm: silence(1600) });
  assert.equal(ctx.started[3].at, 10);
});

test('mic: PCM16 little-endian becomes samples from -1 to 1', async () => {
  const p = fakePage();
  p.send({ tvMic: 'start' });
  await p.mediaDevices.getUserMedia(YT_AUDIO);
  p.send({ tvMic: 'audio', pcm: pcm([0, 16384, -32768, 32767, -1]) });
  const b = p.contexts[0].started[0].buffer;
  assert.equal(b.numberOfChannels, 1);
  assert.equal(b.sampleRate, 16000);
  assert.deepEqual(Array.from(b.getChannelData(0)), [0, 0.5, -1, 32767 / 32768, -1 / 32768]);
});

test('mic: audio that arrives before YouTube asks is kept and played first', async () => {
  const p = fakePage();
  p.send({ tvMic: 'audio', pcm: silence(1600) });   // no session: dropped
  p.send({ tvMic: 'start' });
  p.send({ tvMic: 'audio', pcm: silence(1600) });
  p.send({ tvMic: 'audio', pcm: silence(800) });
  await p.mediaDevices.getUserMedia(YT_AUDIO);
  const ctx = p.contexts[0];
  assert.equal(ctx.started.length, 2);
  assert.deepEqual(ctx.started.map(s => s.at), [0, 0.1]);
  assert.equal(ctx.started[1].buffer.length, 800);
});

test('mic: stoppingTheTrackTellsLauncher', async () => {
  const p = fakePage();
  p.send({ tvMic: 'start' });
  const stream = await p.mediaDevices.getUserMedia(YT_AUDIO);
  const track = stream.getTracks()[0];
  track.stop();
  assert.equal(track.readyState, 'ended');
  track.stop();
  p.send({ tvMic: 'end' });
  assert.equal(JSON.stringify(p.posted), JSON.stringify([{ tvMic: 'stopped' }]), 'once');
  assert.equal(p.contexts[0].closed, true, 'the context is closed');
  p.send({ tvMic: 'audio', pcm: silence(1600) });
  assert.equal(p.contexts[0].started.length, 0, 'audio after the stop goes nowhere');
});

test('mic: endStopsTheTrack', async () => {
  const p = fakePage();
  p.send({ tvMic: 'start' });
  const stream = await p.mediaDevices.getUserMedia(YT_AUDIO);
  p.send({ tvMic: 'end' });
  const track = stream.getTracks()[0];
  assert.equal(track.readyState, 'ended');
  track.stop();   // YouTube tidying up after
  p.send({ tvMic: 'end' });
  assert.equal(JSON.stringify(p.posted), JSON.stringify([{ tvMic: 'stopped' }]), 'once');
  assert.equal(p.contexts[0].closed, true);
});

test('mic: an end before YouTube asks disarms and posts nothing', async () => {
  const p = fakePage();
  p.send({ tvMic: 'start' });
  p.send({ tvMic: 'end' });
  assert.equal(await p.mediaDevices.getUserMedia(YT_AUDIO), 'real stream');
  assert.deepEqual(p.posted, []);
});

test('mic: a new start closes a stream still open, without telling the launcher it stopped', async () => {
  const p = fakePage();
  p.send({ tvMic: 'start' });
  const first = await p.mediaDevices.getUserMedia(YT_AUDIO);
  p.send({ tvMic: 'start' });
  assert.equal(first.getTracks()[0].readyState, 'ended');
  assert.equal(p.contexts[0].closed, true);
  const second = await p.mediaDevices.getUserMedia(YT_AUDIO);
  assert.notEqual(second, first);
  assert.deepEqual(p.posted, [], 'the launcher started the new session itself');
});

test('mic: the arm hook the launcher awaits arms the next audio request like start does', async () => {
  const p = fakePage();
  const key = Symbol.for('tvMic.arm');
  const d = Object.getOwnPropertyDescriptor(p.window, key);
  assert.ok(d, 'defined on window');
  assert.equal(d.enumerable, false);
  assert.equal(d.writable, false);
  assert.equal(Object.keys(p.window).includes('tvMic.arm'), false);

  assert.equal(p.window[key](), true, 'returns true when armed (ExecuteScriptAsync reads "true")');
  const stream = await p.mediaDevices.getUserMedia(YT_AUDIO);
  assert.equal(p.realCalls.length, 0);
  assert.equal(stream, p.contexts[0].destinations[0].stream);
  assert.equal(await p.mediaDevices.getUserMedia(YT_AUDIO), 'real stream', 'one request only');

  // Over an open stream: closed without telling the launcher, as a second start is.
  p.window[key]();
  const again = await p.mediaDevices.getUserMedia(YT_AUDIO);
  assert.equal(stream.getTracks()[0].readyState, 'ended');
  assert.notEqual(again, stream);
  assert.deepEqual(p.posted, []);
});

test('mic: no arm hook outside the launcher', () => {
  const p = fakePage({ webview: false });
  assert.equal(Object.getOwnPropertyDescriptor(p.window, Symbol.for('tvMic.arm')), undefined);
});

test('mic: reportsAMicrophone', async () => {
  // The box has no microphone, and YouTube hides its own unless one audioinput is listed (spike, items 2 and 3).
  const p = fakePage();
  const list = await p.mediaDevices.enumerateDevices();
  const inputs = list.filter(d => d.kind === 'audioinput');
  assert.equal(inputs.length, 1);
  assert.ok(inputs[0] instanceof p.MediaDeviceInfo);
  assert.equal(inputs[0].label, 'Phone');
  assert.equal(JSON.stringify(inputs[0].toJSON()), JSON.stringify({ deviceId: 'tv-phone', kind: 'audioinput', label: 'Phone', groupId: 'tv-phone' }));
  assert.equal(list.slice(0, 2).map(d => d.kind).join(), 'videoinput,audiooutput', 'the real devices are kept');

  assert.equal((await p.permissions.query({ name: 'microphone' })).state, 'granted');
  const cam = await p.permissions.query({ name: 'camera' });
  assert.equal(cam.real, true, 'other names go to the real method');
});

test('mic: a device list that fails, or a page without one, still lists the phone', async () => {
  for (const enumerate of [() => Promise.reject(new Error('NotAllowed')), () => { throw new Error('sync'); }]) {
    const p = fakePage({ enumerate });
    const list = await p.mediaDevices.enumerateDevices();
    assert.equal(list.map(d => d.kind).join(), 'audioinput');
  }
});

test('mic: neverThrowsOnJunk', async () => {
  const p = fakePage();
  p.send({ tvMic: 'start' });
  await p.mediaDevices.getUserMedia(YT_AUDIO);
  const junk = [undefined, null, 42, 'start', '{"tvMic":"end"}', [], {}, { tvMic: 'nope' }, { tvMic: 7 },
    { tvMic: 'audio' }, { tvMic: 'audio', pcm: 12 }, { tvMic: 'audio', pcm: '%%% not base64 %%%' }, { tvMic: 'audio', pcm: '' },
    { tvMic: 'audio', pcm: 'AA==' }];
  for (const data of junk) assert.doesNotThrow(() => p.send(data));
  assert.equal(p.contexts[0].started.length, 0, 'nothing played');
  assert.deepEqual(p.posted, [], 'nothing ended');
  p.send({ tvMic: 'audio', pcm: silence(10) });
  assert.equal(p.contexts[0].started.length, 1, 'the session still works');
});

test('mic: outside the launcher (no chrome.webview) the page is left alone', () => {
  const p = fakePage({ webview: false });
  assert.equal(p.listeners.length, 0);
  assert.equal(p.mediaDevices.getUserMedia, p.originals.getUserMedia);
  assert.equal(p.mediaDevices.enumerateDevices, p.originals.enumerateDevices);
  assert.equal(p.permissions.query, p.originals.query);
  assert.deepEqual(p.warnings, []);
});
