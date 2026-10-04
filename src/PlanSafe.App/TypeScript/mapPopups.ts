import { TextKeys, type TextKey } from "./textKeys.js";
import { bindText, localizedSpan } from "./localizedText.js";

/** Display fields from a map item; names and metrics are always rendered as text. */
export interface MapPopupItem {
  readonly type: string;
  readonly name?: string;
  readonly metricInfo?: string;
}

function element<K extends keyof HTMLElementTagNameMap>(
  tag: K,
  className: string,
  text?: string,
): HTMLElementTagNameMap[K] {
  const node = document.createElement(tag);
  node.className = className;
  if (text !== undefined) node.textContent = text;
  return node;
}

/**
 * Builds a map popup whose imported display fields cannot create HTML or handlers.
 * @param item - Item type, display name, and optional metric text.
 * @param onDelete - Callback invoked by the popup's remove button.
 * @returns A detached popup; Leaflet owns its DOM while it is displayed.
 */
export function createMapItemPopup(
  item: MapPopupItem,
  onDelete: () => void,
): HTMLElement {
  const popup = element("div", "map-popup-card");
  const header = element("div", "map-popup-header");
  let typeName: TextKey = TextKeys.Map.EvacuationZone;
  let typeClass = "badge-zone";
  if (item.type === "blockade") {
    typeName = TextKeys.Map.Blockade;
    typeClass = "badge-blockade";
  } else if (
    item.type === "safe_circle" ||
    item.type === "safe_polygon" ||
    item.type === "safe_point"
  ) {
    typeName = TextKeys.Map.SafeLocation;
    typeClass = "badge-safe";
  }
  header.append(
    bindText(element("span", `map-popup-badge ${typeClass}`), typeName),
    element("strong", "map-popup-title", item.name ?? ""),
  );
  popup.append(header);
  if (item.metricInfo) {
    const metric = element("div", "map-popup-meta");
    const length = /^(Length|Radius): (.+) m$/.exec(item.metricInfo);
    const vertices = /^(\d+) vertices$/.exec(item.metricInfo);
    if (length)
      bindText(
        metric,
        length[1] === "Length" ? TextKeys.Map.Length : TextKeys.Map.Radius,
        length[2],
      );
    else if (vertices) bindText(metric, TextKeys.Map.Vertices, vertices[1]);
    else metric.textContent = item.metricInfo;
    popup.append(metric);
  }

  const actions = element("div", "map-popup-actions");
  const remove = element("button", "btn-popup-delete");
  remove.type = "button";
  // This icon is fixed markup. Item data is assigned only through textContent.
  remove.innerHTML = `<svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">
    <polyline points="3 6 5 6 21 6"></polyline>
    <path d="M19 6v14a2 2 0 0 1-2 2H7a2 2 0 0 1-2-2V6m3 0V4a2 2 0 0 1 2-2h4a2 2 0 0 1 2 2v2"></path>
  </svg>`;
  remove.append(localizedSpan(TextKeys.Common.Remove));
  remove.addEventListener("click", onDelete);
  actions.append(remove);
  popup.append(actions);
  return popup;
}

/**
 * Creates shelter popup content with a literal name and occupancy description.
 * @param name - Published shelter name, displayed without interpreting markup.
 * @param occupancy - Current number of occupants.
 * @param capacity - Maximum number of occupants.
 * @param occupancyPercent - Percentage shown next to the occupant counts.
 * @returns Detached content suitable for Leaflet's bindPopup method.
 */
export function createShelterPopup(
  name: string,
  occupancy: number,
  capacity: number,
  occupancyPercent: number,
): HTMLElement {
  const popup = element("div", "");
  const hint = bindText(element("small", ""), TextKeys.Shelter.SelectHint);
  hint.style.color = "#0ea5e9";
  popup.append(
    element("strong", "", name),
    document.createElement("br"),
    localizedSpan(
      TextKeys.Shelter.OccupancySummary,
      occupancy,
      capacity,
      occupancyPercent,
    ),
    document.createElement("br"),
    hint,
  );
  return popup;
}

/**
 * Creates the shelter marker label and selection badge without parsing its name.
 * @param name - Published shelter name, displayed as text.
 * @param occupancyPercent - Percentage displayed in the label.
 * @param color - Marker color chosen by the occupancy/selection controller.
 * @param isSelected - Whether to display the selection symbol and halo.
 * @returns Detached content suitable for a Leaflet divIcon.
 */
export function createShelterIcon(
  name: string,
  occupancyPercent: number,
  color: string,
  isSelected: boolean,
): HTMLElement {
  const icon = element("div", "");
  icon.style.cssText =
    "transform: translate(-50%, -50%); display: flex; flex-direction: column; align-items: center; pointer-events: auto; cursor: pointer;";
  const badge = element("div", "", isSelected ? "★" : "⌂");
  badge.style.cssText =
    "width: 32px; height: 32px; border-radius: 50%; display: flex; align-items: center; justify-content: center; color: white; font-weight: bold; font-size: 15px;" +
    (isSelected
      ? "box-shadow: 0 0 0 6px rgba(14, 165, 233, 0.45), 0 0 20px rgba(14, 165, 233, 0.8); border: 2.5px solid #ffffff;"
      : "box-shadow: 0 2px 8px rgba(0,0,0,0.5); border: 1.5px solid rgba(255,255,255,0.85);");
  badge.style.backgroundColor = color;
  const label = element("div", "", `${name} (${occupancyPercent}%)`);
  label.style.cssText =
    "background-color: rgba(15, 23, 42, 0.88); color: #ffffff; padding: 2px 7px; border-radius: 4px; font-size: 11px; font-weight: 600; margin-top: 3px; white-space: nowrap; border: 1px solid rgba(255,255,255,0.2); box-shadow: 0 2px 6px rgba(0,0,0,0.4);";
  icon.append(badge, label);
  return icon;
}
