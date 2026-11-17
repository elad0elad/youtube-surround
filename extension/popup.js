const fields = [
  ["videoId", "Video ID"],
  ["sourceCodec", "Source codec"],
  ["sourceFormat", "Source format"],
  ["mix", "Mix presentation"],
  ["channelsIn", "Input channels"],
  ["channelsOut", "Output channels"],
  ["layout", "Output layout"],
  ["sampleRate", "Sample rate"],
  ["bufferSeconds", "Buffer"],
  ["latencyMs", "Audio latency"],
  ["decoder", "Decoder"],
  ["device", "Output device"],
  ["outputPath", "Output path"],
  ["rendering", "Rendering"],
  ["renderChannels", "Render channels"],
  ["spatialEngine", "Spatial engine"],
  ["sourceLayout", "Source layout"],
  ["presentations", "IAMF presentations"],
  ["bedChannels", "Bed channels"],
  ["heights", "Height channels"],
  ["sourceObjects", "Audio objects"],
  ["staticChannels", "Static spatial channels"],
  ["dynamicObjects", "Dynamic spatial objects"],
  ["ambisonics", "Ambisonics elements"],
  ["unsupported", "Unsupported elements"],
  ["fallback", "Fallback"],
  ["binaural", "Binaural layout"],
  ["objectMotion", "Object motion"],
  ["underruns", "Underruns"],
  ["deviceSwitches", "Device switches"],
  ["startupMs", "Time to first audio"]
];

const stateLabels = {
  idle: "Waiting",
  checking: "Checking source",
  stereo: "Stereo source",
  detected: "IAMF source detected",
  decoding: "Decoding",
  active: "5.1 Active",
  unavailable: "Surround unavailable",
  error: "Error",
  buffering: "Buffering"
};

const pathLabels = {
  "full-spatial": "Full spatial",
  "discrete-5.1": "Direct 5.1",
  "spatial-bed": "Spatial 5.1 bed",
  "stereo": "Stereo",
  "stereo-downmix": "Stereo"
};

function render(status) {
  const activeLabel = pathLabels[status.outputPath] || "Playing";
  document.getElementById("state").textContent = status.state === "active" ? activeLabel : (stateLabels[status.state] || status.state || "Waiting");
  document.getElementById("enabled").checked = status.enabled !== false;
  document.getElementById("automatic").classList.toggle("on", status.mode !== "manual");
  document.getElementById("manual").classList.toggle("on", status.mode === "manual");
  const output = document.getElementById("output");
  if (document.activeElement !== output) output.value = status.output || status.outputMode || "auto";
  const route = [];
  if (status.device) route.push(status.device);
  if (status.renderChannels) route.push(status.renderChannels + " ch");
  if (status.sourceLayout) route.push(status.sourceLayout);
  if (status.outputPath === "spatial-bed" || status.outputPath === "full-spatial") route.push(status.spatialEngine || "Windows Spatial Audio");
  document.getElementById("route").textContent = route.join(" · ");
  document.getElementById("outputError").textContent = status.outputError || "";
  document.getElementById("detail").textContent = status.detail || "";
  const dl = document.getElementById("advanced");
  dl.replaceChildren();
  fields.forEach(([key, label]) => {
    if (status[key] == null || status[key] === "") return;
    const dt = document.createElement("dt");
    dt.textContent = label;
    const dd = document.createElement("dd");
    let value = status[key];
    if (key === "bufferSeconds") value = Number(value).toFixed(1) + " s";
    if (key === "latencyMs" || key === "startupMs") value = Math.round(Number(value)) + " ms";
    if (key === "sampleRate") value = value + " Hz";
    dd.textContent = String(value);
    dl.append(dt, dd);
  });
}

function send(message) {
  return chrome.runtime.sendMessage(message);
}

document.getElementById("enabled").addEventListener("change", (event) => {
  send({ type: "setPrefs", enabled: event.target.checked });
});
document.getElementById("automatic").addEventListener("click", () => {
  send({ type: "setPrefs", mode: "automatic" });
});
document.getElementById("manual").addEventListener("click", () => {
  send({ type: "setPrefs", mode: "manual" });
});
document.getElementById("output").addEventListener("change", (event) => {
  send({ type: "setPrefs", output: event.target.value });
});
document.getElementById("start").addEventListener("click", async () => {
  const [tab] = await chrome.tabs.query({ active: true, currentWindow: true });
  if (tab) chrome.tabs.sendMessage(tab.id, { type: "force" });
});
document.getElementById("stop").addEventListener("click", () => send({ type: "stop" }));
document.getElementById("tones").addEventListener("click", () => send({ type: "tones" }));

chrome.runtime.onMessage.addListener((message) => {
  if (message && message.type === "status") render(message.status);
});
send({ type: "getStatus" }).then((status) => {
  if (status) render(status);
}).catch(() => {});
