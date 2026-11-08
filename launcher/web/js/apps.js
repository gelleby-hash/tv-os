// The app tiles: the apps the host arranged, opened by asking the host. The list starts as the config script's `apps`
// and follows the host's `apps` message, which sends every app in the family's order (hidden ones too).
(function () {
  'use strict';

  var cfg = window.TV_HOME_CONFIG;
  var apps = cfg.apps || [], boostApps = cfg.boostApps || [], changed = null;

  // Every app in order, with `shown`, the chosen `version` and the `versions` to choose from.
  function all() {
    return apps.slice();
  }

  // The apps on the home row: the shown ones, in order.
  function list() {
    return Promise.resolve(apps.filter(function (a) { return a.shown; }).map(function (a) {
      return { id: a.id, name: a.name, icon: a.icon };
    }));
  }

  // The names the boost button raises the sound of.
  function boost() {
    return boostApps.slice();
  }

  // One function, called after each `apps` message once the new list is in place (main.js redraws from it).
  function onChange(fn) {
    changed = fn;
  }

  window.TVHost.on('apps', function (msg) {
    if (!Array.isArray(msg.apps)) return;
    apps = msg.apps.filter(function (a) { return a && typeof a.id === 'string'; });
    boostApps = Array.isArray(msg.boostApps) ? msg.boostApps : [];
    if (changed) changed();
  });

  function launch(id) {
    window.TVHost.post({ type: 'launch', id: id });
    return Promise.resolve();
  }

  // The launcher has nothing to exit to; kept so the interface stays {list, launch, exit, isHost}.
  function exit() {}

  window.TVApps = { list: list, all: all, boostApps: boost, onChange: onChange, launch: launch, exit: exit, isHost: window.TVHost.isHost };
})();
