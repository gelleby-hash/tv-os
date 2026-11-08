const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { loadPage, fakeConfig, root } = require('./fake-page.cjs');

const ENTER = 13, ESC = 27, LEFT = 37, UP = 38, RIGHT = 39, DOWN = 40;

// Everything the page sent to the host except the load-time 'ready' and 'hdr' state.
// The page's actions: not its state reports (ready, hdr) or its key log (key, pointer; keylog.test.cjs).
const sent = page => page.posted.filter(m => !['ready', 'hdr', 'key', 'pointer'].includes(m.type));
const button = (page, action) => page.document.querySelector('[data-action="' + action + '"]');
const tiles = page => Array.from(page.document.querySelectorAll('#grid .tile'));

async function ready(options) {
  const page = loadPage(options);
  await page.settle();
  return page;
}

test('index.html loads host.js before apps.js and main.js, and no longer loads config.js', () => {
  const { scripts } = loadPage();
  assert.ok(scripts.includes('js/host.js'));
  assert.ok(scripts.indexOf('js/host.js') < scripts.indexOf('js/apps.js'));
  assert.ok(scripts.indexOf('js/apps.js') < scripts.indexOf('js/main.js'));
  assert.ok(!scripts.includes('js/config.js'));
  assert.ok(scripts.includes('js/moon.js'));
  assert.ok(scripts.indexOf('js/moon.js') < scripts.indexOf('js/icons.js'));
  assert.ok(!fs.existsSync(path.join(root, 'js', 'config.js')));
});

test('settings button is gone', async () => {
  const page = await ready();
  assert.ok(!/data-action="settings"/.test(page.html));
  assert.ok(!/id="settings"/.test(page.html));
  assert.equal(button(page, 'settings'), null);
  for (const kept of ['apps', 'ambient', 'desktop', 'restart', 'shutdown']) {
    assert.ok(button(page, kept), kept + ' button present');
  }
  assert.ok(page.$('forecast') && page.$('ambient'));
});

test('the top row has no home or forecast button; the weather card opens the forecast', async () => {
  const page = await ready();
  const row = page.document.querySelectorAll('.actions .icon-btn').map(b => b.dataset.action);
  assert.deepEqual(row, ['apps', 'ambient', 'boost', 'desktop', 'restart', 'shutdown']);
  page.$('weather').click();
  assert.equal(page.$('forecast').hidden, false, 'the forecast is open');
});

test('every icon named in index.html exists in icons.js', async () => {
  const page = await ready();
  const names = page.document.querySelectorAll('[data-icon]').map(el => el.dataset.icon);
  assert.ok(names.includes('desktop') && names.includes('restart') && names.includes('power'));
  for (const name of names) assert.ok(String(page.window.TVIcons.ui[name]).startsWith('<svg'), name);
});

test('page sends ready once the host can talk to it', async () => {
  const page = await ready();
  assert.deepEqual(page.posted.filter(m => m.type === 'ready'), [{ type: 'ready' }]);
});

test('host messages reach handlers by type; unknown and malformed ones are ignored', async () => {
  const page = await ready();
  const got = [];
  page.window.TVHost.on('backgrounds', msg => got.push(page.plain(msg)));
  page.deliver({ type: 'backgrounds', urls: ['a.jpg'] });
  page.deliver({ type: 'nobody-listens' });
  page.deliver({ type: 'constructor' });
  page.deliver(null);
  page.deliver({ urls: [] });
  page.deliver('{"type":"backgrounds","urls":["b.jpg"]}');
  assert.deepEqual(got, [{ type: 'backgrounds', urls: ['a.jpg'] }, { type: 'backgrounds', urls: ['b.jpg'] }]);
});

test('a tile opens its app through the host', async () => {
  const page = await ready();
  tiles(page)[1].click();
  assert.deepEqual(sent(page), [{ type: 'launch', id: 'disney' }]);
});

