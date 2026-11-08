// Messages between this page and the Windows launcher (WebView2). In a plain browser there is no host:
// post only logs, so the page can still be opened for a look.
(function () {
  'use strict';

  var webview = window.chrome && window.chrome.webview;
  var handlers = Object.create(null);

  function post(msg) {
    if (webview) webview.postMessage(msg);
    else if (window.console) console.log('[host] ' + JSON.stringify(msg));
  }

  function on(type, fn) {
    (handlers[type] = handlers[type] || []).push(fn);
  }

  if (webview) {
    webview.addEventListener('message', function (e) {
      var msg = e.data;
      if (typeof msg === 'string') {
        try { msg = JSON.parse(msg); } catch (err) { return; }
      }
      if (!msg || typeof msg.type !== 'string' || !handlers[msg.type]) return;
      handlers[msg.type].forEach(function (fn) {
        try { fn(msg); } catch (err) { if (window.console) console.error('[host] handler for ' + msg.type + ' failed: ' + err.message); }
      });
    });
  }

  window.TVHost = { post: post, on: on, isHost: !!webview };
})();
