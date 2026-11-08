using System.Globalization;
using System.Text.Json;

namespace TvLauncher.Apps;

/// <summary>
/// The scripts the launcher runs inside an app's page, as tested in a spike on 2026-10-25.
/// Each walks shadow roots too, where players hide their video.
/// </summary>
internal static class PageScripts
{
    /// <summary>Whether a video with a picture is playing. Reads only: it never pauses or plays anything.</summary>
    public const string IsPlaying = "(() => { let p = false; const walk = (r) => { r.querySelectorAll('video').forEach(v => { if (v.videoWidth && !v.paused) p = true; }); r.querySelectorAll('*').forEach(e => e.shadowRoot && walk(e.shadowRoot)); }; walk(document); return p; })()";

    /// <summary>Pauses every video element. Only the YouTube fallback: YouTube's player survived a direct pause, Disney+'s did not.</summary>
    public const string DirectPause = "(() => { const walk = (r) => { r.querySelectorAll('video').forEach(v => v.pause()); r.querySelectorAll('*').forEach(e => e.shadowRoot && walk(e.shadowRoot)); }; walk(document); return 'paused'; })()";

    // Presses the player's own button with this label, its aria-label or its text: Channel 4's player ignores Space
    // and pauses from its Pause button (measured 2026-11-02). An element click, as a mouse click on it would be.
    private const string PressButtonScript = """
        (() => {
          const want = __LABEL__;
          let hit = null;
          const walk = (r) => {
            for (const b of r.querySelectorAll('button')) if (!hit && (b.getAttribute('aria-label') || b.textContent || '').trim() === want) hit = b;
            r.querySelectorAll('*').forEach(e => e.shadowRoot && walk(e.shadowRoot));
          };
          walk(document);
          if (hit) hit.click();
          return !!hit;
        })()
        """;

    /// <summary>Presses the button with this label. The label goes in as a JSON string, so no label can end the script.</summary>
    public static string PressButton(string label) => PressButtonScript.Replace("__LABEL__", JsonSerializer.Serialize(label));

    // Runs in every page before its own scripts, when the document has no root element at all (measured in Chromium:
    // documentElement and head are both null). So it waits for the root element, then adds the style once. WebView2
    // draws a classic 15 px scrollbar on Disney+ (measured), where Edge's app window did not; a TV has no use for one,
    // and the wheel and keys still scroll. Never throws, whatever the page is.
    public const string HideScrollbars = """
        (() => {
          const add = () => {
            try {
              const s = document.createElement('style');
              s.textContent = 'html, body { scrollbar-width: none; } ::-webkit-scrollbar { display: none; width: 0; height: 0; }';
              (document.head || document.documentElement).appendChild(s);
            } catch (e) { console.warn('tv scrollbars', e); }
          };
          try {
            if (document.documentElement) { add(); return; }
            const watch = new MutationObserver(() => {
              if (!document.documentElement) return;
              watch.disconnect();
              add();
            });
            watch.observe(document, { childList: true });
          } catch (e) { console.warn('tv scrollbars', e); }
        })();
        """;

    // Runs in every page of an app with ExitWhenEmpty, before the page's own scripts. Channel 4's TV app answers its own
    // "Are you sure you want to leave Channel 4? Yes" by emptying <div id="appRoot"> (unmountComponentAtNode) and then
    // calling a device exit that throws "exit is not supported" in a browser, which left a dark page (TV app code read
    // and owner's report, 2026-11-02). Once the element has had content and is empty again, the page tells the launcher
    // with a web message, which goes home (CloseRequested). window.close() does not do: the browser refuses it for a
    // page it did not open ("Scripts may close only the windows that were opened by them", 2026-11-02).
    // Never throws, whatever the page is.
    private const string ExitWhenEmptyScript = """
        (() => {
          const id = __ID__;
          let filled = false, closed = false;
          const check = () => {
            const root = document.getElementById(id);
            if (!root || closed) return;
            if (root.childElementCount > 0) { filled = true; return; }
            if (!filled) return;
            closed = true;
            watch.disconnect();
            window.chrome.webview.postMessage({ tvExit: 'empty' });
          };
          let watch;
          try {
            watch = new MutationObserver(() => { try { check(); } catch (e) { console.warn('tv exit', e); } });
            watch.observe(document, { childList: true, subtree: true });
          } catch (e) { console.warn('tv exit', e); }
        })();
        """;

