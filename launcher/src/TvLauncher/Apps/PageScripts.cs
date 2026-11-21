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

    // The player layer, as ArrowNav finds it: a video filling the screen inside a layer fixed over the page, a player
    // that keeps the page's address (Prime Video, 2026-11-14). Null when there is none.
    private const string PlayerLayerScript = """
        const layer = (() => {
          for (const v of document.querySelectorAll('video')) {
            const r = v.getBoundingClientRect();
            if (r.width < innerWidth * 0.9 || r.height < innerHeight * 0.9) continue;
            for (let p = v; p; p = p.parentElement) if (getComputedStyle(p).position === 'fixed') return p;
          }
          return null;
        })();
        """;

    // Whether a player layer is open. Prime Video's player opens over the title page without a history entry, so
    // going back in history from it left the season picked for the series' first (2026-11-15); Escape closes it.
    private const string InPlayerScript = """
        (() => {
          __LAYER__
          return !!layer;
        })()
        """;

    // Presses a button of the player layer by its label, its aria-label or its text. Prime Video draws its controls
    // only after pointer activity (none in the page while idle, 2026-11-15), so a pointer move goes first and the
    // press a moment later. True when there is a player layer, whether or not it then has the button (an advert
    // shows Go ad free in place of Next Episode).
    private const string PlayerButtonScript = """
        (() => {
          const want = __LABEL__;
          __LAYER__
          if (!layer) return false;
          const at = { bubbles: true, composed: true, clientX: Math.round(innerWidth / 2) + 2, clientY: Math.round(innerHeight / 2) };
          const target = document.elementFromPoint(at.clientX, at.clientY) || document.body;
          target.dispatchEvent(new PointerEvent('pointermove', at));
          target.dispatchEvent(new MouseEvent('mousemove', at));
          setTimeout(() => {
            for (const b of layer.querySelectorAll('button'))
              if ((b.getAttribute('aria-label') || b.textContent || '').trim() === want) { b.click(); return; }
          }, 400);
          return true;
        })()
        """;

    /// <summary>Whether a player layer is open over the page (Prime Video's player).</summary>
    public static readonly string InPlayer = InPlayerScript.Replace("__LAYER__", PlayerLayerScript);

    /// <summary>Presses the player layer's button with this label, after showing its controls; false when no player is open.</summary>
    public static string PressPlayerButton(string label) =>
        PlayerButtonScript.Replace("__LAYER__", PlayerLayerScript).Replace("__LABEL__", JsonSerializer.Serialize(label));

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

    /// <summary>Tells the page the remote's 3-lines key was pressed; the arrows' script opens the item's hover card.</summary>
    public const string ItemMenu = "window.dispatchEvent(new Event('tv-menu'))";

    // Runs in every page of an app with ArrowNav, before the page's own scripts, so its key listener comes first. The
    // remote's arrows move focus to the nearest link or button that way, one at a time. Disney+'s own handler pages a
    // row five tiles per press, and Chromium's spatial navigation stops on empty divs between tiles and scrolls the
    // page (both measured 2026-10-30); neither sees a key this takes. The player keeps its own keys (each also nudges
    // a pointer so its controls show), and so does a text box for Left and Right.
    private const string ArrowNavScript = """
        (() => {
          const AFTER_PLAYER = __AFTER_PLAYER__;
          const DIRS = { ArrowLeft: [-1, 0], ArrowRight: [1, 0], ArrowUp: [0, -1], ArrowDown: [0, 1] };
          const PICK = 'a[href], a[data-testid], button, input, select, textarea, [role="button"], [role="tab"], [role="link"], [tabindex]:not([tabindex="-1"])';
          const deep = () => { let a = document.activeElement; while (a && a.shadowRoot && a.shadowRoot.activeElement) a = a.shadowRoot.activeElement; return a; };
          // A carousel's scroll arrow: it controls a region and is not a tab, a menu or an expander. It is the mouse's
          // way along a row (the tiles scroll it for the remote), and Disney+ fades it in under a resting pointer,
          // where it would win the first press (measured 2026-10-30).
          const scrollArrow = (e) => e.hasAttribute('aria-controls') && e.getAttribute('role') !== 'tab'
            && !e.hasAttribute('aria-expanded') && !e.hasAttribute('aria-haspopup');
          // A form field moved off the screen sideways and drawn by its label: Prime Video's season selector is a
          // checkbox far to the left of the page (2026-11-15). It is measured, and scrolled into view, by its label.
          const face = (e) => {
            const r = e.getBoundingClientRect();
            if (e.labels && (r.right <= 0 || r.left >= innerWidth))
              for (const l of e.labels) { const q = l.getBoundingClientRect(); if (q.width >= 4 && q.height >= 4) return l; }
            return e;
          };
          const usable = (e) => {
            if (e.disabled || e.getAttribute('aria-disabled') === 'true' || e.closest('[inert]') || scrollArrow(e)) return false;
            const r = face(e).getBoundingClientRect();
            if (r.width < 4 || r.height < 4) return false;
            const s = getComputedStyle(e);
            // At opacity 0 a button is not there yet (a carousel arrow, a hover card's button), but a link still leads
            // somewhere: Prime Video lays each episode's play link over its picture at opacity 0 (2026-11-15).
            if (s.visibility === 'hidden' || s.display === 'none' || (s.opacity === '0' && !(e.tagName === 'A' && e.hasAttribute('href')))) return false;
            // Inside something at opacity 0: Prime Video stacks its banner's slides in one place and hides all but
            // one that way (measured 2026-11-15), and the focus would go to buttons nobody can see.
            for (let p = e.parentElement; p; p = p.parentElement) if (getComputedStyle(p).opacity === '0') return false;
            return true;
          };
          // The items that can take the focus in a part of the page. A link the page keeps out of the tab order
          // (tabindex -1) stretched behind other items it does not hold is left out: Prime Video's banner image lies
          // behind the banner's title and buttons and shows no focus ring, so on it the focus seemed gone (2026-11-15).
          // An item in a bar fixed to the screen is not on the page under it: scrolled up, Prime's tiles pass under
          // its menu bar. Each item measures the same for the whole key press.
          const items = (root) => {
            const all = [];
            for (const e of root.querySelectorAll(PICK)) if (usable(e)) all.push({ e, r: face(e).getBoundingClientRect() });
            const behind = (a, b) => a !== b && !a.e.contains(b.e) && b.r.left >= a.r.left && b.r.right <= a.r.right
              && b.r.top >= a.r.top && b.r.bottom <= a.r.bottom && b.r.width * b.r.height * 2 < a.r.width * a.r.height
              && (b.pinned ??= pinned(b.e)) === (a.pinned ??= pinned(a.e));
            return all.filter(a => a.e.getAttribute('tabindex') !== '-1' || !all.some(b => behind(a, b)));
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
          // A row that clips its tiles and slides them with a button of its own at each end, drawn only for a mouse:
          // Netflix's "See previous titles" and "See more titles" (2026-11-21). Focus alone leaves a tile past the edge
          // out of sight there (measured), and a click on the button slides the row a page. For a tile the row cuts
          // or hides on the side pressed (dir 1 right, -1 left), the button: one inside a box that clips the tile on
          // that side, level with the tile, at that end of the box, and measuring something (Netflix's left one
          // measures nothing while no tile lies before the first). Null when there is none. A scroll arrow tied to a
          // region (aria-controls, Disney+'s) is not one: Disney+'s banner keeps its hidden slides out of reach.
          const pager = (e, dir) => {
            const r = face(e).getBoundingClientRect();
            for (let p = e.parentElement, n = 0; p && p !== document.body && n < 8; p = p.parentElement, n++) {
              if (!/hidden|clip/.test(getComputedStyle(p).overflowX)) continue;
              const q = p.getBoundingClientRect();
              if (dir > 0 ? r.right <= q.right + 1 : r.left >= q.left - 1) continue;
              for (const b of p.querySelectorAll('button')) {
                if (b.tagName !== 'BUTTON' || b.disabled || b.contains(e) || scrollArrow(b)) continue;
                const k = b.getBoundingClientRect();
                if (k.width < 4 || k.height < 4 || k.bottom <= r.top || k.top >= r.bottom) continue;
                if (dir > 0 ? k.right >= q.right - 150 && k.left > q.left + q.width / 2 : k.left <= q.left + 150 && k.right < q.left + q.width / 2) return b;
              }
            }
            return null;
          };
          // A dialog the page marks modal and shows: the last one wins. Netflix's detail panel is one, and OK on a card
          // opens it with the focus left on the card behind, so the arrows moved between the cards under it
          // (2026-11-21). One that measures nothing is not open: Netflix keeps a privacy dialog in the page all along.
          const openModal = () => {
            const all = document.querySelectorAll('[aria-modal="true"]');
            for (let i = all.length - 1; i >= 0; i--) {
              const m = all[i], r = m.getBoundingClientRect(), s = getComputedStyle(m);
              if (r.width >= 4 && r.height >= 4 && s.visibility !== 'hidden' && s.display !== 'none' && s.opacity !== '0') return m;
            }
            return null;
          };
          // In a bar fixed to the screen (the top menu): always on screen, so it would always look near.
          const pinned = (e) => { for (let p = e; p; p = p.parentElement) { const s = getComputedStyle(p).position; if (s === 'fixed' || s === 'sticky') return true; } return false; };
          const onScreen = (r) => r.right > 0 && r.left < innerWidth && r.bottom > 0 && r.top < innerHeight;
          const typing = (e) => e && (e.isContentEditable || /^(INPUT|TEXTAREA|SELECT)$/.test(e.tagName));

          // The best candidate: the closest one in sight, from the page before the top menu (unless already in it).
          // Along a row (along: 1 right, -1 left), a tile past a carousel's edge counts too, and one past the edge of a
          // row with a button on that side: the carousel is told to show it, or the button is pressed.
          const rank = (found, fromBar, along) => {
            found.sort((a, b) => a.score - b.score);
            const shown = found.filter(f => !clipped(face(f.e)) || (along && (carousel(f.e) || pager(f.e, along))));
            return (shown.find(f => fromBar || !pinned(f.e)) || shown[0] || {}).e;
          };

          const next = (cur, d, root) => {
            const c = face(cur).getBoundingClientRect(), cx = c.left + c.width / 2, cy = c.top + c.height / 2;
            const found = [], fromBar = pinned(cur);
            for (const { e, r } of items(root || cur.closest('[role="dialog"]') || document)) {
              if (e === cur || e.contains(cur) || cur.contains(e)) continue;
              // Laid over the item in focus, not beside it: Prime Video's focus card puts the episode's Download button
              // in the corner of its picture (2026-11-15).
              if (r.left >= c.left && r.right <= c.right && r.top >= c.top && r.bottom <= c.bottom) continue;
              // How far it lies in the direction pressed (edge to edge), and how far off to the side (centre to centre).
              const ahead = d[0] > 0 ? r.left - c.right : d[0] < 0 ? c.left - r.right : d[1] > 0 ? r.top - c.bottom : c.top - r.bottom;
              if (ahead < -Math.min(c.width, c.height) / 2) continue;
              // Left and Right stay in the row; Up and Down only go to what is on screen across.
              if (d[0] !== 0 && (r.bottom <= c.top || r.top >= c.bottom)) continue;
              if (d[1] !== 0 && (r.right <= 0 || r.left >= innerWidth)) continue;
              // Never more than a screen away: Prime Video adds rows only as the page nears its end, and pressing
              // quickly, Down from the last row there was went to the footer far below (2026-11-15).
              if (d[1] !== 0 && ahead > innerHeight) continue;
              // Down never goes from the page into a bar fixed to the screen: with the page scrolled up past the menu
              // bar, the bar lies below what is focused (Prime Video, 2026-11-15). Up reaches it.
              if (d[1] > 0 && !fromBar && pinned(e)) continue;
              // How far off to the side: from its centre to the nearest edge of the current item, none when it lies
              // across from it, so from an item stretched across the page the nearest one ahead wins (Prime Video's
              // title page, 2026-11-14: its logo link spans the page, and the synopsis under its middle beat Play).
              // How far apart the centres are only breaks ties.
              const mid = d[0] !== 0 ? r.top + r.height / 2 : r.left + r.width / 2;
              // Also none when it spans the current item's middle: from a tab under a wide button, the button
              // (Prime's Go ad free over its Episodes tab, 2026-11-15).
              const spans = d[0] !== 0 ? r.top <= cy && r.bottom >= cy : r.left <= cx && r.right >= cx;
              const off = spans ? 0 : d[0] !== 0 ? Math.max(c.top - mid, mid - c.bottom, 0) : Math.max(c.left - mid, mid - c.right, 0);
              const centres = Math.abs(mid - (d[0] !== 0 ? cy : cx));
              // How well the two line up across: the share of the wider one that the other covers. Lining up with
              // none of it counts as much as lying one more of the current item's own sizes away, so the tile under a
              // tile beats a small link between the rows, and Watch now under a title beats the small Watchlist button
              // beside it (Prime Video, 2026-11-15). From an item stretched across the page, whose size that way is
              // small, the nearest one ahead still wins.
              const shared = d[0] !== 0 ? Math.min(r.bottom, c.bottom) - Math.max(r.top, c.top) : Math.min(r.right, c.right) - Math.max(r.left, c.left);
              const share = Math.max(shared, 0) / (d[0] !== 0 ? Math.max(r.height, c.height) : Math.max(r.width, c.width));
              const size = d[0] !== 0 ? c.width : c.height;
              found.push({ e, ahead: Math.max(ahead, 0), score: Math.max(ahead, 0) + off * 2 + centres / 10 + (1 - share) * size });
            }
            const to = rank(found, fromBar, d[0]);
            // Up and Down never pass a row of tabs, which switches what is shown under it: they stop on its selected
            // tab, as on a TV. From Prime Video's episodes, Up went to whichever of Episodes, Related and Details lay
            // nearest, or over them to the wide synopsis above (2026-11-15).
            if (d[1] !== 0 && to && cur.getAttribute('role') !== 'tab' && !(to.getAttribute('role') === 'tab' && to.getAttribute('aria-selected') === 'true')) {
              const past = found.find(f => f.e === to).ahead;
              // Tabs in a bar fixed to the screen are a menu, not a row in the page (Disney+'s For You, Disney+ and Hulu).
              const tab = found.find(f => f.e.getAttribute('role') === 'tab' && f.e.getAttribute('aria-selected') === 'true' && f.ahead <= past
                && (fromBar || !pinned(f.e)));
              if (tab) return tab.e;
            }
            return to;
          };

          // Nothing focused yet (a page just opened): start at the top left of the page, not in the top menu. In a
          // dialog, on its first item, as a browser focuses a dialog it opens: Netflix's panel has Play first and its
          // close button higher up, to the right.
          const first = (root) => {
            const found = [];
            let i = 0;
            for (const { e, r } of items(root || document)) if (onScreen(r)) found.push({ e, score: root ? i++ : r.top + r.left / 4 });
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

          // A video filling the screen inside a layer fixed over the page: a player that keeps the page's address
          // (Prime Video plays over its title page, 2026-11-14). Returns that layer. A trailer in the page itself is
          // not fixed, and the hidden layer measures nothing.
          const playerLayer = () => {
            for (const v of document.querySelectorAll('video')) {
              const r = v.getBoundingClientRect();
              if (r.width < innerWidth * 0.9 || r.height < innerHeight * 0.9) continue;
              for (let p = v; p; p = p.parentElement) if (getComputedStyle(p).position === 'fixed') return p;
            }
            return null;
          };

          // Prime's player listens for keys only with the focus inside it, and started from a play address the focus
          // stays on the page, so the remote's keys and the launcher's pause press reached nothing (2026-11-14). The
          // focus goes into the layer, and the key with it: Prime takes a key a script sends (measured).
          const intoPlayer = (layer, ev) => {
            if (layer.contains(deep())) return;
            layer.focus({ preventScroll: true });
            if (document.activeElement !== layer) return;
            ev.preventDefault();
            ev.stopImmediatePropagation();
            layer.dispatchEvent(new KeyboardEvent('keydown', { key: ev.key, code: ev.code, keyCode: ev.keyCode, which: ev.which,
              altKey: ev.altKey, ctrlKey: ev.ctrlKey, metaKey: ev.metaKey, shiftKey: ev.shiftKey, bubbles: true, cancelable: true, composed: true }));
          };

          // The buttons in a player layer, picked with Up and Down. Prime Video's player takes Left and Right to seek and
          // Up and Down for its own volume, so no key reached its buttons: Next Episode, the Next Up card's Hide, Like
          // (owner, 2026-11-15; the remote has its own volume keys). It draws them only after pointer activity, which
          // each key in the player sends. Up or Down picks the one nearest the bottom middle (pause), then the arrows
          // move between them and OK presses one; Up past the top row or Down past the bottom row goes back to
          // watching. Left, Right and OK stay the player's while nothing is picked, and again once the controls fade.
          const startIn = (layer) => {
            const found = [];
            for (const { e, r } of items(layer)) if (onScreen(r))
              found.push({ e, score: Math.hypot(r.left + r.width / 2 - innerWidth / 2, r.top + r.height / 2 - innerHeight) });
            return rank(found, true);
          };
          // Prime draws no focus mark on its player buttons (outline none, a clear background, 2026-11-15): the
          // picked one gets the launcher's own.
          let marked = null;
          const pickStyle = document.createElement('style');
          pickStyle.textContent = '[data-tv-pick] { outline: 3px solid #fff !important; outline-offset: 4px !important; }';
          const mark = (e) => {
            if (marked && marked !== e) marked.removeAttribute('data-tv-pick');
            marked = e;
            if (!e) return;
            if (!pickStyle.parentNode) (document.head || document.documentElement).appendChild(pickStyle);
            e.setAttribute('data-tv-pick', '');
          };
          const pickKey = (layer, ev) => {
            const d = DIRS[ev.key];
            if ((!d && ev.key !== 'Enter') || ev.altKey || ev.ctrlKey || ev.metaKey || ev.shiftKey) return false;
            const cur = deep();
            const picked = cur && cur !== layer && items(layer).some(i => i.e === cur);
            if (!picked && (!d || d[1] === 0)) return false;
            ev.preventDefault();
            ev.stopImmediatePropagation();
            if (picked && !d) { cur.click(); afterPlayer(layer); return true; }
            if (picked) {
              const to = next(cur, d, layer);
              if (to) { to.focus({ preventScroll: true }); mark(to); }
              else if (d[1] !== 0) { layer.focus({ preventScroll: true }); mark(null); }
              return true;
            }
            // The controls come a moment after the launcher's mouse move, so a press with none yet tries again.
            const pick = () => { const to = startIn(layer); if (to) { to.focus({ preventScroll: true }); mark(to); } return !!to; };
            const later = (waits) => setTimeout(() => {
              try { if (!pick() && waits.length > 1) later(waits.slice(1)); } catch (e) { console.warn('tv player buttons', e); }
            }, waits[0]);
            if (!pick()) later([250, 350]);
            return true;
          };

          // Once the player layer closes (Back, or its own Close player picked with OK), the focus goes to the page
          // button FocusAfterPlayer names. Prime Video left it on the page's body, so the next arrow started again at
          // the top of the title page (2026-11-21); the owner wants its Resume button. Only while nothing on the page
          // has the focus, and only for a layer that closes within about a second. Prime then redraws the page and
          // drops the focus to the body again (Resume 281 ms, BODY 361 ms after Back), so it looks again until about
          // 1.4 s, and stops once anything else has the focus (the viewer moved on).
          const afterPlayer = (layer) => {
            if (!AFTER_PLAYER) return;
            const later = (waits) => setTimeout(() => {
              try {
                const more = () => { if (waits.length > 1) later(waits.slice(1)); };
                if (playerLayer()) { more(); return; }
                const cur = deep(), to = document.querySelector(AFTER_PLAYER);
                if (cur && cur === to) { more(); return; }
                if (cur && cur !== document.body && cur !== document.documentElement && !layer.contains(cur)) return;
                if (!to || !usable(to)) return;
                to.focus({ preventScroll: true });
                const r = face(to).getBoundingClientRect();
                placed = { e: to, y: r.top + scrollY };
                // Well on screen it stays put; else to the middle, as the arrows do: scrolled only as far as showed it,
                // Resume ended under Prime's fixed menu bar (back from an episode further down, 2026-11-21).
                if (r.top < innerHeight * 0.1 || r.bottom > innerHeight * 0.9)
                  face(to).scrollIntoView({ block: 'center', inline: 'nearest', behavior: 'instant' });
                more();
              } catch (e) { console.warn('tv after player', e); }
            }, waits[0]);
            later([150, 250, 400, 600]);
          };
          // Prime Video draws its controls only for a real mouse move: a DevTools mouse move brought them, scripted
          // pointer and mouse events did not (measured 2026-11-15). So the page asks the launcher for one, at the
          // middle of the screen and a pixel to one side and then the other, so each one is a movement.
          let wakeSide = 1;
          const wake = () => {
            const wv = window.chrome && window.chrome.webview;
            if (!wv) return;
            wakeSide = -wakeSide;
            wv.postMessage({ tvPointer: 'wake', x: Math.round(innerWidth / 2) + wakeSide, y: Math.round(innerHeight / 2) });
          };

          // A mouse pointer resting over the page does not mix with the arrows: as they scroll the page, the tiles
          // pass under it and the page reacts to it. Prime Video's hover cards took the focus from the tile the
          // arrows had reached, so the next press started again at the top, and made the rows grow (2026-11-15).
          // While the arrows are in use the page ignores the pointer; moving the mouse gives it back. A mouse event
          // whose screen position has not changed is not a move.
          const remote = document.createElement('style');
          remote.textContent = 'html.tv-keys, html.tv-keys * { pointer-events: none !important; }';
          const keysMode = (on) => {
            const html = document.documentElement;
            if (!html) return;
            if (on && !remote.parentNode) (document.head || html).appendChild(remote);
            if (on) html.classList.add('tv-keys'); else html.classList.remove('tv-keys');
          };
          // Where on the page (not on the screen) the arrows last put the focus, and when the last arrow came.
          let placed = null, last = -Infinity;
          let at = null;
          window.addEventListener('mousemove', (ev) => {
            if (!ev.isTrusted) return;
            const now = ev.screenX + ',' + ev.screenY;
            if (at !== null && now !== at) keysMode(false);
            at = now;
          }, true);

          // The remote's 3-lines key (the launcher's ItemMenu sends 'tv-menu'): the hover card a page shows for the item
          // under a resting mouse, opened for the item in focus, with the focus moved into it; pressed again, or an
          // arrow out of the card, closes it. Prime Video shows Resume, the trailer, its watchlist and hide buttons
          // only there (owner, 2026-11-15; a scripted hover opens it within 200 ms, a scripted leave closes it).
          let card = null;
          const point = (e, types, x, y) => {
            for (const t of types)
              e.dispatchEvent(new (t.startsWith('pointer') ? PointerEvent : MouseEvent)(t, { bubbles: !/enter|leave/.test(t), cancelable: true,
                composed: true, clientX: x, clientY: y, view: window, relatedTarget: /out|leave/.test(t) ? document.body : null }));
          };
          const LEAVE = ['pointerout', 'pointerleave', 'mouseout', 'mouseleave'];
          const closeCard = (refocus) => {
            if (!card) return;
            const e = card.e;
            card = null;
            point(e, LEAVE, 0, 0);
            if (refocus && e.isConnected) e.focus({ preventScroll: true });
          };
          window.addEventListener('tv-menu', () => {
            try {
              if (card) { closeCard(true); return; }
              const e = deep();
              if (!e || e === document.body || e === document.documentElement) return;
              const before = new Set(items(document).map(i => i.e));
              const r = face(e).getBoundingClientRect();
              point(e, ['pointerover', 'pointerenter', 'mouseover', 'mouseenter', 'pointermove', 'mousemove'],
                Math.round(r.left + r.width / 2), Math.round(r.top + r.height / 2));
              setTimeout(() => {
                try {
                  const fresh = items(document).filter(i => !before.has(i.e));
                  if (!fresh.length) { point(e, LEAVE, 0, 0); return; }
                  card = { e, items: new Set(fresh.map(i => i.e)) };
                  // The first of the card, not the picture it lays over the item.
                  const apart = fresh.filter(i => i.r.top >= r.bottom || i.r.bottom <= r.top || i.r.left >= r.right || i.r.right <= r.left);
                  const pick = (apart.length ? apart : fresh).sort((a, b) => (a.r.top + a.r.left / 4) - (b.r.top + b.r.left / 4))[0];
                  pick.e.focus({ preventScroll: true });
                  placed = { e: pick.e, y: pick.r.top + scrollY };
                } catch (err) { console.warn('tv menu', err); }
              }, 300);
            } catch (err) { console.warn('tv menu', err); }
          });

          window.addEventListener('keydown', (ev) => {
            const layer = playerLayer();
            // A player page of its own: Disney+'s /play/, Netflix's /watch/ (Netflix seeks on Left and Right and sets
            // its volume on Up and Down itself; its video is in layers that are not fixed, 2026-11-21).
            if (/\/(play|watch)\//.test(location.pathname) || document.fullscreenElement || layer) {
              try { keysMode(false); } catch (e) { console.warn('tv pointer', e); }
              nudge();
              try { if (layer) wake(); } catch (e) { console.warn('tv player wake', e); }
              if (layer && ev.key === 'Escape') afterPlayer(layer);
              try { if (layer && pickKey(layer, ev)) return; } catch (e) { console.warn('tv player buttons', e); }
              try { if (layer) intoPlayer(layer, ev); } catch (e) { console.warn('tv player focus', e); }
              return;
            }
            if (ev.altKey || ev.ctrlKey || ev.metaKey || ev.shiftKey) return;
            const cur = deep();
            // OK on a checkbox ticks it: a page ticks one with Space, not Enter, and the remote has no Space.
            if (ev.key === 'Enter' && cur && cur.tagName === 'INPUT' && (cur.type === 'checkbox' || cur.type === 'radio')) {
              ev.preventDefault();
              ev.stopImmediatePropagation();
              cur.click();
              return;
            }
            const d = DIRS[ev.key];
            if (!d) return;
            if (typing(cur) && d[0] !== 0) return;
            ev.preventDefault();
            ev.stopImmediatePropagation();
            try { keysMode(true); } catch (e) { console.warn('tv pointer', e); }
            // Someone is there: Prime Video fades its title page's details out while the trailer plays and brings them
            // back on a mouse move, but the arrows take the key before the page sees it (2026-11-15). A scripted move,
            // so the remote mode stays.
            try { document.body.dispatchEvent(new MouseEvent('mousemove', { bubbles: true, clientX: Math.round(innerWidth / 2), clientY: Math.round(innerHeight / 2) })); }
            catch (e) { console.warn('tv wake', e); }
            try {
              // Far out of sight counts as nothing focused when the page grew or moved under the focus: Prime Video
              // adds rows above its footer, which Down had reached while it was the end of the page (2026-11-15). Not
              // when the arrows only ran ahead of the scroll (a held key): the focus is where they put it on the page.
              const now = performance.now(), quick = now - last < 400;
              last = now;
              const nothing = !cur || cur === document.body || cur === document.documentElement;
              const c = nothing ? null : face(cur).getBoundingClientRect();
              const away = c && (c.top > innerHeight * 1.5 || c.bottom < -innerHeight / 2)
                && !(placed && placed.e === cur && Math.abs(c.top + scrollY - placed.y) < innerHeight / 2);
              // A modal dialog open with the focus outside it: start in the dialog.
              const modal = openModal(), outside = modal && !(cur && modal.contains(cur));
              const start = nothing || away || outside;
              const to = start ? first(outside ? modal : null) : next(cur, d);
              // Nothing within a screen up or down: the page moves on a little, so it can show more.
              if (!to && !start && d[1] !== 0) window.scrollBy({ top: d[1] * Math.round(innerHeight / 3), behavior: quick ? 'instant' : 'smooth' });
              if (!to) return;
              if (card && !card.items.has(to)) closeCard(false);
              // A carousel tile not wholly in the carousel's box (hidden, or cut by its edge) is slid into sight.
              // Scrolling into view would scroll the carousel's clipping box itself, behind the carousel's back.
              const box = carousel(to), r = face(to).getBoundingClientRect(), q = box && box.getBoundingClientRect();
              const cut = box && (r.left < q.left - 1 || r.right > q.right + 1);
              const slide = cut ? Array.prototype.indexOf.call(box.swiper.slides, to.closest('.swiper-slide')) : -1;
              // A tile past the edge of a row with its own button on that side: the button slides the row.
              const rowButton = !box && !start && d[0] !== 0 ? pager(to, d[0]) : null;
              to.focus({ preventScroll: true });
              placed = { e: to, y: r.top + scrollY };
              // A smooth scroll lags behind quick presses and a held key, so those scroll at once.
              if (slide >= 0) box.swiper.slideTo(slide);
              else if (rowButton) rowButton.click();
              else face(to).scrollIntoView({ block: 'center', inline: 'nearest', behavior: quick ? 'instant' : 'smooth' });
            } catch (e) { console.warn('tv arrows', e); }
          }, true);
        })();
        """;

    /// <summary>
    /// The arrows' script. FocusAfterPlayer (a CSS selector, or null) names the page button that takes the focus once
    /// the player layer closes; it goes in as a JSON string, so no selector can end the script.
    /// </summary>
    public static string ArrowNav(string? focusAfterPlayer) =>
        ArrowNavScript.Replace("__AFTER_PLAYER__", focusAfterPlayer is null ? "null" : JsonSerializer.Serialize(focusAfterPlayer));

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
