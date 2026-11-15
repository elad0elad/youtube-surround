const HOST = "com.youtubesurround.host";

let port = null;
let status = {
  state: "idle",
  enabled: true,
  mode: "automatic",
  detail: "Waiting for a YouTube video."
};
let activeTabId = null;

chrome.storage.local.get({ enabled: true, mode: "automatic" }, (saved) => {
  status.enabled = saved.enabled !== false;
  status.mode = saved.mode === "manual" ? "manual" : "automatic";
});

function broadcast() {
  chrome.runtime.sendMessage({ type: "status", status }).catch(() => {});
  if (activeTabId != null) {
    chrome.tabs.sendMessage(activeTabId, { type: "status", status }).catch(() => {});
  }
}

let connectFailedAt = 0;

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
      mode: status.mode
    });
    broadcast();
  });
  port.onDisconnect.addListener(() => {
    const reason = chrome.runtime.lastError && chrome.runtime.lastError.message;
    connectFailedAt = Date.now();
    port = null;
    status.state = "error";
    status.detail = reason || "The Windows audio component is not installed.";
    broadcast();
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
  if (message.type === "getStatus") {
    sendResponse(status);
    return;
  }
  if (message.type === "setPrefs") {
    if (typeof message.enabled === "boolean") status.enabled = message.enabled;
    if (message.mode === "automatic" || message.mode === "manual") status.mode = message.mode;
    chrome.storage.local.set({ enabled: status.enabled, mode: status.mode });
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