    /// <summary>Tells the launcher the app exited once the element with this id has had content and is empty again. The id goes in as a JSON string.</summary>
    public static string ExitWhenEmpty(string elementId) => ExitWhenEmptyScript.Replace("__ID__", JsonSerializer.Serialize(elementId));

    // Runs in every page of an app with HidePlayReady, before the page's own scripts, and answers each PlayReady
    // request as a browser without PlayReady would. Channel 4 asks for PlayReady first; on a PC with a screen Edge's
    // PlayReady counts as unsafe (the dev PC's three monitors) it then refuses the programme ("Multiple Screens
    // Detected", channel4.com/4viewers/faqs?faqs=Edge). Refused, it falls back to Widevine, which plays: 1920x1080,
    // 0 dropped frames on the dev PC (2026-11-02). Never throws, whatever the page is.
    public const string HidePlayReady = """
        (() => {
          try {
            const original = navigator.requestMediaKeySystemAccess;
            if (typeof original !== 'function') return;
            navigator.requestMediaKeySystemAccess = function (keySystem, configs) {
              if (/playready/i.test(String(keySystem)))
                return Promise.reject(new DOMException('Unsupported keySystem or supportedConfigurations.', 'NotSupportedError'));
              return original.call(navigator, keySystem, configs);
            };
          } catch (e) { console.warn('tv playready', e); }
        })();
        """;

    // Runs in every page of an app with HideElements, before the page's own scripts, and adds one style that never
    // shows what the selector matches, as a userscript would. Prime Video's cookie notice is pinned to the bottom of the
    // screen over a long page, so the remote's arrows go from tile to tile and never reach it (measured 2026-11-07).
    // A selector the browser does not take hides nothing; one that no longer
    // matches (the site changed) hides nothing either, so the page is then as it was. Waits for the root element as
    // HideScrollbars does. Never throws, whatever the page is.
    private const string HideElementsScript = """
        (() => {
          const sel = __SELECTOR__;
          try {
            if (!CSS.supports('selector(' + sel + ')')) { console.warn('tv hide: not a selector the browser takes', sel); return; }
          } catch (e) { console.warn('tv hide', e); return; }
          const add = () => {
            try {
              const s = document.createElement('style');
              s.textContent = sel + ' { display: none !important; }';
              (document.head || document.documentElement).appendChild(s);
            } catch (e) { console.warn('tv hide', e); }
          };
          try {
            if (document.documentElement) { add(); return; }
            const watch = new MutationObserver(() => {
              if (!document.documentElement) return;
              watch.disconnect();
              add();
            });
            watch.observe(document, { childList: true });
          } catch (e) { console.warn('tv hide', e); }
        })();
        """;

    /// <summary>Never shows what the CSS selector matches. The selector goes in as a JSON string, so no selector can end the script.</summary>
    public static string HideElements(string selector) => HideElementsScript.Replace("__SELECTOR__", JsonSerializer.Serialize(selector));

