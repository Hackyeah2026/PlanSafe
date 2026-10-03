import test from "node:test";
import assert from "node:assert/strict";

// =========================================================================
// Pure Mathematical Port of Simulation Physics Kernels (matching C# and WGSL)
// =========================================================================

export const ComfortDensityThreshold = 0.7;
export const ConstrainedDensityThreshold = 2.2;
export const DenseCrowdThreshold = 4.7;
export const JamDensity = 5.4;

export function calculateSpeedFactor(density: number): number {
  if (density <= 0.0) return 1.0;
  if (density >= JamDensity) return 0.0;

  // Regime I: Comfort zone (rho <= 0.70)
  if (density <= ComfortDensityThreshold) {
    const t = density / ComfortDensityThreshold;
    return 1.0 - 0.149 * Math.pow(t, 1.6);
  }

  // Regime II: Constrained flow (0.70 < rho <= 2.20)
  if (density <= ConstrainedDensityThreshold) {
    const t =
      (density - ComfortDensityThreshold) /
      (ConstrainedDensityThreshold - ComfortDensityThreshold);
    return 0.851 - 0.5 * (t * (1.0 + 0.15 * (1.0 - t)));
  }

  // Regime III: Dense crowd queue (2.20 < rho <= 4.70)
  if (density <= DenseCrowdThreshold) {
    const t =
      (density - ConstrainedDensityThreshold) /
      (DenseCrowdThreshold - ConstrainedDensityThreshold);
    return 0.112 + (0.351 - 0.112) * Math.pow(1.0 - t, 1.4);
  }

  // Regime IV: Jam / Stoppage (4.70 < rho <= 5.40)
  const t =
    (density - DenseCrowdThreshold) / (JamDensity - DenseCrowdThreshold);
  return Math.max(0.0, 0.112 * (1.0 - Math.pow(t, 1.2)));
}

export function calculateEffectiveDensity(
  forwardKernelSum: number,
  closestForwardDist: number,
): number {
  let forwardDensity = 0.0;
  if (closestForwardDist > 0.05 && closestForwardDist < 1.8) {
    forwardDensity = 1.2 / (closestForwardDist * closestForwardDist);
  }

  if (forwardKernelSum <= ConstrainedDensityThreshold) {
    forwardDensity = Math.min(forwardDensity, ConstrainedDensityThreshold);
  } else {
    forwardDensity = Math.min(forwardDensity, JamDensity - 0.05);
  }

  return Math.max(forwardKernelSum, forwardDensity);
}

export function calculatePushingFactor(density: number): number {
  if (density <= ComfortDensityThreshold) return 0.0;
  if (density <= 2.5) {
    return (
      (density - ComfortDensityThreshold) / (2.5 - ComfortDensityThreshold)
    );
  }
  return 1.0 + 0.85 * (density - 2.5);
}

export function calculateRearPushingForce(
  density: number,
  distance: number,
  radiusSum: number,
  contactBuffer = 0.25,
  rearForwardDrive = 1.0,
): number {
  const pushingFactor = calculatePushingFactor(density);
  if (pushingFactor <= 0.0) return 0.0;

  const contactThreshold = radiusSum + contactBuffer;
  if (distance >= contactThreshold || distance <= 0.0001) return 0.0;

  const penetration = Math.max(0.0, radiusSum - distance);
  const contactPush = penetration * 4.0;

  const proximityFactor = (contactThreshold - distance) / contactBuffer;
  const drivePush = Math.max(0.0, rearForwardDrive) * 1.5 * proximityFactor;

  return pushingFactor * (contactPush + drivePush);
}

export interface WallInteractionResult {
  normalX: number;
  normalY: number;
  repulsionX: number;
  repulsionY: number;
  travelX: number;
  travelY: number;
}

