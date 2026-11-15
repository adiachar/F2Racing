// Shots.jslib — browser side of DebugShots.cs (WebGL builds only; .jslib plugins are never part of Android builds).
// ShotsTitle: progress for the screenshot harness (Tools/Shots/shoot.py) through document.title.
// ShotsCanvas: canvas size in CSS pixels, device pixel ratio 1, so the 3D picture is exactly w x h.
// ShotsAck: the last shot index the harness has saved (it sets window.__shotAck), -1 = none yet.
mergeInto(LibraryManager.library, {
  ShotsTitle: function (ptr) {
    try { document.title = UTF8ToString(ptr); } catch (e) {}
  },
  ShotsCanvas: function (w, h) {
    try {
      if (typeof Module !== 'undefined') Module.devicePixelRatio = 1;
      var c = (typeof Module !== 'undefined' && Module.canvas) || document.querySelector('#unity-canvas') || document.querySelector('canvas');
      if (!c) return;
      c.style.width = w + 'px';
      c.style.height = h + 'px';
      var foot = document.querySelector('#unity-footer');
      if (foot) foot.style.display = 'none';
    } catch (e) {}
  },
  ShotsAck: function () {
    var a = (typeof window !== 'undefined') ? window.__shotAck : undefined;
    return (typeof a === 'number') ? (a | 0) : -1;
  }
});
