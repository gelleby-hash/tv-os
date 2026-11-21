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
  const m = source.match(/ArrowNavScript = """\r?\n([\s\S]*?)\r?\n\s*""";/);
  assert.ok(m, 'ArrowNavScript raw string found in PageScripts.cs');
  assert.ok(m[1].includes('__AFTER_PLAYER__'), 'with the place for FocusAfterPlayer');
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
    this.isConnected = true;
    // Only what the script uses: add, remove, contains.
    const el = this;
    const names = () => (el.attrs.class || '').split(' ').filter(Boolean);
    this.classList = {
      add: (n) => { if (!names().includes(n)) el.attrs.class = names().concat(n).join(' '); },
      remove: (n) => { el.attrs.class = names().filter(x => x !== n).join(' '); },
      contains: (n) => names().includes(n),
    };
  }
  add(...kids) { for (const k of kids) { k.parentElement = this; this.children.push(k); } return this; }
  appendChild(k) { return this.add(k), k; }
  get parentNode() { return this.parentElement; }
  getAttribute(n) { return n in this.attrs ? String(this.attrs[n]) : null; }
  setAttribute(n, v) { this.attrs[n] = String(v); }
  removeAttribute(n) { delete this.attrs[n]; }
  hasAttribute(n) { return n in this.attrs; }
  // As in a browser, an element in something not displayed measures nothing.
  getBoundingClientRect() {
    for (let e = this; e; e = e.parentElement) if (e.style.display === 'none') return { left: 0, top: 0, width: 0, height: 0, right: 0, bottom: 0 };
    const [left, top, width, height] = this.rect; return { left, top, width, height, right: left + width, bottom: top + height };
  }
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
  // The script's item selector, 'video', or [attr="value"] (a FocusAfterPlayer selector).
  querySelectorAll(sel) {
    const attr = sel.match(/^\[([\w-]+)="([^"]*)"\]$/);
    const want = sel === 'video' ? (k) => k.tagName === 'VIDEO' : attr ? (k) => k.attrs[attr[1]] === attr[2] : (k) => k.picked();
    const out = []; const walk = (e) => { for (const k of e.children) { if (want(k)) out.push(k); walk(k); } }; walk(this); return out;
  }
  // As in a browser, only an item, or an element with a tabindex (even -1), takes the focus.
  focus(opts) {
    if (!this.picked() && !this.hasAttribute('tabindex')) return;
    page.document.activeElement = this; this.focusOpts = opts;
  }
  // Records what the script sends it: a pointer as type@x,y, a key as type:key.
  dispatchEvent(e) { (this.events = this.events || []).push(e.key !== undefined ? e.type + ':' + e.key : e.type + '@' + e.clientX + ',' + e.clientY); return true; }
  scrollIntoView(opts) { this.scrolled = opts; }
  click() { this.clicks = (this.clicks || 0) + 1; }
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
  // As Disney+ marks them (2026-11-15): a tab, the one shown selected.
  const pill = new El('a', { href: '/en-gb/browse/disneyplus', 'aria-label': 'Pill', role: 'tab', 'aria-selected': 'true' }, [810, 85, 300, 50]);
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
  return run(doc, pathname, { home, search, pill, hero, heroArrow, top, low, c4 });
}

// Prime Video's title page as measured on 2026-11-14 (1920 x 1080): a channel logo link stretched across the page, a
// row of round buttons, Play, and the synopsis (a span with tabindex 0) to the right of Play and below it.
function titlePage({ afterPlayer = null } = {}) {
  const body = new El('body');
  const doc = { body, documentElement: new El('html'), activeElement: body, fullscreenElement: null, querySelectorAll: (s) => body.querySelectorAll(s),
    elementFromPoint: () => body };
  doc.documentElement.add(body);
  const logo = new El('a', { 'data-testid': 'channelLogo-link', 'aria-label': 'Logo' }, [72, 80, 1776, 35]);
  const trailer = new El('a', { href: '/trailer', 'aria-label': 'Watch trailer' }, [72, 684, 48, 48]);
  const like = new El('button', { 'aria-label': 'Like' }, [192, 684, 48, 48]);
  const play = new El('a', { href: '/play', role: 'button', 'aria-label': 'Play', 'data-testid': 'dp-atf-play-button' }, [72, 748, 348, 62]);
  const adFree = new El('a', { href: '/adfree', 'aria-label': 'Go ad free' }, [72, 824, 348, 62]);
  const synopsis = new El('span', { tabindex: 0, 'aria-label': 'Synopsis' }, [448, 815, 950, 75]);
  body.add(logo, trailer, like, play, adFree, synopsis);
  // The player, there all along and shown over the page while a video plays: a fixed layer that takes the focus
  // (tabindex -1), the video filling it, and an element inside that Prime focuses when it opens the player itself.
  const layer = new El('div', { id: 'dv-web-player', tabindex: -1 }, [0, 0, 0, 0], { position: 'fixed', display: 'none' });
  const video = new El('video', {}, [0, 0, 0, 0], { position: 'absolute' });
  const inside = new El('div', { tabindex: -1, 'aria-label': 'Rating' }, [0, 0, 0, 0]);
  body.add(layer.add(video, inside));
  const p = run(doc, '/detail/0FKWLNTAYMFTSZ7TRZSPLG84NM', { logo, trailer, play, synopsis, layer, video, inside }, undefined, afterPlayer);
  p.showPlayer = () => { layer.style.display = 'block'; layer.rect = video.rect = [0, 0, 1920, 1080]; };
  p.hidePlayer = () => { layer.style.display = 'none'; layer.rect = video.rect = [0, 0, 0, 0]; };
  return p;
}

// Prime Video's home page as measured on 2026-11-15 in Edge at the mini PC's 1280 x 720 (300% scaling), signed in: a
// menu bar fixed to the screen, a banner of five slides stacked in one place, each in a list item at opacity 0 but the
// one shown, each with a link stretched behind its title and buttons (tabindex -1, no focus ring), then rows of tiles
// (links with tabindex -1) with a small "See more" link between the rows.
function primeHome({ shown = 'Pacers' } = {}) {
  const body = new El('body');
  const doc = { body, documentElement: new El('html'), activeElement: body, fullscreenElement: null, querySelectorAll: (s) => body.querySelectorAll(s),
    elementFromPoint: () => body };
  doc.documentElement.add(body);
  const bar = new El('nav', {}, [0, 0, 1280, 60], { position: 'fixed' });
  const sports = new El('a', { href: '/sports', 'data-testid': 'pv-nav-home-sports', 'aria-label': 'Sports' }, [391, 9, 74, 42]);
  bar.add(new El('a', { href: '/home', 'aria-label': 'Home' }, [149, 9, 69, 42]), sports,
    new El('a', { href: '/prime', 'aria-label': 'Prime' }, [548, 9, 71, 42]));
  const slide = (name, x, title, buttons) => {
    const li = new El('li', {}, [x, 0, 1280, 480], { opacity: name === shown ? '1' : '0' });
    const image = new El('a', { href: '/detail/' + name, 'data-testid': 'image-link', tabindex: -1, 'aria-label': name + ' banner' }, [x, 0, 1280, 480]);
    const link = new El('a', { href: '/detail/' + name, tabindex: -1, 'aria-label': name + ' title' }, title);
    li.add(image, link, ...buttons.map(([tag, label, rect, testid]) => new El(tag, Object.assign({ 'aria-label': label + ' ' + name }, tag === 'a' ? { href: '/x' } : {}, testid ? { 'data-testid': testid } : {}), rect)));
    return { li, image, link };
  };
  const slides = {
    Girls: slide('Girls', 0, [44, 233, 461, 129], [['button', 'Rent', [44, 404, 151, 86]], ['button', 'Buy', [211, 404, 151, 86]],
      ['button', 'Watchlist', [379, 417, 61, 61]], ['a', 'Details', [444, 417, 61, 61]]]),
    Rocky: slide('Rocky', -20, [4, 264, 461, 88], [['a', 'Watch now', [4, 428, 192, 62], 'play'], ['button', 'Watchlist', [220, 429, 61, 61]],
      ['a', 'Details', [285, 429, 61, 61]]]),
    Pacers: slide('Pacers', 0, [44, 334, 461, 33], [['a', 'Details', [44, 428, 157, 62], 'details-cta'], ['button', 'Watchlist', [217, 429, 61, 61]]]),
  };
  const banner = new El('ul', {}, [0, 0, 1280, 480]).add(...Object.values(slides).map(s => s.li),
    new El('button', { 'data-testid': 'left-arrow', 'aria-label': 'Move back' }, [6, 253, 33, 33]),
    new El('button', { 'data-testid': 'right-arrow', 'aria-label': 'Move forward' }, [1227, 253, 33, 33]));
  const row = (y, names) => names.map((n, i) => new El('a', { href: '/detail/' + n, tabindex: -1, 'aria-label': n }, [44 + i * 304, y, 280, 158]));
  const top = row(621, ['LOL', 'Love in Disguise', 'The Grand Tour', 'Pingu', 'Helluva Boss']);
  const seeMore = new El('a', { href: '/collection/action', 'data-testid': 'see-more', 'aria-label': 'See more' }, [342, 817, 114, 24]);
  const low = row(855, ['Project Hail Mary', 'The Mongoose', 'Shelter', 'The Mask', 'Heads of State']);
  body.add(bar, banner, ...top, seeMore, ...low);
  return run(doc, '/region/eu/storefront', { sports, slides, top, low, seeMore }, [1280, 720]);
}