export function calculateWallInteraction(
  px: number,
  py: number,
  obsX: number,
  obsY: number,
  obsW: number,
  obsH: number,
  agentRadius: number,
  initialTravelX: number,
  initialTravelY: number,
): WallInteractionResult {
  const comfortDistance = agentRadius + 0.6;
  const comfortDistSq = comfortDistance * comfortDistance;

  const nbpX = Math.max(obsX, Math.min(px, obsX + obsW));
  const nbpY = Math.max(obsY, Math.min(py, obsY + obsH));
  const dx = px - nbpX;
  const dy = py - nbpY;
  const distSq = dx * dx + dy * dy;

  let normalX = 0;
  let normalY = 0;
  let repulsionX = 0;
  let repulsionY = 0;
  let travelX = initialTravelX;
  let travelY = initialTravelY;

  if (distSq < comfortDistSq && distSq > 0.000001) {
    const dist = Math.sqrt(distSq);
    normalX = dx / dist;
    normalY = dy / dist;
    const strength = (3.5 * (comfortDistance - dist)) / comfortDistance;
    repulsionX = normalX * strength;
    repulsionY = normalY * strength;

    const dotWithNormal = travelX * normalX + travelY * normalY;
    if (dotWithNormal < 0) {
      travelX -= dotWithNormal * normalX;
      travelY -= dotWithNormal * normalY;
      const slideSpeed = Math.sqrt(travelX * travelX + travelY * travelY);
      if (slideSpeed > 0.05) {
        travelX /= slideSpeed;
        travelY /= slideSpeed;
      } else {
        const tan1X = -normalY;
        const tan1Y = normalX;
        const obsCenterY = obsY + obsH * 0.5;
        const bypassSign = py < obsCenterY ? -1.0 : 1.0;
        if (tan1Y * bypassSign >= 0) {
          travelX = tan1X;
          travelY = tan1Y;
        } else {
          travelX = -tan1X;
          travelY = -tan1Y;
        }
      }
    }
  }

  return { normalX, normalY, repulsionX, repulsionY, travelX, travelY };
}

export function resolveCircleBoxCollision(
  px: number,
  py: number,
  velX: number,
  velY: number,
  obsX: number,
  obsY: number,
  obsW: number,
  obsH: number,
  radius: number,
): { px: number; py: number; velX: number; velY: number } {
  const nbpX = Math.max(obsX, Math.min(px, obsX + obsW));
  const nbpY = Math.max(obsY, Math.min(py, obsY + obsH));
  const dx = px - nbpX;
  const dy = py - nbpY;
  const distSq = dx * dx + dy * dy;

  let outPx = px;
  let outPy = py;
  let outVx = velX;
  let outVy = velY;

  if (distSq < radius * radius) {
    if (distSq > 0.000001) {
      const dist = Math.sqrt(distSq);
      const normX = dx / dist;
      const normY = dy / dist;
      const pen = radius - dist;
      outPx += normX * pen;
      outPy += normY * pen;
      const nVel = outVx * normX + outVy * normY;
      if (nVel < 0) {
        outVx -= nVel * normX;
        outVy -= nVel * normY;
      }
    } else {
      const dLeft = px - obsX;
      const dRight = obsX + obsW - px;
      const dTop = py - obsY;
      const dBottom = obsY + obsH - py;
      const minD = Math.min(dLeft, dRight, dTop, dBottom);
      if (minD === dLeft) {
        outPx = obsX - radius;
        if (outVx > 0) outVx = 0;
      } else if (minD === dRight) {
        outPx = obsX + obsW + radius;
        if (outVx < 0) outVx = 0;
      } else if (minD === dTop) {
        outPy = obsY - radius;
        if (outVy > 0) outVy = 0;
      } else {
        outPy = obsY + obsH + radius;
        if (outVy < 0) outVy = 0;
      }
    }
  }

  return { px: outPx, py: outPy, velX: outVx, velY: outVy };
}

// =========================================================================
// Test Suite: 1:1 Parity and Corner Case Validation
// =========================================================================

