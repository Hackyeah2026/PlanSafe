import { GpuSimulationEngine, isWebGpuSupported } from "./crowdSimulatorGpu.js";

export function checkWebGpuSupport() {
    return isWebGpuSupported();
}

export function initSimulator(canvasRef, mapContainerId = null) {
    const canvas = canvasRef;
    const isMapMode = !!mapContainerId;
    const ctx = canvas.getContext('2d', { alpha: isMapMode });

    // --- World & Camera State ---
    let worldWidth = 200.0;   // in meters
    let worldHeight = 200.0;  // in meters
    let obstacles = [];
    let targets = [];
    let weightDistance = 1.0;
    let weightOccupancy = 0.0;
    let exitZone = { x: 184, y: 80, width: 12, height: 40 };

    let scale = 1.0;          // pixels per meter
    let scaleX = 1.0;
    let scaleY = 1.0;
    let offsetX = 0;          // translation X in canvas pixels
    let offsetY = 0;          // translation Y in canvas pixels
    let isInteractive = !isMapMode;

    // --- Geographic Map Bounds (for Map Overlay Mode) ---
    let leafletMap = null;
    let originLat = null;
    let originLng = null;
    let minLat = null;
    let maxLng = null;

    function updateLeafletProjection() {
        if (!leafletMap && mapContainerId) {
            leafletMap = window.PlanSafeMap?.getMap(mapContainerId);
            if (leafletMap) {
                leafletMap.on('move', onMapSync);
                leafletMap.on('zoom', onMapSync);
                leafletMap.on('viewreset', onMapSync);
                leafletMap.on('resize', onMapSync);
            }
        }
        if (!leafletMap || originLat == null || originLng == null || minLat == null || maxLng == null) return;
        if (canvas.width !== canvas.clientWidth || canvas.height !== canvas.clientHeight) {
            canvas.width = canvas.clientWidth;
            canvas.height = canvas.clientHeight;
        }
        const pNW = leafletMap.latLngToContainerPoint([originLat, originLng]);
        const pSE = leafletMap.latLngToContainerPoint([minLat, maxLng]);
        scaleX = (pSE.x - pNW.x) / Math.max(worldWidth, 1.0);
        scaleY = (pSE.y - pNW.y) / Math.max(worldHeight, 1.0);
        scale = Math.min(Math.abs(scaleX), Math.abs(scaleY));
        offsetX = pNW.x;
        offsetY = pNW.y;
    }

    function onMapSync() {
        updateLeafletProjection();
        redrawCurrent();
    }

    if (isMapMode) {
        updateLeafletProjection();
    }

    // --- Heatmap / Metric Grid Setup ---
    const gridCols = 180;
    const gridRows = 180;
    let offscreenCanvas = document.createElement('canvas');
    offscreenCanvas.width = gridCols;
    offscreenCanvas.height = gridRows;
    let offCtx = offscreenCanvas.getContext('2d');

    let trailCanvas = document.createElement('canvas');
    let trailCtx = null;

    let speedSum = new Float32Array(gridCols * gridRows);
    let weightSum = new Float32Array(gridCols * gridRows);
    let vxSum = new Float32Array(gridCols * gridRows);
    let vySum = new Float32Array(gridCols * gridRows);

    let trackedMinSpeed = 0.1;
    let trackedMaxSpeed = 2.0;
    let trackedMaxDensity = 5.0;

    // --- Last Render State for Instant Redraw (Pan, Zoom, Resize) ---
    let lastFloatArray = null;
    let lastWasGpu = false;
    let lastCount = 0;
    let lastRenderMode = 'agents';
    let lastShowWhiskers = false;
    let lastWhiskerLength = 2.5;
    let lastGranulation = 1;
    let lastShowFlowParticles = true;

    // --- Vector Field Flow Particles Setup ---
    let flowParticles = [];
    const numFlowParticles = 420;
    const maxTrailPoints = 8;

    function createFlowParticle(floatArray, count) {
        const p = {
            x: 0,
            y: 0,
            trail: [],
            age: 0,
            maxLife: 35 + Math.floor(Math.random() * 35)
        };
        respawnFlowParticle(p, floatArray, count);
        // Stagger initial age across lifetime so particles don't expire simultaneously
        p.age = Math.floor(Math.random() * p.maxLife);
        return p;
    }

    function respawnFlowParticle(p, floatArray, count) {
        p.age = 0;
        p.maxLife = 35 + Math.floor(Math.random() * 35);
        p.trail = [];

        let found = false;
        // Prefer spawning directly on an actively moving agent
        if (floatArray && count > 0 && floatArray.length >= count * 5) {
            for (let attempt = 0; attempt < 12; attempt++) {
                const idx = Math.floor(Math.random() * count);
                const u = floatArray[idx * 5 + 2];
                const v = floatArray[idx * 5 + 3];
                if (u * u + v * v > 0.04) {
                    const jitterX = (Math.random() - 0.5) * 4.0;
                    const jitterY = (Math.random() - 0.5) * 4.0;
                    p.x = Math.max(0, Math.min(worldWidth, floatArray[idx * 5] + jitterX));
                    p.y = Math.max(0, Math.min(worldHeight, floatArray[idx * 5 + 1] + jitterY));
                    found = true;
                    break;
                }
            }
        }

        if (!found) {
            // Fallback: spawn in world bounds
            p.x = Math.random() * worldWidth;
            p.y = Math.random() * worldHeight;
        }

        // Pre-seed trail backwards along local flow vector so particle never appears as a single dot
        p.trail = [{ x: p.x, y: p.y }];
        let curX = p.x;
        let curY = p.y;
        for (let step = 0; step < 5; step++) {
            const vel = sampleVelocity(curX, curY);
            if (!vel) break;
            const sp = Math.hypot(vel.vx, vel.vy);
            if (sp < 0.05) break;
            const stepD = Math.max(0.4, Math.min(1.4, sp * 0.7));
            curX -= (vel.vx / sp) * stepD;
            curY -= (vel.vy / sp) * stepD;
            if (curX < 0 || curX > worldWidth || curY < 0 || curY > worldHeight) break;
            p.trail.unshift({ x: curX, y: curY });
        }
    }

    function initFlowParticles() {
        flowParticles = [];
        for (let i = 0; i < numFlowParticles; i++) {
            flowParticles.push(createFlowParticle(lastFloatArray, lastCount));
        }
    }

    function sampleVelocity(wx, wy) {
        const gx = (wx / worldWidth) * gridCols - 0.5;
        const gy = (wy / worldHeight) * gridRows - 0.5;
        if (gx < 0 || gx >= gridCols - 1 || gy < 0 || gy >= gridRows - 1) return null;

        const x0 = Math.floor(gx);
        const y0 = Math.floor(gy);
        const x1 = x0 + 1;
        const y1 = y0 + 1;
        const fx = gx - x0;
        const fy = gy - y0;

        const i00 = y0 * gridCols + x0;
        const i10 = y0 * gridCols + x1;
        const i01 = y1 * gridCols + x0;
        const i11 = y1 * gridCols + x1;

        const w00 = weightSum[i00], w10 = weightSum[i10], w01 = weightSum[i01], w11 = weightSum[i11];
        const totalW = w00 + w10 + w01 + w11;
        if (totalW < 0.01) return null;

        const vx00 = w00 > 0.001 ? vxSum[i00] / w00 : 0;
        const vx10 = w10 > 0.001 ? vxSum[i10] / w10 : 0;
        const vx01 = w01 > 0.001 ? vxSum[i01] / w01 : 0;
        const vx11 = w11 > 0.001 ? vxSum[i11] / w11 : 0;

        const vy00 = w00 > 0.001 ? vySum[i00] / w00 : 0;
        const vy10 = w10 > 0.001 ? vySum[i10] / w10 : 0;
        const vy01 = w01 > 0.001 ? vySum[i01] / w01 : 0;
        const vy11 = w11 > 0.001 ? vySum[i11] / w11 : 0;

        const vx = (vx00 * (1 - fx) + vx10 * fx) * (1 - fy) + (vx01 * (1 - fx) + vx11 * fx) * fy;
        const vy = (vy00 * (1 - fx) + vy10 * fx) * (1 - fy) + (vy01 * (1 - fx) + vy11 * fx) * fy;
        return { vx, vy };
    }

    function updateAndRenderFlowParticles(targetCtx, floatArray, count) {
        if (flowParticles.length === 0) {
            initFlowParticles();
        }

        targetCtx.save();
        targetCtx.lineCap = 'round';
        targetCtx.lineJoin = 'round';

        const scaleFactor = Math.min(1.5, Math.max(0.8, Math.sqrt(scale) * 0.45));

        for (let i = 0; i < flowParticles.length; i++) {
            const p = flowParticles[i];
            p.age++;

            const vel = sampleVelocity(p.x, p.y);
            const speed = vel ? Math.hypot(vel.vx, vel.vy) : 0;

            if (!vel || speed < 0.08 || p.age >= p.maxLife || p.x < 0 || p.x > worldWidth || p.y < 0 || p.y > worldHeight) {
                respawnFlowParticle(p, floatArray, count);
                continue;
            }

            // Step along flow vector
            const stepDist = Math.max(0.45, Math.min(1.7, speed * 0.75)) * 0.5;
            p.x += (vel.vx / speed) * stepDist;
            p.y += (vel.vy / speed) * stepDist;

            p.trail.push({ x: p.x, y: p.y });
            if (p.trail.length > maxTrailPoints) {
                p.trail.shift();
            }

            const trailLen = p.trail.length;
            if (trailLen < 2) continue;

            const lifeFrac = p.age / p.maxLife;
            // Smooth bell curve envelope: particle fades in softly and fades out before dying
            const baseAlpha = Math.sin(lifeFrac * Math.PI);
            if (baseAlpha <= 0.01) continue;

            // Render ghostly line segments from faint wispy tail to luminous head
            for (let k = 0; k < trailLen - 1; k++) {
                const p0 = p.trail[k];
                const p1 = p.trail[k + 1];
                const s0 = worldToScreen(p0.x, p0.y);
                const s1 = worldToScreen(p1.x, p1.y);

                // Viewport culling
                if ((s0.x < -30 && s1.x < -30) || (s0.x > canvas.width + 30 && s1.x > canvas.width + 30) ||
                    (s0.y < -30 && s1.y < -30) || (s0.y > canvas.height + 30 && s1.y > canvas.height + 30)) {
                    continue;
                }

                const segRatio = (k + 1) / trailLen;
                const segAlpha = baseAlpha * (0.04 + 0.78 * Math.pow(segRatio, 1.7));
                const lineWidth = Math.max(0.8, Math.min(2.6, (0.7 + 1.3 * segRatio) * scaleFactor));

                // Ethereal ghostly cyan-white gradient from tail to head
                const r = Math.round(180 + 65 * segRatio);
                const g = Math.round(230 + 24 * segRatio);
                const b = 255;

                targetCtx.strokeStyle = `rgba(${r}, ${g}, ${b}, ${segAlpha.toFixed(3)})`;
                targetCtx.lineWidth = lineWidth;
                targetCtx.beginPath();
                targetCtx.moveTo(s0.x, s0.y);
                targetCtx.lineTo(s1.x, s1.y);
                targetCtx.stroke();
            }
        }

        targetCtx.restore();
    }

    // --- Coordinate Transforms ---
    function worldToScreen(wx, wy) {
        return {
            x: wx * scaleX + offsetX,
            y: wy * scaleY + offsetY
        };
    }

    function screenToWorld(sx, sy) {
        return {
            x: (sx - offsetX) / (scaleX || scale),
            y: (sy - offsetY) / (scaleY || scale)
        };
    }

    function setScale(value) {
        scale = value;
        scaleX = value;
        scaleY = value;
    }

    function pointerToCanvas(e) {
        const rect = canvas.getBoundingClientRect();
        return {
            x: (e.clientX - rect.left) * canvas.width / rect.width,
            y: (e.clientY - rect.top) * canvas.height / rect.height
        };
    }

    function zoomAt(sx, sy, factor) {
        const world = screenToWorld(sx, sy);
        setScale(Math.min(Math.max(scale * factor, 0.02), 50.0));
        offsetX = sx - world.x * scaleX;
        offsetY = sy - world.y * scaleY;
        redrawCurrent();
    }

    function fitBounds(w, h, padding = 30) {
        if (isMapMode) {
            updateLeafletProjection();
            return;
        }
        worldWidth = w || worldWidth;
        worldHeight = h || worldHeight;

        const availableWidth = Math.max(canvas.width - padding * 2, 50);
        const availableHeight = Math.max(canvas.height - padding * 2, 50);

        setScale(Math.min(availableWidth / worldWidth, availableHeight / worldHeight));
        offsetX = (canvas.width - worldWidth * scale) / 2;
        offsetY = (canvas.height - worldHeight * scale) / 2;
    }

    fitBounds(worldWidth, worldHeight);
    initFlowParticles();

    function redrawCurrent() {
        if (lastWasGpu) {
            void renderGpu(lastRenderMode, lastShowWhiskers, lastWhiskerLength, lastGranulation, lastShowFlowParticles);
        } else if (lastFloatArray && lastCount > 0) {
            renderCore(lastFloatArray, lastCount, lastRenderMode, lastShowWhiskers, lastWhiskerLength, lastGranulation, lastShowFlowParticles);
        } else {
            ctx.fillStyle = '#080a0f';
            ctx.fillRect(0, 0, canvas.width, canvas.height);
            drawEnvironment();
            drawScaleBar();
        }
    }

    // --- Interactive Controls (Pan & Zoom) ---
    let isDragging = false;
    let dragStartX = 0;
    let dragStartY = 0;
    let startOffsetX = 0;
    let startOffsetY = 0;

    canvas.style.cursor = 'grab';

    function onPointerDown(e) {
        if (!isInteractive) return;
        isDragging = true;
        canvas.style.cursor = 'grabbing';
        const point = pointerToCanvas(e);
        dragStartX = point.x;
        dragStartY = point.y;
        startOffsetX = offsetX;
        startOffsetY = offsetY;
    }

    function onPointerMove(e) {
        if (!isDragging || !isInteractive) return;
        const point = pointerToCanvas(e);
        const currentX = point.x;
        const currentY = point.y;
        offsetX = startOffsetX + (currentX - dragStartX);
        offsetY = startOffsetY + (currentY - dragStartY);
        redrawCurrent();
    }

    function onPointerUp() {
        if (!isDragging) return;
        isDragging = false;
        canvas.style.cursor = 'grab';
    }

    function onWheel(e) {
        if (!isInteractive) return;
        e.preventDefault();

        const point = pointerToCanvas(e);
        zoomAt(point.x, point.y, e.deltaY < 0 ? 1.15 : 0.85);
    }

    function onDblClick() {
        if (!isInteractive) return;
        fitBounds(worldWidth, worldHeight);
        redrawCurrent();
    }

    canvas.addEventListener('pointerdown', onPointerDown);
    window.addEventListener('pointermove', onPointerMove);
    window.addEventListener('pointerup', onPointerUp);
    canvas.addEventListener('wheel', onWheel, { passive: false });
    canvas.addEventListener('dblclick', onDblClick);

    // --- Color Palettes ---
    function getColorFromPalette(normalizedValue, usePowerCurve = false, invert = false) {
        let t = Math.min(Math.max(normalizedValue, 0), 1);
        if (usePowerCurve) t = Math.pow(t, 0.75);
        if (invert) t = 1.0 - t;

        const stops = [
            { t: 0.00, r: 40, g: 0, b: 70 },
            { t: 0.05, r: 85, g: 0, b: 90 },
            { t: 0.10, r: 160, g: 0, b: 50 },
            { t: 0.15, r: 235, g: 0, b: 0 },
            { t: 0.20, r: 255, g: 45, b: 0 },
            { t: 0.25, r: 255, g: 90, b: 0 },
            { t: 0.30, r: 255, g: 135, b: 0 },
            { t: 0.35, r: 255, g: 180, b: 0 },
            { t: 0.40, r: 255, g: 220, b: 0 },
            { t: 0.45, r: 225, g: 245, b: 0 },
            { t: 0.50, r: 140, g: 235, b: 30 },
            { t: 0.55, r: 60, g: 220, b: 70 },
            { t: 0.60, r: 10, g: 215, b: 130 },
            { t: 0.65, r: 0, g: 230, b: 180 },
            { t: 0.70, r: 0, g: 240, b: 225 },
            { t: 0.75, r: 0, g: 245, b: 255 },
            { t: 0.80, r: 0, g: 210, b: 255 },
            { t: 0.85, r: 0, g: 175, b: 255 },
            { t: 0.90, r: 0, g: 140, b: 255 },
            { t: 0.95, r: 0, g: 105, b: 255 },
            { t: 1.00, r: 0, g: 80, b: 255 }
        ];

        let i = 0;
        while (i < stops.length - 1 && t > stops[i + 1].t) {
            i++;
        }

        if (i >= stops.length - 1) {
            const last = stops[stops.length - 1];
            return { r: last.r, g: last.g, b: last.b };
        }

        const s0 = stops[i];
        const s1 = stops[i + 1];
        const localT = (t - s0.t) / (s1.t - s0.t);

        return {
            r: Math.round(s0.r + (s1.r - s0.r) * localT),
            g: Math.round(s0.g + (s1.g - s0.g) * localT),
            b: Math.round(s0.b + (s1.b - s0.b) * localT)
        };
    }

    function getHeatmapSpeedColor(speed) {
        const range = trackedMaxSpeed - trackedMinSpeed;
        const t = (speed - trackedMinSpeed) / (range > 0.001 ? range : 1.0);
        return getColorFromPalette(t, false, false);
    }

    function getDensityColor(density) {
        const t = density / trackedMaxDensity;
        return getColorFromPalette(t, true, true);
    }

    // Precomputed speed palette (eliminates string allocations per frame)
    const paletteSize = 32;
    const agentSpeedPalette = [];
    for (let i = 0; i < paletteSize; i++) {
        const t = i / (paletteSize - 1);
        let r, g, b;
        if (t < 0.5) {
            const lt = t * 2;
            r = 255;
            g = Math.round(230 * lt);
            b = Math.round(85 * (1 - lt));
        } else {
            const lt = (t - 0.5) * 2;
            r = Math.round(255 * (1 - lt));
            g = Math.round(230 + (240 - 230) * lt);
            b = Math.round(255 * lt);
        }
        agentSpeedPalette.push(`rgb(${r},${g},${b})`);
    }

    // Preallocated buckets for batch rendering
    const colorBuckets = Array.from({ length: paletteSize }, () => []);

    function updateTrailCanvas() {
        if (!trailCanvas || trailCanvas.width !== canvas.width || trailCanvas.height !== canvas.height) {
            trailCanvas.width = canvas.width;
            trailCanvas.height = canvas.height;
            trailCtx = trailCanvas.getContext('2d');
            trailCtx.clearRect(0, 0, trailCanvas.width, trailCanvas.height);
        }
    }

    // --- Environment Rendering ---
    function drawWorldGrid() {
        const worldScreenOrigin = worldToScreen(0, 0);
        const worldScreenWidth = worldWidth * scaleX;
        const worldScreenHeight = worldHeight * scaleY;

        ctx.fillStyle = '#10141a';
        ctx.fillRect(worldScreenOrigin.x, worldScreenOrigin.y, worldScreenWidth, worldScreenHeight);

        ctx.strokeStyle = '#2b394a';
        ctx.lineWidth = 2;
        ctx.strokeRect(worldScreenOrigin.x, worldScreenOrigin.y, worldScreenWidth, worldScreenHeight);

        // Grid lines in meters
        const gridStepMeters = scale > 1.0 ? 50.0 : (scale > 0.3 ? 100.0 : 200.0);
        ctx.strokeStyle = 'rgba(255, 255, 255, 0.04)';
        ctx.lineWidth = 1;
        ctx.beginPath();
        for (let x = gridStepMeters; x < worldWidth; x += gridStepMeters) {
            const p1 = worldToScreen(x, 0);
            const p2 = worldToScreen(x, worldHeight);
            ctx.moveTo(p1.x, p1.y);
            ctx.lineTo(p2.x, p2.y);
        }
        for (let y = gridStepMeters; y < worldHeight; y += gridStepMeters) {
            const p1 = worldToScreen(0, y);
            const p2 = worldToScreen(worldWidth, y);
            ctx.moveTo(p1.x, p1.y);
            ctx.lineTo(p2.x, p2.y);
        }
        ctx.stroke();
    }

    function drawObstaclesAndTargets() {
        // Obstacles (Buildings / Barriers)
        if (obstacles && obstacles.length > 0) {
            for (let obs of obstacles) {
                const s = worldToScreen(obs.x, obs.y);
                const sw = obs.width * scaleX;
                const sh = obs.height * scaleY;

                ctx.fillStyle = '#1e2836';
                ctx.fillRect(s.x, s.y, sw, sh);
                ctx.strokeStyle = '#486581';
                ctx.lineWidth = Math.max(1.5, Math.min(3, 1.5 * scale));
                ctx.strokeRect(s.x, s.y, sw, sh);

                if (sw > 30 && sh > 30) {
                    ctx.fillStyle = 'rgba(255, 255, 255, 0.03)';
                    ctx.fillRect(s.x + 4, s.y + 4, sw - 8, sh - 8);
                }
            }
        }

        // Targets / Shelters or Exit Zone
        if (targets && targets.length > 0) {
            for (let tgt of targets) {
                const tz = worldToScreen(tgt.x, tgt.y);
                const tzw = tgt.width * scaleX;
                const tzh = tgt.height * scaleY;

                ctx.fillStyle = 'rgba(40, 167, 69, 0.25)';
                ctx.fillRect(tz.x, tz.y, tzw, tzh);
                ctx.strokeStyle = '#28a745';
                ctx.lineWidth = 2;
                ctx.strokeRect(tz.x, tz.y, tzw, tzh);

                if (tzw > 30 || tzh > 20) {
                    ctx.fillStyle = '#28a745';
                    ctx.font = 'bold 10px sans-serif';
                    ctx.textAlign = 'center';
                    const name = tgt.name || tgt.id || 'SCHRON';
                    ctx.fillText(name.length > 22 ? name.slice(0, 20) + '…' : name, tz.x + tzw / 2, tz.y + tzh / 2 + 4);
                }
            }
        } else if (exitZone) {
            const ez = worldToScreen(exitZone.x, exitZone.y);
            const ezw = exitZone.width * scaleX;
            const ezh = exitZone.height * scaleY;

            ctx.fillStyle = 'rgba(40, 167, 69, 0.25)';
            ctx.fillRect(ez.x, ez.y, ezw, ezh);
            ctx.strokeStyle = '#28a745';
            ctx.lineWidth = 2;
            ctx.strokeRect(ez.x, ez.y, ezw, ezh);

            if (ezw > 40 || ezh > 40) {
                ctx.fillStyle = '#28a745';
                ctx.font = 'bold 11px sans-serif';
                ctx.textAlign = 'center';
                ctx.fillText('STREFA EWAKUACJI', ez.x + ezw / 2, ez.y + ezh / 2 + 4);
            }
        }
    }

    function drawEnvironment() {
        drawWorldGrid();
        drawObstaclesAndTargets();
    }

    // --- Dynamic Scale Bar & HUD Overlay ---
    function drawScaleBar() {
        ctx.save();
        const padding = 16;
        const x = padding;
        const y = canvas.height - padding - 8;

        const targetPixelWidth = 100;
        const targetMeters = targetPixelWidth / scale;
        const niceValues = [5, 10, 20, 50, 100, 200, 500, 1000, 2000];
        let chosenMeters = niceValues[0];
        for (let val of niceValues) {
            if (val <= targetMeters * 1.5) {
                chosenMeters = val;
            }
        }

        const barPixelLength = chosenMeters * scale;

        // Draw scale line
        ctx.strokeStyle = '#ffffff';
        ctx.lineWidth = 2.5;
        ctx.beginPath();
        ctx.moveTo(x, y);
        ctx.lineTo(x + barPixelLength, y);
        // Ticks
        ctx.moveTo(x, y - 5);
        ctx.lineTo(x, y + 5);
        ctx.moveTo(x + barPixelLength, y - 5);
        ctx.lineTo(x + barPixelLength, y + 5);
        ctx.stroke();

        // Text
        ctx.fillStyle = '#ffffff';
        ctx.font = 'bold 11px sans-serif';
        ctx.textAlign = 'left';
        ctx.fillText(`${chosenMeters} m`, x + barPixelLength + 8, y + 4);

        ctx.font = '10px sans-serif';
        ctx.fillStyle = 'rgba(255, 255, 255, 0.6)';
        ctx.fillText(`Skala: ${scale.toFixed(2)} px/m | Świat: ${worldWidth.toFixed(0)}m × ${worldHeight.toFixed(0)}m`, x, y - 10);

        ctx.restore();
    }

    // Helper to safely extract Float32Array from any buffer format
    function extractFloatArray(rawBuffer, count) {
        if (!rawBuffer) return null;

        if (rawBuffer instanceof Float32Array) {
            return rawBuffer;
        }

        if (rawBuffer instanceof ArrayBuffer) {
            return new Float32Array(rawBuffer, 0, Math.min(count * 5, Math.floor(rawBuffer.byteLength / 4)));
        }

        if (typeof rawBuffer === 'string') {
            try {
                const binary = atob(rawBuffer);
                const len = binary.length;
                const bytes = new Uint8Array(len);
                for (let i = 0; i < len; i++) {
                    bytes[i] = binary.charCodeAt(i);
                }
                return new Float32Array(bytes.buffer, 0, Math.min(count * 5, Math.floor(len / 4)));
            } catch (e) {
                console.error("extractFloatArray Base64 error:", e);
                return null;
            }
        }

        if (ArrayBuffer.isView(rawBuffer)) {
            const byteOffset = rawBuffer.byteOffset || 0;
            const byteLength = rawBuffer.byteLength || (count * 20);
            if (byteOffset % 4 === 0) {
                return new Float32Array(rawBuffer.buffer, byteOffset, Math.min(count * 5, Math.floor(byteLength / 4)));
            } else {
                const sliced = rawBuffer.buffer.slice(byteOffset, byteOffset + byteLength);
                return new Float32Array(sliced, 0, Math.min(count * 5, Math.floor(byteLength / 4)));
            }
        }

        if (Array.isArray(rawBuffer)) {
            return new Float32Array(rawBuffer);
        }

        if (typeof rawBuffer === 'object') {
            if (typeof rawBuffer.$value === 'string') {
                return extractFloatArray(rawBuffer.$value, count);
            }
            if (rawBuffer.buffer && (rawBuffer.buffer instanceof ArrayBuffer)) {
                const byteOffset = rawBuffer.byteOffset || 0;
                const byteLength = rawBuffer.byteLength || (count * 20);
                if (byteOffset % 4 === 0) {
                    return new Float32Array(rawBuffer.buffer, byteOffset, Math.min(count * 5, Math.floor(byteLength / 4)));
                } else {
                    const sliced = rawBuffer.buffer.slice(byteOffset, byteOffset + byteLength);
                    return new Float32Array(sliced, 0, Math.min(count * 5, Math.floor(byteLength / 4)));
                }
            }
            const vals = Object.values(rawBuffer);
            if (vals.length >= count * 20) {
                const u8 = new Uint8Array(vals);
                return new Float32Array(u8.buffer, 0, count * 5);
            } else if (vals.length >= count * 5) {
                return new Float32Array(vals.slice(0, count * 5));
            }
        }

        return null;
    }

    // --- Render binary (High performance: zero JSON serialization, batched draw calls) ---
    function renderBinary(rawBuffer, count, renderMode = 'agents', showWhiskers = false, whiskerLength = 2.5, granulation = 1, showFlowParticles = true) {
        try {
            const floatArray = extractFloatArray(rawBuffer, count);
            if (floatArray && floatArray.length >= count * 5) {
                renderCore(floatArray, count, renderMode, showWhiskers, whiskerLength, granulation, showFlowParticles);
            } else {
                console.warn("renderBinary: invalid buffer length", {
                    extractedLength: floatArray ? floatArray.length : 0,
                    expected: count * 5,
                    rawBufferType: typeof rawBuffer,
                    isView: ArrayBuffer.isView(rawBuffer),
                    isArray: Array.isArray(rawBuffer)
                });
            }
        } catch (err) {
            console.error("renderBinary error:", err);
        }
    }

    // --- Render arrays (Backward compatibility) ---
    function render(posX, posY, vx, vy, radius, count, renderMode = 'agents', showWhiskers = false, whiskerLength = 2.5, granulation = 1, showFlowParticles = true) {
        const floatArray = new Float32Array(count * 5);
        for (let i = 0; i < count; i++) {
            floatArray[i * 5] = posX[i];
            floatArray[i * 5 + 1] = posY[i];
            floatArray[i * 5 + 2] = vx[i];
            floatArray[i * 5 + 3] = vy[i];
            floatArray[i * 5 + 4] = radius[i];
        }
        renderCore(floatArray, count, renderMode, showWhiskers, whiskerLength, granulation, showFlowParticles);
    }

    function renderCore(floatArray, count, renderMode, showWhiskers, whiskerLength, granulation = 1, showFlowParticles = true) {
        lastWasGpu = false;
        lastFloatArray = floatArray;
        lastCount = count;
        lastRenderMode = renderMode;
        lastShowWhiskers = showWhiskers;
        lastWhiskerLength = whiskerLength;
        lastGranulation = granulation;
        lastShowFlowParticles = showFlowParticles;

        if (isMapMode) {
            updateLeafletProjection();
        }
        const worldOrigin = worldToScreen(0, 0);
        const screenWorldW = worldWidth * scaleX;
        const screenWorldH = worldHeight * scaleY;

        if (renderMode === 'heatmap' || renderMode === 'density') {
            updateTrailCanvas();
            const isDensity = renderMode === 'density';
            trailCtx.clearRect(0, 0, trailCanvas.width, trailCanvas.height);

            if (!isDensity) speedSum.fill(0);
            weightSum.fill(0);
            vxSum.fill(0);
            vySum.fill(0);

            const cellMetersX = worldWidth / gridCols;
            const cellMetersY = worldHeight / gridRows;
            const kernelRadiusMeters = Math.max(cellMetersX * 2.0, Math.min(worldWidth * 0.035, 6.0));
            const kernelRadiusSq = kernelRadiusMeters * kernelRadiusMeters;
            const invKernelRadiusSq = 1.0 / kernelRadiusSq;

            for (let i = 0; i < count; i++) {
                const rawR = floatArray[i * 5 + 4];
                if (rawR <= 0.001) continue;

                const wx = floatArray[i * 5];
                const wy = floatArray[i * 5 + 1];
                const u = floatArray[i * 5 + 2];
                const v = floatArray[i * 5 + 3];
                const speed = Math.hypot(u, v);

                const gx = (wx / worldWidth) * gridCols;
                const gy = (wy / worldHeight) * gridRows;

                const radCellsX = Math.ceil(kernelRadiusMeters / cellMetersX);
                const radCellsY = Math.ceil(kernelRadiusMeters / cellMetersY);

                const minX = Math.max(0, Math.floor(gx - radCellsX));
                const maxX = Math.min(gridCols - 1, Math.floor(gx + radCellsX));
                const minY = Math.max(0, Math.floor(gy - radCellsY));
                const maxY = Math.min(gridRows - 1, Math.floor(gy + radCellsY));

                for (let cy = minY; cy <= maxY; cy++) {
                    for (let cx = minX; cx <= maxX; cx++) {
                        const cellWx = (cx + 0.5) * cellMetersX;
                        const cellWy = (cy + 0.5) * cellMetersY;

                        const dx = cellWx - wx;
                        const dy = cellWy - wy;
                        const distSq = dx * dx + dy * dy;

                        if (distSq <= kernelRadiusSq) {
                            const q = 1.0 - (distSq * invKernelRadiusSq);
                            const weight = q * q;

                            if (weight > 0.001) {
                                const idx = cy * gridCols + cx;
                                if (!isDensity) speedSum[idx] += speed * weight;
                                weightSum[idx] += isDensity ? weight * granulation : weight;
                                vxSum[idx] += u * weight;
                                vySum[idx] += v * weight;
                            }
                        }
                    }
                }
            }

            const totalCells = gridCols * gridRows;
            const imgData = offCtx.createImageData(gridCols, gridRows);
            const data = imgData.data;

            if (isDensity) {
                let frameMaxDensity = 0.0;
                for (let i = 0; i < totalCells; i++) {
                    if (weightSum[i] > frameMaxDensity) frameMaxDensity = weightSum[i];
                }
                if (frameMaxDensity > 0.1) {
                    trackedMaxDensity += (frameMaxDensity - trackedMaxDensity) * 0.05;
                    if (trackedMaxDensity < 2.0) trackedMaxDensity = 2.0;
                }

                for (let i = 0; i < totalCells; i++) {
                    const w = weightSum[i];
                    const pixelOffset = i * 4;
                    if (w > 0.005) {
                        const rgb = getDensityColor(w);
                        const norm = Math.min(1.0, Math.max(0.0, w / (trackedMaxDensity || 2.0)));
                        const t = Math.min(1.0, w / 0.15);
                        const edgeFade = t * t * (3.0 - 2.0 * t);
                        const alpha = Math.min(240, Math.floor(edgeFade * (150 + 90 * Math.pow(norm, 0.6))));
                        data[pixelOffset] = rgb.r;
                        data[pixelOffset + 1] = rgb.g;
                        data[pixelOffset + 2] = rgb.b;
                        data[pixelOffset + 3] = alpha;
                    } else {
                        data[pixelOffset + 3] = 0;
                    }
                }
            } else {
                let frameMin = Infinity, frameMax = -Infinity;
                for (let i = 0; i < totalCells; i++) {
                    const w = weightSum[i];
                    if (w > 0.005) {
                        const avgSpeed = speedSum[i] / w;
                        if (avgSpeed < frameMin) frameMin = avgSpeed;
                        if (avgSpeed > frameMax) frameMax = avgSpeed;
                    }
                }
                if (frameMin !== Infinity && frameMax !== -Infinity) {
                    trackedMinSpeed += (frameMin - trackedMinSpeed) * 0.05;
                    trackedMaxSpeed += (frameMax - trackedMaxSpeed) * 0.05;
                    if (trackedMaxSpeed - trackedMinSpeed < 0.1) trackedMaxSpeed = trackedMinSpeed + 0.1;
                }

                for (let i = 0; i < totalCells; i++) {
                    const w = weightSum[i];
                    const pixelOffset = i * 4;
                    if (w > 0.005) {
                        const avgSpeed = speedSum[i] / w;
                        const rgb = getHeatmapSpeedColor(avgSpeed);
                        const normWeight = Math.min(1.0, Math.max(0.0, w / 2.0));
                        const t = Math.min(1.0, w / 0.15);
                        const edgeFade = t * t * (3.0 - 2.0 * t);
                        const alpha = Math.min(240, Math.floor(edgeFade * (150 + 90 * Math.pow(normWeight, 0.6))));
                        data[pixelOffset] = rgb.r;
                        data[pixelOffset + 1] = rgb.g;
                        data[pixelOffset + 2] = rgb.b;
                        data[pixelOffset + 3] = alpha;
                    } else {
                        data[pixelOffset + 3] = 0;
                    }
                }
            }

            offCtx.putImageData(imgData, 0, 0);

            trailCtx.save();
            trailCtx.imageSmoothingEnabled = true;
            trailCtx.imageSmoothingQuality = 'high';
            trailCtx.filter = 'blur(2.0px)';
            trailCtx.drawImage(offscreenCanvas, worldOrigin.x, worldOrigin.y, screenWorldW, screenWorldH);
            trailCtx.restore();

            if (isMapMode) {
                ctx.clearRect(0, 0, canvas.width, canvas.height);
            } else {
                ctx.fillStyle = '#080a0f';
                ctx.fillRect(0, 0, canvas.width, canvas.height);
                drawWorldGrid();
            }
            ctx.drawImage(trailCanvas, 0, 0);
            if (showFlowParticles) {
                updateAndRenderFlowParticles(ctx, floatArray, count);
            }
            if (!isMapMode) {
                drawObstaclesAndTargets();
                drawScaleBar();
            }
            return;
        }

        // --- AGENTS RENDERING (BATCHED BY COLOR FOR 60 FPS) ---
        if (isMapMode) {
            ctx.clearRect(0, 0, canvas.width, canvas.height);
        } else {
            ctx.fillStyle = '#080a0f';
            ctx.fillRect(0, 0, canvas.width, canvas.height);
            drawEnvironment();
        }

        // Optional whiskers (batched into single stroke)
        if (showWhiskers) {
            ctx.beginPath();
            ctx.strokeStyle = 'rgba(255, 255, 255, 0.35)';
            ctx.lineWidth = 1;
            const whiskerScreenLen = whiskerLength * Math.min(scaleX, scaleY);

            for (let i = 0; i < count; i++) {
                const rawR = floatArray[i * 5 + 4];
                if (rawR <= 0.001) continue;

                const u = floatArray[i * 5 + 2];
                const v = floatArray[i * 5 + 3];
                const speedSq = u * u + v * v;
                if (speedSq > 0.001) {
                    const s = worldToScreen(floatArray[i * 5], floatArray[i * 5 + 1]);
                    if (s.x < -20 || s.x > canvas.width + 20 || s.y < -20 || s.y > canvas.height + 20) continue;

                    const speed = Math.sqrt(speedSq);
                    ctx.moveTo(s.x, s.y);
                    ctx.lineTo(s.x + (u / speed) * whiskerScreenLen, s.y + (v / speed) * whiskerScreenLen);
                }
            }
            ctx.stroke();
        }

        // Batch agents into preallocated color buckets
        for (let c = 0; c < paletteSize; c++) {
            colorBuckets[c].length = 0;
        }

        for (let i = 0; i < count; i++) {
            const rawR = Number.isFinite(floatArray[i * 5 + 4]) ? floatArray[i * 5 + 4] : 0.35;
            if (rawR <= 0.001) continue;

            const rawX = floatArray[i * 5];
            const rawY = floatArray[i * 5 + 1];
            const wx = Number.isFinite(rawX) ? rawX : 0;
            const wy = Number.isFinite(rawY) ? rawY : 0;
            const s = worldToScreen(wx, wy);

            // View frustum culling
            if (s.x < -20 || s.x > canvas.width + 20 || s.y < -20 || s.y > canvas.height + 20) {
                continue;
            }

            const u = Number.isFinite(floatArray[i * 5 + 2]) ? floatArray[i * 5 + 2] : 0;
            const v = Number.isFinite(floatArray[i * 5 + 3]) ? floatArray[i * 5 + 3] : 0;
            const speed = Math.hypot(u, v);
            const r = Math.max(rawR * Math.min(scaleX, scaleY), isMapMode ? 4.0 : 3.2);
            const colorIdx = Math.min(paletteSize - 1, Math.max(0, (speed * (paletteSize / 2.2)) | 0));

            colorBuckets[colorIdx].push(s.x, s.y, r);
        }

        // Draw batched circles: only ~32 draw calls instead of 2000!
        for (let c = 0; c < paletteSize; c++) {
            const bucket = colorBuckets[c];
            if (bucket.length > 0) {
                ctx.fillStyle = agentSpeedPalette[c];
                ctx.beginPath();
                for (let j = 0; j < bucket.length; j += 3) {
                    ctx.moveTo(bucket[j] + bucket[j + 2], bucket[j + 1]);
                    ctx.arc(bucket[j], bucket[j + 1], bucket[j + 2], 0, Math.PI * 2);
                }
                ctx.fill();
            }
        }

        // When granulation > 1, draw subtle white outline on cluster macro-dots
        if (granulation > 1) {
            ctx.strokeStyle = "rgba(255, 255, 255, 0.45)";
            ctx.lineWidth = 1.2;
            ctx.beginPath();
            for (let c = 0; c < paletteSize; c++) {
                const bucket = colorBuckets[c];
                for (let j = 0; j < bucket.length; j += 3) {
                    ctx.moveTo(bucket[j] + bucket[j + 2], bucket[j + 1]);
                    ctx.arc(bucket[j], bucket[j + 1], bucket[j + 2], 0, Math.PI * 2);
                }
            }
            ctx.stroke();
        }

        if (!isMapMode) drawScaleBar();
    }

    // --- WebGPU Compute Simulation Strategy ---
    let gpuEngine = null;
    let isGpuReady = false;

    async function initGpu(agentCount, granulation, socialWeight, config) {
        if (!isWebGpuSupported()) {
            throw new Error("WebGPU is not supported in this environment.");
        }
        if (!gpuEngine) {
            gpuEngine = new GpuSimulationEngine();
            await gpuEngine.boot();
        }
        await gpuEngine.syncInFlight(0);
        if (config) {
            weightDistance = config.weightDistance ?? weightDistance;
            weightOccupancy = config.weightOccupancy ?? weightOccupancy;
            if (config.worldWidth) worldWidth = config.worldWidth;
            if (config.worldHeight) worldHeight = config.worldHeight;
            if (config.obstacles) obstacles = config.obstacles;
            if (config.targets) targets = config.targets;
            if (config.exitZone) exitZone = config.exitZone;
            fitBounds(worldWidth, worldHeight);
        }
        gpuEngine.dispatch('plansafe-sim', 'init', {
            count: agentCount,
            worldWidth: worldWidth,
            worldHeight: worldHeight,
            socialRepulsionWeight: socialWeight,
            granulation: granulation,
            obstacles: obstacles,
            targets: targets,
            exitZone: exitZone,
            weightDistance: weightDistance,
            weightOccupancy: weightOccupancy
        }, { add: () => {} });
        isGpuReady = true;
        console.log("%c[PlanSafe] WebGPU compute engine active", "color: #10b981; font-weight: bold;");
        return true;
    }

    async function stepGpu(ticks) {
        if (!gpuEngine || !isGpuReady) return;
        await gpuEngine.advanceFixedTicks(ticks);
    }

    let gpuFloatArray = null;

    function ensureGpuFloatArray(count) {
        const required = count * 5;
        if (!gpuFloatArray || gpuFloatArray.length < required) {
            gpuFloatArray = new Float32Array(required);
        }
        return gpuFloatArray;
    }

    async function renderGpu(renderMode, showWhiskers, whiskerLength, granulation = 1, showFlowParticles = true) {
        if (!gpuEngine || !isGpuReady) return;
        try {
            lastRenderMode = renderMode;
            lastShowWhiskers = showWhiskers;
            lastWhiskerLength = whiskerLength;
            lastGranulation = granulation;
            lastShowFlowParticles = showFlowParticles;
            lastWasGpu = true;
            if (isMapMode) updateLeafletProjection();
            if (renderMode === 'agents') {
                const surface = await gpuEngine.drawAgents(canvas.width, canvas.height,
                    scaleX, scaleY, offsetX, offsetY, isMapMode ? 4.0 : 3.2,
                    granulation, showWhiskers, whiskerLength);
                if (isMapMode) {
                    ctx.clearRect(0, 0, canvas.width, canvas.height);
                } else {
                    ctx.fillStyle = '#080a0f';
                    ctx.fillRect(0, 0, canvas.width, canvas.height);
                    drawEnvironment();
                }
                ctx.drawImage(surface, 0, 0);
                if (!isMapMode) drawScaleBar();
                return;
            }
            const preview = await gpuEngine.capturePreview();
            const count = preview.count;
            if (!count || count <= 0) return;
            const floatArray = ensureGpuFloatArray(count);
            const target = preview.values;
            for (let i = 0; i < count; i++) {
                floatArray[i * 5] = target[i];
                floatArray[i * 5 + 1] = target[count + i];
                floatArray[i * 5 + 2] = target[2 * count + i];
                floatArray[i * 5 + 3] = target[3 * count + i];
                floatArray[i * 5 + 4] = target[4 * count + i];
            }
            renderCore(floatArray, count, renderMode, showWhiskers, whiskerLength, granulation, showFlowParticles);
            lastWasGpu = true;
        } catch (err) {
            console.error("[PlanSafe] renderGpu error:", err);
        }
    }

    async function setGpuWorldConfig(config) {
        if (!gpuEngine || !isGpuReady) return;
        await gpuEngine.syncInFlight(0);
        weightDistance = config.weightDistance ?? weightDistance;
        weightOccupancy = config.weightOccupancy ?? weightOccupancy;
        if (config.worldWidth) worldWidth = config.worldWidth;
        if (config.worldHeight) worldHeight = config.worldHeight;
        if (config.obstacles) obstacles = config.obstacles;
        if (config.targets) targets = config.targets;
        if (config.exitZone) exitZone = config.exitZone;
        fitBounds(worldWidth, worldHeight);

        gpuEngine.dispatch('plansafe-sim', 'preset', {
            worldWidth: worldWidth,
            worldHeight: worldHeight,
            count: config.count,
            granulation: config.granulation,
            obstacles: obstacles,
            targets: targets,
            exitZone: exitZone,
            weightDistance: weightDistance,
            weightOccupancy: weightOccupancy
        }, { add: () => {} });
    }

    async function resetGpu(count, granulation, socialWeight) {
        if (!gpuEngine || !isGpuReady) return;
        await gpuEngine.syncInFlight(0);
        gpuEngine.dispatch('plansafe-sim', 'reset', {
            count: count,
            worldWidth: worldWidth,
            worldHeight: worldHeight,
            socialRepulsionWeight: socialWeight,
            granulation: granulation,
            obstacles: obstacles,
            targets: targets,
            exitZone: exitZone,
            weightDistance: weightDistance,
            weightOccupancy: weightOccupancy
        }, { add: () => {} });
    }

    return {
        render: render,
        renderBinary: renderBinary,
        initGpu: initGpu,
        stepGpu: stepGpu,
        renderGpu: renderGpu,
        getGpuTelemetry: async () => gpuEngine && isGpuReady ? await gpuEngine.captureTelemetry() : null,
        setGpuWorldConfig: setGpuWorldConfig,
        setGpuEnvironment: async function (config) {
            targets = config.targets ?? targets;
            weightDistance = config.weightDistance ?? weightDistance;
            weightOccupancy = config.weightOccupancy ?? weightOccupancy;
            if (gpuEngine && isGpuReady) {
                await gpuEngine.syncInFlight(0);
                gpuEngine.dispatch('plansafe-sim', 'set-environment', config, { add: () => {} });
            }
        },
        resetGpu: resetGpu,
        setGpuWeight: async function (value) {
            if (!gpuEngine || !isGpuReady) return;
            await gpuEngine.syncInFlight(0);
            gpuEngine.dispatch('plansafe-sim', 'weight', { socialRepulsionWeight: value }, { add: () => {} });
        },
        isWebGpuSupported: function () { return isWebGpuSupported(); },
        setWorldConfig: function (config) {
            weightDistance = config.weightDistance ?? weightDistance;
            weightOccupancy = config.weightOccupancy ?? weightOccupancy;
            if (config.worldWidth) worldWidth = config.worldWidth;
            if (config.worldHeight) worldHeight = config.worldHeight;
            if (config.obstacles) obstacles = config.obstacles;
            if (config.targets) targets = config.targets;
            if (config.exitZone) exitZone = config.exitZone;
            if (config.originLat !== undefined) originLat = config.originLat;
            if (config.originLng !== undefined) originLng = config.originLng;
            if (config.minLat !== undefined) minLat = config.minLat;
            if (config.maxLng !== undefined) maxLng = config.maxLng;

            if (isMapMode) {
                updateLeafletProjection();
            } else {
                fitBounds(worldWidth, worldHeight);
            }
            initFlowParticles();
            redrawCurrent();
        },
        setTransform: function (t) {
            if (t.scale !== undefined) setScale(t.scale);
            if (t.offsetX !== undefined) offsetX = t.offsetX;
            if (t.offsetY !== undefined) offsetY = t.offsetY;
            redrawCurrent();
        },
        getTransform: function () {
            return { scale: scale, offsetX: offsetX, offsetY: offsetY };
        },
        resetView: function () {
            fitBounds(worldWidth, worldHeight);
            redrawCurrent();
        },
        zoom: function (factor) {
            zoomAt(canvas.width / 2, canvas.height / 2, factor);
        },
        setInteractive: function (enabled) {
            isInteractive = !!enabled;
        },
        dispose: function () {
            if (gpuEngine) {
                try { gpuEngine.dispose(); } catch (e) { }
                gpuEngine = null;
                isGpuReady = false;
            }
            if (!isMapMode) {
                canvas.removeEventListener('pointerdown', onPointerDown);
                window.removeEventListener('pointermove', onPointerMove);
                window.removeEventListener('pointerup', onPointerUp);
                canvas.removeEventListener('wheel', onWheel);
                canvas.removeEventListener('dblclick', onDblClick);
            }
            if (leafletMap) {
                leafletMap.off('move', onMapSync);
                leafletMap.off('zoom', onMapSync);
                leafletMap.off('viewreset', onMapSync);
                leafletMap.off('resize', onMapSync);
            }
        }
    };
}

export function initMapSimulator(canvasRef, mapContainerId) {
    return initSimulator(canvasRef, mapContainerId);
}
