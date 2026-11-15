const port = process.env.YS_DEBUG_PORT || "9333";
function sleep(ms) { return new Promise((r) => setTimeout(r, ms)); }

async function openTab(url) {
  let res = await fetch("http://127.0.0.1:" + port + "/json/new?" + encodeURIComponent(url), { method: "PUT" });
  if (!res.ok) res = await fetch("http://127.0.0.1:" + port + "/json/new?" + url);
  if (!res.ok) throw new Error("open tab " + res.status);
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
  const tab = await openTab("https://www.youtube.com/watch?v=iMrnS6finbA");
  const client = await connect(tab.webSocketDebuggerUrl);
  await client.send("Runtime.enable");
  await client.send("Page.enable");
  await sleep(6000);
  const ext = client.contexts.filter((c) => c.origin && c.origin.indexOf("chrome-extension://") === 0);
  process.stdout.write("EXT_CONTEXTS " + ext.map((c) => c.origin).join(",") + "\n");
  for (const ctx of ext) {
    const result = await client.send("Runtime.evaluate", {
      contextId: ctx.id,
      awaitPromise: true,
      returnByValue: true,
      expression: `chrome.runtime.sendMessage({type:"getStatus"}).then(s => ({id: chrome.runtime.id, status: s})).catch(e => ({id: chrome.runtime.id, error: String(e)}))`
    });
    process.stdout.write("EXT " + JSON.stringify(result.result && result.result.value) + "\n");
  }
  const page = await client.send("Runtime.evaluate", {
    returnByValue: true,
    expression: `({badge:(document.getElementById("youtube-surround-badge")||{}).textContent||"", title:document.title})`
  });
  process.stdout.write("PAGE " + JSON.stringify(page.result && page.result.value) + "\n");
  process.exit(0);
})().catch((err) => {
  console.error(err.stack || err);
  process.exit(1);
});