test("Fundamental Diagram SpeedFactor matches C# reference exactly across all regimes and corner cases", () => {
  const testCases = [
    { density: 0.0, expected: 1.0 },
    { density: 0.35, expected: 0.950848 },
    { density: 0.7, expected: 0.851 },
    { density: 1.45, expected: 0.58225 },
    { density: 2.2, expected: 0.351 },
    { density: 3.45, expected: 0.202564 },
    { density: 4.7, expected: 0.112 },
    { density: 5.05, expected: 0.063249 },
    { density: 5.4, expected: 0.0 },
    { density: 6.0, expected: 0.0 },
    { density: -0.5, expected: 1.0 },
  ];

  for (const { density, expected } of testCases) {
    const actual = calculateSpeedFactor(density);
    assert.ok(
      Math.abs(actual - expected) < 1e-4,
      `SpeedFactor for rho=${density}: expected ${expected}, got ${actual}`,
    );
  }
});

test("Fundamental Diagram EffectiveDensity matches C# corner cases", () => {
  // Singularity (< 0.05m): no boost
  assert.ok(Math.abs(calculateEffectiveDensity(0.5, 0.02) - 0.5) < 1e-4);

  // Free flow with close leader: clamped to 2.20
  assert.ok(Math.abs(calculateEffectiveDensity(0.5, 0.5) - 2.2) < 1e-4);

  // Dense crowd with close leader: rises up to 4.80
  assert.ok(Math.abs(calculateEffectiveDensity(3.0, 0.5) - 4.8) < 1e-4);

  // Extreme close leader in dense crowd: clamped to 5.35
  assert.ok(Math.abs(calculateEffectiveDensity(3.0, 0.2) - 5.35) < 1e-4);

  // Distant leader: returns forwardKernelSum
  assert.ok(Math.abs(calculateEffectiveDensity(1.5, 2.5) - 1.5) < 1e-4);
});

test("Fundamental Diagram PushingFactor matches C# values", () => {
  assert.equal(calculatePushingFactor(0.0), 0.0);
  assert.equal(calculatePushingFactor(0.7), 0.0);
  assert.ok(Math.abs(calculatePushingFactor(1.6) - 0.5) < 1e-4);
  assert.equal(calculatePushingFactor(2.5), 1.0);
  assert.ok(Math.abs(calculatePushingFactor(3.5) - 1.85) < 1e-4);
});

test("Fundamental Diagram RearPushingForce matches C# penetration and drive push", () => {
  const radiusSum = 0.7;
  const contactBuffer = 0.25;
  const rearDrive = 1.2;

  // Free density -> zero push
  assert.equal(
    calculateRearPushingForce(0.5, 0.6, radiusSum, contactBuffer, rearDrive),
    0.0,
  );

  // Beyond threshold -> zero push
  assert.equal(
    calculateRearPushingForce(3.0, 0.96, radiusSum, contactBuffer, rearDrive),
    0.0,
  );

  // Singularity distance -> zero push
  assert.equal(
    calculateRearPushingForce(
      3.0,
      0.00005,
      radiusSum,
      contactBuffer,
      rearDrive,
    ),
    0.0,
  );

  // Penetration contact: exactly 2.36
  const forcePen = calculateRearPushingForce(
    2.5,
    0.65,
    radiusSum,
    contactBuffer,
    rearDrive,
  );
  assert.ok(Math.abs(forcePen - 2.36) < 1e-4, `Expected 2.36, got ${forcePen}`);

  // Contact without penetration: exactly 0.36
  const forceContact = calculateRearPushingForce(
    1.6,
    0.85,
    radiusSum,
    contactBuffer,
    rearDrive,
  );
  assert.ok(
    Math.abs(forceContact - 0.36) < 1e-4,
    `Expected 0.36, got ${forceContact}`,
  );
});

