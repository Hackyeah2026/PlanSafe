export function initSimulator(canvasRef) {
    const canvas = canvasRef;
    const ctx = canvas.getContext('2d', { alpha: false });

    // --- World & Camera State ---
    let worldWidth = 200.0;   // in meters
    let worldHeight = 200.0;  // in meters
    let obstacles = [];
    let targets = [];
    let exitZone = { x: 184, y: 80, width: 12, height: 40 };

    let scale = 1.0;          // pixels per meter
    let offsetX = 0;          // translation X in canvas pixels
    let offsetY = 0;          // translation Y in canvas pixels
    let isInteractive = true;

    // --- Heatmap / Metric Grid Setup ---
    const gridCols = 100;
    const gridRows = 100;
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

    let flowParticles = [];
    const numFlowParticles = 250;

    function initFlowParticles() {
        flowParticles = [];
        for (let i = 0; i < numFlowParticles; i++) {
            flowParticles.push({
                x: Math.random() * worldWidth,
                y: Math.random() * worldHeight,
                vx: 0,
                vy: 0
            });
        }
    }

    // --- Coordinate Transforms ---
    function worldToScreen(wx, wy) {
        return {
            x: wx * scale + offsetX,
            y: wy * scale + offsetY
        };
    }

    function screenToWorld(sx, sy) {
        return {
            x: (sx - offsetX) / scale,
            y: (sy - offsetY) / scale
        };
    }

    function fitBounds(w, h, padding = 30) {
        worldWidth = w || worldWidth;
        worldHeight = h || worldHeight;

        const availableWidth = Math.max(canvas.width - padding * 2, 50);
        const availableHeight = Math.max(canvas.height - padding * 2, 50);

        scale = Math.min(availableWidth / worldWidth, availableHeight / worldHeight);
        offsetX = (canvas.width - worldWidth * scale) / 2;
        offsetY = (canvas.height - worldHeight * scale) / 2;
    }

    fitBounds(worldWidth, worldHeight);
    initFlowParticles();

    // --- Last Render State for Instant Redraw (Pan, Zoom, Resize) ---
    let lastFloatArray = null;
    let lastCount = 0;
    let lastRenderMode = 'agents';
    let lastShowWhiskers = false;
    let lastWhiskerLength = 2.5;
    let lastGranulation = 1;

    function redrawCurrent() {
        if (lastFloatArray && lastCount > 0) {
            renderCore(lastFloatArray, lastCount, lastRenderMode, lastShowWhiskers, lastWhiskerLength, lastGranulation);
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
        const rect = canvas.getBoundingClientRect();
        dragStartX = e.clientX - rect.left;
        dragStartY = e.clientY - rect.top;
        startOffsetX = offsetX;
        startOffsetY = offsetY;
    }

    function onPointerMove(e) {
        if (!isDragging || !isInteractive) return;
        const rect = canvas.getBoundingClientRect();
        const currentX = e.clientX - rect.left;
        const currentY = e.clientY - rect.top;
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

        const rect = canvas.getBoundingClientRect();
        const mouseX = e.clientX - rect.left;
        const mouseY = e.clientY - rect.top;

        const wx = (mouseX - offsetX) / scale;
        const wy = (mouseY - offsetY) / scale;

        const zoomFactor = e.deltaY < 0 ? 1.15 : 0.85;
        const minScale = 0.02;
        const maxScale = 50.0;
        const newScale = Math.min(Math.max(scale * zoomFactor, minScale), maxScale);

        offsetX = mouseX - wx * newScale;
        offsetY = mouseY - wy * newScale;
        scale = newScale;
        redrawCurrent();
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
            { t: 0.00, r: 48, g: 0, b: 80 },
            { t: 0.05, r: 100, g: 0, b: 60 },
            { t: 0.10, r: 180, g: 0, b: 30 },
            { t: 0.15, r: 255, g: 0, b: 0 },
            { t: 0.20, r: 255, g: 42, b: 0 },
            { t: 0.25, r: 255, g: 85, b: 0 },
            { t: 0.30, r: 255, g: 128, b: 0 },
            { t: 0.35, r: 255, g: 170, b: 0 },
            { t: 0.40, r: 255, g: 212, b: 0 },
            { t: 0.45, r: 235, g: 240, b: 0 },
            { t: 0.50, r: 150, g: 230, b: 30 },
            { t: 0.55, r: 70, g: 215, b: 70 },
            { t: 0.60, r: 20, g: 205, b: 120 },
            { t: 0.65, r: 0, g: 220, b: 170 },
            { t: 0.70, r: 0, g: 235, b: 215 },
            { t: 0.75, r: 0, g: 255, b: 255 },
            { t: 0.80, r: 0, g: 205, b: 255 },
            { t: 0.85, r: 0, g: 155, b: 255 },
            { t: 0.90, r: 0, g: 105, b: 255 },
            { t: 0.95, r: 0, g: 50, b: 255 },
            { t: 1.00, r: 0, g: 0, b: 255 }
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
            trailCtx.fillStyle = '#000';
            trailCtx.fillRect(0, 0, trailCanvas.width, trailCanvas.height);
        }
    }

    // --- Environment Rendering ---
    function drawEnvironment() {
        const worldScreenOrigin = worldToScreen(0, 0);
        const worldScreenWidth = worldWidth * scale;
        const worldScreenHeight = worldHeight * scale;

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

        // Obstacles (Buildings / Barriers)
        if (obstacles && obstacles.length > 0) {
            for (let obs of obstacles) {
                const s = worldToScreen(obs.x, obs.y);
                const sw = obs.width * scale;
                const sh = obs.height * scale;

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
                const tzw = tgt.width * scale;
                const tzh = tgt.height * scale;

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
            const ezw = exitZone.width * scale;
            const ezh = exitZone.height * scale;

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
    function renderBinary(rawBuffer, count, renderMode = 'agents', showWhiskers = false, whiskerLength = 2.5, granulation = 1) {
        try {
            const floatArray = extractFloatArray(rawBuffer, count);
            if (floatArray && floatArray.length >= count * 5) {
                renderCore(floatArray, count, renderMode, showWhiskers, whiskerLength, granulation);
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
    function render(posX, posY, vx, vy, radius, count, renderMode = 'agents', showWhiskers = false, whiskerLength = 2.5, granulation = 1) {
        const floatArray = new Float32Array(count * 5);
        for (let i = 0; i < count; i++) {
            floatArray[i * 5] = posX[i];
            floatArray[i * 5 + 1] = posY[i];
            floatArray[i * 5 + 2] = vx[i];
            floatArray[i * 5 + 3] = vy[i];
            floatArray[i * 5 + 4] = radius[i];
        }
        renderCore(floatArray, count, renderMode, showWhiskers, whiskerLength, granulation);
    }

    function renderCore(floatArray, count, renderMode, showWhiskers, whiskerLength, granulation = 1) {
        lastFloatArray = floatArray;
        lastCount = count;
        lastRenderMode = renderMode;
        lastShowWhiskers = showWhiskers;
        lastWhiskerLength = whiskerLength;
        lastGranulation = granulation;

        const worldOrigin = worldToScreen(0, 0);
        const screenWorldW = worldWidth * scale;
        const screenWorldH = worldHeight * scale;

        if (renderMode === 'heatmap' || renderMode === 'density') {
            updateTrailCanvas();
            const isDensity = renderMode === 'density';
            trailCtx.fillStyle = isDensity ? 'rgba(0, 0, 0, 0.06)' : 'rgba(0, 0, 0, 0.08)';
            trailCtx.fillRect(0, 0, trailCanvas.width, trailCanvas.height);

            if (!isDensity) speedSum.fill(0);
            weightSum.fill(0);
            vxSum.fill(0);
            vySum.fill(0);

            const cellMetersX = worldWidth / gridCols;
            const cellMetersY = worldHeight / gridRows;
            const kernelRadiusMeters = Math.max(cellMetersX * 3.5, isDensity ? 20.0 : 15.0);
            const kernelRadiusSq = kernelRadiusMeters * kernelRadiusMeters;

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
                            const dist = Math.sqrt(distSq);
                            const weight = Math.max(0, 1.0 - (dist / kernelRadiusMeters));

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
                    if (w > 0.001) {
                        const rgb = getDensityColor(w);
                        const alpha = Math.min(255, Math.floor(Math.min(1.0, w / (trackedMaxDensity * 0.7)) * 255));
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
                    if (w > 0.001) {
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
                    if (w > 0.001) {
                        const avgSpeed = speedSum[i] / w;
                        const rgb = getHeatmapSpeedColor(avgSpeed);
                        const alpha = Math.min(255, Math.floor((w / 1.5) * 255));
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
            trailCtx.filter = isDensity ? 'blur(6.0px)' : 'blur(8.0px)';
            trailCtx.drawImage(offscreenCanvas, worldOrigin.x, worldOrigin.y, screenWorldW, screenWorldH);
            trailCtx.restore();

            ctx.fillStyle = '#080a0f';
            ctx.fillRect(0, 0, canvas.width, canvas.height);
            drawEnvironment();
            ctx.drawImage(trailCanvas, 0, 0);
            drawScaleBar();
            return;
        }

        // --- AGENTS RENDERING (BATCHED BY COLOR FOR 60 FPS) ---
        ctx.fillStyle = '#080a0f';
        ctx.fillRect(0, 0, canvas.width, canvas.height);

        drawEnvironment();

        // Optional whiskers (batched into single stroke)
        if (showWhiskers) {
            ctx.beginPath();
            ctx.strokeStyle = 'rgba(255, 255, 255, 0.35)';
            ctx.lineWidth = 1;
            const whiskerScreenLen = whiskerLength * scale;

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
            const r = Math.max(rawR * scale, 3.2);
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

        drawScaleBar();
    }

    return {
        render: render,
        renderBinary: renderBinary,
        setWorldConfig: function (config) {
            if (config.worldWidth) worldWidth = config.worldWidth;
            if (config.worldHeight) worldHeight = config.worldHeight;
            if (config.obstacles) obstacles = config.obstacles;
            if (config.targets) targets = config.targets;
            if (config.exitZone) exitZone = config.exitZone;
            fitBounds(worldWidth, worldHeight);
            initFlowParticles();
            redrawCurrent();
        },
        setTransform: function (t) {
            if (t.scale !== undefined) scale = t.scale;
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
            const centerX = canvas.width / 2;
            const centerY = canvas.height / 2;
            const wx = (centerX - offsetX) / scale;
            const wy = (centerY - offsetY) / scale;
            const newScale = Math.min(Math.max(scale * factor, 0.02), 50.0);
            scale = newScale;
            offsetX = centerX - wx * scale;
            offsetY = centerY - wy * scale;
            redrawCurrent();
        },
        setInteractive: function (enabled) {
            isInteractive = !!enabled;
        },
        dispose: function () {
            canvas.removeEventListener('pointerdown', onPointerDown);
            window.removeEventListener('pointermove', onPointerMove);
            window.removeEventListener('pointerup', onPointerUp);
            canvas.removeEventListener('wheel', onWheel);
            canvas.removeEventListener('dblclick', onDblClick);
        }
    };
}
