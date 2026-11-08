// Runs PageScripts.HideScrollbars (read from the C# file, so there is one copy) against a fake document, because
// the C# test can only read its text. In Chromium a document-created script sees documentElement and head both null.
'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

const source = fs.readFileSync(path.join(__dirname, '..', '..', 'src', 'TvLauncher', 'Apps', 'PageScripts.cs'), 'utf8');
const script = (() => {
  const m = source.match(/HideScrollbars = """\r?\n([\s\S]*?)\r?\n\s*""";/);
  assert.ok(m, 'HideScrollbars raw string found in PageScripts.cs');
  return m[1];
})();

// A document that starts without a root element, like a document-created script sees it.
function fakeDocument(withRoot) {
  const doc = {
    documentElement: withRoot ? { children: [], appendChild(n) { this.children.push(n); } } : null,
    head: null,
    created: 0,
    createElement(tag) { doc.created++; return { tag, textContent: '' }; }
  };
  const observers = [];
  class FakeObserver {
    constructor(cb) { this.cb = cb; this.disconnected = false; this.target = null; this.options = null; observers.push(this); }
    observe(target, options) { this.target = target; this.options = options; }
    disconnect() { this.disconnected = true; }
  }
  const warnings = [];
  const ctx = vm.createContext({ document: doc, MutationObserver: FakeObserver, console: { warn: (...a) => warnings.push(a) } });
  return { doc, observers, warnings, run: () => vm.runInContext(script, ctx, { filename: 'HideScrollbars' }) };
}

test('scrollbars: no root element yet, the style waits for it and is added once', () => {
  const f = fakeDocument(false);
  f.run();
  assert.equal(f.doc.created, 0, 'nothing is created before the root element exists');
  assert.equal(f.observers.length, 1);
  assert.equal(f.observers[0].target, f.doc, 'it watches the document');
  assert.equal(f.observers[0].options.childList, true);

  // The parser creates <html>: the observer fires.
  f.doc.documentElement = { children: [], appendChild(n) { this.children.push(n); } };
  f.observers[0].cb();
  assert.equal(f.doc.documentElement.children.length, 1, 'exactly one style');
  assert.equal(f.doc.documentElement.children[0].tag, 'style');
  assert.equal(f.observers[0].disconnected, true);
  assert.deepEqual(f.warnings, []);
});

test('scrollbars: the observer firing for another reason before the root exists adds nothing', () => {
  const f = fakeDocument(false);
  f.run();
  f.observers[0].cb();
  assert.equal(f.doc.created, 0);
  assert.equal(f.observers[0].disconnected, false);
});

test('scrollbars: a root element already there gets the style at once, without an observer', () => {
  const f = fakeDocument(true);
  f.run();
  assert.equal(f.doc.documentElement.children.length, 1);
  assert.equal(f.observers.length, 0);
});

test('scrollbars: the style has the three rules', () => {
  const f = fakeDocument(true);
  f.run();
  const css = f.doc.documentElement.children[0].textContent;
  assert.ok(css.includes('scrollbar-width: none'));
  assert.ok(css.includes('::-webkit-scrollbar { display: none; width: 0; height: 0; }'));
});

test('scrollbars: a page without MutationObserver does not throw', () => {
  const doc = { documentElement: null, head: null, createElement() { return {}; } };
  const warnings = [];
  const ctx = vm.createContext({ document: doc, console: { warn: (...a) => warnings.push(a) } });
  assert.doesNotThrow(() => vm.runInContext(script, ctx));
});

// PageScripts.HidePlayReady, run against a fake navigator: Channel 4 asks for PlayReady first and falls back to
// Widevine when it is refused (measured 2026-11-02).
const hidePlayReady = (() => {
  const m = source.match(/HidePlayReady = """\r?\n([\s\S]*?)\r?\n\s*""";/);
  assert.ok(m, 'HidePlayReady raw string found in PageScripts.cs');
  return m[1];
})();

function fakeNavigator() {
  const calls = [];
  const navigator = { requestMediaKeySystemAccess(keySystem, configs) { calls.push({ self: this, keySystem, configs }); return Promise.resolve('access ' + keySystem); } };
  class DOMException extends Error { constructor(message, name) { super(message); this.name = name; } }
  const ctx = vm.createContext({ navigator, DOMException, Promise, console: { warn() {} } });
  vm.runInContext(hidePlayReady, ctx, { filename: 'HidePlayReady' });
  return { navigator, calls };
}

