// Citizen Map Interop for Mobile Evacuation Assistant
// Reuses Leaflet instance to display citizen location, shelters, roadblocks, alarm zones, and active route.

const citizenMaps = new Map();

/**
 * Initializes the mobile citizen evacuation map.
 */
export function initCitizenMap(containerId, centerLat = 50.0614, centerLng = 19.9366, zoom = 15, dotNetRef = null) {
    const container = document.getElementById(containerId);
    if (!container) {
        console.warn(`[citizenMapInterop] Container #${containerId} not found.`);
        return false;
    }

    if (citizenMaps.has(containerId)) {
        disposeCitizenMap(containerId);
    }

    const map = L.map(container, {
        center: [centerLat, centerLng],
        zoom: zoom,
        zoomControl: true,
        scrollWheelZoom: true,
        touchZoom: true,
        dragging: true,
        tap: true
    });

    // Dark-friendly OpenStreetMap tile layer
    L.tileLayer('https://tile.openstreetmap.org/{z}/{x}/{y}.png', {
        maxZoom: 19,
        attribution: '&copy; OpenStreetMap contributors'
    }).addTo(map);

    map.zoomControl.setPosition('topright');

    const zonesLayer = L.layerGroup().addTo(map);
    const safeZonesLayer = L.layerGroup().addTo(map);
    const roadblocksLayer = L.layerGroup().addTo(map);
    const routeLayer = L.layerGroup().addTo(map);
    const sheltersLayer = L.layerGroup().addTo(map);
    const citizenLayer = L.layerGroup().addTo(map);

    // Click handler to allow manual citizen repositioning
    map.on('click', (e) => {
        if (dotNetRef) {
            dotNetRef.invokeMethodAsync('OnCitizenMapClicked', e.latlng.lat, e.latlng.lng);
        }
    });

    let resizeObserver = null;
    if (window.ResizeObserver) {
        resizeObserver = new ResizeObserver(() => {
            map.invalidateSize();
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
        hasInitialFit: false
    });

    setTimeout(() => {
        map.invalidateSize();
    }, 150);

    return true;
}

/**
 * Updates citizen location, shelters, roadblocks, and route polyline.
 */
export function updateCitizenMap(containerId, citizenLat, citizenLng, accuracyMeters, sheltersJson, selectedShelterId, roadblocksJson, routeCoordsJson, evacZonesJson, safeZonesJson) {
    const state = citizenMaps.get(containerId);
    if (!state) return;

    const { map, citizenLayer, sheltersLayer, roadblocksLayer, routeLayer, zonesLayer, safeZonesLayer } = state;

    // 1. Update Evacuation Zones
    zonesLayer.clearLayers();
    if (evacZonesJson) {
        try {
            const zones = JSON.parse(evacZonesJson);
            for (const zone of zones) {
                if (Array.isArray(zone) && zone.length >= 3) {
                    const latlngs = zone.map(pt => {
                        if (Array.isArray(pt)) return [Number(pt[0]), Number(pt[1])];
                        return [
                            Number(pt.latitude ?? pt.Latitude ?? pt.lat ?? pt.Lat ?? pt.y ?? pt.Y),
                            Number(pt.longitude ?? pt.Longitude ?? pt.lng ?? pt.Lng ?? pt.x ?? pt.X)
                        ];
                    }).filter(p => !isNaN(p[0]) && !isNaN(p[1]));

                    if (latlngs.length >= 3) {
                        L.polygon(latlngs, {
                            color: '#f97316',
                            weight: 2.5,
                            dashArray: '4, 4',
                            fillColor: '#f97316',
                            fillOpacity: 0.15
                        }).addTo(zonesLayer);
                    }
                }
            }
        } catch (e) {
            console.warn('[citizenMapInterop] Error parsing evac zones:', e);
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
                        const latlngs = zone.map(pt => {
                            if (Array.isArray(pt)) return [Number(pt[0]), Number(pt[1])];
                            return [
                                Number(pt.latitude ?? pt.Latitude ?? pt.lat ?? pt.Lat ?? pt.y ?? pt.Y),
                                Number(pt.longitude ?? pt.Longitude ?? pt.lng ?? pt.Lng ?? pt.x ?? pt.X)
                            ];
                        }).filter(p => !isNaN(p[0]) && !isNaN(p[1]));

                        if (latlngs.length >= 3) {
                            L.polygon(latlngs, {
                                color: '#10b981',
                                weight: 2.5,
                                dashArray: '5, 5',
                                fillColor: '#10b981',
                                fillOpacity: 0.18
                            }).bindTooltip("<b>Strefa Bezpieczna</b>", { sticky: true }).addTo(safeZonesLayer);
                        }
                    }
                }
            } catch (e) {
                console.warn('[citizenMapInterop] Error parsing safe zones:', e);
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
                    const latlngs = rb.map(pt => {
                        if (Array.isArray(pt)) return [Number(pt[0]), Number(pt[1])];
                        return [
                            Number(pt.latitude ?? pt.Latitude ?? pt.lat ?? pt.Lat ?? pt.y ?? pt.Y),
                            Number(pt.longitude ?? pt.Longitude ?? pt.lng ?? pt.Lng ?? pt.x ?? pt.X)
                        ];
                    }).filter(p => !isNaN(p[0]) && !isNaN(p[1]));

                    if (latlngs.length >= 2) {
                        L.polyline(latlngs, {
                            color: '#ef4444',
                            weight: 5,
                            dashArray: '8, 8',
                            opacity: 0.95
                        }).addTo(roadblocksLayer);
                    }
                }
            }
        } catch (e) {
            console.warn('[citizenMapInterop] Error parsing roadblocks:', e);
        }
    }

    // 3. Update Route Polyline
    routeLayer.clearLayers();
    if (routeCoordsJson) {
        try {
            const route = JSON.parse(routeCoordsJson);
            if (Array.isArray(route) && route.length >= 2) {
                const latlngs = route.map(pt => {
                    if (Array.isArray(pt)) return [Number(pt[0]), Number(pt[1])];
                    return [
                        Number(pt.latitude ?? pt.Latitude ?? pt.lat ?? pt.Lat ?? pt.y ?? pt.Y),
                        Number(pt.longitude ?? pt.Longitude ?? pt.lng ?? pt.Lng ?? pt.x ?? pt.X)
                    ];
                }).filter(p => !isNaN(p[0]) && !isNaN(p[1]) && p[0] !== 0 && p[1] !== 0);

                if (latlngs.length >= 2) {
                    L.polyline(latlngs, {
                        color: '#38bdf8',
                        weight: 5,
                        dashArray: '6, 8',
                        opacity: 0.95,
                        lineJoin: 'round'
                    }).addTo(routeLayer);
                }
            }
        } catch (e) {
            console.warn('[citizenMapInterop] Error parsing route:', e);
        }
    }

    // 4. Update Shelters (Always rendered on map)
    sheltersLayer.clearLayers();
    if (sheltersJson) {
        try {
            const shelters = JSON.parse(sheltersJson);
            for (const s of shelters) {
                const sLat = Number(s.latitude ?? s.Latitude ?? s.lat ?? s.Lat ?? s.y ?? s.Y);
                const sLng = Number(s.longitude ?? s.Longitude ?? s.lng ?? s.Lng ?? s.x ?? s.X);
                if (isNaN(sLat) || isNaN(sLng) || sLat === 0 || sLng === 0) continue;

                const sId = s.id ?? s.Id ?? '';
                const sName = s.name ?? s.Name ?? 'Schron Ewakuacyjny';
                const sCap = Number(s.capacity ?? s.Capacity ?? 500);
                const sOcc = Number(s.currentOccupancy ?? s.CurrentOccupancy ?? s.peopleCount ?? s.PeopleCount ?? 0);

                const isSelected = selectedShelterId && String(sId).toLowerCase() === String(selectedShelterId).toLowerCase();
                const isFull = sCap > 0 && sOcc >= sCap;
                const occPct = sCap > 0 ? Math.round((sOcc / sCap) * 100) : 0;

                const color = isSelected ? '#0ea5e9' : (isFull ? '#ef4444' : (occPct >= 70 ? '#f59e0b' : '#10b981'));
                const halo = isSelected 
                    ? `box-shadow: 0 0 0 6px rgba(14, 165, 233, 0.45), 0 0 20px rgba(14, 165, 233, 0.8); border: 2.5px solid #ffffff;` 
                    : `box-shadow: 0 2px 8px rgba(0,0,0,0.5); border: 1.5px solid rgba(255,255,255,0.85);`;

                const markerHtml = `
                    <div style="transform: translate(-50%, -50%); display: flex; flex-direction: column; align-items: center; pointer-events: auto; cursor: pointer;">
                        <div style="background-color: ${color}; width: 32px; height: 32px; border-radius: 50%; ${halo} display: flex; align-items: center; justify-content: center; color: white; font-weight: bold; font-size: 15px;">
                            ${isSelected ? '★' : '⌂'}
                        </div>
                        <div style="background-color: rgba(15, 23, 42, 0.88); color: #ffffff; padding: 2px 7px; border-radius: 4px; font-size: 11px; font-weight: 600; margin-top: 3px; white-space: nowrap; border: 1px solid rgba(255,255,255,0.2); box-shadow: 0 2px 6px rgba(0,0,0,0.4);">
                            ${sName} (${occPct}%)
                        </div>
                    </div>
                `;

                const icon = L.divIcon({
                    html: markerHtml,
                    className: 'custom-shelter-marker',
                    iconSize: [34, 46],
                    iconAnchor: [17, 23]
                });

                const marker = L.marker([sLat, sLng], { icon: icon }).addTo(sheltersLayer);
                marker.bindPopup(`<strong>${sName}</strong><br/>Obłożenie: ${sOcc} / ${sCap} (${occPct}%)<br/><small style="color:#0ea5e9;">Kliknij, aby wybrać ten schron</small>`);

                if (state.dotNetRef) {
                    marker.on('click', () => {
                        state.dotNetRef.invokeMethodAsync('OnShelterSelected', String(sId));
                    });
                }

                if (isSelected) {
                    L.circleMarker([sLat, sLng], {
                        radius: 26,
                        color: '#38bdf8',
                        weight: 2,
                        dashArray: '4, 4',
                        fill: false,
                        interactive: false
                    }).addTo(sheltersLayer);
                }
            }
        } catch (e) {
            console.warn('[citizenMapInterop] Error parsing shelters:', e);
        }
    }

    // 5. Update Citizen Location Marker
    citizenLayer.clearLayers();
    if (citizenLat && citizenLng) {
        // Radar pulse marker
        const citizenHtml = `
            <div style="position: relative; width: 24px; height: 24px; transform: translate(-50%, -50%);">
                <div style="position: absolute; width: 24px; height: 24px; border-radius: 50%; background: rgba(56, 189, 248, 0.3); animation: citizen-radar-pulse 1.8s infinite ease-out;"></div>
                <div style="position: absolute; top: 4px; left: 4px; width: 16px; height: 16px; border-radius: 50%; background: #0284c7; border: 2px solid #ffffff; box-shadow: 0 0 8px rgba(2, 132, 199, 0.8);"></div>
            </div>
            <style>
                @keyframes citizen-radar-pulse {
                    0% { transform: scale(0.6); opacity: 0.9; }
                    100% { transform: scale(2.6); opacity: 0; }
                }
            </style>
        `;

        const citizenIcon = L.divIcon({
            html: citizenHtml,
            className: 'custom-citizen-marker',
            iconSize: [24, 24],
            iconAnchor: [12, 12]
        });

        L.marker([citizenLat, citizenLng], { icon: citizenIcon, zIndexOffset: 1000 }).addTo(citizenLayer);

        if (accuracyMeters && accuracyMeters > 0 && accuracyMeters < 500) {
            L.circle([citizenLat, citizenLng], {
                radius: accuracyMeters,
                color: '#38bdf8',
                weight: 1,
                fillColor: '#38bdf8',
                fillOpacity: 0.08
            }).addTo(citizenLayer);
        }
    }

    // 6. Auto-fit bounds on initial load so citizen AND all shelters/safe zones are in view
    if (!state.hasInitialFit) {
        if (sheltersLayer.getLayers().length > 0 || citizenLayer.getLayers().length > 0) {
            fitCitizenBounds(containerId);
            state.hasInitialFit = true;
        }
    }
}

/**
 * Fits map viewport bounds to enclose citizen, all shelters, safe zones, and route.
 */
export function fitCitizenBounds(containerId) {
    const state = citizenMaps.get(containerId);
    if (!state) return;
    const { map, sheltersLayer, citizenLayer, safeZonesLayer, routeLayer } = state;

    const layers = [];
    if (sheltersLayer && sheltersLayer.getLayers().length > 0) layers.push(sheltersLayer);
    if (citizenLayer && citizenLayer.getLayers().length > 0) layers.push(citizenLayer);
    if (safeZonesLayer && safeZonesLayer.getLayers().length > 0) layers.push(safeZonesLayer);
    if (routeLayer && routeLayer.getLayers().length > 0) layers.push(routeLayer);

    if (layers.length === 0) return;

    try {
        const group = L.featureGroup(layers);
        const bounds = group.getBounds();
        if (bounds.isValid()) {
            map.fitBounds(bounds, {
                padding: [45, 45],
                maxZoom: 16
            });
        }
    } catch (e) {
        console.warn('[citizenMapInterop] fitCitizenBounds error:', e);
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
        state.map.invalidateSize();
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
