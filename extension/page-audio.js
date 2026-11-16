(function () {
  if (window.__ytSurround && window.__ytSurround.installed) return;
  var state = window.__ytSurround || { ctx: null, gain: null, video: null };
  state.installed = true;
  window.__ytSurround = state;

  function currentVideo() {
    return document.querySelector("video.html5-main-video") || document.querySelector("video");
  }

  function wire() {
    var video = currentVideo();
    if (!video) return false;
    if (state.ctx && state.gain && state.video === video) {
      if (state.ctx.state === "suspended") state.ctx.resume();
      return true;
    }
    try {
      var ctx = new AudioContext();
      var source = ctx.createMediaElementSource(video);
      var gain = ctx.createGain();
      gain.gain.value = 1;
      source.connect(gain);
      gain.connect(ctx.destination);
      state.ctx = ctx;
      state.gain = gain;
      state.video = video;
      document.dispatchEvent(new CustomEvent("ys-wire-ok"));
      return true;
    } catch (err) {
      document.dispatchEvent(new CustomEvent("ys-wire-failed"));
      return false;
    }
  }

  document.addEventListener("ys-audible", function (event) {
    var audible = !!(event.detail && event.detail.audible);
    if (!audible && !wire()) return;
    if (!state.gain) return;
    state.gain.gain.value = audible ? 1 : 0;
    if (state.ctx && state.ctx.state === "suspended") state.ctx.resume();
  });

  document.addEventListener("ys-release", function () {
    if (state.gain) state.gain.gain.value = 1;
  });
})();
