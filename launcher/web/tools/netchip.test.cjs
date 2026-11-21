// Tests for the home page's "No internet" / "No network" chip: it follows the host's net message, shows only after
// the state has been wrong for 10 s and the page has been up for 60 s, and is never a target for the arrow keys.
'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const { loadPage } = require('./fake-page.cjs');

const net = state => ({ type: 'net', state });
const chip = page => page.$('netChip');

test('nothing shows before any net message', () => {
  const page = loadPage();
  page.tick(120000);
  assert.equal(chip(page).hidden, true);
  assert.equal(page.window.TVNet.state(), null);
});

test('local for 10 s shows No internet', () => {
  const page = loadPage();
  page.tick(55000);
  page.deliver(net('local'));
  page.tick(9999);
  assert.equal(chip(page).hidden, true);
  page.tick(1);
  assert.equal(chip(page).hidden, false);
  assert.equal(page.$('netChipText').textContent, 'No internet');
  assert.equal(page.window.TVNet.state(), 'local');
});

test('none shows No network', () => {
  const page = loadPage();
  page.tick(55000);
  page.deliver(net('none'));
  page.tick(9999);
  assert.equal(chip(page).hidden, true);
  page.tick(1);
  assert.equal(chip(page).hidden, false);
  assert.equal(page.$('netChipText').textContent, 'No network');
});

test('never in the first 60 s', () => {
  const page = loadPage();
  page.deliver(net('none'));
  page.tick(59999);
  assert.equal(chip(page).hidden, true);
  page.tick(1);
  assert.equal(chip(page).hidden, false);
});

test('a 9 s blip never shows', () => {
  const page = loadPage();
  page.tick(60000);
  page.deliver(net('local'));
  page.tick(9000);
  page.deliver(net('internet'));
  page.tick(30000);
  assert.equal(chip(page).hidden, true);
});

test('internet hides it at once', () => {
  const page = loadPage();
  page.tick(60000);
  page.deliver(net('local'));
  page.tick(10000);
  assert.equal(chip(page).hidden, false);
  page.deliver(net('internet'));
  assert.equal(chip(page).hidden, true);
});

test('the chip is not focusable', () => {
  const page = loadPage();
  page.tick(60000);
  page.deliver(net('local'));
  page.tick(10000);
  assert.equal(chip(page).hidden, false);
  assert.ok(!chip(page).classList.contains('focusable'));
  for (let i = 0; i < 12; i++) {
    page.pressKey(39);
    assert.notEqual(page.focused(), chip(page));
  }
});