test("Wall interaction repulsion and sliding match C# head-on collision and bypass", () => {
  const agentRadius = 0.35;
  const obs = { x: 70.0, y: 36.0, w: 24.0, h: 56.0 };

  // Agent 0.5m west of obstacle wall, heading directly east (head-on collision)
  const result = calculateWallInteraction(
    69.5,
    50.0,
    obs.x,
    obs.y,
    obs.w,
    obs.h,
    agentRadius,
    1.0,
    0.0,
  );

  // Normal vector points west (-1, 0)
  assert.ok(Math.abs(result.normalX - -1.0) < 1e-5);
  assert.ok(Math.abs(result.normalY - 0.0) < 1e-5);
  assert.ok(result.repulsionX < -1.0);

  // Head-on collision bypass: steers south (-Y) along wall towards bottom
  assert.ok(Math.abs(result.travelX - 0.0) < 1e-5);
  assert.ok(Math.abs(result.travelY - -1.0) < 1e-5);
});

test("Circle-box collision resolution eliminates penetration and zeroes normal velocity", () => {
  const obs = { x: 70.0, y: 36.0, w: 24.0, h: 56.0 };
  const radius = 0.35;

  // Agent penetrates west wall by 0.15m (px = 70.2, inside box, heading east vx = 1.2)
  const resolved = resolveCircleBoxCollision(
    70.2,
    50.0,
    1.2,
    0.0,
    obs.x,
    obs.y,
    obs.w,
    obs.h,
    radius,
  );

  // Agent must be pushed out to west of obstacle (px = 70.0 - radius = 69.65)
  assert.ok(Math.abs(resolved.px - (obs.x - radius)) < 1e-4);
  // Velocity in direction of wall (+X) must be zeroed
  assert.equal(resolved.velX, 0.0);
});

// =========================================================================
// Potential Field & Flow Direction Port
// =========================================================================

