import { TextKeys } from "./textKeys.js";
import { text, localizedSpan, localizeMapControls } from "./localizedText.js";

// Citizen Map Interop for Mobile Evacuation Assistant
// Reuses Leaflet instance to display citizen location, shelters, roadblocks, alarm zones, and active route.

import { createShelterIcon, createShelterPopup } from "./mapPopups.js";

const citizenMaps = new Map();

/**
 * Initializes the mobile citizen evacuation map.
 * @param {string} containerId - DOM ID of the map container.
 * @param {number} centerLat - Initial latitude in degrees.
 * @param {number} centerLng - Initial longitude in degrees.
 * @param {number} zoom - Initial Leaflet zoom level.
 * @param {{invokeMethodAsync(method: string, ...args: unknown[]): Promise<unknown>} | null} dotNetRef - Citizen click and shelter selection callbacks.
 * @param {boolean} readOnly - Show only a non-interactive base map.
 * @returns {boolean} Whether the container was found and initialized.
 */
export function initCitizenMap(
  containerId,
  centerLat = 50.0614,
  centerLng = 19.9366,
  zoom = 15,
  dotNetRef = null,
  readOnly = false,
) {
  const container = document.getElementById(containerId);
  if (!container) {
    console.warn(`[citizenMapInterop] Container #${containerId} not found.`);
    return false;
  }

  if (citizenMaps.has(containerId)) {
    disposeCitizenMap(containerId);
  }

  const reduceMotion = window.matchMedia(
    "(prefers-reduced-motion: reduce)",
  ).matches;
  const map = L.map(container, {
    center: [centerLat, centerLng],
    zoom: zoom,
    zoomControl: !readOnly,
    scrollWheelZoom: !readOnly,
    touchZoom: !readOnly,
    dragging: !readOnly,
    doubleClickZoom: !readOnly,
    boxZoom: !readOnly,
    keyboard: !readOnly,
    tap: !readOnly,
    zoomAnimation: !reduceMotion,
    fadeAnimation: !reduceMotion,
    markerZoomAnimation: !reduceMotion,
  });

  // Dark-friendly OpenStreetMap tile layer
  L.tileLayer("https://tile.openstreetmap.org/{z}/{x}/{y}.png", {
    maxZoom: 19,
    attribution:
      '&copy; OpenStreetMap <span data-i18n="Map.Contributors">' +
      text(TextKeys.Map.Contributors) +
      "</span>",
  }).addTo(map);

  if (!readOnly) map.zoomControl.setPosition("topleft");
  localizeMapControls(container);

  const zonesLayer = L.layerGroup().addTo(map);
  const safeZonesLayer = L.layerGroup().addTo(map);
  const roadblocksLayer = L.layerGroup().addTo(map);
  const routeLayer = L.layerGroup().addTo(map);
  const sheltersLayer = L.layerGroup().addTo(map);
  const citizenLayer = L.layerGroup().addTo(map);

  // Click handler to allow manual citizen repositioning
  map.on("click", (e) => {
    if (!readOnly && dotNetRef) {
      dotNetRef.invokeMethodAsync(
        "OnCitizenMapClicked",
        e.latlng.lat,
        e.latlng.lng,
      );
    }
  });

  let resizeObserver = null;
  if (window.ResizeObserver) {
    resizeObserver = new ResizeObserver(() => {
      if (!citizenMaps.has(containerId)) return;
      map.invalidateSize({ pan: false });
    });
    resizeObserver.observe(container);
  }

  citizenMaps.set(containerId, {
    map: map,
    containerId: containerId,
    dotNetRef: dotNetRef,
    resizeObserver: resizeObserver,
    zonesLayer: zonesLayer,
    safeZonesLayer: safeZonesLayer,
    roadblocksLayer: roadblocksLayer,
    routeLayer: routeLayer,
    sheltersLayer: sheltersLayer,
    citizenLayer: citizenLayer,
    hasInitialFit: false,
  });

  return true;
}

/**
 * Updates citizen location, shelters, roadblocks, and route polyline.
 */