// afterPlayer: the app's FocusAfterPlayer selector, put in the script as the launcher does (PageScripts.ArrowNav).
function run(doc, pathname, parts, [width, height] = [1920, 1080], afterPlayer = null) {
  doc.head = new El('head');
  doc.documentElement.add(doc.head);
  doc.createElement = (tag) => new El(tag);
  doc.querySelector = (s) => doc.body.querySelectorAll(s)[0] || null;
  const handlers = [];
  const warnings = [];
  const scrolls = [];
  const ctx = vm.createContext({
    document: doc,
    window: { addEventListener: (type, fn, capture) => handlers.push({ type, fn, capture }), scrollBy: (o) => scrolls.push(o),
      chrome: { webview: { postMessage: (m) => page.posted.push(m) } } },
    location: { pathname },
    innerWidth: width,
    innerHeight: height,
    getComputedStyle: (e) => e.style,
    MouseEvent: class { constructor(type, init) { this.type = type; Object.assign(this, init); } },
    KeyboardEvent: class { constructor(type, init) { this.type = type; Object.assign(this, init); } },
    PointerEvent: class { constructor(type, init) { this.type = type; Object.assign(this, init); } },
    console: { warn: (...a) => warnings.push(a) },
    Math,
    scrollY: 0,
    // A clock that moves on a second at each look, so presses are far apart unless a test sets page.tick.
    performance: { now: () => (page.clock += page.tick) },
    // Kept until a test runs them with page.runTimers().
    setTimeout: (fn) => { page.timers.push(fn); return page.timers.length; },
  });
  page = Object.assign({ document: doc, handlers, warnings, scrolls, clock: 0, tick: 1000, timers: [], posted: [] }, parts);
  page.runTimers = () => { const t = page.timers.splice(0); for (const fn of t) fn(); };
  vm.runInContext(script.replace('__AFTER_PLAYER__', JSON.stringify(afterPlayer)), ctx, { filename: 'ArrowNav' });
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
  const keys = p.handlers.filter(h => h.type === 'keydown');
  assert.equal(keys.length, 1);
  assert.equal(keys[0].capture, true);
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
  p.handlers.find(h => h.type === 'keydown').fn(ev);
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

test('arrows: away from the player no pointer nudge is made', () => {
  const p = homePage();
  p.top.tiles[0].focus();
  press(p, 'ArrowRight');
  press(p, 'Enter');
  assert.ok(!(p.document.body.events || []).some(m => m.startsWith('pointermove@')));
});

test('arrows: Down from an item stretched across the page goes to the nearest item below, not the one under its middle', () => {
  const p = titlePage();
  p.logo.focus();
  press(p, 'ArrowDown');
  assert.equal(focused(p), 'Like', 'the row of buttons, the nearest below; not the synopsis under the middle of the logo link');
  press(p, 'ArrowDown');
  assert.equal(focused(p), 'Play');
});

// Prime Video plays in a layer fixed over its title page, at the same address (measured 2026-11-14): the arrows took
// focus to the cast links behind the video, and the player never saw them.
test('arrows: a video filling the screen in a fixed layer is the player, which keeps its keys', () => {
  const p = titlePage();
  p.showPlayer();
  p.inside.focus();
  const ev = press(p, 'ArrowRight');
  assert.equal(ev.prevented || ev.stopped, false, 'Right goes on to the player');
  assert.equal(focused(p), 'Rating', 'and focus stays where it was, inside the player');
  assert.ok((p.document.body.events || []).some(m => m.startsWith('mousemove@')), 'a pointer nudge, as in any player');
});

// Prime's player listens for keys only with the focus inside it. Started by loading a play address (?autoplay=1) the
// focus stays on the page body, so Space, the remote's OK and the launcher's own pause press on Home all reached
// nothing, and the video played on behind the home screen (measured 2026-11-14). Prime takes a key a script sends.
test('arrows: a key while the focus is outside the player layer moves the focus into it and goes to the player there', () => {
  const p = titlePage();
  p.showPlayer();
  for (const key of [' ', 'Enter', 'ArrowRight']) {
    p.document.activeElement = p.document.body;
    const ev = press(p, key);
    assert.ok(ev.prevented && ev.stopped, key + ': the copy on the body goes no further');
    assert.equal(p.document.activeElement, p.layer, key + ': the focus is in the player now');
    assert.equal(p.layer.events.at(-1), 'keydown:' + key, key + ': the player gets the key');
  }
  assert.equal(p.layer.focusOpts.preventScroll, true);
});

test('arrows: a player layer that cannot take the focus is left alone, and the key goes on as before', () => {
  const p = titlePage();
  delete p.layer.attrs.tabindex;
  p.showPlayer();
  const ev = press(p, ' ');
  assert.equal(ev.prevented || ev.stopped, false);
  assert.equal(p.document.activeElement, p.document.body);
  assert.equal(p.layer.events, undefined);
});

test('arrows: a video that is hidden, or not fixed over the page (a trailer on a home page), is not the player', () => {
  const p = titlePage();
  p.trailer.focus();
  assert.ok(press(p, 'ArrowDown').prevented, 'the player layer is there but hidden');
  assert.equal(focused(p), 'Play');
  p.layer.style.position = 'static';
  p.layer.style.display = 'block';
  p.layer.rect = p.video.rect = [0, 0, 1920, 1080];
  p.video.style.position = 'static';
  assert.ok(press(p, 'ArrowUp').prevented, 'a big video in the page itself');
  assert.equal(focused(p), 'Like', 'the button over the middle of Play');
});

// On the mini PC Down from a tile went to the next row's "See more" link, and from a banner's title to its small
// Watchlist button, skipping Watch now (2026-11-15): from the 2026-11-14 scoring on, whatever lay across from the
// current item counted as straight ahead, so the nearest edge won.
test('arrows: Down from a tile goes to the tile under it, not the small link between the rows', () => {
  const p = primeHome();
  p.top[1].focus();
  press(p, 'ArrowDown');
  assert.equal(focused(p), 'The Mongoose');
  press(p, 'ArrowUp');
  assert.equal(focused(p), 'Love in Disguise');
});

test('arrows: Down from a banner title goes to the wide Watch now button under it, not the small button beside that', () => {
  const p = primeHome({ shown: 'Rocky' });
  p.slides.Rocky.link.focus();
  press(p, 'ArrowDown');
  assert.equal(focused(p), 'Watch now Rocky');
});

test('arrows: an item in a part of the page at opacity 0 (a banner slide not shown) is never chosen', () => {
  const p = primeHome();
  p.sports.focus();
  press(p, 'ArrowDown');
  assert.equal(focused(p), 'Pacers title', 'the slide shown, not a button of a slide stacked under it');
});

test('arrows: a link stretched behind other items (a banner image under its buttons) is never chosen, so the focus never vanishes', () => {
  const p = primeHome();
  press(p, 'ArrowDown');
  assert.ok(!focused(p).endsWith(' banner'), 'not the first press either: ' + focused(p));
  // With the banner just under the menu bar, the banner image is the nearest thing below the bar.
  p.slides.Pacers.image.rect = [0, 56, 1280, 480];
  p.sports.focus();
  press(p, 'ArrowDown');
  assert.equal(focused(p), 'Pacers title');
  // A tile is not a backdrop: it holds nothing, and the link of a tile holding its own button is still a target.
  const own = new El('button', { 'aria-label': 'Own button' }, [400, 880, 40, 40]);
  p.low[1].add(own);
  p.top[1].focus();
  press(p, 'ArrowDown');
  assert.equal(focused(p), 'The Mongoose');
});


// Scrolled up, Prime's tiles pass under the menu bar fixed to the screen, so a menu link lies inside a tile's box
// (measured 2026-11-15): that does not make the tile a backdrop.
test('arrows: a tile scrolled under the fixed menu bar is still a target, though a menu link lies inside its box', () => {
  const p = primeHome();
  // Scrolled that far, the banner and the "See more" link are out of the way.
  Object.values(p.slides).forEach(s => { s.li.style.opacity = '0'; });
  p.seeMore.style.display = 'none';
  p.top.forEach(t => { t.rect = [t.rect[0], -100, 280, 158]; });
  p.low.forEach(t => { t.rect = [t.rect[0], 140, 280, 158]; });
  p.low[1].focus();
  press(p, 'ArrowUp');
  assert.equal(focused(p), 'Love in Disguise');
});

// Prime's title page under the buttons (measured 2026-11-15 at 1920 x 1080): the season selector, a checkbox moved
// far off the screen and drawn by its label, then the Episodes, Related and Details tabs.
function seasonPage() {
  const p = titlePage();
  const label = new El('label', { for: 'season', 'aria-hidden': 'true' }, [72, 966, 154, 48]);
  const box = new El('input', { id: 'season', role: 'button', 'aria-haspopup': 'listbox', 'aria-label': 'Season selector' }, [-99927, 966, 13, 13]);
  box.type = 'checkbox';
  box.labels = [label];
  box.click = function () { this.clicks = (this.clicks || 0) + 1; };
  const episodes = new El('button', { role: 'tab', 'aria-label': 'Episodes' }, [72, 1014, 82, 66]);
  p.document.body.add(new El('div', {}, [72, 966, 154, 48]).add(box, label), episodes);
  return Object.assign(p, { label, box, episodes });
}

test('arrows: Up from a tab under a wide button goes to that button, not past it to the small buttons above', () => {
  const p = titlePage();
  const episodes = new El('button', { role: 'tab', 'aria-label': 'Episodes' }, [72, 1014, 82, 66]);
  p.document.body.add(episodes);
  episodes.focus();
  press(p, 'ArrowUp');
  assert.equal(focused(p), 'Go ad free');
});

test('arrows: a checkbox moved off the screen and drawn by its label is reached by its label, and OK ticks it', () => {
  const p = seasonPage();
  p.episodes.focus();
  press(p, 'ArrowUp');
  assert.equal(focused(p), 'Season selector');
  assert.ok(p.label.scrolled, 'the label is scrolled into view, not the box far off the screen');
  const ok = press(p, 'Enter');
  assert.equal(p.box.clicks, 1, 'OK ticks it, as Space would');
  assert.ok(ok.prevented && ok.stopped);
  press(p, 'ArrowDown');
  assert.equal(focused(p), 'Episodes');
  press(p, 'Enter');
  assert.equal(p.box.clicks, 1, 'OK anywhere else is left to the page');
});

// A mouse pointer resting over the page: as the arrows scroll the page, Prime Video's tiles pass under it and open
// their hover cards, which took the focus from the tile the arrows had reached (to the page body, so the next press
// started again at the top) and made the rows grow (measured 2026-11-15 in the dev launcher and in Edge).
const mouse = (p, x, y) => { for (const h of p.handlers) if (h.type === 'mousemove') h.fn({ screenX: x, screenY: y, isTrusted: true }); };
const ignoresPointer = (p) => p.document.documentElement.classList.contains('tv-keys')
  && p.document.head.children.some(s => /html\.tv-keys \* \{ pointer-events: none !important; \}/.test(s.textContent || ''));

test('arrows: while the arrows are in use the page ignores a mouse pointer resting over it', () => {
  const p = primeHome();
  assert.equal(ignoresPointer(p), false, 'not before any key');
  press(p, 'ArrowDown');
  assert.equal(ignoresPointer(p), true);
  press(p, 'ArrowDown');
  assert.equal(p.document.head.children.length, 1, 'one style, however many keys');
});

test('arrows: moving the mouse gives the page the pointer back; a mouse event that does not move does not', () => {
  const p = primeHome();
  mouse(p, 500, 400);
  press(p, 'ArrowDown');
  mouse(p, 500, 400);
  assert.equal(ignoresPointer(p), true, 'the pointer did not move');
  mouse(p, 503, 400);
  assert.equal(p.document.documentElement.classList.contains('tv-keys'), false);
  press(p, 'ArrowDown');
  assert.equal(ignoresPointer(p), true, 'and the arrows take it again');
});

test('arrows: the player keeps the pointer, which shows its controls', () => {
  const p = titlePage();
  p.trailer.focus();
  press(p, 'ArrowDown');
  assert.equal(ignoresPointer(p), true);
  p.showPlayer();
  press(p, ' ');
  assert.equal(p.document.documentElement.classList.contains('tv-keys'), false);
});

// Prime Video adds rows only as the page nears its end. Pressing quickly, Down from the last row there was went to
// the footer's link some 3,900 px further down, out of sight, and the rows then loaded between (measured 2026-11-15).
test('arrows: Up and Down never jump more than a screen; with nothing nearer the page scrolls on and the focus stays', () => {
  const p = primeHome();
  const footer = new El('a', { href: '/terms', 'aria-label': 'Terms' }, [550, 4500, 171, 19]);
  p.document.body.add(footer);
  p.low[1].focus();
  const ev = press(p, 'ArrowDown');
  assert.equal(focused(p), 'The Mongoose', 'the focus stays');
  assert.ok(ev.prevented, 'and the page does not scroll by itself');
  assert.equal(p.scrolls.length, 1);
  assert.ok(p.scrolls[0].top > 0 && p.scrolls[0].top <= 720 / 2, 'the page is scrolled down a little: ' + JSON.stringify(p.scrolls[0]));
  footer.rect = [550, 1200, 171, 19];
  press(p, 'ArrowDown');
  assert.equal(focused(p), 'Terms', 'within a screen it is reached as usual');
  assert.equal(p.scrolls.length, 1);
});


// Down reached the footer while it was the end of the page; Prime then added rows above it, so the focus sat some
// 3,400 px below the screen and the next presses only scrolled toward it (measured 2026-11-15).
test('arrows: with the focus far out of sight (the page grew under it), a press starts again from what is on screen', () => {
  const fresh = primeHome();
  press(fresh, 'ArrowDown');
  const p = primeHome();
  const footer = new El('a', { href: '/terms', 'aria-label': 'Terms' }, [550, 4500, 171, 19]);
  p.document.body.add(footer);
  footer.focus();
  press(p, 'ArrowDown');
  assert.equal(focused(p), focused(fresh), 'as the first press on that screen');
  assert.equal(p.scrolls.length, 0);
});

test('arrows: an item just out of sight, such as the next tile of a row while it slides in, is still where a press starts', () => {
  const p = homePage();
  p.top.tiles[5].focus();   // A6, off the right edge of the screen
  press(p, 'ArrowRight');
  assert.equal(focused(p), 'A7');
});

// A held arrow repeats every 30 ms or so, and quick presses come faster than a smooth scroll: the focus ran ahead of
// the screen, and the rule that starts again from the screen when the focus is far out of sight sent Up round in a
// circle for two seconds (the owner's test in the dev launcher, 2026-11-15 17:37:31).
test('arrows: the focus running ahead of the scroll is still where the next press starts', () => {
  const p = primeHome();
  const more = [1089, 1323, 1557].map((y, i) => new El('a', { href: '/r' + i, tabindex: -1, 'aria-label': 'Row ' + (i + 3) }, [348, y, 280, 158]));
  p.document.body.add(...more);
  p.top[1].focus();
  for (let i = 0; i < 4; i++) press(p, 'ArrowDown');   // the fake page never scrolls: Row 5 is far below the screen
  assert.equal(focused(p), 'Row 5');
});

test('arrows: the page growing under the focus the arrows put there still counts as out of sight', () => {
  const fresh = primeHome();
  press(fresh, 'ArrowDown');
  const p = primeHome();
  const footer = new El('a', { href: '/terms', 'aria-label': 'Terms' }, [400, 1100, 171, 19]);
  p.document.body.add(footer);
  p.low[1].focus();
  press(p, 'ArrowDown');
  assert.equal(focused(p), 'Terms', 'the end of the page, for now');
  footer.rect = [400, 4500, 171, 19];   // rows added above it
  press(p, 'ArrowDown');
  assert.equal(focused(p), focused(fresh));
});

test('arrows: a lone press scrolls smoothly; a quick one, or a held key, scrolls at once so the screen keeps up', () => {
  const p = homePage();
  p.top.tiles[0].focus();
  press(p, 'ArrowRight');
  assert.equal(p.top.tiles[1].scrolled.behavior, 'smooth');
  p.tick = 120;
  press(p, 'ArrowRight');
  assert.equal(p.top.tiles[2].scrolled.behavior, 'instant');
  p.tick = 1000;
  press(p, 'ArrowRight');
  assert.equal(p.top.tiles[3].scrolled.behavior, 'smooth');
});


// Prime's episode cards (measured 2026-11-15 on The Legend of Vox Machina at 1920 x 1080): each one's only target is
// its play link, laid over the picture at opacity 0 (Prime fades it in under the pointer). Its tiles carry a hover
// button at opacity 0 too, which is not a target.
test('arrows: a link at opacity 0 laid over a card is a target; a button at opacity 0 is not', () => {
  const p = seasonPage();
  const play = new El('a', { href: '/detail/x?autoplay=1', 'data-testid': 'episodes-playbutton', role: 'button', 'aria-label': 'Play episode 1' }, [72, 1168, 422, 238], { opacity: '0' });
  const hover = new El('button', { 'aria-label': 'Hover card' }, [518, 1168, 422, 238], { opacity: '0' });
  p.document.body.add(new El('li', {}, [72, 1144, 422, 406]).add(play), hover);
  p.episodes.focus();
  press(p, 'ArrowDown');
  assert.equal(focused(p), 'Play episode 1');
  press(p, 'ArrowRight');
  assert.equal(focused(p), 'Play episode 1', 'not the hover button beside it');
});

// With nothing under the Episodes tab that the arrows saw, Down scrolled the page until the tab lay above the menu
// bar fixed to the screen, and then went into the bar; the next Down came back to the page (the owner's test,
// 2026-11-15 17:42:31).
test('arrows: Down never goes from the page into a bar fixed to the screen', () => {
  const p = primeHome();
  const walk = (e) => { for (const k of e.children) { if (k.style.position !== 'fixed') { k.rect = [k.rect[0], k.rect[1] - 3000, k.rect[2], k.rect[3]]; walk(k); } } };
  walk(p.document.body);
  p.top[1].rect = [348, -213, 280, 158];   // scrolled up past the bar
  p.top[1].focus();
  press(p, 'ArrowDown');
  assert.equal(focused(p), 'Love in Disguise');
  assert.equal(p.scrolls.length, 1, 'the page moves on instead');
  // Down from the bar into the page: the test of the banner slides at opacity 0 (Sports, then the Pacers title).
});

// Prime's title page fades its details (title, buttons, season selector) out a few seconds after its trailer starts,
// and brings them back on activity. The arrows took every key before Prime saw it, so the details stayed hidden and
// at opacity 0 nothing could take the focus (owner, 2026-11-15; a scripted mouse move brings them back, measured).
test('arrows: every arrow tells the page someone is there, with a scripted mouse move that keeps the remote mode', () => {
  const p = primeHome();
  mouse(p, 500, 400);
  p.top[1].focus();
  press(p, 'ArrowDown');
  assert.ok((p.document.body.events || []).some(m => m.startsWith('mousemove@')), 'a mouse move on the page');
  for (const h of p.handlers) if (h.type === 'mousemove') h.fn({ screenX: 0, screenY: 0, isTrusted: false });
  assert.equal(ignoresPointer(p), true, 'a scripted move is not the mouse moving');
});

// With an episode focused, Prime opens its focus card, which puts the episode's Download button in the picture's top
// right corner: Up and Down from the episode went to it (owner's test, 2026-11-15 18:39).
function episodesPage() {
  const p = seasonPage();
  const related = new El('button', { role: 'tab', 'aria-label': 'Related' }, [182, 1014, 71, 66]);
  const details = new El('button', { role: 'tab', 'aria-label': 'Details' }, [281, 1014, 65, 66]);
  p.episodes.attrs['aria-selected'] = 'true';
  related.attrs['aria-selected'] = 'false';
  details.attrs['aria-selected'] = 'false';
  const ep = (n, x, y) => new El('li', {}, [x, y - 24, 426, 406]).add(
    new El('a', { href: '/play/' + n, 'data-testid': 'episodes-playbutton', 'aria-label': 'Episode ' + n }, [x, y, 426, 240]),
    new El('a', { href: 'web+primevideo://' + n, 'aria-label': 'Download ' + n }, [x + 370, y + 8, 48, 48], { display: 'none' }));
  p.document.body.add(related, details, ep(1, 72, 1168), ep(2, 522, 1168), ep(5, 72, 1600), ep(6, 522, 1600));
  // Only the episode in focus shows its Download button, in its focus card.
  const all = (label) => p.document.body.querySelectorAll('*').find(e => e.getAttribute('aria-label') === label)
    || [...function* walk(e) { for (const k of e.children) { yield k; yield* walk(k); } }(p.document.body)].find(e => e.getAttribute('aria-label') === label);
  p.open = (n) => { all('Download ' + n).style.display = 'block'; all('Episode ' + n).focus(); };
  return p;
}

test('arrows: an item laid over the one in focus (a button in its corner) is not above, below or beside it', () => {
  const p = episodesPage();
  const find = (label) => p.document.body.querySelectorAll('*').find(e => e.getAttribute('aria-label') === label);
  p.open(1);
  press(p, 'ArrowUp');
  assert.notEqual(focused(p), 'Download 1');
  p.open(1);
  press(p, 'ArrowRight');
  assert.equal(focused(p), 'Episode 2', 'not its own Download button on the way');
  press(p, 'ArrowDown');
  assert.equal(focused(p), 'Episode 6');
});

test('arrows: Up or Down into a row of tabs lands on the selected tab', () => {
  const p = episodesPage();
  const find = (label) => p.document.body.querySelectorAll('*').find(e => e.getAttribute('aria-label') === label);
  p.open(2);
  press(p, 'ArrowUp');
  assert.equal(focused(p), 'Episodes');
  press(p, 'ArrowRight');
  assert.equal(focused(p), 'Related', 'along the tabs, one at a time as before');
});

// The remote's 3-lines key (the launcher sends 'tv-menu'): Prime Video's hover card, with Resume, the trailer, its
// watchlist and hide buttons, opens only under a mouse resting on a tile (owner, 2026-11-15). Measured: a scripted
// hover opens it within 200 ms, and a scripted leave closes it.
function hoverPage() {
  const p = primeHome();
  const tile = p.top[1];   // Love in Disguise, 348,621 280x158
  const card = [
    new El('a', { href: '/detail/love', 'aria-label': 'Card picture' }, [330, 600, 320, 180]),
    new El('a', { href: '/play/love', 'aria-label': 'Resume' }, [368, 790, 200, 50]),
    new El('button', { 'aria-label': 'Trailer' }, [580, 790, 50, 50]),
  ];
  for (const c of card) c.style.display = 'none';
  p.document.body.add(...card, new El('a', { href: '/beside', 'aria-label': 'Beside' }, [100, 790, 100, 50]));
  tile.dispatchEvent = function (e) {
    (this.events = this.events || []).push(e.type);
    if (e.type === 'mouseover') for (const c of card) c.style.display = 'block';
    if (e.type === 'mouseleave') for (const c of card) c.style.display = 'none';
    return true;
  };
  return Object.assign(p, { tile, card });
}
const menu = (p) => { for (const h of p.handlers) if (h.type === 'tv-menu') h.fn(new Event('tv-menu')); };

test('arrows: the menu key hovers the item in focus and moves the focus into the card it opens', () => {
  const p = hoverPage();
  p.tile.focus();
  menu(p);
  assert.ok(['pointerover', 'mouseover', 'mouseenter', 'mousemove'].every(t => p.tile.events.includes(t)), p.tile.events.join());
  p.runTimers();
  assert.equal(focused(p), 'Resume', 'the first of the card, not its picture over the tile');
  press(p, 'ArrowRight');
  assert.equal(focused(p), 'Trailer');
  assert.ok(!p.tile.events.includes('mouseleave'), 'moving inside the card keeps it open');
});

test('arrows: the menu key again closes the card and gives the focus back to the item', () => {
  const p = hoverPage();
  p.tile.focus();
  menu(p);
  p.runTimers();
  menu(p);
  assert.ok(['mouseout', 'mouseleave', 'pointerleave'].every(t => p.tile.events.includes(t)));
  assert.equal(focused(p), 'Love in Disguise');
});

test('arrows: an arrow out of the card closes it', () => {
  const p = hoverPage();
  p.tile.focus();
  menu(p);
  p.runTimers();
  press(p, 'ArrowLeft');
  assert.equal(focused(p), 'Beside');
  assert.ok(p.tile.events.includes('mouseleave'));
});

test('arrows: the menu key on an item with no hover card leaves things as they were', () => {
  const p = primeHome();
  p.top[0].focus();
  menu(p);
  p.runTimers();
  assert.equal(focused(p), 'LOL');
  p.document.activeElement = p.document.body;
  menu(p);
  p.runTimers();
  assert.equal(p.document.activeElement, p.document.body, 'nothing focused: nothing to hover');
});

// Prime Video's player as measured on 2026-11-15 at 1920 x 1080 (Vox Machina S1, near an episode's end): the layer
// fixed over the title page, its controls drawn only after pointer activity (none in the page while idle), a top bar,
// a bottom row (skip back, pause, skip forward), Next Episode at the bottom right and the Next Up card's Hide above
// it. In the player Prime takes Left and Right to seek and Up and Down for its own volume, so no key reached a button.
function playerPage(opts) {
  const p = titlePage(opts);
  p.showPlayer();
  const controls = new El('div', {}, [0, 0, 1920, 1080], { display: 'none' });
  const b = (label, rect) => new El('button', { 'aria-label': label }, rect);
  const close = b('Close player', [38, 29, 61, 61]), subtitles = b('Turn on subtitles', [1517, 29, 61, 61]);
  const back10 = b('Skip back 10 seconds', [844, 983, 61, 61]), pause = b('Pause', [919, 973, 82, 82]);
  const fwd10 = b('Skip forward 10 seconds', [1015, 983, 61, 61]), nextEp = b('Next Episode', [1694, 989, 174, 49]);
  const hide = b('Hide', [1823, 813, 58, 23]);
  p.layer.add(controls.add(close, subtitles, back10, pause, fwd10, nextEp, hide));
  Object.assign(p, { controls, close, subtitles, back10, pause, fwd10, nextEp, hide });
  p.showControls = () => { controls.style.display = 'block'; };
  p.inside.focus();
  return p;
}
const kept = (ev) => ev.prevented && ev.stopped;

test('player: Up or Down picks the button nearest the bottom middle, and the player never sees the key', () => {
  for (const key of ['ArrowDown', 'ArrowUp']) {
    const p = playerPage();
    p.showControls();
    const ev = press(p, key);
    assert.ok(kept(ev), key + ' is not the player volume');
    assert.equal(focused(p), 'Pause', key);
  }
});

test('player: with the controls not drawn yet, Up or Down shows them and picks once they are there', () => {
  const p = playerPage();
  const ev = press(p, 'ArrowDown');
  assert.ok(kept(ev));
  assert.ok((p.document.body.events || []).some(m => m.startsWith('mousemove@')), 'the pointer nudge that brings the controls');
  assert.equal(focused(p), 'Rating', 'nothing to pick yet');
  p.showControls();
  p.runTimers();
  assert.equal(focused(p), 'Pause');
});

test('player: with a button picked, the arrows move between the buttons and the player never sees them', () => {
  const p = playerPage();
  p.showControls();
  press(p, 'ArrowDown');
  const steps = [['ArrowRight', 'Skip forward 10 seconds'], ['ArrowRight', 'Next Episode'], ['ArrowUp', 'Hide'],
    ['ArrowDown', 'Next Episode'], ['ArrowLeft', 'Skip forward 10 seconds'], ['ArrowLeft', 'Pause'], ['ArrowLeft', 'Skip back 10 seconds']];
  for (const [key, want] of steps) {
    const ev = press(p, key);
    assert.ok(kept(ev), key);
    assert.equal(focused(p), want, key);
  }
});

test('player: OK presses the picked button, and the player never sees the key', () => {
  const p = playerPage();
  p.showControls();
  press(p, 'ArrowDown');
  press(p, 'ArrowRight');
  press(p, 'ArrowRight');
  const ev = press(p, 'Enter');
  assert.ok(kept(ev));
  assert.equal(p.nextEp.clicks, 1);
});

test('player: Up past the top row or Down past the bottom row goes back to watching, where Left and Right seek', () => {
  for (const [start, key] of [['close', 'ArrowUp'], ['pause', 'ArrowDown']]) {
    const p = playerPage();
    p.showControls();
    p[start].focus();
    assert.ok(kept(press(p, key)), key);
    assert.equal(p.document.activeElement, p.layer, key + ': the focus is on the player again');
    const ev = press(p, 'ArrowLeft');
    assert.equal(ev.prevented || ev.stopped, false, 'Left goes to the player, which seeks');
  }
});

test("player: while nothing is picked, Left, Right and OK are the player's; once the controls fade, so are they again", () => {
  const p = playerPage();
  p.showControls();
  for (const key of ['ArrowLeft', 'ArrowRight', 'Enter']) assert.equal(press(p, key).prevented, false, key);
  press(p, 'ArrowDown');
  assert.equal(focused(p), 'Pause');
  p.controls.style.display = 'none';
  const ev = press(p, 'ArrowRight');
  assert.equal(ev.prevented || ev.stopped, false, 'the controls faded: Right seeks');
  assert.equal(p.pause.clicks, undefined);
});

// Back from Prime's player (the launcher's Escape, PlayerEscape) left the focus on the page's body, so the next arrow
// started again at the top of the title page (measured 2026-11-21); the owner wants it on the Resume button.
const RESUME = '[data-testid="dp-atf-play-button"]';
const closedByBack = (p, { blurred = true } = {}) => {
  const ev = press(p, 'Escape');
  p.hidePlayer();
  // Chromium moves the focus off an element that is no longer shown; the first check may come before it does.
  if (blurred) p.document.activeElement = p.document.body;
  p.runTimers();
  return ev;
};

test('after the player: Back that closes the player layer puts the focus on the page button FocusAfterPlayer names', () => {
  for (const blurred of [true, false]) {
    const p = titlePage({ afterPlayer: RESUME });
    p.showPlayer();
    p.inside.focus();
    const ev = closedByBack(p, { blurred });
    assert.equal(ev.prevented || ev.stopped, false, 'Escape still goes to the player, which closes');
    assert.equal(focused(p), 'Play', blurred ? 'from the body' : 'from the hidden player');
    assert.equal(p.play.focusOpts.preventScroll, true);
    assert.equal(p.play.scrolled, null, 'on screen already: the page stays where it is');
  }
});

// Started from an episode card further down, the page was left scrolled there; scrolled only as far as showed it,
// Resume ended at the top edge of the screen, under Prime's fixed menu bar (measured 2026-11-21).
test('after the player: a button off the screen, or at its very edge, is scrolled to the middle as the arrows do', () => {
  for (const top of [-600, 1, 1500]) {
    const p = titlePage({ afterPlayer: RESUME });
    p.play.rect = [72, top, 348, 62];
    p.showPlayer();
    p.inside.focus();
    closedByBack(p);
    assert.equal(focused(p), 'Play', 'top ' + top);
    assert.equal(p.play.scrolled.block, 'center', 'top ' + top);
  }
});

// Prime redraws its title page after the player closes and drops the focus to the body again, about 100 ms after the
// script had put it on Resume (measured 2026-11-21: Resume at 281 ms, BODY at 361 ms after Escape).
test('after the player: a focus the page drops again within the next second or so is put back', () => {
  const p = titlePage({ afterPlayer: RESUME });
  p.showPlayer();
  p.inside.focus();
  closedByBack(p);
  assert.equal(focused(p), 'Play');
  p.document.activeElement = p.document.body;
  p.runTimers();
  assert.equal(focused(p), 'Play', 'put back');
  for (let i = 0; i < 5; i++) p.runTimers();
  assert.equal(p.timers.length, 0, 'and then it stops looking');
});

test('after the player: once the viewer moves on from it, it is left alone', () => {
  const p = titlePage({ afterPlayer: RESUME });
  p.showPlayer();
  p.inside.focus();
  closedByBack(p);
  press(p, 'ArrowDown');
  const moved = focused(p);
  assert.notEqual(moved, 'Play');
  p.runTimers();
  assert.equal(focused(p), moved);
  assert.equal(p.timers.length, 0);
});

test('after the player: OK on a picked button that closes the player (Close player) does the same', () => {
  const p = playerPage({ afterPlayer: RESUME });
  p.showControls();
  p.close.focus();
  press(p, 'Enter');
  assert.equal(p.close.clicks, 1);
  p.hidePlayer();
  p.document.activeElement = p.document.body;
  p.runTimers();
  assert.equal(focused(p), 'Play');
});

test('after the player: without FocusAfterPlayer the focus is left as the page leaves it', () => {
  const p = titlePage();
  p.showPlayer();
  p.inside.focus();
  closedByBack(p);
  assert.equal(p.document.activeElement, p.document.body);
  assert.equal(p.timers.length, 0);
});

test('after the player: a player layer still open after Escape is waited for a moment, then left alone', () => {
  const p = titlePage({ afterPlayer: RESUME });
  p.showPlayer();
  p.inside.focus();
  press(p, 'Escape');
  for (let i = 0; i < 5; i++) p.runTimers();
  assert.equal(p.timers.length, 0, 'it stops asking');
  assert.equal(focused(p), 'Rating');
  p.hidePlayer();
  p.runTimers();
  assert.equal(focused(p), 'Rating', 'a player closed much later is not its doing');
});

test('after the player: a focus the page or the viewer already put on an item is kept', () => {
  const p = titlePage({ afterPlayer: RESUME });
  p.showPlayer();
  p.inside.focus();
  press(p, 'Escape');
  p.hidePlayer();
  p.trailer.focus();
  p.runTimers();
  assert.equal(focused(p), 'Watch trailer');
});

test('after the player: a selector that matches nothing, or that the browser refuses, changes nothing and never throws', () => {
  for (const sel of ['[data-testid="gone"]', 'a[[']) {
    const p = titlePage({ afterPlayer: sel });
    p.document.querySelector = (s) => { if (s === 'a[[') throw new Error('SyntaxError'); return p.document.body.querySelectorAll(s)[0] || null; };
    p.showPlayer();
    p.inside.focus();
    closedByBack(p);
    assert.equal(p.document.activeElement, p.document.body, sel);
  }
});

test('player: a player page of its own (/play/) keeps Up and Down', () => {
  const p = homePage({ pathname: '/en-gb/play/0818ff05' });
  p.top.tiles[0].focus();
  const ev = press(p, 'ArrowDown');
  assert.equal(ev.prevented || ev.stopped, false);
});

// Prime draws its controls only for a real mouse move: a DevTools mouse move brought 13 buttons, scripted pointer and
// mouse events none (measured 2026-11-15). The page asks the launcher for one, at the middle of the screen.
test('player: each key in a player layer asks the launcher for a real mouse move, a pixel apart each time', () => {
  const p = playerPage();
  press(p, 'ArrowRight');
  press(p, 'ArrowDown');
  assert.equal(p.posted.length, 2);
  assert.equal(p.posted[0].tvPointer, 'wake');
  assert.ok(Math.abs(p.posted[0].x - 960) <= 2 && p.posted[0].y === 540, JSON.stringify(p.posted[0]));
  assert.notEqual(p.posted[0].x, p.posted[1].x);
});

test('player: no mouse move is asked for away from a player layer, or on a player page of its own', () => {
  const t = titlePage();
  t.trailer.focus();
  press(t, 'ArrowDown');
  const own = homePage({ pathname: '/en-gb/play/0818ff05' });
  press(own, 'ArrowRight');
  assert.deepEqual([t.posted, own.posted], [[], []]);
});

test('player: picking tries again a little later when the controls are slow to come', () => {
  const p = playerPage();
  press(p, 'ArrowDown');
  p.runTimers();
  assert.equal(focused(p), 'Rating', 'still nothing drawn at the first try');
  p.showControls();
  p.runTimers();
  assert.equal(focused(p), 'Pause');
});

// Prime draws no focus mark on its player buttons (the picked one measured outline none and a clear background,
// 2026-11-15), so a picked button was invisible: the launcher marks it.
test('player: the picked button carries the mark, which moves with it and goes when watching again', () => {
  const p = playerPage();
  p.showControls();
  press(p, 'ArrowDown');
  assert.ok(p.pause.hasAttribute('data-tv-pick'));
  assert.ok(p.document.head.children.some(c => /\[data-tv-pick\][^{]*\{[^}]*outline/.test(c.textContent || '')), 'a style draws it');
  press(p, 'ArrowRight');
  assert.ok(p.fwd10.hasAttribute('data-tv-pick') && !p.pause.hasAttribute('data-tv-pick'));
  press(p, 'ArrowDown');
  assert.equal(p.document.activeElement, p.layer);
  assert.ok(!p.fwd10.hasAttribute('data-tv-pick'));
});

// Netflix's home page as measured on 2026-11-21 (1920 x 1080, signed in): rows of cards (links, 250 wide every 256)
// moved by a transform inside a box that clips them (overflow clip), with "See previous titles" and "See more titles"
// buttons at opacity 0 at the box's edges, drawn only for a mouse. With nothing before the first card the left button
// measures nothing. A click on "See more titles" slides the row seven cards, so the card cut by the right edge becomes
// the first. OK on a card opens its detail panel, a dialog marked aria-modal, with Play first in it and the close
// button above it to the right, while the focus stays on the card behind. A privacy dialog, also aria-modal, is in the
// page all along and measures nothing. The player has its own page, /watch/<id>, whose video fills the screen in layers
// that are not fixed; Netflix focuses its player element and takes the arrows itself (Left and Right seek 10 s).
function netflixPage({ pathname = '/browse' } = {}) {
  const body = new El('body');
  const doc = { body, documentElement: new El('html'), activeElement: body, fullscreenElement: null, querySelectorAll: (s) => body.querySelectorAll(s),
    elementFromPoint: () => body };
  doc.documentElement.add(body);
  const bar = new El('div', {}, [0, 0, 1920, 80], { position: 'fixed' });
  bar.add(new El('a', { href: '/browse', 'aria-label': 'Home' }, [160, 20, 76, 40]));
  const names = ['Young Sheldon', 'Avatar', 'Raw', 'War Machine', 'Wednesday', 'ONE PIECE', 'One Piece', 'Stranger Things',
    'The Middle', 'Black Clover', 'Stargate', 'Jujutsu Kaisen', '2 Broke Girls'];
  const scroller = new El('section', { 'data-uia': 'carousel-scroller' }, [0, 470, 1920, 141], { overflowX: 'clip', overflowY: 'clip' });
  const track = new El('div', {}, [0, 470, 1920, 141], { overflowX: 'clip', overflowY: 'clip' });
  const cards = names.map((n, i) => new El('a', { href: '/title/' + i, 'data-uia': 'standard-card', 'aria-label': n }, [60 + i * 256, 470, 250, 141]));
  track.add(...cards);
  const prev = new El('button', { 'aria-label': 'See previous titles', tabindex: -1 }, [0, 0, 0, 0], { opacity: '0' });
  const more = new El('button', { 'aria-label': 'See more titles' }, [1831, 512, 28, 56], { opacity: '0' });
  const shift = (by) => {
    for (const c of cards) c.rect = [c.rect[0] + by, c.rect[1], c.rect[2], c.rect[3]];
    prev.rect = cards[0].rect[0] < 0 ? [46, 512, 28, 56] : [0, 0, 0, 0];
  };
  more.click = function () { this.clicks = (this.clicks || 0) + 1; shift(-7 * 256); };
  prev.click = function () { this.clicks = (this.clicks || 0) + 1; shift(7 * 256); };
  scroller.add(track, prev, more);
  const below = new El('a', { href: '/title/below', 'data-uia': 'standard-card', 'aria-label': 'Below' }, [60, 692, 250, 141]);
  // The privacy dialog, there all along: marked aria-modal, measuring nothing.
  const privacy = new El('div', { role: 'dialog', 'aria-modal': 'true' }, [0, 0, 0, 0]);
  privacy.add(new El('button', { 'aria-label': 'Close preference center' }, [0, 0, 0, 0]));
  body.add(bar, scroller, below, privacy);
  // The detail panel, added when OK opens it.
  const panel = new El('div', { role: 'dialog', 'aria-modal': 'true', tabindex: -1 }, [431, 32, 1058, 1900]);
  const play = new El('button', { 'data-uia': 'play-button', 'aria-label': 'Play' }, [483, 519, 92, 40]);
  const myList = new El('button', { 'aria-label': 'Add to My List' }, [585, 519, 40, 40]);
  const close = new El('button', { 'aria-label': 'Close' }, [1430, 46, 48, 48]);
  const episode = new El('a', { href: '/watch/1', 'aria-label': 'Episode 1' }, [479, 943, 962, 130]);
  panel.add(play, myList, close, episode);
  // The player page: Netflix's player element, focused, over a video filling the screen; nothing in it is fixed.
  const player = new El('div', { 'data-uia': 'player', tabindex: 0, 'aria-label': 'Player' }, [0, 0, 1920, 1080], { position: 'absolute' });
  const video = new El('video', {}, [0, -3, 1920, 1087], { position: 'absolute' });
  if (pathname.startsWith('/watch/')) { body.children.length = 0; body.add(player.add(video)); }
  const p = run(doc, pathname, { cards, prev, more, below, panel, play, myList, close, episode, player, video });
  p.openPanel = () => body.add(panel);
  return p;
}

test('netflix: the player page (/watch/) keeps every key, as a player page of its own', () => {
  const p = netflixPage({ pathname: '/watch/80018499' });
  p.player.focus();
  for (const key of ['ArrowRight', 'ArrowLeft', 'ArrowUp', 'ArrowDown', 'Enter']) {
    const ev = press(p, key);
    assert.equal(ev.prevented || ev.stopped, false, key + ' goes on to Netflix, which seeks or changes the volume');
    assert.equal(focused(p), 'Player', key + ': the focus stays on the player');
  }
});

test('netflix: a page whose address only has "watch" in it is not the player', () => {
  const p = netflixPage({ pathname: '/browse/my-list/watchlist' });
  p.cards[0].focus();
  assert.ok(press(p, 'ArrowRight').prevented);
  assert.equal(focused(p), 'Avatar');
});

test('netflix: with the detail panel open and the focus behind it, the next press starts on the panel\'s first item, Play', () => {
  const p = netflixPage();
  p.cards[6].focus();
  p.openPanel();
  const ev = press(p, 'ArrowDown');
  assert.ok(ev.prevented && ev.stopped);
  assert.equal(focused(p), 'Play', 'the first item in the panel, not the close button higher up');
  press(p, 'ArrowRight');
  assert.equal(focused(p), 'Add to My List', 'then the arrows move inside the panel');
  press(p, 'ArrowDown');
  assert.equal(focused(p), 'Episode 1');
});

test('netflix: a dialog marked aria-modal that measures nothing (a hidden privacy dialog) does not take the arrows', () => {
  const p = netflixPage();
  p.cards[0].focus();
  press(p, 'ArrowRight');
  assert.equal(focused(p), 'Avatar');
});

test('netflix: Right onto the card cut by the row\'s edge presses the row\'s own "See more titles", which slides it in', () => {
  const p = netflixPage();
  p.cards[6].focus();   // the 7th, the last card wholly in sight
  const ev = press(p, 'ArrowRight');
  assert.ok(ev.prevented && ev.stopped);
  assert.equal(focused(p), 'Stranger Things');
  assert.equal(p.more.clicks, 1, 'the row slid');
  assert.equal(p.cards[7].rect[0], 60, 'the cut card is now the first');
  assert.equal(JSON.stringify(p.cards[7].focusOpts), JSON.stringify({ preventScroll: true }));
  assert.equal(p.cards[7].scrolled, null, 'no scrollIntoView, which would scroll the clipping box behind the row\'s back');
});

test('netflix: after a slide, Right moves on along the cards in sight without pressing again; Left past the first slides back', () => {
  const p = netflixPage();
  p.cards[6].focus();
  press(p, 'ArrowRight');
  press(p, 'ArrowRight');
  assert.equal(focused(p), 'The Middle');
  assert.equal(p.more.clicks, 1);
  press(p, 'ArrowLeft');
  assert.equal(focused(p), 'Stranger Things');
  press(p, 'ArrowLeft');
  assert.equal(focused(p), 'One Piece', 'the card before, out of sight to the left');
  assert.equal(p.prev.clicks, 1, '"See previous titles" slid it back');
  assert.equal(p.cards[6].rect[0], 1596);
});

test('netflix: Left from the first card, with nothing before it, stays put and presses nothing', () => {
  const p = netflixPage();
  p.cards[0].focus();
  press(p, 'ArrowLeft');
  assert.equal(focused(p), 'Young Sheldon');
  assert.equal(p.prev.clicks, undefined);
});

test('netflix: Down never jumps to a card out of sight, and presses no row button', () => {
  const p = netflixPage();
  p.cards[0].focus();
  press(p, 'ArrowDown');
  assert.equal(focused(p), 'Below');
  assert.equal(p.more.clicks, undefined);
});

// Prime Video's search results as measured on 2026-11-21 (1920 x 1080, "batman", signed in): a row of tiles (links,
// 276 wide every 300) in a list that scrolls sideways inside a box that clips at the screen's edge, with the row's own
// "previous 5 titles" and "next 5 titles" buttons at its ends, shown (opacity 1, tabindex 0). The left one measures
// nothing while nothing lies before the first tile. A click on "next 5 titles" slides the row 1800 px, so the tile cut
// by the right edge becomes the first.
function primeSearch() {
  const body = new El('body');
  const doc = { body, documentElement: new El('html'), activeElement: body, fullscreenElement: null, querySelectorAll: (s) => body.querySelectorAll(s),
    elementFromPoint: () => body };
  doc.documentElement.add(body);
  const names = ['Gotham Knights', 'Batman & Robin', 'The Killing Joke', 'Mask of the Phantasm', 'SubZero', 'Batman Beyond',
    'Batman', 'Justice League', 'The Dark Knight Returns', 'The Long Halloween', 'Superman', 'Batwheels', 'Caped Crusader'];
  const box = new El('div', {}, [0, 463, 1905, 155], { overflowX: 'hidden', overflowY: 'hidden' });
  const list = new El('ul', { 'data-testid': 'card-container-list' }, [0, 463, 1905, 155], { overflowX: 'scroll', overflowY: 'auto' });
  const tiles = names.map((n, i) => new El('a', { href: '/detail/' + i, 'aria-label': n }, [72 + i * 300, 463, 276, 155]));
  // Each tile has a button of its own over it (opacity 0), and the tile in focus a hover card, a button wider than
  // the tile (opacity 1), all before the row's own buttons in the page. Measured with Batman Beyond focused.
  const items = [];
  list.add(...tiles.map((t, i) => {
    const own = new El('button', { 'aria-label': names[i] }, t.rect.slice(), { opacity: '0' });
    items.push(own, t);
    const li = new El('li', {}, t.rect.slice()).add(own, t);
    if (i === 5) { const card = new El('button', { 'aria-label': names[i] }, [1417, 419, 431, 242]); items.push(card); li.add(card); }
    return li;
  }));
  const prev = new El('button', { 'data-testid': 'left-arrow', 'aria-label': 'previous 5 titles', tabindex: -1 }, [0, 463, 0, 155]);
  const more = new El('button', { 'data-testid': 'right-arrow', 'aria-label': 'next 5 titles', tabindex: 0 }, [1857, 463, 48, 155]);
  const shift = (by) => {
    for (const t of items) t.rect = [t.rect[0] + by, t.rect[1], t.rect[2], t.rect[3]];
    const before = tiles[0].rect[0] < 0;
    prev.rect = [0, 463, before ? 48 : 0, 155];
    prev.attrs.tabindex = before ? 0 : -1;
  };
  more.click = function () { this.clicks = (this.clicks || 0) + 1; shift(-1800); };
  prev.click = function () { this.clicks = (this.clicks || 0) + 1; shift(1800); };
  box.add(list, prev, more);
  body.add(box);
  return run(doc, '/search/ref=atv_nb_sug', { tiles, prev, more });
}

test('prime search: Right onto the tile cut by the row\'s edge goes to the tile, not the row\'s "next 5 titles", and presses it', () => {
  const p = primeSearch();
  p.tiles[5].focus();   // Batman Beyond, the last tile wholly in sight
  const ev = press(p, 'ArrowRight');
  assert.ok(ev.prevented && ev.stopped);
  assert.equal(focused(p), 'Batman', 'the next tile, though the button lies nearer');
  assert.equal(p.more.clicks, 1, 'the row slid');
  assert.equal(p.tiles[6].rect[0], 72, 'the cut tile is now the first');
  press(p, 'ArrowRight');
  assert.equal(focused(p), 'Justice League', 'and Right moves on along the row');
  assert.equal(p.more.clicks, 1);
});

test('prime search: Left from the first tile after a slide goes to the tile before, not "previous 5 titles", and slides back', () => {
  const p = primeSearch();
  p.tiles[5].focus();
  press(p, 'ArrowRight');
  press(p, 'ArrowLeft');
  assert.equal(focused(p), 'Batman Beyond');
  assert.equal(p.prev.clicks, 1, '"previous 5 titles" slid it back');
  assert.equal(p.tiles[5].rect[0], 1572);
});

test('prime search: Right from the last tile of the row stays put, never on "next 5 titles", and presses nothing', () => {
  const p = primeSearch();
  p.more.click();
  p.more.click();   // the row at its end: the last tile wholly in sight, nothing after it, the button still shown
  p.tiles[12].focus();
  const ev = press(p, 'ArrowRight');
  assert.ok(ev.prevented && ev.stopped);
  assert.equal(focused(p), 'Caped Crusader');
  assert.equal(p.more.clicks, 2);
});

test('prime search: Right reaches the last tile when it lies under "next 5 titles", and presses the button to show it', () => {
  const p = primeSearch();
  p.more.click();
  for (const t of p.tiles) t.attrs.tabindex = -1;   // as Prime marks its tiles
  // Slid so the last tile is cut by the edge with the button over it (measured: tile 1674 to 1950, button 1857 to 1905).
  for (const t of p.tiles) t.rect = [t.rect[0] - 198, t.rect[1], t.rect[2], t.rect[3]];
  p.tiles[11].focus();   // Batwheels, 1374 to 1650
  press(p, 'ArrowRight');
  assert.equal(focused(p), 'Caped Crusader', 'a tile is not a banner image behind the row\'s own button');
  assert.equal(p.more.clicks, 2, 'the button slid the rest of the row in');
});