    // Runs in every page of an app with ArrowNav, before the page's own scripts, so its key listener comes first. The
    // remote's arrows move focus to the nearest link or button that way, one at a time. Disney+'s own handler pages a
    // row five tiles per press, and Chromium's spatial navigation stops on empty divs between tiles and scrolls the
    // page (both measured 2026-10-30); neither sees a key this takes. The player keeps its own keys (each also nudges
    // a pointer so its controls show), and so does a text box for Left and Right.
    public const string ArrowNav = """
        (() => {
          const DIRS = { ArrowLeft: [-1, 0], ArrowRight: [1, 0], ArrowUp: [0, -1], ArrowDown: [0, 1] };
          const PICK = 'a[href], a[data-testid], button, input, select, textarea, [role="button"], [role="tab"], [role="link"], [tabindex]:not([tabindex="-1"])';
          const deep = () => { let a = document.activeElement; while (a && a.shadowRoot && a.shadowRoot.activeElement) a = a.shadowRoot.activeElement; return a; };
          // A carousel's scroll arrow: it controls a region and is not a tab, a menu or an expander. It is the mouse's
          // way along a row (the tiles scroll it for the remote), and Disney+ fades it in under a resting pointer,
          // where it would win the first press (measured 2026-10-30).
          const scrollArrow = (e) => e.hasAttribute('aria-controls') && e.getAttribute('role') !== 'tab'
            && !e.hasAttribute('aria-expanded') && !e.hasAttribute('aria-haspopup');
          const usable = (e) => {
            if (e.disabled || e.getAttribute('aria-disabled') === 'true' || e.closest('[inert]') || scrollArrow(e)) return false;
            const r = e.getBoundingClientRect();
            if (r.width < 4 || r.height < 4) return false;
            const s = getComputedStyle(e);
            return s.visibility !== 'hidden' && s.display !== 'none' && s.opacity !== '0';
          };
          // Cut out of sight by a parent that clips (a hidden slide). A parent that scrolls does not count: the next
          // tile off the edge of a row is reachable, and focusing it scrolls it in.
          const clipped = (e) => {
            const r = e.getBoundingClientRect();
            for (let p = e.parentElement; p && p !== document.body; p = p.parentElement) {
              const s = getComputedStyle(p);
              if (!/hidden|clip/.test(s.overflowX + s.overflowY)) continue;
              const q = p.getBoundingClientRect();
              if (r.right <= q.left || r.left >= q.right || r.bottom <= q.top || r.top >= q.bottom) return true;
            }
            return false;
          };
          // The Swiper carousel a tile is in (Channel 4's rows). It clips its tiles and moves them with a transform, not
          // by scrolling, so a tile past its edge is clipped, and focus alone leaves it out of sight (measured 2026-11-02).
          const carousel = (e) => { const c = e.closest('.swiper'); return c && c.swiper && typeof c.swiper.slideTo === 'function' ? c : null; };
          // In a bar fixed to the screen (the top menu): always on screen, so it would always look near.
          const pinned = (e) => { for (let p = e; p; p = p.parentElement) { const s = getComputedStyle(p).position; if (s === 'fixed' || s === 'sticky') return true; } return false; };
          const onScreen = (r) => r.right > 0 && r.left < innerWidth && r.bottom > 0 && r.top < innerHeight;
          const typing = (e) => e && (e.isContentEditable || /^(INPUT|TEXTAREA|SELECT)$/.test(e.tagName));

          // The best candidate: the closest one in sight, from the page before the top menu (unless already in it).
          // Along a row, a carousel's tile past its edge counts too: the carousel is told to show it.
          const rank = (found, fromBar, along) => {
            found.sort((a, b) => a.score - b.score);
            const shown = found.filter(f => !clipped(f.e) || (along && carousel(f.e)));
            return (shown.find(f => fromBar || !pinned(f.e)) || shown[0] || {}).e;
          };

          const next = (cur, d) => {
            const c = cur.getBoundingClientRect(), cx = c.left + c.width / 2, cy = c.top + c.height / 2;
            const found = [];
            for (const e of (cur.closest('[role="dialog"]') || document).querySelectorAll(PICK)) {
              if (e === cur || e.contains(cur) || cur.contains(e) || !usable(e)) continue;
              const r = e.getBoundingClientRect();
              // How far it lies in the direction pressed (edge to edge), and how far off to the side (centre to centre).
              const ahead = d[0] > 0 ? r.left - c.right : d[0] < 0 ? c.left - r.right : d[1] > 0 ? r.top - c.bottom : c.top - r.bottom;
              if (ahead < -Math.min(c.width, c.height) / 2) continue;
              // Left and Right stay in the row; Up and Down only go to what is on screen across.
              if (d[0] !== 0 && (r.bottom <= c.top || r.top >= c.bottom)) continue;
              if (d[1] !== 0 && (r.right <= 0 || r.left >= innerWidth)) continue;
              const side = d[0] !== 0 ? Math.abs(r.top + r.height / 2 - cy) : Math.abs(r.left + r.width / 2 - cx);
              found.push({ e, score: Math.max(ahead, 0) + side * 2 });
            }
            return rank(found, pinned(cur), d[0] !== 0);
          };

          // Nothing focused yet (a page just opened): start at the top left of the page, not in the top menu.
          const first = () => {
            const found = [];
            for (const e of document.querySelectorAll(PICK)) {
              if (!usable(e)) continue;
              const r = e.getBoundingClientRect();
              if (onScreen(r)) found.push({ e, score: r.top + r.left / 4 });
            }
            return rank(found, false);
          };

          // The player draws its control bar only for a pointer, never for a key (measured 2026-10-31), so in the player
          // each key also nudges a pointer at the middle of the screen, a pixel to one side and then the other.
          let side = 1;
          const nudge = () => {
            try {
              side = -side;
              const at = { bubbles: true, composed: true, clientX: Math.round(innerWidth / 2) + side, clientY: Math.round(innerHeight / 2) };
              const target = document.elementFromPoint(at.clientX, at.clientY) || document.body;
              target.dispatchEvent(new PointerEvent('pointermove', at));
              target.dispatchEvent(new MouseEvent('mousemove', at));
            } catch (e) { console.warn('tv nudge', e); }
          };

          window.addEventListener('keydown', (ev) => {
            if (location.pathname.includes('/play/') || document.fullscreenElement) { nudge(); return; }
            const d = DIRS[ev.key];
            if (!d || ev.altKey || ev.ctrlKey || ev.metaKey || ev.shiftKey) return;
            const cur = deep();
            if (typing(cur) && d[0] !== 0) return;
            ev.preventDefault();
            ev.stopImmediatePropagation();
            try {
              const start = !cur || cur === document.body || cur === document.documentElement;
              const to = start ? first() : next(cur, d);
              if (!to) return;
              // A carousel tile not wholly in the carousel's box (hidden, or cut by its edge) is slid into sight.
              // Scrolling into view would scroll the carousel's clipping box itself, behind the carousel's back.
              const box = carousel(to), r = to.getBoundingClientRect(), q = box && box.getBoundingClientRect();
              const cut = box && (r.left < q.left - 1 || r.right > q.right + 1);
              const slide = cut ? Array.prototype.indexOf.call(box.swiper.slides, to.closest('.swiper-slide')) : -1;
              to.focus({ preventScroll: true });
              if (slide >= 0) box.swiper.slideTo(slide);
              else to.scrollIntoView({ block: 'center', inline: 'nearest', behavior: 'smooth' });
            } catch (e) { console.warn('tv arrows', e); }
          }, true);
        })();
        """;

