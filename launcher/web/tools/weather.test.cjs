// Tests for the weather look (the tint of the card and the detail panel) and the stars on clear nights.
'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const { loadPage, fakeConfig, placeMessage, root } = require('./fake-page.cjs');

function loadWeather() {
  const window = {};
  const ctx = vm.createContext({ window, Math, Date, Number, String, Array, Object, JSON });
  vm.runInContext(fs.readFileSync(path.join(root, 'js', 'weather.js'), 'utf8'), ctx, { filename: 'weather.js' });
  return window.TVWeather;
}

const KINDS = ['clear', 'partly', 'cloudy', 'fog', 'drizzle', 'rain', 'snow', 'storm'];
const DAY = { clear: 'day-clear', partly: 'day-partly', cloudy: 'cloudy', fog: 'fog', drizzle: 'rain', rain: 'rain', snow: 'snow', storm: 'storm' };

test('look: each kind by day and night', () => {
  const W = loadWeather();
  for (const kind of KINDS) {
    assert.equal(W.look(kind, true, 0), DAY[kind], 'day ' + kind);
    const night = kind === 'clear' || kind === 'partly' ? 'night-clear' : 'night-cloudy';
    assert.equal(W.look(kind, false, 0), night, 'night ' + kind);
  }
});

test('look: windy boundary and priority', () => {
  const W = loadWeather();
  assert.equal(W.look('clear', true, 39.9), 'day-clear');
  assert.equal(W.look('clear', true, 40), 'windy');
  assert.equal(W.look('partly', true, 55), 'windy');
  assert.equal(W.look('cloudy', true, 55), 'windy');
  assert.equal(W.look('storm', true, 60), 'storm');
  assert.equal(W.look('rain', true, 60), 'rain');
  assert.equal(W.look('drizzle', true, 60), 'rain');
  assert.equal(W.look('snow', true, 60), 'snow');
  assert.equal(W.look('fog', true, 60), 'fog');
  assert.equal(W.look('clear', false, 60), 'night-clear');
  assert.equal(W.look('cloudy', false, 60), 'night-cloudy');
});

test('stars: fine stars of varied size, brightness and tint, thinning towards the bottom (owner, option B)', () => {
  const W = loadWeather();
  const svg = W.stars(500, 1520, 900, 6);
  assert.equal(svg, W.stars(500, 1520, 900, 6), 'same every time');
  const fine = Array.from(svg.matchAll(/<circle class="star" cx="([\d.]+)" cy="([\d.]+)" r="([\d.]+)" fill="(#[0-9A-F]{6})" opacity="([\d.]+)"\/>/g));
  assert.equal(fine.length, 500);
  let top = 0;
  const sizes = new Set();
  for (const [, cx, cy, r, fill, o] of fine) {
    assert.ok(+cx >= 0 && +cx <= 1520 && +cy >= 0 && +cy <= 900, 'inside the layer');
    assert.ok(+r >= 0.35 && +r <= 1.25, 'a point, not a dot: radius ' + r);
    assert.ok(+o >= 0.3 && +o <= 0.95, 'opacity ' + o);
    assert.ok(['#FFFFFF', '#DCE6FF', '#FFF1DC'].includes(fill), 'white, cool or warm: ' + fill);
    if (+cy < 450) top++;
    sizes.add(+r >= 0.9 ? 'big' : 'small');
  }
  assert.ok(top > 1.5 * (500 - top), 'more in the top half than the bottom: ' + top);
  assert.deepEqual([...sizes].sort(), ['big', 'small']);
});

test('stars: a few bright stars with a soft glow and fine glints, each twinkling out of step', () => {
  const W = loadWeather();
  const svg = W.stars(90, 600, 260, 3);
  const glints = Array.from(svg.matchAll(/<g class="star-glint" style="animation-delay: ([\d.]+)s">([\s\S]*?)<\/g>/g));
  assert.equal(glints.length, 3);
  assert.equal(new Set(glints.map(g => g[1])).size, 3, 'each starts at its own moment');
  for (const [, , inner] of glints) {
    assert.match(inner, /<path d="M[\d. ]+H[\d.]+M[\d. ]+V[\d.]+"/, 'a fine cross: the glint');
    assert.ok((inner.match(/<circle/g) || []).length >= 3, 'a glow around a bright core');
  }
  assert.equal(W.stars(90, 600, 260, 0).includes('star-glint'), false, 'none asked for, none drawn');
});

