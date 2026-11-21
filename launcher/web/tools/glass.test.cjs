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

// ---------- on the home page ----------
const { loadPage } = require('./fake-page.cjs');

const SEVEN = ['--s-greet', '--s-wx', '--s-top', '--s-bottom', '--g-wx', '--g-tile', '--b-bright'];
const glassProps = page => SEVEN.filter(n => n in page.document.documentElement.style);
const glassLogs = page => page.posted.filter(m => m.type === 'log');

// Wraps the page's createElement so a test sees the images and canvases the glass makes.
// pixels() gives what getImageData returns; set it to a function that throws to fake a tainted canvas.
function spy(page, pixels) {
  const made = { img: [], canvas: [] };
  const real = page.document.createElement;
  page.document.createElement = tag => {
    const e = real(tag);
    if (tag === 'img') made.img.push(e);
    if (tag === 'canvas') {
      made.canvas.push(e);
      e.getContext = () => ({ drawImage() {}, getImageData: (x, y, w, h) => pixels.fn(x, y, w, h) });
    }
    return e;
  };
  return made;
}
const whitePixels = () => ({ data: new Uint8ClampedArray(1920 * 1080 * 4).fill(255), width: 1920, height: 1080 });

test('the page loads with no canvas and sets no glass property', () => {
  const page = loadPage();
  assert.deepEqual(glassProps(page), []);
  assert.deepEqual(glassLogs(page), []);
  assert.deepEqual(page.logs, []);
});

test('a photo that cannot be measured logs once and leaves today\'s look', () => {
  const page = loadPage();
  const pixels = { fn: () => { const e = new Error('tainted'); e.name = 'SecurityError'; throw e; } };
  const made = spy(page, pixels);
  page.window.TVGlass.show('https://photos.test/a.jpg');
  made.img[0].onload();
  page.window.TVGlass.show('https://photos.test/a.jpg');
  made.img[1].onload();
  assert.deepEqual(glassProps(page), []);
  assert.deepEqual(glassLogs(page), [{ type: 'log', text: 'glass: could not measure a.jpg: SecurityError' }]);
});

test('the image is loaded with crossOrigin anonymous', () => {
  const page = loadPage();
  const made = spy(page, { fn: whitePixels });
  page.window.TVGlass.show('https://photos.test/a.jpg');
  assert.equal(made.img[0].crossOrigin, 'anonymous');
  assert.equal(made.img[0].src, 'https://photos.test/a.jpg');
});

test('the first good measurement is logged once', () => {
  const page = loadPage();
  const made = spy(page, { fn: whitePixels });
  page.window.TVGlass.show('https://photos.test/a.jpg');
  made.img[0].onload();
  page.window.TVGlass.show('https://photos.test/b.jpg');
  made.img[1].onload();
  const logs = glassLogs(page);
  assert.equal(logs.length, 1);
  assert.ok(logs[0].text.startsWith('glass: measuring works ('), logs[0].text);
});

test('a measured photo sets all seven properties, clear removes them', () => {
  const page = loadPage();
  const made = spy(page, { fn: whitePixels });
  page.window.TVGlass.show('https://photos.test/a.jpg');
  made.img[0].onload();
  assert.deepEqual(glassProps(page), SEVEN);
  const style = page.document.documentElement.style;
  assert.equal(style['--g-wx'], '0.45');   // white photo: the darkest glass limit
  assert.ok(Number.isFinite(Number(style['--s-greet'])));
  page.window.TVGlass.clear();
  assert.deepEqual(glassProps(page), []);
});

test('a load that is no longer the newest is ignored', () => {
  const page = loadPage();
  const made = spy(page, { fn: whitePixels });
  page.window.TVGlass.show('https://photos.test/a.jpg');
  page.window.TVGlass.show('https://photos.test/b.jpg');
  made.img[0].onload();
  assert.deepEqual(glassProps(page), []);
  made.img[1].onload();
  assert.deepEqual(glassProps(page), SEVEN);
});

test('remeasure measures the kept picture again and does nothing without one', () => {
  const page = loadPage();
  const made = spy(page, { fn: whitePixels });
  page.window.TVGlass.remeasure();
  assert.deepEqual(glassProps(page), []);
  page.window.TVGlass.show('https://photos.test/a.jpg');
  made.img[0].onload();
  const style = page.document.documentElement.style;
  SEVEN.forEach(n => style.removeProperty(n));
  page.window.TVGlass.remeasure();
  assert.deepEqual(glassProps(page), SEVEN);
  assert.equal(glassLogs(page).length, 1);   // only the first good measurement is logged
});

test('every region rect is clamped to the picture, so no value is NaN', () => {
  const page = loadPage();
  const seen = [];
  const made = spy(page, { fn: (x, y, w, h) => { seen.push([x, y, w, h]); return whitePixels(); } });
  page.window.TVGlass.show('https://photos.test/a.jpg');
  made.img[0].onload();
  SEVEN.forEach(n => assert.ok(Number.isFinite(Number(page.document.documentElement.style[n])), n));
  assert.deepEqual(seen, [[0, 0, 1920, 1080]]);   // the whole picture is read once
});