test('playready: every PlayReady key system is refused as not supported, without asking the browser', async () => {
  const f = fakeNavigator();
  for (const k of ['com.microsoft.playready', 'com.microsoft.playready.recommendation', 'com.microsoft.playready.recommendation.3000', 'com.microsoft.PlayReady.hardware']) {
    await assert.rejects(f.navigator.requestMediaKeySystemAccess(k, []), e => e.name === 'NotSupportedError', k);
  }
  assert.equal(f.calls.length, 0);
});

test('playready: Widevine and the rest reach the browser unchanged, called on navigator', async () => {
  const f = fakeNavigator();
  const configs = [{ initDataTypes: ['cenc'] }];
  const request = f.navigator.requestMediaKeySystemAccess;
  assert.equal(await request('com.widevine.alpha', configs), 'access com.widevine.alpha');
  assert.equal(await f.navigator.requestMediaKeySystemAccess('org.w3.clearkey', configs), 'access org.w3.clearkey');
  assert.equal(f.calls.length, 2);
  assert.equal(f.calls[0].keySystem, 'com.widevine.alpha');
  assert.equal(f.calls[0].configs, configs);
  assert.equal(f.calls[0].self, f.navigator, 'a detached call still reaches the browser on navigator');
});

// PageScripts.HideElements, run against a fake document: Prime Video's cookie notice cannot be reached with the
// remote's arrows (measured 2026-11-07), so its entry hides it with a selector.
const hideElements = (selector) => {
  const m = source.match(/HideElementsScript = """\r?\n([\s\S]*?)\r?\n\s*""";/);
  assert.ok(m, 'HideElementsScript raw string found in PageScripts.cs');
  return m[1].replace('__SELECTOR__', JSON.stringify(selector));
};

function runHideElements(selector, { withRoot = true, valid = () => true } = {}) {
  const f = fakeDocument(withRoot);
  const warnings = [];
  const CSS = { supports: (s) => valid(s) };
  const ctx = vm.createContext({ document: f.doc, MutationObserver: class { constructor(cb) { this.cb = cb; f.observers.push(this); } observe() {} disconnect() { this.disconnected = true; } }, CSS, console: { warn: (...a) => warnings.push(a) } });
  vm.runInContext(hideElements(selector), ctx, { filename: 'HideElements' });
  return { ...f, warnings };
}

test('hide elements: one style hides the selector, whatever the page sets', () => {
  const sel = '[data-testid="critical-notification"]:has(a[href*="privacyprefs"])';
  const f = runHideElements(sel);
  assert.equal(f.doc.documentElement.children.length, 1);
  assert.equal(f.doc.documentElement.children[0].textContent, sel + ' { display: none !important; }');
  assert.deepEqual(f.warnings, []);
});

test('hide elements: the browser asks to check the selector before anything is added', () => {
  const asked = [];
  runHideElements('div.notice', { valid: (s) => { asked.push(s); return true; } });
  assert.deepEqual(asked, ['selector(div.notice)']);
});

test('hide elements: a selector the browser does not take hides nothing, with a warning', () => {
  const f = runHideElements('div[', { valid: () => false });
  assert.equal(f.doc.created, 0);
  assert.equal(f.warnings.length, 1);
});

test('hide elements: no root element yet, the style waits for it', () => {
  const f = runHideElements('div.notice', { withRoot: false });
  assert.equal(f.doc.created, 0);
  f.doc.documentElement = { children: [], appendChild(n) { this.children.push(n); } };
  f.observers[0].cb();
  assert.equal(f.doc.documentElement.children.length, 1);
});

test('hide elements: a quote in the selector stays inside the string', () => {
  const sel = 'a[title="x"]"; globalThis.broken = true; "';
  const f = runHideElements(sel);
  assert.equal(f.doc.documentElement.children[0].textContent, sel + ' { display: none !important; }');
});

// PageScripts.PressButton, run against a fake page: Channel 4's player ignores Space, and its own Pause button
// pauses it (measured 2026-11-02). The C# side puts the label in as a JSON string.
const pressButton = (label) => {
  const m = source.match(/PressButtonScript = """\r?\n([\s\S]*?)\r?\n\s*""";/);
  assert.ok(m, 'PressButtonScript raw string found in PageScripts.cs');
  return m[1].replace('__LABEL__', JSON.stringify(label));
};

