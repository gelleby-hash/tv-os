// Tests for the home page following the host's location message: no weather until there is a place, no place name on screen,
// and a "Weather off" tile when the weather is switched off or there is no place. Runs the real page scripts in a fake DOM.
'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { loadPage, fakeConfig, placeMessage, root } = require('./fake-page.cjs');

// A clear morning: 9:00 on 2 November 2026, by the page's own clock.
const NOW = new Date(2026, 10, 2, 9, 0, 0).getTime();
const CACHE_KEY = 'tvhome.weather.v2';

// A raw Open-Meteo answer for that morning: clear sky, in daylight, seven days with sunrise at 07:00 and sunset at 17:00.
function rawClear(temperature = 12) {
  const pad = n => (n < 10 ? '0' : '') + n;
  const stamp = d => d.getFullYear() + '-' + pad(d.getMonth() + 1) + '-' + pad(d.getDate()) + 'T' + pad(d.getHours()) + ':' + pad(d.getMinutes());
  const start = new Date(NOW);
  const hours = Array.from({ length: 48 }, (_, i) => new Date(NOW + i * 3600e3));
  const days = Array.from({ length: 7 }, (_, i) => new Date(start.getFullYear(), start.getMonth(), start.getDate() + i));
  const at = (d, h) => stamp(new Date(d.getFullYear(), d.getMonth(), d.getDate(), h, 0));
  return {
    current: { temperature_2m: temperature, apparent_temperature: 10, relative_humidity_2m: 70, weather_code: 0, is_day: 1, wind_speed_10m: 10, cloud_cover: 5 },
    hourly: { time: hours.map(stamp), temperature_2m: hours.map(() => temperature), weather_code: hours.map(() => 0),
      precipitation_probability: hours.map(() => 10), is_day: hours.map(() => 1) },
    daily: { time: days.map(d => stamp(d).slice(0, 10)), weather_code: days.map(() => 0), temperature_2m_max: days.map(() => 15),
      temperature_2m_min: days.map(() => 7), sunrise: days.map(d => at(d, 7)), sunset: days.map(d => at(d, 17)),
      precipitation_probability_max: days.map(() => 20) }
  };
}

// A fetch that records the URLs asked for and answers each with the raw answer built for it.
function recordingFetch(answer = rawClear) {
  const urls = [];
  const fn = url => { urls.push(url); return Promise.resolve({ ok: true, json: () => Promise.resolve(answer()) }); };
  fn.urls = urls;
  return fn;
}

async function pageWith(options = {}) {
  const fetch = options.fetch || recordingFetch();
  const page = loadPage(Object.assign({ now: NOW }, options, { fetch }));
  await page.settle();
  page.fetch = fetch;
  return page;
}

// Open-Meteo's geocoding answer for "Hales" in GB, as read live on 2026-11-02 (the fields the page uses).
const HALES = { results: [
  { name: 'Hales', latitude: 52.52225, longitude: 1.50869, country_code: 'GB', admin1: 'England', admin2: 'Norfolk' },
  { name: 'Hales', latitude: 52.90232, longitude: -2.42784, country_code: 'GB', admin1: 'England', admin2: 'Staffordshire' },
  { name: 'Halesworth', latitude: 52.34312, longitude: 1.50391, country_code: 'GB', admin1: 'England', admin2: 'Suffolk' }
] };
const GEOCODING = 'https://geocoding-api.open-meteo.com/v1/search?';

// A fetch that answers the weather with rawClear and hands each town search to geo(url), which returns the response's promise.
// The default geo answers every search with HALES. fetch.searches lists the search URLs asked for.
function placesFetch(geo = () => Promise.resolve({ ok: true, json: () => Promise.resolve(HALES) })) {
  const searches = [];
  const fn = url => {
    if (url.startsWith(GEOCODING)) { searches.push(url); return geo(url); }
    return Promise.resolve({ ok: true, json: () => Promise.resolve(rawClear()) });
  };
  fn.searches = searches;
  return fn;
}
const ok = body => ({ ok: true, json: () => Promise.resolve(body) });

// Moves the focus to an element as the mouse does, then presses OK on it.
function enterOn(page, el) {
  assert.ok(el, 'the element to press is there');
  page.fire(el, 'mouseover');
  assert.equal(page.focused(), el, 'the element took the focus');
  page.pressKey(13);
}
const keyFor = (page, key) => page.$('locKeys').querySelector('[data-key="' + key + '"]');
const resultButtons = page => page.$('locResults').querySelectorAll('.focusable');

// A page whose host has no place, with the pop-up open (as the none message opens it), then the search screen for GB.
async function searchScreen(fetch) {
  const page = await pageWith({ fetch: fetch || placesFetch() });
  page.deliver(placeMessage('none'));
  await page.settle();
  page.pressKey(13);   // Choose a place
  page.pressKey(13);   // the region's country, GB
  assert.equal(page.$('locSearch').hidden, false);
  return page;
}
function type(page, letters) {
  for (const ch of letters) enterOn(page, keyFor(page, ch));
}

test('before any location message no weather request is made', async () => {
  const page = await pageWith();
  assert.deepEqual(page.fetch.urls, []);
  assert.equal(page.$('wxCond').textContent, 'Loading weather…');
});

test('a location message starts one request, for its coordinates', async () => {
  const page = await pageWith();
  page.deliver(placeMessage('config'));
  await page.settle();
  assert.equal(page.fetch.urls.length, 1);
  assert.ok(page.fetch.urls[0].includes('latitude=10&longitude=20'), page.fetch.urls[0]);
  assert.equal(page.$('wxTemp').textContent, '12°');
});

test('the coordinates are rounded to 2 decimals before they are sent', async () => {
  const page = await pageWith();
  page.deliver(Object.assign(placeMessage('windows'), { latitude: 55.95678, longitude: -3.19123 }));
  await page.settle();
  assert.ok(page.fetch.urls[0].includes('latitude=55.96&longitude=-3.19&'), page.fetch.urls[0]);
});

