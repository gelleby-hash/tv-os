const test = require('node:test');
const assert = require('node:assert/strict');
const { loadPage, fakeConfig } = require('./fake-page.cjs');

test('launch posts a launch message with the app id', async () => {
  const page = loadPage();
  await page.window.TVApps.launch('disney');
  assert.deepEqual(page.posted.filter(m => m.type === 'launch'), [{ type: 'launch', id: 'disney' }]);
});

test('list returns the configured apps in order', async () => {
  const { window, plain } = loadPage();
  const apps = plain(await window.TVApps.list());
  assert.deepEqual(apps.map(a => a.name), ['YouTube', 'Disney+']);
  assert.deepEqual(apps.map(a => a.id), ['youtube', 'disney']);
  assert.deepEqual(apps.map(a => a.icon), ['icons/youtube.svg', 'icons/disney.svg']);
  assert.ok(apps.every(a => !('sample' in a)), 'no sample flag');
});

test('list is empty when the host configured no apps', async () => {
  const page = loadPage({ config: { ...fakeConfig(), apps: [] } });
  assert.deepEqual(page.plain(await page.window.TVApps.list()), []);
});

const app = (id, name, shown = true, version = 'default') =>
  ({ id, name, icon: 'icons/' + id + '.svg', shown, version, versions: [{ id: 'default', name }] });

function threeApps() {
  const config = fakeConfig();
  config.apps = [app('youtube', 'YouTube'), app('disney', 'Disney+', false), app('channel4', 'Channel 4')];
  return config;
}

test("list gives the shown apps in the host's order and skips the hidden ones", async () => {
  const { window, plain } = loadPage({ config: threeApps() });
  assert.deepEqual(plain(await window.TVApps.list()).map(a => a.id), ['youtube', 'channel4']);
});

test('all gives every app in order with its shown flag, version and versions', () => {
  const { window, plain } = loadPage({ config: threeApps() });
  const all = plain(window.TVApps.all());
  assert.deepEqual(all.map(a => [a.id, a.shown]), [['youtube', true], ['disney', false], ['channel4', true]]);
  assert.equal(all[0].version, 'default');
  assert.deepEqual(all[0].versions, [{ id: 'default', name: 'YouTube' }]);
});

test('an apps message from the host replaces the order and what is shown', async () => {
  const page = loadPage({ config: threeApps() });
  page.deliver({ type: 'apps', apps: [app('channel4', 'Channel 4'), app('disney', 'Disney+'), app('youtube', 'YouTube', false)], boostApps: [] });
  assert.deepEqual(page.plain(await page.window.TVApps.list()).map(a => a.id), ['channel4', 'disney']);
  assert.deepEqual(page.plain(page.window.TVApps.all()).map(a => a.id), ['channel4', 'disney', 'youtube']);
  assert.deepEqual(page.plain(page.window.TVApps.boostApps()), []);
});

test('a malformed apps message changes nothing', async () => {
  const page = loadPage({ config: threeApps() });
  page.deliver({ type: 'apps' });
  page.deliver({ type: 'apps', apps: 'nope', boostApps: 3 });
  assert.deepEqual(page.plain(await page.window.TVApps.list()).map(a => a.id), ['youtube', 'channel4']);
  assert.deepEqual(page.plain(page.window.TVApps.boostApps()), ['Disney+']);
});

test('onChange runs after an apps message, with the new list already in place', () => {
  const page = loadPage({ config: threeApps() });
  const seen = [];
  page.window.TVApps.onChange(() => seen.push(page.plain(page.window.TVApps.all()).map(a => a.id)));
  page.deliver({ type: 'apps', apps: [app('disney', 'Disney+')], boostApps: [] });
  assert.deepEqual(seen, [['disney']]);
});

test('isHost follows the host bridge', () => {
  assert.equal(loadPage().window.TVApps.isHost, true);
  assert.equal(loadPage({ host: false }).window.TVApps.isHost, false);
});

test('in a plain browser launch logs instead of posting', async () => {
  const page = loadPage({ host: false });
  await page.window.TVApps.launch('youtube');
  assert.deepEqual(page.posted, []);
  assert.ok(page.logs.some(line => line.includes('"launch"') && line.includes('youtube')));
});
