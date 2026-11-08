// Manual measuring tool for an app page in the dev launcher: the DevTools protocol over the app's remote debugging
// port, from Node (the global WebSocket, Node 22 or later). Not an automated test.
//
// The launcher opens the port only under --dev: app n (0-based in "Apps") listens on 9300 + n, so with the
// repository's apps Channel 4 is 9302, Netflix 9303, Prime Video 9304 and Plex 9305. Only one launcher can run, so close the
// desktop one first.
//   node launcher/tests/manual/measure-app.cjs <port> hook         arm the DRM recorder, then reload the page
//   node launcher/tests/manual/measure-app.cjs <port> video        print the page's url, the DRM record and every <video>
//   node launcher/tests/manual/measure-app.cjs <port> focus        print the focused element
//   node launcher/tests/manual/measure-app.cjs <port> key <name>   one key press: ArrowLeft ArrowRight ArrowUp ArrowDown Enter Escape Space
//
// hook keeps running after the reload and stays connected until Ctrl+C (run it in the background): DevTools drops a
// script added for new documents when its connection closes, so the recorder lives only as long as hook does. Run
// video or key from another process meanwhile. window.__drm then holds { requested, created }: each
// requestMediaKeySystemAccess call (key system, the robustness levels asked for, how it ended) and each
// createMediaKeys call. A page that never asks for a key system plays nothing protected.
//
// The key press is a real key event inside the page, as the launcher sends Space to an app it pauses; it does not
// go through Windows, so the launcher's own keys (Home, Back) are not reachable this way.
//
// Exit codes: 0 done, 1 could not connect, no page, or the page did not answer, 2 wrong arguments.

'use strict';

const KEYS = {
  ArrowLeft: { key: 'ArrowLeft', code: 'ArrowLeft', vk: 37 },
  ArrowUp: { key: 'ArrowUp', code: 'ArrowUp', vk: 38 },
  ArrowRight: { key: 'ArrowRight', code: 'ArrowRight', vk: 39 },
  ArrowDown: { key: 'ArrowDown', code: 'ArrowDown', vk: 40 },
  Enter: { key: 'Enter', code: 'Enter', vk: 13, text: '\r' },
  Escape: { key: 'Escape', code: 'Escape', vk: 27 },
  Space: { key: ' ', code: 'Space', vk: 32, text: ' ' },
};

// Runs in the page before its own scripts, in every new document.
const HOOK = `(() => {
  const drm = window.__drm = { requested: [], created: [] };
  // Both are missing on a page that is not a secure context, so the hook then has nothing to record.
  if (!Navigator.prototype.requestMediaKeySystemAccess || typeof MediaKeySystemAccess === 'undefined') return;
  const request = Navigator.prototype.requestMediaKeySystemAccess;
  Navigator.prototype.requestMediaKeySystemAccess = function (keySystem, configs) {
    const robustness = [];
    for (const c of configs || [])
      for (const kind of ['videoCapabilities', 'audioCapabilities'])
        for (const cap of c[kind] || []) robustness.push(kind[0] + ':' + (cap.robustness || ''));
    const entry = { keySystem, robustness, result: 'pending', at: Date.now() };
    drm.requested.push(entry);
    const promise = request.apply(this, arguments);
    promise.then(() => { entry.result = 'granted'; }, e => { entry.result = 'refused: ' + (e && e.name); });
    return promise;
  };
  const create = MediaKeySystemAccess.prototype.createMediaKeys;
  MediaKeySystemAccess.prototype.createMediaKeys = function () {
    drm.created.push({ keySystem: this.keySystem, at: Date.now() });
    return create.apply(this, arguments);
  };
})();`;

const VIDEO = `JSON.stringify({
  url: location.href,
  __drm: window.__drm || null,
  videos: [...document.querySelectorAll('video')].map(v => {
    const q = v.getVideoPlaybackQuality ? v.getVideoPlaybackQuality() : {};
    return {
      videoWidth: v.videoWidth,
      videoHeight: v.videoHeight,
      paused: v.paused,
      currentTime: v.currentTime,
      totalVideoFrames: q.totalVideoFrames,
      droppedVideoFrames: q.droppedVideoFrames,
      corruptedVideoFrames: q.corruptedVideoFrames,
    };
  }),
}, null, 2)`;