test('the same place again does not ask again', async () => {
  const page = await pageWith();
  page.deliver(placeMessage('config'));
  await page.settle();
  page.deliver(placeMessage('picked'));
  await page.settle();
  assert.equal(page.fetch.urls.length, 1);
});

for (const source of ['off', 'none']) {
  test('source ' + source + ': the weather card is hidden, the Weather off tile is shown, the picture screen has no weather', async () => {
    const page = await pageWith();
    page.deliver(placeMessage('config'));
    await page.settle();
    assert.equal(page.$('weather').hidden, false);
    assert.equal(page.$('weatherOff').hidden, true, 'no tile while the weather shows');
    assert.ok(page.$('wxTemp').textContent.length > 0);

    page.deliver(placeMessage(source));
    await page.settle();
    assert.equal(page.fetch.urls.length, 1, 'no request for ' + source);
    assert.equal(page.$('weather').hidden, true);
    assert.equal(page.$('weatherOff').hidden, false);
    assert.equal(page.$('weatherOff').textContent, 'Weather off');
    // none also asks for a place: Back answers "not now" and leaves the home screen as it is.
    assert.equal(page.$('locAsk').hidden, source !== 'none');
    if (source === 'none') page.pressKey(166);
    page.document.querySelector('[data-action="ambient"]').click();
    assert.equal(page.$('ambient').hidden, false);
    assert.equal(page.$('ambWx').innerHTML, '');
    assert.equal(page.$('ambWx').textContent, '');
  });
}

test('the Weather off tile is a focusable tile in the side column, before the weather card', async () => {
  const page = await pageWith();
  const tile = page.$('weatherOff');
  assert.ok(tile.classes.has('focusable'));
  assert.equal(tile.dataset.action, 'locAsk');
  assert.equal(tile.hidden, true, 'hidden until the weather is off');
  const side = page.$('weather').closest('.side');
  assert.ok(side.contains(tile));
  assert.ok(side.children.indexOf(tile) < side.children.indexOf(page.$('weather')));
});

test('Enter on the Weather off tile opens the pop-up, and a click does too', async () => {
  const page = await pageWith();
  page.deliver(placeMessage('off'));
  await page.settle();
  assert.equal(page.$('locAsk').hidden, true, 'off does not ask by itself');
  enterOn(page, page.$('weatherOff'));
  assert.equal(page.$('locAsk').hidden, false);
  assert.equal(page.focused().dataset.action, 'locChoose');
  assert.ok(page.document.body.classes.has('overlay-on'));
  page.pressKey(27);   // Escape closes it like the remote's Back
  assert.equal(page.$('locAsk').hidden, true);
  assert.equal(page.focused(), page.$('weatherOff'), 'back on the tile it was opened from');
  page.$('weatherOff').click();
  assert.equal(page.$('locAsk').hidden, false);
  assert.deepEqual(page.logs, []);
});

test('with the weather loaded no place name is on screen', async () => {
  const page = await pageWith();
  page.deliver(placeMessage('config'));
  await page.settle();
  assert.equal(page.$('weatherOff').hidden, true);
  assert.equal(page.$('sub').textContent, 'Clear skies and sunshine today.');

  page.document.querySelector('[data-action="ambient"]').click();
  const ambient = page.$('ambWx').textContent;
  assert.ok(ambient.includes('12°') && ambient.includes('Clear'), ambient);
  assert.ok(!ambient.includes(' in '), ambient);

  page.pressKey(13);   // any key leaves the picture screen
  page.$('weather').click();
  assert.equal(page.$('forecast').hidden, false);
  assert.ok(page.$('fcPlace').textContent.startsWith('Updated'), page.$('fcPlace').textContent);
  assert.ok(/^Updated \d\d:\d\d$/.test(page.$('fcPlace').textContent), page.$('fcPlace').textContent);
});

test('the page does not read a place from the host config', async () => {
  const config = fakeConfig();
  config.location = { name: 'Elsewhere', latitude: 1, longitude: 2 };
  const page = await pageWith({ config });
  assert.deepEqual(page.fetch.urls, [], 'the config place is ignored');
  page.deliver(placeMessage('config'));
  await page.settle();
  assert.ok(page.fetch.urls[0].includes('latitude=10&longitude=20'));
  assert.ok(!page.document.body.textContent.includes('Elsewhere'));
});

test('the cache is for one place: another place gets no cache from it', async () => {
  const seeded = { [CACHE_KEY]: JSON.stringify({ raw: rawClear(12), fetchedAt: NOW, loc: { latitude: 10, longitude: 20 } }) };
  const page = await pageWith({ storage: seeded });
  const W = page.window.TVWeather;
  assert.ok(W.cached({ latitude: 10, longitude: 20 }), 'the cache is for 10, 20');
  assert.equal(W.cached({ latitude: 30, longitude: 40 }), null, 'but not for 30, 40');
  assert.equal(W.cached(), null, 'and a cache is only read for a place');

  page.deliver(placeMessage('config'));
  await page.settle();
  assert.deepEqual(page.fetch.urls, [], 'a fresh cache for this place needs no request');
  assert.equal(page.$('wxTemp').textContent, '12°', 'shown from the cache');
});