export function buildStaticPotentialField(
  width = 200.0,
  height = 200.0,
  obs: { x: number; y: number; w: number; h: number }[],
  exitZone: { x: number; y: number; w: number; h: number },
  cols = 100,
  rows = 100,
): Float32Array {
  const cellW = width / cols;
  const cellH = height / rows;
  const total = cols * rows;
  const pot = new Float32Array(total).fill(1e9);
  const blocked = new Uint8Array(total);

  for (let r = 0; r < rows; r++) {
    const wy = (r + 0.5) * cellH;
    for (let c = 0; c < cols; c++) {
      const wx = (c + 0.5) * cellW;
      for (const o of obs) {
        if (
          wx > o.x + 0.001 &&
          wx < o.x + o.w - 0.001 &&
          wy > o.y + 0.001 &&
          wy < o.y + o.h - 0.001
        ) {
          blocked[r * cols + c] = 1;
          break;
        }
      }
    }
  }

  const dCol = [1, -1, 0, 0, 1, 1, -1, -1];
  const dRow = [0, 0, 1, -1, 1, -1, 1, -1];
  const step = cellW;
  const diagStep = cellW * 1.41421356;
  const costs = [
    step,
    step,
    step,
    step,
    diagStep,
    diagStep,
    diagStep,
    diagStep,
  ];

  // Min-heap
  const heapNodes = new Int32Array(total * 4);
  const heapPrios = new Float32Array(total * 4);
  let heapSize = 0;

  function push(n: number, p: number) {
    let i = heapSize++;
    heapNodes[i] = n;
    heapPrios[i] = p;
    while (i > 0) {
      const parent = (i - 1) >> 1;
      if (heapPrios[parent] <= heapPrios[i]) break;
      const tn = heapNodes[parent];
      const tp = heapPrios[parent];
      heapNodes[parent] = heapNodes[i];
      heapPrios[parent] = heapPrios[i];
      heapNodes[i] = tn;
      heapPrios[i] = tp;
      i = parent;
    }
  }

  function pop(): { node: number; priority: number } | null {
    if (heapSize === 0) return null;
    const topNode = heapNodes[0];
    const topPrio = heapPrios[0];
    const last = --heapSize;
    heapNodes[0] = heapNodes[last];
    heapPrios[0] = heapPrios[last];
    let i = 0;
    while (true) {
      const left = (i << 1) + 1;
      const right = left + 1;
      if (left >= heapSize) break;
      let smallest = left;
      if (right < heapSize && heapPrios[right] < heapPrios[left]) {
        smallest = right;
      }
      if (heapPrios[i] <= heapPrios[smallest]) break;
      const tn = heapNodes[i];
      const tp = heapPrios[i];
      heapNodes[i] = heapNodes[smallest];
      heapPrios[i] = heapPrios[smallest];
      heapNodes[smallest] = tn;
      heapPrios[smallest] = tp;
      i = smallest;
    }
    return { node: topNode, priority: topPrio };
  }

  const minCol = Math.max(
    0,
    Math.min(cols - 1, Math.floor(exitZone.x / cellW)),
  );
  const maxCol = Math.max(
    0,
    Math.min(cols - 1, Math.floor((exitZone.x + exitZone.w) / cellW)),
  );
  const minRow = Math.max(
    0,
    Math.min(rows - 1, Math.floor(exitZone.y / cellH)),
  );
  const maxRow = Math.max(
    0,
    Math.min(rows - 1, Math.floor((exitZone.y + exitZone.h) / cellH)),
  );

  for (let r = minRow; r <= maxRow; r++) {
    for (let c = minCol; c <= maxCol; c++) {
      const idx = r * cols + c;
      if (!blocked[idx]) {
        pot[idx] = 0;
        push(idx, 0);
      }
    }
  }

  while (true) {
    const item = pop();
    if (!item) break;
    const currIdx = item.node;
    const currPot = item.priority;
    if (currPot > pot[currIdx]) continue;

    const currCol = currIdx % cols;
    const currRow = (currIdx / cols) | 0;

    for (let i = 0; i < 8; i++) {
      const nCol = currCol + dCol[i];
      const nRow = currRow + dRow[i];
      if (nCol >= 0 && nCol < cols && nRow >= 0 && nRow < rows) {
        const nIdx = nRow * cols + nCol;
        if (blocked[nIdx]) continue;

        if (i >= 4) {
          if (
            blocked[currRow * cols + nCol] &&
            blocked[nRow * cols + currCol]
          ) {
            continue;
          }
        }

        const candPot = currPot + costs[i];
        if (candPot < pot[nIdx]) {
          pot[nIdx] = candPot;
          push(nIdx, candPot);
        }
      }
    }
  }

  return pot;
}

