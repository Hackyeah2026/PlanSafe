const mounted = new Map<HTMLDetailsElement, () => void>();
/**
 * Closes a disclosure on Escape, outside pointer presses, or link navigation.
 * @param details - Disclosure whose listeners this module will own.
 * @remarks Repeated mounts are ignored. Call unmount before removing the element.
 */
export function mount(details: HTMLDetailsElement): void {
  if (mounted.has(details)) return;
  const keydown = (event: KeyboardEvent): void => {
    if (event.key === "Escape" && details.open) {
      details.open = false;
      details.querySelector("summary")?.focus();
      event.preventDefault();
    }
  };
  const outside = (event: PointerEvent): void => {
    if (!(event.target instanceof Node) || !details.contains(event.target))
      details.open = false;
  };
  const navigate = (event: MouseEvent): void => {
    if (event.target instanceof Element && event.target.closest("a[href]"))
      details.open = false;
  };
  document.addEventListener("keydown", keydown);
  document.addEventListener("pointerdown", outside);
  details.addEventListener("click", navigate);
  mounted.set(details, () => {
    document.removeEventListener("keydown", keydown);
    document.removeEventListener("pointerdown", outside);
    details.removeEventListener("click", navigate);
  });
}
/**
 * Releases the disclosure's element and document listeners.
 * @param details - Previously mounted disclosure; missing mounts are ignored.
 */
export function unmount(details: HTMLDetailsElement): void {
  mounted.get(details)?.();
  mounted.delete(details);
}
