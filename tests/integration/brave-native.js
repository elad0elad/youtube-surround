const port = "9333";
function connect(wsUrl) {
  const ws = new WebSocket(wsUrl);
  let seq = 0;
  const pending = new Map();
  ws.addEventListener("message", (ev) => {
    const msg = JSON.parse(ev.data);
    if (msg.id && pending.has(msg.id)) {
      const item = pending.get(msg.id);
      pending.delete(msg.id);
      if (msg.error) item.reject(new Error(msg.error.message));
      else item.resolve(msg.result);
    }
  });
  return new Promise((resolve, reject) => {
    ws.addEventListener("open", () => resolve({
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
(async () => {
  const list = await fetch("http://127.0.0.1:" + port + "/json/list").then((r) => r.json());
  const sw = list.find((t) => (t.url || "").indexOf("background.js") >= 0);
  const client = await connect(sw.webSocketDebuggerUrl);
  await client.send("Runtime.enable");
  const file = await client.send("Runtime.evaluate", {
    awaitPromise: true,
    returnByValue: true,
    expression: `fetch(chrome.runtime.getURL("background.js")).then(r => r.text()).then(t => t.indexOf("component is not installed") >= 0 && t.indexOf("status.state === \\"active\\"") < 0)`
  });
  process.stdout.write("NEW_BACKGROUND " + JSON.stringify(file.result && file.result.value) + "\n");
  const native = await client.send("Runtime.evaluate", {
    awaitPromise: true,
    returnByValue: true,
    expression: `new Promise((resolve) => {
      let port;
      try { port = chrome.runtime.connectNative("com.youtubesurround.host"); }
      catch (e) { resolve({thrown: String(e)}); return; }
      const timer = setTimeout(() => resolve({timeout: true}), 4000);
      port.onMessage.addListener((message) => { clearTimeout(timer); resolve({message}); });
      port.onDisconnect.addListener(() => {
        clearTimeout(timer);
        resolve({disconnected: (chrome.runtime.lastError && chrome.runtime.lastError.message) || "no lastError"});
      });
    })`
  });
  process.stdout.write("NATIVE " + JSON.stringify(native.result && native.result.value) + "\n");
  process.exit(0);
})().catch((err) => { console.error(err.stack || err); process.exit(1); });
