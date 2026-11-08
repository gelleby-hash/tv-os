const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const path = require('node:path');
function load() {
  const ctx = vm.createContext({ window: {} });
  for (const name of ['i18n.js', 'weather.js']) vm.runInContext(fs.readFileSync(path.join(__dirname, '../js', name), 'utf8'), ctx);
  return ctx.window;
}
test('the page strings are English: text gives the string back with its placeholders filled', () => {
  const { TVI18n: lang, TVWeather: weather } = load();
  assert.deepEqual(Object.keys(lang), ['text']);
  assert.equal(lang.text('Humidity'), 'Humidity');
  assert.equal(lang.text('{count} apps', { count: 5 }), '5 apps');
  assert.equal(lang.text('{name} is not connected', { name: 'Game console' }), 'Game console is not connected');
  assert.equal(lang.text('Sunrise at {time}'), 'Sunrise at {time}', 'a missing value leaves its placeholder');
  assert.equal(weather.describe(63).label, 'Rain');
});
