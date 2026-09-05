// Entry point (TASK-UI-05). Wires the static markup in index.html to the control API: real status,
// real plugin rows, real settings, live theme. No framework, no build step — plain DOM.

import { api, connectEvents } from "./api.js";
import { readStoredTheme, storeTheme, applyTheme, watchSystemTheme } from "./theme.js";

// ---------- Theme ----------
// Section 6: localStorage is only a fast-path for the first paint; the engine's persisted `theme`
// (from GET /api/settings) is the source of truth once it arrives, and callers never format this
// value themselves - it is stamped straight onto the root dataset.

const themeButtons = document.querySelectorAll("#theme-toggle button");
let currentTheme = readStoredTheme();
applyTheme(currentTheme);
paintThemeButtons(currentTheme);

function paintThemeButtons(choice) {
  themeButtons.forEach((button) => {
    button.setAttribute("aria-pressed", String(button.dataset.themeChoice === choice));
  });
}

function setTheme(choice) {
  currentTheme = choice;
  applyTheme(choice);
  storeTheme(choice);
  paintThemeButtons(choice);
}

themeButtons.forEach((button) => {
  button.addEventListener("click", async () => {
    const choice = button.dataset.themeChoice;
    if (choice === currentTheme) return;

    setTheme(choice); // instant, live, no restart notice (section 5's last bullet)
    try {
      // Persisted server-side so it survives a restart and the native caption bar can follow
      // (MainWindow.ApplyThemeSetting); the page's own colours are already correct regardless of
      // whether this save succeeds.
      await api.saveSettings({ theme: choice });
    } catch {
      // Best-effort: the chosen theme still renders correctly for this session even if it could
      // not be persisted (e.g. the config file is locked); nothing more to surface to the user
      // over what is, visually, already a successful change.
    }
  });
});

watchSystemTheme(() => {
  if (currentTheme === "system") applyTheme("system");
});

// ---------- Header: status ----------

const STATUS_COLOR_VAR = {
  capturing: "--ok",
  idle: "--text-dim",
  replay: "--accent-2",
  error: "--danger",
};

const statusPill = document.getElementById("status-pill");
const statusDot = document.getElementById("status-dot");
const statusLabel = document.getElementById("status-label");
const appVersion = document.getElementById("app-version");

// Section 3: TrayView fields are already formatted display strings - iconState and engineVersion
// are the only two with a home in the trimmed header, rendered directly with no formatting logic
// here. Mode/Frame/OcrLanguage/Fps/Metrics are deliberately not read at all.
function renderStatus(view) {
  statusLabel.textContent = view.iconState;
  statusDot.style.background = `var(${STATUS_COLOR_VAR[view.iconState] ?? "--text-dim"})`;
  appVersion.textContent = `v${view.engineVersion}`;
}

function setReconnecting(reconnecting) {
  statusPill.classList.toggle("is-muted", reconnecting);
  if (reconnecting) {
    statusLabel.textContent = "Reconnecting…";
    statusDot.style.animation = "none";
  } else {
    statusDot.style.animation = "";
  }
}

// ---------- Plugins pane ----------

const pluginList = document.getElementById("plugin-list");
const previewBuildsCheckbox = document.getElementById("preview-builds");
const previewError = document.getElementById("preview-error");

let pluginRows = [];
let selectedPluginId = null;
const busyPluginIds = new Set();
const pluginRowErrors = new Map();
const PLUGIN_LOG_POLL_MS = 1000;
const MAX_RENDERED_PLUGIN_LOG_LINES = 1000;
let pluginLogView = null;
let pluginLogPollTimer = null;
let pluginLogPollInFlight = false;

