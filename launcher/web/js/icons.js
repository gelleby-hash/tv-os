// Interface and weather icons as inline SVG strings.
(function () {
  'use strict';

  function ui(body) {
    return '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" ' +
      'stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">' + body + '</svg>';
  }

  var UI = {
    spark: ui('<path d="M12 2.5v19M2.5 12h19M5.3 5.3l13.4 13.4M18.7 5.3 5.3 18.7"/>'),
    apps: ui('<rect x="3.5" y="3.5" width="7" height="7" rx="2"/><rect x="13.5" y="3.5" width="7" height="7" rx="2"/>' +
      '<rect x="3.5" y="13.5" width="7" height="7" rx="2"/><rect x="13.5" y="13.5" width="7" height="7" rx="2"/>'),
    image: ui('<rect x="3" y="4" width="18" height="16" rx="2.5"/><circle cx="9" cy="9.5" r="1.8"/>' +
      '<path d="m21 15.5-5-5-9.5 9.5"/>'),
    volume: ui('<path d="M4 9.5h3.5L12 5.5v13l-4.5-4H4z"/><path d="M15.5 9.5a3.5 3.5 0 0 1 0 5"/>'),
    volumeBoost: ui('<path d="M4 9.5h3.5L12 5.5v13l-4.5-4H4z"/><path d="M15.5 9.5a3.5 3.5 0 0 1 0 5"/>' +
      '<path d="M18.2 7a7 7 0 0 1 0 10"/>'),
    desktop: ui('<rect x="3" y="4" width="18" height="12" rx="2"/><path d="M8 20h8M12 16v4"/>'),
    restart: ui('<path d="M4 12a8 8 0 1 0 2.6-5.9"/><path d="M4 3.5v5h5"/>'),
    power: ui('<path d="M12 3v8.5"/><path d="M7 6.3a7.6 7.6 0 1 0 10 0"/>'),
    check: ui('<path d="m5 12.5 4.5 4.5L19 7.5"/>'),
    drop: ui('<path d="M12 3.5s6 6.4 6 10.5a6 6 0 0 1-12 0c0-4.1 6-10.5 6-10.5z"/>'),
    sunrise: ui('<path d="M12 3v5M8.5 6.5 12 3l3.5 3.5M4 18h16M6.3 14.5a6 6 0 0 1 11.4 0M2 21h20"/>'),
    sunset: ui('<path d="M12 8V3M8.5 4.5 12 8l3.5-3.5M4 18h16M6.3 14.5a6 6 0 0 1 11.4 0M2 21h20"/>'),
    wind: ui('<path d="M3 8h11a3 3 0 1 0-3-3M3 12h16a3 3 0 1 1-3 3M3 16h7"/>'),
    uv: ui('<circle cx="12" cy="12" r="4"/><path d="M12 2v2M12 20v2M4.9 4.9l1.4 1.4M17.7 17.7l1.4 1.4M2 12h2M20 12h2M4.9 19.1l1.4-1.4M17.7 6.3l1.4-1.4"/>'),
    sun: ui('<path d="M3 18h18M7 18a5 5 0 0 1 10 0M12 7v3M5.6 10.6l1.6 1.6M18.4 10.6l-1.6 1.6"/>'),
    eye: ui('<path d="M2 12s3.6-7 10-7 10 7 10 7-3.6 7-10 7S2 12 2 12z"/><circle cx="12" cy="12" r="3"/>'),
    humid: ui('<path d="M12 3s6 6.5 6 11a6 6 0 0 1-12 0c0-4.5 6-11 6-11z"/><path d="M9.5 15.5l5-5M9.8 11h.01M14.2 15h.01"/>')
  };

  var CLOUD_PATH = 'M20 48h26a10 10 0 0 0 0-20 14 14 0 0 0-27-3A11 11 0 0 0 20 48z';
  var DAY = { cloudTop: '#FFFFFF', cloudBottom: '#C6CEDB', backTop: '#DCE2EC', backBottom: '#9AA4B5',
    stormTop: '#A9B2C2', stormBottom: '#5E6779' };
  var NIGHT = { cloudTop: '#8E96A4', cloudBottom: '#545C6B', backTop: '#7D8696', backBottom: '#434B5A',
    stormTop: '#7D8696', stormBottom: '#3A414F' };

  // Every gradient, clip and filter id on the page must be unique: many icons share one document.
  var uid = 0;

  function place(x, y, s) {
    return '<g transform="translate(' + x + ' ' + y + ') scale(' + s + ')">';
  }

  // A puffy cloud: soft shadow underneath, body shaded top to bottom, a highlight on the upper left.
  function cloud(top, bottom, x, y, s) {
    var u = ++uid;
    return '<defs>' +
      '<linearGradient id="cb' + u + '" x1="0" y1="20" x2="0" y2="48" gradientUnits="userSpaceOnUse">' +
        '<stop offset="0" stop-color="' + top + '"/><stop offset="1" stop-color="' + bottom + '"/></linearGradient>' +
      '<radialGradient id="ch' + u + '" cx="26" cy="27" r="14" gradientUnits="userSpaceOnUse">' +
        '<stop offset="0" stop-color="#FFFFFF" stop-opacity="0.55"/><stop offset="1" stop-color="#FFFFFF" stop-opacity="0"/></radialGradient>' +
      '<filter id="cs' + u + '" filterUnits="userSpaceOnUse" x="-20" y="-20" width="104" height="104">' +
        '<feGaussianBlur stdDeviation="2.2"/></filter></defs>' +
      place(x, y, s) +
      '<path d="' + CLOUD_PATH + '" transform="translate(0 3)" fill="#000000" opacity="0.35" filter="url(#cs' + u + ')"/>' +
      '<path d="' + CLOUD_PATH + '" fill="url(#cb' + u + ')"/>' +
      '<path d="' + CLOUD_PATH + '" fill="url(#ch' + u + ')"/></g>';
  }

  // A glowing sun orb: warm glow, body from pale yellow to orange, a small highlight.
  function sun(x, y, s) {
    var u = ++uid;
    return '<defs>' +
      '<radialGradient id="sb' + u + '" cx="26" cy="25" r="21" gradientUnits="userSpaceOnUse">' +
        '<stop offset="0" stop-color="#FFF4B8"/><stop offset="0.55" stop-color="#FFC93F"/><stop offset="1" stop-color="#FF9A1F"/></radialGradient>' +
      '<radialGradient id="sg' + u + '" cx="32" cy="32" r="30" gradientUnits="userSpaceOnUse">' +
        '<stop offset="0.35" stop-color="#FFB13B" stop-opacity="0.55"/><stop offset="1" stop-color="#FFB13B" stop-opacity="0"/></radialGradient></defs>' +
      place(x, y, s) +
      '<circle cx="32" cy="32" r="30" fill="url(#sg' + u + ')"/>' +
      '<circle cx="32" cy="32" r="18" fill="url(#sb' + u + ')"/>' +
      '<ellipse cx="25" cy="24" rx="6" ry="3.8" fill="#FFFFFF" opacity="0.45"/></g>';
  }

  // The moon drawn by its phase (0 new, 0.5 full): glow from the lit part only, dark disc with depth,
  // lit part shaded, rim light on the lit side. At a full moon the rim is even all round; at a new
  // moon there is no lit part and no glow, only the dark disc and a faint even rim.
  function moon(p, x, y, s) {
    var u = ++uid, cx = 32, cy = 32, r = 22, k = Math.cos(2 * Math.PI * p), rx = Math.abs(k) * r;
    var waxing = p < 0.5, lit = window.TVMoon.lit(p);
    var full = Math.abs(p - 0.5) < 0.03, fresh = lit < 0.02;
    var outer = waxing ? 1 : 0, inner = waxing ? (k > 0 ? 0 : 1) : (k > 0 ? 1 : 0);
    var d = 'M' + cx + ' ' + (cy - r) + 'A' + r + ' ' + r + ' 0 0 ' + outer + ' ' + cx + ' ' + (cy + r) +
      'A' + rx.toFixed(2) + ' ' + r + ' 0 0 ' + inner + ' ' + cx + ' ' + (cy - r) + 'z';
    var side = waxing ? 1 : -1;
    var defs = (fresh ? '' : '<filter id="mh' + u + '" filterUnits="userSpaceOnUse" x="0" y="0" width="64" height="64"><feGaussianBlur stdDeviation="3.4"/></filter>') +
      '<radialGradient id="md' + u + '" cx="27" cy="20" r="32" gradientUnits="userSpaceOnUse"><stop offset="0" stop-color="#30364A"/><stop offset="1" stop-color="#090B11"/></radialGradient>' +
      (fresh ? '' : '<radialGradient id="ml' + u + '" cx="38" cy="18" r="34" gradientUnits="userSpaceOnUse"><stop offset="0" stop-color="#F4F6FA"/><stop offset="1" stop-color="#C9CFDA"/></radialGradient>') +
      '<filter id="mg' + u + '" filterUnits="userSpaceOnUse" x="0" y="0" width="64" height="64"><feGaussianBlur stdDeviation="1.8"/></filter>';
    var rimWide, rimThin;
    if (full || fresh) {
      var o = fresh ? 0.2 : 0.95;
      rimWide = '<circle cx="' + cx + '" cy="' + cy + '" r="' + r + '" fill="none" stroke="#FFFFFF" stroke-opacity="' + o +
        '" stroke-width="3.5" filter="url(#mg' + u + ')"/>';
      rimThin = '<circle cx="' + cx + '" cy="' + cy + '" r="' + (r - 0.6) + '" fill="none" stroke="#FFFFFF" stroke-opacity="' + o +
        '" stroke-width="1.2"/>';
    } else {
      defs += '<linearGradient id="mr' + u + '" x1="' + (cx + side * r) + '" y1="0" x2="' + (cx + side * r * (0.6 - 2 * lit)).toFixed(2) +
        '" y2="0" gradientUnits="userSpaceOnUse"><stop offset="0" stop-color="#FFFFFF" stop-opacity="0.95"/>' +
        '<stop offset="0.55" stop-color="#FFFFFF" stop-opacity="0.12"/><stop offset="1" stop-color="#FFFFFF" stop-opacity="0"/></linearGradient>';
      rimWide = '<circle cx="' + cx + '" cy="' + cy + '" r="' + r + '" fill="none" stroke="url(#mr' + u +
        ')" stroke-width="3.5" filter="url(#mg' + u + ')"/>';
      rimThin = '<circle cx="' + cx + '" cy="' + cy + '" r="' + (r - 0.6) + '" fill="none" stroke="url(#mr' + u + ')" stroke-width="1.2"/>';
    }
    return '<defs>' + defs + '</defs>' + place(x, y, s) +
      (fresh ? '' : '<path d="' + d + '" fill="#C9CFDA" opacity="0.8" filter="url(#mh' + u + ')"/>') +
      '<circle cx="' + cx + '" cy="' + cy + '" r="' + r + '" fill="url(#md' + u + ')"/>' +
      (fresh ? '' : '<path d="' + d + '" fill="url(#ml' + u + ')"/>') +
      rimWide + rimThin + '</g>';
  }

  // Glassy raindrops, snowflakes, a lightning bolt and fog bars.
  function drops(small) {
    var u = ++uid, len = small ? 6 : 9;
    function drop(x, y) {
      return '<path d="M' + x + ' ' + y + 'c-3 ' + (len * 0.55) + ' -4 ' + (len * 0.8) + ' -4 ' + len + 'a4 4 0 0 0 8 0c0 ' +
        (-len * 0.2) + ' -1 ' + (-len * 0.45) + ' -4 ' + (-len) + 'z" fill="url(#rd' + u + ')"/>';
    }
    return '<defs><linearGradient id="rd' + u + '" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#BFE6FF"/>' +
      '<stop offset="1" stop-color="#2F8BFF"/></linearGradient></defs>' +
      (small ? drop(24, 50) + drop(36, 50) : drop(21, 49) + drop(32, 52) + drop(43, 49));
  }
  function flakes() {
    var u = ++uid, out = '<defs><filter id="sf' + u + '" filterUnits="userSpaceOnUse" x="0" y="0" width="64" height="64">' +
      '<feGaussianBlur stdDeviation="1.2"/></filter></defs>';
    [[22, 52], [32, 58], [42, 52]].forEach(function (p) {
      out += '<circle cx="' + p[0] + '" cy="' + p[1] + '" r="4" fill="#FFFFFF" opacity="0.6" filter="url(#sf' + u + ')"/>' +
        '<circle cx="' + p[0] + '" cy="' + p[1] + '" r="2.6" fill="#FFFFFF"/>';
    });
    return out;
  }
  function bolt() {
    var u = ++uid, d = 'M35 38 26 52h7l-3 10 11-15h-7l4-9z';
    return '<defs><linearGradient id="bt' + u + '" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#FFE966"/>' +
      '<stop offset="1" stop-color="#FF8A1F"/></linearGradient>' +
      '<filter id="bg' + u + '" filterUnits="userSpaceOnUse" x="0" y="0" width="64" height="64"><feGaussianBlur stdDeviation="2"/></filter></defs>' +
      '<path d="' + d + '" fill="#FFB13B" opacity="0.7" filter="url(#bg' + u + ')"/>' +
      '<path d="' + d + '" fill="url(#bt' + u + ')"/>';
  }
  function fogBars(c) {
    return '<g stroke-linecap="round" stroke-width="4">' +
      '<line x1="12" y1="48" x2="52" y2="48" stroke="' + c.cloudTop + '" opacity="0.85"/>' +
      '<line x1="18" y1="56" x2="46" y2="56" stroke="' + c.cloudBottom + '" opacity="0.85"/></g>';
  }

  // date (default now) sets the moon's phase on night icons.
  function weather(kind, isDay, date) {
    var c = isDay ? DAY : NIGHT, body;
    var phase = isDay ? 0 : window.TVMoon.phase(date || new Date());
    function light(x, y, s) { return isDay ? sun(x, y, s) : moon(phase, x, y, s); }
    switch (kind) {
      case 'clear': body = light(0, 0, 1); break;
      case 'partly': body = light(14, -8, 0.72) + cloud(c.cloudTop, c.cloudBottom, -2, 6, 1); break;
      case 'cloudy': body = cloud(c.backTop, c.backBottom, 12, -6, 0.78) + cloud(c.cloudTop, c.cloudBottom, -2, 6, 1); break;
      case 'fog': body = cloud(c.backTop, c.backBottom, 0, -10, 1) + fogBars(c); break;
      case 'drizzle': body = cloud(c.cloudTop, c.cloudBottom, 0, -10, 1) + drops(true); break;
      case 'rain': body = cloud(c.backTop, c.backBottom, 0, -10, 1) + drops(false); break;
      case 'snow': body = cloud(c.cloudTop, c.cloudBottom, 0, -10, 1) + flakes(); break;
      case 'storm': body = cloud(c.stormTop, c.stormBottom, 0, -10, 1) + bolt(); break;
      default: body = cloud(c.cloudTop, c.cloudBottom, 0, -10, 1);
    }
    return '<svg viewBox="0 0 64 64" aria-hidden="true" style="overflow:visible">' + body + '</svg>';
  }

  window.TVIcons = { ui: UI, weather: weather };
})();