    // Runs in every YouTube page with PhoneMic, before the page's own scripts, and hands YouTube's voice search the
    // phone's sound in place of a microphone. Measured in a spike on 2026-10-31:
    // YouTube asks getUserMedia for audio and recognises whatever stream comes back, here a 16 kHz
    // MediaStreamAudioDestinationNode fed with the samples; it hides its microphone unless enumerateDevices lists an
    // audioinput, and the mini PC has none, so one is always listed. The launcher arms the next audio request and
    // then sends the key that makes YouTube listen (VK 12290). The "start" message and the key travel on different
    // channels, so the launcher arms through window[Symbol.for('tvMic.arm')] with ExecuteScriptAsync and presses the
    // key only once that returns true; "start" does the same. YouTube ended the listening about 0.6 s after the
    // speech ended (spike, item 8), but whether it calls stop() on the track was not checked: if it does, the page
    // posts "stopped"; if not, the stream ends only on "end" (the phone's stop or the launcher's 15 s limit).
    // Without chrome.webview (no launcher) it changes nothing, and while idle it does no work. Never throws,
    // whatever the message.
    public const string PhoneMic = """
        (() => {
          try {
            const wv = window.chrome && window.chrome.webview;
            const md = navigator.mediaDevices;
            if (!wv || !md || typeof md.getUserMedia !== 'function') return;
            const RATE = 16000, HOLD = 15 * RATE;
            let armed = false, held = [], heldSamples = 0, live = null;

            const warn = (e) => console.warn('tv mic', e);
            // PCM16 little-endian mono in base64, as samples from -1 to 1; null for anything else.
            const decode = (pcm) => {
              if (typeof pcm !== 'string') return null;
              const bin = atob(pcm), n = bin.length >> 1;
              if (!n) return null;
              const f = new Float32Array(n);
              for (let i = 0; i < n; i++) {
                const v = bin.charCodeAt(2 * i) | (bin.charCodeAt(2 * i + 1) << 8);
                f[i] = (v > 32767 ? v - 65536 : v) / 32768;
              }
              return f;
            };
            // Each piece starts where the last one ends, or now after a gap in the network.
            const play = (s, f) => {
              const b = s.ctx.createBuffer(1, f.length, RATE);
              b.getChannelData(0).set(f);
              const src = s.ctx.createBufferSource();
              src.buffer = b;
              src.connect(s.dest);
              const at = Math.max(s.next, s.ctx.currentTime);
              src.start(at);
              s.next = at + f.length / RATE;
            };
            const disarm = () => { armed = false; held = []; heldSamples = 0; };
            // Ends the stream YouTube has, once, however it ends; tells the launcher unless it asked for a new one.
            const finish = (s, tell) => {
              if (!s || s.done) return;
              s.done = true;
              if (live === s) live = null;
              try { s.stops.forEach(stop => stop()); } catch (e) { warn(e); }
              try { s.ctx.close().catch(() => {}); } catch (e) { warn(e); }
              if (tell) { try { wv.postMessage({ tvMic: 'stopped' }); } catch (e) { warn(e); } }
            };
            const open = () => {
              const ctx = new AudioContext({ sampleRate: RATE });
              const dest = ctx.createMediaStreamDestination();
              const s = { ctx, dest, next: 0, done: false, stops: [] };
              for (const t of dest.stream.getTracks()) {
                const stop = t.stop.bind(t);
                s.stops.push(stop);
                t.stop = () => { stop(); finish(s, true); };
              }
              if (ctx.state === 'suspended') ctx.resume().catch(() => {});
              live = s;
              // Sound the phone sent before YouTube asked: the key and YouTube's request race the phone.
              held.forEach(f => play(s, f));
              disarm();
              return dest.stream;
            };

            // A stream still open is closed without telling the launcher, which has just started a new session.
            const arm = () => { finish(live, false); disarm(); armed = true; return true; };
            Object.defineProperty(window, Symbol.for('tvMic.arm'), { value: arm, enumerable: false, writable: false });

            const realGetUserMedia = md.getUserMedia;
            md.getUserMedia = function (c) {
              if (armed && c && c.audio && !c.video) {
                try { return Promise.resolve(open()); } catch (e) { warn(e); disarm(); }
              }
              return realGetUserMedia.apply(this, arguments);
            };

            const phone = Object.create(typeof MediaDeviceInfo === 'function' ? MediaDeviceInfo.prototype : Object.prototype, {
              deviceId: { value: 'tv-phone', enumerable: true },
              kind: { value: 'audioinput', enumerable: true },
              label: { value: 'Phone', enumerable: true },
              groupId: { value: 'tv-phone', enumerable: true },
              toJSON: { value: () => ({ deviceId: 'tv-phone', kind: 'audioinput', label: 'Phone', groupId: 'tv-phone' }) },
            });
            const realEnumerate = md.enumerateDevices;
            md.enumerateDevices = function () {
              let real;
              try { real = Promise.resolve(realEnumerate.apply(this, arguments)); } catch (e) { real = Promise.resolve([]); }
              return real.catch(() => []).then(list => Array.from(list || []).concat(phone));
            };

            // YouTube never asked in the spike; answered anyway, as a box with a microphone would.
            const perms = navigator.permissions;
            if (perms && typeof perms.query === 'function') {
              const realQuery = perms.query;
              perms.query = function (d) {
                if (d && d.name === 'microphone') {
                  return Promise.resolve({ name: 'microphone', state: 'granted', onchange: null,
                    addEventListener() {}, removeEventListener() {}, dispatchEvent() { return false; } });
                }
                return realQuery.apply(this, arguments);
              };
            }

            wv.addEventListener('message', (e) => {
              try {
                const m = e && e.data;
                if (!m || typeof m !== 'object') return;
                if (m.tvMic === 'start') {
                  arm();
                } else if (m.tvMic === 'audio') {
                  if (!live && !armed) return;
                  const f = decode(m.pcm);
                  if (!f) return;
                  if (live) play(live, f);
                  else if (heldSamples + f.length <= HOLD) { held.push(f); heldSamples += f.length; }
                } else if (m.tvMic === 'end') {
                  disarm();
                  finish(live, true);
                }
              } catch (err) { warn(err); }
            });
          } catch (e) { console.warn('tv mic', e); }
        })();
        """;