function pluginGlyphMarkup(row) {
  // Mirrors the four states the approved mock illustrated: an in-flight update takes visual
  // priority over "running", matching PluginRow's own state precedence (see PluginRowBuilder).
  if (row.state === "updateAvailable") {
    return '<svg viewBox="0 0 10 10" width="10" height="10" fill="none" style="stroke:var(--accent-2)" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round"><path d="M5 8.5V1.5M2 4.3 5 1.5l3 2.8"/></svg>';
  }
  if (row.isRunning) {
    return '<svg viewBox="0 0 10 10" width="10" height="10"><circle cx="5" cy="5" r="5" style="fill:var(--ok)"/></svg>';
  }
  if (row.state === "installed") {
    return '<svg viewBox="0 0 10 10" width="10" height="10"><circle cx="5" cy="5" r="3.5" fill="none" style="stroke:var(--text-dim)" stroke-width="1.6"/></svg>';
  }
  return '<svg viewBox="0 0 10 10" width="10" height="10" style="stroke:var(--text-dim)" stroke-width="1.6" stroke-linecap="round"><path d="M5 1.5v7M1.5 5h7"/></svg>';
}

function pluginStateClass(row) {
  if (row.state === "updateAvailable") return "state-update";
  if (row.isRunning) return "state-connected";
  return "state-dim";
}

// No per-tick counter exists on PluginRow (the wire shape has no field for it), so the row-sub
// line drops the mock's illustrative "· N ticks" fragment rather than inventing one.
function pluginRowSub(row) {
  return row.installedVersion ? `<span class="mono">${escapeHtml(row.installedVersion)}</span>` : "from catalog";
}

function pluginDetailVersion(row) {
  if (!row.installedVersion) return "";
  if (row.state === "updateAvailable" && row.latestVersion) return `${row.installedVersion} → ${row.latestVersion}`;
  return row.installedVersion;
}

// Exactly one primary ("forward") action per row, matching every example in the approved mock (an
// install/update action and a launch action never appear together): Install/Update takes the primary
// slot whenever it applies, Start otherwise. Stop/Remove are always plain buttons when their flag
// allows them. CanReinstall has no button here - like the trimmed header's unused TrayView fields,
// it has no home in the approved design, so it is left unrendered rather than inventing one.
function pluginActionButtons(row) {
  const buttons = [];
  const isForwardAction = row.state === "notInstalled" || row.state === "updateAvailable";

  if (isForwardAction) {
    buttons.push({
      action: row.state === "updateAvailable" ? "update" : "install",
      label: row.installActionText,
      primary: true,
      enabled: row.canInstall,
    });
  } else if (row.canLaunch) {
    buttons.push({ action: "start", label: "Start", primary: true, enabled: true });
  }

  if (row.canStop) buttons.push({ action: "stop", label: "Stop", primary: false, enabled: true });
  if (row.canShowRois) {
    buttons.push({
      action: "roiOverlay",
      label: row.isRoiOverlayVisible ? "Hide active ROIs" : "Show active ROIs",
      primary: false,
      enabled: true,
    });
  }
  if (row.hasLogs) buttons.push({ action: "logs", label: "Show logs", primary: false, enabled: true });
  if (row.canRemove) buttons.push({ action: "uninstall", label: "Remove", primary: false, enabled: true });

  return buttons;
}

const BUSY_LABEL = {
  install: "Installing…",
  update: "Updating…",
  uninstall: "Removing…",
  start: "Starting…",
  stop: "Stopping…",
  roiOverlay: "Updating ROI overlay…",
};

function renderPluginList(rows) {
  pluginRows = rows;
  if (selectedPluginId && !rows.some((row) => row.id === selectedPluginId)) {
    selectedPluginId = null;
  }
  if (pluginLogView && !rows.some((row) => row.id === pluginLogView.id)) {
    stopPluginLogPolling();
    pluginLogView = null;
  }

  pluginList.innerHTML = rows.map(renderPluginRow).join("");

  pluginList.querySelectorAll(".plugin-row").forEach((rowEl) => {
    rowEl.addEventListener("click", () => selectPlugin(rowEl.dataset.plugin));
  });
  pluginList.querySelectorAll("[data-plugin-action]").forEach((button) => {
    button.addEventListener("click", (event) => {
      event.stopPropagation();
      if (button.dataset.pluginAction === "logs") togglePluginLogs(button.dataset.pluginId);
      else runPluginAction(button.dataset.pluginId, button.dataset.pluginAction);
    });
  });
  pluginList.querySelectorAll("[data-plugin-autostart]").forEach((checkbox) => {
    // click, not change: the checkbox lives inside the row's own click handler, and letting that
    // bubble would collapse the detail pane out from under the box the user just ticked.
    checkbox.addEventListener("click", (event) => event.stopPropagation());
    checkbox.addEventListener("change", () => setPluginAutoStart(checkbox, checkbox.dataset.pluginId, checkbox.checked));
  });
  pluginList.querySelectorAll("[data-close-plugin-logs]").forEach((button) => {
    button.addEventListener("click", (event) => {
      event.stopPropagation();
      closePluginLogs();
    });
  });
}

