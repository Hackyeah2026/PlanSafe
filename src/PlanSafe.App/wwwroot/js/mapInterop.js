// Leaflet OpenStreetMap Interop Module for PlanSafe / CrowdSim
// Supports desktop & mobile, zoom controls, wheel scrolling, resize observation,
// and interactive authoring of Evacuation Zones (circle/polygon), Blockades (line), and Safe Locations (circle/polygon).

const mapInstances = new Map();

// Helper to expose global bridge for popup click actions
window.PlanSafeMap = {
  getMap: (containerId) => {
    return mapInstances.get(containerId)?.map;
  },
  deleteItem: (containerId, itemId) => {
    deleteMapItem(containerId, itemId);
  },
  finishPolygon: (containerId) => {
    finishPolygon(containerId);
  },
  cancelDrawing: (containerId) => {
    cancelCurrentDrawing(containerId);
  },
};

const STORAGE_KEY = "plansafe_map_items";

/**
 * Initializes a Leaflet map in the specified container, centered on Kraków.
 * @param {string} containerId - DOM ID of the container element
 * @param {object} options - Map configuration options
 * @param {object} dotNetRef - DotNetObjectReference for callbacks
 */
export function initMap(containerId, options = {}, dotNetRef = null) {
  const container = document.getElementById(containerId);
  if (!container) {
    console.warn(`[mapInterop] Container #${containerId} not found.`);
    return false;
  }

  if (mapInstances.has(containerId)) {
    disposeMap(containerId);
  }

  const defaultCenter = [50.0614, 19.9366];
  const defaultZoom = 14;
  const center =
    options.lat && options.lng ? [options.lat, options.lng] : defaultCenter;
  const zoom = options.zoom || defaultZoom;

  const map = L.map(container, {
    center: center,
    zoom: zoom,
    zoomControl: true,
    scrollWheelZoom: true,
    touchZoom: true,
    dragging: true,
    tap: true,
  });

  L.tileLayer("https://tile.openstreetmap.org/{z}/{x}/{y}.png", {
    maxZoom: 19,
    attribution:
      '&copy; <a href="https://www.openstreetmap.org/copyright" target="_blank" rel="noopener">OpenStreetMap</a> contributors',
  }).addTo(map);

  map.zoomControl.setPosition("topleft");

  // Create LayerGroup for user-defined zones, blockades, and safe points
  const itemsLayer = L.layerGroup().addTo(map);
  // Create LayerGroup for active drawing ghost elements
  const drawTempLayer = L.layerGroup().addTo(map);
  // Create LayerGroups for GUS census 125m grid and occupant agents (used when simulation starts)
  const gusGridLayer = L.layerGroup().addTo(map);
  const occupantsLayer = L.layerGroup().addTo(map);

  let resizeObserver = null;
  if (window.ResizeObserver) {
    resizeObserver = new ResizeObserver(() => {
      map.invalidateSize();
    });
    resizeObserver.observe(container);
  }

  const state = {
    map: map,
    containerId: containerId,
    resizeObserver: resizeObserver,
    dotNetRef: dotNetRef,
    itemsLayer: itemsLayer,
    drawTempLayer: drawTempLayer,
    gusGridLayer: gusGridLayer,
    occupantsLayer: occupantsLayer,
    items: [],
    drawMode: "none", // 'none' | 'evac_circle' | 'evac_polygon' | 'safe_circle' | 'safe_polygon' | 'blockade'
    drawState: null,
    shapeLayers: new Map(), // itemId -> Leaflet Layer
    disposePreparationCursor: setupPreparationCursor(container),
  };

  mapInstances.set(containerId, state);

  // Bind map drawing events
  setupMapEvents(state);

  // Load persisted items from localStorage
  loadPersistedItems(state);

  // Initial map invalidation
  requestAnimationFrame(() => {
    map.invalidateSize();
  });

  return true;
}

