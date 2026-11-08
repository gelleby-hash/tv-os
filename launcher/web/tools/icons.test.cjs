// Tests for the moon's phase and the glass weather icons. Loads moon.js and icons.js into a bare vm context.
'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const root = path.join(__dirname, '..');

const SYNODIC = 29.530588853;
const REFERENCE = Date.UTC(2000, 0, 6, 18, 14);
const KINDS = ['clear', 'partly', 'cloudy', 'fog', 'drizzle', 'rain', 'snow', 'storm'];

function load() {
  const window = {};
  const ctx = vm.createContext({ window, Math, Date, Number, String, Array });
  for (const file of ['moon.js', 'icons.js']) {
    vm.runInContext(fs.readFileSync(path.join(root, 'js', file), 'utf8'), ctx, { filename: file });
  }
  return window;
}
// The date on which the moon has the given phase.
const dateAt = phase => new Date(REFERENCE + phase * SYNODIC * 86400000);

test('moon: full on the evening of 2026-10-25', () => {
  const w = load();
  const p = w.TVMoon.phase(new Date(2026, 9, 25, 20));
  assert.ok(Math.abs(p - 0.5) < 0.03, 'phase ' + p);
});

test('moon: new at the reference', () => {
  const w = load();
  const p = w.TVMoon.phase(new Date(Date.UTC(2000, 0, 6, 18, 14)));
  assert.ok(p < 0.001 || p > 0.999, 'phase ' + p);
});

test('moon: new, full and wrap', () => {
  const w = load();
  assert.equal(w.TVMoon.lit(0), 0);
  assert.equal(w.TVMoon.lit(0.5), 1);
  const p = w.TVMoon.phase(new Date(REFERENCE + SYNODIC * 86400000));
  assert.ok(p < 0.001 || p > 0.999, 'phase ' + p);
  for (const phase of [0, 0.5, 0.999]) {
    const svg = w.TVIcons.weather('clear', false, dateAt(phase));
    assert.ok(!/NaN/.test(svg), 'phase ' + phase);
  }
});

test('icons: every kind draws by day and night', () => {
  const w = load();
  let n = 0;
  for (const kind of KINDS) for (const day of [true, false]) {
    const svg = w.TVIcons.weather(kind, day);
    n++;
    assert.ok(svg.startsWith('<svg viewBox="0 0 64 64"'), kind);
    assert.ok(svg.endsWith('</svg>'), kind);
    assert.ok(!/undefined|NaN/.test(svg), kind + ' ' + day);
  }
  assert.equal(n, 16);
});

test('icons: ids are unique across a page', () => {
  const w = load();
  let all = '';
  for (const kind of KINDS) for (const day of [true, false]) all += w.TVIcons.weather(kind, day);
  for (const kind of KINDS.slice(0, 6)) { all += w.TVIcons.weather(kind, true); all += w.TVIcons.weather(kind, false); }
  all += w.TVIcons.weather('storm', false) + w.TVIcons.weather('snow', true);
  const ids = all.match(/id="[^"]+"/g) || [];
  assert.ok(ids.length > 100, 'ids ' + ids.length);
  assert.equal(new Set(ids).size, ids.length);
});

test('icons: night clouds are the darker grey', () => {
  const w = load();
  const svg = w.TVIcons.weather('partly', false);
  assert.ok(svg.includes('#8E96A4') || svg.includes('#8e96a4'), 'light end');
  assert.ok(svg.includes('#545C6B') || svg.includes('#545c6b'), 'dark end');
});

test('icons: the rim light follows the lit side', () => {
  const w = load();
  const x1 = svg => Number(/<linearGradient id="mr\d+" x1="([-\d.]+)"/.exec(svg)[1]);
  assert.ok(x1(w.TVIcons.weather('clear', false, dateAt(0.2))) > 32, 'waxing');
  assert.ok(x1(w.TVIcons.weather('clear', false, dateAt(0.8))) < 32, 'waning');
});

test('icons: full moon has an even rim, new moon has no lit part and no glow', () => {
  const w = load();
  const full = w.TVIcons.weather('clear', false, dateAt(0.5));
  assert.ok(!/id="mr\d+"/.test(full), 'no one-sided rim gradient');
  assert.ok(/stroke="#FFFFFF"/.test(full), 'plain white rim');
  const fresh = w.TVIcons.weather('clear', false, dateAt(0.01));
  assert.ok(!/id="(ml|mh)\d+"/.test(fresh), 'no lit part, no glow');
  assert.ok(/stroke="#FFFFFF"/.test(fresh), 'faint even rim');
});

test('icons: no sun at night', () => {
  const w = load();
  for (const kind of KINDS) assert.ok(!w.TVIcons.weather(kind, false).includes('#FFC93F'), kind);
  assert.ok(w.TVIcons.weather('clear', true).includes('#FFC93F'));
});

test('icons: the UI icons are unchanged and the hourly date is accepted', () => {
  const w = load();
  assert.ok(String(w.TVIcons.ui.image).startsWith('<svg viewBox="0 0 24 24"'));
  assert.ok(w.TVIcons.weather('clear', false, new Date(2026, 9, 25, 21)).includes('<svg'));
});

test('icons: the Apps button has its icon, a 2x2 grid of rounded squares', () => {
  const w = load();
  const svg = String(w.TVIcons.ui.apps);
  assert.ok(svg.startsWith('<svg viewBox="0 0 24 24"'));
  assert.equal((svg.match(/<rect /g) || []).length, 4);
  assert.ok(!/undefined|NaN/.test(svg));
});
