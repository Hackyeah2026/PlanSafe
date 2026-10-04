import { driver, type Driver, type DriveStep } from "driver.js";
import { text } from "./localizedText.js";
import { TextKeys } from "./textKeys.js";

const storageKey = "plansafe.map-tour.v1";
let seen = false;
let active: { id: string; tour: Driver } | undefined;
let notifyClosed: (() => void) | undefined;

export function shouldStart(): boolean {
  try {
    return !seen && localStorage.getItem(storageKey) !== "seen";
  } catch {
    return !seen;
  }
}

// Illustrations are previews only: no map items or session data are changed.
const diagrams = {
  polygon:
    '<svg viewBox="0 0 280 100" aria-hidden="true"><path d="M45 75 80 20 215 30 235 80Z"/><g><circle cx="45" cy="75" r="5"/><circle cx="80" cy="20" r="5"/><circle cx="215" cy="30" r="5"/><circle cx="235" cy="80" r="5"/></g><text x="27" y="78">1</text><text x="76" y="12">2</text><text x="225" y="28">3</text><text x="247" y="84">4</text></svg>',
  circle:
    '<svg viewBox="0 0 280 100" aria-hidden="true"><circle cx="140" cy="50" r="39"/><path d="M140 50h39"/><g><circle cx="140" cy="50" r="5"/><circle cx="179" cy="50" r="5"/></g><text x="127" y="70">1</text><text x="187" y="54">2</text></svg>',
};

export function start(
  id: string,
  reference: { invokeMethodAsync(name: string): Promise<unknown> },
): void {
  dispose(active?.id ?? "");
  const root = document
    .getElementById(id)
    ?.closest<HTMLElement>(".map-module-layout");
  if (!root) return;
  const controlsPreview = root.querySelector<HTMLElement>(
    ".map-tour-controls-preview",
  );
  const showControlsPreview = (show: boolean) => {
    if (controlsPreview) controlsPreview.hidden = !show;
  };
  const t = TextKeys.Tour;
  const step = (
    selector: string | undefined,
    title: Parameters<typeof text>[0],
    body: Parameters<typeof text>[0],
    diagram?: keyof typeof diagrams,
  ): DriveStep => ({
    ...(selector ? { element: () => root.querySelector(selector)! } : {}),
    popover: {
      title: text(title),
      description: text(body),
      side: "right",
      align: "center",
      onPopoverRender(popover) {
        // Treat catalogue strings as text, including any future translations.
        popover.title.textContent = text(title);
        popover.description.textContent = text(body);
        if (diagram) {
          const preview = document.createElement("div");
          preview.className = "map-tour-diagram";
          preview.innerHTML = diagrams[diagram];
          popover.description.prepend(preview);
        }
        // Driver.js initially focuses the first control (Close). Once it has
        // finished rendering, keep keyboard focus on the tour's next action.
        queueMicrotask(() => popover.nextButton.focus({ preventScroll: true }));
      },
    },
  });
  notifyClosed = () => {
    void reference.invokeMethodAsync("OnMapTourClosed").catch(() => {});
  };
  const tour = driver({
    animate: !matchMedia("(prefers-reduced-motion: reduce)").matches,
    overlayColor: "#0f172a",
    overlayOpacity: 0.56,
    stagePadding: 6,
    stageRadius: 12,
    popoverClass: "map-tour-popover",
    disableActiveInteraction: true,
    overlayClickBehavior: "none",
    showProgress: true,
    progressText: text(t.Progress),
    nextBtnText: text(t.Next),
    prevBtnText: text(t.Back),
    doneBtnText: text(t.Done),
    closeBtnLabel: text(t.Close),
    onHighlightStarted(element) {
      showControlsPreview(!!element?.closest(".map-tour-controls-preview"));
    },
    steps: [
      step(undefined, t.WelcomeTitle, t.WelcomeBody),
      step(".drawing-tool--zone", t.ZoneTitle, t.ZoneBody),
      step(".drawing-tool--safe", t.SafeTitle, t.SafeBody),
      step(".drawing-tool--blockade", t.BlockadeTitle, t.BlockadeBody),
      step(
        '[data-map-tour="polygon"]',
        t.PolygonTitle,
        t.PolygonBody,
        "polygon",
      ),
      step('[data-map-tour="circle"]', t.CircleTitle, t.CircleBody, "circle"),
      step(".drawing-tool", t.RemoveTitle, t.RemoveBody),
      step('[data-map-tour="settings"]', t.SettingsTitle, t.SettingsBody),
      step('[data-map-tour="simulation"]', t.SimulationTitle, t.SimulationBody),
      step(
        ".map-tour-controls-preview .sim-control-capsule",
        t.ControlsTitle,
        t.ControlsBody,
      ),
      step('[data-map-tour="evacuation"]', t.EvacuationTitle, t.EvacuationBody),
    ],
    onDestroyed() {
      showControlsPreview(false);
      active = undefined;
      const callback = notifyClosed;
      notifyClosed = undefined;
      callback?.();
    },
  });
  active = { id, tour };
  tour.drive();
  seen = true;
  try {
    localStorage.setItem(storageKey, "seen");
  } catch {
    /* Session fallback above. */
  }
}

export function dispose(id: string): void {
  if (active?.id !== id) return;
  notifyClosed = undefined;
  active.tour.destroy();
  active = undefined;
}