// CSS animates the spinner independently of pointer updates and WASM work.
function setupPreparationCursor(container) {
  const workspace = container.closest(".map-module-layout");
  const spinner = workspace?.querySelector(".simulation-preparation-cursor");
  if (!spinner) return null;
  const move = (event) => {
    if (event.pointerType === "touch") {
      spinner.style.visibility = "hidden";
      return;
    }
    spinner.style.left = `${Math.min(window.innerWidth - 22, event.clientX + 14)}px`;
    spinner.style.top = `${Math.min(window.innerHeight - 22, event.clientY + 14)}px`;
    spinner.style.visibility = "visible";
  };
  const leave = () => {
    spinner.style.visibility = "hidden";
  };
  // Capture also covers Leaflet and child controls that stop propagation.
  workspace.addEventListener("pointermove", move, true);
  workspace.addEventListener("pointerdown", move, true);
  workspace.addEventListener("pointerleave", leave);
  return () => {
    workspace.removeEventListener("pointermove", move, true);
    workspace.removeEventListener("pointerdown", move, true);
    workspace.removeEventListener("pointerleave", leave);
    leave();
  };
}

/**
 * Setup map interaction events for drawing
 */
function setupMapEvents(state) {
  const { map, containerId } = state;

  map.on("click", (e) => {
    handleMapClick(state, e);
  });

  map.on("mousemove", (e) => {
    handleMapMouseMove(state, e);
  });

  map.on("dblclick", (e) => {
    if (
      state.drawMode === "evac_polygon" ||
      state.drawMode === "safe_polygon" ||
      state.drawMode === "polygon"
    ) {
      L.DomEvent.stopPropagation(e);
      L.DomEvent.preventDefault(e);
      if (
        state.drawState &&
        state.drawState.points &&
        state.drawState.points.length >= 3
      ) {
        const pts = state.drawState.points;
        if (pts.length >= 4) {
          const last = pts[pts.length - 1];
          const prev = pts[pts.length - 2];
          if (map.distance(last, prev) < 10) {
            pts.pop();
            if (state.drawState.activeItemId) {
              const existingItem = state.items.find(
                (i) => i.id === state.drawState.activeItemId,
              );
              if (existingItem) {
                existingItem.coordinates = [...pts];
                existingItem.metricInfo = `${pts.length} vertices`;
              }
            }
          }
        }
        finishPolygon(containerId);
      }
    }
  });

  // Handle ESC key to cancel active drawing
  const handleKeyDown = (e) => {
    if (e.key === "Escape" && state.drawMode !== "none") {
      cancelCurrentDrawing(containerId);
    }
  };
  window.addEventListener("keydown", handleKeyDown);
  state._keydownHandler = handleKeyDown;
}

/**
 * Handles map click based on current drawMode
 */
