const BADGE_ID = "youtube-surround-badge";
const LEAVE_KEY = "ys-leave";

let video = null;
let lastVideoId = "";
let watchSent = false;
let lastStatus = { state: "idle", enabled: true, mode: "automatic" };
let clockTimer = null;
let wireFailed = false;

function videoIdFromUrl(url) {
  try {
    const u = new URL(url);
    const fromQuery = u.searchParams.get("v") || "";
    if (/^[A-Za-z0-9_-]{11}$/.test(fromQuery)) return fromQuery;
    const parts = u.pathname.split("/").filter(Boolean);
    if (u.hostname === "youtu.be" && parts[0] && /^[A-Za-z0-9_-]{11}$/.test(parts[0])) return parts[0];
    if (parts.length >= 2 && /^(shorts|live|embed|v)$/.test(parts[0]) && /^[A-Za-z0-9_-]{11}$/.test(parts[1])) return parts[1];
    return "";
  } catch (err) {
    return "";
  }
}

function currentVideo() {
  return document.querySelector("video.html5-main-video") || document.querySelector("video");
}

function adShowing() {
  return !!document.querySelector(".ad-showing, .ad-interrupting");
}

function ensureBadge() {
  let badge = document.getElementById(BADGE_ID);
  if (badge) return badge;
  const player = document.querySelector("#movie_player") || document.body;
  if (!player) return null;
  badge = document.createElement("div");
  badge.id = BADGE_ID;
  badge.style.cssText = [
    "position:absolute",
    "right:12px",
    "top:12px",
    "z-index:40",
    "padding:6px 10px",
    "border-radius:999px",
    "font:12px/1.3 Segoe UI,sans-serif",
    "color:#f4f1ea",
    "background:rgba(12,14,18,.82)",
    "border:1px solid rgba(255,255,255,.14)",
    "pointer-events:none",
    "letter-spacing:.01em"
  ].join(";");
  if (getComputedStyle(player).position === "static") player.style.position = "relative";
  player.appendChild(badge);
  return badge;
}

function paint(status) {
  lastStatus = status || lastStatus;
  const badge = ensureBadge();
  if (!badge) return;
  const labels = {
    idle: "YouTube Surround",
    checking: "Checking source",
    stereo: "Stereo source",
    detected: "IAMF source detected",
    decoding: "Decoding",
    active: "5.1 Active",
    unavailable: "Surround unavailable",
    error: "Surround error",
    buffering: "Buffering"
  };
  const state = lastStatus.state || "idle";
  const covered = bufferCoversPlayhead();
  const shown = state === "active" && !covered ? "buffering" : state;
  let label = labels[shown] || shown;
  if (shown === "active" && (lastStatus.outputPath === "stereo" || lastStatus.outputPath === "stereo-downmix")) label = "Stereo downmix";
  if (shown === "active" && lastStatus.outputPath === "discrete-5.1") label = "Direct 5.1";
  if (shown === "active" && lastStatus.outputPath === "full-spatial") {
    label = "Full spatial";
    if (lastStatus.sourceLayout) label += " · " + lastStatus.sourceLayout;
  }
  if (shown === "active" && lastStatus.outputPath === "spatial-bed") label = "Spatial bed";
  if ((shown === "active" || shown === "buffering") && lastStatus.device) label += " · " + lastStatus.device;
  if (wireFailed && state === "active") label += " · refresh the tab";
  badge.textContent = label + (state === "error" && lastStatus.detail ? " — " + lastStatus.detail : "");
  badge.style.display = lastStatus.enabled === false ? "none" : "block";
  const holdBrowserStereo = state === "active" && covered && !adShowing();
  setStereoAudible(!holdBrowserStereo);
}

function bufferCoversPlayhead() {
  const buffered = Number(lastStatus && lastStatus.bufferSeconds);
  if (!Number.isFinite(buffered) || !video) return false;
  return buffered + 0.15 >= (video.currentTime || 0);
}

function setStereoAudible(audible) {
  document.dispatchEvent(new CustomEvent("ys-audible", { detail: { audible: !!audible } }));
}

