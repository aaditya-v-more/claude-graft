/* Shared appearance for Aaditya's sites. Load synchronously in the head. */
(() => {
  if (window.AadityaAppearance) return;
  const key = "aaditya-appearance";
  const root = document.documentElement;
  const device = window.matchMedia("(prefers-color-scheme: dark)");
  const firstParty = (host) =>
    host === "aadityamore.com" || host.endsWith(".aadityamore.com");
  const shared = firstParty(location.hostname);
  const valid = (value) => value === "light" || value === "dark";
  const stored = () => {
    try {
      return localStorage.getItem(key);
    } catch {
      return null;
    }
  };
  const cookie = () => {
    try {
      const value = document.cookie
        .split(";")
        .map((v) => v.trim())
        .find((v) => v.startsWith(key + "="))
        ?.slice(key.length + 1);
      return valid(value) ? value : null;
    } catch {
      return null;
    }
  };
  const saveLocal = (value) => {
    try {
      localStorage.setItem(key, value);
    } catch {
      /* Optional persistence. */
    }
  };
  const saveCookie = (value) => {
    if (!shared) return;
    try {
      document.cookie = `${key}=${value}; Path=/; Domain=aadityamore.com; Max-Age=31536000; SameSite=Lax${location.protocol === "https:" ? "; Secure" : ""}`;
    } catch {
      /* The selected palette still works if cookies are blocked. */
    }
  };
  let preference = "system";
  let effective;
  let lastCookie = cookie();
  let sessionChoice = null;
  const read = () => {
    if (!shared)
      return sessionChoice || (valid(stored()) ? stored() : "system");
    const latest = cookie();
    if (latest !== lastCookie) sessionChoice = null;
    lastCookie = latest;
    return sessionChoice || latest || "system";
  };

  // Migrate the old portfolio-only setting once. Other hosts' old storage must
  // never overwrite the newer shared cookie when a dormant tab is revisited.
  if (shared && location.hostname === "aadityamore.com") {
    try {
      if (!localStorage.getItem(key + "-migrated")) {
        const previous = stored();
        if (!lastCookie && valid(previous)) saveCookie(previous);
        localStorage.setItem(key + "-migrated", "1");
      }
    } catch {
      /* Storage can be unavailable in private or restricted contexts. */
    }
  }

  const paintControls = () => {
    document.querySelectorAll("[data-appearance-choice]").forEach((button) => {
      button.setAttribute(
        "aria-pressed",
        String(button.dataset.appearanceChoice === effective),
      );
    });
  };
  const apply = () => {
    const next =
      preference === "system"
        ? device.matches
          ? "dark"
          : "light"
        : preference;
    const changed =
      effective !== next || root.dataset.appearancePreference !== preference;
    effective = next;
    root.dataset.theme = effective;
    root.dataset.appearancePreference = preference;
    document.querySelectorAll('meta[name="theme-color"]').forEach((meta) => {
      meta.content =
        getComputedStyle(root).getPropertyValue("--appearance-chrome").trim() ||
        (effective === "dark" ? "#131c2b" : "#f4f0e8");
    });
    paintControls();
    if (changed)
      window.dispatchEvent(
        new CustomEvent("aaditya:appearance", {
          detail: { preference, effective },
        }),
      );
  };
  const refresh = () => {
    preference = read();
    apply();
  };
  const choose = (value) => {
    if (!valid(value)) return;
    saveLocal(value);
    saveCookie(value);
    lastCookie = cookie();
    sessionChoice = shared && lastCookie !== value ? value : null;
    if (!shared && stored() !== value) sessionChoice = value;
    preference = value;
    apply();
  };
  window.AadityaAppearance = {
    get: () => ({ preference, effective }),
    choose,
    refresh,
  };

  // The one linked project on GitHub Pages cannot share a parent-domain cookie.
  // Transfer only the appearance choice on navigation between our own sites.
  const owned = (url) =>
    firstParty(url.hostname) ||
    (url.hostname === "aaditya-v-more.github.io" &&
      /^\/claude-ollama(?:\/|$)/.test(url.pathname));
  const current = new URL(location.href);
  const incoming = current.searchParams.get("appearance");
  if (owned(current) && valid(incoming)) {
    choose(incoming);
    current.searchParams.delete("appearance");
    try {
      history.replaceState(history.state, "", current.href);
    } catch {
      /* Cosmetic only. */
    }
  } else refresh();

  document.addEventListener("click", (event) => {
    const button = event.target.closest?.("button[data-appearance-choice]");
    if (button) choose(button.dataset.appearanceChoice);
  });
  const decorate = (event) => {
    const link = event.target.closest?.("a[href]");
    if (!link) return;
    const url = new URL(link.href, location.href);
    if (
      !owned(current) ||
      !owned(url) ||
      url.protocol !== "https:" ||
      url.origin === current.origin
    )
      return;
    if (firstParty(current.hostname) && firstParty(url.hostname)) return;
    if (valid(preference)) url.searchParams.set("appearance", preference);
    else url.searchParams.delete("appearance");
    link.href = url.href;
  };
  document.addEventListener("click", decorate, true);
  document.addEventListener("auxclick", decorate, true);
  document.addEventListener("contextmenu", decorate, true);
  document.addEventListener("DOMContentLoaded", refresh);
  window.addEventListener("pageshow", refresh);
  window.addEventListener("focus", refresh);
  document.addEventListener("visibilitychange", () => {
    if (!document.hidden) refresh();
  });
  window.addEventListener("storage", (event) => {
    if (event.key === key || event.key === null) refresh();
  });
  device.addEventListener("change", refresh);
  // Storage events don't cross origins. Refresh visible sibling-site tabs, and
  // catch restoration from the back/forward cache without requiring a reload.
  window.cookieStore?.addEventListener("change", refresh);
  setInterval(() => {
    if (!document.hidden) refresh();
  }, 1500);
})();
