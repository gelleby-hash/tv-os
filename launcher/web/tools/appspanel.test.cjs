// Tests for the Apps panel's pure helpers (js/appspanel.js), loaded into a bare vm context.
'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const root = path.join(__dirname, '..');

function load() {
  const window = {};
  vm.runInContext(fs.readFileSync(path.join(root, 'js', 'appspanel.js'), 'utf8'), vm.createContext({ window }), { filename: 'appspanel.js' });
  return window.TVAppsPanel;
}

const one = (id, name, shown = true) =>
  ({ id, name, icon: 'icons/' + id + '.svg', shown, version: 'default', versions: [{ id: 'default', name }] });
const channel4 = (version = 'tv', shown = true) =>
  ({ id: 'channel4', name: 'Channel 4', icon: 'icons/channel4.svg', shown, version, versions: [{ id: 'tv', name: 'TV' }, { id: 'web', name: 'Website' }] });
const apps = () => [one('youtube', 'YouTube'), one('disney', 'Disney+', false), channel4()];

test('rowsHtml: one row per app, each with its focusable row and switch', () => {
  const P = load();
  const html = P.rowsHtml(apps(), null);
  assert.equal((html.match(/class="app-row[ "]/g) || []).length, 3);
  for (const id of ['youtube', 'disney', 'channel4']) {
    assert.ok(html.includes('data-app-row="' + id + '"'), id + ' row');
    assert.ok(html.includes('data-app-show="' + id + '"'), id + ' switch');
  }
  assert.equal((html.match(/focusable/g) || []).length, 3 + 1 + 3, 'three rows, one version choice, three switches');
});

test('rowsHtml: a version control only where there is a choice, showing the chosen version', () => {
  const P = load();
  const html = P.rowsHtml(apps(), null);
  assert.ok(!html.includes('data-app-version="youtube"'));
  assert.ok(!html.includes('data-app-version="disney"'));
  assert.ok(/data-app-version="channel4"[^>]*>TV</.test(html), 'shows TV');
  const web = P.rowsHtml([channel4('web')], null);
  assert.ok(/data-app-version="channel4"[^>]*>Website</.test(web), 'shows Website');
});