test('stars: keep their shape on any card size, and twinkle only where motion is welcome', () => {
  const html = fs.readFileSync(path.join(__dirname, '..', 'index.html'), 'utf8');
  const layers = html.match(/<svg class="stars"[^>]*>/g);
  assert.equal(layers.length, 2);
  for (const l of layers) assert.match(l, /preserveAspectRatio="xMidYMin slice"/, 'not stretched: ' + l);
  const css = fs.readFileSync(path.join(__dirname, '..', 'css', 'style.css'), 'utf8');
  assert.match(css, /\.star-glint \{[^}]*animation: twinkle/);
  assert.match(css, /@media \(prefers-reduced-motion: reduce\) \{[^}]*\.star-glint \{ animation: none; \}/);
});

// A raw Open-Meteo answer for the page: the current weather as given, then two days of hours and seven days.
function rawWeather(code, isDay, wind) {
  const pad = n => (n < 10 ? '0' : '') + n;
  const stamp = d => d.getFullYear() + '-' + pad(d.getMonth() + 1) + '-' + pad(d.getDate()) + 'T' + pad(d.getHours()) + ':' + pad(d.getMinutes());
  const start = new Date(); start.setMinutes(0, 0, 0);
  const hours = Array.from({ length: 48 }, (_, i) => new Date(start.getTime() + i * 3600e3));
  const days = Array.from({ length: 7 }, (_, i) => new Date(start.getFullYear(), start.getMonth(), start.getDate() + i));
  const at = (d, h) => stamp(new Date(d.getFullYear(), d.getMonth(), d.getDate(), h, 0));
  return {
    current: { temperature_2m: 12, apparent_temperature: 10, relative_humidity_2m: 70, weather_code: code, is_day: isDay ? 1 : 0, wind_speed_10m: wind, cloud_cover: 20 },
    hourly: { time: hours.map(stamp), temperature_2m: hours.map(() => 12), weather_code: hours.map(() => code),
      precipitation_probability: hours.map(() => 10), is_day: hours.map(() => (isDay ? 1 : 0)) },
    daily: { time: days.map(d => stamp(d).slice(0, 10)), weather_code: days.map(() => code), temperature_2m_max: days.map(() => 15),
      temperature_2m_min: days.map(() => 7), sunrise: days.map(d => at(d, 7)), sunset: days.map(d => at(d, 17)),
      precipitation_probability_max: days.map(() => 20) }
  };
}

async function pageWith(code, isDay, wind) {
  const raw = rawWeather(code, isDay, wind);
  const page = loadPage({ fetch: () => Promise.resolve({ ok: true, json: () => Promise.resolve(raw) }) });
  page.deliver(placeMessage('config'));
  await page.settle();
  return page;
}
const looks = el => Array.from(el.classes).filter(c => c.startsWith('look-'));
const panel = page => page.document.querySelector('.forecast-panel');
const starsOf = el => el.querySelector('.stars');
// Whether the stars are hidden: the hidden attribute is what CSS matches, and on an SVG element the hidden property is not the attribute.
const starsHidden = el => starsOf(el).hasAttribute('hidden');

test('card: takes the current look', async () => {
  const night = await pageWith(0, false, 10);
  assert.deepEqual(looks(night.$('weather')), ['look-night-clear']);
  assert.ok(starsOf(night.$('weather')), 'the card has a stars layer');
  assert.equal(starsHidden(night.$('weather')), false, 'shown on a clear night');
  assert.ok(starsOf(night.$('weather')).innerHTML.includes('<circle'), 'with stars in it');

  const day = await pageWith(0, true, 10);
  assert.deepEqual(looks(day.$('weather')), ['look-day-clear']);
  assert.equal(starsHidden(day.$('weather')), true, 'hidden by day');

  const cloudyNight = await pageWith(3, false, 10);
  assert.deepEqual(looks(cloudyNight.$('weather')), ['look-night-cloudy']);
  assert.equal(starsHidden(cloudyNight.$('weather')), true, 'no stars when cloudy');

  const windy = await pageWith(1, true, 45);
  assert.deepEqual(looks(windy.$('weather')), ['look-windy']);
});

