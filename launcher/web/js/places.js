// Choosing the weather's place by hand: the list of countries and the town search (Open-Meteo's geocoding service, no key).
(function () {
  'use strict';

  // Every ISO 3166-1 alpha-2 code. The names come from the browser, in the page's language.
  var CODES = ('AD AE AF AG AI AL AM AO AQ AR AS AT AU AW AX AZ BA BB BD BE BF BG BH BI BJ BL BM BN BO BQ BR BS BT BV BW BY BZ ' +
    'CA CC CD CF CG CH CI CK CL CM CN CO CR CU CV CW CX CY CZ DE DJ DK DM DO DZ EC EE EG EH ER ES ET FI FJ FK FM FO FR ' +
    'GA GB GD GE GF GG GH GI GL GM GN GP GQ GR GS GT GU GW GY HK HM HN HR HT HU ID IE IL IM IN IO IQ IR IS IT JE JM JO JP ' +
    'KE KG KH KI KM KN KP KR KW KY KZ LA LB LC LI LK LR LS LT LU LV LY MA MC MD ME MF MG MH MK ML MM MN MO MP MQ MR MS MT ' +
    'MU MV MW MX MY MZ NA NC NE NF NG NI NL NO NP NR NU NZ OM PA PE PF PG PH PK PL PM PN PR PS PT PW PY QA RE RO RS RU RW ' +
    'SA SB SC SD SE SG SH SI SJ SK SL SM SN SO SR SS ST SV SX SY SZ TC TD TF TG TH TJ TK TL TM TN TO TR TT TV TW TZ ' +
    'UA UG UM US UY UZ VA VC VE VG VI VN VU WF WS YE YT ZA ZM ZW').split(' ');

  var SEARCH = 'https://geocoding-api.open-meteo.com/v1/search';
  var COUNT = 8;

  // [{ code, name }] sorted by name, with Windows' region (when it is in the list) first. A code the browser cannot name is left out.
  function countries(language, regionCode) {
    var names;
    try { names = new Intl.DisplayNames([language], { type: 'region' }); } catch (e) { return []; }
    var list = [];
    CODES.forEach(function (code) {
      var name;
      try { name = names.of(code); } catch (e) { return; }
      if (name && name !== code) list.push({ code: code, name: name });
    });
    list.sort(function (a, b) { return a.name.localeCompare(b.name, language); });
    for (var i = 0; i < list.length; i++) {
      if (list[i].code === regionCode) { list.unshift(list.splice(i, 1)[0]); break; }
    }
    return list;
  }

  function round(n) { return Math.round(n * 100) / 100; }
  function pageLanguage() {
    var lang = typeof document !== 'undefined' && document.documentElement && document.documentElement.lang;
    return String(lang || 'en').slice(0, 2);
  }

  // The towns matching text in one country: [{ name, area, latitude, longitude }], area being the county and region (each once).
  // No request under 3 letters (two letters find nothing, 2026-11-02). Rejects when the service cannot be reached or answers an error.
  function search(text, code) {
    if (String(text).trim().length < 3) return Promise.resolve([]);
    var url = SEARCH + '?name=' + encodeURIComponent(text) + '&countryCode=' + encodeURIComponent(code) + '&count=' + COUNT +
      '&language=' + pageLanguage() + '&format=json';
    return fetch(url).then(function (r) {
      if (!r.ok) throw new Error('Place search returned ' + r.status);
      return r.json();
    }).then(function (body) {
      return (body && body.results || []).map(function (p) {
        var area = [];
        [p.admin2, p.admin1].forEach(function (a) { if (a && area.indexOf(a) < 0) area.push(a); });
        return { name: p.name, area: area.join(', '), latitude: round(p.latitude), longitude: round(p.longitude) };
      });
    });
  }

  window.TVPlaces = { countries: countries, search: search };
})();
