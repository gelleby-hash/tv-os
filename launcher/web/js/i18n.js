// The page's strings are British English (the launcher is English only, owner 2026-11-02). text() fills a string's
// {name} placeholders; a placeholder with no value is left as it is.
(function () {
  'use strict';
  function text(key, values) {
    return key.replace(/\{([a-z]+)\}/g, function (match, name) {
      return values && Object.prototype.hasOwnProperty.call(values, name) ? String(values[name]) : match;
    });
  }
  window.TVI18n = { text: text };
})();
