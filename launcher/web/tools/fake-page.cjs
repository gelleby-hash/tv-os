// Test helper: runs the page's real scripts in a vm context against a small fake DOM built from index.html.
// No browser and no dependencies. Layout is fake: an element's rectangle comes from its position among its siblings.
'use strict';
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const root = path.join(__dirname, '..');

const VOID = new Set(['meta', 'link', 'br', 'img', 'input', 'hr']);
const camel = name => name.replace(/-([a-z])/g, (m, c) => c.toUpperCase());
const unescapeText = s => s.replace(/&lt;/g, '<').replace(/&gt;/g, '>').replace(/&quot;/g, '"').replace(/&amp;/g, '&');

function parseCompound(text) {
  const tokens = [], re = /#([\w-]+)|\.([\w-]+)|\[([\w-]+)(?:="([^"]*)")?\]|([a-zA-Z][\w]*)/g;
  let m;
  while ((m = re.exec(text))) {
    if (m[1]) tokens.push({ id: m[1] });
    else if (m[2]) tokens.push({ cls: m[2] });
    else if (m[3]) tokens.push({ attr: m[3], value: m[4] });
    else tokens.push({ tag: m[5].toLowerCase() });
  }
  return tokens;
}

class El {
  constructor(tag) {
    this.tagName = tag.toUpperCase();
    this.id = '';
    this.classes = new Set();
    this.dataset = {};
    this.attrs = {};
    this.hidden = false;
    // setProperty for CSS custom properties, as the page sets --motion on the root element.
    this.style = { setProperty(name, value) { this[name] = String(value); } };
    this.children = [];
    this.parentNode = null;
    this._text = '';
    this._html = '';
    this.clientWidth = 1920;
    this.offsetLeft = 0;
    this.offsetWidth = 100;
    const self = this;
    this.classList = {
      add: c => self.classes.add(c),
      remove: c => self.classes.delete(c),
      contains: c => self.classes.has(c),
      toggle(c, force) {
        const on = force === undefined ? !self.classes.has(c) : !!force;
        if (on) self.classes.add(c); else self.classes.delete(c);
        return on;
      }
    };
  }
  get className() { return Array.from(this.classes).join(' '); }
  set className(v) { this.classes = new Set(String(v).split(/\s+/).filter(Boolean)); }
  get firstChild() { return this.children[0] || null; }
  get offsetParent() {
    for (let e = this; e; e = e.parentNode) if (e.hidden) return null;
    return this.parentNode || {};
  }
  get textContent() { return this._text + this.children.map(c => c.textContent).join(''); }
  set textContent(v) { this.children.forEach(c => { c.parentNode = null; }); this.children = []; this._text = String(v); }
  get innerHTML() { return this._html; }
  set innerHTML(html) {
    this.children.forEach(c => { c.parentNode = null; });
    this.children = [];
    this._text = '';
    this._html = String(html);
    parseInto(this, this._html);
  }
  setAttr(name, value) {
    if (name === 'id') this.id = value;
    else if (name === 'class') this.className = value;
    else if (name === 'hidden') { this.hidden = true; this.attrs.hidden = ''; }
    else if (name.startsWith('data-')) this.dataset[camel(name.slice(5))] = value;
    else this.attrs[name] = value;
  }
  setAttribute(name, value) { this.setAttr(name, String(value)); }
  // Like a browser: on an SVG element the hidden property is a plain property and does not touch the attribute.
  hasAttribute(name) {
    if (name === 'hidden' && this.tagName !== 'SVG') return this.hidden;
    return name === 'id' ? !!this.id : this.getAttribute(name) !== undefined;
  }
  removeAttribute(name) {
    if (name === 'hidden' && this.tagName !== 'SVG') this.hidden = false;
    if (name.startsWith('data-')) delete this.dataset[camel(name.slice(5))]; else delete this.attrs[name];
  }
  getAttribute(name) {
    if (name === 'id') return this.id;
    if (name.startsWith('data-')) return this.dataset[camel(name.slice(5))];
    return this.attrs[name];
  }
  appendChild(node) { return this.insertBefore(node, null); }
  insertBefore(node, ref) {
    if (node.parentNode) node.parentNode.children.splice(node.parentNode.children.indexOf(node), 1);
    const i = ref ? this.children.indexOf(ref) : -1;
    if (i < 0) this.children.push(node); else this.children.splice(i, 0, node);
    node.parentNode = this;
    return node;
  }
  remove() { if (this.parentNode) { this.parentNode.children.splice(this.parentNode.children.indexOf(this), 1); this.parentNode = null; } }
  contains(other) { for (let e = other; e; e = e.parentNode) if (e === this) return true; return false; }
  matchesCompound(tokens) {
    return tokens.every(t => {
      if (t.id) return this.id === t.id;
      if (t.cls) return this.classes.has(t.cls);
      if (t.tag) return this.tagName.toLowerCase() === t.tag;
      const v = t.attr.startsWith('data-') ? this.dataset[camel(t.attr.slice(5))] : this.attrs[t.attr];
      if (t.attr === 'hidden') return this.hidden;
      return t.value === undefined ? v !== undefined : String(v) === t.value;
    });
  }
  matches(selector) {
    const chain = selector.trim().split(/\s+/).map(parseCompound);
    if (!this.matchesCompound(chain[chain.length - 1])) return false;
    let e = this.parentNode;
    for (let i = chain.length - 2; i >= 0; i--) {
      while (e && !e.matchesCompound(chain[i])) e = e.parentNode;
      if (!e) return false;
      e = e.parentNode;
    }
    return true;
  }
  closest(selector) { for (let e = this; e && e.matches; e = e.parentNode) if (e.matches(selector)) return e; return null; }
  querySelectorAll(selector) {
    const out = [];
    const walk = node => node.children.forEach(c => { if (c.matches(selector)) out.push(c); walk(c); });
    walk(this);
    return out;
  }
  querySelector(selector) { return this.querySelectorAll(selector)[0] || null; }
  getBoundingClientRect() {
    if (this.classes.has('grid-viewport')) return { left: -1e9, right: 1e9, top: -1e9, bottom: 1e9, width: 2e9, height: 2e9 };
    let depth = 0;
    for (let e = this.parentNode; e; e = e.parentNode) depth++;
    const index = this.parentNode ? this.parentNode.children.indexOf(this) : 0;
    const left = index * 150, top = depth * 100;
    return { left, top, right: left + 100, bottom: top + 50, width: 100, height: 50 };
  }
  addEventListener() {}
  click() { this.ownerPage.fire(this, 'click'); }
}