function renderPluginRow(row) {
  const expanded = row.id === selectedPluginId;
  const busy = busyPluginIds.has(row.id);
  const error = pluginRowErrors.get(row.id);

  const buttonsHtml = pluginActionButtons(row)
    .map((button) => {
      const label = busy ? (BUSY_LABEL[button.action] ?? button.label) : button.label;
      const disabled = busy || !button.enabled;
      return `<button type="button" class="btn${button.primary ? " btn-primary" : ""}"
        data-plugin-id="${escapeHtml(row.id)}" data-plugin-action="${button.action}"
        ${disabled ? "disabled" : ""}>${escapeHtml(label)}</button>`;
    })
    .join("");

  const description = row.entry?.description ? `<p>${escapeHtml(row.entry.description)}</p>` : "";
  const versionText = pluginDetailVersion(row);
  const versionSpan = versionText ? `<span class="detail-version">${escapeHtml(versionText)}</span>` : "";
  const errorHtml = error ? `<p class="row-error">${escapeHtml(error)}</p>` : "";
  const logsHtml = pluginLogView?.id === row.id ? renderPluginLogs() : "";
  const autoStartHtml = row.canSetAutoStart
    ? `<label class="checkbox-row plugin-autostart">
        <input type="checkbox" data-plugin-autostart data-plugin-id="${escapeHtml(row.id)}"
          ${row.autoStart ? "checked" : ""} ${busy ? "disabled" : ""}>
        Start automatically with the engine
      </label>`
    : "";

  return `
    <li>
      <button type="button" class="plugin-row" data-plugin="${escapeHtml(row.id)}" aria-expanded="${expanded}">
        <span class="glyph" aria-hidden="true">${pluginGlyphMarkup(row)}</span>
        <span class="row-main">
          <span class="row-name">${escapeHtml(row.name)}</span>
          <span class="row-sub">${pluginRowSub(row)}</span>
        </span>
        <span class="row-state ${pluginStateClass(row)}">${escapeHtml(row.stateText)}</span>
      </button>
      <div class="plugin-detail" id="detail-${escapeHtml(row.id)}" ${expanded ? "" : "hidden"}>
        <h3>${escapeHtml(row.name)}${versionSpan}</h3>
        ${description}
        <div class="action-row">${buttonsHtml}</div>
        ${autoStartHtml}
        ${logsHtml}
        ${errorHtml}
      </div>
    </li>`;
}

function selectPlugin(id) {
  selectedPluginId = selectedPluginId === id ? null : id;
  if (pluginLogView && pluginLogView.id !== selectedPluginId) {
    stopPluginLogPolling();
    pluginLogView = null;
  }
  renderPluginList(pluginRows);
}

function renderPluginLogs() {
  const view = pluginLogView;
  const status = view.error
    ? `<p class="plugin-log-status is-error">${escapeHtml(view.error)}</p>`
    : view.loading
      ? '<p class="plugin-log-status">Loading captured output…</p>'
      : view.lines.length === 0
        ? '<p class="plugin-log-status">No captured output yet.</p>'
        : "";
  const truncation = view.truncated
    ? '<p class="plugin-log-status is-warning">Some older captured output was discarded.</p>'
    : "";
  const lines = view.lines.map((line) =>
    `<span class="plugin-log-line stream-${escapeHtml(line.stream)}"><span class="plugin-log-stream">${escapeHtml(line.stream)}</span>${escapeHtml(line.text)}</span>`).join("\n");

  return `
    <section class="plugin-logs" aria-label="Captured plugin output">
      <div class="plugin-logs-header">
        <h4>Captured output</h4>
        <button type="button" class="btn plugin-logs-close" data-close-plugin-logs>Close</button>
      </div>
      ${truncation}
      ${status}
      <pre class="plugin-log-output" aria-live="polite">${lines}</pre>
    </section>`;
}