function handleMapClick(state, e) {
  const { map, drawMode, containerId } = state;
  if (drawMode === "none") return;

  const latlng = e.latlng;

  // Blockade (Line: start -> end)
  if (drawMode === "blockade") {
    if (!state.drawState || state.drawState.step === 0) {
      // First click: start point
      const marker = L.circleMarker(latlng, {
        radius: 5,
        color: "#ef4444",
        fillColor: "#ef4444",
        fillOpacity: 1,
      }).addTo(state.drawTempLayer);

      state.drawState = {
        step: 1,
        start: [latlng.lat, latlng.lng],
        startMarker: marker,
        tempLine: null,
      };
      notifyBlazorModeChanged(
        state,
        "blockade",
        "Click to set end of blockade",
        1,
      );
    } else if (state.drawState.step === 1) {
      // Second click: end point
      const start = state.drawState.start;
      const end = [latlng.lat, latlng.lng];
      const distMeters = Math.round(map.distance(start, end));

      const item = {
        id: generateId(),
        type: "blockade",
        name: getNextName(state, "blockade"),
        color: "#ef4444",
        startPoint: start,
        endPoint: end,
        metricInfo: `Length: ${distMeters} m`,
        createdAt: new Date().toISOString(),
      };
      cleanDrawState(state);
      addItem(state, item);
      notifyBlazorModeChanged(state, "none", null, 0);
      setDrawMode(containerId, "none");
    }
    return;
  }

  // Circles (Evac Zone or Safe Zone)
  if (
    drawMode === "evac_circle" ||
    drawMode === "safe_circle" ||
    drawMode === "circle"
  ) {
    const isSafe = drawMode === "safe_circle";
    const color = isSafe ? "#22c55e" : "#ea580c";
    const fillColor = isSafe ? "#16a34a" : "#f97316";
    const itemType = isSafe ? "safe_circle" : "circle_zone";

    if (!state.drawState || state.drawState.step === 0) {
      // First click: center
      const centerMarker = L.circleMarker(latlng, {
        radius: 5,
        color: color,
        fillColor: color,
        fillOpacity: 1,
      }).addTo(state.drawTempLayer);

      state.drawState = {
        step: 1,
        center: [latlng.lat, latlng.lng],
        centerMarker: centerMarker,
        tempCircle: null,
        color: color,
        fillColor: fillColor,
        itemType: itemType,
      };
      notifyBlazorModeChanged(
        state,
        drawMode,
        "Move cursor to set radius, click to confirm",
        1,
      );
    } else if (state.drawState.step === 1) {
      // Second click: perimeter
      const center = state.drawState.center;
      const radiusMeters = Math.max(
        10,
        Math.round(map.distance(center, latlng)),
      );

      const item = {
        id: generateId(),
        type: itemType,
        name: getNextName(state, itemType),
        color: color,
        center: center,
        radius: radiusMeters,
        metricInfo: `Radius: ${radiusMeters} m`,
        createdAt: new Date().toISOString(),
      };
      cleanDrawState(state);
      addItem(state, item);
      notifyBlazorModeChanged(state, "none", null, 0);
      setDrawMode(containerId, "none");
    }
    return;
  }

  // Polygons (Evac Zone or Safe Zone)
  // Clicking 3rd point confirms polygon immediately, while keeping drawing mode active to allow adding points or clicking Done
  if (
    drawMode === "evac_polygon" ||
    drawMode === "safe_polygon" ||
    drawMode === "polygon"
  ) {
    const isSafe = drawMode === "safe_polygon";
    const color = isSafe ? "#22c55e" : "#ea580c";
    const fillColor = isSafe ? "#16a34a" : "#f97316";
    const itemType = isSafe ? "safe_polygon" : "polygon_zone";

    if (!state.drawState) {
      state.drawState = {
        points: [],
        pointMarkers: [],
        tempPolyline: null,
        ghostLine: null,
        color: color,
        fillColor: fillColor,
        itemType: itemType,
        activeItemId: null,
      };
    }

    const point = [latlng.lat, latlng.lng];
    state.drawState.points.push(point);
    const count = state.drawState.points.length;

    // Add a vertex marker for click feedback
    const vertexMarker = L.circleMarker(latlng, {
      radius: 5,
      color: color,
      fillColor: "#ffffff",
      fillOpacity: 1,
      weight: 2,
    }).addTo(state.drawTempLayer);
    state.drawState.pointMarkers.push(vertexMarker);

    // Clicking back on the first point marker when count >= 4 finishes polygon
    if (count >= 4) {
      const firstPt = state.drawState.points[0];
      const p1 = map.latLngToContainerPoint(firstPt);
      const p2 = map.latLngToContainerPoint(latlng);
      if (p1.distanceTo(p2) < 25) {
        // Clicked on/near first point, complete!
        state.drawState.points.pop(); // remove duplicate closing point
        if (state.drawState.activeItemId) {
          const existingItem = state.items.find(
            (i) => i.id === state.drawState.activeItemId,
          );
          if (existingItem) {
            existingItem.coordinates = [...state.drawState.points];
            existingItem.metricInfo = `${state.drawState.points.length} vertices`;
            const layer = state.shapeLayers.get(existingItem.id);
            if (layer) {
              layer.setLatLngs(existingItem.coordinates);
            }
            savePersistedItems(state);
            syncItemsWithBlazor(state);
          }
        }
        finishPolygon(containerId);
        return;
      }
    }

    if (count === 1) {
      // First point marker can be clicked to finish when count >= 3
      vertexMarker.on("click", (ev) => {
        L.DomEvent.stopPropagation(ev);
        if (state.drawState && state.drawState.points.length >= 3) {
          finishPolygon(containerId);
        }
      });
      notifyBlazorModeChanged(
        state,
        drawMode,
        "Point 1 placed. Click to place 2nd point",
        1,
      );
    } else if (count === 2) {
      // Render line connecting point 1 and 2
      if (!state.drawState.tempPolyline) {
        state.drawState.tempPolyline = L.polyline(state.drawState.points, {
          color: color,
          weight: 3,
          dashArray: "5, 5",
        }).addTo(state.drawTempLayer);
      } else {
        state.drawState.tempPolyline.setLatLngs(state.drawState.points);
      }
      notifyBlazorModeChanged(
        state,
        drawMode,
        "Point 2 placed. Click 3rd point to confirm polygon immediately",
        2,
      );
    } else if (count === 3) {
      // 3rd point immediately creates and confirms polygon!
      if (state.drawState.tempPolyline) {
        state.drawTempLayer.removeLayer(state.drawState.tempPolyline);
        state.drawState.tempPolyline = null;
      }

      const item = {
        id: generateId(),
        type: itemType,
        name: getNextName(state, itemType),
        color: color,
        coordinates: [...state.drawState.points],
        metricInfo: `3 vertices`,
        createdAt: new Date().toISOString(),
      };
      state.drawState.activeItemId = item.id;
      addItem(state, item, false); // non-interactive while drawing to pass clicks

      notifyBlazorModeChanged(
        state,
        drawMode,
        "Polygon confirmed! Click more points to expand, or click [Done] when finished",
        3,
      );
    } else {
      // count >= 4: Immediately expands the confirmed polygon!
      if (state.drawState.activeItemId) {
        const existingItem = state.items.find(
          (i) => i.id === state.drawState.activeItemId,
        );
        if (existingItem) {
          existingItem.coordinates = [...state.drawState.points];
          existingItem.metricInfo = `${count} vertices`;
          const layer = state.shapeLayers.get(existingItem.id);
          if (layer) {
            layer.setLatLngs(existingItem.coordinates);
          }
          savePersistedItems(state);
          syncItemsWithBlazor(state);
        }
      }
      notifyBlazorModeChanged(
        state,
        drawMode,
        `Polygon confirmed (${count} vertices). Click more points or [Done] to complete`,
        count,
      );
    }
  }
}

