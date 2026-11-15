const port = "9333";
function sleep(ms) { return new Promise((r) => setTimeout(r, ms)); }
function connect(wsUrl) {
  const ws = new WebSocket(wsUrl);
  let seq = 0;
  const pending = new Map();
  const contexts = [];
  ws.addEventListener("message", (ev) => {
    const msg = JSON.parse(ev.data);
    if (msg.method === "Runtime.executionContextCreated") contexts.push(msg.params.context);
    if (msg.id && pending.has(msg.id)) {
      const item = pending.get(msg.id);
      pending.delete(msg.id);
      if (msg.error) item.reject(new Error(msg.error.message));
      else item.resolve(msg.result);
    }
  });
  return new Promise((resolve, reject) => {
    ws.addEventListener("open", () => resolve({
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
(async () => {
  const list = await fetch("http://127.0.0.1:" + port + "/json/list").then((r) => r.json());
  const page = list.find((t) => (t.url || "").indexOf("iMrnS6finbA") >= 0);
  const sw = list.find((t) => (t.url || "").indexOf("background.js") >= 0);
  const swClient = await connect(sw.webSocketDebuggerUrl);
  await swClient.send("Runtime.enable");
  const script = await swClient.send("Runtime.evaluate", {
    awaitPromise: true,
    returnByValue: true,
    expression: `fetch(chrome.runtime.getURL("content.js")).then(r => r.text()).then(t => ({hasWatchSent: t.indexOf("watchSent") >= 0, len: t.length}))`
  });
  process.stdout.write("SCRIPT " + JSON.stringify(script.result.value) + "\n");
  const pageClient = await connect(page.webSocketDebuggerUrl);
  await pageClient.send("Runtime.enable");
  await sleep(300);
  const ctx = pageClient.contexts.filter((c) => c.origin && c.origin.indexOf("chrome-extension://") === 0).pop();
  if (!ctx) throw new Error("no extension context");
  const sent = await pageClient.send("Runtime.evaluate", {
    contextId: ctx.id,
    awaitPromise: true,
    returnByValue: true,
    expression: `chrome.runtime.sendMessage({type:"watch", force:true, url:location.href, videoId:"iMrnS6finbA", time:5, paused:true, rate:1, ad:false})`
  });
  process.stdout.write("WATCH " + JSON.stringify(sent.result && (sent.result.value || sent.result.description || sent)) + "\n");
  await sleep(2000);
  const status = await pageClient.send("Runtime.evaluate", {
    contextId: ctx.id,
    awaitPromise: true,
    returnByValue: true,
    expression: `chrome.runtime.sendMessage({type:"getStatus"})`
  });
  process.stdout.write("STATUS " + JSON.stringify(status.result && status.result.value) + "\n");
  process.exit(0);
})().catch((err) => { console.error(err.stack || err); process.exit(1); });
