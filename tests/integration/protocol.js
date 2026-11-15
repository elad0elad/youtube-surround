// Speaks Chrome's native-messaging framing to the installed host.
const { spawn } = require("child_process");
const fs = require("fs");
const path = require("path");

const host = process.env.YS_HOST || path.join(process.env.LOCALAPPDATA, "YouTubeSurround", "YouTubeSurroundHost.exe");
const child = spawn(host, [], { stdio: ["pipe", "pipe", "pipe"] });
let buf = Buffer.alloc(0);
const messages = [];
let failed = null;

child.stderr.on("data", (d) => process.stderr.write(d));
child.on("exit", (code) => {
  if (!done) finish(new Error("host exited " + code));
});

function send(obj) {
  const body = Buffer.from(JSON.stringify(obj));
  const len = Buffer.alloc(4);
  len.writeUInt32LE(body.length, 0);
  child.stdin.write(Buffer.concat([len, body]));
}

child.stdout.on("data", (chunk) => {
  buf = Buffer.concat([buf, chunk]);
  while (buf.length >= 4) {
    const n = buf.readUInt32LE(0);
    if (buf.length < 4 + n) return;
    const msg = JSON.parse(buf.slice(4, 4 + n).toString("utf8"));
    buf = buf.slice(4 + n);
    messages.push(msg);
    process.stdout.write("STATUS " + msg.state + " " + (msg.detail || "") + " ch=" + msg.channelsOut + "\n");
  }
});

let seen = 0;
function waitFor(pred, ms) {
  const start = Date.now();
  return new Promise((resolve, reject) => {
    const timer = setInterval(() => {
      for (let i = seen; i < messages.length; i++) {
        if (!pred(messages[i])) continue;
        seen = i + 1;
        clearInterval(timer);
        resolve(messages[i]);
        return;
      }
      if (Date.now() - start > ms) {
        clearInterval(timer);
        reject(new Error("timeout waiting for status"));
      }
    }, 100);
  });
}

let done = false;
function finish(err) {
  if (done) return;
  done = true;
  try { child.stdin.end(); } catch (e) {}
  setTimeout(() => {
    try { child.kill(); } catch (e) {}
    if (err) {
      console.error(err.message);
      process.exit(1);
    }
    process.exit(0);
  }, 300);
}

(async () => {
  await waitFor((m) => m.state === "idle", 5000);
  send({ type: "start", videoId: "iMrnS6finbA", time: 310, paused: true, rate: 1, ad: false });
  const active = await waitFor((m) => m.state === "active" && m.channelsOut === 6, 90000);
  if (active.sourceCodec !== "iamf.001.001.Opus" || active.layout !== "5.1") {
    throw new Error("active status is missing measured 5.1 fields");
  }
  send({ type: "clock", videoId: "iMrnS6finbA", time: 310, paused: false, rate: 1 });
  await new Promise((r) => setTimeout(r, 2500));
  send({ type: "clock", videoId: "iMrnS6finbA", time: 20, paused: true, rate: 1 });
  await new Promise((r) => setTimeout(r, 400));
  send({ type: "stop" });
  const idle = await waitFor((m) => m.state === "idle" && m.channelsOut === 0, 5000);
  if (idle.layout) throw new Error("stop still reports a layout");
  const leftovers = fs.readdirSync(process.env.TEMP).filter((name) => name.startsWith("ys-") && name.endsWith(".wav") && name !== "ys-tones.wav");
  if (leftovers.length) throw new Error("temp wav left behind: " + leftovers.join(","));
  send({ type: "start", videoId: "jNQXAC9IVRw", time: 1, paused: true, rate: 1, ad: false });
  const stereo = await waitFor((m) => m.state === "stereo", 60000);
  if (stereo.channelsOut !== 0 || stereo.layout) throw new Error("stereo video was labeled as surround");
  console.log("RESULT=PROTOCOL_OK");
  finish();
})().catch((err) => finish(err));
