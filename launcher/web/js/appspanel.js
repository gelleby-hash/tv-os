// The Apps panel's pure parts: the rows as HTML, and the small questions the panel asks of the app list (main.js owns the
// focus, the keys and the messages to the host). An app here is what TVApps.all() returns: id, name, icon, shown, version, versions.
(function () {
  'use strict';

  function esc(s) { return String(s).replace(/[&<>"]/g, function (c) { return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]; }); }
  function tr(text) { return window.TVI18n ? window.TVI18n.text(text) : text; }

  function indexOf(apps, id) {
    for (var i = 0; i < apps.length; i++) if (apps[i].id === id) return i;
    return -1;
  }

  function versionIndex(app) {
    var versions = app.versions || [];
    for (var i = 0; i < versions.length; i++) if (versions[i].id === app.version) return i;
    return 0;
  }

  // The id of the version after the chosen one, wrapping; an app with one version keeps it.
  function nextVersion(app) {
    var versions = app.versions || [];
    if (versions.length < 2) return app.version;
    return versions[(versionIndex(app) + 1) % versions.length].id;
  }

  // False for the one shown app: the home row never goes empty. A hidden app can always be shown.
  function canHide(apps, id) {
    var i = indexOf(apps, id);
    if (i < 0) return false;
    if (!apps[i].shown) return true;
    return apps.filter(function (a) { return a.shown; }).length > 1;
  }

  // The index in the full list after one step up (-1) or down (+1), or null at an end.
  function moveIndex(apps, id, dir) {
    var i = indexOf(apps, id), to = i + dir;
    return i < 0 || to < 0 || to >= apps.length ? null : to;
  }

  // One row per app: the row itself (pick it up to move it), the version choice where there is one, and the show switch.
  function rowsHtml(apps, carryingId) {
    return apps.map(function (a) {
      var id = esc(a.id), versions = a.versions || [];
      var cls = 'app-row' + (a.shown ? '' : ' off') + (a.id === carryingId ? ' carrying' : '');
      var logo = a.icon ? '<img class="app-logo" src="' + esc(a.icon) + '" alt="">' : '';
      var version = versions.length > 1
        ? '<button class="pill-btn app-version focusable" data-app-version="' + id + '">' + esc(versions[versionIndex(a)].name) + '</button>' : '';
      var label = esc(tr('Show on home'));
      return '<div class="' + cls + '">' +
        '<button class="pill-btn app-main focusable" data-app-row="' + id + '">' + logo + '<span class="app-name">' + esc(a.name) + '</span></button>' +
        version +
        '<button class="pill-btn app-switch focusable' + (a.shown ? ' on' : '') + '" data-app-show="' + id + '" role="switch" aria-checked="' +
          (a.shown ? 'true' : 'false') + '" aria-label="' + label + '"><span>' + label + '</span><i class="sw-track"></i></button>' +
        '</div>';
    }).join('');
  }

  window.TVAppsPanel = { rowsHtml: rowsHtml, nextVersion: nextVersion, canHide: canHide, moveIndex: moveIndex };
})();