/**
 * Handles map mousemove to show live preview of shape being drawn
 */
function handleMapMouseMove(state, e) {
  const { map, drawMode, drawState, drawTempLayer } = state;
  if (!drawState) return;

  const latlng = e.latlng;

  if (drawMode === "blockade" && drawState.step === 1) {
    const start = drawState.start;
    const currentEnd = [latlng.lat, latlng.lng];

    if (!drawState.tempLine) {
      drawState.tempLine = L.polyline([start, currentEnd], {
        color: "#ef4444",
        weight: 5,
        opacity: 0.8,
        dashArray: "8, 8",
      }).addTo(drawTempLayer);
    } else {
      drawState.tempLine.setLatLngs([start, currentEnd]);
    }
  } else if (
    (drawMode === "evac_circle" ||
      drawMode === "safe_circle" ||
      drawMode === "circle") &&
    drawState.step === 1
  ) {
    const center = drawState.center;
    const radius = Math.max(10, Math.round(map.distance(center, latlng)));

    if (!drawState.tempCircle) {
      drawState.tempCircle = L.circle(center, {
        radius: radius,
        color: drawState.color || "#ea580c",
        weight: 2,
        dashArray: "6, 6",
        fillColor: drawState.fillColor || "#f97316",
        fillOpacity: 0.2,
      }).addTo(drawTempLayer);
    } else {
      drawState.tempCircle.setRadius(radius);
    }
  } else if (
    (drawMode === "evac_polygon" ||
      drawMode === "safe_polygon" ||
      drawMode === "polygon") &&
    drawState.points &&
    drawState.points.length > 0
  ) {
    const lastPt = drawState.points[drawState.points.length - 1];
    const pts = [lastPt, [latlng.lat, latlng.lng]];

    if (!drawState.ghostLine) {
      drawState.ghostLine = L.polyline(pts, {
        color: drawState.color || "#ea580c",
        weight: 2,
        dashArray: "3, 6",
        opacity: 0.7,
      }).addTo(drawTempLayer);
    } else {
      drawState.ghostLine.setLatLngs(pts);
    }
  }
}