test('desktop posts immediately', async () => {
  const page = await ready();
  button(page, 'desktop').click();
  assert.deepEqual(sent(page), [{ type: 'desktop' }]);
  assert.equal(page.$('confirm').hidden, true);
});

test('restart and shutdown post only after confirming', async () => {
  const page = await ready();

  button(page, 'shutdown').click();
  assert.equal(page.$('confirm').hidden, false);
  assert.equal(page.$('confirmText').textContent, 'Shut down the TV box?');
  assert.equal(page.focused().textContent, 'No', 'No is focused by default');
  assert.deepEqual(sent(page), [], 'nothing sent while the dialog is open');
  page.pressKey(ENTER);
  assert.deepEqual(sent(page), [], 'OK on No sends nothing');
  assert.equal(page.$('confirm').hidden, true);
  assert.equal(page.focused(), button(page, 'shutdown'), 'focus returns to the button');

  button(page, 'shutdown').click();
  page.pressKey(RIGHT);
  assert.equal(page.focused().textContent, 'Yes');
  page.pressKey(ENTER);
  assert.deepEqual(sent(page), [{ type: 'shutdown' }]);
  assert.equal(page.$('confirm').hidden, true);

  button(page, 'restart').click();
  assert.equal(page.$('confirmText').textContent, 'Restart the TV box?');
  assert.equal(page.focused().textContent, 'No');
  page.pressKey(RIGHT);
  page.pressKey(LEFT);
  assert.equal(page.focused().textContent, 'No', 'arrows move between No and Yes');
  page.pressKey(RIGHT);
  page.pressKey(ENTER);
  assert.deepEqual(sent(page), [{ type: 'shutdown' }, { type: 'restart' }]);
});

test('Back closes the confirm dialog without sending anything', async () => {
  const page = await ready();
  button(page, 'restart').click();
  page.pressKey(ESC);
  assert.equal(page.$('confirm').hidden, true);
  assert.deepEqual(sent(page), []);
});

test('back on the home screen does nothing', async () => {
  const page = await ready();
  let exits = 0;
  page.window.TVApps.exit = () => { exits++; };
  const before = page.focused();
  for (const code of [ESC, 8, 10009, 166]) page.pressKey(code);
  assert.deepEqual(sent(page), []);
  assert.equal(exits, 0);
  assert.equal(page.focused(), before);
});

test('back closes an open overlay', async () => {
  const page = await ready();
  button(page, 'forecast').click();
  assert.equal(page.$('forecast').hidden, false);
  page.pressKey(ESC);
  assert.equal(page.$('forecast').hidden, true);
});

test('the remote Back key (VK 166, Browser Back) closes an open overlay', async () => {
  const page = await ready();
  button(page, 'forecast').click();
  assert.equal(page.$('forecast').hidden, false);
  page.pressKey(166);
  assert.equal(page.$('forecast').hidden, true);
});

test('home message closes overlays and focuses the first tile', async () => {
  const page = await ready();
  tiles(page)[1].click();
  button(page, 'forecast').click();
  assert.equal(page.$('forecast').hidden, false);
  page.window.TVApps.exit = () => assert.fail('home must not exit');

  page.deliver({ type: 'home' });
  assert.equal(page.$('forecast').hidden, true);
  assert.equal(page.focused().dataset.app, 'youtube');
  assert.equal(sent(page).filter(m => m.type === 'homeShown').length, 0, 'not before the next frame');
  page.flushFrames();
  const shown = sent(page).filter(m => m.type === 'homeShown');
  assert.equal(shown.length, 1);
  assert.equal(typeof shown[0].at, 'number');
});

test('home message also leaves the confirm dialog and ambient mode', async () => {
  const page = await ready();
  button(page, 'shutdown').click();
  page.deliver({ type: 'home' });
  assert.equal(page.$('confirm').hidden, true);
  button(page, 'ambient').click();
  assert.equal(page.$('ambient').hidden, false);
  page.deliver({ type: 'home' });
  assert.equal(page.$('ambient').hidden, true);
  assert.ok(!page.document.body.classList.contains('ambient-on'));
  assert.deepEqual(sent(page), []);
});