export function getFlowDirection(
  px: number,
  py: number,
  pot: Float32Array,
  width = 200.0,
  height = 200.0,
  cols = 100,
  rows = 100,
): { fx: number; fy: number } {
  const invCellW = cols / width;
  const invCellH = rows / height;
  const u = px * invCellW - 0.5;
  const v = py * invCellH - 0.5;

  const c0 = Math.max(0, Math.min(cols - 2, Math.floor(u)));
  const r0 = Math.max(0, Math.min(rows - 2, Math.floor(v)));
  const s = Math.max(0, Math.min(1.0, u - c0));
  const t = Math.max(0, Math.min(1.0, v - r0));

  const idx00 = r0 * cols + c0;
  const idx10 = idx00 + 1;
  const idx01 = idx00 + cols;
  const idx11 = idx01 + 1;

  const p00 = pot[idx00];
  const p10 = pot[idx10];
  const p01 = pot[idx01];
  const p11 = pot[idx11];

  const maxValidPot = 900000.0;
  const v00 = p00 < maxValidPot;
  const v10 = p10 < maxValidPot;
  const v01 = p01 < maxValidPot;
  const v11 = p11 < maxValidPot;

  const nearestCol = Math.max(0, Math.min(cols - 1, Math.round(u)));
  const nearestRow = Math.max(0, Math.min(rows - 1, Math.round(v)));
  const nearestIdx = nearestRow * cols + nearestCol;

  const cOffsets = [0, 0, -1, 1, -1, 1, -1, 1];
  const rOffsets = [-1, 1, 0, 0, -1, -1, 1, 1];

  if (pot[nearestIdx] >= maxValidPot || (!v00 && !v10 && !v01 && !v11)) {
    let bestPot = maxValidPot;
    let bestDx = 1.0;
    let bestDy = 0.0;
    for (let k = 0; k < 8; k++) {
      const nc = nearestCol + cOffsets[k];
      const nr = nearestRow + rOffsets[k];
      if (nc >= 0 && nc < cols && nr >= 0 && nr < rows) {
        const nPot = pot[nr * cols + nc];
        if (nPot < bestPot) {
          bestPot = nPot;
          bestDx = cOffsets[k];
          bestDy = rOffsets[k];
        }
      }
    }
    const mag = Math.sqrt(bestDx * bestDx + bestDy * bestDy);
    return mag > 0.0001
      ? { fx: bestDx / mag, fy: bestDy / mag }
      : { fx: 1.0, fy: 0.0 };
  }

  let flowX = 0;
  let flowY = 0;

  if (v00 && v10 && v01 && v11) {
    flowX = (1.0 - t) * (p00 - p10) + t * (p01 - p11);
    flowY = (1.0 - s) * (p00 - p01) + s * (p10 - p11);
  } else {
    let gradX0 = 0;
    let hasGX0 = false;
    if (v00 && v10) {
      gradX0 = p00 - p10;
      hasGX0 = true;
    } else if (v00 && !v10) {
      gradX0 = -1.0;
      hasGX0 = true;
    } else if (!v00 && v10) {
      gradX0 = 1.0;
      hasGX0 = true;
    }

    let gradX1 = 0;
    let hasGX1 = false;
    if (v01 && v11) {
      gradX1 = p01 - p11;
      hasGX1 = true;
    } else if (v01 && !v11) {
      gradX1 = -1.0;
      hasGX1 = true;
    } else if (!v01 && v11) {
      gradX1 = 1.0;
      hasGX1 = true;
    }

    if (hasGX0 && hasGX1) flowX = (1.0 - t) * gradX0 + t * gradX1;
    else if (hasGX0) flowX = gradX0;
    else if (hasGX1) flowX = gradX1;

    let gradY0 = 0;
    let hasGY0 = false;
    if (v00 && v01) {
      gradY0 = p00 - p01;
      hasGY0 = true;
    } else if (v00 && !v01) {
      gradY0 = -1.0;
      hasGY0 = true;
    } else if (!v00 && v01) {
      gradY0 = 1.0;
      hasGY0 = true;
    }

    let gradY1 = 0;
    let hasGY1 = false;
    if (v10 && v11) {
      gradY1 = p10 - p11;
      hasGY1 = true;
    } else if (v10 && !v11) {
      gradY1 = -1.0;
      hasGY1 = true;
    } else if (!v10 && v11) {
      gradY1 = 1.0;
      hasGY1 = true;
    }

    if (hasGY0 && hasGY1) flowY = (1.0 - s) * gradY0 + s * gradY1;
    else if (hasGY0) flowY = gradY0;
    else if (hasGY1) flowY = gradY1;
  }

  const gradMag = Math.sqrt(flowX * flowX + flowY * flowY);
  return gradMag > 0.0001
    ? { fx: flowX / gradMag, fy: flowY / gradMag }
    : { fx: 1.0, fy: 0.0 };
}

