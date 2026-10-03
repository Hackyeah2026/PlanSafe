// Leaflet OpenStreetMap Interop Module for PlanSafe / CrowdSim
// Supports desktop & mobile, zoom controls, wheel scrolling, resize observation, and clean disposal

const mapInstances = new Map();

/**
 * Initializes a Leaflet map in the specified container, centered on Kraków.
 * @param {string} containerId - DOM ID of the container element
 * @param {object} options - Map configuration options
 * @param {object} dotNetRef - Optional DotNetObjectReference for future callbacks
 */
export function initMap(containerId, options = {}, dotNetRef = null) {
    const container = document.getElementById(containerId);
    if (!container) {
        console.warn(`[mapInterop] Container #${containerId} not found.`);
        return false;
    }

    // If map already initialized on this container, clean it up first
    if (mapInstances.has(containerId)) {
        disposeMap(containerId);
    }

    // Kraków Center coordinates (Rynek Główny)
    const defaultCenter = [50.0614, 19.9366];
    const defaultZoom = 14;

    const center = (options.lat && options.lng) ? [options.lat, options.lng] : defaultCenter;
    const zoom = options.zoom || defaultZoom;

    // Create Leaflet map instance
    const map = L.map(container, {
        center: center,
        zoom: zoom,
        zoomControl: true,
        scrollWheelZoom: true,
        touchZoom: true,
        dragging: true,
        tap: true
    });

    // Add OpenStreetMap Standard Tile Layer
    L.tileLayer('https://tile.openstreetmap.org/{z}/{x}/{y}.png', {
        maxZoom: 19,
        attribution: '&copy; <a href="https://www.openstreetmap.org/copyright" target="_blank" rel="noopener">OpenStreetMap</a> contributors'
    }).addTo(map);

    // Position the zoom controls
    map.zoomControl.setPosition('topleft');

    // Setup ResizeObserver to ensure tiles re-render smoothly during panel collapse / mobile orientation changes
    let resizeObserver = null;
    if (window.ResizeObserver) {
        resizeObserver = new ResizeObserver(() => {
            map.invalidateSize();
        });
        resizeObserver.observe(container);
    }

    mapInstances.set(containerId, {
        map: map,
        resizeObserver: resizeObserver,
        dotNetRef: dotNetRef
    });

    // Trigger initial size invalidation after a brief tick to ensure container styles have computed
    requestAnimationFrame(() => {
        map.invalidateSize();
    });

    return true;
}

/**
 * Manually trigger map size invalidation (e.g., when side panel is toggled)
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
 * Disposes the map instance and disconnects observers
 */
export function disposeMap(containerId) {
    const entry = mapInstances.get(containerId);
    if (entry) {
        if (entry.resizeObserver) {
            entry.resizeObserver.disconnect();
        }
        if (entry.map) {
            entry.map.remove();
        }
        mapInstances.delete(containerId);
    }
}