/**
 * Completes polygon drawing and saves item
 */
export function finishPolygon(containerId) {
  const state = mapInstances.get(containerId);
  if (!state || !state.drawState) return;

  if (state.drawState.activeItemId) {
    finalizeDraftItem(state, state.drawState.activeItemId);
  } else {
    const points = state.drawState.points;
    if (!points || points.length < 3) {
      alert("A polygon zone requires at least 3 points.");
      return;
    }

    const itemType = state.drawState.itemType || "polygon_zone";
    const color = state.drawState.color || "#ea580c";

    const item = {
      id: generateId(),
      type: itemType,
      name: getNextName(state, itemType),
      color: color,
      coordinates: points,
      metricInfo: `${points.length} vertices`,
      createdAt: new Date().toISOString(),
    };

    addItem(state, item, true);
  }

  cleanDrawState(state);
  notifyBlazorModeChanged(state, "none", null, 0);
  setDrawMode(containerId, "none");
}

/**
 * Promotes a draft shape to a fully interactive shape with popup
 */
function finalizeDraftItem(state, itemId) {
  const item = state.items.find((i) => i.id === itemId);
  if (!item) return;

  const layer = state.shapeLayers.get(itemId);
  if (layer && state.itemsLayer) {
    state.itemsLayer.removeLayer(layer);
    state.shapeLayers.delete(itemId);
  }
  renderItemLayer(state, item, true);
  savePersistedItems(state);
  syncItemsWithBlazor(state);
}

/**
 * Cancels active drawing and clears ghost elements
 */
export function cancelCurrentDrawing(containerId) {
  const state = mapInstances.get(containerId);
  if (!state) return;

  if (state.drawState && state.drawState.activeItemId) {
    deleteMapItem(containerId, state.drawState.activeItemId);
  }
  cleanDrawState(state);
  notifyBlazorModeChanged(state, "none", null, 0);
  setDrawMode(containerId, "none");
}

/**
 * Sets current active draw mode
 * @param {string} containerId
 * @param {string} mode - 'none' | 'evac_circle' | 'evac_polygon' | 'safe_circle' | 'safe_polygon' | 'blockade'
 */
export function setDrawMode(containerId, mode) {
  const state = mapInstances.get(containerId);
  if (!state) return;

  if (state.drawState && state.drawState.activeItemId) {
    finalizeDraftItem(state, state.drawState.activeItemId);
  }

  cleanDrawState(state);
  state.drawMode = mode || "none";

  if (state.map) {
    if (
      state.drawMode === "evac_polygon" ||
      state.drawMode === "safe_polygon" ||
      state.drawMode === "polygon"
    ) {
      state.map.doubleClickZoom.disable();
    } else {
      state.map.doubleClickZoom.enable();
    }
  }

  const container = document.getElementById(containerId);
  if (container) {
    if (state.drawMode !== "none") {
      container.classList.add("drawing-mode-active");
    } else {
      container.classList.remove("drawing-mode-active");
    }
  }

  let hint = null;
  if (mode === "evac_circle")
    hint = "Click map to place center of Evacuation Zone";
  else if (mode === "evac_polygon")
    hint = "Click points on map to build Evac Polygon (immediate preview)";
  else if (mode === "safe_circle")
    hint = "Click map to place center of Safe Zone";
  else if (mode === "safe_polygon")
    hint = "Click points on map to build Safe Polygon (immediate preview)";
  else if (mode === "blockade") hint = "Click map to set start of Blockade";

  notifyBlazorModeChanged(state, mode, hint, 0);
}

/**
 * Clears temporary drawing ghost objects
 */
function cleanDrawState(state) {
  if (state.drawTempLayer) {
    state.drawTempLayer.clearLayers();
  }
  state.drawState = null;
}

/**
 * Add an item, render its layer, persist, and notify Blazor
 */
function addItem(state, item, interactive = true) {
  state.items.push(item);
  renderItemLayer(state, item, interactive);
  savePersistedItems(state);
  syncItemsWithBlazor(state);
}

/**
 * Renders a shape onto the map and attaches interactive popup
 */