test("Potential field and bilinear flow direction match C# reference tests", () => {
  const obstacles = [
    { x: 70.0, y: 36.0, w: 24.0, h: 56.0 },
    { x: 70.0, y: 108.0, w: 24.0, h: 56.0 },
  ];
  const exitZone = { x: 184.0, y: 80.0, w: 12.0, h: 40.0 };

  const pot = buildStaticPotentialField(200.0, 200.0, obstacles, exitZone);

  // Open field before obstacles (x = 30.0, y = 100.0):
  const openFlow = getFlowDirection(30.0, 100.0, pot);
  const magOpen = Math.sqrt(
    openFlow.fx * openFlow.fx + openFlow.fy * openFlow.fy,
  );
  assert.ok(Math.abs(magOpen - 1.0) < 1e-4);
  assert.ok(openFlow.fx > 0.9);
  assert.ok(Math.abs(openFlow.fy) < 0.2);

  // Near western wall of top obstacle (x = 68.0, y = 60.0):
  // Must NOT point backwards (fx must be >= 0)
  const wallFlow = getFlowDirection(68.0, 60.0, pot);
  assert.ok(wallFlow.fx >= 0.0, `Expected fx >= 0, got ${wallFlow.fx}`);
});

test("Velocity smoothing inertia matches C# rules across regimes and corridors", () => {
  function getVelocityInertia(
    density: number,
    isInsideCorridor: boolean,
    currentVelMag: number,
    speedCap: number,
    granulation = 1,
  ): number {
    let inertia = 0.25;
    if (density >= DenseCrowdThreshold) {
      inertia = currentVelMag > speedCap ? 0.85 : 0.35;
    } else if (isInsideCorridor) {
      inertia = 0.55;
    }
    if (granulation > 1) {
      const inertiaDamping = Math.min(1.75, Math.pow(granulation, 0.18));
      inertia = Math.max(0.12, inertia / inertiaDamping);
    }
    return inertia;
  }

  // Regime IV: sudden brake into jam (currentVel > speedCap) -> 0.85
  assert.equal(getVelocityInertia(4.8, false, 1.2, 0.1), 0.85);
  // Regime IV: steady in jam (currentVel <= speedCap) -> 0.35
  assert.equal(getVelocityInertia(4.8, false, 0.05, 0.1), 0.35);
  // Corridor -> 0.55
  assert.equal(getVelocityInertia(1.0, true, 0.8, 1.2), 0.55);
  // Free space -> 0.25
  assert.equal(getVelocityInertia(0.5, false, 1.2, 1.4), 0.25);
});

