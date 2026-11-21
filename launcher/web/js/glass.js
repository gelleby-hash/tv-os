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
  function levelOf(pixels, rect) {
    var data = pixels.data, width = pixels.width;
    var lumas = [], sum = 0;
    for (var y = rect.t; y < rect.b; y++) {
      for (var x = rect.l; x < rect.r; x += 4) {
        var i = (y * width + x) * 4;
        var l = (0.2126 * data[i] + 0.7152 * data[i + 1] + 0.0722 * data[i + 2]) / 255;
        lumas.push(l);
        sum += l;
      }
    }
    if (!lumas.length) return 0;
    lumas.sort(function (a, b) { return a - b; });
    var p90 = lumas[Math.min(lumas.length - 1, Math.floor(0.9 * lumas.length))];
    return 0.6 * (sum / lumas.length) + 0.4 * p90;
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

  window.TVGlass = { REF: REF, levelOf: levelOf, valuesFor: valuesFor };
})();
