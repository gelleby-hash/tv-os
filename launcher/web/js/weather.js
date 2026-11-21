// Weather from Open-Meteo (no API key). Values are fetched in °C and km/h and converted for display.
(function () {
  'use strict';

  var CACHE_KEY = 'tvhome.weather.v3';
  // The key before the hourly feels-like, wind and humidity were saved; still read, so an old forecast shows after an update.
  var OLD_CACHE_KEYS = ['tvhome.weather.v2'];

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
    // MET Norway's forecast carries its symbol names (partlycloudy_day) where Open-Meteo carries WMO numbers.
    if (typeof code === 'string') {
      var m = metSymbol(code);
      return { label: window.TVI18n ? window.TVI18n.text(m.label) : m.label, kind: m.kind, intensity: m.intensity };
    }
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
      '&hourly=temperature_2m,weather_code,precipitation_probability,is_day,apparent_temperature,wind_speed_10m,relative_humidity_2m' +
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
        pop: h.precipitation_probability[i], isDay: h.is_day[i] === 1,
        feels: at(h.apparent_temperature, i), wind: at(h.wind_speed_10m, i), humidity: at(h.relative_humidity_2m, i) });
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

  // MET Norway's symbol names ("lightrainshowersandthunder_night") as the page's kind, intensity and label. The suffix gives
  // day or night (polar twilight counts as day). The base name is matched by these rules, the first that fits winning; a
  // name that fits none is a cloudy sky labelled Unknown. Two of MET Norway's own names are misspelt (lightssleet...), and
  // matching on parts of the name covers them.
  function metSymbol(code) {
    var name = String(code == null ? '' : code), base = name.replace(/_(day|night|polartwilight)$/, '');
    var isDay = !/_night$/.test(name);
    var light = base.indexOf('light') >= 0, heavy = base.indexOf('heavy') >= 0, showers = base.indexOf('showers') >= 0;
    var amount = light ? 1 : heavy ? 3 : 2;
    function out(label, kind, intensity) { return { label: label, kind: kind, intensity: intensity, isDay: isDay }; }
    if (base.indexOf('thunder') >= 0) return out('Thunderstorm', 'storm', heavy ? 3 : 2);
    if (base.indexOf('snow') >= 0) return out(showers ? 'Snow showers' : light ? 'Light snow' : heavy ? 'Heavy snow' : 'Snow', 'snow', amount);
    if (base.indexOf('sleet') >= 0) return out('Sleet', 'snow', amount);
    if (base.indexOf('rain') >= 0) return out(showers ? (heavy ? 'Heavy showers' : 'Showers') : light ? 'Light rain' : heavy ? 'Heavy rain' : 'Rain', 'rain', amount);
    if (base.indexOf('fog') >= 0) return out('Fog', 'fog', 0);
    if (base.indexOf('partlycloudy') >= 0) return out('Partly cloudy', 'partly', 0);
    if (base.indexOf('fair') >= 0) return out('Mainly clear', 'partly', 0);
    if (base.indexOf('clearsky') >= 0) return out('Clear', 'clear', 0);
    if (base.indexOf('cloudy') >= 0) return out('Cloudy', 'cloudy', 0);
    return out('Unknown', 'cloudy', 0);
  }

  // Sunrise and sunset for a place on the local calendar day of `date`, as Dates (NOAA's sunrise equation, the sun's centre
  // 90.833 degrees from straight up). With the sun up all day or down all day they are the day's two ends, or its noon twice.
  function sunTimes(date, lat, lon) {
    var rad = Math.PI / 180;
    var jd = Date.UTC(date.getFullYear(), date.getMonth(), date.getDate()) / 86400e3 + 2440587.5;
    var n = Math.ceil(jd - 2451545.0 + 0.0008);
    var jStar = n - lon / 360;
    var mean = (357.5291 + 0.98560028 * jStar) % 360;
    var centre = 1.9148 * Math.sin(mean * rad) + 0.02 * Math.sin(2 * mean * rad) + 0.0003 * Math.sin(3 * mean * rad);
    var lambda = (mean + centre + 180 + 102.9372) % 360;
    var transit = 2451545.0 + jStar + 0.0053 * Math.sin(mean * rad) - 0.0069 * Math.sin(2 * lambda * rad);
    var sinDec = Math.sin(lambda * rad) * Math.sin(23.4397 * rad);
    var cosDec = Math.cos(Math.asin(sinDec));
    var cosHour = (Math.sin(-0.833 * rad) - Math.sin(lat * rad) * sinDec) / (Math.cos(lat * rad) * cosDec);
    function toDate(j) { return new Date((j - 2440587.5) * 86400e3); }
    if (cosHour >= 1) return { sunrise: toDate(transit), sunset: toDate(transit) };
    if (cosHour <= -1) {
      return { sunrise: new Date(date.getFullYear(), date.getMonth(), date.getDate()), sunset: new Date(date.getFullYear(), date.getMonth(), date.getDate() + 1) };
    }
    var half = Math.acos(cosHour) / rad / 360;
    return { sunrise: toDate(transit - half), sunset: toDate(transit + half) };
  }

  // Worst weather first, for a day with no 12 hour symbol: storms, then snow, rain, fog, cloud, partly cloudy, clear.
  var SEVERITY = { storm: 70, snow: 60, rain: 50, drizzle: 50, fog: 40, cloudy: 30, partly: 20, clear: 10 };
  function severity(code) { var m = metSymbol(code); return SEVERITY[m.kind] * 10 + m.intensity; }

  function sameLocalDay(a, b) {
    return a.getFullYear() === b.getFullYear() && a.getMonth() === b.getMonth() && a.getDate() === b.getDate();
  }

  // MET Norway's answer (UTC times, m/s, percent) in the shape normalize returns. A value it did not give is null: gusts, rain
  // chance and visibility are not in the complete product, and any step may lack fields. The current weather is the latest step
  // that began by fetchedAt, the hourly list the next 24 hours, the daily list 7 days from today. `place` is the rounded place
  // the forecast is for, which the sunrise and sunset are worked out from.
  function fromMetNorway(raw, fetchedAt, place) {
    var series = raw && raw.properties && raw.properties.timeseries || [];
    var steps = [];
    for (var i = 0; i < series.length; i++) {
      var e = series[i], data = e && e.data || {}, inst = data.instant && data.instant.details || {};
      var n1 = data.next_1_hours, n6 = data.next_6_hours, n12 = data.next_12_hours;
      var sym1 = n1 && n1.summary && n1.summary.symbol_code || null, sym6 = n6 && n6.summary && n6.summary.symbol_code || null;
      var sym12 = n12 && n12.summary && n12.summary.symbol_code || null;
      var t = new Date(e && e.time);
      if (isNaN(t.getTime())) continue;
      var d1 = n1 && n1.details || {}, d6 = n6 && n6.details || {};
      steps.push({
        time: t, inst: inst, has1: !!n1, sym: sym1 || sym6 || sym12, sym12: sym12,
        pop1: num(d1.probability_of_precipitation), pop6: num(d6.probability_of_precipitation),
        rain1: num(d1.precipitation_amount), rain6: num(d6.precipitation_amount),
        max6: num(d6.air_temperature_max), min6: num(d6.air_temperature_min)
      });
    }
    function kmh(v) { return v == null ? null : v * 3.6; }
    function isDayOf(step) {
      if (step.sym && /_(day|night|polartwilight)$/.test(step.sym)) return metSymbol(step.sym).isDay;
      var s = sunTimes(step.time, place.latitude, place.longitude);
      return step.time >= s.sunrise && step.time < s.sunset;
    }
    var now = Date.now(), hourly = [], daily = [], cur = steps[0];
    for (var a = 0; a < steps.length; a++) if (steps[a].time.getTime() <= fetchedAt) cur = steps[a];
    for (var b = 0; b < steps.length && hourly.length < 24; b++) {
      var s = steps[b];
      if (s.time.getTime() + 3600e3 <= now) continue;
      hourly.push({ time: s.time, temp: num(s.inst.air_temperature), code: s.sym, pop: s.pop1, isDay: isDayOf(s),
        feels: num(s.inst.apparent_air_temperature), wind: kmh(num(s.inst.wind_speed)), humidity: num(s.inst.relative_humidity) });
    }
    var days = [];
    steps.forEach(function (st) {
      var day = days.filter(function (d) { return sameLocalDay(d.date, st.time); })[0];
      if (!day) { day = { date: new Date(st.time.getFullYear(), st.time.getMonth(), st.time.getDate()), steps: [] }; days.push(day); }
      day.steps.push(st);
    });
    days.slice(0, 7).forEach(function (day) {
      var highs = [], lows = [], temps = [], rain = null, covered = 0, pop = null, uv = null, noon = null, worst = null;
      var noonAt = new Date(day.date.getFullYear(), day.date.getMonth(), day.date.getDate(), 12).getTime();
      day.steps.forEach(function (st) {
        if (st.max6 != null) highs.push(st.max6);
        if (st.min6 != null) lows.push(st.min6);
        if (st.inst.air_temperature != null) temps.push(st.inst.air_temperature);
        // Rain counts each stretch of time once: an hourly amount, or a 6 hour one where the forecast has no hourly step.
        var t0 = st.time.getTime();
        if (st.has1 && st.rain1 != null) { rain = (rain || 0) + st.rain1; covered = t0 + 3600e3; }
        else if (!st.has1 && st.rain6 != null && t0 >= covered) { rain = (rain || 0) + st.rain6; covered = t0 + 6 * 3600e3; }
        var p = st.pop1 != null ? st.pop1 : st.pop6;
        if (p != null && (pop == null || p > pop)) pop = p;
        var u = num(st.inst.ultraviolet_index_clear_sky);
        if (u != null && (uv == null || u > uv)) uv = u;
        if (st.sym12 && (!noon || Math.abs(t0 - noonAt) < Math.abs(noon.time.getTime() - noonAt))) noon = st;
        if (st.sym && (!worst || severity(st.sym) > severity(worst))) worst = st.sym;
      });
      var hi = highs.length ? Math.max.apply(null, highs) : temps.length ? Math.max.apply(null, temps) : null;
      var lo = lows.length ? Math.min.apply(null, lows) : temps.length ? Math.min.apply(null, temps) : null;
      var sun = sunTimes(day.date, place.latitude, place.longitude);
      daily.push({ date: day.date, code: noon ? noon.sym12 : worst, max: hi, min: lo, sunrise: sun.sunrise, sunset: sun.sunset,
        pop: pop, uvMax: uv, rain: rain });
    });
    var ci = cur ? cur.inst : {};
    return {
      source: 'metno',
      fetchedAt: fetchedAt,
      current: { temp: num(ci.air_temperature), feels: num(ci.apparent_air_temperature), humidity: num(ci.relative_humidity),
        wind: kmh(num(ci.wind_speed)), code: cur ? cur.sym : null, isDay: cur ? isDayOf(cur) : true,
        cloud: ci.cloud_area_fraction == null ? null : ci.cloud_area_fraction / 100,
        windDir: num(ci.wind_from_direction), gusts: kmh(num(ci.wind_speed_of_gust)), dew: num(ci.dew_point_temperature),
        uv: num(ci.ultraviolet_index_clear_sky), visibility: null },
      hourly: hourly,
      daily: daily
    };
  }

  // What the weather is now, from a saved forecast. Within an hour of the fetch that is the fetched "now" reading. After
  // that it is the saved hour that contains nowMs (the last saved hour once they have run out), its values over the old
  // reading's; a value that hour lacks is null, not the old reading's. UV, visibility and the rest stay as fetched.
  function nowFor(w, nowMs) {
    if (nowMs - w.fetchedAt <= 3600e3 || !w.hourly || !w.hourly.length) return w.current;
    var h = w.hourly[w.hourly.length - 1];
    for (var i = 0; i < w.hourly.length; i++) {
      var start = w.hourly[i].time.getTime();
      if (nowMs >= start && nowMs < start + 3600e3) { h = w.hourly[i]; break; }
    }
    var out = {};
    for (var k in w.current) out[k] = w.current[k];
    out.temp = num(h.temp); out.code = h.code; out.isDay = h.isDay;
    out.feels = num(h.feels); out.wind = num(h.wind); out.humidity = num(h.humidity);
    return out;
  }

  // Coordinates are used to 2 decimals (about a kilometre) everywhere: the request, the cache and the comparison.
  function rounded(loc) {
    return { latitude: Math.round(loc.latitude * 100) / 100, longitude: Math.round(loc.longitude * 100) / 100 };
  }

  // The cache holds one place. It is read only for the place it was stored for, so another place never shows the old one's weather.
  function readCache(loc) {
    if (!loc) return null;
    try {
      var saved = localStorage.getItem(CACHE_KEY);
      for (var k = 0; saved == null && k < OLD_CACHE_KEYS.length; k++) saved = localStorage.getItem(OLD_CACHE_KEYS[k]);
      var c = JSON.parse(saved), want = rounded(loc);
      if (!c || !c.raw || !c.loc || c.loc.latitude !== want.latitude || c.loc.longitude !== want.longitude) return null;
      return c.source === 'metno' ? fromMetNorway(c.raw, c.fetchedAt, c.loc) : normalize(c.raw, c.fetchedAt);
    } catch (e) { return null; }
  }

  // MET Norway's answer, kept as the saved forecast for the place and returned in the page's shape.
  function saveMetNorway(raw, fetchedAt, place) {
    var loc = rounded(place), w = fromMetNorway(raw, fetchedAt, loc);
    try { localStorage.setItem(CACHE_KEY, JSON.stringify({ raw: raw, fetchedAt: fetchedAt, loc: loc, source: 'metno' })); } catch (e) { /* storage full */ }
    return w;
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

  window.TVWeather = { describe: describe, look: look, looks: LOOKS, compassName: compassName, uvCategory: uvCategory, stars: stars, fetch: fetchWeather, saveMetNorway: saveMetNorway, metSymbol: metSymbol, sunTimes: sunTimes, fromMetNorway: fromMetNorway, cached: readCache, rounded: rounded, nowFor: nowFor };
})();
