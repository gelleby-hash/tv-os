// Weather from Open-Meteo (no API key). Values are fetched in °C and km/h and converted for display.
(function () {
  'use strict';

  var CACHE_KEY = 'tvhome.weather.v2';

  // WMO weather interpretation codes.
  var CODES = {
    0: ['Clear', 'clear', 0], 1: ['Mainly clear', 'clear', 0], 2: ['Partly cloudy', 'partly', 0],
    3: ['Overcast', 'cloudy', 0], 45: ['Fog', 'fog', 0], 48: ['Freezing fog', 'fog', 0],
    51: ['Light drizzle', 'drizzle', 1], 53: ['Drizzle', 'drizzle', 1], 55: ['Heavy drizzle', 'drizzle', 2],
    56: ['Freezing drizzle', 'drizzle', 1], 57: ['Freezing drizzle', 'drizzle', 2],
    61: ['Light rain', 'rain', 1], 63: ['Rain', 'rain', 2], 65: ['Heavy rain', 'rain', 3],
    66: ['Freezing rain', 'rain', 1], 67: ['Freezing rain', 'rain', 3],
    71: ['Light snow', 'snow', 1], 73: ['Snow', 'snow', 2], 75: ['Heavy snow', 'snow', 3], 77: ['Snow grains', 'snow', 1],
    80: ['Showers', 'rain', 1], 81: ['Showers', 'rain', 2], 82: ['Heavy showers', 'rain', 3],
    85: ['Snow showers', 'snow', 1], 86: ['Snow showers', 'snow', 3],
    95: ['Thunderstorm', 'storm', 2], 96: ['Thunderstorm, hail', 'storm', 3], 99: ['Thunderstorm, hail', 'storm', 3]
  };

  function describe(code) {
    var c = CODES[code];
    var label = c ? c[0] : 'Unknown';
    return { label: window.TVI18n ? window.TVI18n.text(label) : label, kind: c ? c[1] : 'cloudy', intensity: c ? c[2] : 0 };
  }

  // The tint of the weather card and the forecast panel. At night only two are used (clear sky or not); by day a storm,
  // rain, snow or fog wins over a wind of 40 km/h or more, which wins over the sky's own look.
  var LOOKS = ['day-clear', 'day-partly', 'cloudy', 'fog', 'rain', 'snow', 'storm', 'windy', 'night-clear', 'night-cloudy'];
  function look(kind, isDay, windKmh) {
    if (!isDay) return kind === 'clear' || kind === 'partly' ? 'night-clear' : 'night-cloudy';
    if (kind === 'storm') return 'storm';
    if (kind === 'rain' || kind === 'drizzle') return 'rain';
    if (kind === 'snow' || kind === 'fog') return kind;
    if (windKmh >= 40) return 'windy';
    return kind === 'clear' ? 'day-clear' : kind === 'partly' ? 'day-partly' : 'cloudy';
  }

  // Stars for a clear night (owner, option B, 2026-10-30), the same every time, as SVG in a w by h box: count fine points
  // of varied size, brightness and tint, thinning towards the bottom, and `bright` stars with a soft glow and a fine
  // cross that twinkle out of step (.star-glint in the CSS). The old soft dots, stretched with the card, read as snow.
  var STAR_TINTS = ['#FFFFFF', '#FFFFFF', '#FFFFFF', '#DCE6FF', '#FFF1DC'];
  function stars(count, w, h, bright) {
    var seed = 11, out = '';
    function rnd() { seed = (seed * 16807) % 2147483647; return seed / 2147483647; }
    for (var i = 0; i < count; i++) {
      var cx = rnd() * w, cy = Math.pow(rnd(), 1.6) * h, big = rnd() < 0.15;
      var r = big ? 0.9 + rnd() * 0.35 : 0.35 + rnd() * 0.4, o = big ? 0.75 + rnd() * 0.2 : 0.3 + rnd() * 0.45;
      var fill = STAR_TINTS[Math.floor(rnd() * STAR_TINTS.length)];
      out += '<circle class="star" cx="' + cx.toFixed(1) + '" cy="' + cy.toFixed(1) + '" r="' + r.toFixed(2) + '" fill="' + fill + '" opacity="' + o.toFixed(2) + '"/>';
    }
    for (var j = 0; j < (bright || 0); j++) {
      var x = (0.1 + rnd() * 0.8) * w, y = (0.05 + rnd() * 0.5) * h, a = x.toFixed(1), b = y.toFixed(1);
      out += '<g class="star-glint" style="animation-delay: ' + (j * 1.7).toFixed(1) + 's">' +
        '<circle cx="' + a + '" cy="' + b + '" r="5" fill="#E8EEFF" opacity="0.08"/>' +
        '<circle cx="' + a + '" cy="' + b + '" r="3" fill="#E8EEFF" opacity="0.16"/>' +
        '<path d="M' + (x - 6).toFixed(1) + ' ' + b + 'H' + (x + 6).toFixed(1) + 'M' + a + ' ' + (y - 6).toFixed(1) + 'V' + (y + 6).toFixed(1) + '" stroke="#FFFFFF" stroke-width="0.5" stroke-linecap="round" opacity="0.7"/>' +
        '<circle cx="' + a + '" cy="' + b + '" r="1.2" fill="#FFFFFF"/></g>';
    }
    return out;
  }

  function url(loc) {
    return 'https://api.open-meteo.com/v1/forecast?latitude=' + loc.latitude + '&longitude=' + loc.longitude +
      '&current=temperature_2m,apparent_temperature,relative_humidity_2m,weather_code,is_day,wind_speed_10m,cloud_cover,wind_direction_10m,wind_gusts_10m,dew_point_2m,uv_index,visibility' +
      '&hourly=temperature_2m,weather_code,precipitation_probability,is_day' +
      '&daily=weather_code,temperature_2m_max,temperature_2m_min,sunrise,sunset,precipitation_probability_max,uv_index_max,precipitation_sum' +
      '&timezone=auto&forecast_days=7';
  }

  // Open-Meteo returns local times without an offset ("2026-10-19T07:51"); the TV runs in the same timezone.
  function localDate(s) {
    var p = s.split(/[-T:]/).map(Number);
    return new Date(p[0], p[1] - 1, p[2], p[3] || 0, p[4] || 0);
  }

  // A value Open-Meteo left out or sent as null stays null; the page shows a hyphen for it.
  function num(v) { return v == null ? null : v; }
  function at(list, i) { return list ? num(list[i]) : null; }

  var COMPASS = ['N', 'NNE', 'NE', 'ENE', 'E', 'ESE', 'SE', 'SSE', 'S', 'SSW', 'SW', 'WSW', 'W', 'WNW', 'NW', 'NNW'];
  // The compass point a bearing falls in: 16 points of 22.5 degrees, each centred on its own bearing.
  function compassName(deg) {
    return COMPASS[Math.floor((((deg % 360) + 360) % 360) / 22.5 + 0.5) % 16];
  }

  // The UV index on the rounded value: 0 to 2, 3 to 5, 6 to 7, 8 to 10, 11 and above.
  function uvCategory(v) {
    var r = Math.round(v);
    return r <= 2 ? 'Low' : r <= 5 ? 'Moderate' : r <= 7 ? 'High' : r <= 10 ? 'Very high' : 'Extreme';
  }

  function normalize(raw, fetchedAt) {
    var cur = raw.current, h = raw.hourly, d = raw.daily;
    var now = Date.now(), hourly = [], daily = [];
    for (var i = 0; i < h.time.length; i++) {
      var t = localDate(h.time[i]);
      if (t.getTime() + 3600e3 <= now) continue;
      hourly.push({ time: t, temp: h.temperature_2m[i], code: h.weather_code[i],
        pop: h.precipitation_probability[i], isDay: h.is_day[i] === 1 });
      if (hourly.length >= 24) break;
    }
    for (var j = 0; j < d.time.length; j++) {
      daily.push({ date: localDate(d.time[j]), code: d.weather_code[j], max: d.temperature_2m_max[j],
        min: d.temperature_2m_min[j], sunrise: localDate(d.sunrise[j]), sunset: localDate(d.sunset[j]),
        pop: d.precipitation_probability_max[j], uvMax: at(d.uv_index_max, j), rain: at(d.precipitation_sum, j) });
    }
    return {
      fetchedAt: fetchedAt,
      current: { temp: cur.temperature_2m, feels: cur.apparent_temperature, humidity: cur.relative_humidity_2m,
        wind: cur.wind_speed_10m, code: cur.weather_code, isDay: cur.is_day === 1, cloud: cur.cloud_cover / 100,
        windDir: num(cur.wind_direction_10m), gusts: num(cur.wind_gusts_10m), dew: num(cur.dew_point_2m), uv: num(cur.uv_index),
        visibility: num(cur.visibility) },
      hourly: hourly,
      daily: daily
    };
  }

  // Coordinates are used to 2 decimals (about a kilometre) everywhere: the request, the cache and the comparison.
  function rounded(loc) {
    return { latitude: Math.round(loc.latitude * 100) / 100, longitude: Math.round(loc.longitude * 100) / 100 };
  }

  // The cache holds one place. It is read only for the place it was stored for, so another place never shows the old one's weather.
  function readCache(loc) {
    if (!loc) return null;
    try {
      var c = JSON.parse(localStorage.getItem(CACHE_KEY)), want = rounded(loc);
      if (!c || !c.raw || !c.loc || c.loc.latitude !== want.latitude || c.loc.longitude !== want.longitude) return null;
      return normalize(c.raw, c.fetchedAt);
    } catch (e) { return null; }
  }

  function fetchWeather(place) {
    var loc = rounded(place);
    var ctrl = typeof AbortController !== 'undefined' ? new AbortController() : null;
    var timer = ctrl && setTimeout(function () { ctrl.abort(); }, 15000);
    return fetch(url(loc), ctrl ? { signal: ctrl.signal } : {}).then(function (r) {
      if (!r.ok) throw new Error('Weather service returned ' + r.status);
      return r.json();
    }).then(function (raw) {
      var at = Date.now();
      try { localStorage.setItem(CACHE_KEY, JSON.stringify({ raw: raw, fetchedAt: at, loc: loc })); } catch (e) { /* storage full */ }
      return normalize(raw, at);
    }).finally(function () { if (timer) clearTimeout(timer); });
  }

  window.TVWeather = { describe: describe, look: look, looks: LOOKS, compassName: compassName, uvCategory: uvCategory, stars: stars, fetch: fetchWeather, cached: readCache, rounded: rounded };
})();