test('rowsHtml: the switch is on for a shown app, and a hidden row is off', () => {
  const P = load();
  const html = P.rowsHtml(apps(), null);
  const switchOf = id => new RegExp('<button class="([^"]*)" data-app-show="' + id + '"').exec(html)[1];
  assert.ok(/\bon\b/.test(switchOf('youtube')));
  assert.ok(!/\bon\b/.test(switchOf('disney')));
  assert.equal((html.match(/class="app-row off/g) || []).length, 1, 'only the hidden app is off');
  assert.ok(/class="app-row off[^"]*"[^>]*>(?:(?!app-row)[\s\S])*data-app-row="disney"/.test(html), 'and it is Disney+');
});

test('rowsHtml: the carried row is marked', () => {
  const P = load();
  const html = P.rowsHtml(apps(), 'channel4');
  assert.equal((html.match(/carrying/g) || []).length, 1);
  assert.ok(/class="app-row carrying"[^>]*>(?:(?!app-row)[\s\S])*data-app-row="channel4"/.test(html));
  assert.ok(!P.rowsHtml(apps(), null).includes('carrying'));
});

test('rowsHtml: names and icons are escaped, and the logo is the chosen version icon', () => {
  const P = load();
  const html = P.rowsHtml([Object.assign(one('x', 'A <b> & "C"'), { icon: 'icons/a"b.svg' })], null);
  assert.ok(!html.includes('<b>'));
  assert.ok(html.includes('A &lt;b&gt; &amp; &quot;C&quot;'));
  assert.ok(html.includes('src="icons/a&quot;b.svg"'));
  assert.ok(P.rowsHtml(apps(), null).includes('src="icons/youtube.svg"'));
});

test('nextVersion: the version after the chosen one, wrapping', () => {
  const P = load();
  assert.equal(P.nextVersion(channel4('tv')), 'web');
  assert.equal(P.nextVersion(channel4('web')), 'tv');
  const three = { id: 'x', version: 'b', versions: [{ id: 'a', name: 'A' }, { id: 'b', name: 'B' }, { id: 'c', name: 'C' }] };
  assert.equal(P.nextVersion(three), 'c');
  assert.equal(P.nextVersion(Object.assign({}, three, { version: 'c' })), 'a');
  assert.equal(P.nextVersion(one('y', 'Y')), 'default', 'one version: it stays');
});

test('canHide: false only for the one shown app', () => {
  const P = load();
  const list = apps();
  assert.equal(P.canHide(list, 'youtube'), true);
  assert.equal(P.canHide(list, 'disney'), true, 'a hidden app can always be shown');
  const last = [one('youtube', 'YouTube'), one('disney', 'Disney+', false)];
  assert.equal(P.canHide(last, 'youtube'), false);
  assert.equal(P.canHide(last, 'disney'), true);
  assert.equal(P.canHide(last, 'nothing'), false, 'an unknown app');
});

test('moveIndex: the new index, or null at an end', () => {
  const P = load();
  const list = apps();
  assert.equal(P.moveIndex(list, 'youtube', 1), 1);
  assert.equal(P.moveIndex(list, 'disney', -1), 0);
  assert.equal(P.moveIndex(list, 'channel4', -1), 1);
  assert.equal(P.moveIndex(list, 'youtube', -1), null);
  assert.equal(P.moveIndex(list, 'channel4', 1), null);
  assert.equal(P.moveIndex(list, 'nothing', 1), null);
});

// A carried row lifts, rings and shadows beyond its box, so the list may clip only while it scrolls (class "scrolls", set in main.js).
function appRowsRules() {
  const css = fs.readFileSync(path.join(root, 'css', 'style.css'), 'utf8').replace(/\/\*[\s\S]*?\*\//g, '');
  return [...css.matchAll(/([^{}]+)\{([^}]*)\}/g)].filter(r => r[1].split(',').some(s => s.trim().split(/\s+/)[0].startsWith('.app-rows')));
}

test('style.css: no .app-rows rule clips unless it is .scrolls', () => {
  const rules = appRowsRules();
  assert.ok(rules.length > 0);
  const clipping = rules.filter(r => /overflow(-[xy])?\s*:\s*(hidden|auto|scroll|clip)/.test(r[2]));
  assert.ok(clipping.length > 0, 'the scrolling list still clips');
  for (const r of clipping) assert.match(r[1].trim(), /^\.app-rows\.scrolls$/, 'clipping rule: ' + r[1].trim());
});

test('style.css: .app-rows.scrolls leaves room for a carried row (lift, ring, shadow) and cancels it with margins', () => {
  const rule = appRowsRules().find(r => r[1].trim() === '.app-rows.scrolls');
  assert.ok(rule, 'a .app-rows.scrolls rule');
  const px = name => {
    const m = rule[2].match(new RegExp('(?:^|[;\\s])' + name + '\\s*:\\s*([^;]+)'));
    assert.ok(m, name);
    return m[1].trim().split(/\s+/).map(v => parseFloat(v));
  };
  const [pt, pr, pb] = px('padding'), [mt, mr, mb] = px('margin');
  // The base rule's padding (6 px top and bottom, 20 px sides) is what the rows sit inside; the margins keep them where they were.
  assert.ok(pt >= 14, 'top room: lift 8 + ring 4 + scale');
  assert.ok(pb >= 58, 'bottom room: shadow 28 + 44 - 14 = 58');
  assert.ok(pr >= 46, 'side room: scale 18 + shadow 30');
  assert.equal(mt + pt, 6);
  assert.equal(mb + pb, 6);
  assert.equal(mr + pr, 0);
});