const toastText = page => page.$('toast').textContent;

test('boost button posts toggleBoost', async () => {
  const page = await ready();
  const boost = button(page, 'boost');
  assert.ok(boost, 'boost button present');
  assert.equal(boost.dataset.icon, 'volume');
  const row = page.document.querySelectorAll('.actions .icon-btn').map(b => b.dataset.action);
  assert.ok(row.indexOf('boost') >= 0 && row.indexOf('boost') < row.indexOf('desktop'), 'before the desktop button');
  boost.click();
  assert.deepEqual(sent(page), [{ type: 'toggleBoost' }]);
  assert.equal(page.$('confirm').hidden, true, 'no overlay');
});

test('boost message sets state, icon, light and label', async () => {
  const page = await ready();
  const boost = button(page, 'boost');
  page.deliver({ type: 'boost', on: true });
  assert.ok(boost.classList.contains('on'));
  assert.equal(boost.dataset.icon, 'volumeBoost');
  assert.ok(boost.innerHTML.startsWith(page.window.TVIcons.ui.volumeBoost), 'the icon with two waves');
  assert.ok(boost.innerHTML.includes('<span class="led"></span>'), 'the light (CSS lights it while on)');
  assert.ok(boost.innerHTML.includes('>Boost Disney+: on</span>'), 'the label under the button');
  assert.equal(boost.getAttribute('aria-label'), 'Boost Disney+: on');
  page.deliver({ type: 'boost', on: false });
  assert.ok(!boost.classList.contains('on'));
  assert.equal(boost.dataset.icon, 'volume');
  assert.ok(boost.innerHTML.startsWith(page.window.TVIcons.ui.volume));
  assert.ok(boost.innerHTML.includes('>Boost Disney+: off</span>'));
  assert.equal(boost.getAttribute('aria-label'), 'Boost Disney+: off');
});

const appOf = (id, name, shown = true) =>
  ({ id, name, icon: 'icons/' + id + '.svg', shown, version: 'default', versions: [{ id: 'default', name }] });
const tileIds = page => tiles(page).map(t => t.dataset.app);

test("the home row follows the host's apps message: its order, and a hidden app gone", async () => {
  const page = await ready();
  assert.deepEqual(tileIds(page), ['youtube', 'disney']);
  page.deliver({ type: 'apps', apps: [appOf('disney', 'Disney+'), appOf('channel4', 'Channel 4'), appOf('youtube', 'YouTube', false)], boostApps: ['Disney+'] });
  await page.settle();
  assert.deepEqual(tileIds(page), ['disney', 'channel4']);
  page.deliver({ type: 'apps', apps: [appOf('youtube', 'YouTube'), appOf('disney', 'Disney+')], boostApps: ['Disney+'] });
  await page.settle();
  assert.deepEqual(tileIds(page), ['youtube', 'disney']);
});

test("the first paint keeps the host's order from the config, not a sort by name", async () => {
  const config = fakeConfig();
  config.apps = [appOf('zed', 'Zed'), appOf('abc', 'Abc')];
  assert.deepEqual(tileIds(await ready({ config })), ['zed', 'abc']);
});

test('an apps message that arrives before the page has painted is not lost', async () => {
  const page = loadPage();
  page.deliver({ type: 'apps', apps: [appOf('disney', 'Disney+')], boostApps: ['Disney+'] });
  await page.settle();
  assert.deepEqual(tileIds(page), ['disney']);
});

test('focus stays on the app in focus when the row changes around it', async () => {
  const page = await ready();
  page.pressKey(RIGHT);
  assert.equal(page.focused().dataset.app, 'disney');
  page.deliver({ type: 'apps', apps: [appOf('disney', 'Disney+'), appOf('youtube', 'YouTube')], boostApps: ['Disney+'] });
  await page.settle();
  assert.equal(page.focused().dataset.app, 'disney');
});

