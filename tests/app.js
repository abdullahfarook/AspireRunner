const express = require("express");

const app = express();
const port = Number(process.env.PORT || 3000);

app.get("/", (_req, res) => {
  res.json({
    service: "node-express-test-app",
    status: "ok",
    port,
    timestamp: new Date().toISOString(),
  });
});

app.get("/health", (_req, res) => {
  res.status(200).send("healthy");
});

app.get("/crash", (_req, res) => {
  res.status(500).send("intentional crash");
  setTimeout(() => process.exit(1), 100);
});

const server = app.listen(port, "127.0.0.1", () => {
  console.log(`Node Express app listening on http://127.0.0.1:${port}`);
});

let tick = 0;
const heartbeat = setInterval(() => {
  tick += 1;
  console.log(`node-heartbeat-${tick}`);

  if (tick % 3 === 0) {
    console.error(`node-stderr-heartbeat-${tick}`);
  }
}, 1500);

function shutdown(signal) {
  console.log(`received ${signal}, shutting down node app`);
  clearInterval(heartbeat);

  server.close((err) => {
    if (err) {
      console.error(`node app shutdown error: ${err.message}`);
      process.exit(1);
      return;
    }

    process.exit(0);
  });
}

process.on("SIGINT", () => shutdown("SIGINT"));
process.on("SIGTERM", () => shutdown("SIGTERM"));