    // Runs in every page before its own scripts: each <video> is routed through a gain and a limiter when it
    // first plays, so the boost survives reloads and new titles.
    private const string BoostScript = """
        (() => {
          const state = window.__tvBoost = { gain: __GAIN__, nodes: [] };
          state.set = (g) => { state.gain = g; state.nodes.forEach(n => n.gain.value = g); return g; };
          const attach = (v) => {
            if (!(v instanceof HTMLVideoElement) || v.__tvBoost) return;
            v.__tvBoost = true;
            const ctx = new AudioContext();
            const src = ctx.createMediaElementSource(v);
            const gain = ctx.createGain(); gain.gain.value = state.gain;
            const lim = ctx.createDynamicsCompressor();
            lim.threshold.value = -3; lim.knee.value = 0; lim.ratio.value = 20; lim.attack.value = 0.003; lim.release.value = 0.25;
            src.connect(gain); gain.connect(lim); lim.connect(ctx.destination);
            state.nodes.push(gain);
            v.__tvCtx = ctx;
          };
          const play = HTMLMediaElement.prototype.play;
          HTMLMediaElement.prototype.play = function () {
            try { attach(this); if (this.__tvCtx) this.__tvCtx.resume(); } catch (e) { console.warn('tv boost', e); }
            return play.apply(this, arguments);
          };
        })();
        """;

    /// <summary>The boost script for new pages, starting at <paramref name="gain"/> (1 is no boost).</summary>
    public static string Boost(double gain) => BoostScript.Replace("__GAIN__", Number(gain));

    /// <summary>Changes the gain in the page already loaded; null when that page has no boost yet.</summary>
    public static string SetBoostGain(double gain) => $"window.__tvBoost ? window.__tvBoost.set({Number(gain)}) : null";

    // A German Windows would write 1.5 as "1,5", which is not a number in JavaScript.
    private static string Number(double value) => value.ToString(CultureInfo.InvariantCulture);
}