test('the boost label follows boostApps in each apps message, and the button hides when it is empty', async () => {
  const page = await ready();
  const boost = button(page, 'boost');
  page.deliver({ type: 'boost', on: true });
  assert.equal(boost.hidden, false);
  assert.equal(boost.getAttribute('aria-label'), 'Boost Disney+: on');
  page.deliver({ type: 'apps', apps: [appOf('youtube', 'YouTube'), appOf('disney', 'Disney+'), appOf('netflix', 'Netflix')], boostApps: ['Disney+', 'Netflix'] });
  await page.settle();
  assert.equal(boost.getAttribute('aria-label'), 'Boost Disney+ and Netflix: on', 'the state (on) is kept');
  assert.ok(boost.classList.contains('on'));
  page.deliver({ type: 'apps', apps: [appOf('youtube', 'YouTube')], boostApps: [] });
  await page.settle();
  assert.equal(boost.hidden, true);
  page.deliver({ type: 'apps', apps: [appOf('youtube', 'YouTube'), appOf('disney', 'Disney+')], boostApps: ['Disney+'] });
  await page.settle();
  assert.equal(boost.hidden, false);
  assert.equal(boost.getAttribute('aria-label'), 'Boost Disney+: on');
});

test('boost label joins several app names with and', async () => {
  const config = fakeConfig();
  config.boostApps = ['Disney+', 'Netflix'];
  const page = await ready({ config });
  page.deliver({ type: 'boost', on: true });
  assert.equal(button(page, 'boost').getAttribute('aria-label'), 'Boost Disney+ and Netflix: on');
});

test('boost button hidden without boostApps', async () => {
  for (const boostApps of [[], undefined]) {
    const config = fakeConfig();
    if (boostApps) config.boostApps = boostApps; else delete config.boostApps;
    const page = await ready({ config });
    const boost = button(page, 'boost');
    assert.equal(boost.hidden, true);
    assert.ok(!page.document.querySelectorAll('.focusable').some(el => el === boost && el.offsetParent !== null));
  }
  assert.equal((await ready()).$('toast').classes.has('on'), false);
});

// Every top-row button shows its name under it while in focus (owner, 2026-11-02): the circular arrow alone did not say
// that it restarts the whole box.
test('every top-row button carries its name as a label under it', async () => {
  const page = await ready();
  const names = { apps: 'Apps', ambient: 'Picture mode', desktop: 'Windows desktop', restart: 'Restart the TV box', shutdown: 'Shut down the TV box' };
  for (const [action, name] of Object.entries(names)) {
    const btn = button(page, action);
    assert.ok(btn.innerHTML.includes('<span class="btn-tip">' + name + '</span>'), action + ' label');
    assert.equal(btn.getAttribute('aria-label'), name, action + ' aria-label');
  }
  assert.ok(button(page, 'boost').innerHTML.includes('<span class="btn-tip">Boost Disney+</span>'), 'boost keeps its own label');
});

// A press changes boost while the button has focus: its label springs in again there, so no toast covers the screen.
test('a boost change springs the label in under the button, and shows no toast', async () => {
  const page = await ready();
  const boost = button(page, 'boost');
  page.deliver({ type: 'boost', on: true });
  assert.ok(!boost.innerHTML.includes('btn-tip pop'), 'the starting state does not spring');
  page.deliver({ type: 'boost', on: false });
  assert.ok(boost.innerHTML.includes('<span class="btn-tip pop">Boost Disney+: off</span>'));
  page.deliver({ type: 'boost', on: true });
  assert.ok(boost.innerHTML.includes('<span class="btn-tip pop">Boost Disney+: on</span>'));
  assert.equal(page.$('toast').classes.has('on'), false);
  assert.equal(toastText(page), '');
});

