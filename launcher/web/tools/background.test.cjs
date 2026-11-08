const test = require('node:test');
const assert = require('node:assert/strict');
const vm = require('node:vm');
const fs = require('node:fs');
const path = require('node:path');
const { loadPage } = require('./fake-page.cjs');

const URLS = ['https://photos.test/a.jpg', 'https://photos.test/b.jpg', 'https://photos.test/c.jpg'];
const STATE = { phase: 'day', cloud: 0.2, kind: 'clear' };
const css = u => 'url("' + u + '")';

// Runs js/background.js alone, with fake timers, images, layers and a scrim.
function setup(config, realLandscape) {
  const images = [], intervals = [], landscapeCalls = [];
  let now = 0, nextId = 1;
  const scrimClasses = new Set();
  const scrim = { classList: { add: c => scrimClasses.add(c), remove: c => scrimClasses.delete(c), contains: c => scrimClasses.has(c) } };
  const landscapeLayers = [{ style: { opacity: '1', backgroundImage: 'url("css/landscapes/clear.png")', filter: 'none' } },
    { style: { opacity: '0' } }];
  const layers = [{ style: { opacity: '0' } }, { style: { opacity: '0' } }];   // the photo layers
  const element = { dataset: {}, querySelectorAll: sel => (sel === '.photo-layer' ? layers : sel === '.landscape-layer' ? landscapeLayers : []) };
  const window = { TV_HOME_CONFIG: config || { backgroundRotateSeconds: 60 } };
  if (!realLandscape) window.TVLandscape = { render: (el, state) => landscapeCalls.push({ el, state }) };
  const context = vm.createContext({
    window,
    document: { getElementById: id => (id === 'scrim' ? scrim : null) },
    Image: function () { images.push(this); },
    setInterval: (fn, ms) => { const id = nextId++; intervals.push({ id, fn, ms, due: now + ms }); return id; },
    clearInterval: id => { const i = intervals.findIndex(t => t.id === id); if (i >= 0) intervals.splice(i, 1); }
  });
  if (realLandscape) vm.runInContext(fs.readFileSync(path.join(__dirname, '../js/landscape.js'), 'utf8'), context, { filename: 'js/landscape.js' });
  vm.runInContext(fs.readFileSync(path.join(__dirname, '../js/background.js'), 'utf8'), context, { filename: 'js/background.js' });
  function advance(seconds) {
    const end = now + seconds * 1000;
    for (;;) {
      const due = intervals.filter(t => t.due <= end).sort((a, b) => a.due - b.due)[0];
      if (!due) break;
      now = due.due;
      due.due += due.ms;
      due.fn();
    }
    now = end;
  }
  const load = i => images[i].onload();
  return { bg: window.TVBackground, images, layers, landscapeLayers, element, scrim, landscapeCalls, intervals, advance, load, window };
}

const shown = layers => layers.filter(l => l.style.opacity === '1').map(l => l.style.backgroundImage);

test('rotates through photos in order and wraps', () => {
  const s = setup();
  s.bg.setPhotos(URLS);
  s.bg.update(s.element, STATE);
  assert.equal(s.images[0].src, URLS[0]);
  assert.equal(s.bg.next(), URLS[1]);
  assert.equal(s.bg.next(), URLS[2]);
  assert.equal(s.bg.next(), URLS[0]);
});

test('rotates every backgroundRotateSeconds', () => {
  const s = setup({ backgroundRotateSeconds: 60 });
  s.bg.setPhotos(URLS);
  s.bg.update(s.element, STATE);
  s.load(0);
  assert.equal(s.images.length, 1);
  s.advance(59);
  assert.equal(s.images.length, 1, 'nothing before 60 s');
  s.advance(1);
  assert.equal(s.images.length, 2, 'one change at 60 s');
  assert.equal(s.images[1].src, URLS[1]);
  s.advance(60);
  assert.equal(s.images.length, 3, 'one more at 120 s');
  assert.equal(s.images[2].src, URLS[2]);
});

test('rotation interval falls back to 60 s and follows the config', () => {
  const none = setup({});
  none.bg.setPhotos(URLS);
  assert.equal(none.intervals[0].ms, 60000);
  const fast = setup({ backgroundRotateSeconds: 5 });
  fast.bg.setPhotos(URLS);
  assert.equal(fast.intervals[0].ms, 5000);
});