test('a different place asks again and shows nothing of the old place while it loads', async () => {
  const seeded = { [CACHE_KEY]: JSON.stringify({ raw: rawClear(12), fetchedAt: NOW, loc: { latitude: 10, longitude: 20 } }) };
  let release;
  const slow = new Promise(resolve => { release = resolve; });
  const urls = [];
  const fetch = url => { urls.push(url); return slow.then(() => ({ ok: true, json: () => Promise.resolve(rawClear(3)) })); };
  const page = await pageWith({ storage: seeded, fetch });
  page.deliver(placeMessage('config'));
  await page.settle();
  assert.equal(page.$('wxTemp').textContent, '12°');
  assert.equal(urls.length, 0);

  page.deliver(Object.assign(placeMessage('picked'), { latitude: 30, longitude: 40 }));
  await page.settle();
  assert.equal(urls.length, 1);
  assert.ok(urls[0].includes('latitude=30&longitude=40'), urls[0]);
  assert.equal(page.$('wxCond').textContent, 'Loading weather…');
  assert.equal(page.$('wxTemp').textContent, '--°', 'nothing of the old place');
  assert.equal(page.$('wxHours').innerHTML, '');
  assert.equal(page.$('wxIcon').innerHTML, '');
  release();
  await page.settle();
  assert.equal(page.$('wxTemp').textContent, '3°');
  assert.equal(JSON.parse(page.storage[CACHE_KEY]).loc.latitude, 30, 'the cache is stored with its place');
});

test('an answer for the old place that arrives late is ignored', async () => {
  const waiting = {};
  const urls = [];
  const fetch = url => { urls.push(url); return new Promise(resolve => { waiting[url.includes('latitude=10') ? 'old' : 'new'] = resolve; }); };
  const page = await pageWith({ fetch });
  page.deliver(placeMessage('config'));
  await page.settle();
  page.deliver(Object.assign(placeMessage('picked'), { latitude: 30, longitude: 40 }));
  await page.settle();
  assert.equal(urls.length, 2);
  waiting.new({ ok: true, json: () => Promise.resolve(rawClear(5)) });
  await page.settle();
  waiting.old({ ok: true, json: () => Promise.resolve(rawClear(25)) });
  await page.settle();
  assert.equal(page.$('wxTemp').textContent, '5°', 'the late answer for the old place did not replace it');
});

test('the weather returning after off asks again, and turning it off clears what was shown', async () => {
  const page = await pageWith();
  page.deliver(placeMessage('config'));
  await page.settle();
  page.deliver(placeMessage('off'));
  await page.settle();
  assert.equal(page.$('sunline').innerHTML, '');
  assert.equal(page.$('sub').textContent, 'A fresh start. Take it easy.', 'the fallback sentence');
  page.deliver(placeMessage('config'));
  await page.settle();
  assert.equal(page.$('weather').hidden, false);
  assert.equal(page.$('weatherOff').hidden, true);
  assert.equal(page.$('wxTemp').textContent, '12°');
});

// ---------- the pop-up, the country list and the letter grid ----------

test('none on the home screen opens the pop-up on its first button; Back closes it and it does not come back this run', async () => {
  const page = await pageWith();
  const before = page.focused();
  page.deliver(placeMessage('none'));
  await page.settle();
  const ask = page.$('locAsk');
  assert.equal(ask.hidden, false);
  assert.ok(ask.classes.has('overlay') && ask.classes.has('scope') && ask.querySelector('.panel'));
  assert.equal(page.focused(), ask.querySelector('[data-action="locChoose"]'), 'the first button on screen');
  assert.equal(page.focused().dataset.action, 'locChoose');
  assert.equal(ask.querySelector('h3').textContent, 'Show the weather?');
  assert.ok(page.document.body.classes.has('overlay-on'));

  page.pressKey(166);
  assert.equal(ask.hidden, true);
  assert.equal(page.document.body.classes.has('overlay-on'), false);
  assert.equal(page.focused(), before, 'the focus is back where it was');
  page.deliver(placeMessage('none'));
  await page.settle();
  assert.equal(ask.hidden, true, 'not asked twice in one run');
  assert.deepEqual(page.posted.filter(m => m.type.startsWith('location')), [], 'Back is "not now": nothing is sent');
});

test('the pop-up has its three choices, the Windows one hidden until a place and Windows location allow it', async () => {
  const page = await pageWith();
  const buttons = page.$('locAsk').querySelectorAll('button');
  assert.deepEqual(buttons.map(b => b.dataset.action), ['locTurnOn', 'locChoose', 'locOff', 'locWindows']);
  assert.deepEqual(buttons.slice(1).map(b => b.textContent), ['Choose a place', 'Continue without weather', 'Use Windows location']);
  assert.equal(buttons[0].hidden, true);
  assert.equal(buttons[3].hidden, true);
});

test('none while the forecast is open waits for it to close', async () => {
  const page = await pageWith();
  page.$('weather').click();
  assert.equal(page.$('forecast').hidden, false);
  page.deliver(placeMessage('none'));
  await page.settle();
  assert.equal(page.$('locAsk').hidden, true, 'not over the forecast');
  assert.equal(page.$('forecast').hidden, false);
  page.pressKey(166);
  assert.equal(page.$('forecast').hidden, true);
  assert.equal(page.$('locAsk').hidden, false, 'asked once the forecast closed');
  assert.equal(page.focused().dataset.action, 'locChoose');
});

test('none while the page is hidden (an app in front) waits until it is visible', async () => {
  const page = await pageWith();
  page.document.hidden = true;
  page.deliver(placeMessage('none'));
  await page.settle();
  assert.equal(page.$('locAsk').hidden, true, 'never over an app');
  page.document.hidden = false;
  page.fire(page.document, 'visibilitychange');
  await page.settle();
  assert.equal(page.$('locAsk').hidden, false);
});

test('a place arriving before the page is visible cancels the waiting pop-up', async () => {
  const page = await pageWith();
  page.document.hidden = true;
  page.deliver(placeMessage('none'));
  page.deliver(placeMessage('windows'));
  await page.settle();
  page.document.hidden = false;
  page.fire(page.document, 'visibilitychange');
  await page.settle();
  assert.equal(page.$('locAsk').hidden, true);
});

