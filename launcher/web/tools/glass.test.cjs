// Tests for measuring the photo behind a region and working out the glass and shading values.
'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const { root } = require('./fake-page.cjs');

function loadGlass() {
  const window = {};
  const ctx = vm.createContext({ window, Math, Number, Array, Object });
  vm.runInContext(fs.readFileSync(path.join(root, 'js', 'glass.js'), 'utf8'), ctx, { filename: 'glass.js' });
  return window.TVGlass;
}

// A w x h picture where fill(x, y) gives [r, g, b].
function picture(w, h, fill) {
  const data = new Uint8ClampedArray(w * h * 4);
  for (let y = 0; y < h; y++) {
    for (let x = 0; x < w; x++) {
      const c = fill(x, y), i = (y * w + x) * 4;
      data[i] = c[0]; data[i + 1] = c[1]; data[i + 2] = c[2]; data[i + 3] = 255;
    }
  }
  return { data, width: w };
}
const FULL = { l: 0, t: 0, r: 100, b: 100 };
const near = (a, b, tol) => assert.ok(Math.abs(a - b) <= tol, a + ' is not within ' + tol + ' of ' + b);

test('levelOf: an all-white region is 1 and an all-black region is 0', () => {
  const G = loadGlass();
  near(G.levelOf(picture(100, 100, () => [255, 255, 255]), FULL), 1, 0.001);
  near(G.levelOf(picture(100, 100, () => [0, 0, 0]), FULL), 0, 0.001);
});

test('levelOf: weights the mean 0.6 and the 90th percentile 0.4', () => {
  const G = loadGlass();
  const p = picture(100, 100, (x, y) => (y >= 90 ? [255, 255, 255] : [0, 0, 0]));
  near(G.levelOf(p, FULL), 0.6 * 0.1 + 0.4 * 1, 0.02);
});

test('levelOf: luma is 0.2126 R + 0.7152 G + 0.0722 B', () => {
  const G = loadGlass();
  near(G.levelOf(picture(100, 100, () => [0, 255, 0]), FULL), 0.7152, 0.01);
});

test('levelOf: only the rect is measured', () => {
  const G = loadGlass();
  const p = picture(100, 100, (x) => (x < 50 ? [255, 255, 255] : [0, 0, 0]));
  near(G.levelOf(p, { l: 0, t: 0, r: 50, b: 100 }), 1, 0.001);
  near(G.levelOf(p, { l: 50, t: 0, r: 100, b: 100 }), 0, 0.001);
});

const NAMES = ['--s-greet', '--s-wx', '--s-top', '--s-bottom', '--g-wx', '--g-tile', '--b-bright'];
function all(L) { return { greet: L, wx: L, sub: L, tiles: L, top: L }; }
function check(v, want) {
  for (const k of NAMES) near(v[k], want[k], 0.001);
}

test("valuesFor: at REF every property is today's value", () => {
  const G = loadGlass();
  assert.equal(G.REF, 0.35);
  check(G.valuesFor(all(G.REF)), { '--s-greet': 0.55, '--s-wx': 0.42, '--s-top': 0.5, '--s-bottom': 0.6, '--g-wx': 0.72, '--g-tile': 0.72, '--b-bright': 1.05 });
});

test('valuesFor: clamps at a white photo', () => {
  const G = loadGlass();
  check(G.valuesFor(all(1)), { '--s-greet': 0.80, '--s-wx': 0.75, '--s-top': 0.75, '--s-bottom': 0.80, '--g-wx': 0.45, '--g-tile': 0.45, '--b-bright': 0.621 });
});

test('valuesFor: clamps at a black photo', () => {
  const G = loadGlass();
  check(G.valuesFor(all(0.02)), { '--s-greet': 0.30, '--s-wx': 0.20, '--s-top': 0.30, '--s-bottom': 0.45, '--g-wx': 1.0, '--g-tile': 1.0, '--b-bright': 1.15 });
});

test('valuesFor: the sentence sets --s-wx and the card sets --g-wx', () => {
  const G = loadGlass();
  const base = G.valuesFor(all(0.35));
  const a = G.valuesFor({ greet: 0.35, wx: 0.35, sub: 0.5, tiles: 0.35, top: 0.35 });
  const b = G.valuesFor({ greet: 0.35, wx: 0.6, sub: 0.35, tiles: 0.35, top: 0.35 });
  for (const k of NAMES) {
    assert.equal(a[k] !== base[k], k === '--s-wx', 'sub changes ' + k);
    assert.equal(b[k] !== base[k], k === '--g-wx', 'wx changes ' + k);
  }
});