test('a reloaded page treats its first boost message as the starting state again', async () => {
  const page = await ready();
  page.deliver({ type: 'boost', on: true });
  const reloaded = await ready();
  reloaded.deliver({ type: 'boost', on: false });
  assert.ok(!button(reloaded, 'boost').innerHTML.includes('btn-tip pop'));
});

// The liquid-glass layout (owner, 2026-10-31): no Home title or place in the top bar, the date and greeting top left,
// the weather's sentence and sunrise under its card.
test('layout: no title or place in the top bar; the weather words sit in the column of the weather card', async () => {
  const page = await ready();
  assert.equal(page.document.querySelector('.brand'), null);
  assert.equal(page.$('place'), null);
  const side = page.$('weather').closest('.side');
  assert.ok(side, 'the weather card is in the side column');
  assert.ok(side.contains(page.$('sub')) && side.contains(page.$('sunline')));
  assert.ok(page.$('greet').closest('.greeting').contains(page.$('date')));
  assert.ok(!page.$('greet').closest('.greeting').contains(page.$('sub')));
});

test('tiles are glass: no brand colour is painted on them', async () => {
  const page = await ready();
  for (const t of tiles(page)) assert.ok(!t.style.background, t.dataset.app + ' has no painted background');
});

test('motion time from the config sets --motion on the page', async () => {
  assert.equal((await ready()).document.documentElement.style['--motion'], '1', 'the normal speed when left out');
  const config = fakeConfig();
  config.motionTime = 2;
  assert.equal((await ready({ config })).document.documentElement.style['--motion'], '2');
});

test('tiles render before weather resolves', async () => {
  const page = await ready({ fetch: () => new Promise(() => {}) });
  assert.deepEqual(tiles(page).map(t => t.textContent), ['YouTube', 'Disney+']);
  assert.equal(page.$('wxCond').textContent, 'Loading weather…');
});

test('tiles render when the weather request fails', async () => {
  const page = await ready({ fetch: () => Promise.reject(new Error('offline')) });
  assert.equal(tiles(page).length, 2);
});

test('in a plain browser there is no host: preview class set, post only logs', async () => {
  const page = await ready({ host: false });
  assert.equal(page.window.TVHost.isHost, false);
  assert.ok(page.document.body.classList.contains('preview'));
  button(page, 'shutdown').click();
  page.pressKey(RIGHT);
  page.pressKey(ENTER);
  assert.deepEqual(page.posted, []);
  assert.ok(page.logs.some(line => line.includes('shutdown')), 'the message was logged');
});

test('inside the host the preview class is not set', async () => {
  const page = await ready();
  assert.equal(page.window.TVHost.isHost, true);
  assert.ok(!page.document.body.classList.contains('preview'));
});

test('no Tizen calls remain in the page code', () => {
  for (const file of ['js/apps.js', 'js/main.js', 'js/host.js', 'index.html']) {
    assert.doesNotMatch(fs.readFileSync(path.join(root, file), 'utf8'), /tizen/i, file);
  }
});

test('no HDR: message instead of tiles, icon row usable', async () => {
  const config = fakeConfig();
  config.requireHdr = true;
  const page = await ready({ config, matchMedia: { matches: false } });
  assert.ok(page.mediaQueries.includes('(dynamic-range: high)'), 'the page watches Windows HDR');
  assert.equal(tiles(page).length, 0);
  assert.equal(page.$('grid').textContent, 'Waiting for HDR...');
  assert.equal(page.document.querySelectorAll('#grid .focusable').length, 0, 'nothing in the row to focus or click');
  assert.ok(page.focused() && page.focused().dataset.action, 'focus is on the icon row');
  const first = page.focused();
  page.pressKey(RIGHT);
  assert.ok(page.focused() && page.focused() !== first && page.focused().dataset.action, 'D-pad still moves along the icon row');
  button(page, 'restart').click();
  assert.equal(page.$('confirm').hidden, false, 'restart still asks to confirm');
  page.pressKey(ESC);
  button(page, 'shutdown').click();
  assert.equal(page.$('confirm').hidden, false, 'shut down still asks to confirm');
  assert.deepEqual(sent(page), []);
});