const FOCUS = `JSON.stringify((() => {
  let e = document.activeElement;
  while (e && e.shadowRoot && e.shadowRoot.activeElement) e = e.shadowRoot.activeElement;
  if (!e) return null;
  return {
    tag: e.tagName,
    text: (e.innerText || e.textContent || '').trim().replace(/\\s+/g, ' ').slice(0, 120),
    ariaLabel: e.getAttribute('aria-label'),
  };
})())`;

function usage(message) {
  if (message) console.error(message);
  console.error('usage: node measure-app.cjs <port> hook | video | focus | key <name>');
  console.error('keys: ' + Object.keys(KEYS).join(' '));
  process.exit(2);
}

function fail(message) {
  console.error(message);
  process.exit(1);
}

// One DevTools connection to the page: send(method, params) resolves with the result, rejects on a protocol error.
function connect(url) {
  return new Promise((resolve, reject) => {
    const ws = new WebSocket(url);
    const pending = new Map();
    let next = 1;
    ws.addEventListener('message', event => {
      const msg = JSON.parse(event.data);
      const call = pending.get(msg.id);
      if (!call) return;
      pending.delete(msg.id);
      if (msg.error) call.reject(new Error(msg.error.message));
      else call.resolve(msg.result);
    });
    ws.addEventListener('close', () => {
      for (const call of pending.values()) call.reject(new Error('the connection closed'));
      pending.clear();
    });
    ws.addEventListener('error', () => reject(new Error('could not open ' + url)));
    ws.addEventListener('open', () => resolve({
      ws,
      send(method, params = {}) {
        return new Promise((ok, no) => {
          const id = next++;
          const timer = setTimeout(() => { pending.delete(id); no(new Error(method + ' got no answer in 10 s')); }, 10000);
          pending.set(id, { resolve: r => { clearTimeout(timer); ok(r); }, reject: e => { clearTimeout(timer); no(e); } });
          ws.send(JSON.stringify({ id, method, params }));
        });
      },
    }));
  });
}

async function evaluate(client, expression) {
  const r = await client.send('Runtime.evaluate', { expression, returnByValue: true, awaitPromise: true });
  if (r.exceptionDetails) throw new Error('the page threw: ' + (r.exceptionDetails.exception?.description || r.exceptionDetails.text));
  return r.result.value;
}

async function pressKey(client, name) {
  const k = KEYS[name];
  const base = { key: k.key, code: k.code, windowsVirtualKeyCode: k.vk, nativeVirtualKeyCode: k.vk };
  await client.send('Input.dispatchKeyEvent', { type: 'keyDown', ...base, ...(k.text ? { text: k.text } : {}) });
  await client.send('Input.dispatchKeyEvent', { type: 'keyUp', ...base });
}

async function main() {
  const [portArg, command, keyName] = process.argv.slice(2);
  const port = Number(portArg);
  if (!Number.isInteger(port) || port < 1 || port > 65535) usage('port must be a number: ' + portArg);
  if (!['hook', 'video', 'focus', 'key'].includes(command)) usage(command ? 'unknown command: ' + command : 'no command');
  if (command === 'key' && !KEYS[keyName]) usage(keyName ? 'unknown key: ' + keyName : 'no key name');

  let targets;
  try {
    const response = await fetch(`http://127.0.0.1:${port}/json`);
    targets = await response.json();
  } catch (e) {
    fail(`nothing answers on port ${port} (is the launcher running with --dev, and is this the app's port?): ${e.message}`);
  }
  const page = targets.find(t => t.type === 'page' && t.webSocketDebuggerUrl);
  if (!page) fail(`port ${port} lists no page target: ${targets.map(t => t.type).join(', ') || 'no targets'}`);

  const client = await connect(page.webSocketDebuggerUrl);
  try {
    if (command === 'hook') {
      await client.send('Page.enable');
      const { identifier } = await client.send('Page.addScriptToEvaluateOnNewDocument', { source: HOOK });
      await client.send('Page.reload');
      console.log(`armed (script ${identifier}) on ${page.url}, page reloaded; stays connected, Ctrl+C to stop`);
      await new Promise(resolve => client.ws.addEventListener('close', resolve));
      console.log('the page connection closed');
    } else if (command === 'video') {
      console.log(await evaluate(client, VIDEO));
    } else if (command === 'focus') {
      console.log(await evaluate(client, FOCUS));
    } else {
      await pressKey(client, keyName);
      console.log('pressed ' + keyName);
    }
  } catch (e) {
    fail(e.message);
  }
  if (command !== 'hook') client.ws.close();
}

main().catch(e => fail(e.message));
