// The moon's phase from the date alone (mean synodic month, good to about a day).
(function () {
  'use strict';

  var SYNODIC = 29.530588853;
  var NEW_MOON = Date.UTC(2000, 0, 6, 18, 14);

  // 0 new, 0.25 first quarter, 0.5 full, 0.75 last quarter.
  function phase(date) {
    var days = (date.getTime() - NEW_MOON) / 86400000;
    return ((days / SYNODIC) % 1 + 1) % 1;
  }

  // The lit share of the disc, 0 to 1.
  function lit(p) {
    return (1 - Math.cos(2 * Math.PI * p)) / 2;
  }

  window.TVMoon = { phase: phase, lit: lit };
})();
