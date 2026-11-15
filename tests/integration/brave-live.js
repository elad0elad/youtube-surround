// Drives a Brave profile that already has the extension loaded and remote debugging open.
const fs = require("fs");
const path = require("path");

const port = process.env.YS_DEBUG_PORT || "9333";
const logPath = path.join(process.env.LOCALAPPDATA, "YouTubeSurround", "host.log");
const iamf = "https://www.youtube.com/watch?v=iMrnS6finbA";
const stereo = "https://www.youtube.com/watch?v=jNQXAC9IVRw";

function sleep(ms) { return new Promise((r) => setTimeout(r, ms)); }

async function listTabs() {
  const res = await fetch("http://127.0.0.1:" + port + "/json/list");
  if (!res.ok) throw new Error("debug port " + res.status);
  return res.json();
}

async function openTab(url) {
  const res = await fetch("http://127.0.0.1:" + port + "/json/new?" + encodeURIComponent(url), { method: "PUT" });
  if (!res.ok) {
    const alt = await fetch("http://127.0.0.1:" + port + "/json/new?" + url);
    if (!alt.ok) throw new Error("open tab " + res.status);
    return alt.json();
  }
  return res.json();
}

function connect(wsUrl) {
  const ws = new WebSocket(wsUrl);
  let seq = 0;
  const pending = new Map();
  const contexts = [];
  ws.addEventListener("message", (ev) => {
    const msg = JSON.parse(ev.data);
    if (msg.method === "Runtime.executionContextCreated") contexts.push(msg.params.context);
    if (msg.id && pending.has(msg.id)) {
      const { resolve, reject } = pending.get(msg.id);
      pending.delete(msg.id);
      if (msg.error) reject(new Error(msg.error.message));
      else resolve(msg.result);
    }
  });
  return new Promise((resolve, reject) => {
    ws.addEventListener("open", () => resolve({
      ws,
      contexts,
      send(method, params) {
        const id = ++seq;
        return new Promise((res, rej) => {
          pending.set(id, { resolve: res, reject: rej });
          ws.send(JSON.stringify({ id, method, params }));
        });
      }
    }));
    ws.addEventListener("error", () => reject(new Error("websocket failed")));
  });
}

async function pageEval(client, expression) {
  const result = await client.send("Runtime.evaluate", {
    expression,
    awaitPromise: true,
    returnByValue: true
  });
  if (result.exceptionDetails) throw new Error(JSON.stringify(result.exceptionDetails).slice(0, 400));
  return result.result.value;
}

async function extensionEval(client, expression) {
  const matches = client.contexts.filter((c) => c.origin && c.origin.indexOf("chrome-extension://") === 0);
  const ctx = matches[matches.length - 1];
  if (!ctx) throw new Error("extension context was not found");
  const result = await client.send("Runtime.evaluate", {
    expression,
    awaitPromise: true,
    returnByValue: true,
    contextId: ctx.id
  });
  if (result.exceptionDetails) throw new Error("extension " + JSON.stringify(result.exceptionDetails).slice(0, 400));
  return result.result.value;
}

async function prepare(client) {
  await client.send("Runtime.enable");
  await client.send("Page.enable");
  await sleep(500);
  await pageEval(client, `(() => {
    const buttons = [...document.querySelectorAll("button, [role=button]")];
    const hit = buttons.find((b) => /accept all|i agree|agree|reject all|קבל הכל|אני מסכים/i.test(b.innerText || ""));
    if (hit) hit.click();
    const skip = document.querySelector(".ytp-skip-ad-button, .ytp-ad-skip-button, .ytp-ad-skip-button-modern");
    if (skip) skip.click();
    return true;
  })()`);
}

async function readPlayer(client) {
  return pageEval(client, `(() => {
    const v = document.querySelector("video.html5-main-video") || document.querySelector("video");
    const b = document.getElementById("youtube-surround-badge");
    return {
      href: location.href,
      title: document.title,
      badge: b ? b.textContent : "",
      hasVideo: !!v,
      muted: v ? v.muted : null,
      paused: v ? v.paused : null,
      time: v ? v.currentTime : null,
      rate: v ? v.playbackRate : null,
      ad: !!document.querySelector(".ad-showing, .ad-interrupting")
    };
  })()`);
}

async function waitPlayer(client, pred, ms, label) {
  const start = Date.now();
  let last = null;
  while (Date.now() - start < ms) {
    await prepare(client);
    last = await readPlayer(client);
    process.stdout.write("PAGE " + JSON.stringify(last) + "\n");
    if (pred(last)) return last;
    await sleep(1000);
  }
  throw new Error("timeout " + label + " last=" + JSON.stringify(last));
}

