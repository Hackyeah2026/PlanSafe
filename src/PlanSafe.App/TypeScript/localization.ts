// Loaded before Blazor so boot errors and appearance use the browser language too.
interface Window {
  planSafeTextKeys: typeof import("./textKeys.js").TextKeys;
  planSafeTranslations: Record<string, Record<string, string>>;
  planSafeLocalization: {
    detect(): string;
    apply(
      language: string,
      strings: Record<string, string>,
      persist: boolean,
    ): void;
    text(key: import("./textKeys.js").TextKey, ...args: unknown[]): string;
    bind(
      element: HTMLElement,
      key: import("./textKeys.js").TextKey,
      ...args: unknown[]
    ): void;
  };
}

(() => {
  const TextKeys = window.planSafeTextKeys;
  const storageKey = "plansafe.language.v1";
  function detect(): string {
    try {
      const saved = localStorage.getItem(storageKey);
      if (saved === "pl" || saved === "en") return saved;
    } catch {
      /* Storage may be unavailable. */
    }
    for (const preference of navigator.languages ?? [navigator.language]) {
      const language = preference.toLowerCase().split("-")[0];
      if (language === "pl" || language === "en") return language;
    }
    return "en";
  }

  let language = detect();
  let strings = window.planSafeTranslations[language];
  function text(key: string, ...args: unknown[]): string {
    return (strings[key] ?? window.planSafeTranslations.en[key] ?? key).replace(
      /\{(\d+)\}/g,
      (placeholder, index: string) =>
        args[Number(index)] === undefined
          ? placeholder
          : String(args[Number(index)]),
    );
  }
  function update(element: HTMLElement): void {
    const key = element.dataset.i18n;
    if (key)
      element.textContent = text(
        key,
        ...JSON.parse(element.dataset.i18nArgs ?? "[]"),
      );
    const aria = element.dataset.i18nAria;
    if (aria) element.setAttribute("aria-label", text(aria));
    const title = element.dataset.i18nTitle;
    if (title) element.title = text(title);
  }
  const bound = new Set<WeakRef<HTMLElement>>();
  function refresh(): void {
    for (const reference of bound) {
      const element = reference.deref();
      if (element) update(element);
      else bound.delete(reference);
    }
    document.documentElement.lang = language;
    document.documentElement.style.setProperty(
      "--plansafe-error-text",
      JSON.stringify(text(TextKeys.Common.Error)),
    );
    document.documentElement.style.setProperty(
      "--plansafe-loading-text",
      JSON.stringify(text(TextKeys.Common.Loading)),
    );
    document
      .querySelectorAll<HTMLElement>(
        "[data-i18n], [data-i18n-aria], [data-i18n-title]",
      )
      .forEach(update);
  }
  window.planSafeLocalization = {
    detect,
    text,
    bind(element, key, ...args) {
      if (!element.dataset.i18n) bound.add(new WeakRef(element));
      element.dataset.i18n = key;
      element.dataset.i18nArgs = JSON.stringify(args);
      update(element);
    },
    apply(selected, catalogue, persist) {
      language = selected === "pl" ? "pl" : "en";
      strings = catalogue;
      if (persist) {
        try {
          localStorage.setItem(storageKey, language);
        } catch {
          /* Keep the selection for this session when storage is blocked. */
        }
      }
      refresh();
    },
  };
  refresh();
  document.addEventListener("DOMContentLoaded", refresh);
})();