async function togglePluginLogs(id) {
  if (pluginLogView?.id === id) {
    closePluginLogs();
    return;
  }

  stopPluginLogPolling();
  pluginLogView = { id, lines: [], nextSequence: -1, truncated: false, loading: true, error: "" };
  renderPluginList(pluginRows);
  await refreshPluginLogs();

  if (pluginLogView?.id === id) {
    pluginLogPollTimer = setInterval(refreshPluginLogs, PLUGIN_LOG_POLL_MS);
  }
}

function closePluginLogs() {
  stopPluginLogPolling();
  pluginLogView = null;
  renderPluginList(pluginRows);
}

function stopPluginLogPolling() {
  if (pluginLogPollTimer !== null) clearInterval(pluginLogPollTimer);
  pluginLogPollTimer = null;
}

async function refreshPluginLogs() {
  const view = pluginLogView;
  if (!view || pluginLogPollInFlight) return;

  pluginLogPollInFlight = true;
  try {
    const page = await api.getPluginLogs(view.id, view.nextSequence);
    if (pluginLogView !== view) return;

    view.lines = [...view.lines, ...page.lines].slice(-MAX_RENDERED_PLUGIN_LOG_LINES);
    view.nextSequence = page.nextSequence;
    view.truncated ||= page.truncated;
    view.loading = false;
    view.error = "";
  } catch (err) {
    if (pluginLogView !== view) return;
    view.loading = false;
    view.error = err.message;
  } finally {
    pluginLogPollInFlight = false;
    if (pluginLogView === view && !updatePluginLogsSection()) renderPluginList(pluginRows);
  }
}

// Patches only the log section's DOM instead of renderPluginList's full rebuild,
// so the <pre> element survives each 1s poll and keeps its scroll position.
function updatePluginLogsSection() {
  const section = pluginList.querySelector(".plugin-logs");
  if (!section) return false;

  const oldPre = section.querySelector(".plugin-log-output");
  const wasAtBottom = oldPre ? oldPre.scrollHeight - oldPre.scrollTop - oldPre.clientHeight < 4 : true;
  const prevScrollTop = oldPre ? oldPre.scrollTop : 0;

  section.outerHTML = renderPluginLogs();

  const newSection = pluginList.querySelector(".plugin-logs");
  const newPre = newSection?.querySelector(".plugin-log-output");
  if (newPre) newPre.scrollTop = wasAtBottom ? newPre.scrollHeight : prevScrollTop;
  newSection?.querySelector("[data-close-plugin-logs]")?.addEventListener("click", (event) => {
    event.stopPropagation();
    closePluginLogs();
  });
  return true;
}

async function runPluginAction(id, action) {
  busyPluginIds.add(id);
  pluginRowErrors.delete(id);
  renderPluginList(pluginRows);

  try {
    const actionsById = {
      install: api.installPlugin,
      update: api.updatePlugin,
      uninstall: api.uninstallPlugin,
      start: api.startPlugin,
      stop: api.stopPlugin,
      roiOverlay: (pluginId) => api.setRoiOverlay(
        pluginId,
        !pluginRows.find((row) => row.id === pluginId)?.isRoiOverlayVisible),
    };
    await actionsById[action](id);
    // The socket normally pushes this change, but it may still be connecting or reconnecting when an
    // action finishes. Re-read here so the UI converges even when that one push was missed.
    renderPluginList(await api.getPlugins());
  } catch (err) {
    pluginRowErrors.set(id, err.message);
  } finally {
    busyPluginIds.delete(id);
    renderPluginList(pluginRows);
  }
}