test('the host\'s Home does not answer the pop-up: it is still there on the home screen', async () => {
  const page = await pageWith();
  page.deliver(placeMessage('none'));
  await page.settle();
  page.deliver({ type: 'home' });
  assert.equal(page.$('locAsk').hidden, false);
  assert.equal(page.focused().dataset.action, 'locChoose');
  page.pressKey(166);
  assert.equal(page.$('locAsk').hidden, true);
  assert.equal(page.focused(), page.document.querySelector('#grid .tile'), 'Home starts from the first tile');
});

test('Choose a place opens the countries on the region\'s country; OK there opens the letters; Back steps back one screen at a time', async () => {
  const page = await pageWith();
  const homeTile = page.focused();
  page.deliver(placeMessage('none'));
  await page.settle();
  page.pressKey(13);
  assert.equal(page.$('locAsk').hidden, true);
  assert.equal(page.$('locCountry').hidden, false);
  assert.equal(page.focused().dataset.country, 'GB');
  assert.equal(page.focused().textContent, 'United Kingdom');
  assert.ok(page.$('locCountries').querySelectorAll('.focusable').length > 200);

  page.pressKey(13);
  assert.equal(page.$('locCountry').hidden, true);
  assert.equal(page.$('locSearch').hidden, false);
  assert.equal(page.focused(), keyFor(page, 'A'), 'the letters start on A');

  page.pressKey(166);
  assert.equal(page.$('locSearch').hidden, true);
  assert.equal(page.$('locCountry').hidden, false);
  assert.equal(page.focused().dataset.country, 'GB', 'back on the country it came from');
  page.pressKey(166);
  assert.equal(page.$('locCountry').hidden, true);
  assert.equal(page.$('locAsk').hidden, false);
  assert.equal(page.focused().dataset.action, 'locChoose');
  assert.ok(page.document.body.classes.has('overlay-on'), 'the home screen stays dimmed between the screens');
  page.pressKey(166);
  assert.equal(page.$('locAsk').hidden, true);
  assert.equal(page.focused(), homeTile, 'the focus from before the first screen opened');
});

test('the on-screen Back buttons step back like the remote, by OK and by a click', async () => {
  const page = await searchScreen();
  enterOn(page, page.$('locSearch').querySelector('[data-action="locBack"]'));
  assert.equal(page.$('locCountry').hidden, false);
  page.$('locCountry').querySelector('[data-action="locBack"]').click();
  assert.equal(page.$('locAsk').hidden, false);
});

test('another country: the letters search in it', async () => {
  const page = await pageWith({ fetch: placesFetch() });
  page.deliver(placeMessage('none'));
  await page.settle();
  page.pressKey(13);
  enterOn(page, page.$('locCountries').querySelector('[data-country="PT"]'));
  type(page, 'LIS');
  page.tick(400);
  assert.equal(page.fetch.searches.length, 1);
  assert.ok(page.fetch.searches[0].includes('name=LIS&countryCode=PT&'), page.fetch.searches[0]);
});