test('HDR on later: tiles appear, and go again when HDR goes off', async () => {
  const config = fakeConfig();
  config.requireHdr = true;
  const page = await ready({ config, matchMedia: { matches: false } });
  page.setMedia(true);
  assert.deepEqual(tiles(page).map(t => t.dataset.app), ['youtube', 'disney']);
  assert.ok(!page.$('grid').textContent.includes('Waiting'));
  tiles(page)[0].click();
  assert.deepEqual(sent(page), [{ type: 'launch', id: 'youtube' }]);
  assert.ok(page.focused() === tiles(page)[0], 'the clicked tile has focus');
  page.setMedia(false);
  assert.equal(tiles(page).length, 0);
  assert.equal(page.$('grid').textContent, 'Waiting for HDR...');
  assert.ok(page.focused() && page.focused().dataset.action, 'focus falls back to the icon row');
});

test('requireHdr false: tiles regardless', async () => {
  for (const matches of [false, true]) {
    const page = await ready({ matchMedia: { matches } });
    assert.equal(tiles(page).length, 2);
    page.setMedia(!matches);
    assert.equal(tiles(page).length, 2);
  }
  assert.equal((await ready()).document.querySelectorAll('#grid .tile').length, 2, 'also without matchMedia');
});

test('hdr state posted on load and change', async () => {
  const hdr = page => page.posted.filter(m => m.type === 'hdr');
  const page = await ready({ matchMedia: { matches: false } });
  assert.deepEqual(hdr(page), [{ type: 'hdr', on: false }]);
  page.setMedia(true);
  page.setMedia(false);
  assert.deepEqual(hdr(page), [{ type: 'hdr', on: false }, { type: 'hdr', on: true }, { type: 'hdr', on: false }]);
  const on = await ready({ matchMedia: { matches: true } });
  assert.deepEqual(hdr(on), [{ type: 'hdr', on: true }]);
});

// The launcher is English only (owner, 2026-11-02): the Portuguese that came with custom-tv is gone, and a Locale other
// than en-GB in launcher.json changes nothing on screen.
test('the page is in English whatever the host says its language is', async () => {
  const config = fakeConfig();
  config.requireHdr = true;
  config.defaults.language = 'pt-PT';
  const page = await ready({ config, matchMedia: { matches: false } });
  assert.equal(page.$('grid').textContent, 'Waiting for HDR...');
  assert.ok(button(page, 'restart').innerHTML.includes('<span class="btn-tip">Restart the TV box</span>'));
});

test('index.html and the page scripts carry no Portuguese', () => {
  const html = fs.readFileSync(path.join(root, 'index.html'), 'utf8');
  assert.ok(html.includes('<html lang="en-GB">'));
  assert.ok(!/data-i18n/.test(html), 'no translation markers left');
  const i18n = fs.readFileSync(path.join(root, 'js', 'i18n.js'), 'utf8');
  for (const word of ['portuguese', 'pt-PT', 'setLanguage', 'Início']) assert.ok(!i18n.includes(word), word);
  assert.ok(!fs.readFileSync(path.join(root, 'js', 'main.js'), 'utf8').includes('setLanguage'));
});

// ---------- the Apps button and panel ----------

const withChannel4 = (version = 'tv') => ({
  id: 'channel4', name: 'Channel 4', icon: 'icons/channel4.svg', shown: true, version,
  versions: [{ id: 'tv', name: 'TV' }, { id: 'web', name: 'Website' }]
});
const appRow = (page, id) => page.document.querySelector('#appsList [data-app-row="' + id + '"]');
const rowIds = page => page.document.querySelectorAll('#appsList [data-app-row]').map(el => el.dataset.appRow);
const rowBox = (page, id) => appRow(page, id).closest('.app-row');
const layout = () => [appOf('youtube', 'YouTube'), appOf('disney', 'Disney+'), withChannel4()];
async function openApps(apps, options) {
  const page = await ready(options);
  if (apps) { page.deliver({ type: 'apps', apps, boostApps: ['Disney+'] }); await page.settle(); }
  button(page, 'apps').click();
  return page;
}

