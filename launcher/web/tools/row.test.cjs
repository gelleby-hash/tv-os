// Tests for the row's scrolling helpers (js/row.js), loaded into a bare vm context. The numbers are the home row's real geometry:
// tiles 312 wide with a 24 gap, a window of 1680, so six tiles are 2016 wide.
'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const root = path.join(__dirname, '..');

// Objects made inside the vm context have another realm's prototype, so edges() results are spread before deepEqual.
function load() {
  const window = {};
  vm.runInContext(fs.readFileSync(path.join(root, 'js', 'row.js'), 'utf8'), vm.createContext({ window }), { filename: 'row.js' });
  return window.TVRow;
}

test('keepInView: an item already in view keeps the offset', () => {
  assert.equal(load().keepInView(336, 648, 1680, 0, 2016), 0);
});

test('keepInView: the sixth tile slides the row just far enough', () => {
  assert.equal(load().keepInView(1680, 1992, 1680, 0, 2016), 312);
});

test('keepInView: back to the first tile slides to 0', () => {
  assert.equal(load().keepInView(0, 312, 1680, 312, 2016), 0);
});

test('keepInView: wrap to the first tile goes back to 0', () => {
  assert.equal(load().keepInView(0, 312, 1680, 336, 2016), 0);
});

test('keepInView: a shorter row pulls the offset back', () => {
  const R = load();
  assert.equal(R.keepInView(0, 312, 1680, 312, 1656), 0);
  assert.equal(R.keepInView(1344, 1656, 1680, 312, 1656), 0);
});

test('edges: a row that fits fades neither edge', () => {
  assert.deepEqual({ ...load().edges(0, 1680, 1656) }, { before: false, after: false });
});

test('edges: scrolled to the end fades only the start', () => {
  const R = load();
  assert.deepEqual({ ...R.edges(336, 1680, 2016) }, { before: true, after: false });
  assert.deepEqual({ ...R.edges(0, 1680, 2016) }, { before: false, after: true });
});

// scrollGrid puts more-before / more-after on .apps itself, so a rule for those classes alone that set left or right would
// override .apps' own insets and push the row off the stage.
test('style.css: the state classes more-before and more-after never set left or right on their own', () => {
  const css = fs.readFileSync(path.join(root, 'css', 'style.css'), 'utf8').replace(/\/\*[\s\S]*?\*\//g, '');
  for (const rule of css.matchAll(/([^{}]+)\{([^}]*)\}/g)) {
    const alone = rule[1].split(',').some(sel => /\.more-(before|after)$/.test(sel.trim().split(/\s+/).pop()));
    if (!alone) continue;
    assert.ok(!/(^|[;\s])(left|right)\s*:/.test(rule[2]), 'sets left or right: ' + rule[1].trim());
  }
});

// The viewport clips what is outside it. The focused tile's shadow reaches well below the row, so the viewport must run down to
// the bottom of the stage (the row sits 70 px above it), or the shadow ends in a straight line.
test('style.css: .grid-viewport has at least 70 px of room below the row', () => {
  const css = fs.readFileSync(path.join(root, 'css', 'style.css'), 'utf8').replace(/\/\*[\s\S]*?\*\//g, '');
  const rule = [...css.matchAll(/([^{}]+)\{([^}]*)\}/g)].find(r => r[1].trim() === '.grid-viewport');
  assert.ok(rule, '.grid-viewport rule not found');
  const bottom = /(^|[;\s])bottom\s*:\s*(-?\d+(?:\.\d+)?)px/.exec(rule[2]);
  assert.ok(bottom && -parseFloat(bottom[2]) >= 70, 'bottom is ' + (bottom && bottom[2]) + 'px, needs -70px or more');
  const pad = /(^|[;\s])padding\s*:\s*([^;]+)/.exec(rule[2]);
  const parts = pad[2].trim().split(/\s+/).map(parseFloat);
  const padBottom = parts.length === 1 ? parts[0] : parts.length === 2 ? parts[0] : parts[2];
  assert.ok(padBottom >= 70, 'bottom padding is ' + padBottom + 'px, needs 70px or more');
});

// The same goes for the sides: the first and last tile's shadow reaches about 80 px sideways, and the row sits 120 px from each
// stage edge. While no tiles are hidden on a side the viewport runs out to the stage edge there (left or right together with the
// padding on that side, so the tiles do not move); while tiles are hidden the side keeps its small room, so they stay hidden.
test('style.css: .grid-viewport widens each side to at least 120 px while that side has no hidden tiles', () => {
  const css = fs.readFileSync(path.join(root, 'css', 'style.css'), 'utf8').replace(/\/\*[\s\S]*?\*\//g, '');
  const rules = [...css.matchAll(/([^{}]+)\{([^}]*)\}/g)];
  for (const [side, hiddenClass] of [['left', 'more-before'], ['right', 'more-after']]) {
    const rule = rules.find(r => r[1].replace(/\s+/g, ' ').trim() === '.apps:not(.' + hiddenClass + ') .grid-viewport');
    assert.ok(rule, 'no rule widening the ' + side + ' side while ' + hiddenClass + ' is off');
    const edge = new RegExp(String.raw`(^|[;\s])` + side + String.raw`\s*:\s*(-?\d+(?:\.\d+)?)px`).exec(rule[2]);
    assert.ok(edge && -parseFloat(edge[2]) >= 120, side + ' is ' + (edge && edge[2]) + 'px, needs -120px or more');
    const pad = new RegExp(String.raw`(^|[;\s])padding-` + side + String.raw`\s*:\s*(\d+(?:\.\d+)?)px`).exec(rule[2]);
    assert.ok(pad && parseFloat(pad[2]) === -parseFloat(edge[2]), 'padding-' + side + ' must equal ' + -parseFloat(edge[2]) + 'px so the tiles do not move');
  }
});