function userMix() {
  const player = document.getElementById("movie_player");
  if (player && typeof player.getVolume === "function" && typeof player.isMuted === "function") {
    const raw = Number(player.getVolume());
    const volume = Math.max(0, Math.min(1, raw / 100));
    return { volume: Number.isFinite(volume) ? volume : 1, muted: !!player.isMuted() };
  }
  if (!video) return { volume: 1, muted: false };
  return { volume: Math.max(0, Math.min(1, Number(video.volume) || 0)), muted: !!video.muted };
}

function post(message) {
  chrome.runtime.sendMessage(message).catch(() => {});
}

function sendWatch(force) {
  if (!video) return;
  const id = videoIdFromUrl(location.href);
  if (!id) return;
  post({
    type: "watch",
    force: !!force,
    visible: document.visibilityState === "visible",
    url: location.href.split("#")[0],
    videoId: id,
    time: video.currentTime || 0,
    paused: video.paused,
    rate: video.playbackRate || 1,
    ad: adShowing(),
    volume: userMix().volume,
    muted: userMix().muted
  });
}

function sendClock() {
  if (!video) return;
  const id = videoIdFromUrl(location.href);
  if (!id || id !== lastVideoId) return;
  post({
    type: "clock",
    visible: document.visibilityState === "visible",
    videoId: id,
    time: video.currentTime || 0,
    paused: video.paused || adShowing(),
    rate: video.playbackRate || 1,
    ad: adShowing(),
    volume: userMix().volume,
    muted: userMix().muted
  });
}

function stopSurround() {
  const id = lastVideoId;
  lastVideoId = "";
  watchSent = false;
  if (id) post({ type: "stop", videoId: id });
  setStereoAudible(true);
}

function attach(next) {
  if (video === next) return;
  video = next;
  if (!video) return;
  ["play", "pause", "seeked", "ratechange", "emptied", "volumechange", "ended"].forEach((name) => {
    video.addEventListener(name, () => {
      sendClock();
    });
  });
}

function scan() {
  attach(currentVideo());
  const id = videoIdFromUrl(location.href);
  if (!id) {
    if (lastVideoId) stopSurround();
    const badge = document.getElementById(BADGE_ID);
    if (badge) badge.style.display = "none";
    return;
  }
  if (id !== lastVideoId) {
    lastVideoId = id;
    watchSent = false;
    wireFailed = false;
  }
  if (!watchSent && video) {
    watchSent = true;
    sendWatch(false);
  }
  paint(lastStatus);
}

chrome.runtime.onMessage.addListener((message) => {
  if (message && message.type === "force") sendWatch(true);
  if (message && message.type === "status") paint(message.status);
  if (message && message.type === "rescan") {
    lastVideoId = "";
    scan();
  }
});

document.addEventListener("ys-wire-failed", () => {
  wireFailed = true;
  paint(lastStatus);
});
document.addEventListener("ys-wire-ok", () => {
  wireFailed = false;
});

document.addEventListener("yt-navigate-finish", () => {
  watchSent = false;
  scan();
});

document.addEventListener("visibilitychange", () => {
  sendClock();
  if (document.visibilityState === "visible") {
    watchSent = false;
    scan();
  }
});

window.addEventListener("pagehide", (event) => {
  if (event.persisted) return;
  document.dispatchEvent(new CustomEvent("ys-release"));
  const token = String(Date.now());
  try { sessionStorage.setItem(LEAVE_KEY, token); } catch (err) {}
  post({ type: "leaving", videoId: lastVideoId, token: token });
});

function holdServiceWorker() {
  try {
    const port = chrome.runtime.connect({ name: "youtube-surround" });
    port.onDisconnect.addListener(() => {
      if (chrome.runtime && chrome.runtime.id) setTimeout(holdServiceWorker, 250);
    });
  } catch (err) {}
}

try {
  const pending = sessionStorage.getItem(LEAVE_KEY);
  if (pending) {
    sessionStorage.removeItem(LEAVE_KEY);
    post({ type: "here", token: pending });
  }
} catch (err) {}

clockTimer = setInterval(() => {
  scan();
  sendClock();
}, 250);

scan();
holdServiceWorker();
chrome.runtime.sendMessage({ type: "getStatus" }, (status) => {
  if (status) paint(status);
});