test('OK on the Apps button opens the panel with focus on the first row', async () => {
  const page = await openApps(layout());
  assert.equal(page.$('apps').hidden, false);
  assert.ok(page.document.body.classList.contains('overlay-on'));
  assert.deepEqual(rowIds(page), ['youtube', 'disney', 'channel4']);
  assert.equal(page.focused(), appRow(page, 'youtube'));
  assert.deepEqual(sent(page), []);
});

test('Back closes the Apps panel and returns focus to the Apps button', async () => {
  const page = await openApps(layout());
  page.pressKey(ESC);
  assert.equal(page.$('apps').hidden, true);
  assert.equal(page.focused(), button(page, 'apps'));
});

test('Right moves along a row: the version choice where there is one, then the switch', async () => {
  // (The fake page lays a row's controls out by position in the row, so Channel 4 goes first for the version choice to be nearest.)
  const page = await openApps([withChannel4(), appOf('youtube', 'YouTube')]);
  assert.equal(page.focused(), appRow(page, 'channel4'));
  page.pressKey(RIGHT);
  assert.equal(page.focused().dataset.appVersion, 'channel4');
  page.pressKey(RIGHT);
  assert.equal(page.focused().dataset.appShow, 'channel4');
  page.pressKey(LEFT);
  page.pressKey(LEFT);
  assert.equal(page.focused(), appRow(page, 'channel4'));
  const single = await openApps([appOf('youtube', 'YouTube'), appOf('disney', 'Disney+')]);
  single.pressKey(RIGHT);
  assert.equal(single.focused().dataset.appShow, 'youtube', 'no version choice on YouTube');
});

test('OK on a show switch posts appsShow with the opposite state', async () => {
  const page = await openApps(layout().slice(0, 1).concat([appOf('disney', 'Disney+', false)]));
  page.document.querySelector('[data-app-show="disney"]').click();
  assert.deepEqual(sent(page), [{ type: 'appsShow', id: 'disney', on: true }]);
  page.deliver({ type: 'apps', apps: [appOf('youtube', 'YouTube'), appOf('disney', 'Disney+')], boostApps: [] });
  page.document.querySelector('[data-app-show="youtube"]').click();
  assert.deepEqual(sent(page).slice(1), [{ type: 'appsShow', id: 'youtube', on: false }]);
});

test('the only shown app cannot be hidden: nothing is posted, a toast says why', async () => {
  const page = await openApps([appOf('youtube', 'YouTube'), appOf('disney', 'Disney+', false)]);
  page.document.querySelector('[data-app-show="youtube"]').click();
  assert.deepEqual(sent(page), []);
  assert.equal(toastText(page), 'At least one app stays on the home row');
  assert.ok(page.$('toast').classes.has('on'));
});

test("OK on Channel 4's version posts appsVersion for the next one, wrapping", async () => {
  const page = await openApps(layout());
  page.document.querySelector('[data-app-version="channel4"]').click();
  assert.deepEqual(sent(page), [{ type: 'appsVersion', id: 'channel4', version: 'web' }]);
  page.deliver({ type: 'apps', apps: [appOf('youtube', 'YouTube'), appOf('disney', 'Disney+'), withChannel4('web')], boostApps: [] });
  assert.ok(/>Website</.test(page.$('appsList').innerHTML), 'the panel shows the chosen version');
  page.document.querySelector('[data-app-version="channel4"]').click();
  assert.deepEqual(sent(page).slice(1), [{ type: 'appsVersion', id: 'channel4', version: 'tv' }]);
});

