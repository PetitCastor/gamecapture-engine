// Control-API client (TASK-UI-05 section 2, WebSocket auth per TASK-UI-07).
//
// window.__GC_TOKEN and window.__GC_PORT are injected by MainWindow before navigation
// (AddScriptToExecuteOnDocumentCreatedAsync) — never read from the URL, never logged, never put in
// localStorage. Every REST call carries it as `Authorization: Bearer <token>`; the WebSocket upgrade
// cannot set that header from a browser, so it proves the same token via a `bearer.<token>`
// Sec-WebSocket-Protocol entry instead (see ControlApi.cs's TryMatchBearerSubProtocol).

const token = window.__GC_TOKEN;
const port = window.__GC_PORT;
const httpBase = `http://127.0.0.1:${port}`;
const wsUrl = `ws://127.0.0.1:${port}/api/events`;

async function request(path, options) {
  const response = await fetch(`${httpBase}${path}`, {
    ...options,
    headers: {
      ...(options && options.headers),
      Authorization: `Bearer ${token}`,
    },
  });

  if (response.status === 204) return null;

  if (!response.ok) {
    let message = `request to ${path} failed (${response.status})`;
    try {
      const body = await response.json();
      if (body && typeof body.error === "string" && body.error.length > 0) message = body.error;
    } catch {
      // Non-JSON or empty error body; keep the generic message above.
    }
    throw new Error(message);
  }

  const text = await response.text();
  return text.length > 0 ? JSON.parse(text) : null;
}

function get(path) {
  return request(path, { method: "GET" });
}

function post(path, body) {
  return request(path, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
}

export const api = {
  getStatus: () => get("/api/status"),
  getMonitors: () => get("/api/monitors"),
  getPlugins: () => get("/api/plugins"),
  getPluginLogs: (id, after) => get(`/api/plugins/${encodeURIComponent(id)}/logs?after=${after}`),
  getSettings: () => get("/api/settings"),
  saveSettings: (patch) => post("/api/settings", patch),
  browseFolder: (initialDirectory) => post("/api/settings/browse", { initialDirectory }),
  installPlugin: (id) => post(`/api/plugins/${encodeURIComponent(id)}/install`),
  updatePlugin: (id) => post(`/api/plugins/${encodeURIComponent(id)}/update`),
  uninstallPlugin: (id) => post(`/api/plugins/${encodeURIComponent(id)}/uninstall`),
  startPlugin: (id) => post(`/api/plugins/${encodeURIComponent(id)}/start`),
  stopPlugin: (id) => post(`/api/plugins/${encodeURIComponent(id)}/stop`),
  setPluginAutoStart: (id, enabled) => post(`/api/plugins/${encodeURIComponent(id)}/autostart`, { enabled }),
  setRoiOverlay: (id, visible) => post(`/api/plugins/${encodeURIComponent(id)}/roi-overlay`, { visible }),
  setIncludePreviews: (includePreviews) => post("/api/plugins/settings", { includePreviews }),
  exit: () => post("/api/exit"),
};

const INITIAL_RETRY_DELAY_MS = 1000;
const MAX_RETRY_DELAY_MS = 15000;

/**
 * Subscribes to `WS /api/events`, reconnecting with exponential backoff on any drop — a rejected
 * handshake (TASK-UI-07: no/invalid subprotocol) and a normal disconnect both land on the same
 * `close` event, so both take the same reconnect path. `handlers.onReconnecting` fires the instant a
 * connection is lost or a connect attempt fails, before the retry timer starts, so the header can
 * show a muted state immediately rather than a stale-but-confident one.
 *
 * Returns `{ close() }` to stop reconnecting for good (used on page teardown; the WebView2 page
 * itself never navigates away in normal use, so this mostly exists for completeness/tests).
 */
export function connectEvents(handlers) {
  let closedByCaller = false;
  let retryDelay = INITIAL_RETRY_DELAY_MS;
  let socket = null;

  function connect() {
    if (closedByCaller) return;

    socket = new WebSocket(wsUrl, [`bearer.${token}`]);

    socket.addEventListener("open", () => {
      retryDelay = INITIAL_RETRY_DELAY_MS;
      handlers.onConnected?.();
    });

    socket.addEventListener("message", (event) => {
      let message;
      try {
        message = JSON.parse(event.data);
      } catch {
        return; // Malformed frame; wait for the next one rather than crash the page.
      }

      if (message.type === "status") handlers.onStatus?.(message.data);
      else if (message.type === "plugins") handlers.onPlugins?.(message.data);
    });

    socket.addEventListener("close", () => {
      if (closedByCaller) return;
      handlers.onReconnecting?.();
      setTimeout(connect, retryDelay);
      retryDelay = Math.min(retryDelay * 2, MAX_RETRY_DELAY_MS);
    });

    // The close handler above always runs after error (per the WebSocket spec, a failed connection
    // fires error then close), so this only needs to fail the socket fast rather than duplicate the
    // reconnect logic.
    socket.addEventListener("error", () => socket.close());
  }

  connect();

  return {
    close() {
      closedByCaller = true;
      socket?.close();
    },
  };
}