test('panel: takes the same look as the card', async () => {
  for (const [code, isDay, wind] of [[0, false, 10], [0, true, 10], [63, true, 10], [95, false, 10], [2, true, 50]]) {
    const page = await pageWith(code, isDay, wind);
    page.$('weather').click();
    assert.equal(page.$('forecast').hidden, false, 'the forecast is open');
    assert.deepEqual(looks(panel(page)), looks(page.$('weather')), 'code ' + code);
    assert.equal(looks(panel(page)).length, 1);
    assert.equal(starsHidden(panel(page)), starsHidden(page.$('weather')), 'stars follow too');
  }
  const night = await pageWith(0, false, 10);
  assert.equal(starsHidden(panel(night)), false);
  assert.ok((starsOf(panel(night)).innerHTML.match(/<circle/g) || []).length >= 100, 'about 140 over the panel');
});

// The card and the panel are neutral glass (owner, 2026-10-31: black and dark grey); a look only switches the stars.
test('style: no look tints the card or the panel any more', () => {
  const css = fs.readFileSync(path.join(root, 'css', 'style.css'), 'utf8');
  assert.equal(/\.(weather|forecast-panel)\.look-[\w-]+\s*\{/.test(css), false);
  assert.match(css, /\.weather, \.tile, \.panel, \.toast \{[^}]*rgba\(10, 10, 12, 0\.60\)/, 'one dark neutral glass for all four');
});

// Chromium paints a backdrop-filter even when its element is visibility: hidden, so a blurred rectangle showed over
// the photo in picture mode (and under an overlay) where the weather card sits. Hiding #home must also drop the blur.
test('style: while #home is hidden (picture mode, overlay) nothing inside it keeps a backdrop-filter', () => {
  const css = fs.readFileSync(path.join(root, 'css', 'style.css'), 'utf8').replace(/\/\*[\s\S]*?\*\//g, '');
  const rules = Array.from(css.matchAll(/([^{}]+)\{([^}]*)\}/g), m => ({
    selectors: m[1].split(',').map(s => s.trim().replace(/\s+/g, ' ')),
    body: m[2]
  }));
  const value = (rule, prop) => {
    const m = rule.body.match(new RegExp('(?:^|[;\\s])' + prop + '\\s*:\\s*([^;]+)'));
    return m ? m[1].trim() : null;
  };

  // One rule covers both modes and every element inside #home, in both spellings of the property.
  for (const mode of ['body.ambient-on', 'body.overlay-on']) {
    const rule = rules.find(r => r.selectors.includes(mode + ' #home *'));
    assert.ok(rule, 'a rule for ' + mode + ' #home *');
    assert.equal(value(rule, 'backdrop-filter'), 'none', mode);
    assert.equal(value(rule, '-webkit-backdrop-filter'), 'none', mode);
  }

  // The hiding rule wins on specificity (an id) over the blur declarations, as long as none of them is !important.
  const blurs = rules.filter(r => value(r, 'backdrop-filter') && !r.selectors.some(s => / #home \*$/.test(s)));
  assert.ok(blurs.length >= 3, 'the card, the plain tiles and the panels have a blur');
  blurs.forEach(r => assert.ok(!/!important/.test(r.body), r.selectors.join(', ') + ' is not !important'));

  // The blurs inside #home are the card and the plain tiles; the panels belong to overlays, which sit outside it.
  const html = fs.readFileSync(path.join(root, 'index.html'), 'utf8');
  const home = html.slice(html.indexOf('<main id="home"'), html.indexOf('</main>'));
  assert.ok(home.includes('class="weather focusable"'), 'the weather card is inside #home');
  assert.ok(!home.includes('class="panel'), 'no panel is inside #home');
  assert.ok(!home.includes('class="overlay') && !home.includes('class="ambient'), 'no overlay or picture mode inside #home');
});

test('fake page: hidden on an SVG element is a plain property, as in a browser', async () => {
  const page = await pageWith(0, true, 10);
  const stars = starsOf(page.$('weather'));
  assert.equal(stars.hasAttribute('hidden'), true);
  stars.hidden = false;
  assert.equal(stars.hasAttribute('hidden'), true, 'the property does not clear the attribute');
  stars.removeAttribute('hidden');
  assert.equal(stars.hasAttribute('hidden'), false);
});

// ---------- the detail tiles and the new weather fields ----------

// weather.js alone, with its own localStorage and fetch.
function weatherWith(options) {
  const window = {};
  const ctx = vm.createContext({ window, Math, Date, Number, String, Array, Object, JSON,
    localStorage: options.localStorage || { getItem: () => null, setItem() {} },
    fetch: options.fetch || (() => Promise.reject(new Error('no fetch'))) });
  vm.runInContext(fs.readFileSync(path.join(root, 'js', 'weather.js'), 'utf8'), ctx, { filename: 'weather.js' });
  return window.TVWeather;
}
const answering = raw => () => Promise.resolve({ ok: true, json: () => Promise.resolve(raw) });

// The live Open-Meteo answer of 2026-10-25 20:15 (the spec's verified values), on top of the base fixture.
function liveRaw(overrides) {
  const raw = rawWeather(3, false, 17.6);
  Object.assign(raw.current, { relative_humidity_2m: 72, wind_direction_10m: 253, wind_gusts_10m: 35.6, dew_point_2m: 4.1, uv_index: 0, visibility: 26220 },
    (overrides || {}).current);
  Object.assign(raw.daily, { uv_index_max: [1.15, 0.40, 0.5, 0.5, 0.5, 0.5, 0.5], precipitation_sum: [0.10, 5.40, 0, 0, 0, 0, 0] },
    (overrides || {}).daily);
  return raw;
}
async function tilesPage(raw, config) {
  const page = loadPage({ fetch: answering(raw), config });
  page.deliver(placeMessage('config'));
  await page.settle();
  page.$('weather').click();
  assert.equal(page.$('forecast').hidden, false, 'the forecast is open');
  return page;
}
const tile = (page, name) => page.document.querySelector('.fc-tiles .fc-' + name);
// The markup of one tile (the fake DOM keeps only the string last assigned to an element's innerHTML).
const tileHtml = (page, name) => {
  const all = page.$('fcTiles').innerHTML, start = all.indexOf('<div class="fc-tile fc-' + name + '">'), end = all.indexOf('<div class="fc-tile ', start + 1);
  return all.slice(start, end < 0 ? undefined : end);
};
const big = (page, name) => tile(page, name).querySelector('.t-big').textContent;
const GLYPHS = /NaN|undefined|null|\u2013|\u2014/;

test('normalize: the new fields', async () => {
  const w = await weatherWith({ fetch: answering(liveRaw()) }).fetch({ latitude: 1, longitude: 2 });
  assert.equal(w.current.windDir, 253);
  assert.equal(w.current.gusts, 35.6);
  assert.equal(w.current.dew, 4.1);
  assert.equal(w.current.uv, 0);
  assert.equal(w.current.visibility, 26220);
  assert.equal(w.daily[0].uvMax, 1.15);
  assert.equal(w.daily[1].uvMax, 0.40);
  assert.equal(w.daily[0].rain, 0.10);
  assert.equal(w.daily[1].rain, 5.40);
});

test('normalize: a missing or null value stays null', async () => {
  const raw = rawWeather(3, false, 10);
  raw.current.visibility = null;
  raw.daily.uv_index_max = [null, 1, 1, 1, 1, 1, 1];
  const w = await weatherWith({ fetch: answering(raw) }).fetch({ latitude: 1, longitude: 2 });
  for (const k of ['windDir', 'gusts', 'dew', 'uv', 'visibility']) assert.strictEqual(w.current[k], null, k);
  assert.strictEqual(w.daily[0].uvMax, null);
  assert.strictEqual(w.daily[0].rain, null);
});

test('the fetch asks for the new fields', async () => {
  let asked = '';
  await weatherWith({ fetch: u => { asked = u; return answering(liveRaw())(); } }).fetch({ latitude: 1, longitude: 2 });
  for (const f of ['wind_direction_10m', 'wind_gusts_10m', 'dew_point_2m', 'uv_index', 'visibility', 'uv_index_max', 'precipitation_sum']) {
    assert.ok(asked.includes(f), f);
  }
});

test('compassName: bearings', () => {
  const W = loadWeather();
  assert.equal(W.compassName(0), 'N');
  assert.equal(W.compassName(359), 'N');
  assert.equal(W.compassName(360), 'N');
  assert.equal(W.compassName(11.25), 'NNE');
  assert.equal(W.compassName(11.2), 'N');
  assert.equal(W.compassName(253), 'WSW');
  assert.equal(W.compassName(348.75), 'N');
  assert.deepEqual(Array.from({ length: 16 }, (_, i) => W.compassName(i * 22.5)),
    ['N', 'NNE', 'NE', 'ENE', 'E', 'ESE', 'SE', 'SSE', 'S', 'SSW', 'SW', 'WSW', 'W', 'WNW', 'NW', 'NNW']);
});

test('uvCategory: bands', () => {
  const W = loadWeather();
  const bands = [[0, 'Low'], [2.4, 'Low'], [2.5, 'Moderate'], [3, 'Moderate'], [5.4, 'Moderate'], [6, 'High'], [7.4, 'High'],
    [8, 'Very high'], [10.4, 'Very high'], [11, 'Extreme'], [14, 'Extreme']];
  for (const [v, name] of bands) assert.equal(W.uvCategory(v), name, String(v));
});

test('cache: the v1 key is ignored', () => {
  const here = { latitude: 1, longitude: 2 };
  const store = { 'tvhome.weather.v1': JSON.stringify({ raw: rawWeather(3, false, 10), fetchedAt: Date.now(), loc: here }) };
  const W = weatherWith({ localStorage: { getItem: k => (k in store ? store[k] : null), setItem() {} } });
  assert.strictEqual(W.cached(here), null);
  store['tvhome.weather.v2'] = store['tvhome.weather.v1'];
  assert.ok(W.cached(here), 'the v2 key is read');
});

test('cache: read only for the place it was stored for, to 2 decimals', async () => {
  const store = {};
  const W = weatherWith({ fetch: answering(rawWeather(3, false, 10)), localStorage: { getItem: k => (k in store ? store[k] : null), setItem: (k, v) => { store[k] = v; } } });
  await W.fetch({ latitude: 55.9512, longitude: -3.1888 });
  assert.deepEqual(JSON.parse(store['tvhome.weather.v2']).loc, { latitude: 55.95, longitude: -3.19 }, 'stored rounded');
  assert.ok(W.cached({ latitude: 55.95, longitude: -3.19 }));
  assert.ok(W.cached({ latitude: 55.9499, longitude: -3.1901 }), 'the same place to 2 decimals');
  assert.strictEqual(W.cached({ latitude: 55.96, longitude: -3.19 }), null);
  assert.strictEqual(W.cached(), null, 'no place, no cache');
  store['tvhome.weather.v2'] = JSON.stringify({ raw: rawWeather(3, false, 10), fetchedAt: Date.now() });
  assert.strictEqual(W.cached({ latitude: 55.95, longitude: -3.19 }), null, 'a cache without a place is not trusted');
});

test('tiles: render from the live values', async () => {
  const page = await tilesPage(liveRaw());
  const all = page.$('fcTiles').textContent;
  for (const s of ['WSW 253°', '36 km/h', '26 km', '0.1 mm', '5.4 mm expected tomorrow', 'Peak today 1, Low', '72%', 'Dew point 4°', '18 km/h', '07:00', '17:00']) {
    assert.ok(all.includes(s), 'the tiles show ' + s + ', got: ' + all);
  }
  assert.ok(!GLYPHS.test(all));
  assert.deepEqual(page.$('fcTiles').children.map(t => Array.from(t.classes).filter(c => c !== 'fc-tile')[0]),
    ['fc-uv', 'fc-wind', 'fc-sun', 'fc-rain', 'fc-vis', 'fc-humid'], 'six tiles in order');
  assert.equal(page.document.querySelector('.fc-facts'), null, 'the old facts row is gone');
  assert.ok(!/\bNow\b/.test(page.$('forecast').textContent), 'no Now fact');
});

test('tiles: the compass arrow points where the wind blows to, and the scales sit at the values', async () => {
  const page = await tilesPage(liveRaw({ current: { uv_index: 5.5 } }));
  assert.ok(tileHtml(page, 'wind').includes('rotate(73 60 60)'), '253 + 180 = 433, which is 73');
  assert.ok(tileHtml(page, 'uv').includes('left:50%'), 'UV 5.5 of 11');
  assert.ok(tileHtml(page, 'humid').includes('width:72%'));
  const high = await tilesPage(liveRaw({ current: { uv_index: 14 } }));
  assert.ok(tileHtml(high, 'uv').includes('left:100%'), 'the marker stops at the end of the scale');
});

test('tiles: the sun curve has one clip path and every id in the panel is unique', async () => {
  const page = await tilesPage(liveRaw());
  const html = tileHtml(page, 'sun');
  assert.equal((html.match(/clip-path="url\(#fcSunAbove\)"/g) || []).length, 1);
  assert.ok(html.includes('<circle'), 'a dot for now');
  const ids = Array.from(page.$('fcTiles').innerHTML.matchAll(/ id="([^"]+)"/g), m => m[1]);
  assert.ok(ids.includes('fcSunAbove'));
  assert.equal(new Set(ids).size, ids.length, 'ids: ' + ids.join(' '));
});

test('tiles: visibility is one decimal under 10 km, whole above', async () => {
  for (const [metres, want] of [[26220, '26 km'], [9500, '9.5 km'], [9960, '10 km'], [10000, '10 km'], [800, '0.8 km'], [0, '0.0 km']]) {
    const page = await tilesPage(liveRaw({ current: { visibility: metres } }));
    assert.equal(big(page, 'vis'), want, metres + ' m');
  }
});

test('tiles: a missing field shows a dash', async () => {
  const page = await tilesPage(liveRaw({ current: { visibility: null, wind_gusts_10m: null } }));
  assert.equal(big(page, 'vis'), '-');
  const gusts = tile(page, 'wind').querySelectorAll('.wind-rows div').filter(d => d.querySelector('span').textContent === 'Gusts')[0];
  assert.equal(gusts.querySelector('b').textContent, '-');
  assert.ok(!GLYPHS.test(page.$('forecast').textContent), 'no NaN, undefined, null or dash glyph');

  const none = await tilesPage(rawWeather(3, false, 10));
  assert.ok(!GLYPHS.test(none.$('forecast').textContent), 'a payload without the new fields: ' + none.$('forecast').textContent);
  const noPop = await tilesPage(liveRaw({ daily: { precipitation_probability_max: [null, null, null, null, null, null, null] } }));
  assert.ok(!GLYPHS.test(noPop.$('forecast').textContent), 'a missing chance of rain shows a hyphen');
  assert.equal(noPop.document.querySelector('.fc-pop').textContent, '-');
  assert.equal(big(none, 'uv'), '-');
  assert.equal(big(none, 'rain'), '-');
  assert.equal(big(none, 'vis'), '-');
  assert.equal(tile(none, 'humid').querySelector('.t-sub').textContent, 'Dew point -');
  assert.ok(!tileHtml(none, 'uv').includes('uvmark'), 'no marker without a value');
  assert.ok(!tileHtml(none, 'wind').includes('rotate('), 'no arrow without a direction');
});

test('tiles: the Fahrenheit setting converts wind and dew point', async () => {
  const config = fakeConfig();
  config.defaults.unit = 'F';
  const page = await tilesPage(liveRaw(), config);
  const all = page.$('fcTiles').textContent;
  assert.ok(all.includes('22 mph') && all.includes('Dew point 39°'), all);
});

