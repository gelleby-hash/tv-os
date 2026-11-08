// The home page reports its first keys and its first pointer move to the launcher's log: after a boot a viewer
// found the remote dead until the mouse moved (owner, 2026-11-08), and the log could not say whether the page got
// the keys. Runs the real page scripts in a fake DOM.
'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const { loadPage } = require('./fake-page.cjs');

async function page() {
  const p = loadPage({ now: new Date(2026, 10, 8, 12, 0, 0).getTime() });
  await p.settle();
  return p;
}
const keys = p => p.posted.filter(m => m.type === 'key');
const pointers = p => p.posted.filter(m => m.type === 'pointer');

test('key log: the first ten keys are reported with their code, the scope and what had the focus', async () => {
  const p = await page();
  for (let i = 0; i < 12; i++) p.pressKey(39);
  const sent = keys(p);
  assert.equal(sent.length, 10, 'ten, then no more');
  assert.equal(sent[0].code, 39);
  assert.match(sent[0].id, /^home:.+/, 'the scope, then what had the focus');
  assert.match(sent[1].id, /^home:tile \d+$/, 'after a Right a tile has the focus');
});

test('key log: the focus is described before the page acts on the key', async () => {
  const p = await page();
  const top = p.document.querySelector('#home .focusable[data-action]');
  p.fire(top, 'mouseover');
  p.pressKey(40);
  assert.equal(keys(p)[0].id, 'home:' + top.dataset.action);
});

test('key log: a key the page does not use is reported too', async () => {
  const p = await page();
  p.pressKey(65, 'a');
  assert.deepEqual(keys(p).map(m => m.code), [65]);
});

test('key log: the first pointer move is reported once', async () => {
  const p = await page();
  const tile = p.document.querySelector('#home .focusable');
  p.fire(tile, 'mouseover');
  p.fire(tile, 'mouseover');
  assert.equal(pointers(p).length, 1);
});