function renderItemLayer(state, item, interactive = true) {
  const { itemsLayer, containerId } = state;
  let layer = null;

  if (
    (item.type === "circle_zone" || item.type === "safe_circle") &&
    item.center &&
    item.radius
  ) {
    const isSafe = item.type === "safe_circle";
    layer = L.circle(item.center, {
      radius: item.radius,
      color: item.color || (isSafe ? "#22c55e" : "#ea580c"),
      weight: 3,
      fillColor: isSafe ? "#16a34a" : "#f97316",
      fillOpacity: 0.25,
      interactive: interactive,
    });
  } else if (
    (item.type === "polygon_zone" || item.type === "safe_polygon") &&
    item.coordinates
  ) {
    const isSafe = item.type === "safe_polygon";
    layer = L.polygon(item.coordinates, {
      color: item.color || (isSafe ? "#22c55e" : "#ea580c"),
      weight: 3,
      fillColor: isSafe ? "#16a34a" : "#f97316",
      fillOpacity: 0.25,
      interactive: interactive,
    });
  } else if (item.type === "blockade" && item.startPoint && item.endPoint) {
    layer = L.polyline([item.startPoint, item.endPoint], {
      color: item.color || "#ef4444",
      weight: 6,
      opacity: 0.95,
      interactive: interactive,
    });
  } else if (item.type === "safe_point" && item.position) {
    const safeIcon = L.divIcon({
      className: "custom-safe-point-marker",
      html: `
                <div class="safe-pin">
                    <span class="safe-ping"></span>
                    <div class="safe-icon-inner">
                        <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="#ffffff" stroke-width="2.5" stroke-linecap="round" stroke-linejoin="round">
                            <path d="M12 22s8-4 8-10V5l-8-3-8 3v7c0 6 8 10 8 10z"/>
                        </svg>
                    </div>
                </div>
            `,
      iconSize: [32, 32],
      iconAnchor: [16, 16],
    });

    layer = L.marker(item.position, {
      icon: safeIcon,
      interactive: interactive,
    });
  }

  if (layer) {
    if (interactive) {
      const popupContent = createPopupHtml(containerId, item);
      layer.bindPopup(popupContent, {
        className: "plansafe-leaflet-popup",
        maxWidth: 240,
      });
    }

    itemsLayer.addLayer(layer);
    state.shapeLayers.set(item.id, layer);
  }
}

/**
 * Creates HTML string for shape popup
 */
function createPopupHtml(containerId, item) {
  let typeName = "Evacuation Zone";
  let typeClass = "badge-zone";
  if (item.type === "blockade") {
    typeName = "Blockade";
    typeClass = "badge-blockade";
  } else if (
    item.type === "safe_circle" ||
    item.type === "safe_polygon" ||
    item.type === "safe_point"
  ) {
    typeName = "Safe Location";
    typeClass = "badge-safe";
  }

  return `
        <div class="map-popup-card">
            <div class="map-popup-header">
                <span class="map-popup-badge ${typeClass}">${typeName}</span>
                <strong class="map-popup-title">${item.name}</strong>
            </div>
            ${item.metricInfo ? `<div class="map-popup-meta">${item.metricInfo}</div>` : ""}
            <div class="map-popup-actions">
                <button type="button" class="btn-popup-delete" onclick="window.PlanSafeMap.deleteItem('${containerId}', '${item.id}')">
                    <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">
                        <polyline points="3 6 5 6 21 6"></polyline>
                        <path d="M19 6v14a2 2 0 0 1-2 2H7a2 2 0 0 1-2-2V6m3 0V4a2 2 0 0 1 2-2h4a2 2 0 0 1 2 2v2"></path>
                    </svg>
                    Remove
                </button>
            </div>
        </div>
    `;
}

/**
 * Deletes an item by id
 */
export function deleteMapItem(containerId, itemId) {
  const state = mapInstances.get(containerId);
  if (!state) return;

  const layer = state.shapeLayers.get(itemId);
  if (layer) {
    state.itemsLayer.removeLayer(layer);
    state.shapeLayers.delete(itemId);
  }

  state.items = state.items.filter((i) => i.id !== itemId);
  savePersistedItems(state);
  syncItemsWithBlazor(state);
}

