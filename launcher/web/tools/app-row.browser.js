// Browser integration fixture; injected before main.js by check-app-row.py.
window.__rowErrors = [];
window.addEventListener('error', function (e) { window.__rowErrors.push(e.message); });
var count = Number(new URLSearchParams(location.search).get('count'));
var style = document.createElement('style');
style.textContent = '.grid,.tile,.landscape-layer{transition:none!important}';
document.head.appendChild(style);
var now = new Date();
var days = Array.from({ length: 7 }, function (_, i) {
  var d = new Date(now); d.setDate(d.getDate() + i);
  var rise = new Date(d), set = new Date(d); rise.setHours(7, 30); set.setHours(18, 45);
  return { date: d, code: 3, min: 9, max: 16, sunrise: rise, sunset: set, pop: 10 };
});
var fixture = { fetchedAt: Date.now(), current: { temp: 13, feels: 12, humidity: 87, wind: 5, code: 3, isDay: false, cloud: 1 }, daily: days, hourly: [] };
TVWeather.cached = function () { return fixture; };
TVWeather.fetch = function () { return Promise.resolve(fixture); };
var apps = TV_HOME_CONFIG.apps.filter(function (a) { return a.id !== 'input.hdmi2'; });
while (apps.length < count) apps.push({ id: 'extra.' + apps.length, name: 'Additional app ' + apps.length });
apps = apps.slice(0, count);
TVApps.list = function () { return Promise.resolve(apps); };
var launched = [];
TVApps.launch = function (id) { launched.push(id); return Promise.resolve(); };
TVApps.exit = function () {};
localStorage.setItem('tvhome.settings.v1', JSON.stringify({ language: 'pt-PT', unit: 'C', clock: '24', scenery: 'night', effects: false, hidden: [] }));
window.addEventListener('load', function () { setTimeout(async function () {
  var failures = [], checks = 0;
  function check(ok, label) { checks++; if (!ok) failures.push(label); }
  function key(n) { document.dispatchEvent(new KeyboardEvent('keydown', { keyCode: n, bubbles: true })); }
  function tiles() { return Array.from(document.querySelectorAll('#grid .tile')); }
  function focused() { return document.querySelector('#home .focused'); }
  function point(el) { el.dispatchEvent(new MouseEvent('mouseover', { bubbles: true })); }
  function open(id) { document.querySelector('#home [data-action="' + id + '"]').click(); }
  function visible(el) {
    var r = el.getBoundingClientRect(), v = document.querySelector('.grid-viewport').getBoundingClientRect();
    return r.left - 5 >= v.left && r.right + 5 <= v.right && r.top - 5 >= v.top && r.bottom + 5 <= v.bottom;
  }
  check(!document.querySelector('.apps-head') && !document.getElementById('pager'), 'heading and count removed');
  check(tiles().length === count, 'initial app count');
  if (count) {
    var list = tiles();
    var tops = list.map(function (el) { return el.offsetTop; });
    check(tops.every(function (top) { return top === tops[0]; }), 'single horizontal row');
    var stage = document.getElementById('stage').getBoundingClientRect();
    check(list[0].getBoundingClientRect().top > stage.top + stage.height * 0.74, 'row near screen bottom');
    check(visible(focused()), 'initial focus has margin');
    for (var i = 0; i < count * 2 + 1; i++) {
      key(39); check(focused() === list[(i + 1) % count], 'right navigation ' + i);
      check(visible(focused()), 'right focus fully visible ' + i);
    }
    point(list[0]); key(37);
    check(focused() === list[count - 1], 'left from first wraps to last');
    check(visible(focused()), 'last app fully visible');
    key(13); check(launched[0] === list[count - 1].dataset.app, 'OK launches wrapped app');
    key(39); check(focused() === list[0], 'right from last wraps to first');
    key(37); key(27); check(focused() === list[0] && visible(focused()), 'Back returns to first app');
    key(38); check(focused() && !focused().classList.contains('tile'), 'Up reaches controls');
    for (var step = 0; step < 3 && !focused().classList.contains('tile'); step++) key(40);
    check(focused() && focused().classList.contains('tile') && visible(focused()), 'Down returns to visible app');
    point(list[count - 1]);
    var selectedId = focused().dataset.app;
    document.dispatchEvent(new Event('visibilitychange')); await new Promise(function (resolve) { setTimeout(resolve, 0); });
    check(focused().dataset.app === selectedId && visible(focused()), 'app refresh preserves selected app');
    open('forecast'); key(27); check(focused() && focused().dataset.action === 'forecast', 'forecast closes back to its trigger');
    for (var step = 0; step < 3 && !focused().classList.contains('tile'); step++) key(40);
    check(focused() && focused().classList.contains('tile') && visible(focused()), 'row reachable after forecast');
  }
  open('settings');
  document.querySelector('[data-pref="language"]').click();
  check(document.documentElement.lang === 'en-GB', 'language switching works without pager');
  if (count) {
    Array.from(document.querySelectorAll('[data-toggle]')).forEach(function (el) { el.click(); });
    check(tiles().length === 0, 'all apps can be hidden');
    check(document.querySelector('#grid .empty').textContent.includes('No apps'), 'empty state shown');
  }
  key(27); key(39); key(38); key(40);
  check(focused() && document.contains(focused()), 'empty row retains usable controls');
  open('settings');
  Array.from(document.querySelectorAll('[data-toggle]')).forEach(function (el) { el.click(); });
  document.querySelector('[data-pref="language"]').click(); key(27);
  if (count) { point(tiles()[0]); check(visible(focused()), 'restored row has focus margin'); }
  var report = document.createElement('script'); report.id = 'row-check-result'; report.type = 'application/json';
  report.textContent = JSON.stringify({ count: count, checks: checks, failures: failures, errors: window.__rowErrors });
  document.body.appendChild(report);
}, 1500); });