test('a new photo list replaces the old timer instead of stacking another', () => {
  const s = setup();
  s.bg.setPhotos(URLS);
  s.bg.setPhotos(URLS.slice(0, 2));
  assert.equal(s.intervals.length, 1);
  s.bg.setPhotos([]);
  assert.equal(s.intervals.length, 0);
});

test('update_withNoPhotos_usesWeatherLandscape', () => {
  const s = setup();
  s.bg.update(s.element, STATE);
  assert.equal(s.landscapeCalls.length, 1);
  assert.equal(s.landscapeCalls[0].el, s.element);
  assert.deepEqual(s.landscapeCalls[0].state, STATE);
  assert.equal(s.images.length, 0);
  assert.ok(!s.scrim.classList.contains('photo'));
});

test('update with photos still renders the landscape underneath, so the weather state stays current', () => {
  const s = setup();
  s.bg.setPhotos(URLS);
  s.bg.update(s.element, STATE);
  s.load(0);
  const rain = { phase: 'night', cloud: 0.9, kind: 'rain' };
  s.bg.update(s.element, rain);
  assert.equal(s.landscapeCalls.length, 2);
  assert.deepEqual(s.landscapeCalls[1].state, rain);
});

test('photos sent after the first update appear on the photo layers and leave the landscape layers alone', () => {
  const s = setup();
  s.bg.update(s.element, STATE);
  s.bg.setPhotos(URLS);
  s.load(0);
  assert.deepEqual(shown(s.layers), [css(URLS[0])]);
  assert.equal(s.landscapeLayers[0].style.opacity, '1');
  assert.equal(s.landscapeLayers[0].style.backgroundImage, 'url("css/landscapes/clear.png")');
  assert.equal(s.landscapeLayers[1].style.opacity, '0');
});

test('each rotation crossfades onto the other layer', () => {
  const s = setup();
  s.bg.setPhotos(URLS);
  s.bg.update(s.element, STATE);
  s.load(0);
  assert.deepEqual(shown(s.layers), [css(URLS[0])]);
  s.bg.next();
  s.load(1);
  assert.deepEqual(shown(s.layers), [css(URLS[1])]);
  assert.equal(s.layers.filter(l => l.style.opacity === '0').length, 1);
});

test('a photo that fails to load is skipped, not shown blank', () => {
  const s = setup();
  s.bg.setPhotos(URLS);
  s.bg.update(s.element, STATE);
  s.images[0].onerror();
  assert.equal(s.images.length, 2, 'moves on to the next photo');
  assert.equal(s.images[1].src, URLS[1]);
  s.load(1);
  assert.deepEqual(shown(s.layers), [css(URLS[1])]);
  // The skipped photo stays skipped on later rotations.
  s.bg.next();
  assert.equal(s.images[2].src, URLS[2]);
  s.bg.next();
  assert.equal(s.images[3].src, URLS[1]);
});

test('when every photo fails the weather landscape comes back', () => {
  const s = setup();
  s.bg.update(s.element, STATE);
  s.bg.setPhotos(URLS);
  s.images[0].onerror();
  s.images[1].onerror();
  s.images[2].onerror();
  assert.equal(s.images.length, 3, 'no endless retry loop');
  assert.ok(!s.scrim.classList.contains('photo'));
  assert.deepEqual(shown(s.layers), []);
  const rain = { phase: 'day', cloud: 0.9, kind: 'rain' };
  s.bg.update(s.element, rain);
  assert.deepEqual(s.landscapeCalls[s.landscapeCalls.length - 1].state, rain, 'weather changes still reach the landscape');
  s.advance(60);
  assert.equal(s.images.length, 4, 'the next rotation tries the photos again');
  s.load(3);
  assert.ok(s.scrim.classList.contains('photo'));
});

test('a late load of an older photo cannot replace a newer one', () => {
  const s = setup();
  s.bg.setPhotos(URLS);
  s.bg.update(s.element, STATE);
  s.bg.next();
  s.load(1);
  s.load(0);
  assert.deepEqual(shown(s.layers), [css(URLS[1])]);
});

