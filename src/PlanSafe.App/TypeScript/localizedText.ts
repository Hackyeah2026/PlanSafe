import { TextKeys, type TextKey, englishText } from "./textKeys.js";
/** Text-only localization for DOM owned by map controls. Never interprets translations as HTML. */
export function text(key: TextKey, ...args: unknown[]): string {
  return (
    globalThis.window?.planSafeLocalization?.text(key, ...args) ??
    englishText[key].replace(/\{(\d+)\}/g, (_, index: string) =>
      String(args[Number(index)] ?? ""),
    )
  );
}

export function bindText(
  element: HTMLElement,
  key: TextKey,
  ...args: unknown[]
): HTMLElement {
  if (globalThis.window?.planSafeLocalization)
    window.planSafeLocalization.bind(element, key, ...args);
  else element.textContent = text(key, ...args);
  return element;
}

export function localizedSpan(key: TextKey, ...args: unknown[]): HTMLElement {
  return bindText(document.createElement("span"), key, ...args);
}

export function localizeMapControls(container: HTMLElement): void {
  for (const [selector, key] of [
    [".leaflet-control-zoom-in", TextKeys.Map.ZoomIn],
    [".leaflet-control-zoom-out", TextKeys.Map.ZoomOut],
  ] as const) {
    const button = container.querySelector<HTMLElement>(selector);
    if (!button) continue;
    button.dataset.i18nAria = key;
    button.dataset.i18nTitle = key;
    button.setAttribute("aria-label", text(key));
    button.title = text(key);
  }
}