// Not routed through runPluginAction: nothing here touches the install folder or the running set, so
// there is no busy state to show and no row refresh to wait for — the local row is patched and
// re-rendered, which is the whole visible effect of the call.
async function setPluginAutoStart(checkbox, id, enabled) {
  pluginRowErrors.delete(id);
  // Disabled for the duration, like the preview-builds checkbox: without it a double-click puts two
  // writes in flight and the page keeps whichever response happens to land last, which need not be
  // the last thing the user clicked.
  checkbox.disabled = true;
  try {
    const result = await api.setPluginAutoStart(id, enabled);
    const row = pluginRows.find((candidate) => candidate.id === id);
    if (row) row.autoStart = result?.autoStart ?? enabled;
  } catch (err) {
    pluginRowErrors.set(id, err.message); // the row keeps its last persisted value on re-render
  } finally {
    renderPluginList(pluginRows); // rebuilds the checkbox, so the disabled flag above goes with it
  }
}

previewBuildsCheckbox.addEventListener("change", async () => {
  const checked = previewBuildsCheckbox.checked;
  previewBuildsCheckbox.disabled = true;
  previewError.hidden = true;
  try {
    const rows = await api.setIncludePreviews(checked);
    renderPluginList(rows);
  } catch (err) {
    previewBuildsCheckbox.checked = !checked; // revert to the last known-persisted value
    previewError.textContent = err.message;
    previewError.hidden = false;
  } finally {
    previewBuildsCheckbox.disabled = false;
  }
});

function escapeHtml(value) {
  return String(value)
    .replaceAll("&", "&amp;")
    .replaceAll("<", "&lt;")
    .replaceAll(">", "&gt;")
    .replaceAll('"', "&quot;");
}

// ---------- Settings pane ----------

const monitorSelect = document.getElementById("set-monitor");
const ocrSelect = document.getElementById("set-ocr");
const intervalInput = document.getElementById("set-interval");
const hotkeyInput = document.getElementById("set-hotkey");
const outdirInput = document.getElementById("set-outdir");
const browseButton = document.getElementById("browse-outdir");
const applyButton = document.getElementById("apply-restart");
const settingsStatus = document.getElementById("settings-status");

const AUTO_OCR_VALUE = "";
const AUTO_OCR_LABEL = "(auto — first installed)";

let loadedSettings = null;
// Populated once from the initial GET /api/settings and unaffected by a later POST's
// {settings, restartPending} response (which carries neither) - so renderSettingsPane can rebuild
// the whole pane from just a settings object, on both the initial load and every subsequent apply.
let availableMonitors = [];
let availableOcrLanguages = [];

function renderSettingsPane(settings) {
  loadedSettings = settings;

  monitorSelect.innerHTML = availableMonitors
    .map((label, index) => `<option value="${index}">${escapeHtml(label)}</option>`)
    .join("");
  monitorSelect.value = String(loadedSettings.monitorIndex);

  const ocrOptions = [AUTO_OCR_VALUE, ...availableOcrLanguages];
  // A tag persisted earlier whose pack is no longer installed must still round-trip rather than
  // silently reset to auto (matches the deleted SettingsForm's own rule).
  if (loadedSettings.ocrLanguage && !ocrOptions.includes(loadedSettings.ocrLanguage)) {
    ocrOptions.push(loadedSettings.ocrLanguage);
  }
  ocrSelect.innerHTML = ocrOptions
    .map((tag) => `<option value="${escapeHtml(tag)}">${tag === AUTO_OCR_VALUE ? AUTO_OCR_LABEL : escapeHtml(tag)}</option>`)
    .join("");
  ocrSelect.value = loadedSettings.ocrLanguage ?? AUTO_OCR_VALUE;

  intervalInput.value = String(loadedSettings.scanIntervalMs);
  hotkeyInput.value = loadedSettings.hotkey;
  outdirInput.value = loadedSettings.outputDir;

  applyButton.disabled = true; // freshly loaded values are never dirty against themselves
}

