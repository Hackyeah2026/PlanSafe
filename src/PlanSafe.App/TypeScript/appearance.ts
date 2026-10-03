// Synchronous boot script: appearance is applied before Blazor and owned by JS.
type ThemePreference = "light" | "dark" | null;
interface AppearanceController {
  mount(host: HTMLElement): void;
  unmount(host: HTMLElement): void;
}
interface Window {
  planSafeAppearance: AppearanceController;
}

(() => {
  const key = "plansafe.appearance.v1";
  const media = window.matchMedia("(prefers-color-scheme: dark)");
  const hosts = new Map<HTMLElement, EventListener>();

  function load(): ThemePreference {
    try {
      const value = JSON.parse(window.localStorage.getItem(key) ?? "null");
      return value?.version === 1 &&
        (value.theme === "light" || value.theme === "dark")
        ? value.theme
        : null;
    } catch {
      return null;
    }
  }

  let theme = load();

  function apply(): void {
    document.documentElement.dataset.bsTheme =
      theme ?? (media.matches ? "dark" : "light");
    for (const host of hosts.keys()) {
      for (const button of host.querySelectorAll<HTMLButtonElement>(
        "[data-theme]",
      )) {
        button.setAttribute(
          "aria-pressed",
          String((button.dataset.theme || null) === theme),
        );
      }
    }
  }

  apply();
  media.addEventListener("change", () => {
    if (theme === null) apply();
  });
  window.addEventListener("storage", (event) => {
    if (event.key === key || event.key === null) {
      theme = load();
      apply();
    }
  });

  window.planSafeAppearance = {
    mount(host) {
      if (hosts.has(host)) return;
      const fieldset = document.createElement("fieldset");
      fieldset.className = "appearance-settings";
      const legend = document.createElement("legend");
      legend.textContent = "Theme";
      fieldset.append(legend);
      const group = document.createElement("div");
      group.className = "segmented-control appearance-segmented";
      group.setAttribute("role", "group");
      group.setAttribute("aria-label", "Theme");
      for (const [value, label] of [
        ["light", "Light"],
        ["", "System"],
        ["dark", "Dark"],
      ]) {
        const button = document.createElement("button");
        button.type = "button";
        button.className = "app-button";
        button.dataset.theme = value;
        button.textContent = label;
        group.append(button);
      }
      fieldset.append(group);
      host.replaceChildren(fieldset);
      const click: EventListener = (event) => {
        const button =
          event.target instanceof Element
            ? event.target.closest<HTMLButtonElement>("[data-theme]")
            : null;
        if (!button || !host.contains(button)) return;
        const value = button.dataset.theme;
        theme = value === "light" || value === "dark" ? value : null;
        try {
          window.localStorage.setItem(
            key,
            JSON.stringify({ version: 1, theme }),
          );
        } catch {
          /* Blocked storage: keep the choice for this session. */
        }
        apply();
      };
      hosts.set(host, click);
      host.addEventListener("click", click);
      apply();
    },
    unmount(host) {
      const click = hosts.get(host);
      if (!click) return;
      host.removeEventListener("click", click);
      hosts.delete(host);
      host.replaceChildren();
    },
  };
})();