function logSize() {
  return fs.existsSync(logPath) ? fs.statSync(logPath).size : 0;
}

function logSince(size, text) {
  if (!fs.existsSync(logPath)) return false;
  const end = fs.statSync(logPath).size;
  if (end <= size) return false;
  const fd = fs.openSync(logPath, "r");
  const buf = Buffer.alloc(end - size);
  fs.readSync(fd, buf, 0, buf.length, size);
  fs.closeSync(fd);
  return buf.toString("utf8").indexOf(text) >= 0;
}

(async () => {
  const started = new Date().toISOString();
  fs.appendFileSync(logPath, "\r\nBRAVE_TEST " + started + "\r\n");
  const tab = await openTab(iamf);
  if (tab.id) await fetch("http://127.0.0.1:" + port + "/json/activate/" + tab.id);
  const client = await connect(tab.webSocketDebuggerUrl);
  await client.send("Page.bringToFront").catch(() => {});
  let prefs = null;
  for (let i = 0; i < 15 && !prefs; i++) {
    await prepare(client);
    try {
      prefs = await extensionEval(client, `chrome.runtime.sendMessage({type:"setPrefs", mode:"automatic", enabled:true})`);
    } catch (err) {
      await sleep(400);
    }
  }
  if (!prefs || prefs.mode !== "automatic") throw new Error("could not switch to automatic");
  await client.send("Page.reload", { ignoreCache: true });
  const active = await waitPlayer(client, (p) => p.badge === "5.1 Active" && p.muted === true && p.hasVideo, 120000, "5.1 active");
  if (!active.ad) {
    const beforePlay = logSize();
    await pageEval(client, `(() => { const v = document.querySelector("video"); v.currentTime = 30; v.playbackRate = 1; return v.play(); })()`);
    await sleep(1500);
    const playing = await readPlayer(client);
    if (playing.paused) throw new Error("video stayed paused");
    if (!logSince(beforePlay, "paused=False") && !logSince(beforePlay, "paused=false")) {
      throw new Error("play was not seen by the host");
    }
    const beforePause = logSize();
    await pageEval(client, `(() => { document.querySelector("video").pause(); return true; })()`);
    await sleep(800);
    if (!logSince(beforePause, "paused=True") && !logSince(beforePause, "paused=true")) {
      throw new Error("pause was not seen by the host");
    }
    const beforeSeek = logSize();
    await pageEval(client, `(() => { const v = document.querySelector("video"); v.currentTime = 80; v.playbackRate = 1.25; return v.play(); })()`);
    await sleep(1500);
    if (!logSince(beforeSeek, "clock seek")) throw new Error("seek was not seen by the host");
    if (!logSince(beforeSeek, "rate=1.25")) throw new Error("rate change was not seen by the host");
    await pageEval(client, `(() => { document.querySelector("video").playbackRate = 1; return true; })()`);
  }
  await client.send("Page.navigate", { url: stereo });
  const stereoPage = await waitPlayer(client, (p) => p.href.indexOf("jNQXAC9IVRw") >= 0 && (p.badge === "Stereo source" || p.badge === "Surround error" || p.badge === "Surround unavailable"), 90000, "stereo");
  if (stereoPage.badge !== "Stereo source") throw new Error("stereo video badge " + stereoPage.badge);
  if (stereoPage.muted === true && stereoPage.hasVideo) {
    const forced = await pageEval(client, `(() => {
      const v = document.querySelector("video");
      v.muted = false;
      return new Promise((r) => setTimeout(() => r(v.muted), 600));
    })()`);
    if (forced === true) throw new Error("stereo video was forced mute");
  }
  await client.send("Page.navigate", { url: iamf });
  await waitPlayer(client, (p) => p.href.indexOf("iMrnS6finbA") >= 0 && p.badge === "5.1 Active" && p.muted === true, 120000, "return to iamf");
  const manual = await extensionEval(client, `chrome.runtime.sendMessage({type:"setPrefs", mode:"manual"})`);
  process.stdout.write("MANUAL " + JSON.stringify(manual) + "\n");
  await client.send("Page.navigate", { url: stereo });
  const manualPage = await waitPlayer(client, (p) => p.href.indexOf("jNQXAC9IVRw") >= 0 && p.badge !== "Checking source" && p.badge !== "Decoding", 20000, "manual switch");
  if (manualPage.badge === "5.1 Active") throw new Error("manual mode started surround by itself");
  console.log("RESULT=BRAVE_OK");
  client.ws.close();
  process.exit(0);
})().catch((err) => {
  console.error(err && err.stack ? err.stack : err);
  process.exit(1);
});