/**
 * Clears all user items
 */
export function clearAllMapItems(containerId) {
  const state = mapInstances.get(containerId);
  if (!state) return;

  state.itemsLayer.clearLayers();
  state.shapeLayers.clear();
  state.items = [];
  savePersistedItems(state);
  syncItemsWithBlazor(state);
}

/**
 * Pans map camera to specific item
 */
export function panToMapItem(containerId, itemId) {
  const state = mapInstances.get(containerId);
  if (!state) return;

  const layer = state.shapeLayers.get(itemId);
  if (!layer) return;

  if (layer.getBounds) {
    state.map.flyToBounds(layer.getBounds(), {
      padding: [50, 50],
      maxZoom: 16,
      duration: 1,
    });
  } else if (layer.getLatLng) {
    state.map.flyTo(layer.getLatLng(), 16, { duration: 1 });
  }

  layer.openPopup();
}

/**
 * Loads a complete session's items into the map, replacing previous session shapes.
 * @param {string} containerId
 * @param {string|Array} itemsData
 * @param {object} options - Optional { lat, lng, zoom }
 */
export function loadSessionItems(containerId, itemsData, options = {}) {
  const state = mapInstances.get(containerId);
  if (!state) return;

  cleanDrawState(state);

  if (state.itemsLayer) {
    state.itemsLayer.clearLayers();
  }
  state.shapeLayers.clear();

  let items = [];
  if (typeof itemsData === "string") {
    try {
      items = JSON.parse(itemsData);
    } catch (e) {
      console.error("[mapInterop] Failed parsing session items:", e);
    }
  } else if (Array.isArray(itemsData)) {
    items = itemsData;
  }

  state.items = items || [];

  state.items.forEach((item) => {
    renderItemLayer(state, item);
  });

  if (options && options.lat && options.lng && state.map) {
    const zoom = options.zoom || state.map.getZoom();
    state.map.flyTo([options.lat, options.lng], zoom, { duration: 0.8 });
  }

  savePersistedItems(state);
  syncItemsWithBlazor(state);
}

/**
 * Generates unique ID
 */
function generateId() {
  return (
    "item_" +
    Math.random().toString(36).substring(2, 9) +
    "_" +
    Date.now().toString(36)
  );
}

/**
 * Auto-names items: Evac Zone 1, Safe Zone 1, Blockade 1, etc.
 */
function getNextName(state, type) {
  const items = state.items;
  if (type === "circle_zone" || type === "polygon_zone") {
    const count =
      items.filter((i) => i.type === "circle_zone" || i.type === "polygon_zone")
        .length + 1;
    return `Evac Zone ${count}`;
  }
  if (
    type === "safe_circle" ||
    type === "safe_polygon" ||
    type === "safe_point"
  ) {
    const count =
      items.filter(
        (i) =>
          i.type === "safe_circle" ||
          i.type === "safe_polygon" ||
          i.type === "safe_point",
      ).length + 1;
    return `Safe Zone ${count}`;
  }
  if (type === "blockade") {
    const count = items.filter((i) => i.type === "blockade").length + 1;
    return `Blockade ${count}`;
  }
  return `Item ${items.length + 1}`;
}

/**
 * Persistence: Load from localStorage
 */
function loadPersistedItems(state) {
  try {
    const raw = localStorage.getItem(STORAGE_KEY);
    if (raw) {
      const parsed = JSON.parse(raw);
      if (Array.isArray(parsed)) {
        state.items = parsed;
        parsed.forEach((item) => {
          renderItemLayer(state, item);
        });
        syncItemsWithBlazor(state);
      }
    }
  } catch (err) {
    console.error("[mapInterop] Error loading items from localStorage:", err);
  }
}

/**
 * Persistence: Save to localStorage
 */
function savePersistedItems(state) {
  try {
    localStorage.setItem(STORAGE_KEY, JSON.stringify(state.items));
  } catch (err) {
    console.error("[mapInterop] Error saving items to localStorage:", err);
  }
}

/**
 * Sync items array to Blazor component
 */