export function updateCitizenMap(
  containerId,
  citizenLat,
  citizenLng,
  accuracyMeters,
  sheltersJson,
  selectedShelterId,
  roadblocksJson,
  routeCoordsJson,
  evacZonesJson,
  safeZonesJson,
  needsLocation = false,
) {
  const state = citizenMaps.get(containerId);
  if (!state) return;

  const {
    map,
    citizenLayer,
    sheltersLayer,
    roadblocksLayer,
    routeLayer,
    zonesLayer,
    safeZonesLayer,
  } = state;

  // 1. Update Evacuation Zones
  zonesLayer.clearLayers();
  if (evacZonesJson) {
    try {
      const zones = JSON.parse(evacZonesJson);
      for (const zone of zones) {
        if (Array.isArray(zone) && zone.length >= 3) {
          const latlngs = zone
            .map((pt) => {
              if (Array.isArray(pt)) return [Number(pt[0]), Number(pt[1])];
              return [
                Number(
                  pt.latitude ??
                    pt.Latitude ??
                    pt.lat ??
                    pt.Lat ??
                    pt.y ??
                    pt.Y,
                ),
                Number(
                  pt.longitude ??
                    pt.Longitude ??
                    pt.lng ??
                    pt.Lng ??
                    pt.x ??
                    pt.X,
                ),
              ];
            })
            .filter((p) => !isNaN(p[0]) && !isNaN(p[1]));

          if (latlngs.length >= 3) {
            L.polygon(latlngs, {
              color: "#f97316",
              weight: 2.5,
              dashArray: "4, 4",
              fillColor: "#f97316",
              fillOpacity: 0.15,
            }).addTo(zonesLayer);
          }
        }
      }
    } catch (e) {
      console.warn("[citizenMapInterop] Error parsing evac zones:", e);
    }
  }

  // 1b. Update Safe Zones (green polygons for safe zones / shelters)
  if (safeZonesLayer) {
    safeZonesLayer.clearLayers();
    if (safeZonesJson) {
      try {
        const safeZones = JSON.parse(safeZonesJson);
        for (const zone of safeZones) {
          if (Array.isArray(zone) && zone.length >= 3) {
            const latlngs = zone
              .map((pt) => {
                if (Array.isArray(pt)) return [Number(pt[0]), Number(pt[1])];
                return [
                  Number(
                    pt.latitude ??
                      pt.Latitude ??
                      pt.lat ??
                      pt.Lat ??
                      pt.y ??
                      pt.Y,
                  ),
                  Number(
                    pt.longitude ??
                      pt.Longitude ??
                      pt.lng ??
                      pt.Lng ??
                      pt.x ??
                      pt.X,
                  ),
                ];
              })
              .filter((p) => !isNaN(p[0]) && !isNaN(p[1]));

            if (latlngs.length >= 3) {
              L.polygon(latlngs, {
                color: "#10b981",
                weight: 2.5,
                dashArray: "5, 5",
                fillColor: "#10b981",
                fillOpacity: 0.18,
              })
                .bindTooltip(localizedSpan(TextKeys.Map.SafeZone), {
                  sticky: true,
                })
                .addTo(safeZonesLayer);
            }
          }
        }
      } catch (e) {
        console.warn("[citizenMapInterop] Error parsing safe zones:", e);
      }
    }
  }

  // 2. Update Roadblocks
  roadblocksLayer.clearLayers();
  if (roadblocksJson) {
    try {
      const roadblocks = JSON.parse(roadblocksJson);
      for (const rb of roadblocks) {
        if (Array.isArray(rb) && rb.length >= 2) {
          const latlngs = rb
            .map((pt) => {
              if (Array.isArray(pt)) return [Number(pt[0]), Number(pt[1])];
              return [
                Number(
                  pt.latitude ??
                    pt.Latitude ??
                    pt.lat ??
                    pt.Lat ??
                    pt.y ??
                    pt.Y,
                ),
                Number(
                  pt.longitude ??
                    pt.Longitude ??
                    pt.lng ??
                    pt.Lng ??
                    pt.x ??
                    pt.X,
                ),
              ];
            })
            .filter((p) => !isNaN(p[0]) && !isNaN(p[1]));

          if (latlngs.length >= 2) {
            L.polyline(latlngs, {
              color: "#ef4444",
              weight: 5,
              dashArray: "8, 8",
              opacity: 0.95,
            }).addTo(roadblocksLayer);
          }
        }
      }
    } catch (e) {
      console.warn("[citizenMapInterop] Error parsing roadblocks:", e);
    }
  }

  // 3. Update Route Polyline
  routeLayer.clearLayers();
  if (routeCoordsJson) {
    try {
      const route = JSON.parse(routeCoordsJson);
      if (Array.isArray(route) && route.length >= 2) {
        const latlngs = route
          .map((pt) => {
            if (Array.isArray(pt)) return [Number(pt[0]), Number(pt[1])];
            return [
              Number(
                pt.latitude ?? pt.Latitude ?? pt.lat ?? pt.Lat ?? pt.y ?? pt.Y,
              ),
              Number(
                pt.longitude ??
                  pt.Longitude ??
                  pt.lng ??
                  pt.Lng ??
                  pt.x ??
                  pt.X,
              ),
            ];
          })
          .filter(
            (p) => !isNaN(p[0]) && !isNaN(p[1]) && p[0] !== 0 && p[1] !== 0,
          );

        if (latlngs.length >= 2) {
          L.polyline(latlngs, {
            color: "#38bdf8",
            weight: 5,
            dashArray: "6, 8",
            opacity: 0.95,
            lineJoin: "round",
          }).addTo(routeLayer);
        }
      }
    } catch (e) {
      console.warn("[citizenMapInterop] Error parsing route:", e);
    }
  }

  // 4. Update Shelters (Always rendered on map)
  sheltersLayer.clearLayers();
  if (sheltersJson) {
    try {
      const shelters = JSON.parse(sheltersJson);
      for (const s of shelters) {
        const sLat = Number(
          s.latitude ?? s.Latitude ?? s.lat ?? s.Lat ?? s.y ?? s.Y,
        );
        const sLng = Number(
          s.longitude ?? s.Longitude ?? s.lng ?? s.Lng ?? s.x ?? s.X,
        );
        if (isNaN(sLat) || isNaN(sLng) || sLat === 0 || sLng === 0) continue;

        const sId = s.id ?? s.Id ?? "";
        const sName = s.name ?? s.Name ?? text(TextKeys.Shelter.DefaultName);
        const sCap = Number(s.capacity ?? s.Capacity ?? 500);
        const sOcc = Number(
          s.currentOccupancy ??
            s.CurrentOccupancy ??
            s.peopleCount ??
            s.PeopleCount ??
            0,
        );

        const isSelected =
          !!selectedShelterId &&
          String(sId).toLowerCase() === String(selectedShelterId).toLowerCase();
        const isFull = sCap > 0 && sOcc >= sCap;
        const occPct = sCap > 0 ? Math.round((sOcc / sCap) * 100) : 0;

        const color = isSelected
          ? "#0ea5e9"
          : isFull
            ? "#ef4444"
            : occPct >= 70
              ? "#f59e0b"
              : "#10b981";
        const icon = L.divIcon({
          html: createShelterIcon(sName, occPct, color, isSelected),
          className: "custom-shelter-marker",
          iconSize: [34, 34],
          iconAnchor: [17, 17],
        });

        const marker = L.marker([sLat, sLng], { icon: icon }).addTo(
          sheltersLayer,
        );
        marker.bindPopup(createShelterPopup(sName, sOcc, sCap, occPct), {
          className: "plansafe-leaflet-popup",
          maxWidth: 240,
        });

        if (state.dotNetRef) {
          marker.on("click", () => {
            state.dotNetRef.invokeMethodAsync("OnShelterSelected", String(sId));
          });
        }

        if (isSelected) {
          L.circleMarker([sLat, sLng], {
            radius: 26,
            color: "#38bdf8",
            weight: 2,
            dashArray: "4, 4",
            fill: false,
            interactive: false,
          }).addTo(sheltersLayer);
        }
      }
    } catch (e) {
      console.warn("[citizenMapInterop] Error parsing shelters:", e);
    }
  }

  // 5. Update Citizen Location Marker
  citizenLayer.clearLayers();
  if (Number.isFinite(citizenLat) && Number.isFinite(citizenLng)) {
    const citizenHtml = '<div class="citizen-position"></div>';

    const citizenIcon = L.divIcon({
      html: citizenHtml,
      className: "custom-citizen-marker",
      iconSize: [18, 18],
      iconAnchor: [9, 9],
    });

    L.marker([citizenLat, citizenLng], {
      icon: citizenIcon,
      zIndexOffset: 1000,
      keyboard: false,
      interactive: false,
    }).addTo(citizenLayer);

    if (accuracyMeters && accuracyMeters > 0 && accuracyMeters < 500) {
      L.circle([citizenLat, citizenLng], {
        radius: accuracyMeters,
        color: "#38bdf8",
        weight: 1,
        fillColor: "#38bdf8",
        fillOpacity: 0.08,
      }).addTo(citizenLayer);
    }
  }

  // Fit once on entry. Location updates and polling preserve the chosen viewport.
  if (!state.hasInitialFit) {
    state.hasInitialFit = fitCitizenBounds(containerId);
  }
}