function fakePlayerPage() {
  const clicks = [];
  const button = (label, how) => ({ label, clicked: 0, shadowRoot: null,
    getAttribute: (n) => (n === 'aria-label' && how === 'aria' ? label : null),
    textContent: how === 'text' ? '  ' + label + '\n' : '',
    click() { this.clicked++; clicks.push(label); } });
  const root = (buttons, hosts = []) => ({
    querySelectorAll: (sel) => (sel === 'button' ? buttons : sel === '*' ? [...buttons, ...hosts] : []) });
  return { button, root, clicks };
}

test('pause button: presses the button with that label, found by its aria-label or its text, once', () => {
  const f = fakePlayerPage();
  const mute = f.button('Mute', 'aria'), pause = f.button('Pause', 'aria'), pause2 = f.button('Pause', 'text');
  const document = f.root([mute, pause, pause2]);
  const result = vm.runInContext(pressButton('Pause'), vm.createContext({ document }));
  assert.equal(result, true);
  assert.deepEqual(f.clicks, ['Pause']);
  assert.equal(pause.clicked, 1);
});

test('pause button: found inside a shadow root, and by its text', () => {
  const f = fakePlayerPage();
  const inner = f.button('Pause', 'text');
  const host = { shadowRoot: f.root([inner]), getAttribute: () => null };
  const document = f.root([f.button('Play next', 'aria')], [host]);
  assert.equal(vm.runInContext(pressButton('Pause'), vm.createContext({ document })), true);
  assert.equal(inner.clicked, 1);
});

test('pause button: no button with that exact label, nothing is pressed', () => {
  const f = fakePlayerPage();
  const document = f.root([f.button('Play', 'aria'), f.button('Pause ad', 'aria')]);
  assert.equal(vm.runInContext(pressButton('Pause'), vm.createContext({ document })), false);
  assert.deepEqual(f.clicks, []);
});

// PageScripts.ExitWhenEmpty, run against a fake page: Channel 4's TV app exits by emptying <div id="appRoot"> and
// then failing to exit (its exit throws "exit is not supported"), which left a dark page (2026-11-02).
const exitWhenEmpty = (id) => {
  const m = source.match(/ExitWhenEmptyScript = """\r?\n([\s\S]*?)\r?\n\s*""";/);
  assert.ok(m, 'ExitWhenEmptyScript raw string found in PageScripts.cs');
  return m[1].replace('__ID__', JSON.stringify(id));
};

function fakeAppPage() {
  const page = { closed: 0, posted: [], observers: [], root: null };
  const document = { getElementById: (id) => (id === 'appRoot' ? page.root : null) };
  class FakeObserver {
    constructor(cb) { this.cb = cb; this.disconnected = false; page.observers.push(this); }
    observe(target, options) { this.target = target; this.options = options; }
    disconnect() { this.disconnected = true; }
  }
  const window = { chrome: { webview: { postMessage(m) { page.posted.push(m); if (m && m.tvExit === 'empty') page.closed++; } } } };
  const ctx = vm.createContext({ document, window, MutationObserver: FakeObserver, console: { warn() {} } });
  vm.runInContext(exitWhenEmpty('appRoot'), ctx, { filename: 'ExitWhenEmpty' });
  page.change = () => page.observers.filter(o => !o.disconnected).forEach(o => o.cb([]));
  return page;
}

test('exit when empty: the app filled its root and then emptied it, the page is closed once', () => {
  const p = fakeAppPage();
  p.root = { childElementCount: 0 };
  p.change();
  assert.equal(p.closed, 0, 'empty before the app mounted: not an exit');
  p.root.childElementCount = 3;
  p.change();
  assert.equal(p.closed, 0);
  p.root.childElementCount = 0;
  p.change();
  assert.equal(p.closed, 1);
  p.change();
  assert.equal(p.closed, 1, 'once only');
  assert.equal(JSON.stringify(p.posted), JSON.stringify([{ tvExit: 'empty' }]), 'the launcher is told by a web message');
});

test('exit when empty: a root that never fills, or never appears, closes nothing', () => {
  const p = fakeAppPage();
  p.change();
  p.root = { childElementCount: 0 };
  p.change();
  p.change();
  assert.equal(p.closed, 0);
});

test('exit when empty: it watches the whole document, so a root added later is seen', () => {
  const p = fakeAppPage();
  assert.equal(p.observers.length, 1);
  assert.equal(p.observers[0].options.subtree, true);
  assert.equal(p.observers[0].options.childList, true);
});
