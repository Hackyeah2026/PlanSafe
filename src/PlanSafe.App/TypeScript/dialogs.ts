const mounted = new WeakMap<
  HTMLDialogElement,
  {
    previous: HTMLElement | null;
    cancel: EventListener;
    outside: (event: MouseEvent) => void;
  }
>();
export function mount(dialog: HTMLDialogElement): void {
  if (mounted.has(dialog)) return;
  const previous =
    document.activeElement instanceof HTMLElement
      ? document.activeElement
      : null;
  const cancel: EventListener = (event) => {
    event.preventDefault();
    // The feature close callback owns its flag. Native dialog owns focus and modality.
    dialog.querySelector<HTMLButtonElement>("[data-dialog-close]")?.click();
  };
  const outside = (event: MouseEvent): void => {
    if (event.target !== dialog) return;
    const bounds = dialog.getBoundingClientRect();
    if (
      event.clientX < bounds.left ||
      event.clientX > bounds.right ||
      event.clientY < bounds.top ||
      event.clientY > bounds.bottom
    )
      dialog.querySelector<HTMLButtonElement>("[data-dialog-close]")?.click();
  };
  dialog.addEventListener("cancel", cancel);
  dialog.addEventListener("click", outside);
  mounted.set(dialog, { previous, cancel, outside });
  dialog.showModal();
}
export function unmount(dialog: HTMLDialogElement | null): void {
  if (!dialog) return;
  const state = mounted.get(dialog);
  if (!state) return;
  dialog.removeEventListener("cancel", state.cancel);
  dialog.removeEventListener("click", state.outside);
  dialog.close();
  if (state.previous?.isConnected) state.previous.focus();
  mounted.delete(dialog);
}