function parseInto(parent, html) {
  const stack = [parent];
  const re = /<!--[\s\S]*?-->|<(\/?)([a-zA-Z][\w-]*)((?:[^>"']|"[^"]*"|'[^']*')*)>|([^<]+)/g;
  let m;
  while ((m = re.exec(html))) {
    const top = stack[stack.length - 1];
    if (m[4] !== undefined) { top._text += unescapeText(m[4]); continue; }
    if (!m[2]) continue;
    const tag = m[2].toLowerCase();
    if (m[1]) {
      const at = stack.map(e => e.tagName.toLowerCase()).lastIndexOf(tag);
      if (at > 0) stack.length = at;
      continue;
    }
    let attrText = m[3], selfClose = false;
    if (/\/\s*$/.test(attrText)) { selfClose = true; attrText = attrText.replace(/\/\s*$/, ''); }
    const el = new El(tag);
    el.ownerPage = parent.ownerPage;
    const attrRe = /([^\s=]+)(?:="([^"]*)")?/g;
    let a;
    while ((a = attrRe.exec(attrText))) el.setAttr(a[1], a[2] === undefined ? '' : unescapeText(a[2]));
    top.appendChild(el);
    if (!selfClose && !VOID.has(tag)) stack.push(el);
  }
}

// What the host injects: custom-tv's config shape plus backgroundRotateSeconds.
function fakeConfig() {
  return {
    locale: 'en-GB',
    weatherRefreshMinutes: 15,
    // The host's app layout (AppLayout.ToPage): every app in order, hidden ones included, each with its versions.
    apps: [
      { id: 'youtube', name: 'YouTube', icon: 'icons/youtube.svg', shown: true, version: 'default', versions: [{ id: 'default', name: 'YouTube' }] },
      { id: 'disney', name: 'Disney+', icon: 'icons/disney.svg', shown: true, version: 'default', versions: [{ id: 'default', name: 'Disney+' }] }
    ],
    boostApps: ['Disney+'],
    requireHdr: false,
    defaults: { language: 'en-GB', unit: 'C', clock: '24', scenery: 'auto', effects: true },
    backgroundRotateSeconds: 60
  };
}

// A made-up place for tests. Like the host's message, latitude and longitude are present only for a source that has a place
// (picked, config, windows, lastWindows); off and none carry none.
function placeMessage(source = 'config') {
  const message = { type: 'location', source, windowsOn: true, region: 'GB', canTurnOn: false };
  if (!['off', 'none'].includes(source)) { message.latitude = 10; message.longitude = 20; }
  return message;
}

const htmlPath = path.join(root, 'index.html');

// options: host (default true) gives the page a fake chrome.webview; fetch replaces the weather request;
// matchMedia ({ matches }) gives the page a window.matchMedia whose lists the test can flip with setMedia(matches);
// now (a Date or milliseconds) fixes the page's clock, so Date.now() and new Date() give that moment and a test does not
// depend on the time of day; storage (an object) seeds the page's localStorage, which keeps what the page stores in memory
// (page.storage shows it). setTimeout callbacks wait until the test calls page.tick(ms), which runs those due by then in order
// of their time; setInterval callbacks never run.
function loadPage(options = {}) {
  const html = fs.readFileSync(htmlPath, 'utf8');
  const posted = [], logs = [], messageListeners = [], docListeners = {}, frames = [];
  const page = { fire };

  const body = new El('body');
  body.ownerPage = page;
  parseInto(body, html.slice(html.indexOf('<body>') + 6, html.indexOf('</body>')).replace(/<script[\s\S]*?<\/script>/g, ''));
  const documentElement = new El('html');
  const byId = id => {
    for (const e of [body, ...body.querySelectorAll('*')]) if (e.id === id) return e;
    return null;
  };
  function fire(target, type) {
    const event = { type, target, keyCode: 0, preventDefault() { event.defaultPrevented = true; } };
    (docListeners[type] || []).forEach(fn => fn(event));
    return event;
  }
  const document = {
    body, documentElement, title: '', hidden: false,
    getElementById: byId,
    createElement: tag => { const e = new El(tag); e.ownerPage = page; return e; },
    querySelector: s => body.querySelector(s),
    querySelectorAll: s => body.querySelectorAll(s),
    contains: el => body.contains(el),
    addEventListener: (type, fn) => { (docListeners[type] = docListeners[type] || []).push(fn); }
  };

  const window = {
    innerWidth: 1920, innerHeight: 1080, TV_HOME_CONFIG: options.config || fakeConfig(),
    addEventListener() {},
    TVSky: { render() {}, Precip: () => ({ set() {}, pause() {} }) },
    TVLandscape: { render() {}, select() {} }
  };
  if (options.host !== false) {
    window.chrome = { webview: {
      postMessage: m => posted.push(JSON.parse(JSON.stringify(m))),
      addEventListener: (type, fn) => { if (type === 'message') messageListeners.push(fn); }
    } };
  }
  const mediaQueries = [], mediaLists = [];
  if (options.matchMedia) {
    window.matchMedia = query => {
      const listeners = [];
      const list = { media: query, matches: !!options.matchMedia.matches, listeners, addEventListener: (type, fn) => { if (type === 'change') listeners.push(fn); } };
      mediaQueries.push(query);
      mediaLists.push(list);
      return list;
    };
  }
  window.console = { log: (...a) => logs.push(a.join(' ')), error: (...a) => logs.push(a.join(' ')) };
  let clock = 1000;
  // The page's timers: none runs until the test advances them with tick(ms).
  let timerNow = 0, timerId = 0;
  const timers = new Map();
  const storage = Object.assign({}, options.storage);
  const context = vm.createContext({
    window, document, location: { search: options.search || '' },
    localStorage: { getItem: k => (k in storage ? storage[k] : null), setItem: (k, v) => { storage[k] = String(v); } },
    CSS: { escape: s => s },
    getComputedStyle: () => ({ paddingTop: '0px', paddingBottom: '0px' }),
    Image: function () { const e = new El('img'); e.ownerPage = page; return e; },
    URLSearchParams,
    console: window.console,
    fetch: options.fetch || (() => new Promise(() => {})),
    setTimeout: (fn, ms) => { timers.set(++timerId, { at: timerNow + (+ms || 0), fn }); return timerId; },
    clearTimeout: id => { timers.delete(id); },
    setInterval: () => 0,
    requestAnimationFrame: fn => frames.push(fn),
    performance: { now: () => (clock += 5) }
  });

  if (options.now !== undefined) {
    const fixed = +options.now;
    vm.runInContext('(function () { var RealDate = Date; var fixed = ' + fixed + ';' +
      ' class FakeDate extends RealDate { constructor(...a) { if (a.length) super(...a); else super(fixed); } static now() { return fixed; } }' +
      ' globalThis.Date = FakeDate; })()', context);
  }

  const scripts = Array.from(html.matchAll(/<script src="([^"]+)"/g), m => m[1]);
  for (const src of scripts) {
    if (src === 'js/sky.js' || src === 'js/landscape.js') continue; // canvas drawing is not under test
    vm.runInContext(fs.readFileSync(path.join(root, src), 'utf8'), context, { filename: src });
  }

  const plain = x => JSON.parse(JSON.stringify(x));
  return {
    window, document, scripts, posted, logs, html, mediaQueries, storage,
    $: byId,
    plain,
    fire,
    tick(ms) {
      const end = timerNow + ms;
      for (;;) {
        let next = null;
        for (const [id, t] of timers) if (t.at <= end && (!next || t.at < next[1].at)) next = [id, t];
        if (!next) break;
        timers.delete(next[0]);
        timerNow = next[1].at;
        next[1].fn();
      }
      timerNow = end;
    },
    deliver: msg => messageListeners.forEach(fn => fn({ data: msg })),
    setMedia(matches) {
      mediaLists.forEach(list => { list.matches = !!matches; list.listeners.slice().forEach(fn => fn({ matches: !!matches, media: list.media })); });
    },
    flushFrames: () => { frames.splice(0).forEach(fn => fn()); },
    settle: () => new Promise(resolve => setImmediate(resolve)),
    focused: () => body.querySelector('.focused'),
    // key (optional) is the event's key property as a real keyboard sets it; modifiers (optional) adds ctrlKey, altKey and the like.
    pressKey(code, key, modifiers) {
      const event = Object.assign({ type: 'keydown', keyCode: code, key, target: body, preventDefault() { event.defaultPrevented = true; } }, modifiers);
      (docListeners.keydown || []).forEach(fn => fn(event));
      return event;
    }
  };
}

module.exports = { loadPage, fakeConfig, placeMessage, htmlPath, root };