test("Adaptive spatial hash grid maintains physical cell invariants across map sizes", () => {
  function computeAdaptiveGrid(worldWidth: number, worldHeight: number) {
    const targetCellSize = 4.0;
    let nextCols = Math.max(10, Math.ceil(worldWidth / targetCellSize));
    let nextRows = Math.max(10, Math.ceil(worldHeight / targetCellSize));
    const maxDimension = 250;
    if (nextCols > maxDimension || nextRows > maxDimension) {
      const maxDim = Math.max(nextCols, nextRows);
      nextCols = Math.max(10, Math.round((nextCols / maxDim) * maxDimension));
      nextRows = Math.max(10, Math.round((nextRows / maxDim) * maxDimension));
    }
    const cellW = worldWidth / nextCols;
    const cellH = worldHeight / nextRows;
    return { cols: nextCols, rows: nextRows, cellW, cellH };
  }

  const mapSizes = [200, 500, 1000];
  const maxPerCell = 128;
  const spatialSearchRadius = 2.8;

  for (const size of mapSizes) {
    const grid = computeAdaptiveGrid(size, size);

    // 1. Grid cell dimension must never be smaller than the physical search radius (2.8m)
    // This mathematically guarantees all physical neighbors lie in the 3x3 search stencil.
    assert.ok(
      grid.cellW >= spatialSearchRadius,
      `Map ${size}m: cellW (${grid.cellW}) < searchRadius (${spatialSearchRadius})`,
    );
    assert.ok(
      grid.cellH >= spatialSearchRadius,
      `Map ${size}m: cellH (${grid.cellH}) < searchRadius (${spatialSearchRadius})`,
    );

    // 2. Physical crush limit of humans (5.4 os/m²) must fit within maxPerCell buffer capacity
    const cellArea = grid.cellW * grid.cellH;
    const theoreticalMaxCrushAgents = Math.floor(cellArea * JamDensity);
    assert.ok(
      theoreticalMaxCrushAgents <= maxPerCell,
      `Map ${size}m: cellArea ${cellArea}m² allows ${theoreticalMaxCrushAgents} agents at jam density, exceeding maxPerCell ${maxPerCell}`,
    );

    // 3. For any two agents separated by <= searchRadius, their cell delta must be in {-1, 0, 1}
    for (let offset = 0.05; offset <= spatialSearchRadius; offset += 0.25) {
      const p1x = size * 0.5;
      const p1y = size * 0.5;
      const p2x = p1x + offset;
      const p2y = p1y + offset * 0.5;

      const col1 = Math.floor(p1x / grid.cellW);
      const row1 = Math.floor(p1y / grid.cellH);
      const col2 = Math.floor(p2x / grid.cellW);
      const row2 = Math.floor(p2y / grid.cellH);

      assert.ok(
        Math.abs(col1 - col2) <= 1,
        `Delta col ${Math.abs(col1 - col2)} exceeds 1 for distance ${offset}m on map ${size}m`,
      );
      assert.ok(
        Math.abs(row1 - row2) <= 1,
        `Delta row ${Math.abs(row1 - row2)} exceeds 1 for distance ${offset}m on map ${size}m`,
      );
    }
  }

  // Verify specifically for 1000m:
  const grid1k = computeAdaptiveGrid(1000, 1000);
  assert.equal(grid1k.cols, 250);
  assert.equal(grid1k.rows, 250);
  assert.equal(grid1k.cellW, 4.0);
  assert.equal(grid1k.cellH, 4.0);
});

test("Dynamic field relaxation throttling preserves normal rate and caps extreme speed passes", () => {
  function simulateRelaxationSchedule(
    ticksPerSecond: number,
    totalSeconds: number,
  ) {
    let dynamicFieldTimer = 0.2;
    let lastDynamicFieldWallTime = 0;
    let nowWallTime = 0;
    let updates = 0;
    const dt = 0.016;
    const realTimePerTick = 1000 / ticksPerSecond;

    const totalTicks = Math.round(ticksPerSecond * totalSeconds);
    for (let tick = 0; tick < totalTicks; tick++) {
      nowWallTime += realTimePerTick;
      dynamicFieldTimer += dt;

      const wallElapsed = nowWallTime - lastDynamicFieldWallTime;
      const isInitial = tick === 0;
      if (isInitial || (dynamicFieldTimer >= 0.2 && wallElapsed >= 80)) {
        dynamicFieldTimer = 0.0;
        lastDynamicFieldWallTime = nowWallTime;
        updates++;
      }
    }
    return updates;
  }

  // At 1x speed (60 ticks/s for 5 seconds = 300 ticks, 5.0s sim time):
  // Should trigger ~5 times per second = ~25 times total (matching reference 0.20s intervals)
  const updatesAt1x = simulateRelaxationSchedule(60, 5.0);
  assert.ok(
    updatesAt1x >= 24 && updatesAt1x <= 26,
    `Expected ~25 updates at 1x, got ${updatesAt1x}`,
  );

  // At 20x speed (1200 ticks/s for 5 seconds):
  // Without throttling, it would trigger 100 times/second = 500 times total!
  // With throttling (every >= 80ms), it is capped to ~12.5 updates/second = ~62 times total
  const updatesAt20x = simulateRelaxationSchedule(1200, 5.0);
  assert.ok(
    updatesAt20x <= 65,
    `Expected <= 65 updates at 20x, got ${updatesAt20x} (throttled from 500)`,
  );
  assert.ok(
    updatesAt20x >= 55,
    `Expected >= 55 updates at 20x, got ${updatesAt20x}`,
  );
});