test('OK on a row picks it up, Down moves it one place, OK drops it', async () => {
  const page = await openApps(layout());
  appRow(page, 'youtube').click();
  assert.ok(rowBox(page, 'youtube').classList.contains('carrying'));
  assert.deepEqual(sent(page), []);
  page.pressKey(DOWN);
  assert.deepEqual(sent(page), [{ type: 'appsMove', id: 'youtube', to: 1 }]);
  // The host answers with the new order: the panel follows and the row is still carried and in focus.
  page.deliver({ type: 'apps', apps: [appOf('disney', 'Disney+'), appOf('youtube', 'YouTube'), withChannel4()], boostApps: ['Disney+'] });
  assert.deepEqual(rowIds(page), ['disney', 'youtube', 'channel4']);
  assert.ok(rowBox(page, 'youtube').classList.contains('carrying'), 'still carried after the redraw');
  assert.equal(page.focused(), appRow(page, 'youtube'));
  page.pressKey(DOWN);
  assert.deepEqual(sent(page).slice(1), [{ type: 'appsMove', id: 'youtube', to: 2 }]);
  page.pressKey(ENTER);
  assert.ok(!rowBox(page, 'youtube').classList.contains('carrying'));
  assert.equal(sent(page).length, 2, 'dropping posts nothing: the host already has the order');
  assert.equal(page.$('apps').hidden, false);
});

test('at the ends of the list Up and Down post nothing while carrying, and Left and Right do nothing', async () => {
  const page = await openApps(layout());
  appRow(page, 'youtube').click();
  page.pressKey(UP);
  page.pressKey(LEFT);
  page.pressKey(RIGHT);
  assert.deepEqual(sent(page), []);
  assert.equal(page.focused(), appRow(page, 'youtube'));
});

test('Back while carrying drops the row and keeps the panel; a second Back closes it', async () => {
  const page = await openApps(layout());
  appRow(page, 'disney').click();
  assert.ok(rowBox(page, 'disney').classList.contains('carrying'));
  page.pressKey(ESC);
  assert.ok(!rowBox(page, 'disney').classList.contains('carrying'));
  assert.equal(page.$('apps').hidden, false, 'the panel stays open');
  assert.equal(page.focused(), appRow(page, 'disney'));
  page.pressKey(ESC);
  assert.equal(page.$('apps').hidden, true);
  assert.equal(page.focused(), button(page, 'apps'));
  assert.deepEqual(sent(page), []);
});

test('Home while carrying closes the panel and ends the carry', async () => {
  const page = await openApps(layout());
  appRow(page, 'disney').click();
  page.deliver({ type: 'home' });
  assert.equal(page.$('apps').hidden, true);
  button(page, 'apps').click();
  assert.ok(!page.$('appsList').innerHTML.includes('carrying'), 'a fresh panel carries nothing');
});

test('the open panel follows each apps message and keeps focus on the same control', async () => {
  const page = await openApps(layout());
  page.document.querySelector('[data-app-show="disney"]').click();   // focuses the Disney+ switch (and asks to hide it)
  assert.equal(page.focused().dataset.appShow, 'disney');
  page.deliver({ type: 'apps', apps: [appOf('youtube', 'YouTube'), appOf('disney', 'Disney+', false), withChannel4()], boostApps: [] });
  assert.equal(page.focused().dataset.appShow, 'disney', 'still on the Disney+ switch');
  assert.ok(page.document.contains(page.focused()), 'which is the new element');
  assert.ok(rowBox(page, 'disney').classList.contains('off'));
  assert.ok(!page.document.querySelector('[data-app-show="disney"]').classList.contains('on'));
  await page.settle();
  assert.deepEqual(tileIds(page), ['youtube', 'channel4'], 'the home row behind it follows too');
});

test('the panel is in English', async () => {
  const page = await openApps(layout());
  assert.equal(page.$('apps').querySelector('h3').textContent, 'Apps');
  assert.ok(page.$('appsList').innerHTML.includes('Show on home'));
});
