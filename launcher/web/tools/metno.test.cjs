// Tests for MET Norway's forecast as a second weather source: its symbols, sunrise and sunset, and the normaliser.
'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const { root } = require('./fake-page.cjs');

function loadWeather() {
  const window = {};
  const ctx = vm.createContext({ window, Math, Date, Number, String, Array, Object, JSON, isNaN });
  vm.runInContext(fs.readFileSync(path.join(root, 'js', 'weather.js'), 'utf8'), ctx, { filename: 'weather.js' });
  return window.TVWeather;
}

const fixture = () => JSON.parse(fs.readFileSync(path.join(__dirname, 'fixtures', 'metno-london.json'), 'utf8'));
const symbols = () => JSON.parse(fs.readFileSync(path.join(__dirname, 'fixtures', 'metno-symbols.json'), 'utf8'));
const LONDON = { latitude: 51.51, longitude: -0.13 };
const FETCHED = Date.parse('2026-11-21T15:58:00Z');

test('metSymbol follows the rules', () => {
  const W = loadWeather();
  const m = W.metSymbol;
  assert.deepEqual({ ...m('lightrainshowersandthunder_night') }, { label: 'Thunderstorm', kind: 'storm', intensity: 2, isDay: false });
  assert.deepEqual({ ...m('heavysnow_day') }, { label: 'Heavy snow', kind: 'snow', intensity: 3, isDay: true });
  assert.deepEqual({ ...m('lightsleet_day') }, { label: 'Sleet', kind: 'snow', intensity: 1, isDay: true });
  assert.deepEqual({ ...m('rainshowers_day') }, { label: 'Showers', kind: 'rain', intensity: 2, isDay: true });
  assert.deepEqual({ ...m('heavyrainshowers_day') }, { label: 'Heavy showers', kind: 'rain', intensity: 3, isDay: true });
  assert.equal(m('fog').kind, 'fog');
  assert.equal(m('fog').intensity, 0);
  assert.equal(m('partlycloudy_polartwilight').kind, 'partly');
  assert.equal(m('partlycloudy_polartwilight').isDay, true);
  assert.equal(m('fair_night').kind, 'partly');
  assert.equal(m('fair_night').label, 'Mainly clear');
  assert.equal(m('clearsky_day').kind, 'clear');
  assert.equal(m('cloudy').kind, 'cloudy');
  assert.equal(m('cloudy').label, 'Cloudy');
  assert.equal(m('nonsense').kind, 'cloudy');
  assert.equal(m('nonsense').label, 'Unknown');
});

test("every symbol in MET Norway's list maps to a known kind", () => {
  const W = loadWeather();
  const list = symbols();
  assert.ok(list.length >= 40);
  for (const name of list) {
    for (const suffix of ['', '_day', '_night', '_polartwilight']) {
      const m = W.metSymbol(name + suffix);
      assert.notEqual(m.label, 'Unknown', name + suffix);
    }
  }
});

test('describe takes a MET Norway symbol name', () => {
  const W = loadWeather();
  assert.deepEqual({ ...W.describe('heavyrain') }, { label: 'Heavy rain', kind: 'rain', intensity: 3 });
  assert.equal(W.describe(0).label, 'Clear');
});

test('sunTimes for London on 2026-06-21 is about 04:43 and 21:21 local', () => {
  const W = loadWeather();
  const s = W.sunTimes(new Date(2026, 5, 21, 12), 51.51, -0.13);
  // BST is UTC+1, so 04:43 and 21:21 local are 03:43 and 20:21 UTC.
  const minutes = d => d.getUTCHours() * 60 + d.getUTCMinutes();
  assert.ok(Math.abs(minutes(s.sunrise) - (3 * 60 + 43)) <= 3, s.sunrise.toISOString());
  assert.ok(Math.abs(minutes(s.sunset) - (20 * 60 + 21)) <= 3, s.sunset.toISOString());
});

