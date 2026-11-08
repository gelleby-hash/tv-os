(function () {
  'use strict';

  var CFG = window.TV_HOME_CONFIG, I = window.TVIcons, W = window.TVWeather;
  var H = window.TVHost;
  var $ = function (id) { return document.getElementById(id); };

  var tr = window.TVI18n.text;
  // There is no settings screen: the host's config is the only source. The page is English only, whatever the host's Locale.
  var settings = {};
  Object.keys(CFG.defaults).forEach(function (k) { settings[k] = CFG.defaults[k]; });
  settings.language = 'en-GB';
  // The weather follows the host's last `location` message (null until the first). weatherKey names the coordinates the weather
  // is for ('' when there is no place), so an answer for a place the viewer has left is ignored.
  var place = null, weatherKey = '';
  var weather = null;
  var weatherError = null, lastAttempt = 0;
  var allApps = [], shownApps = [];
  var ROW = window.TVRow;
  // The row picked up in the Apps panel to be moved (its app id), or null.
  var carryingId = null;
  var precip = window.TVSky.Precip($('precip'), $('flash'));
  var scope = $('home'), focused = null, homeFocus = null, rowOffset = 0, skyKey = '', pendingPower = null;
  var boostApps = window.TVApps.boostApps(), boostOn, boostKnown = false;
  // The tiles wait for Windows HDR on the screen this page is on (the host's requireHdr). Without matchMedia there is nothing to wait for.
  var hdrQuery = window.matchMedia ? window.matchMedia('(dynamic-range: high)') : null;
  function hdrOn() { return !!(hdrQuery && hdrQuery.matches); }
  function waitingForHdr() { return !!CFG.requireHdr && !!hdrQuery && !hdrOn(); }

  // ---------- formatting ----------
  function pad(n) { return (n < 10 ? '0' : '') + n; }
  function fmtTime(d) {
    if (settings.clock === '24') return pad(d.getHours()) + ':' + pad(d.getMinutes());
    var h = d.getHours() % 12 || 12;
    return h + ':' + pad(d.getMinutes()) + ' ' + (d.getHours() < 12 ? 'AM' : 'PM');
  }
  function fmtHour(d) {
    if (settings.clock === '24') return pad(d.getHours()) + ':00';
    var h = d.getHours() % 12 || 12;
    return h + (d.getHours() < 12 ? ' AM' : ' PM');
  }
  function temp(c) { return Math.round(settings.unit === 'F' ? c * 9 / 5 + 32 : c) + '°'; }
  function wind(k) { return settings.unit === 'F' ? Math.round(k * 0.621) + ' mph' : Math.round(k) + ' km/h'; }
  function sameDay(a, b) { return a.getFullYear() === b.getFullYear() && a.getMonth() === b.getMonth() && a.getDate() === b.getDate(); }
  var dateFmt = new Intl.DateTimeFormat(settings.language, { weekday: 'long', day: 'numeric', month: 'long' });
  var dayFmt = new Intl.DateTimeFormat(settings.language, { weekday: 'short' });
  function esc(s) { return String(s).replace(/[&<>"]/g, function (c) { return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]; }); }

  // ---------- time of day ----------
  function today(now) { return weather && weather.daily.filter(function (d) { return sameDay(d.date, now); })[0]; }
  function sunTimes(now) {
    var t = today(now);
    if (t) return { rise: t.sunrise, set: t.sunset };
    var r = new Date(now), s = new Date(now);
    r.setHours(7, 30, 0, 0); s.setHours(19, 0, 0, 0);
    return { rise: r, set: s };
  }
  function phase(now) {
    if (settings.scenery !== 'auto') return settings.scenery;
    var st = sunTimes(now), t = now.getTime(), m = 40 * 60e3;
    if (Math.abs(t - st.rise) < m) return 'dawn';
    if (Math.abs(t - st.set) < m) return 'dusk';
    return t > st.rise && t < st.set ? 'day' : 'night';
  }
  function period(h) { return h < 5 ? 'night' : h < 12 ? 'morning' : h < 18 ? 'afternoon' : h < 22 ? 'evening' : 'night'; }

  var MOOD = {
    clear: ['Clear skies and sunshine today.', 'Clear skies tonight. The evening is yours.'],
    partly: ['A mix of sun and cloud today.', 'A few clouds drifting by. Settle in.'],
    cloudy: ['Grey skies. A good day for a series.', 'Cloudy and calm. Settle in.'],
    fog: ['Foggy out there. Stay cosy.', 'Fog rolling in tonight. Stay cosy.'],
    drizzle: ['A little drizzle outside.', 'Light drizzle tonight. Settle in.'],
    rain: ['Rain today. Perfect for staying in.', 'Rain outside. The evening is yours.'],
    snow: ['Snow outside. Stay warm.', 'Snow falling tonight. Stay warm.'],
    storm: ['Thunderstorms around. Stay in and stay dry.', 'Storms tonight. Stay in and stay dry.']
  };
  // No afternoon line (owner, 2026-11-02): the afternoon without weather has no sentence.
  var FALLBACK = { morning: 'A fresh start. Take it easy.',
    evening: 'Settle in. The evening is yours.', night: 'Late one? Keep it cosy.' };

  // ---------- rendering ----------
  function renderClock(now) {
    $('clock').textContent = fmtTime(now);
    $('date').textContent = dateFmt.format(now);
    var p = period(now.getHours());
    $('greet').textContent = tr('Good ' + p + '.');

    var sub = FALLBACK[p] ? tr(FALLBACK[p]) : '';
    if (weather) {
      var d = W.describe(weather.current.code), dayish = p === 'morning' || p === 'afternoon';
      sub = tr(MOOD[d.kind][dayish ? 0 : 1]);
    }
    $('sub').textContent = sub;

    var st = sunTimes(now), line = '';
    if (weather) {
      if (now < st.rise) line = I.ui.sunrise + tr('Sunrise at {time}', { time: fmtTime(st.rise) });
      else if (now < st.set) line = I.ui.sunset + tr('Sunset at {time}', { time: fmtTime(st.set) });
      else {
        var tm = weather.daily[1];
        line = tm ? I.ui.sunrise + tr('Sunrise tomorrow at {time}', { time: fmtTime(tm.sunrise) }) : '';
      }
    }
    $('sunline').innerHTML = line;

    if (!$('ambient').hidden) renderAmbient(now);
  }

  // The card and the forecast panel take one tint from the current weather (look-<id> classes), with stars on a clear night.
  function applyLook(kind, c) {
    var id = W.look(kind, c.isDay, c.wind);
    [$('weather'), document.querySelector('.forecast-panel')].forEach(function (el) {
      W.looks.forEach(function (l) { el.classList.remove('look-' + l); });
      el.classList.add('look-' + id);
      // The attribute itself: on an SVG element the hidden property is not reflected, so setting it would leave [hidden] in place.
      var stars = el.querySelector('.stars');
      if (id === 'night-clear') stars.removeAttribute('hidden'); else stars.setAttribute('hidden', '');
    });
  }

  // The weather is switched off, or the host has no place to show it for: the card gives way to the Weather off tile.
  function weatherIsOff() { return !!place && (place.source === 'off' || place.source === 'none'); }
  // Where the weather is for: the host's coordinates to 2 decimals, or '' when the message has none.
  function keyOf(msg) {
    if (!msg || typeof msg.latitude !== 'number' || typeof msg.longitude !== 'number') return '';
    var r = W.rounded(msg);
    return r.latitude + ',' + r.longitude;
  }

  function renderWeather() {
    // With the forecast failing and nothing saved for this place the card goes until an answer comes (owner, 2026-11-02:
    // Open-Meteo answered 503 for every place); the Weather off tile is only for weather switched off.
    var now = new Date(), off = weatherIsOff(), failing = !off && !weather && !!weatherError;
    $('weather').hidden = off || failing;
    $('weatherOff').hidden = !off;
    if (scope === $('home') && focused === $('weather') && failing) focusEl(firstTile());
    if (off || failing) return;
    if (!weather) {
      // Nothing of an earlier place stays on the card.
      $('wxCond').textContent = tr('Loading weather…');
      $('wxDetails').textContent = '';
      $('wxTemp').textContent = '--°';
      $('wxIcon').innerHTML = '';
      $('wxHilo').textContent = '';
      $('wxHours').innerHTML = '';
      $('wxStars').setAttribute('hidden', '');
      $('fcStars').setAttribute('hidden', '');
      return;
    }
    var c = weather.current, d = W.describe(c.code), t = today(now) || weather.daily[0];
    applyLook(d.kind, c);
    $('wxIcon').innerHTML = I.weather(d.kind, c.isDay);
    $('wxTemp').textContent = temp(c.temp);
    $('wxCond').textContent = d.label;
    $('wxHilo').textContent = t ? tr('H {high}   L {low}', { high: temp(t.max), low: temp(t.min) }) : '';
    var stale = Date.now() - weather.fetchedAt > 60 * 60e3;
    $('wxDetails').innerHTML = tr('Feels') + ' ' + temp(c.feels) + '<i></i>' + tr('Wind') + ' ' + wind(c.wind) + '<i></i>' + tr('Humidity') + ' ' +
      Math.round(c.humidity) + '%' + (stale ? '<i></i>' + tr('Updated') + ' ' + fmtTime(new Date(weather.fetchedAt)) : '');
    var hours = weather.hourly.filter(function (h) { return h.time > now; }).slice(0, 6);
    $('wxHours').innerHTML = hours.map(function (h) {
      var hd = W.describe(h.code);
      return '<div class="hr"><span>' + fmtHour(h.time) + '</span>' + I.weather(hd.kind, h.isDay, h.time) +
        '<b>' + temp(h.temp) + '</b></div>';
    }).join('');
  }

  function renderSky(now) {
    var ph = phase(now), cur = weather ? weather.current : null;
    var d = cur ? W.describe(cur.code) : { kind: 'clear', intensity: 0 };
    var cloud = cur ? cur.cloud : 0.1;
    var key = [ph, Math.round(cloud * 10), d.kind, now.getDate()].join('|');
    if (key !== skyKey) {
      skyKey = key;
      window.TVSky.render($('sky'), { phase: ph, cloud: cloud, kind: d.kind, date: now });
      window.TVBackground.update($('landscape'), { phase: ph, cloud: cloud, kind: d.kind });
      document.body.dataset.phase = ph;
    }
    precip.set(settings.effects ? d.kind : null, d.intensity);
  }

  function renderAll() {
    var now = new Date();
    renderClock(now);
    renderWeather();
    renderSky(now);
  }

  // ---------- apps ----------
  // If an icon is a full opaque tile, extend its edge colour across the tile.
  function tintFromIcon(img, tile) {
    try {
      var c = document.createElement('canvas'); c.width = c.height = 12;
      var x = c.getContext('2d'); x.drawImage(img, 0, 0, 12, 12);
      var px = x.getImageData(0, 0, 12, 12).data, corners = [0, 11, 132, 143], r = 0, g = 0, b = 0;
      for (var i = 0; i < corners.length; i++) {
        var o = corners[i] * 4;
        if (px[o + 3] < 200) return;
        r += px[o]; g += px[o + 1]; b += px[o + 2];
      }
      tile.style.background = 'rgb(' + Math.round(r / 4) + ',' + Math.round(g / 4) + ',' + Math.round(b / 4) + ')';
      tile.classList.add('opaque');
    } catch (e) { /* cross-origin icon: keep brand colour */ }
  }

  function renderGrid() {
    shownApps = allApps;
    var grid = $('grid');
    grid.innerHTML = '';
    if (waitingForHdr()) {
      grid.innerHTML = '<div class="empty waiting">' + tr('Waiting for HDR...') + '</div>';
      scrollGrid(null);
      return;
    }
    shownApps.forEach(function (a, i) {
      var tile = document.createElement('button');
      tile.className = 'tile focusable';
      tile.dataset.app = a.id;
      tile.dataset.idx = i;
      tile.innerHTML = '<span class="tile-name">' + esc(a.name) + '</span>';
      if (a.icon) {
        var img = new Image();
        img.alt = '';
        img.onload = function () { tintFromIcon(img, tile); };
        img.onerror = function () { img.remove(); tile.classList.add('no-icon'); };
        img.src = a.icon;
        tile.insertBefore(img, tile.firstChild);
      } else tile.classList.add('no-icon');
      grid.appendChild(tile);
    });
    if (!shownApps.length) grid.innerHTML = '<div class="empty">' + tr('No apps to show.') + '</div>';
    scrollGrid(null);
  }

  function scrollGrid(idx) {
    var grid = $('grid'), tiles = grid.querySelectorAll('.tile');
    var width = grid.clientWidth, last = tiles[tiles.length - 1];
    var total = last ? last.offsetLeft + last.offsetWidth : 0;
    var tile = idx !== null && tiles[idx];
    // With no tile the row only keeps its place, pulled back if the row got shorter.
    rowOffset = tile ? ROW.keepInView(tile.offsetLeft, tile.offsetLeft + tile.offsetWidth, width, rowOffset, total)
      : ROW.keepInView(rowOffset, rowOffset, width, rowOffset, total);
    grid.style.transform = 'translateX(' + (-rowOffset) + 'px)';
    var hidden = ROW.edges(rowOffset, width, total), apps = grid.parentNode.parentNode;
    apps.classList.toggle('more-before', hidden.before);
    apps.classList.toggle('more-after', hidden.after);
  }

  function loadApps() {
    return window.TVApps.list().then(function (list) {
      var prevId = focused && focused.dataset.app;
      allApps = list;
      renderGrid();
      if (scope === $('home')) {
        var again = prevId && document.querySelector('.tile[data-app="' + CSS.escape(prevId) + '"]');
        if (again || !focused || !document.contains(focused)) focusEl(again || firstTile() || document.querySelector('#home .focusable'));
      }
    }).catch(function () { toast(tr('Could not read installed apps')); });
  }
  // While the tiles wait for HDR the first thing to reach is the icon row.
  function firstTile() { return document.querySelector('#grid .tile') || document.querySelector('#home .focusable'); }

  // Windows HDR came on or went off: show or hide the tiles, and keep focus on something that is still there.
  function onHdrChange() {
    H.post({ type: 'hdr', on: hdrOn() });
    renderGrid();
    if (scope === $('home') && (!focused || !document.contains(focused))) focusEl(firstTile());
  }

  // ---------- weather loading ----------
  function refreshWeather(force) {
    if (!weatherKey) return;
    var age = weather ? Date.now() - weather.fetchedAt : Infinity;
    var due = force || age > CFG.weatherRefreshMinutes * 60e3;
    var backoff = weatherError ? 2 * 60e3 : 0;
    if (!due || Date.now() - lastAttempt < backoff) return;
    lastAttempt = Date.now();
    var asked = weatherKey;
    W.fetch(place).then(function (w) {
      if (asked !== weatherKey) return;
      weather = w; weatherError = null; renderAll();
    }).catch(function (e) {
      if (asked !== weatherKey) return;
      weatherError = e; renderWeather();
    });
  }

  // ---------- forecast detail tiles ----------
  // A value the weather service did not give shows a hyphen, never a number made up for it.
  var DASH = '-';
  function percent(v, max) { return Math.max(0, Math.min(100, v / max * 100)); }
  function pos(v) { return +v.toFixed(1); }
  function speed(v) { return v == null ? DASH : wind(v); }
  // The unit is set smaller, so a big number and its unit still fit a narrow tile.
  function withUnit(n, unit) { return n + '<small> ' + unit + '</small>'; }
  function millimetres(v) { return v == null ? DASH : withUnit(pos(v), 'mm'); }
  // One decimal under 10 km, whole kilometres above.
  function kilometres(m) {
    if (m == null) return DASH;
    var k = m / 1000, s = k < 10 ? k.toFixed(1) : String(Math.round(k));
    return withUnit(s === '10.0' ? '10' : s, 'km');
  }
  function dayFraction(d) { return (d.getHours() * 60 + d.getMinutes()) / 1440; }

  // The compass: ticks, N E S W, and an arrow that points where the wind blows to (the bearing it comes from, turned round).
  function compass(dir) {
    var ticks = '';
    for (var a = 0; a < 360; a += 10) {
      var r1 = a % 90 === 0 ? 50 : 54, rad = (a - 90) * Math.PI / 180;
      ticks += '<line x1="' + (60 + Math.cos(rad) * r1).toFixed(1) + '" y1="' + (60 + Math.sin(rad) * r1).toFixed(1) +
        '" x2="' + (60 + Math.cos(rad) * 58).toFixed(1) + '" y2="' + (60 + Math.sin(rad) * 58).toFixed(1) + '"/>';
    }
    var arrow = dir == null ? '' : '<g transform="rotate(' + (Math.round(dir + 180) % 360) + ' 60 60)">' +
      '<line x1="60" y1="84" x2="60" y2="40" stroke="#FFFFFF" stroke-width="3" stroke-linecap="round"/>' +
      '<path d="M60 30l-7 12h14z" fill="#FFFFFF"/><circle cx="60" cy="86" r="4.5" fill="#FFFFFF"/></g>';
    return '<svg viewBox="0 0 120 120" class="compass" aria-hidden="true"><g stroke="rgba(255,255,255,0.35)" stroke-width="1.5">' + ticks + '</g>' +
      '<g fill="rgba(255,255,255,0.75)" font-size="13" font-weight="600" text-anchor="middle"><text x="60" y="30">' + tr('N') + '</text>' +
      '<text x="92" y="64">' + tr('E') + '</text><text x="60" y="97">' + tr('S') + '</text><text x="28" y="64">' + tr('W') + '</text></g>' + arrow + '</svg>';
  }

  // One day as a wave across 24 hours, level with the horizon at sunrise and at sunset, so the gold part is exactly the daylight.
  // The dot is where the sun is now (below the horizon at night).
  function sunCurve(rise, set, now) {
    var w = 240, h = 110, horizon = 64, k = 28, mid = (dayFraction(rise) + dayFraction(set)) / 2;
    var floor = Math.cos(2 * Math.PI * (dayFraction(set) - dayFraction(rise)) / 2);
    function y(f) { return Math.min(h - 4, horizon - (Math.cos(2 * Math.PI * (f - mid)) - floor) * k); }
    var d = '';
    for (var i = 0; i <= 48; i++) d += (i ? 'L' : 'M') + (i / 48 * w).toFixed(1) + ' ' + y(i / 48).toFixed(1);
    var nowF = dayFraction(now);
    return '<svg viewBox="0 0 ' + w + ' ' + h + '" class="suncurve" aria-hidden="true">' +
      '<defs><clipPath id="fcSunAbove"><rect x="0" y="0" width="' + w + '" height="' + horizon + '"/></clipPath></defs>' +
      '<path d="' + d + '" fill="none" stroke="rgba(255,255,255,0.28)" stroke-width="3"/>' +
      '<path d="' + d + '" fill="none" stroke="#FFC93F" stroke-width="3" clip-path="url(#fcSunAbove)"/>' +
      '<line x1="0" y1="' + horizon + '" x2="' + w + '" y2="' + horizon + '" stroke="rgba(255,255,255,0.35)" stroke-width="1.5"/>' +
      '<circle cx="' + (nowF * w).toFixed(1) + '" cy="' + y(nowF).toFixed(1) + '" r="7" fill="#14171E" stroke="#FFFFFF" stroke-width="2.5"/></svg>';
  }

  function tileHead(icon, title) { return '<div class="t-head">' + icon + '<span>' + tr(title) + '</span></div>'; }
  function row(label, value) { return '<div><span>' + tr(label) + '</span><b>' + value + '</b></div>'; }

  function forecastTiles(now) {
    var days = weather.daily, c = weather.current, t = today(now) || days[0], next = days[days.indexOf(t) + 1];
    var uv = c.uv == null ? DASH : Math.round(c.uv);
    var peak = t.uvMax == null ? DASH : tr('Peak today {n}, {category}', { n: Math.round(t.uvMax), category: tr(W.uvCategory(t.uvMax)) });
    var mark = c.uv == null ? '' : '<i class="uvmark" style="left:' + pos(percent(c.uv, 11)) + '%"></i>';
    var from = c.windDir == null ? DASH : tr(W.compassName(c.windDir)) + ' ' + Math.round(c.windDir) + '°';
    var tomorrow = !next || next.rain == null ? DASH : tr('{n} mm expected tomorrow', { n: pos(next.rain) });
    var humidity = c.humidity == null ? DASH : Math.round(c.humidity) + '%';
    var fill = c.humidity == null ? 0 : Math.round(percent(c.humidity, 100));
    return [
      '<div class="fc-tile fc-uv">' + tileHead(I.ui.uv, 'UV index') + '<b class="t-big">' + uv + '</b><div class="t-sub">' + peak + '</div>' +
        '<div class="uvbar">' + mark + '</div></div>',
      '<div class="fc-tile fc-wind">' + tileHead(I.ui.wind, 'Wind') + '<div class="wind"><div class="wind-rows">' +
        row('Wind', speed(c.wind)) + row('Gusts', speed(c.gusts)) + row('From', from) + '</div>' + compass(c.windDir) + '</div></div>',
      '<div class="fc-tile fc-sun">' + tileHead(I.ui.sun, 'Sun') + sunCurve(t.sunrise, t.sunset, now) +
        '<div class="t-pair">' + row('Sunrise', fmtTime(t.sunrise)) + row('Sunset', fmtTime(t.sunset)) + '</div></div>',
      '<div class="fc-tile fc-rain">' + tileHead(I.ui.drop, 'Rainfall') + '<b class="t-big">' + millimetres(t.rain) + '</b>' +
        '<div class="t-sub">' + tr('today') + '</div><div class="t-sub2">' + tomorrow + '</div></div>',
      '<div class="fc-tile fc-vis">' + tileHead(I.ui.eye, 'Visibility') + '<b class="t-big">' + kilometres(c.visibility) + '</b></div>',
      '<div class="fc-tile fc-humid">' + tileHead(I.ui.humid, 'Humidity') + '<b class="t-big">' + humidity + '</b>' +
        '<div class="t-sub">' + tr('Dew point {n}', { n: c.dew == null ? DASH : temp(c.dew) }) + '</div>' +
        '<div class="hbar"><i style="width:' + fill + '%"></i></div></div>'
    ].join('');
  }

  // ---------- overlays ----------
  function renderForecast() {
    var now = new Date();
    $('fcPlace').textContent = weather ? tr('Updated') + ' ' + fmtTime(new Date(weather.fetchedAt)) : '';
    if (!weather) { $('fcDays').innerHTML = '<div class="empty">' + tr('Weather is not available yet.') + '</div>'; $('fcTiles').innerHTML = ''; return; }
    var days = weather.daily, lo = Infinity, hi = -Infinity;
    days.forEach(function (d) { lo = Math.min(lo, d.min); hi = Math.max(hi, d.max); });
    var span = Math.max(1, hi - lo);
    $('fcDays').innerHTML = days.map(function (d, i) {
      var wd = W.describe(d.code);
      var left = (d.min - lo) / span * 100, width = Math.max(6, (d.max - d.min) / span * 100);
      return '<div class="fc-day' + (sameDay(d.date, now) ? ' today' : '') + '">' +
        '<div class="fc-name">' + (i === 0 && sameDay(d.date, now) ? tr('Today') : dayFmt.format(d.date)) + '</div>' +
        I.weather(wd.kind, true) + '<div class="fc-label">' + wd.label + '</div>' +
        '<div class="fc-temps"><b>' + temp(d.max) + '</b><span>' + temp(d.min) + '</span></div>' +
        '<div class="fc-bar"><i style="left:' + left + '%;width:' + width + '%"></i></div>' +
        '<div class="fc-pop">' + I.ui.drop + (d.pop == null ? DASH : d.pop + '%') + '</div></div>';
    }).join('');
    $('fcTiles').innerHTML = forecastTiles(now);
  }

  function renderAmbient(now) {
    $('ambTime').textContent = fmtTime(now);
    $('ambDate').textContent = dateFmt.format(now);
    if (weather && !weatherIsOff()) {
      var c = weather.current, d = W.describe(c.code);
      $('ambWx').innerHTML = I.weather(d.kind, c.isDay) + '<span>' + temp(c.temp) + '</span><em>' + esc(d.label) + '</em>';
    } else $('ambWx').innerHTML = '';
  }

  function openOverlay(id) {
    var el = $(id);
    if (!el) return;   // an overlay this page does not have (yet): nothing to open
    homeFocus = focused;
    if (id === 'forecast') renderForecast();
    if (id === 'apps') { carryingId = null; drawApps(); }
    if (id === 'ambient') { renderAmbient(new Date()); document.body.classList.add('ambient-on'); }
    else document.body.classList.add('overlay-on');
    if (id === 'locAsk') $('locNote').hidden = true;
    el.hidden = false;
    if (id === 'apps') fitApps();
    scope = el;
    focusEl(firstShown(el));
  }
  // The first focusable that is on screen: the pop-up's first button is hidden while Windows location cannot be turned on.
  function firstShown(el) {
    return Array.prototype.filter.call(el.querySelectorAll('.focusable'), function (b) { return b.offsetParent !== null; })[0] || null;
  }
  function askToConfirm(kind) {
    pendingPower = kind;
    $('confirmText').textContent = tr(kind === 'shutdown' ? 'Shut down the TV box?' : 'Restart the TV box?');
    openOverlay('confirm');
  }
  function closeOverlay() {
    if (scope === $('home')) return;
    pendingPower = null;
    carryingId = null;
    if (onPlaceScreen()) {
      stopSearch();
      if (askState === 'shown') askState = 'done';
    }
    scope.hidden = true;
    document.body.classList.remove('ambient-on');
    document.body.classList.remove('overlay-on');
    scope = $('home');
    focusEl(homeFocus && document.contains(homeFocus) && homeFocus.offsetParent !== null ? homeFocus : firstTile());
    maybeAsk();
  }

  // ---------- the Apps panel ----------
  // The host keeps the apps' order, which are shown and which version each plays; the panel only asks (appsMove, appsShow,
  // appsVersion) and redraws from the host's answer, which arrives as an apps message even when the host refuses.
  var AP = window.TVAppsPanel;
  var APP_CONTROLS = [['appRow', 'data-app-row'], ['appVersion', 'data-app-version'], ['appShow', 'data-app-show']];
  function appControl(box, attr, id) { return box.querySelector('[' + attr + '="' + CSS.escape(id) + '"]'); }
  function findApp(id) { return window.TVApps.all().filter(function (a) { return a.id === id; })[0]; }

  // The list clips (and makes room for a carried row inside the clip) only when its rows do not fit. Measured without the class, and
  // only while the panel is shown: a hidden panel measures 0 (openOverlay calls this again once it shows).
  function fitApps() {
    var box = $('appsList');
    box.classList.remove('scrolls');
    box.classList.toggle('scrolls', box.scrollHeight > box.clientHeight && box.clientHeight > 0);
  }

  // Draws the rows. When the panel is open the focus stays on the same control (a switch stays a switch) and a carried row keeps it.
  function drawApps() {
    var list = window.TVApps.all(), box = $('appsList'), open = scope === $('apps'), kept = null;
    if (carryingId && !findApp(carryingId)) carryingId = null;
    if (open && focused && box.contains(focused)) {
      APP_CONTROLS.forEach(function (c) { if (focused.dataset[c[0]]) kept = { attr: c[1], id: focused.dataset[c[0]] }; });
    }
    var scrolled = box.scrollTop;
    box.innerHTML = AP.rowsHtml(list, carryingId);
    fitApps();
    box.scrollTop = scrolled;   // a redraw keeps the list where it was; the focus move below only scrolls as far as needed
    if (!open) return;
    var target = carryingId ? appControl(box, 'data-app-row', carryingId) :
      kept && (appControl(box, kept.attr, kept.id) || appControl(box, 'data-app-row', kept.id));
    focusEl(target || box.querySelector('.focusable'));
  }

  function dropRow() {
    carryingId = null;
    drawApps();
  }

  // Keys while a row is carried: Up and Down ask the host to move it one place, OK drops it, Left and Right do nothing; Back
  // (not handled here) drops it through back(). True when the key was used.
  function carryKey(k) {
    if (k === 38 || k === 40) {
      var to = AP.moveIndex(window.TVApps.all(), carryingId, k === 38 ? -1 : 1);
      if (to !== null) H.post({ type: 'appsMove', id: carryingId, to: to });
      return true;
    }
    if (k === 13) { dropRow(); return true; }
    return k === 37 || k === 39;
  }

  function activateApps(d) {
    if (carryingId && d.appRow !== carryingId) carryingId = null;   // a click elsewhere puts the row down
    var list = window.TVApps.all(), id = d.appRow || d.appVersion || d.appShow, target = findApp(id);
    if (!target) return;
    if (d.appRow) {
      carryingId = d.appRow === carryingId ? null : d.appRow;
      drawApps();
    } else if (d.appVersion) {
      H.post({ type: 'appsVersion', id: id, version: AP.nextVersion(target) });
    } else if (target.shown && !AP.canHide(list, id)) {
      toast(tr('At least one app stays on the home row'));
    } else {
      H.post({ type: 'appsShow', id: id, on: !target.shown });
    }
  }

  // ---------- choosing the weather's place ----------
  // Three screens in one overlay: the pop-up (#locAsk), the countries (#locCountry) and the letters (#locSearch). Moving between
  // them keeps homeFocus from when the first opened; Back (on screen or on the remote) steps back one screen, and on the pop-up
  // closes it. askState: 'idle'; 'waiting' (the host has no place and the pop-up waits for the home screen to be in front);
  // 'shown' (the launcher opened it by itself); 'done' (the viewer answered or closed it: not asked again this run).
  var P = window.TVPlaces;
  var PLACE_SCREENS = ['locAsk', 'locCountry', 'locSearch'];
  // The letter grid, 9 to a row: A to Z and the hyphen, then the apostrophe, a wide space and the delete key.
  var KEYS = 'ABCDEFGHIJKLMNOPQRSTUVWXYZ'.split('').concat(['-', "'", 'space', 'del']);
  var askState = 'idle', pickedCountry = null, typed = '', found = [], searchTimer = 0, searchSeq = 0;

  function onPlaceScreen() { return PLACE_SCREENS.indexOf(scope.id) >= 0; }

  // The pop-up opens only on the home screen with nothing over it, never over an app (the page is hidden then).
  function maybeAsk() {
    if (askState !== 'waiting' || scope !== $('home') || document.hidden || !place || place.source !== 'none') return;
    askState = 'shown';
    openOverlay('locAsk');
  }

  function showPlaceScreen(id, target) {
    scope.hidden = true;
    scope = $(id);
    scope.hidden = false;
    focusEl(target || scope.querySelector('.focusable'));
  }

  // The countries, Windows' region first and in focus.
  function openCountries() {
    var box = $('locCountries');
    box.innerHTML = P.countries(settings.language, place && place.region).map(function (c) {
      return '<button class="pill-btn loc-item focusable" data-country="' + esc(c.code) + '">' + esc(c.name) + '</button>';
    }).join('');
    box.scrollTop = 0;
    showPlaceScreen('locCountry', box.querySelector('.focusable'));
  }

  // The letters for one country, starting empty on A.
  function openSearch(code) {
    pickedCountry = code;
    typed = '';
    renderTyped();
    stopSearch();
    showResults([], '');
    showPlaceScreen('locSearch', $('locKeys').querySelector('.focusable'));
  }

  // Back from the letters or the countries: true when it stepped back a screen.
  function placeBack() {
    if (scope === $('locSearch')) {
      stopSearch();
      showPlaceScreen('locCountry', $('locCountries').querySelector('[data-country="' + pickedCountry + '"]'));
      return true;
    }
    if (scope === $('locCountry')) {
      showPlaceScreen('locAsk', $('locAsk').querySelector('[data-action="locChoose"]'));
      return true;
    }
    return false;
  }

  function buildKeys() {
    $('locKeys').innerHTML = KEYS.map(function (k) {
      var label = k === 'space' ? tr('Space') : k === 'del' ? tr('Delete') : k;
      return '<button class="loc-key focusable' + (k === 'space' || k === 'del' ? ' ' + k : '') + '" data-key="' + esc(k) + '">' + esc(label) + '</button>';
    }).join('');
  }

  // A key on a real keyboard: a letter, space, hyphen or apostrophe (null for any other key). The character comes from e.key when
  // that is one character (keyCode 222 is the apostrophe on a US layout but the # key on a UK one); a key without one, such as
  // a remote's, goes by its code.
  function keyboardKey(e) {
    var k = e.keyCode, c = e.key;
    if (e.ctrlKey || e.altKey || e.metaKey) return null;
    if (typeof c === 'string' && c.length === 1) {
      if (/[a-zA-Z]/.test(c)) return c.toUpperCase();
      if (c === ' ') return 'space';
      return c === '-' || c === "'" ? c : null;
    }
    if (k >= 65 && k <= 90) return String.fromCharCode(k);
    if (k === 32) return 'space';
    if (k === 189 || k === 109) return '-';
    if (k === 222) return "'";
    return null;
  }

  function typeKey(key) {
    if (key === 'del') typed = typed.slice(0, -1);
    else if (typed.length < 40) typed += key === 'space' ? ' ' : key;
    renderTyped();
    scheduleSearch();
  }
  function renderTyped() {
    $('locText').textContent = typed;
    $('locHint').hidden = !!typed;
  }

  // Any answer still on its way is ignored from now on.
  function stopSearch() {
    searchSeq++;
    clearTimeout(searchTimer);
  }
  // A search runs 400 ms after the last key, from the third letter; only the answer to the latest one is shown.
  function scheduleSearch() {
    stopSearch();
    var seq = searchSeq, text = typed, code = pickedCountry;
    if (text.trim().length < 3) { showResults([], ''); return; }
    searchTimer = setTimeout(function () {
      P.search(text, code).then(function (list) {
        if (seq === searchSeq) showResults(list, list.length ? '' : tr('No matching places.'));
      }, function () {
        if (seq === searchSeq) showResults([], tr('Search is not available right now.'));
      });
    }, 400);
  }
  // The towns, each with its county and region: the only place names this page ever shows.
  function showResults(list, message) {
    found = list;
    $('locResults').innerHTML = list.map(function (r, i) {
      return '<button class="loc-result focusable" data-result="' + i + '"><b>' + esc(r.name) + '</b><span>' + esc(r.area) + '</span></button>';
    }).join('');
    $('locStatus').textContent = message;
    if (scope === $('locSearch') && focused && !document.contains(focused)) {
      focusEl($('locResults').querySelector('.focusable') || $('locKeys').querySelector('.focusable'));
    }
  }

  function pickResult(i) {
    var r = found[i];
    if (!r) return;
    var c = W.rounded(r);
    H.post({ type: 'locationPick', latitude: c.latitude, longitude: c.longitude });
    closeOverlay();
  }

  // ---------- focus & navigation ----------
  function focusables() {
    return Array.prototype.filter.call(scope.querySelectorAll('.focusable'), function (el) { return el.offsetParent !== null || el === scope; });
  }
  function focusEl(el) {
    if (!el) return;
    if (focused) focused.classList.remove('focused');
    focused = el;
    el.classList.add('focused');
    if (el.classList.contains('tile')) scrollGrid(+el.dataset.idx);
    var appRow = el.closest('#appsList .app-row');
    if (appRow) {
      var rows = $('appsList'), room = getComputedStyle(rows);   // the list's padding is the room for a carried row's lift, ring and shadow
      rows.scrollTop = ROW.keepInView(appRow.offsetTop - parseFloat(room.paddingTop), appRow.offsetTop + appRow.offsetHeight + parseFloat(room.paddingBottom),
        rows.clientHeight, rows.scrollTop, rows.scrollHeight);
    }
    if (el.parentNode && (el.parentNode.id === 'appList' || el.parentNode.id === 'locCountries')) {
      var list = el.parentNode, top = el.offsetTop, inset = 8;
      if (top - inset < list.scrollTop) list.scrollTop = Math.max(0, top - inset);
      else if (top + el.offsetHeight + inset > list.scrollTop + list.clientHeight) list.scrollTop = top + el.offsetHeight + inset - list.clientHeight;
    }
  }
  function move(dir) {
    if (!focused || !scope.contains(focused)) { focusEl(focusables()[0]); return; }
    if (focused.classList.contains('tile') && (dir === 'left' || dir === 'right')) {
      var tiles = $('grid').querySelectorAll('.tile'), count = tiles.length;
      if (count) focusEl(tiles[(+focused.dataset.idx + (dir === 'right' ? 1 : -1) + count) % count]);
      return;
    }
    var a = focused.getBoundingClientRect(), ax = a.left + a.width / 2, ay = a.top + a.height / 2;
    var best = null, bestScore = Infinity;
    focusables().forEach(function (el) {
      if (el === focused) return;
      var b = el.getBoundingClientRect(), bx = b.left + b.width / 2, by = b.top + b.height / 2, primary, cross;
      if (el.classList.contains('tile')) {
        var viewport = $('grid').parentNode.getBoundingClientRect();
        if (b.left < viewport.left || b.right > viewport.right) return;
      }
      if (dir === 'left') { if (bx >= ax - 5) return; primary = Math.max(0, a.left - b.right); cross = Math.abs(by - ay) * 2; }
      if (dir === 'right') { if (bx <= ax + 5) return; primary = Math.max(0, b.left - a.right); cross = Math.abs(by - ay) * 2; }
      if (dir === 'up') { if (by >= ay - 5) return; primary = Math.max(0, a.top - b.bottom); cross = Math.abs(bx - ax); }
      if (dir === 'down') { if (by <= ay + 5) return; primary = Math.max(0, b.top - a.bottom); cross = Math.abs(bx - ax); }
      var score = primary + cross;
      if (score < bestScore) { bestScore = score; best = el; }
    });
    if (best) focusEl(best);
  }

  function activate(el) {
    if (!el) return;
    var d = el.dataset;
    if (d.app) {
      var app = allApps.filter(function (a) { return a.id === d.app; })[0];
      window.TVApps.launch(d.app).catch(function (e) { toast(tr('Could not open {app}.', { app: app ? app.name : tr('app') }) + ' ' + tr(e.message)); });
    } else if (d.appRow || d.appVersion || d.appShow) {
      activateApps(d);
    } else if (d.action === 'boost') {
      H.post({ type: 'toggleBoost' });
    } else if (d.action === 'desktop') {
      H.post({ type: 'desktop' });
    } else if (d.action === 'restart' || d.action === 'shutdown') {
      askToConfirm(d.action);
    } else if (d.action === 'confirm-yes') {
      var power = pendingPower;
      pendingPower = null;
      closeOverlay();
      if (power) H.post({ type: power });
    } else if (d.action === 'close' || d.action === 'confirm-no') {
      closeOverlay();
    } else if (d.action === 'locChoose') {
      openCountries();
    } else if (d.action === 'locOff') {
      H.post({ type: 'locationOff' });
      closeOverlay();
    } else if (d.action === 'locWindows') {
      H.post({ type: 'locationWindows' });
      closeOverlay();
    } else if (d.action === 'locTurnOn') {
      // The host opens Windows' settings and steps aside; the pop-up stays and Home (the host's home message) closes it.
      H.post({ type: 'locationTurnOn' });
    } else if (d.action === 'locBack') {
      back();
    } else if (d.country) {
      openSearch(d.country);
    } else if (d.key) {
      typeKey(d.key);
    } else if (d.result != null) {
      pickResult(+d.result);
    } else if (d.action === 'locAsk' && scope === $('forecast')) {
      // From the forecast: it closes first, so Back from the pop-up lands on the home screen as it does from the Weather off tile.
      closeOverlay();
      if (scope === $('home')) openOverlay('locAsk');
    } else if (d.action) {
      openOverlay(d.action);
    }
  }

  // Back closes an overlay, or steps back one of the place screens; on the home screen it does nothing (the family cannot
  // leave the launcher).
  function back() {
    if (scope === $('apps') && carryingId) { dropRow(); return; }
    if (placeBack()) return;
    if (scope !== $('home')) closeOverlay();
  }

  // The host brought the home screen to the front (Home key): start from a clean first tile. Home is not an answer to the
  // pop-up the launcher opened by itself, so that one comes back over the home screen.
  H.on('home', function () {
    if (askState === 'shown') askState = 'waiting';
    closeOverlay();
    rowOffset = 0;
    scrollGrid(null);
    if (scope === $('home')) focusEl(firstTile()); else homeFocus = firstTile();
    maybeAsk();
    requestAnimationFrame(function () { H.post({ type: 'homeShown', at: performance.now() }); });
  });

  // The host's boost state: on startup and after each press. The button shows it: its icon, a light while on, and its
  // label under it while in focus. After a press (not the starting state) the label springs in again (pop).
  function boostLabel(on) {
    return tr(on === undefined ? 'Boost {apps}' : on ? 'Boost {apps}: on' : 'Boost {apps}: off', { apps: boostApps.join(tr(' and ')) });
  }
  function drawBoost(btn, on, pop) {
    btn.innerHTML = I.ui[btn.dataset.icon] + '<span class="led"></span>' +
      '<span class="btn-tip' + (pop ? ' pop' : '') + '">' + esc(boostLabel(on)) + '</span>';
    btn.setAttribute('aria-label', boostLabel(on));
  }
  H.on('boost', function (msg) {
    var on = boostOn = !!msg.on, btn = document.querySelector('[data-action="boost"]');
    btn.classList.toggle('on', on);
    btn.dataset.icon = on ? 'volumeBoost' : 'volume';
    drawBoost(btn, on, boostKnown);
    boostKnown = true;
  });

  // The boost button follows the shown apps that have a boost: its label names them, and it is gone when there are none.
  function showBoostButton() {
    var btn = document.querySelector('[data-action="boost"]');
    btn.hidden = !boostApps.length;
    drawBoost(btn, boostOn, false);
    if (btn.hidden && focused === btn) focusEl(firstTile());
  }

  // The host's place for the weather: on startup and after every change. A different place drops the weather of the old one
  // at once and asks for the new; the same place again changes nothing but which card shows.
  H.on('location', function (msg) {
    var key = keyOf(msg);
    place = msg;
    if (key !== weatherKey) {
      weatherKey = key;
      weather = key ? W.cached(msg) : null;
      weatherError = null;
      lastAttempt = 0;
    }
    renderAll();
    if (!$('forecast').hidden) renderForecast();
    refreshWeather(false);
    // A place arriving answers the pop-up the launcher opened by itself, on whichever screen it is: it closes. One the viewer opened
    // (Change location, the Weather off tile) stays.
    if (msg.source !== 'none' && askState === 'shown' && onPlaceScreen()) closeOverlay();
    showWindowsChoice();
    showTurnOnButton();
    // The card that had the focus may have just gone: land on the first tile instead.
    if (scope === $('home') && focused && focused.offsetParent === null) focusEl(firstTile());
    // No place: ask once this run, when the home screen is in front. A place arriving first cancels the wait.
    if (msg.source === 'none') { if (askState === 'idle') askState = 'waiting'; }
    else if (askState === 'waiting') askState = 'idle';
    maybeAsk();
  });

  // "Use Windows location" is offered only when Windows location is on and the place is one the viewer or the config chose
  // (or the weather is off): never when Windows' own location is already in use or there is no place at all.
  function showWindowsChoice() {
    var btn = $('locAsk').querySelector('[data-action="locWindows"]');
    btn.hidden = !(place && place.windowsOn && (place.source === 'picked' || place.source === 'config' || place.source === 'off'));
    if (btn.hidden && focused === btn) focusEl(firstShown($('locAsk')));
  }

  // "Turn on Windows location" follows the latest location message: offered only while Windows location is off.
  function showTurnOnButton() {
    var btn = $('locAsk').querySelector('[data-action="locTurnOn"]');
    btn.hidden = !(place && place.canTurnOn);
    if (btn.hidden && focused === btn) focusEl(firstShown($('locAsk')));
  }

  // The host read Windows again after the viewer came back from its settings. Still off: the pop-up, with a note, on the home
  // screen with nothing over it (the one the launcher opened by itself just gets the note); on, or an app or another overlay in
  // front: nothing, and nothing waits.
  H.on('locationTurnOn', function (msg) {
    if (msg.on) return;
    if (scope === $('home') && !document.hidden) openOverlay('locAsk');
    else if (scope !== $('locAsk')) return;
    $('locNote').hidden = false;
  });

  // The first keys and the first pointer move go to the launcher's log. After a boot a viewer found the remote dead until
  // the mouse moved (owner, 2026-11-08), and the log could not say whether the page got the keys.
  var keysToReport = 10, pointerReported = false;
  function describeFocus(el) {
    if (!el) return 'nothing';
    if (el.classList.contains('tile')) return 'tile ' + el.dataset.idx;
    return el.dataset.action || el.id || el.className;
  }

  document.addEventListener('keydown', function (e) {
    var k = e.keyCode;
    if (keysToReport > 0) { keysToReport--; H.post({ type: 'key', code: k, id: scope.id + ':' + describeFocus(focused) }); }
    if (scope === $('ambient')) { e.preventDefault(); closeOverlay(); return; }
    if (scope === $('apps') && carryingId && carryKey(k)) { e.preventDefault(); return; }
    // On the letters a keyboard types, and Backspace deletes a letter instead of stepping back.
    var typedKey = scope === $('locSearch') && (k === 8 ? 'del' : keyboardKey(e));
    if (typedKey) { typeKey(typedKey); e.preventDefault(); return; }
    if (k === 37) move('left');
    else if (k === 38) move('up');
    else if (k === 39) move('right');
    else if (k === 40) move('down');
    else if (k === 13) activate(focused);
    else if (k === 10009 || k === 27 || k === 8 || k === 166) back();   // 166: the Rii remote's Browser Back
    else return;
    e.preventDefault();
  });
  // Samsung remote pointer and desktop mouse.
  document.addEventListener('mouseover', function (e) {
    if (!pointerReported) { pointerReported = true; H.post({ type: 'pointer' }); }
    var el = e.target.closest && e.target.closest('.focusable');
    if (carryingId) return;   // a carried row keeps the focus
    if (el && scope.contains(el) && el !== focused) focusEl(el);
  });
  document.addEventListener('click', function (e) {
    if (scope === $('ambient')) { closeOverlay(); return; }
    var el = e.target.closest && e.target.closest('.focusable');
    if (el && scope.contains(el)) { focusEl(el); activate(el); }
  });

  // ---------- toast ----------
  var toastTimer = 0;
  function toast(msg) {
    var t = $('toast');
    t.textContent = msg; t.classList.add('on');
    clearTimeout(toastTimer);
    toastTimer = setTimeout(function () { t.classList.remove('on'); }, 3500);
  }

  // ---------- layout & lifecycle ----------
  function fit() {
    var s = Math.min(window.innerWidth / 1920, window.innerHeight / 1080);
    var st = $('stage');
    st.style.transform = 'scale(' + s + ')';
    st.style.left = Math.max(0, (window.innerWidth - 1920 * s) / 2) + 'px';
    st.style.top = Math.max(0, (window.innerHeight - 1080 * s) / 2) + 'px';
  }

  function init() {
    scrollGrid(null);
    // A top-row button also carries its name, shown under it while in focus (boost draws its own in drawBoost).
    Array.prototype.forEach.call(document.querySelectorAll('[data-icon]'), function (el) {
      var name = el.classList.contains('icon-btn') && el.dataset.action !== 'boost' && el.getAttribute('aria-label');
      el.innerHTML = I.ui[el.dataset.icon] + (name ? '<span class="btn-tip">' + esc(name) + '</span>' : '');
    });
    $('wxStars').innerHTML = W.stars(90, 600, 260, 3);
    $('fcStars').innerHTML = W.stars(500, 1520, 900, 6);
    showBoostButton();
    window.TVApps.onChange(function () {
      boostApps = window.TVApps.boostApps();
      showBoostButton();
      loadApps();
      if (scope === $('apps')) drawApps();
    });
    buildKeys();
    // How long the motion takes (launcher.json's MotionTime); none at all when Windows' animation effects are off.
    var still = window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches;
    document.documentElement.style.setProperty('--motion', still ? 0 : (CFG.motionTime || 1));
    if (!window.TVApps.isHost) document.body.classList.add('preview');
    fit();
    window.addEventListener('resize', fit);
    var q = window.TVApps.isHost ? null : new URLSearchParams(location.search);
    if (q && q.get('scenery')) settings.scenery = q.get('scenery');
    renderAll();
    loadApps();
    refreshWeather(false);
    if (q && q.get('open')) setTimeout(function () { openOverlay(q.get('open')); }, 300);
    setInterval(function () { renderClock(new Date()); renderSky(new Date()); }, 5000);
    setInterval(function () { refreshWeather(false); renderWeather(); }, 60e3);
    document.addEventListener('visibilitychange', function () {
      precip.pause(document.hidden);
      if (!document.hidden) { renderAll(); refreshWeather(false); loadApps(); maybeAsk(); }
    });
    H.post({ type: 'ready' });
    H.post({ type: 'hdr', on: hdrOn() });
    if (hdrQuery) hdrQuery.addEventListener('change', onHdrChange);
  }

  init();
})();