test('adds a darkening scrim class so text stays readable', () => {
  const s = setup();
  s.bg.setPhotos(URLS);
  s.bg.update(s.element, STATE);
  assert.ok(!s.scrim.classList.contains('photo'), 'not before a photo shows');
  s.load(0);
  assert.ok(s.scrim.classList.contains('photo'));
  s.bg.setPhotos([]);
  assert.ok(!s.scrim.classList.contains('photo'), 'removed when the photos go');
  assert.deepEqual(shown(s.layers), [], 'photo layers fade out, the landscape underneath shows');
  assert.equal(s.landscapeLayers[0].style.opacity, '1');
});

test('a landscape load finishing after photos arrive does not cover the photo', () => {
  const s = setup(undefined, true);   // the real landscape.js
  s.bg.update(s.element, STATE);      // starts the landscape image load: images[0]
  assert.match(s.images[0].src, /landscapes\/clear\.png$/);
  s.bg.setPhotos(URLS);               // photo load: images[1]
  s.load(1);
  assert.deepEqual(shown(s.layers), [css(URLS[0])]);
  s.images[0].onload();               // the landscape finishes late
  assert.deepEqual(shown(s.layers), [css(URLS[0])], 'photo still visible');
  assert.equal(s.landscapeLayers.filter(l => l.style.opacity === '1').length, 1, 'landscape appears underneath');
  assert.ok(s.scrim.classList.contains('photo'));
});

test('a landscape load finishing between photo rotations does not change the photo layers', () => {
  const s = setup(undefined, true);
  s.bg.setPhotos(URLS);
  s.bg.update(s.element, STATE);      // photo images[0], landscape images[1]
  s.load(0);
  s.bg.update(s.element, { phase: 'night', cloud: 0.9, kind: 'rain' });   // landscape images[2]
  s.images[1].onload();
  s.images[2].onload();
  assert.deepEqual(shown(s.layers), [css(URLS[0])]);
});

test('index.html stacks the two photo layers above the landscape layers and the haze', () => {
  const page = loadPage();
  const kids = page.$('landscape').children.map(c => c.className);
  assert.deepEqual(kids, ['landscape-layer', 'landscape-layer', 'landscape-haze', 'photo-layer', 'photo-layer']);
});

test('the stylesheet gives the photo scrim a stronger gradient', () => {
  const sheet = fs.readFileSync(path.join(__dirname, '../css/style.css'), 'utf8');
  assert.match(sheet, /\.scrim\.photo\s*\{/);
  assert.match(sheet, /body\[data-phase='day'\] \.scrim\.photo\s*\{/);
});

test('setPhotos ignores the same list again, and anything that is not a list', () => {
  const s = setup();
  s.bg.setPhotos(URLS);
  s.bg.update(s.element, STATE);
  s.load(0);
  s.bg.setPhotos(URLS.slice());
  s.bg.setPhotos(undefined);
  s.bg.setPhotos('x');
  assert.equal(s.images.length, 1);
  assert.ok(s.scrim.classList.contains('photo'));
});

test('non-string and empty entries are dropped from a photo list', () => {
  const s = setup();
  s.bg.update(s.element, STATE);
  s.bg.setPhotos([42, '', URLS[1]]);
  assert.equal(s.images[0].src, URLS[1]);
});

test('the page hands host backgrounds messages to TVBackground.setPhotos', async () => {
  const page = loadPage();
  await page.settle();
  assert.ok(page.scripts.indexOf('js/landscape.js') < page.scripts.indexOf('js/background.js'));
  assert.ok(page.scripts.indexOf('js/background.js') < page.scripts.indexOf('js/main.js'));
  const got = [];
  page.window.TVBackground.setPhotos = urls => got.push(page.plain(urls));
  page.deliver({ type: 'backgrounds', urls: URLS });
  page.deliver({ type: 'backgrounds', urls: 'nope' });
  page.deliver({ type: 'backgrounds' });
  assert.deepEqual(got, [URLS]);
});

test('main.js renders the sky through TVBackground, not TVLandscape directly', () => {
  const main = fs.readFileSync(path.join(__dirname, '../js/main.js'), 'utf8');
  assert.match(main, /TVBackground\.update\(\$\('landscape'\)/);
  assert.ok(!/TVLandscape\.render/.test(main));
});
