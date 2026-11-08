// Tests for TVPlaces: the country list (named by the browser, the region's country first) and the town search on
// Open-Meteo's geocoding service. Runs places.js in a vm context with a fake fetch.
'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

const SOURCE = fs.readFileSync(path.join(__dirname, '..', 'js', 'places.js'), 'utf8');

// Open-Meteo's answer for "Hales" in GB, as read live on 2026-11-02 (the fields the page uses).
const HALES = {
  results: [
    { name: 'Hales', latitude: 52.52225, longitude: 1.50869, country_code: 'GB', admin1: 'England', admin2: 'Norfolk' },
    { name: 'Hales', latitude: 52.90232, longitude: -2.42784, country_code: 'GB', admin1: 'England', admin2: 'Staffordshire' },
    { name: 'Halesworth', latitude: 52.34312, longitude: 1.50391, country_code: 'GB', admin1: 'England', admin2: 'Suffolk' }
  ],
  generationtime_ms: 0.5
};

function load(fetch, lang = 'en-GB') {
  const window = {};
  const urls = [];
  const recording = fetch && (url => { urls.push(url); return fetch(url); });
  vm.runInContext(SOURCE, vm.createContext({ window, document: { documentElement: { lang } }, fetch: recording }));
  window.TVPlaces.urls = urls;
  return window.TVPlaces;
}
const plain = x => JSON.parse(JSON.stringify(x));
const answer = (body, status = 200) => () => Promise.resolve({ ok: status >= 200 && status < 300, status, json: () => Promise.resolve(body) });

test('countries: the region\'s country comes first', () => {
  const P = load();
  assert.deepEqual(JSON.parse(JSON.stringify(P.countries('en-GB', 'GB')[0])), { code: 'GB', name: 'United Kingdom' });
});

test('countries: sorted by name, every entry named, no region given', () => {
  const list = load().countries('en-GB', null);
  assert.ok(list.length > 200, 'about 249 countries, got ' + list.length);
  for (let i = 1; i < list.length; i++) assert.ok(list[i - 1].name.localeCompare(list[i].name, 'en-GB') <= 0, list[i - 1].name + ' before ' + list[i].name);
  for (const c of list) assert.notEqual(c.name, c.code, c.code + ' has no name');
  assert.ok(list.some(c => c.code === 'PT' && c.name === 'Portugal'));
});

test('countries: named in the page language, an unknown region changes nothing', () => {
  const P = load();
  assert.equal(P.countries('pt-PT', 'GB')[0].name, 'Reino Unido');
  assert.equal(P.countries('en-GB', 'ZZ')[0].code, P.countries('en-GB', null)[0].code);
});

test('search: under 3 letters gives no results and makes no request', async () => {
  const P = load(answer(HALES));
  assert.deepEqual(plain(await P.search('Ha', 'GB')), []);
  assert.deepEqual(plain(await P.search('  Ha  ', 'GB')), []);
  assert.deepEqual(P.urls, []);
});

test('search: asks Open-Meteo for the text in the country, and gives each town its area and rounded coordinates', async () => {
  const P = load(answer(HALES));
  const results = JSON.parse(JSON.stringify(await P.search('Hales', 'GB')));
  assert.deepEqual(P.urls, ['https://geocoding-api.open-meteo.com/v1/search?name=Hales&countryCode=GB&count=8&language=en&format=json']);
  assert.deepEqual(results, [
    { name: 'Hales', area: 'Norfolk, England', latitude: 52.52, longitude: 1.51 },
    { name: 'Hales', area: 'Staffordshire, England', latitude: 52.9, longitude: -2.43 },
    { name: 'Halesworth', area: 'Suffolk, England', latitude: 52.34, longitude: 1.5 }
  ]);
});

test('search: the text is encoded, the language is the page\'s, and an area is not repeated', async () => {
  const P = load(answer({ results: [{ name: 'Lisboa', latitude: 38.71667, longitude: -9.13333, admin1: 'Lisboa', admin2: 'Lisboa' },
    { name: 'Odivelas', latitude: 38.8, longitude: -9.18, admin1: 'Lisboa' }] }), 'pt-PT');
  const results = await P.search("SAO JOAO D'ARCO", 'PT');
  assert.equal(P.urls[0], 'https://geocoding-api.open-meteo.com/v1/search?name=SAO%20JOAO%20D\'ARCO&countryCode=PT&count=8&language=pt&format=json');
  assert.equal(results[0].area, 'Lisboa');
  assert.equal(results[1].area, 'Lisboa');
});

test('search: an answer with no results key gives no results', async () => {
  const P = load(answer({ generationtime_ms: 0.3 }));
  assert.deepEqual(plain(await P.search('Zzzqx', 'GB')), []);
});

test('search: a 500 rejects, and so does a network failure', async () => {
  await assert.rejects(load(answer({ error: true }, 500)).search('Hales', 'GB'));
  await assert.rejects(load(() => Promise.reject(new Error('offline'))).search('Hales', 'GB'));
});

test('places.js has no dash characters', () => {
  assert.ok(!/[\u2013\u2014]/.test(SOURCE));
});
