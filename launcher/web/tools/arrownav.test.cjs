// Runs PageScripts.ArrowNav (read from the C# file, so there is one copy) against a fake page laid out like Disney+'s
// home page: a top bar fixed to the screen, rows of tiles in sideways scrollers, a menu clipped out of sight, the
// rows' arrow buttons at opacity 0. The geometry is given, as getBoundingClientRect would report it.
'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

const source = fs.readFileSync(path.join(__dirname, '..', '..', 'src', 'TvLauncher', 'Apps', 'PageScripts.cs'), 'utf8');
const script = (() => {
  const m = source.match(/ArrowNav = """\r?\n([\s\S]*?)\r?\n\s*""";/);
  assert.ok(m, 'ArrowNav raw string found in PageScripts.cs');
  return m[1];
})();

class El {
  constructor(tag, attrs = {}, rect = [0, 0, 0, 0], style = {}) {
    this.tagName = tag.toUpperCase();
    this.attrs = attrs;
    this.rect = rect;
    this.style = Object.assign({ visibility: 'visible', display: 'block', opacity: '1', overflowX: 'visible', overflowY: 'visible', position: 'static' }, style);
    this.parentElement = null;
    this.children = [];
    this.disabled = false;
    this.isContentEditable = false;
    this.shadowRoot = null;
    this.scrolled = null;
  }
  add(...kids) { for (const k of kids) { k.parentElement = this; this.children.push(k); } return this; }
  getAttribute(n) { return n in this.attrs ? String(this.attrs[n]) : null; }
  hasAttribute(n) { return n in this.attrs; }
  getBoundingClientRect() { const [left, top, width, height] = this.rect; return { left, top, width, height, right: left + width, bottom: top + height }; }
  contains(o) { for (let p = o; p; p = p.parentElement) if (p === this) return true; return false; }
  closest(sel) {
    // Only the selectors the script uses: [attr], [attr="value"] and .class.
    const cls = sel.match(/^\.([\w-]+)$/);
    if (cls) { for (let p = this; p; p = p.parentElement) if ((p.attrs.class || '').split(' ').includes(cls[1])) return p; return null; }
    const m = sel.match(/^\[([\w-]+)(?:="([^"]*)")?\]$/);
    for (let p = this; p; p = p.parentElement) if (m[1] in p.attrs && (m[2] === undefined || p.attrs[m[1]] === m[2])) return p;
    return null;
  }
  // Only the selector the script uses: links, buttons, form fields, the roles, and a tabindex other than -1.
  picked() {
    const t = this.tagName, a = this.attrs;
    return (t === 'A' && ('href' in a || 'data-testid' in a)) || ['BUTTON', 'INPUT', 'SELECT', 'TEXTAREA'].includes(t)
      || ['button', 'tab', 'link'].includes(a.role) || ('tabindex' in a && String(a.tabindex) !== '-1');
  }
  querySelectorAll() { const out = []; const walk = (e) => { for (const k of e.children) { if (k.picked()) out.push(k); walk(k); } }; walk(this); return out; }
  focus(opts) { page.document.activeElement = this; this.focusOpts = opts; }
  // Records what the script sends it, as type@x,y.
  dispatchEvent(e) { (this.events = this.events || []).push(e.type + '@' + e.clientX + ',' + e.clientY); return true; }
  scrollIntoView(opts) { this.scrolled = opts; }
}

let page;

// The home page: a fixed top bar, then a row at y 300 and a row at y 700, each tile 360 wide on a 1920 x 1080 screen.
function homePage({ pathname = '/en-gb/home', carousel = false } = {}) {
  const body = new El('body');
  const doc = { body, documentElement: new El('html'), activeElement: body, fullscreenElement: null, querySelectorAll: (s) => body.querySelectorAll(s),
    elementFromPoint: () => body };
  doc.documentElement.add(body);

  const bar = new El('nav', {}, [0, 0, 1920, 70], { position: 'fixed' });
  const home = new El('a', { href: '/home', 'aria-label': 'Home' }, [680, 15, 100, 40]);
  const search = new El('a', { href: '/search', 'aria-label': 'Search' }, [1840, 15, 50, 40]);
  // The For You / Disney+ / hulu pills, fixed at the top centre over the page.
  const pill = new El('a', { href: '/en-gb/browse/disneyplus', 'aria-label': 'Pill' }, [810, 85, 300, 50]);
  bar.add(home, search, pill);

  // The hero: the slide shown, and the next slide waiting beside it, outside the hero's clipping box.
  const heroBox = new El('div', {}, [0, 100, 1920, 180], { overflowX: 'hidden', overflowY: 'hidden' });
  const hero = new El('a', { href: '/hero', 'aria-label': 'Hero' }, [70, 150, 400, 80]);
  const slide = new El('a', { href: '/slide', 'aria-label': 'Hidden slide' }, [-500, 150, 400, 80]);
  // Its scroll arrow, a strip down the left edge: invisible until the mouse pointer rests on the banner.
  const heroArrow = new El('button', { 'aria-label': 'left arrow', 'aria-controls': 'hero-1' }, [0, 100, 71, 180], { opacity: '0' });
  heroBox.add(slide, hero, heroArrow);

  const row = (y, names) => {
    const scroller = new El('div', {}, [0, y, 1920, 400], { overflowX: 'auto', overflowY: 'auto' });
    const tiles = names.map((n, i) => {
      const x = 70 + i * 360;
      const onScreen = x + 360 > 0 && x < 1920;
      return new El('a', { 'data-testid': 'set-item', 'aria-label': n, tabindex: onScreen ? 0 : -1 }, [x, y, 340, 400]);
    });
    // The empty div Disney+ gives listeners: no tabindex, never a target.
    const filler = new El('div', {}, [0, y, 60, 400]);
    const arrow = new El('button', { 'aria-label': 'right arrow', 'aria-controls': 'shelf-' + y }, [1860, y + 150, 60, 100], { opacity: '0' });
    scroller.add(filler, ...tiles, arrow);
    return { scroller, tiles, arrow };
  };
  const top = row(300, ['A1', 'A2', 'A3', 'A4', 'A5', 'A6', 'A7']);
  const low = row(720, ['B1', 'B2', 'B3', 'B4', 'B5', 'B6', 'B7']);

  // With carousel, a row as Channel 4 builds it (measured 2026-11-02): a Swiper carousel that clips its tiles and moves them with a
  // transform, not by scrolling. Each tile is a link in a slide, 289 wide every 297; past the edge, tabindex -1.
  const carouselBox = new El('div', { class: 'swiper swiper-initialized swiper-horizontal' }, [0, 1100, 1920, 460], { overflowX: 'hidden', overflowY: 'hidden' });
  const wrapper = new El('div', { class: 'swiper-wrapper' }, [0, 1100, 1920, 460]);
  const slides = ['C1', 'C2', 'C3', 'C4', 'C5', 'C6', 'C7', 'C8', 'C9'].map((n, i) => {
    const x = 72 + i * 297;
    const link = new El('a', { href: '/programmes/' + n, 'aria-label': n, tabindex: x + 289 <= 1920 ? 0 : -1 }, [x, 1100, 289, 453]);
    return new El('div', { class: 'swiper-slide' }, [x, 1100, 289, 453]).add(link);
  });
  wrapper.add(...slides);
  carouselBox.add(wrapper);
  carouselBox.swiper = { slides, slid: [], slideTo(i) { this.slid.push(i); } };
  const c4 = { box: carouselBox, tiles: slides.map(s => s.children[0]) };
  body.add(bar, heroBox, top.scroller, low.scroller);
  if (carousel) body.add(carouselBox);

  const handlers = [];
  const warnings = [];
  const ctx = vm.createContext({
    document: doc,
    window: { addEventListener: (type, fn, capture) => handlers.push({ type, fn, capture }) },
    location: { pathname },
    innerWidth: 1920,
    innerHeight: 1080,
    getComputedStyle: (e) => e.style,
    MouseEvent: class { constructor(type, init) { this.type = type; Object.assign(this, init); } },
    PointerEvent: class { constructor(type, init) { this.type = type; Object.assign(this, init); } },
    console: { warn: (...a) => warnings.push(a) },
    Math,
  });
  page = { document: doc, handlers, warnings, home, search, pill, hero, heroArrow, top, low, c4 };
  vm.runInContext(script, ctx, { filename: 'ArrowNav' });
  return page;
}

function press(p, key) {
  const ev = { key, altKey: false, ctrlKey: false, metaKey: false, shiftKey: false, prevented: false, stopped: false,
    preventDefault() { this.prevented = true; }, stopImmediatePropagation() { this.stopped = true; } };
  for (const h of p.handlers) if (h.type === 'keydown') h.fn(ev);
  return ev;
}
const focused = (p) => p.document.activeElement.getAttribute('aria-label');

test('arrows: listens on window, in the capture phase, so it runs before the page', () => {
  const p = homePage();
  assert.equal(p.handlers.length, 1);
  assert.equal(p.handlers[0].type, 'keydown');
  assert.equal(p.handlers[0].capture, true);
});

test('arrows: Right and Left move one tile at a time, and the page never sees the key', () => {
  const p = homePage();
  p.top.tiles[0].focus();
  for (const want of ['A2', 'A3', 'A4']) {
    const ev = press(p, 'ArrowRight');
    assert.equal(focused(p), want);
    assert.ok(ev.prevented && ev.stopped, 'neither Disney+ nor spatial navigation acts on it');
  }
  press(p, 'ArrowLeft');
  assert.equal(focused(p), 'A3');
  assert.equal(JSON.stringify(p.top.tiles[2].scrolled), JSON.stringify({ block: 'center', inline: 'nearest', behavior: 'smooth' }));
});

test('arrows: Right at the screen edge reaches the next tile off screen, never the invisible arrow button', () => {
  const p = homePage();
  p.top.tiles[4].focus();   // A5, the last tile wholly on screen
  press(p, 'ArrowRight');
  assert.equal(focused(p), 'A6', 'off screen (tabindex -1) but in the scroller: reachable');
  press(p, 'ArrowRight');
  assert.equal(focused(p), 'A7');
  const ev = press(p, 'ArrowRight');
  assert.equal(focused(p), 'A7', 'the end of the row: focus stays');
  assert.ok(ev.prevented, 'and the page does not scroll');
});

test('arrows: Down and Up go to the tile under or over, in the next row', () => {
  const p = homePage();
  p.top.tiles[2].focus();
  press(p, 'ArrowDown');
  assert.equal(focused(p), 'B3');
  press(p, 'ArrowUp');
  assert.equal(focused(p), 'A3');
});

test('arrows: Up from the page goes to the page above first, the fixed top bar last', () => {
  const p = homePage();
  p.top.tiles[2].focus();   // A3, right under the pills: on screen they are nearer than the hero
  press(p, 'ArrowUp');
  assert.equal(focused(p), 'Hero');
  p.top.tiles[0].focus();
  press(p, 'ArrowUp');
  assert.equal(focused(p), 'Hero', 'the hero, though the bar is nearer on screen');
  press(p, 'ArrowUp');
  assert.equal(focused(p), 'Home', 'nothing left in the page above: the bar');
});

test('arrows: Left and Right stay in the row they are in', () => {
  const p = homePage();
  p.search.focus();
  press(p, 'ArrowRight');
  assert.equal(focused(p), 'Search', 'nothing to the right in the bar: no jump into a row below');
  press(p, 'ArrowLeft');
  assert.equal(focused(p), 'Home');
});

test('arrows: an item clipped out of sight is never chosen', () => {
  const p = homePage();
  p.hero.focus();
  press(p, 'ArrowLeft');
  assert.equal(focused(p), 'Hero', 'the next slide waits outside the clipping box');
});

test('arrows: Right past the edge of a carousel row reaches the next tile and slides the carousel to it', () => {
  const p = homePage({ carousel: true });
  p.c4.tiles[6].focus();   // C7, cut by the screen edge but in sight
  const ev = press(p, 'ArrowRight');
  assert.equal(focused(p), 'C8', 'clipped by the carousel, which moves it in');
  assert.ok(ev.prevented && ev.stopped);
  assert.deepEqual(p.c4.box.swiper.slid, [7], 'the carousel is told to show slide 7');
  assert.equal(JSON.stringify(p.c4.tiles[7].focusOpts), JSON.stringify({ preventScroll: true }));
  assert.equal(p.c4.tiles[7].scrolled, null, 'no scrollIntoView, which would scroll the clipping box behind the carousel');
});

test('arrows: Right onto a carousel tile cut by the edge slides the carousel too, so the tile is wholly in sight', () => {
  const p = homePage({ carousel: true });
  p.c4.tiles[5].focus();   // C6, the last tile wholly in sight
  press(p, 'ArrowRight');
  assert.equal(focused(p), 'C7', 'cut by the edge, 66 of 289 px in sight');
  assert.deepEqual(p.c4.box.swiper.slid, [6]);
  assert.equal(p.c4.tiles[6].scrolled, null);
});

test('arrows: Right inside a carousel row moves like any row, without touching the carousel', () => {
  const p = homePage({ carousel: true });
  p.c4.tiles[1].focus();
  press(p, 'ArrowRight');
  assert.equal(focused(p), 'C3');
  assert.deepEqual(p.c4.box.swiper.slid, []);
  assert.ok(p.c4.tiles[2].scrolled, 'scrolled into view as usual');
});

test('arrows: Down and Up never jump to a carousel tile out of sight', () => {
  const p = homePage({ carousel: true });
  p.low.tiles[4].focus();   // B5, over C6
  press(p, 'ArrowDown');
  assert.equal(focused(p), 'C6', 'the tile in sight under it, not C8 or C9 out of sight');
  assert.deepEqual(p.c4.box.swiper.slid, []);
});

test('arrows: with nothing focused, the first press starts at the top left of the page, not in the bar', () => {
  const p = homePage();
  press(p, 'ArrowDown');
  assert.equal(focused(p), 'Hero');
});

test('arrows: the player keeps its own keys', () => {
  const p = homePage({ pathname: '/en-gb/play/0818ff05' });
  p.top.tiles[0].focus();
  const ev = press(p, 'ArrowRight');
  assert.equal(ev.prevented || ev.stopped, false);
  assert.equal(focused(p), 'A1');
});

test('arrows: in a text box Left and Right move the caret, Up and Down still move focus', () => {
  const p = homePage();
  const box = new El('input', { 'aria-label': 'Search box' }, [70, 600, 600, 60]);
  p.document.body.add(box);
  box.focus();
  assert.equal(press(p, 'ArrowLeft').prevented, false);
  assert.equal(focused(p), 'Search box');
  press(p, 'ArrowDown');
  assert.equal(focused(p), 'B1');
});

test('arrows: other keys and modified arrows pass through', () => {
  const p = homePage();
  p.top.tiles[0].focus();
  assert.equal(press(p, 'Enter').prevented, false);
  const shifted = press(p, 'ArrowRight');
  assert.equal(focused(p), 'A2');
  const ev = { key: 'ArrowRight', shiftKey: true, prevented: false, preventDefault() { this.prevented = true; }, stopImmediatePropagation() {} };
  p.handlers[0].fn(ev);
  assert.equal(ev.prevented, false);
  assert.ok(shifted.prevented);
});

test('arrows: Down goes to a tile in sight, not one the row has scrolled off screen', () => {
  const p = homePage();
  // The lower row scrolled along: B1 has left the screen on the left, B2 is the only tile in sight.
  p.low.tiles.forEach((t, i) => { t.rect = [i === 0 ? -400 : 1500 + (i - 1) * 360, 720, 340, 400]; });
  p.top.tiles[0].focus();
  press(p, 'ArrowDown');
  assert.equal(focused(p), 'B2');
});

test('arrows: carousel scroll arrows are never targets, even shown under the mouse; tabs that switch a panel are', () => {
  const p = homePage();
  // The pointer rests on the banner and the rows: Disney+ fades their scroll arrows in.
  p.heroArrow.style.opacity = '1';
  p.top.arrow.style.opacity = '1';
  press(p, 'ArrowDown');
  assert.equal(focused(p), 'Hero', 'the first press, though the arrow strip starts higher up');
  p.top.tiles[4].focus();
  press(p, 'ArrowRight');
  assert.equal(focused(p), 'A6', 'the next tile, not the row arrow over it');

  const tab = new El('button', { role: 'tab', 'aria-controls': 'panel-pixar', 'aria-label': 'Pixar' }, [70, 1150, 200, 60]);
  p.document.body.add(tab);
  p.low.tiles[0].focus();
  press(p, 'ArrowDown');
  assert.equal(focused(p), 'Pixar');
});

// Disney+'s player draws its control bar only for a pointer, never for a key (measured 2026-10-31: a Down key showed
// nothing; a mouse move made by a script showed Back, the timeline and the progress bar).
test('arrows: in the player every key still reaches the player and also nudges a pointer, so its controls show', () => {
  const p = homePage({ pathname: '/en-gb/play/0818ff05' });
  for (const key of ['ArrowRight', 'Enter', ' ']) {
    const ev = press(p, key);
    assert.equal(ev.prevented || ev.stopped, false, key + ' goes on to the player');
  }
  const moves = p.document.body.events || [];
  const mouse = moves.filter(m => m.startsWith('mousemove@'));
  assert.equal(mouse.length, 3, 'one mouse move per key');
  assert.ok(moves.some(m => m.startsWith('pointermove@')), 'and a pointer move');
  assert.notEqual(mouse[0], mouse[1], 'a pixel apart each time, so each one is a movement');
});

test('arrows: away from the player no pointer move is made', () => {
  const p = homePage();
  p.top.tiles[0].focus();
  press(p, 'ArrowRight');
  press(p, 'Enter');
  assert.equal(p.document.body.events, undefined);
});
