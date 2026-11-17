const HOST = "com.youtubesurround.host";

let port = null;
let status = {
  state: "idle",
  enabled: true,
  mode: "automatic",
  output: "auto",
  detail: "Waiting for a YouTube video."
};
let activeTabId = null;

function normalizeOutput(value) {
  if (value === "speakers51" || value === "atmos" || value === "surroundHeadphones" || value === "stereo") return value;
  return "auto";
}

chrome.storage.local.get({ enabled: true, mode: "automatic", output: "auto" }, (saved) => {
  status.enabled = saved.enabled !== false;
  status.mode = saved.mode === "manual" ? "manual" : "automatic";
  status.output = normalizeOutput(saved.output);
  if (port) send({ type: "set-output", output: status.output });
  broadcast();
});

function broadcast() {
  chrome.runtime.sendMessage({ type: "status", status }).catch(() => {});
  if (activeTabId != null) {
    chrome.tabs.sendMessage(activeTabId, { type: "status", status }).catch(() => {});
  }
}

let connectFailedAt = 0;
let resumeAt = 0;
let leaveTimer = null;
let leaveTabId = null;
let leaveToken = null;

function cancelLeave(token) {
  if (token && leaveToken && token !== leaveToken) return;
  if (leaveTimer) clearTimeout(leaveTimer);
  leaveTimer = null;
  leaveTabId = null;
  leaveToken = null;
}

function scheduleLeave(tabId, videoId, token) {
  cancelLeave(null);
  leaveTabId = tabId;
  leaveToken = token || null;
  leaveTimer = setTimeout(() => {
    leaveTimer = null;
    leaveToken = null;
    if (tabId != null && activeTabId != null && tabId !== activeTabId) return;
    activeTabId = null;
    send({ type: "stop", videoId: videoId || "" });
  }, 3000);
}

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

chrome.tabs.onRemoved.addListener((tabId) => {
  if (leaveTabId === tabId) cancelLeave(null);
  if (activeTabId == null || tabId !== activeTabId) return;
  activeTabId = null;
  send({ type: "stop" });
});

chrome.tabs.onUpdated.addListener((tabId, info, tab) => {
  if (activeTabId == null || tabId !== activeTabId) return;
  const url = info.url || (tab && tab.url) || "";
  if (!url || url.indexOf("http") !== 0) return;
  if (videoIdFromUrl(url)) return;
  activeTabId = null;
  cancelLeave(null);
  send({ type: "stop" });
});

function connect() {
  if (port) return;
  if (Date.now() - connectFailedAt < 4000) return;
  try {
    port = chrome.runtime.connectNative(HOST);
  } catch (err) {
    connectFailedAt = Date.now();
    status.state = "error";
    status.detail = "The Windows audio component is not installed.";
    broadcast();
    port = null;
    return;
  }
  port.onMessage.addListener((message) => {
    if (!message || message.type !== "status") return;
    status = Object.assign({}, status, message, {
      enabled: status.enabled,
      mode: status.mode,
      output: status.output
    });
    broadcast();
  });
  try { port.postMessage({ type: "set-output", output: status.output || "auto" }); } catch (err) {}
  port.onDisconnect.addListener(() => {
    const reason = chrome.runtime.lastError && chrome.runtime.lastError.message;
    const resume = status.videoId && (status.state === "active" || status.state === "decoding" || status.state === "checking");
    const tabId = activeTabId;
    connectFailedAt = Date.now();
    port = null;
    status.state = "error";
    status.detail = reason || "The Windows audio component stopped. Reconnecting.";
    broadcast();
    if (resume && tabId != null && Date.now() - resumeAt > 10000) {
      resumeAt = Date.now();
      setTimeout(() => {
        chrome.tabs.sendMessage(tabId, { type: "rescan" }).catch(() => {});
      }, 400);
    }
  });
}

function send(message) {
  connect();
  if (!port) return;
  try {
    port.postMessage(message);
  } catch (err) {
    port = null;
    status.state = "error";
    status.detail = "Could not talk to the Windows audio component.";
    broadcast();
  }
}

chrome.runtime.onConnect.addListener(() => {});

chrome.runtime.onMessage.addListener((message, sender, sendResponse) => {
  if (!message || !message.type) return;
  if (message.type === "here") {
    if (!sender.tab || leaveTabId == null || sender.tab.id === leaveTabId) cancelLeave(message.token || "");
    sendResponse(status);
    return;
  }
  if (message.type === "leaving") {
    if (sender.tab && activeTabId != null && sender.tab.id !== activeTabId) {
      sendResponse(status);
      return;
    }
    scheduleLeave(sender.tab ? sender.tab.id : activeTabId, message.videoId, message.token);
    sendResponse(status);
    return;
  }
  if ((message.type === "watch" || message.type === "clock") && sender.tab && (leaveTabId == null || sender.tab.id === leaveTabId)) {
    cancelLeave(null);
  }
  if (message.type === "getStatus") {
    connect();
    sendResponse(status);
    return;
  }
  if (message.type === "setPrefs") {
    if (typeof message.enabled === "boolean") status.enabled = message.enabled;
    if (message.mode === "automatic" || message.mode === "manual") status.mode = message.mode;
    if (typeof message.output === "string") status.output = normalizeOutput(message.output);
    chrome.storage.local.set({ enabled: status.enabled, mode: status.mode, output: status.output });
    if (typeof message.output === "string") send({ type: "set-output", output: status.output });
    if (!status.enabled) send({ type: "stop" });
    broadcast();
    sendResponse(status);
    return;
  }
  if (sender.tab && (message.visible === true || activeTabId == null)) activeTabId = sender.tab.id;
  if (sender.tab && activeTabId != null && sender.tab.id !== activeTabId) {
    sendResponse(status);
    return;
  }
  if (!status.enabled && message.type !== "stop") {
    sendResponse(status);
    return;
  }
  if (message.type === "watch") {
    if (status.mode === "manual" && !message.force) {
      const switched = status.videoId && message.videoId !== status.videoId;
      if (switched && (status.state === "active" || status.state === "decoding" || status.state === "checking")) {
        send({ type: "stop" });
      }
      status.videoId = message.videoId;
      status.state = "idle";
      status.channelsOut = 0;
      status.layout = "";
      status.sourceCodec = "";
      status.sourceFormat = "";
      status.detail = "Manual mode. Press Play Surround when you want 5.1.";
      broadcast();
      sendResponse(status);
      return;
    }
    send({
      type: "start",
      url: message.url,
      videoId: message.videoId,
      time: message.time || 0,
      paused: !!message.paused,
      rate: message.rate || 1,
      ad: !!message.ad
    });
    sendResponse(status);
    return;
  }
  if (message.type === "clock") {
    if (sender.tab && activeTabId != null && sender.tab.id !== activeTabId) {
      sendResponse(status);
      return;
    }
    if (port) send(message);
    sendResponse(status);
    return;
  }
  if (message.type === "stop" || message.type === "tones") {
    send(message);
    sendResponse(status);
  }
});
