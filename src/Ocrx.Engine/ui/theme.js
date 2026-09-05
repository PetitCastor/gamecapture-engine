// Theme resolution + persistence (TASK-UI-05 section 6).
//
// "system" | "light" | "dark". The engine's persisted `theme` setting (served on `/api/settings`)
// is the source of truth on load; localStorage is only a fast-path so the very first paint is not
// the wrong colour before that request resolves. Every storage read/write is wrapped in try/catch
// and degrades to "system" (or a silent no-op on write) when storage throws or is empty — private
// browsing, a disk quota, or a WebView2 profile that disables it must never break rendering.

const THEME_KEY = "ocrx.theme";
const root = document.documentElement;

export function readStoredTheme() {
  try {
    const value = localStorage.getItem(THEME_KEY);
    if (value === "light" || value === "dark" || value === "system") return value;
  } catch {
    // localStorage unavailable - fall through to the default below.
  }
  return "system";
}

export function storeTheme(choice) {
  try {
    localStorage.setItem(THEME_KEY, choice);
  } catch {
    // Persisting the fast-path copy is a nicety; the engine's own setting is still the source of
    // truth on the next load, fetched fresh over the network either way.
  }
}

/** Stamps (or clears) `documentElement.dataset.theme`; CSS's `prefers-color-scheme` query handles
 * "system" on its own once no explicit choice is stamped. */
export function applyTheme(choice) {
  if (choice === "light" || choice === "dark") {
    root.dataset.theme = choice;
  } else {
    delete root.dataset.theme;
  }
}

/** Notifies `onChange` when the OS light/dark preference flips while in "system" mode. The CSS
 * media query already repaints on its own; callers only need this for UI that mirrors the choice
 * (e.g. keeping a toggle's pressed state in sync). Returns an unsubscribe function. */
export function watchSystemTheme(onChange) {
  const query = window.matchMedia("(prefers-color-scheme: dark)");
  query.addEventListener("change", onChange);
  return () => query.removeEventListener("change", onChange);
}