function syncItemsWithBlazor(state) {
  if (state.dotNetRef) {
    try {
      state.dotNetRef.invokeMethodAsync(
        "OnItemsUpdated",
        JSON.stringify(state.items),
      );
    } catch (e) {
      // Circuit might be transitioning
    }
  }
}

/**
 * Notify Blazor about active drawing mode, prompt hint, and current vertex count
 */
function notifyBlazorModeChanged(state, mode, hint, pointCount = 0) {
  if (state.dotNetRef) {
    try {
      state.dotNetRef.invokeMethodAsync(
        "OnDrawModeChanged",
        mode,
        hint,
        pointCount,
      );
    } catch (e) {
      // Circuit might be transitioning
    }
  }
}

/**
 * Manually trigger map size invalidation
 */
export function invalidateSize(containerId) {
  const entry = mapInstances.get(containerId);
  if (entry && entry.map) {
    entry.map.invalidateSize();
  }
}

/**
 * Recenter map to Kraków city center
 */
export function centerOnKrakow(containerId) {
  const entry = mapInstances.get(containerId);
  if (entry && entry.map) {
    entry.map.flyTo([50.0614, 19.9366], 14, { duration: 1.2 });
  }
}

/**
 * Disposes the map instance and cleans listeners
 */
export function disposeMap(containerId) {
  const entry = mapInstances.get(containerId);
  if (entry) {
    entry.dotNetRef = null;
    entry.disposePreparationCursor?.();
    if (entry._keydownHandler) {
      window.removeEventListener("keydown", entry._keydownHandler);
    }
    if (entry.resizeObserver) {
      entry.resizeObserver.disconnect();
    }
    if (entry.map) {
      entry.map.remove();
    }
    mapInstances.delete(containerId);
  }
}

/**
 * Visualizes GUS NSP 2021 125m grid cells on the map (called when simulation/evacuation starts).
 * @param {string} containerId - Map container ID
 * @param {Array} cells - List of GusGridCell objects
 */
export function renderGusGrid(containerId, cells) {
  const entry = mapInstances.get(containerId);
  if (!entry || !entry.gusGridLayer) return;
  entry.gusGridLayer.clearLayers();
  if (!cells || cells.length === 0) return;

  for (let i = 0; i < cells.length; i++) {
    const c = cells[i];
    const bounds = [
      [c.minLat, c.minLng],
      [c.maxLat, c.maxLng],
    ];
    const rect = L.rectangle(bounds, {
      color: "#3b82f6",
      weight: 1,
      fillColor: "#3b82f6",
      fillOpacity: Math.min(0.55, Math.max(0.08, c.pop / 150.0)),
      interactive: true,
    });
    rect.bindTooltip(
      `<strong>GUS 125m Grid</strong><br>Population: ${c.pop} residents`,
      { sticky: true },
    );
    entry.gusGridLayer.addLayer(rect);
  }
}

/**
 * Clears GUS 125m grid overlay from the map.
 */
export function clearGusGrid(containerId) {
  const entry = mapInstances.get(containerId);
  if (entry && entry.gusGridLayer) {
    entry.gusGridLayer.clearLayers();
  }
}

/**
 * Visualizes individual 1:1 occupant agents on the map (called when simulation/evacuation starts).
 * @param {string} containerId - Map container ID
 * @param {Array} agents - List of OccupantAgent objects
 */
export function renderOccupants(containerId, agents) {
  const entry = mapInstances.get(containerId);
  if (!entry || !entry.occupantsLayer) return;
  entry.occupantsLayer.clearLayers();
  if (!agents || agents.length === 0) return;

  for (let i = 0; i < agents.length; i++) {
    const a = agents[i];
    const marker = L.circleMarker([a.latitude, a.longitude], {
      radius: 3,
      fillColor: "#f97316",
      color: "#ffffff",
      weight: 0.5,
      fillOpacity: 0.85,
    });
    entry.occupantsLayer.addLayer(marker);
  }
}

/**
 * Clears occupant agents from the map.
 */
export function clearOccupants(containerId) {
  const entry = mapInstances.get(containerId);
  if (entry && entry.occupantsLayer) {
    entry.occupantsLayer.clearLayers();
  }
}
