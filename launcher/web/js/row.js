// Scrolling a row (the home tiles sideways, the Apps panel's rows downwards) so the focused item stays in view. Pure arithmetic:
// main.js owns the elements and measures them.
(function () {
  'use strict';

  // The new offset so the item spanning [start, end) lies inside a window of `size` that starts at `offset`, over content `total`
  // long. It moves only as far as needed and stays within [0, max(0, total - size)], so a shorter row pulls the offset back.
  function keepInView(start, end, size, offset, total) {
    var max = Math.max(0, total - size);
    var o = Math.min(Math.max(offset, 0), max);
    if (start < o) o = start;
    else if (end > o + size) o = end - size;
    return Math.min(Math.max(o, 0), max);
  }

  // Whether content is hidden before the window and after it (1 px of slack for rounding).
  function edges(offset, size, total) {
    return { before: offset > 1, after: offset + size < total - 1 };
  }

  window.TVRow = { keepInView: keepInView, edges: edges };
})();