test('sunTimes in the polar night and the midnight sun', () => {
  const W = loadWeather();
  const night = W.sunTimes(new Date(2026, 11, 21, 12), 78, 15);
  assert.equal(night.sunrise.getTime(), night.sunset.getTime());
  const day = W.sunTimes(new Date(2026, 5, 21, 12), 78, 15);
  assert.ok(day.sunset - day.sunrise >= 23.9 * 3600e3);
});

test('fromMetNorway on the fixture', () => {
  const W = loadWeather();
  const raw = fixture();
  const w = W.fromMetNorway(raw, FETCHED, LONDON);
  const first = raw.properties.timeseries[0].data.instant.details;
  assert.equal(w.source, 'metno');
  assert.equal(w.fetchedAt, FETCHED);
  assert.equal(w.current.temp, first.air_temperature);
  assert.equal(w.current.feels, first.apparent_air_temperature);
  assert.ok(Math.abs(w.current.wind - first.wind_speed * 3.6) < 1e-9);
  assert.ok(Math.abs(w.current.cloud - first.cloud_area_fraction / 100) < 1e-9);
  assert.equal(w.current.windDir, first.wind_from_direction);
  assert.equal(w.current.dew, first.dew_point_temperature);
  assert.equal(w.current.uv, first.ultraviolet_index_clear_sky);
  assert.equal(w.current.visibility, null);
  assert.equal(w.current.gusts, null);
  assert.equal(w.current.code, 'fair_day');
  assert.equal(w.current.isDay, true);
  assert.ok(w.daily.length >= 7);
  for (const d of w.daily) {
    assert.ok(d.max >= d.min, d.date + ' ' + d.max + ' ' + d.min);
    assert.equal(d.pop, null);
    assert.ok(d.sunrise < d.sunset);
    assert.equal(typeof d.code, 'string');
  }
  assert.ok(w.daily.some(d => typeof d.rain === 'number'));
});

test('fromMetNorway hourly keeps the next 24 hours from now', () => {
  const W = loadWeather();
  const w = W.fromMetNorway(fixture(), FETCHED, LONDON);
  assert.ok(w.hourly.length <= 24);
  for (const h of w.hourly) assert.ok(h.time.getTime() + 3600e3 > Date.now());
});

test('fromMetNorway with steps lacking next_1_hours and fields missing does not throw', () => {
  const W = loadWeather();
  const raw = fixture();
  const series = raw.properties.timeseries;
  for (const e of series) delete e.data.next_1_hours;
  delete series[0].data.instant.details.air_temperature;
  delete series[1].data.next_6_hours;
  delete series[2].data.next_12_hours;
  series[3].data = {};
  const w = W.fromMetNorway(raw, FETCHED, LONDON);
  assert.equal(w.current.temp, null);
  assert.ok(w.daily.length >= 1);
  assert.doesNotThrow(() => W.fromMetNorway({}, FETCHED, LONDON));
});

test('gusts and rain chance are used when the answer has them', () => {
  const W = loadWeather();
  const raw = fixture();
  const e = raw.properties.timeseries[0].data;
  e.instant.details.wind_speed_of_gust = 10;
  e.next_1_hours.details.probability_of_precipitation = 40;
  const w = W.fromMetNorway(raw, FETCHED, LONDON);
  assert.ok(Math.abs(w.current.gusts - 36) < 1e-9);
  assert.equal(w.daily[0].pop, 40);
});

test('an entry at 23:30 UTC on a BST date belongs to the next local day', () => {
  const W = loadWeather();
  // 23:30 UTC on 2026-06-20; the machine's own offset decides which local day that is.
  const t = new Date(Date.UTC(2026, 5, 20, 23, 30));
  const raw = { properties: { timeseries: [{ time: t.toISOString(), data: { instant: { details: { air_temperature: 9 } },
    next_1_hours: { summary: { symbol_code: 'cloudy' }, details: { precipitation_amount: 1.5 } } } }] } };
  const w = W.fromMetNorway(raw, t.getTime(), LONDON);
  const local = new Date(t.getFullYear(), t.getMonth(), t.getDate());
  assert.equal(w.daily.length, 1);
  assert.equal(w.daily[0].date.getTime(), local.getTime());
  assert.equal(w.daily[0].rain, 1.5);
});