function currentSettingsPatch() {
  return {
    monitorIndex: Number(monitorSelect.value),
    ocrLanguage: ocrSelect.value,
    scanIntervalMs: Number(intervalInput.value),
    hotkey: hotkeyInput.value,
    outputDir: outdirInput.value,
  };
}

function isDirty() {
  if (!loadedSettings) return false;
  const patch = currentSettingsPatch();
  return (
    patch.monitorIndex !== loadedSettings.monitorIndex ||
    patch.ocrLanguage !== (loadedSettings.ocrLanguage ?? AUTO_OCR_VALUE) ||
    patch.scanIntervalMs !== loadedSettings.scanIntervalMs ||
    patch.hotkey !== loadedSettings.hotkey ||
    patch.outputDir !== loadedSettings.outputDir
  );
}

let settingsRequestInFlight = false;

function updateDirtyState() {
  applyButton.disabled = settingsRequestInFlight || !isDirty();
}

[monitorSelect, ocrSelect, intervalInput, hotkeyInput, outdirInput].forEach((el) => {
  el.addEventListener("input", updateDirtyState);
  el.addEventListener("change", updateDirtyState);
});

function showSettingsStatus(text, isError) {
  settingsStatus.textContent = text;
  settingsStatus.classList.toggle("is-error", isError);
  settingsStatus.hidden = text.length === 0;
}

browseButton.addEventListener("click", async () => {
  browseButton.disabled = true;
  try {
    const result = await api.browseFolder(outdirInput.value);
    if (result && result.path) {
      outdirInput.value = result.path;
      updateDirtyState();
    }
  } catch (err) {
    showSettingsStatus(err.message, true);
  } finally {
    browseButton.disabled = false;
  }
});

applyButton.addEventListener("click", async () => {
  // Independent of isDirty(): a field edited while this request is still in flight must not
  // re-enable the button and let a second POST /api/settings race the first one (whichever
  // response lands second wins the repaint, which can show a stale value even though the server
  // itself serializes the writes correctly).
  settingsRequestInFlight = true;
  applyButton.disabled = true;
  showSettingsStatus("", false);
  try {
    const result = await api.saveSettings(currentSettingsPatch());
    // Re-render from the response, never from what was typed - the server may have silently
    // corrected an unavailable OCR pack or an unparseable hotkey (section 5).
    renderSettingsPane(result.settings);
    if (result.restartPending) {
      showSettingsStatus("Restarting the engine to apply the change…", false);
      // The engine relaunches and this page's WebSocket drops; api.js's own reconnect-with-backoff
      // covers the window coming back (section 5 / manual check 3).
    }
  } catch (err) {
    showSettingsStatus(err.message, true);
  } finally {
    settingsRequestInFlight = false;
    updateDirtyState();
  }
});

// ---------- Initial load ----------

async function init() {
  try {
    renderStatus(await api.getStatus());
  } catch {
    // The WebSocket's greeting message (sent immediately on connect, see ControlApiEventHub.RunAsync)
    // backfills this within moments; nothing more to do for a transient failure here.
  }

  try {
    renderPluginList(await api.getPlugins());
  } catch {
    // Leaves the list empty; the "plugins" WebSocket push or a later manual reload can recover.
  }

  try {
    const payload = await api.getSettings();
    availableMonitors = payload.monitors;
    availableOcrLanguages = payload.ocrLanguages;
    previewBuildsCheckbox.checked = payload.includePreviews;
    renderSettingsPane(payload.settings);

    // The engine's persisted theme is the source of truth (section 6); the localStorage read above
    // was only a fast-path guess for the very first paint.
    if (payload.settings.theme !== currentTheme) setTheme(payload.settings.theme);
  } catch (err) {
    showSettingsStatus(err.message, true);
  }

  connectEvents({
    onStatus: renderStatus,
    onPlugins: renderPluginList,
    onConnected: () => setReconnecting(false),
    onReconnecting: () => setReconnecting(true),
  });
}

init();