test('the letter grid: 9 columns of A to Z, space, hyphen, apostrophe and delete', async () => {
  const page = await searchScreen();
  const keys = page.$('locKeys').querySelectorAll('.focusable').map(k => k.dataset.key);
  for (const ch of 'ABCDEFGHIJKLMNOPQRSTUVWXYZ') assert.ok(keys.includes(ch), ch);
  for (const k of ['space', '-', "'", 'del']) assert.ok(keys.includes(k), k);
  assert.equal(keys.length, 30);
  const css = fs.readFileSync(path.join(root, 'css', 'style.css'), 'utf8');
  assert.ok(/\.loc-keys\s*\{[^}]*grid-template-columns:\s*repeat\(9,/.test(css));
});

test('OK on H, A, L searches for HAL in GB 400 ms after the last letter; OK on a result sends it and closes', async () => {
  const page = await searchScreen();
  const homeTile = page.document.querySelector('#grid .tile');
  type(page, 'HA');
  page.tick(1000);
  assert.deepEqual(page.fetch.searches, [], 'two letters do not search');
  type(page, 'L');
  assert.equal(page.$('locText').textContent, 'HAL');
  page.tick(399);
  assert.deepEqual(page.fetch.searches, [], 'not before 400 ms');
  page.tick(1);
  assert.deepEqual(page.fetch.searches, [GEOCODING + 'name=HAL&countryCode=GB&count=8&language=en&format=json']);
  await page.settle();

  const results = resultButtons(page);
  assert.equal(results.length, 3);
  assert.ok(results[0].textContent.includes('Hales') && results[0].textContent.includes('Norfolk, England'), results[0].textContent);
  assert.ok(results[1].textContent.includes('Staffordshire, England'));
  assert.ok(results[2].textContent.includes('Halesworth'));

  enterOn(page, results[2]);
  assert.deepEqual(page.posted.filter(m => m.type === 'locationPick'), [{ type: 'locationPick', latitude: 52.34, longitude: 1.5 }]);
  for (const id of ['locAsk', 'locCountry', 'locSearch']) assert.equal(page.$(id).hidden, true, id);
  assert.equal(page.document.body.classes.has('overlay-on'), false);
  assert.equal(page.focused(), homeTile);
});

test('a letter typed quickly after another restarts the wait: one search for the whole word', async () => {
  const page = await searchScreen();
  type(page, 'HAL');
  page.tick(300);
  type(page, 'E');
  page.tick(300);
  assert.deepEqual(page.fetch.searches, []);
  page.tick(100);
  assert.equal(page.fetch.searches.length, 1);
  assert.ok(page.fetch.searches[0].includes('name=HALE&'));
});

test('space, hyphen and apostrophe go into the text, encoded in the request', async () => {
  const page = await searchScreen();
  type(page, 'ST');
  enterOn(page, keyFor(page, 'space'));
  enterOn(page, keyFor(page, '-'));
  enterOn(page, keyFor(page, "'"));
  assert.equal(page.$('locText').textContent, "ST -'");
  page.tick(400);
  assert.ok(page.fetch.searches[0].includes("name=ST%20-'&countryCode=GB"), page.fetch.searches[0]);
});

test('the delete key and the keyboard: Backspace deletes, letters type, Back still steps back', async () => {
  const page = await searchScreen();
  page.pressKey(72);   // H on a keyboard
  page.pressKey(65);
  page.pressKey(76);
  assert.equal(page.$('locText').textContent, 'HAL');
  assert.equal(page.$('locSearch').hidden, false);
  const backspace = page.pressKey(8);
  assert.ok(backspace.defaultPrevented);
  assert.equal(page.$('locText').textContent, 'HA');
  assert.equal(page.$('locSearch').hidden, false, 'Backspace does not leave the screen');
  enterOn(page, keyFor(page, 'del'));
  assert.equal(page.$('locText').textContent, 'H');
  page.pressKey(8);
  page.pressKey(8);
  assert.equal(page.$('locText').textContent, '');
  assert.equal(page.$('locSearch').hidden, false);
  page.pressKey(90);
  assert.equal(page.$('locText').textContent, 'Z');
  page.pressKey(166);
  assert.equal(page.$('locSearch').hidden, true);
  assert.equal(page.$('locCountry').hidden, false);
});

test('going back to the letters starts again with no text and no results', async () => {
  const page = await searchScreen();
  type(page, 'HAL');
  page.tick(400);
  await page.settle();
  assert.equal(resultButtons(page).length, 3);
  page.pressKey(166);
  page.pressKey(13);
  assert.equal(page.$('locText').textContent, '');
  assert.equal(resultButtons(page).length, 0);
  assert.equal(page.$('locStatus').textContent, '');
});

test('fewer than 3 letters again clears the results, and an answer still on its way is not shown', async () => {
  let release;
  const page = await searchScreen(placesFetch(() => new Promise(resolve => { release = () => resolve(ok(HALES)); })));
  type(page, 'HAL');
  page.tick(400);
  page.pressKey(8);
  release();
  await page.settle();
  assert.equal(resultButtons(page).length, 0);
});

test('two searches answered in reverse order show only the newer one\'s results', async () => {
  const waiting = {};
  const page = await searchScreen(placesFetch(url => new Promise(resolve => { waiting[/name=([^&]*)/.exec(url)[1]] = resolve; })));
  type(page, 'HAL');
  page.tick(400);
  type(page, 'E');
  page.tick(400);
  assert.equal(page.fetch.searches.length, 2);
  waiting.HALE(ok(HALES));
  await page.settle();
  waiting.HAL(ok({ results: [{ name: 'Halifax', latitude: 53.7, longitude: -1.85, admin1: 'England', admin2: 'Calderdale' }] }));
  await page.settle();
  const shown = resultButtons(page).map(b => b.textContent).join(' | ');
  assert.equal(resultButtons(page).length, 3, shown);
  assert.ok(!shown.includes('Halifax'), shown);
});

test('a failed search says so, and an empty answer says there is no match', async () => {
  let answer = () => Promise.reject(new Error('offline'));
  const page = await searchScreen(placesFetch(url => answer(url)));
  type(page, 'HAL');
  page.tick(400);
  await page.settle();
  assert.equal(page.$('locStatus').textContent, 'Search is not available right now.');
  assert.equal(resultButtons(page).length, 0);
  assert.equal(page.$('locSearch').hidden, false);

  answer = () => Promise.resolve(ok({ generationtime_ms: 0.2 }));
  type(page, 'X');
  page.tick(400);
  await page.settle();
  assert.equal(page.$('locStatus').textContent, 'No matching places.');

  answer = () => Promise.resolve(ok(HALES));
  enterOn(page, keyFor(page, 'del'));
  page.tick(400);
  await page.settle();
  assert.equal(page.$('locStatus').textContent, '', 'the message goes when there are results');
  assert.equal(resultButtons(page).length, 3);
});

test('Continue without weather sends locationOff and closes', async () => {
  const page = await pageWith();
  page.deliver(placeMessage('none'));
  await page.settle();
  enterOn(page, page.$('locAsk').querySelector('[data-action="locOff"]'));
  assert.deepEqual(page.posted.filter(m => m.type.startsWith('location')), [{ type: 'locationOff' }]);
  assert.equal(page.$('locAsk').hidden, true);
  assert.equal(page.document.body.classes.has('overlay-on'), false);
});

test('English whatever the host says its language is: the screens and the country names', async () => {
  const config = fakeConfig();
  config.defaults.language = 'pt-PT';
  const page = await pageWith({ config });
  page.deliver(placeMessage('none'));
  await page.settle();
  assert.equal(page.$('locAsk').querySelector('h3').textContent, 'Show the weather?');
  page.pressKey(13);
  assert.equal(page.focused().textContent, 'United Kingdom');
  page.pressKey(13);
  assert.equal(keyFor(page, 'del').textContent, 'Delete');
});

test('the place strings are gone', () => {
  const source = fs.readFileSync(path.join(root, 'js', 'i18n.js'), 'utf8') + fs.readFileSync(path.join(root, 'js', 'main.js'), 'utf8');
  assert.ok(!source.includes('{condition} in {place}'));
  assert.ok(!source.includes('sunshine over'));
});

test('no dash characters or place names in the page scripts', () => {
  for (const f of ['main.js', 'weather.js']) {
    const text = fs.readFileSync(path.join(root, 'js', f), 'utf8');
    assert.ok(!/[\u2013\u2014]/.test(text), f + ' has an en or em dash');
  }
  assert.ok(!fs.readFileSync(path.join(root, 'js', 'main.js'), 'utf8').includes('CFG.location'));
});

// ---------- Change location, Use Windows location, the automatic pop-up closing, typed characters ----------

const here = (source, windowsOn) => Object.assign(placeMessage(source), { windowsOn });
const windowsButton = page => page.$('locAsk').querySelector('[data-action="locWindows"]');
const changeButton = page => page.$('forecast').querySelector('.panel-head [data-action="locAsk"]');

async function forecastOpen(source = 'picked', windowsOn = true) {
  const page = await pageWith();
  page.deliver(here(source, windowsOn));
  await page.settle();
  page.$('weather').click();
  assert.equal(page.$('forecast').hidden, false);
  return page;
}

test('the forecast has a Change location button before Close in its head', async () => {
  const page = await forecastOpen();
  const head = page.$('forecast').querySelector('.panel-head');
  const buttons = head.querySelectorAll('button');
  assert.deepEqual(buttons.map(b => b.dataset.action), ['locAsk', 'close']);
  assert.equal(buttons[0].textContent, 'Change location');
  assert.ok(buttons[0].classes.has('pill-btn') && buttons[0].classes.has('focusable'));
});

test('Enter on Change location closes the forecast and opens the pop-up; Back returns to the weather card', async () => {
  const page = await forecastOpen();
  const card = page.$('weather');
  enterOn(page, changeButton(page));
  assert.equal(page.$('forecast').hidden, true);
  assert.equal(page.$('locAsk').hidden, false);
  assert.equal(page.focused().dataset.action, 'locChoose');
  page.pressKey(166);
  assert.equal(page.$('locAsk').hidden, true);
  assert.equal(page.document.body.classes.has('overlay-on'), false);
  assert.equal(page.focused(), card, 'the focus is back on the weather card');
  assert.equal(page.$('forecast').hidden, true);
});

test('a click on Change location opens the pop-up too', async () => {
  const page = await forecastOpen();
  changeButton(page).click();
  assert.equal(page.$('forecast').hidden, true);
  assert.equal(page.$('locAsk').hidden, false);
});

test('Use Windows location: shown for a picked, config or off place with Windows location on, otherwise hidden', async () => {
  for (const [source, windowsOn, shown] of [
    ['picked', true, true], ['config', true, true], ['off', true, true],
    ['picked', false, false], ['config', false, false], ['off', false, false],
    ['windows', true, false], ['windows', false, false], ['lastWindows', true, false], ['none', true, false]]) {
    const page = await pageWith();
    page.deliver(here(source, windowsOn));
    await page.settle();
    if (source === 'none') {
      assert.equal(page.$('locAsk').hidden, false, 'opened by itself');
    } else if (source === 'off') {
      enterOn(page, page.$('weatherOff'));
    } else {
      page.$('weather').click();
      enterOn(page, changeButton(page));
    }
    assert.equal(page.$('locAsk').hidden, false, source);
    assert.equal(windowsButton(page).hidden, !shown, source + ' windowsOn ' + windowsOn);
  }
});

test('Enter on Use Windows location posts locationWindows and closes the pop-up', async () => {
  const page = await forecastOpen('picked', true);
  enterOn(page, changeButton(page));
  const button = windowsButton(page);
  assert.equal(button.hidden, false);
  assert.equal(button.textContent, 'Use Windows location');
  page.pressKey(39);   // the fake page lays siblings out in a row
  page.pressKey(39);
  assert.equal(page.focused(), button, 'the arrows reach it');
  page.pressKey(13);
  assert.deepEqual(page.posted.filter(m => m.type.startsWith('location')), [{ type: 'locationWindows' }]);
  assert.equal(page.$('locAsk').hidden, true);
  assert.equal(page.document.body.classes.has('overlay-on'), false);
});

test('Use Windows location after Continue without weather: the Weather off tile opens the pop-up with it', async () => {
  const page = await pageWith();
  page.deliver(here('off', true));
  await page.settle();
  enterOn(page, page.$('weatherOff'));
  enterOn(page, windowsButton(page));
  assert.deepEqual(page.posted.filter(m => m.type.startsWith('location')), [{ type: 'locationWindows' }]);
  assert.equal(page.$('locAsk').hidden, true);
});

test('a message that takes Windows location away hides the button, and the focus moves off it', async () => {
  const page = await forecastOpen('picked', true);
  enterOn(page, changeButton(page));
  page.pressKey(39);   // the fake page lays siblings out in a row
  page.pressKey(39);
  assert.equal(page.focused(), windowsButton(page));
  page.deliver(here('picked', false));
  assert.equal(windowsButton(page).hidden, true);
  assert.equal(page.focused().dataset.action, 'locChoose');
});

// An automatic pop-up (opened because the source was none) closes when a place arrives, on any of its three screens.
for (const screen of ['locAsk', 'locCountry', 'locSearch']) {
  test('a place arriving closes the automatic pop-up on ' + screen + ' and the focus goes back', async () => {
    const page = await pageWith({ fetch: placesFetch() });
    const before = page.focused();
    page.deliver(placeMessage('none'));
    await page.settle();
    if (screen !== 'locAsk') page.pressKey(13);   // Choose a place
    if (screen === 'locSearch') page.pressKey(13);   // GB
    assert.equal(page.$(screen).hidden, false);
    page.deliver(placeMessage('windows'));
    await page.settle();
    for (const id of ['locAsk', 'locCountry', 'locSearch']) assert.equal(page.$(id).hidden, true, id);
    assert.equal(page.document.body.classes.has('overlay-on'), false);
    assert.equal(page.focused(), before);
    assert.equal(page.$('weather').hidden, false);
  });
}

test('a place arriving closes the automatic pop-up: a search still on its way is dropped and it is not asked again', async () => {
  let release;
  const page = await pageWith({ fetch: placesFetch(() => new Promise(resolve => { release = resolve; })) });
  page.deliver(placeMessage('none'));
  await page.settle();
  page.pressKey(13);
  page.pressKey(13);
  type(page, 'HAL');
  page.tick(400);
  page.deliver(placeMessage('picked'));
  await page.settle();
  release(ok(HALES));
  await page.settle();
  assert.equal(page.$('locSearch').hidden, true);
  assert.equal(resultButtons(page).length, 0);
  page.deliver(placeMessage('none'));
  await page.settle();
  assert.equal(page.$('locAsk').hidden, true, 'not asked twice in one run');
});

test('a pop-up the viewer opened from the forecast stays open when a place arrives', async () => {
  const page = await forecastOpen('picked', true);
  enterOn(page, changeButton(page));
  page.deliver(Object.assign(here('picked', true), { latitude: 50, longitude: 1 }));
  await page.settle();
  assert.equal(page.$('locAsk').hidden, false);
  page.pressKey(13);   // Choose a place
  assert.equal(page.$('locCountry').hidden, false);
  page.deliver(here('windows', true));
  await page.settle();
  assert.equal(page.$('locCountry').hidden, false);
});

test('a pop-up opened from the Weather off tile stays open when a place arrives', async () => {
  const page = await pageWith();
  page.deliver(placeMessage('off'));
  await page.settle();
  enterOn(page, page.$('weatherOff'));
  page.deliver(placeMessage('config'));
  await page.settle();
  assert.equal(page.$('locAsk').hidden, false);
});

test('the automatic pop-up stays open for a message that is still none', async () => {
  const page = await pageWith();
  page.deliver(placeMessage('none'));
  await page.settle();
  page.deliver(placeMessage('none'));
  await page.settle();
  assert.equal(page.$('locAsk').hidden, false);
});

test('typed characters come from the key: a letter, space, hyphen and apostrophe', async () => {
  const page = await searchScreen();
  page.pressKey(72, 'h');
  page.pressKey(65, 'A');
  page.pressKey(32, ' ');
  page.pressKey(173, '-');
  page.pressKey(222, "'");
  assert.equal(page.$('locText').textContent, "HA -'");
});

test('on a UK layout keyCode 222 is the # key: it types nothing', async () => {
  const page = await searchScreen();
  page.pressKey(222, '#');
  page.pressKey(222, '@');
  assert.equal(page.$('locText').textContent, '');
  assert.equal(page.$('locSearch').hidden, false);
});

test('a key that is not one character falls back to its code; Backspace still deletes; Ctrl and Alt are ignored', async () => {
  const page = await searchScreen();
  page.pressKey(72, 'Unidentified');
  page.pressKey(65, 'a');
  assert.equal(page.$('locText').textContent, 'HA');
  page.pressKey(8, 'Backspace');
  assert.equal(page.$('locText').textContent, 'H');
  page.pressKey(65, 'a', { ctrlKey: true });
  page.pressKey(66, 'b', { altKey: true });
  assert.equal(page.$('locText').textContent, 'H');
  page.pressKey(53, '5');
  assert.equal(page.$('locText').textContent, 'H', 'a digit types nothing');
});

// Open-Meteo's forecast answered 503 "The service is overloaded" for every place on 2026-11-02 (owner: hide the weather
// rather than show "Weather unavailable"). With nothing saved for the place, the card and the picture screen's line go;
// the Weather off tile does not come, since nobody switched the weather off.
const failing = (status = 503) => () => Promise.resolve({ ok: false, status, json: () => Promise.resolve({ error: true }) });

test('a failed forecast with nothing saved hides the card, and shows no Weather off tile', async () => {
  const page = await pageWith({ fetch: failing() });
  page.deliver(placeMessage('config'));
  await page.settle();
  assert.equal(page.$('weather').hidden, true);
  assert.equal(page.$('weatherOff').hidden, true);
  page.fire(page.document.querySelector('[data-action="ambient"]'), 'click');
  assert.equal(page.$('ambWx').innerHTML, '');
});

test('the card comes back when the forecast answers', async () => {
  let answer = failing();
  const page = await pageWith({ fetch: url => answer(url) });
  page.deliver(placeMessage('config'));
  await page.settle();
  assert.equal(page.$('weather').hidden, true);
  answer = recordingFetch();
  page.deliver(Object.assign(placeMessage('config'), { latitude: 30, longitude: 40 }));
  await page.settle();
  assert.equal(page.$('weather').hidden, false);
  assert.equal(page.$('wxTemp').textContent, '12°');
});

test('a card in focus that goes away hands the focus to the first tile', async () => {
  const page = await pageWith({ fetch: failing() });
  page.fire(page.$('weather'), 'mouseover');
  assert.equal(page.focused(), page.$('weather'));
  page.deliver(placeMessage('config'));
  await page.settle();
  assert.notEqual(page.focused(), page.$('weather'));
  assert.ok(page.focused().offsetParent !== null);
});

// Owner, 2026-11-02: no "Take a break. You have earned it." In the afternoon with no weather the sentence is empty.
test('an afternoon with no weather has no sentence', async () => {
  const page = await pageWith({ now: new Date(2026, 10, 2, 15, 0, 0).getTime() });
  assert.equal(page.$('sub').textContent, '');
});

// ---------- Turn on Windows location ----------

const TURN_ON_LINE = 'Opens Windows settings: turn on Location services and Let apps access your location, then press Home.';
const turnOnButton = page => page.$('locAsk').querySelector('[data-action="locTurnOn"]');
const noteLine = page => page.$('locAsk').querySelector('#locNote');
const canTurnOn = (source, on) => Object.assign(here(source, !on), { canTurnOn: on });
const turnOnPosts = page => page.posted.filter(m => m.type === 'locationTurnOn');

test('Turn on Windows location: the first button of the pop-up, with a smaller second line, hidden until a message says it can help', async () => {
  const page = await pageWith();
  const button = turnOnButton(page);
  assert.ok(button, 'the button is in the pop-up');
  assert.ok(button.classes.has('pill-btn') && button.classes.has('focusable'));
  assert.equal(page.$('locAsk').querySelectorAll('button')[0], button, 'first in the pop-up');
  assert.equal(button.hidden, true, 'hidden before any location message');
  assert.equal(button.querySelector('span').textContent, 'Turn on Windows location');
  assert.equal(button.querySelector('small').textContent, TURN_ON_LINE);

  for (const [flag, shown] of [[true, true], [false, false], [true, true]]) {
    page.deliver(canTurnOn('off', flag));
    await page.settle();
    assert.equal(button.hidden, !shown, 'canTurnOn ' + flag);
  }
});

test('with the button shown the pop-up opens on it, and without it on Choose a place', async () => {
  const page = await pageWith();
  page.deliver(canTurnOn('none', true));
  await page.settle();
  assert.equal(page.$('locAsk').hidden, false);
  assert.equal(page.focused(), turnOnButton(page));
  page.pressKey(166);

  const other = await pageWith();
  other.deliver(canTurnOn('none', false));
  await other.settle();
  assert.equal(other.focused().dataset.action, 'locChoose', 'never the hidden button');
});

test('Enter on Turn on Windows location posts locationTurnOn; so does a click; the pop-up stays until the host says', async () => {
  const page = await pageWith();
  page.deliver(canTurnOn('off', true));
  await page.settle();
  enterOn(page, page.$('weatherOff'));
  assert.equal(page.focused(), turnOnButton(page));
  page.pressKey(13);
  assert.deepEqual(turnOnPosts(page), [{ type: 'locationTurnOn' }]);
  assert.equal(page.$('locAsk').hidden, false);
  turnOnButton(page).click();
  assert.equal(turnOnPosts(page).length, 2);
  assert.deepEqual(page.posted.filter(m => m.type.startsWith('location') && m.type !== 'locationTurnOn'), []);
});

test('a message that says Windows location is on hides the button, and the focus moves to the next button', async () => {
  const page = await pageWith();
  page.deliver(canTurnOn('off', true));
  await page.settle();
  enterOn(page, page.$('weatherOff'));
  assert.equal(page.focused(), turnOnButton(page));
  page.deliver(canTurnOn('off', false));
  assert.equal(turnOnButton(page).hidden, true);
  assert.equal(page.focused().dataset.action, 'locChoose');
});

test('locationTurnOn off after Home opens the pop-up with the note; Back closes it and the next opening has no note', async () => {
  const page = await pageWith();
  page.deliver(canTurnOn('picked', true));
  await page.settle();
  page.$('weather').click();
  enterOn(page, changeButton(page));
  page.pressKey(13);   // Turn on Windows location
  page.deliver({ type: 'home' });
  assert.equal(page.$('locAsk').hidden, true, 'Home closed the pop-up');
  page.deliver(canTurnOn('picked', true));
  page.deliver({ type: 'locationTurnOn', on: false });
  assert.equal(page.$('locAsk').hidden, false);
  assert.equal(noteLine(page).hidden, false);
  assert.equal(noteLine(page).textContent, 'Windows location is still off.');
  assert.equal(page.focused(), turnOnButton(page));
  page.pressKey(166);
  assert.equal(page.$('locAsk').hidden, true);
  assert.equal(page.document.body.classes.has('overlay-on'), false);

  page.$('weather').click();
  enterOn(page, changeButton(page));
  assert.equal(page.$('locAsk').hidden, false);
  assert.equal(noteLine(page).hidden, true, 'opened from the forecast: no note');
});

test('the note is not shown when the pop-up opens from the Weather off tile, even after an earlier note', async () => {
  const page = await pageWith();
  page.deliver(canTurnOn('off', true));
  await page.settle();
  assert.equal(noteLine(page).hidden, true);
  page.deliver({ type: 'home' });
  page.deliver({ type: 'locationTurnOn', on: false });
  assert.equal(noteLine(page).hidden, false);
  page.pressKey(166);
  enterOn(page, page.$('weatherOff'));
  assert.equal(page.$('locAsk').hidden, false);
  assert.equal(noteLine(page).hidden, true);
});

test('locationTurnOn on opens nothing', async () => {
  const page = await pageWith();
  page.deliver(canTurnOn('picked', true));
  await page.settle();
  page.deliver({ type: 'home' });
  page.deliver({ type: 'locationTurnOn', on: true });
  assert.equal(page.$('locAsk').hidden, true);
  assert.equal(noteLine(page).hidden, true);
  assert.equal(page.document.body.classes.has('overlay-on'), false);
});

test('locationTurnOn off does not open the pop-up over another overlay or an app', async () => {
  const page = await pageWith();
  page.deliver(canTurnOn('picked', true));
  await page.settle();
  page.$('weather').click();
  page.deliver({ type: 'locationTurnOn', on: false });
  assert.equal(page.$('locAsk').hidden, true, 'not over the forecast');
  assert.equal(page.$('forecast').hidden, false);
  page.pressKey(166);
  assert.equal(page.$('locAsk').hidden, true, 'and it does not wait for the forecast to close');

  page.document.hidden = true;
  page.deliver({ type: 'locationTurnOn', on: false });
  assert.equal(page.$('locAsk').hidden, true, 'never over an app');
  page.document.hidden = false;
  page.fire(page.document, 'visibilitychange');
  await page.settle();
  assert.equal(page.$('locAsk').hidden, true);
});

test('locationTurnOn off while the pop-up is already open (the automatic one) adds the note to it', async () => {
  const page = await pageWith();
  page.deliver(canTurnOn('none', true));
  await page.settle();
  assert.equal(page.$('locAsk').hidden, false);
  page.deliver({ type: 'home' });   // the viewer comes back: the automatic pop-up is over the home screen again
  page.deliver(canTurnOn('none', true));
  page.deliver({ type: 'locationTurnOn', on: false });
  assert.equal(page.$('locAsk').hidden, false);
  assert.equal(noteLine(page).hidden, false);
  assert.equal(page.focused(), turnOnButton(page));
});