/** Keep fitted locations inside the part of the map left visible by the panel. */
function citizenFitPadding(map) {
  const container = map.getContainer();
  const workspace = container.closest(".evac-workspace");
  const bounds = container.getBoundingClientRect();
  const panel = workspace
    ?.querySelector(".evac-panel")
    ?.getBoundingClientRect();
  const tools = workspace
    ?.querySelector(".evac-map-tools")
    ?.getBoundingClientRect();
  let right = 96;
  let bottom = 56;
  if (panel) {
    if (panel.width > bounds.width * 0.65)
      bottom = bounds.bottom - panel.top + 56;
    else right = bounds.right - panel.left + 96;
  }
  return {
    paddingTopLeft: [
      96,
      Math.min(
        (tools?.bottom ?? bounds.top + 64) - bounds.top + 36,
        bounds.height * 0.3,
      ),
    ],
    paddingBottomRight: [
      Math.min(right, bounds.width * 0.6),
      Math.min(bottom, bounds.height * 0.7),
    ],
  };
}

/**
 * Fits map viewport bounds to enclose citizen, all shelters, evacuation/safe zones, and route.
 * @returns {boolean} Whether valid bounds were successfully fitted.
 */
export function fitCitizenBounds(containerId) {
  const state = citizenMaps.get(containerId);
  if (!state) return false;
  const {
    map,
    sheltersLayer,
    citizenLayer,
    zonesLayer,
    safeZonesLayer,
    routeLayer,
  } = state;

  try {
    const bounds = L.latLngBounds([]);
    for (const group of [
      sheltersLayer,
      zonesLayer,
      citizenLayer,
      safeZonesLayer,
      routeLayer,
    ]) {
      for (const layer of group.getLayers()) {
        bounds.extend(layer.getBounds ? layer.getBounds() : layer.getLatLng());
      }
    }
    if (!bounds.isValid()) return false;
    map.fitBounds(bounds, {
      ...citizenFitPadding(map),
      maxZoom: 16,
      animate: false,
    });
    return true;
  } catch (e) {
    console.warn("[citizenMapInterop] fitCitizenBounds error:", e);
    return false;
  }
}

/**
 * Pans map to target location.
 */
export function panToCitizen(containerId, lat, lng, zoom = null) {
  const state = citizenMaps.get(containerId);
  if (!state) return;
  if (zoom) {
    state.map.setView([lat, lng], zoom);
  } else {
    state.map.panTo([lat, lng]);
  }
}

/**
 * Invalidate map size when container dimensions change.
 */
export function invalidateCitizenMapSize(containerId) {
  const state = citizenMaps.get(containerId);
  if (state) {
    state.map.invalidateSize({ pan: false });
  }
}

/**
 * Disposes the Leaflet map instance and observers.
 */
export function disposeCitizenMap(containerId) {
  const state = citizenMaps.get(containerId);
  if (!state) return;

  if (state.resizeObserver) {
    state.resizeObserver.disconnect();
  }
  state.map.remove();
  citizenMaps.delete(containerId);
}
