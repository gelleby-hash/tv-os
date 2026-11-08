// Background: Windows Spotlight photos rotating on their own two .photo-layer divs, crossfading like landscape.js.
// The photo layers sit above the weather landscape (landscape.js, untouched), which keeps rendering underneath as the
// fallback. A landscape image that finishes loading late therefore can never cover a photo.
(function () {
  'use strict';

  var DEFAULT_SECONDS = 60;
  var photos = [], failed = {}, index = -1;
  var element = null;
  var layerIn = -1;      // photo layer holding the photo on show, -1 while none is showing
  var ticket = 0;        // a load that is no longer the newest is ignored
  var timer = null;

  function layers() { return element.querySelectorAll('.photo-layer'); }
  function scrim() { return document.getElementById('scrim'); }

  function rotateMs() {
    var cfg = window.TV_HOME_CONFIG, s = cfg && Number(cfg.backgroundRotateSeconds);
    return (s > 0 ? s : DEFAULT_SECONDS) * 1000;
  }

  function show(url) {
    var all = layers();
    layerIn = layerIn === 0 ? 1 : 0;
    all[layerIn].style.backgroundImage = 'url("' + url + '")';
    all[layerIn].style.opacity = '1';
    all[layerIn === 0 ? 1 : 0].style.opacity = '0';
    var s = scrim();
    if (s) s.classList.add('photo');
  }

  // No photo to show: fade the photo layers out and the weather landscape underneath is what is left.
  function fallBack() {
    ticket++;
    var s = scrim();
    if (s) s.classList.remove('photo');
    if (!element || layerIn < 0) return;
    var all = layers();
    for (var i = 0; i < all.length; i++) all[i].style.opacity = '0';
    layerIn = -1;
  }

  // Next photo that has not failed to load, after the one at `from`; -1 when none is left.
  function pick(from) {
    for (var step = 1; step <= photos.length; step++) {
      var i = (from + step) % photos.length;
      if (!failed[photos[i]]) return i;
    }
    return -1;
  }

  function load(i) {
    var url = photos[i], mine = ++ticket, image = new Image();
    index = i;
    image.onload = function () {
      if (mine !== ticket) return;
      show(url);
    };
    image.onerror = function () {
      if (mine !== ticket) return;
      failed[url] = true;
      var n = pick(i);
      if (n < 0) {
        failed = {};   // every photo failed: show the landscape, try them all again on the next rotation
        fallBack();
      } else load(n);
    };
    image.src = url;
    return url;
  }

  function next() {
    if (!photos.length) return null;
    var n = pick(index);
    if (n < 0) { failed = {}; n = pick(index); }
    return n < 0 ? null : load(n);
  }

  function stopTimer() {
    if (timer !== null) clearInterval(timer);
    timer = null;
  }

  function setPhotos(urls) {
    if (!Array.isArray(urls)) return;
    var list = urls.filter(function (u) { return typeof u === 'string' && u !== ''; });
    if (list.join('\n') === photos.join('\n')) return;
    photos = list;
    failed = {};
    index = -1;
    stopTimer();
    if (!photos.length) { fallBack(); return; }
    if (photos.length > 1) timer = setInterval(next, rotateMs());
    if (element) load(0);
  }

  // The landscape always renders (it is the fallback and follows the weather); photos sit on top of it.
  function update(el, state) {
    element = el;
    window.TVLandscape.render(el, state);
    if (photos.length && index < 0) load(0);
  }

  window.TVBackground = { setPhotos: setPhotos, update: update, next: next };

  if (window.TVHost) {
    window.TVHost.on('backgrounds', function (msg) {
      if (Array.isArray(msg.urls)) window.TVBackground.setPhotos(msg.urls);
    });
  }
})();
