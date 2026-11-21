// Glass and shading that follow the brightness of the photo behind them.
// levelOf measures how bright the photo is under a region; valuesFor turns those levels into
// the numbers the page's style properties take.
(function () {
  'use strict';

  // The brightness the current look was tuned at: at this level every value is what it was before.
  var REF = 0.35;

  // One row per style property: which region's level it follows, how it is worked out, and its limits.
  // 'line' is base + slope * (L - REF); 'power' is base * (REF / max(L, 0.05)) ^ exp.
  // Tuning at the TV is a change to one row.
  var TABLE = [
    { name: '--s-greet',  region: 'greet', kind: 'line',  base: 0.55, slope: 0.9, min: 0.30, max: 0.80 },
    { name: '--s-wx',     region: 'sub',   kind: 'line',  base: 0.42, slope: 0.9, min: 0.20, max: 0.75 },
    { name: '--s-top',    region: 'top',   kind: 'line',  base: 0.50, slope: 0.8, min: 0.30, max: 0.75 },
    { name: '--s-bottom', region: 'tiles', kind: 'line',  base: 0.60, slope: 0.6, min: 0.45, max: 0.80 },
    { name: '--g-wx',     region: 'wx',    kind: 'power', base: 0.72, exp: 0.6,   min: 0.45, max: 1.0 },
    { name: '--g-tile',   region: 'tiles', kind: 'power', base: 0.72, exp: 0.6,   min: 0.45, max: 1.0 },
    { name: '--b-bright', region: 'top',   kind: 'power', base: 1.05, exp: 0.5,   min: 0.60, max: 1.15 }
  ];

  // Brightness 0 to 1 of the photo inside rect {l, t, r, b} (right and bottom excluded), from
  // pixels {data, width} (RGBA bytes, as ImageData). Every 4th pixel of each row is read. The mean
  // alone would hide a small bright patch that still washes out the text, so the 90th percentile counts too.
  // The percentile comes from a histogram of BINS steps (within half a step of the exact value) rather than a sort
  // of every sample, which held the page's thread for a noticeable time on each photo change.
  var BINS = 1024;
  function levelOf(pixels, rect) {
    var data = pixels.data, width = pixels.width;
    var counts = new Array(BINS).fill(0), n = 0, sum = 0;
    for (var y = rect.t; y < rect.b; y++) {
      for (var x = rect.l; x < rect.r; x += 4) {
        var i = (y * width + x) * 4;
        var l = (0.2126 * data[i] + 0.7152 * data[i + 1] + 0.0722 * data[i + 2]) / 255;
        counts[Math.min(BINS - 1, Math.floor(l * BINS))]++;
        n++;
        sum += l;
      }
    }
    if (!n) return 0;
    // The sample at sorted position floor(0.9 n), as the sort read it, lies in the first bin whose running count passes it.
    var target = Math.min(n - 1, Math.floor(0.9 * n)), seen = 0, bin = 0;
    while (seen + counts[bin] <= target) seen += counts[bin++];
    return 0.6 * (sum / n) + 0.4 * ((bin + 0.5) / BINS);
  }

  // levels: {greet, wx, sub, tiles, top}, each 0 to 1. Returns {propertyName: number}.
  function valuesFor(levels) {
    var out = {};
    TABLE.forEach(function (row) {
      var L = levels[row.region];
      var v = row.kind === 'line'
        ? row.base + row.slope * (L - REF)
        : row.base * Math.pow(REF / Math.max(L, 0.05), row.exp);
      out[row.name] = Math.min(row.max, Math.max(row.min, v));
    });
    return out;
  }

  // ---------- on the page ----------
  var W = 1920, H = 1080;
  var canvas = null, pixels = null, kept = '';
  var ticket = 0, loggedFor = {}, loggedWorks = false;

  function styleOf() { return document.documentElement.style; }
  function post(text) {
    if (window.TVHost && window.TVHost.post) window.TVHost.post({ type: 'log', text: text });
  }
  function nameOf(url) {
    var last = String(url).split('?')[0].split('/').pop();
    try { return decodeURIComponent(last); } catch (e) { return last; }
  }

  // Back to today's look: the page's own default values apply again.
  function clear() {
    ticket++;
    canvas = null; pixels = null; kept = '';
    var st = styleOf();
    TABLE.forEach(function (row) { st.removeProperty(row.name); });
  }

  // Gives up on this photo: today's look, and one line in the log per file name per run.
  function fail(url, e) {
    clear();
    var name = nameOf(url);
    if (loggedFor[name]) return;
    loggedFor[name] = true;
    post('glass: could not measure ' + name + ': ' + (e && e.name ? e.name : 'Error'));
  }

  function stageScale() {
    var stage = document.getElementById('stage');
    var m = stage && stage.style && /scale\(([\d.]+)\)/.exec(stage.style.transform || '');
    var s = m ? Number(m[1]) : 1;
    return s > 0 ? s : 1;
  }

  // The union of the live boxes of the elements picked by selectors, in the 1920 x 1080 the photo is drawn at,
  // grown by pad and cut to the picture. null when none of them has a size.
  function regionOf(selectors, pad, origin, scale) {
    var l = Infinity, t = Infinity, r = -Infinity, b = -Infinity;
    selectors.forEach(function (sel) {
      var el = document.querySelector(sel);
      var box = el && el.getBoundingClientRect && el.getBoundingClientRect();
      if (!box || !(box.width > 0) || !(box.height > 0)) return;
      l = Math.min(l, (box.left - origin.left) / scale);
      t = Math.min(t, (box.top - origin.top) / scale);
      r = Math.max(r, (box.right - origin.left) / scale);
      b = Math.max(b, (box.bottom - origin.top) / scale);
    });
    if (l === Infinity) return null;
    var rect = {
      l: Math.max(0, Math.floor(l - pad)), t: Math.max(0, Math.floor(t - pad)),
      r: Math.min(W, Math.ceil(r + pad)), b: Math.min(H, Math.ceil(b + pad))
    };
    return rect.r > rect.l && rect.b > rect.t && isFinite(rect.l + rect.t + rect.r + rect.b) ? rect : null;
  }

  function measure() {
    var stage = document.getElementById('stage');
    var origin = stage && stage.getBoundingClientRect ? stage.getBoundingClientRect() : { left: 0, top: 0 };
    var scale = stageScale();
    var wanted = {
      greet: [['#date', '#greet'], 40], wx: [['#weather'], 0], sub: [['#sub', '#sunline'], 20],
      tiles: [['.grid-viewport'], 0], top: [['.topbar'], 10]
    };
    var levels = {};
    Object.keys(wanted).forEach(function (key) {
      var rect = regionOf(wanted[key][0], wanted[key][1], origin, scale);
      levels[key] = rect ? levelOf(pixels, rect) : REF;
    });
    var values = valuesFor(levels);
    var st = styleOf();
    Object.keys(values).forEach(function (name) { st.setProperty(name, String(Math.round(values[name] * 1000) / 1000)); });
    return levels;
  }

  function apply(url) {
    var levels = measure();
    if (!loggedWorks) {
      loggedWorks = true;
      post('glass: measuring works (' + nameOf(url) + ', tiles ' + levels.tiles.toFixed(2) + ', top ' + levels.top.toFixed(2) + ')');
    }
  }

  // Loads the photo as a CORS image, draws it as background-size: cover on a 1920 x 1080 canvas, measures the regions
  // and sets the style properties. Any failure leaves today's look.
  function show(url) {
    var mine = ++ticket;
    var image = document.createElement('img');
    image.crossOrigin = 'anonymous';
    image.onload = function () {
      if (mine !== ticket) return;
      try {
        var c = document.createElement('canvas');
        c.width = W; c.height = H;
        var ctx = c.getContext('2d', { willReadFrequently: true });
        var iw = image.naturalWidth || W, ih = image.naturalHeight || H;
        var k = Math.max(W / iw, H / ih);
        ctx.drawImage(image, (W - iw * k) / 2, (H - ih * k) / 2, iw * k, ih * k);
        pixels = ctx.getImageData(0, 0, W, H);
        canvas = c; kept = url;
        apply(url);
      } catch (e) { fail(url, e); }
    };
    image.onerror = function () {
      if (mine !== ticket) return;
      fail(url, { name: 'LoadError' });
    };
    image.src = url;
  }

  // The layout changed (the number of tiles, say): measure the kept picture again.
  function remeasure() {
    if (!canvas || !pixels) return;
    try { apply(kept); } catch (e) { fail(kept, e); }
  }

  window.TVGlass = { REF: REF, levelOf: levelOf, valuesFor: valuesFor, show: show, clear: clear, remeasure: remeasure };
})();
