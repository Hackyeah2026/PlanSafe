export const stepPhysicsShader = `
struct Agent {
    pos: vec2<f32>,
    vel: vec2<f32>,
    radius: f32,
    desiredSpeed: f32,
    flags: u32,
    pad: u32,
};

struct RecoveryState {
    anchor: vec2<f32>,
    stationaryTicks: u32,
    pad: u32,
};

struct SimParams {
    worldWidth: f32,
    worldHeight: f32,
    dt: f32,
    socialWeight: f32,
    agentCount: u32,
    gridCols: u32,
    gridRows: u32,
    maxPerCell: u32,
    obs1: vec4<f32>,
    obs2: vec4<f32>,
    exitZone: vec4<f32>,
    granulation: u32,
    isMap: u32,
    mapCols: u32,
    mapRows: u32,
    mapCellSize: f32,
    numExits: u32,
    potCols: u32,
    potRows: u32,
    potCellSize: f32,
    pad0: u32,
    routingEnabled: u32,
    safeZoneCount: u32,
};

@group(0) @binding(0) var<storage, read_write> agents: array<Agent>;
@group(0) @binding(1) var<storage, read_write> cellCounts: array<atomic<u32>>;
@group(0) @binding(2) var<storage, read> cellAgents: array<u32>;
@group(0) @binding(3) var<uniform> params: SimParams;
@group(0) @binding(4) var<storage, read> potentialGrid: array<f32>;
@group(0) @binding(5) var<storage, read> smoothedDensityGrid: array<f32>;
@group(0) @binding(6) var<storage, read> blockedRaster: array<u32>;
@group(0) @binding(7) var<storage, read> exits: array<vec4<f32>>;
@group(0) @binding(8) var<storage, read_write> recovery: array<RecoveryState>;

const cOffsets = array<i32, 8>(0, 0, -1, 1, -1, 1, -1, 1);
const rOffsets = array<i32, 8>(-1, 1, 0, 0, -1, -1, 1, 1);

fn mapPositionBlocked(pos: vec2<f32>) -> bool {
    let col = i32(floor(pos.x / params.mapCellSize));
    let row = i32(floor(pos.y / params.mapCellSize));
    if (col < 0 || row < 0 || col >= i32(params.mapCols) || row >= i32(params.mapRows)) { return true; }
    return (blockedRaster[u32(row) * params.mapCols + u32(col)] & 1u) != 0u;
}

fn mapCircleClear(pos: vec2<f32>, radius: f32) -> bool {
    if (any(pos < vec2<f32>(radius)) || any(pos > vec2<f32>(params.worldWidth - radius, params.worldHeight - radius))) { return false; }
    let cell = params.mapCellSize;
    let first = max(vec2<i32>(0), vec2<i32>(floor((pos - vec2<f32>(radius)) / cell)));
    let last = min(vec2<i32>(i32(params.mapCols) - 1, i32(params.mapRows) - 1), vec2<i32>(floor((pos + vec2<f32>(radius)) / cell)));
    for (var row = first.y; row <= last.y; row++) {
        for (var col = first.x; col <= last.x; col++) {
            if ((blockedRaster[u32(row) * params.mapCols + u32(col)] & 1u) == 0u) { continue; }
            let lo = vec2<f32>(f32(col), f32(row)) * cell;
            let d = pos - clamp(pos, lo, lo + vec2<f32>(cell));
            if (dot(d, d) < radius * radius - 0.000001) { return false; }
        }
    }
    return true;
}

fn mapMovementBlocked(startPos: vec2<f32>, endPos: vec2<f32>) -> bool {
    let cell = params.mapCellSize;
    let first = max(vec2<i32>(0), vec2<i32>(floor(min(startPos, endPos) / cell)));
    let last = min(vec2<i32>(i32(params.mapCols) - 1, i32(params.mapRows) - 1), vec2<i32>(floor(max(startPos, endPos) / cell)));
    let delta = endPos - startPos;
    for (var row = first.y; row <= last.y; row++) {
        for (var col = first.x; col <= last.x; col++) {
            if ((blockedRaster[u32(row) * params.mapCols + u32(col)] & 1u) == 0u) { continue; }
            let lo = vec2<f32>(f32(col), f32(row)) * cell;
            let hi = lo + vec2<f32>(cell);
            var enter = 0.0;
            var leave = 1.0;
            var intersects = true;
            for (var axis = 0u; axis < 2u; axis++) {
                if (abs(delta[axis]) < 0.000001) {
                    if (startPos[axis] < lo[axis] || startPos[axis] > hi[axis]) { intersects = false; }
                } else {
                    let a = (lo[axis] - startPos[axis]) / delta[axis];
                    let b = (hi[axis] - startPos[axis]) / delta[axis];
                    enter = max(enter, min(a, b));
                    leave = min(leave, max(a, b));
                }
            }
            if (intersects && enter <= leave && leave > 0.0 && enter < 1.0) { return true; }
        }
    }
    return false;
}


fn recoverySpace(pos: vec2<f32>, radius: f32, agentIndex: u32) -> bool {
    let cell = vec2<f32>(params.worldWidth / f32(params.gridCols), params.worldHeight / f32(params.gridRows));
    let search = 2.0 * radius + 0.1;
    let first = max(vec2<i32>(0), vec2<i32>(floor((pos - vec2<f32>(search)) / cell)));
    let last = min(vec2<i32>(i32(params.gridCols) - 1, i32(params.gridRows) - 1), vec2<i32>(floor((pos + vec2<f32>(search)) / cell)));
    for (var row = first.y; row <= last.y; row++) {
        for (var col = first.x; col <= last.x; col++) {
            let cellId = u32(row) * params.gridCols + u32(col);
            let count = min(atomicLoad(&cellCounts[cellId]) & 0x7fffffffu, params.maxPerCell);
            for (var k = 0u; k < count; k++) {
                let neighborIndex = cellAgents[cellId * params.maxPerCell + k];
                if (neighborIndex == agentIndex || agents[neighborIndex].flags == 0u) { continue; }
                let neighbor = agents[neighborIndex];
                let d = neighbor.pos - pos;
                let separation = radius + neighbor.radius + 0.1;
                if (dot(d, d) < separation * separation) { return false; }
            }
        }
    }
    return true;
}

// Reserve the destination footprint until the next grid rebuild so agents
// recovering in parallel cannot choose overlapping positions. The high bit
// is excluded from neighbor counts and cleared by the existing grid pass.
fn reserveRecoverySpace(pos: vec2<f32>, radius: f32) -> bool {
    let cell = vec2<f32>(params.worldWidth / f32(params.gridCols), params.worldHeight / f32(params.gridRows));
    let footprint = radius + 0.05;
    let first = max(vec2<i32>(0), vec2<i32>(floor((pos - vec2<f32>(footprint)) / cell)));
    let last = min(vec2<i32>(i32(params.gridCols) - 1, i32(params.gridRows) - 1), vec2<i32>(floor((pos + vec2<f32>(footprint)) / cell)));
    for (var row = first.y; row <= last.y; row++) {
        for (var col = first.x; col <= last.x; col++) {
            let previous = atomicOr(&cellCounts[u32(row) * params.gridCols + u32(col)], 0x80000000u);
            if ((previous & 0x80000000u) != 0u) { return false; }
        }
    }
    return true;
}

fn nearestRecoveryStreet(pos: vec2<f32>, radius: f32, agentIndex: u32) -> vec2<f32> {
    let cell = params.mapCellSize;
    let center = vec2<i32>(floor(pos / cell));
    var best = vec2<f32>(-1.0);
    var bestDistance = 1e30;
    var bestIndex = 0xffffffffu;
    let minimumMove = max(0.5, radius * 2.0);
    let reach = i32(max(params.mapCols, params.mapRows));
    for (var ring = 0; ring <= reach; ring++) {
        for (var edge = 0; edge < 4; edge++) {
            for (var offset = -ring; offset <= ring; offset++) {
                var col = center.x + offset;
                var row = center.y - ring;
                if (edge == 1) { row = center.y + ring; }
                if (edge == 2) { col = center.x - ring; row = center.y + offset; }
                if (edge == 3) { col = center.x + ring; row = center.y + offset; }
                if (col < 0 || row < 0 || col >= i32(params.mapCols) || row >= i32(params.mapRows)) { continue; }
                let index = u32(row) * params.mapCols + u32(col);
                if ((blockedRaster[index] & 3u) != 2u) { continue; }
                let candidate = (vec2<f32>(f32(col), f32(row)) + vec2<f32>(0.5)) * cell;
                let d = candidate - pos;
                let distance = dot(d, d);
                if (distance < minimumMove * minimumMove || distance > bestDistance || (distance == bestDistance && index >= bestIndex)) { continue; }
                if (!mapCircleClear(candidate, radius + 0.6) || !recoverySpace(candidate, radius, agentIndex)) { continue; }
                let pc = clamp(vec2<i32>(floor(candidate / params.potCellSize)), vec2<i32>(0), vec2<i32>(i32(params.potCols) - 1, i32(params.potRows) - 1));
                if (potentialGrid[u32(pc.y) * params.potCols + u32(pc.x)] >= 1.7014117e38) { continue; }
                bestDistance = distance;
                bestIndex = index;
                best = candidate;
            }
        }
        let remaining = min(min(pos.x - (f32(center.x - ring) - 0.5) * cell, (f32(center.x + ring) + 1.5) * cell - pos.x),
            min(pos.y - (f32(center.y - ring) - 0.5) * cell, (f32(center.y + ring) + 1.5) * cell - pos.y));
        if (best.x >= 0.0 && remaining > 0.0 && remaining * remaining > bestDistance) { break; }
    }
    if (best.x >= 0.0 && !reserveRecoverySpace(best, radius)) { return vec2<f32>(-2.0); }
    return best;
}

// Bilinear gradient interpolation matching C# GetFlowDirection exactly
fn getFlowDirection(pos: vec2<f32>, assignedTarget: i32) -> vec2<f32> {
    let pCols = params.potCols;
    let fieldOffset = select(0u, (u32(max(0, assignedTarget)) + 1u) * params.potCols * params.potRows, params.routingEnabled == 1u && assignedTarget >= 0);
    let pRows = params.potRows;
    let invCellW = 1.0 / params.potCellSize;
    let invCellH = 1.0 / params.potCellSize;

    let u = pos.x * invCellW - 0.5;
    let v = pos.y * invCellH - 0.5;

    let c0 = clamp(i32(floor(u)), 0, i32(pCols) - 2);
    let r0 = clamp(i32(floor(v)), 0, i32(pRows) - 2);

    let s = clamp(u - f32(c0), 0.0, 1.0);
    let t = clamp(v - f32(r0), 0.0, 1.0);

    let idx00 = u32(r0 * i32(pCols) + c0);
    let idx10 = idx00 + 1u;
    let idx01 = idx00 + pCols;
    let idx11 = idx01 + 1u;

    let p00 = potentialGrid[fieldOffset + idx00];
    let p10 = potentialGrid[fieldOffset + idx10];
    let p01 = potentialGrid[fieldOffset + idx01];
    let p11 = potentialGrid[fieldOffset + idx11];

    let maxValidPot = 1.7014117e38;
    let v00 = p00 < maxValidPot;
    let v10 = p10 < maxValidPot;
    let v01 = p01 < maxValidPot;
    let v11 = p11 < maxValidPot;

    let nearestCol = clamp(i32(round(u)), 0, i32(pCols) - 1);
    let nearestRow = clamp(i32(round(v)), 0, i32(pRows) - 1);
    let nearestIdx = u32(nearestRow * i32(pCols) + nearestCol);

    if (potentialGrid[fieldOffset + nearestIdx] >= maxValidPot || (!v00 && !v10 && !v01 && !v11)) {
        var bestPot = maxValidPot;
        var bestDx = 1.0;
        var bestDy = 0.0;

        for (var k = 0u; k < 8u; k++) {
            let nc = nearestCol + cOffsets[k];
            let nr = nearestRow + rOffsets[k];
            if (nc >= 0 && nc < i32(pCols) && nr >= 0 && nr < i32(pRows)) {
                let nIdx = u32(nr * i32(pCols) + nc);
                let nPot = potentialGrid[fieldOffset + nIdx];
                if (nPot < bestPot) {
                    bestPot = nPot;
                    bestDx = f32(cOffsets[k]);
                    bestDy = f32(rOffsets[k]);
                }
            }
        }

        let mag = sqrt(bestDx * bestDx + bestDy * bestDy);
        if (mag > 0.0001) {
            return vec2<f32>(bestDx / mag, bestDy / mag);
        }
        return vec2<f32>(1.0, 0.0);
    }

    var flowX = 0.0;
    var flowY = 0.0;

    if (v00 && v10 && v01 && v11) {
        flowX = ((1.0 - t) * (p00 - p10) + t * (p01 - p11)) * invCellW;
        flowY = ((1.0 - s) * (p00 - p01) + s * (p10 - p11)) * invCellH;
    } else {
        var gradX0 = 0.0;
        var hasGX0 = false;
        if (v00 && v10) { gradX0 = p00 - p10; hasGX0 = true; }
        else if (v00 && !v10) { gradX0 = -1.0; hasGX0 = true; }
        else if (!v00 && v10) { gradX0 = 1.0; hasGX0 = true; }

        var gradX1 = 0.0;
        var hasGX1 = false;
        if (v01 && v11) { gradX1 = p01 - p11; hasGX1 = true; }
        else if (v01 && !v11) { gradX1 = -1.0; hasGX1 = true; }
        else if (!v01 && v11) { gradX1 = 1.0; hasGX1 = true; }

        if (hasGX0 && hasGX1) {
            flowX = ((1.0 - t) * gradX0 + t * gradX1) * invCellW;
        } else if (hasGX0) {
            flowX = gradX0 * invCellW;
        } else if (hasGX1) {
            flowX = gradX1 * invCellW;
        }

        var gradY0 = 0.0;
        var hasGY0 = false;
        if (v00 && v01) { gradY0 = p00 - p01; hasGY0 = true; }
        else if (v00 && !v01) { gradY0 = -1.0; hasGY0 = true; }
        else if (!v00 && v01) { gradY0 = 1.0; hasGY0 = true; }

        var gradY1 = 0.0;
        var hasGY1 = false;
        if (v10 && v11) { gradY1 = p10 - p11; hasGY1 = true; }
        else if (v10 && !v11) { gradY1 = -1.0; hasGY1 = true; }
        else if (!v10 && v11) { gradY1 = 1.0; hasGY1 = true; }

        if (hasGY0 && hasGY1) {
            flowY = ((1.0 - s) * gradY0 + s * gradY1) * invCellH;
        } else if (hasGY0) {
            flowY = gradY0 * invCellH;
        } else if (hasGY1) {
            flowY = gradY1 * invCellH;
        }
    }

    let gradMag = sqrt(flowX * flowX + flowY * flowY);
    if (gradMag > 0.0001) {
        return vec2<f32>(flowX / gradMag, flowY / gradMag);
    }
    var bestDist = 3.4028234e38;
    var direction = vec2<f32>(1.0, 0.0);
    for (var e = 0u; e < params.numExits; e++) {
        let z = exits[e];
        if (params.routingEnabled == 1u && assignedTarget >= 0 && i32(z.w) != assignedTarget) { continue; }
        let center = select(z.xy + z.zw * 0.5, z.xy, params.isMap == 1u);
        let delta = center - pos;
        let dist = dot(delta, delta);
        if (dist < bestDist) { bestDist = dist; direction = delta; }
    }
    if (bestDist > 0.00000001) { return normalize(direction); }
    return vec2<f32>(1.0, 0.0);
}

fn sampleSmoothedDensity(pos: vec2<f32>) -> f32 {
    let pCols = params.potCols;
    let pRows = params.potRows;
    let invCell = 1.0 / params.potCellSize;
    let u = pos.x * invCell - 0.5;
    let v = pos.y * invCell - 0.5;
    let c0 = i32(floor(u));
    let r0 = i32(floor(v));
    let s = clamp(u - f32(c0), 0.0, 1.0);
    let t = clamp(v - f32(r0), 0.0, 1.0);

    let maxC = i32(pCols) - 1;
    let maxR = i32(pRows) - 1;
    let c0Clamped = clamp(c0, 0, maxC);
    let r0Clamped = clamp(r0, 0, maxR);
    let c1Clamped = clamp(c0 + 1, 0, maxC);
    let r1Clamped = clamp(r0 + 1, 0, maxR);

    let d00 = smoothedDensityGrid[u32(r0Clamped * i32(pCols) + c0Clamped)];
    let d10 = smoothedDensityGrid[u32(r0Clamped * i32(pCols) + c1Clamped)];
    let d01 = smoothedDensityGrid[u32(r1Clamped * i32(pCols) + c0Clamped)];
    let d11 = smoothedDensityGrid[u32(r1Clamped * i32(pCols) + c1Clamped)];

    return (1.0 - s) * (1.0 - t) * d00 +
           s * (1.0 - t) * d10 +
           (1.0 - s) * t * d01 +
           s * t * d11;
}

// Weidmann / Seyfried Empirical Fundamental Diagram (4 Flow Regimes)
fn calculateSpeedFactor(density: f32) -> f32 {
    if (density <= 0.0) { return 1.0; }
    if (density >= 5.40) { return 0.0; }

    // Regime I: Comfort zone (rho <= 0.70)
    if (density <= 0.70) {
        let t = density / 0.70;
        return 1.0 - 0.149 * pow(t, 1.6);
    }
    // Regime II: Constrained flow (0.70 < rho <= 2.20)
    if (density <= 2.20) {
        let t = (density - 0.70) / 1.50;
        return 0.851 - 0.500 * (t * (1.0 + 0.15 * (1.0 - t)));
    }
    // Regime III: Dense crowd / Queue (2.20 < rho <= 4.70)
    if (density <= 4.70) {
        let t = (density - 2.20) / 2.50;
        return 0.112 + (0.351 - 0.112) * pow(1.0 - t, 1.4);
    }
    // Regime IV: Jam / Stoppage (4.70 < rho <= 5.40)
    let t = (density - 4.70) / 0.70;
    return max(0.0, 0.112 * (1.0 - pow(t, 1.2)));
}

fn calculateEffectiveDensity(forwardKernelSum: f32, closestForwardDist: f32) -> f32 {
    var forwardDensity = 0.0;
    if (closestForwardDist > 0.05 && closestForwardDist < 1.8) {
        forwardDensity = 1.20 / (closestForwardDist * closestForwardDist);
    }
    if (forwardKernelSum <= 2.20) {
        forwardDensity = min(forwardDensity, 2.20);
    } else {
        forwardDensity = min(forwardDensity, 5.35);
    }
    return max(forwardKernelSum, forwardDensity);
}

fn calculatePushingFactor(density: f32) -> f32 {
    if (density <= 0.70) { return 0.0; }
    if (density <= 2.50) {
        return (density - 0.70) / (2.50 - 0.70);
    }
    return 1.0 + 0.85 * (density - 2.50);
}

fn calculateRearPushingForce(density: f32, distance: f32, radiusSum: f32, contactBuffer: f32, rearForwardDrive: f32) -> f32 {
    let pushingFactor = calculatePushingFactor(density);
    if (pushingFactor <= 0.0) { return 0.0; }

    let contactThreshold = radiusSum + contactBuffer;
    if (distance >= contactThreshold || distance <= 0.0001) { return 0.0; }

    let penetration = max(0.0, radiusSum - distance);
    let contactPush = penetration * 4.0;

    let proximityFactor = (contactThreshold - distance) / contactBuffer;
    let drivePush = max(0.0, rearForwardDrive) * 1.5 * proximityFactor;

    return pushingFactor * (contactPush + drivePush);
}

@compute @workgroup_size(64)
fn main(@builtin(global_invocation_id) global_id: vec3<u32>) {
    let i = global_id.x;
    if (i >= params.agentCount) { return; }

    var agent = agents[i];
    if (agent.flags == 0u) { return; }

    // 1. Flow direction from potential field (bilinear gradient)
    var assignedTarget = -1;
    if (params.isMap == 1u && params.routingEnabled == 1u) {
        let assignmentsOffset = (params.safeZoneCount + 1u) * params.potCols * params.potRows;
        assignedTarget = i32(potentialGrid[assignmentsOffset + i]);
    }
    let flowDirection = getFlowDirection(agent.pos, assignedTarget);

    let currentSpeed = length(agent.vel);
    let hasSignificantVelocity = currentSpeed > 0.05;
    var normVel = flowDirection;
    if (hasSignificantVelocity) {
        normVel = agent.vel / currentSpeed;
    }

    var travelDir = flowDirection;

    // 2. Wall repulsion and sliding
    var wallRepulsionForce = vec2<f32>(0.0, 0.0);
    var minDistanceToObstacle = 9999.0;
    var isInsideCorridor = false;

    let comfortDist = agent.radius + 0.6;
    let comfortDistSq = comfortDist * comfortDist;

    if (params.isMap == 1u) {
        // Map mode: Wall repulsion from nearby blocked raster cells
        let cell = params.mapCellSize;
        let curCol = i32(floor(agent.pos.x / cell));
        let curRow = i32(floor(agent.pos.y / cell));

        for (var dr = -1; dr <= 1; dr++) {
            let r = curRow + dr;
            if (r < 0 || r >= i32(params.mapRows)) { continue; }
            for (var dc = -1; dc <= 1; dc++) {
                let c = curCol + dc;
                if (c < 0 || c >= i32(params.mapCols)) { continue; }
                let cellIdx = u32(r) * params.mapCols + u32(c);
                if ((blockedRaster[cellIdx] & 1u) == 0u) { continue; }

                let minX = f32(c) * cell;
                let maxX = minX + cell;
                let minY = f32(r) * cell;
                let maxY = minY + cell;
                let qx = clamp(agent.pos.x, minX, maxX);
                let qy = clamp(agent.pos.y, minY, maxY);
                let dbox = agent.pos - vec2<f32>(qx, qy);
                let dboxSq = dot(dbox, dbox);
                if (dboxSq < comfortDistSq && dboxSq > 0.000001) {
                    let d = sqrt(dboxSq);
                    minDistanceToObstacle = min(minDistanceToObstacle, d);
                    let norm = dbox / d;
                    let strength = 3.5 * (comfortDist - d) / comfortDist;
                    wallRepulsionForce += norm * strength;

                    let dotWithNorm = dot(travelDir, norm);
                    if (dotWithNorm < 0.0) {
                        travelDir -= dotWithNorm * norm;
                        let slideSpeed = length(travelDir);
                        if (slideSpeed > 0.05) {
                            travelDir /= slideSpeed;
                        }
                    }
                }
            }
        }
    } else {
        if (agent.pos.x < 6.0) {
            wallRepulsionForce.x += max(1.0, (6.0 - agent.pos.x) * 2.0);
        }

        // Obstacle 1
        let obs1 = params.obs1;
        let nbp1X = clamp(agent.pos.x, obs1.x, obs1.x + obs1.z);
        let nbp1Y = clamp(agent.pos.y, obs1.y, obs1.y + obs1.w);
        let dbox1 = agent.pos - vec2<f32>(nbp1X, nbp1Y);
        let dbox1Sq = dot(dbox1, dbox1);
        if (dbox1Sq < comfortDistSq && dbox1Sq > 0.000001) {
            let dbox = sqrt(dbox1Sq);
            minDistanceToObstacle = min(minDistanceToObstacle, dbox);
            let norm = dbox1 / dbox;
            let strength = 3.5 * (comfortDist - dbox) / comfortDist;
            wallRepulsionForce += norm * strength;

            let dotWithNorm = dot(travelDir, norm);
            if (dotWithNorm < 0.0) {
                travelDir -= dotWithNorm * norm;
                let slideSpeed = length(travelDir);
                if (slideSpeed > 0.05) {
                    travelDir /= slideSpeed;
                } else {
                    let obsCenterY = obs1.y + obs1.w * 0.5;
                    let bypassSign = select(1.0, -1.0, agent.pos.y < obsCenterY);
                    let tan1 = vec2<f32>(-norm.y, norm.x);
                    if (tan1.y * bypassSign >= 0.0) {
                        travelDir = tan1;
                    } else {
                        travelDir = -tan1;
                    }
                }
            }
        }

        // Obstacle 2
        let obs2 = params.obs2;
        let nbp2X = clamp(agent.pos.x, obs2.x, obs2.x + obs2.z);
        let nbp2Y = clamp(agent.pos.y, obs2.y, obs2.y + obs2.w);
        let dbox2 = agent.pos - vec2<f32>(nbp2X, nbp2Y);
        let dbox2Sq = dot(dbox2, dbox2);
        if (dbox2Sq < comfortDistSq && dbox2Sq > 0.000001) {
            let dbox = sqrt(dbox2Sq);
            minDistanceToObstacle = min(minDistanceToObstacle, dbox);
            let norm = dbox2 / dbox;
            let strength = 3.5 * (comfortDist - dbox) / comfortDist;
            wallRepulsionForce += norm * strength;

            let dotWithNorm = dot(travelDir, norm);
            if (dotWithNorm < 0.0) {
                travelDir -= dotWithNorm * norm;
                let slideSpeed = length(travelDir);
                if (slideSpeed > 0.05) {
                    travelDir /= slideSpeed;
                } else {
                    let obsCenterY = obs2.y + obs2.w * 0.5;
                    let bypassSign = select(1.0, -1.0, agent.pos.y < obsCenterY);
                    let tan1 = vec2<f32>(-norm.y, norm.x);
                    if (tan1.y * bypassSign >= 0.0) {
                        travelDir = tan1;
                    } else {
                        travelDir = -tan1;
                    }
                }
            }
        }

        // 3. Corridor check matching C#
        let corridorMinX = min(params.obs1.x, params.obs2.x) - 0.5;
        let corridorMaxX = max(params.obs1.x + params.obs1.z, params.obs2.x + params.obs2.z) + 1.0;
        let corridorMinY = min(params.obs1.y + params.obs1.w, params.obs2.y + params.obs2.w);
        let corridorMaxY = max(params.obs1.y, params.obs2.y);
        isInsideCorridor = agent.pos.x >= corridorMinX && agent.pos.x <= corridorMaxX &&
                           agent.pos.y >= corridorMinY && agent.pos.y <= corridorMaxY;
    }

    // 4. Spatial neighbor search for density, collision, and queue fanning
    let scaleSqrtG = sqrt(f32(params.granulation));
    let powG025 = pow(f32(params.granulation), 0.25);
    let h = 2.0 * powG025;
    let h2 = h * h;
    let invH2 = 1.0 / h2;
    let spatialSearchRadius = select(max(4.5, 2.2 * scaleSqrtG), 2.8, params.granulation == 1u);
    let spatialSearchRadiusSq = spatialSearchRadius * spatialSearchRadius;
    let radialCoeff = 0.764 * scaleSqrtG;
    let forwardCoeff = 1.528 * scaleSqrtG;
    let effectiveWhisker = select(2.5 * powG025, 2.5, params.granulation == 1u);
    let brakeMargin = min(4.5, 1.80 * scaleSqrtG);
    let rearPushThreshold = 0.30 * scaleSqrtG;
    let lateralRampScale = 0.40 * scaleSqrtG;
    let lateralRampInv = 1.0 / lateralRampScale;
    let lateralBackCutoff = -0.5 * scaleSqrtG;
    let invScaleSqrtG = 1.0 / scaleSqrtG;

    var totalRadialDensity = 0.0;
    var forwardKernelSum = 0.0;
    var closestForwardDist = 10.0;
    var closestLeaderRadius = 0.35;
    var crowdGrad = vec2<f32>(0.0, 0.0);
    var blocked = false;
    var closestBlockDist = effectiveWhisker;
    var densityLeft = 0.0;
    var densityRight = 0.0;
    var hasRecoveryNeighbor = false;

    let cellW = params.worldWidth / f32(params.gridCols);
    let cellH = params.worldHeight / f32(params.gridRows);
    let col = i32(agent.pos.x / cellW);
    let row = i32(agent.pos.y / cellH);

    // Pass 1: Accumulate density, blocking, and crowd gradient
    for (var dy = -1; dy <= 1; dy++) {
        let ny = row + dy;
        if (ny < 0 || ny >= i32(params.gridRows)) { continue; }
        for (var dx = -1; dx <= 1; dx++) {
            let nx = col + dx;
            if (nx < 0 || nx >= i32(params.gridCols)) { continue; }

            let nCellId = u32(ny * i32(params.gridCols) + nx);
            let nCellCount = min(atomicLoad(&cellCounts[nCellId]) & 0x7fffffffu, params.maxPerCell);

            for (var k = 0u; k < nCellCount; k++) {
                let neighborIdx = cellAgents[nCellId * params.maxPerCell + k];
                if (neighborIdx == i) { continue; }

                let neighbor = agents[neighborIdx];
                if (neighbor.flags == 0u) { continue; }

                let diff = neighbor.pos - agent.pos;
                let distSq = dot(diff, diff);
                let recoveryDistance = agent.radius + neighbor.radius + 1.0;
                if (distSq < recoveryDistance * recoveryDistance) { hasRecoveryNeighbor = true; }
                if (distSq > spatialSearchRadiusSq || distSq < 0.000001) { continue; }

                let dist = sqrt(distSq);
                let minDist = agent.radius + neighbor.radius;

                let forwardDist = diff.x * normVel.x + diff.y * normVel.y;
                let signedLateral = diff.x * (-normVel.y) + diff.y * normVel.x;
                let lateralDist = abs(signedLateral);

                // Radial density kernel
                if (distSq < h2) {
                    let w = 1.0 - distSq * invH2;
                    totalRadialDensity += radialCoeff * (w * w);
                    crowdGrad += scaleSqrtG * (diff / dist) * w;

                    if (forwardDist > 0.05) {
                        let cosAngle = forwardDist / dist;
                        if (cosAngle > 0.50) {
                            let wf = w * cosAngle;
                            forwardKernelSum += forwardCoeff * (wf * wf);
                        }
                        if (forwardDist < closestForwardDist && lateralDist < minDist * 0.9) {
                            closestForwardDist = forwardDist;
                            closestLeaderRadius = neighbor.radius;
                        }
                    }
                }

                // Left / Right density balance
                if (forwardDist > lateralBackCutoff) {
                    let w = 1.0 - (dist / spatialSearchRadius);
                    if (params.granulation == 1u) {
                        if (signedLateral > 0.1) {
                            densityLeft += w;
                        } else if (signedLateral < -0.1) {
                            densityRight += w;
                        }
                    } else {
                        let lateralRamp = clamp(signedLateral * lateralRampInv, -1.0, 1.0);
                        if (lateralRamp > 0.0) {
                            densityLeft += f32(params.granulation) * w * lateralRamp;
                        } else if (lateralRamp < 0.0) {
                            densityRight += f32(params.granulation) * w * (-lateralRamp);
                        }
                    }
                }

                // Whisker blocking
                if (forwardDist > 0.05 && forwardDist < effectiveWhisker) {
                    let collisionThreshold = select(minDist * 1.0, minDist * 0.8, params.granulation == 1u);
                    if (lateralDist < collisionThreshold) {
                        blocked = true;
                        if (forwardDist < closestBlockDist) {
                            closestBlockDist = forwardDist;
                        }
                    }
                }
            }
        }
    }

    // 5. Density relief force (-grad rho) with corridor dampening
    var reliefForce = vec2<f32>(0.0, 0.0);
    if (totalRadialDensity > 0.70) {
        let gradLen = length(crowdGrad);
        if (gradLen > 0.01) {
            var pressureCoeff = min(2.5, (totalRadialDensity - 0.70) * 0.75);
            if (isInsideCorridor && minDistanceToObstacle < 1.5) {
                pressureCoeff *= 0.25;
            }
            reliefForce = -(crowdGrad / gradLen) * pressureCoeff;
        }
    }

    // 6. Effective forward kernel & Headway matching C#
    var effectiveForwardKernel = forwardKernelSum;
    if (params.granulation > 1u) {
        let forwardMacroDensity = sampleSmoothedDensity(agent.pos + 1.8 * normVel);
        let currentMacroDensity = sampleSmoothedDensity(agent.pos);
        effectiveForwardKernel = max(max(currentMacroDensity, forwardMacroDensity), forwardKernelSum);
    }
    let surfaceGap = closestForwardDist - (agent.radius + closestLeaderRadius);
    var effectiveHeadway = 10.0;
    if (closestForwardDist < 9.0) {
        effectiveHeadway = select(0.44 + max(0.0, surfaceGap / scaleSqrtG), closestForwardDist, params.granulation == 1u);
    }
    let localDensity = calculateEffectiveDensity(effectiveForwardKernel, effectiveHeadway);
    agent.pad = bitcast<u32>(localDensity);

    // Total pressure factor for social repulsion
    let crowdPressureFactor = 1.0 + min(3.5, max(0.0, (max(localDensity, totalRadialDensity) - 0.70) * 1.2));
    let wallComfortDistance = agent.radius + 0.6;
    var wallClearanceFactor = 1.0;
    if (minDistanceToObstacle < wallComfortDistance) {
        wallClearanceFactor = 1.0 + 1.5 * (wallComfortDistance - minDistanceToObstacle) / wallComfortDistance;
    }
    let totalPressureFactor = crowdPressureFactor * wallClearanceFactor;

    // Pass 2: Social repulsion & Rear pushing forces
    var crowdAvoidanceForce = vec2<f32>(0.0, 0.0);
    var rearPushForce = vec2<f32>(0.0, 0.0);
    var totalRearPushMag = 0.0;

    for (var dy = -1; dy <= 1; dy++) {
        let ny = row + dy;
        if (ny < 0 || ny >= i32(params.gridRows)) { continue; }
        for (var dx = -1; dx <= 1; dx++) {
            let nx = col + dx;
            if (nx < 0 || nx >= i32(params.gridCols)) { continue; }

            let nCellId = u32(ny * i32(params.gridCols) + nx);
            let nCellCount = min(atomicLoad(&cellCounts[nCellId]) & 0x7fffffffu, params.maxPerCell);

            for (var k = 0u; k < nCellCount; k++) {
                let neighborIdx = cellAgents[nCellId * params.maxPerCell + k];
                if (neighborIdx == i) { continue; }

                let neighbor = agents[neighborIdx];
                if (neighbor.flags == 0u) { continue; }

                let diff = neighbor.pos - agent.pos;
                let distSq = dot(diff, diff);
                let minDist = agent.radius + neighbor.radius;
                let repulsionThreshold = minDist * 2.2;

                if (distSq <= repulsionThreshold * repulsionThreshold && distSq > 0.000001) {
                    let dist = sqrt(distSq);
                    let proximityWeight = (repulsionThreshold - dist) / repulsionThreshold;
                    let baseRepel = (params.socialWeight * 0.45 * totalPressureFactor) * proximityWeight;
                    var denseCrowdPush = 0.0;
                    if (totalRadialDensity > 1.2) {
                        denseCrowdPush = min(1.8, (totalRadialDensity - 1.2) * 0.55) * proximityWeight;
                    }
                    let totalRepel = baseRepel + denseCrowdPush;
                    crowdAvoidanceForce -= (diff / dist) * totalRepel;
                }

                let forwardDist = diff.x * normVel.x + diff.y * normVel.y;
                if (forwardDist < -0.05 && distSq < spatialSearchRadiusSq && distSq > 0.000001) {
                    let dist = sqrt(distSq);
                    let signedLateral = diff.x * (-normVel.y) + diff.y * normVel.x;
                    let lateralDist = abs(signedLateral);
                    let collisionThreshold = minDist * 0.85;

                    if (lateralDist < collisionThreshold) {
                        var rearForwardDrive = neighbor.vel.x * normVel.x + neighbor.vel.y * normVel.y;
                        if (rearForwardDrive < 0.2) {
                            rearForwardDrive = neighbor.desiredSpeed * 0.5;
                        }
                        let pushMag = calculateRearPushingForce(localDensity, dist, minDist, rearPushThreshold, rearForwardDrive);
                        if (pushMag > 0.0) {
                            rearPushForce += normVel * pushMag;
                            totalRearPushMag += pushMag;
                        }
                    }
                }
            }
        }
    }

    // 7. Contact braking & Queue fanning
    let speedFactor = calculateSpeedFactor(localDensity);
    let effectiveGoalSpeed = agent.desiredSpeed * speedFactor;

    var contactBrakeFactor = 1.0;
    if (blocked) {
        if (params.granulation == 1u) {
            let contactBrakeDist = agent.radius * 2.0 * 1.5;
            if (closestBlockDist < contactBrakeDist) {
                contactBrakeFactor = clamp(closestBlockDist / contactBrakeDist, 0.05, 1.0);
                if (totalRearPushMag > 0.0) {
                    let pushRelief = min(1.0, totalRearPushMag / 2.5);
                    contactBrakeFactor = contactBrakeFactor + (1.0 - contactBrakeFactor) * pushRelief;
                }
            }
        } else {
            let contactBrakeDist = agent.radius * 2.0 + brakeMargin;
            if (closestBlockDist < contactBrakeDist) {
                let contactGap = max(0.0, closestBlockDist - agent.radius * 2.0);
                contactBrakeFactor = clamp(0.15 + 0.85 * (contactGap / brakeMargin), 0.08, 1.0);
                if (totalRearPushMag > 0.0) {
                    let pushRelief = min(1.0, totalRearPushMag / 2.5);
                    contactBrakeFactor = contactBrakeFactor + (1.0 - contactBrakeFactor) * pushRelief;
                }
            }
        }
    }

    // Lateral queue fanning
    let leftNormal = vec2<f32>(-normVel.y, normVel.x);
    var lateralFanForce = vec2<f32>(0.0, 0.0);
    if (blocked) {
        var steerDir = 0.0;
        if (params.granulation == 1u) {
            if (densityLeft < densityRight - 0.15) {
                steerDir = 1.0;
            } else if (densityRight < densityLeft - 0.15) {
                steerDir = -1.0;
            } else {
                steerDir = select(-1.0, 1.0, (i & 1u) == 0u);
            }
        } else {
            let diffNorm = (densityRight - densityLeft) * invScaleSqrtG;
            let parityBias = select(-0.25, 0.25, (i & 1u) == 0u);
            steerDir = clamp(diffNorm * 0.85 + parityBias * 0.20, -1.0, 1.0);
        }
        let congestionFactor = clamp((localDensity - 0.70) / 1.5, 0.0, 1.0);
        var fanStrength = (1.0 - closestBlockDist / effectiveWhisker) * (1.6 * congestionFactor);
        if (minDistanceToObstacle < 1.2) { fanStrength *= 0.4; }
        lateralFanForce += leftNormal * (steerDir * fanStrength);
    }

    // Continuum crowd decompression
    let effDensityDiff = select((densityRight - densityLeft) * invScaleSqrtG, densityRight - densityLeft, params.granulation == 1u);
    if (abs(effDensityDiff) > 0.08 && localDensity > 0.5) {
        var decompMag = clamp(effDensityDiff * 0.45 * min(2.5, localDensity), -1.8, 1.8);
        if (minDistanceToObstacle < 1.2) { decompMag *= 0.4; }
        lateralFanForce += leftNormal * decompMag;
    }

    // 8. Forward speed & Perpendicular budget
    var forwardSpeed = effectiveGoalSpeed * contactBrakeFactor;
    if (totalRearPushMag > 0.1 && localDensity < 4.70) {
        let pushSurge = min(0.10, totalRearPushMag * 0.05);
        forwardSpeed *= (1.0 + pushSurge);
    }
    let minSpeedFloor = select(0.05, 0.002, localDensity >= 4.70);
    forwardSpeed = max(minSpeedFloor, min(agent.desiredSpeed, forwardSpeed));

    let perpFlow = vec2<f32>(-travelDir.y, travelDir.x);
    let crowdLateralTotal = crowdAvoidanceForce * 0.45 + lateralFanForce + reliefForce;
    var perpComponent = dot(crowdLateralTotal, perpFlow);

    var maxPerpSpeed = 0.0;
    if (localDensity >= 4.70) {
        maxPerpSpeed = min(0.025, max(0.005, effectiveGoalSpeed * 0.40));
    } else if (localDensity >= 3.50) {
        maxPerpSpeed = min(0.20, max(0.05, effectiveGoalSpeed * 0.65));
    } else if (localDensity >= 2.20) {
        maxPerpSpeed = min(0.40, max(0.10, effectiveGoalSpeed * 0.75));
    } else {
        maxPerpSpeed = min(0.70, agent.desiredSpeed * 0.45);
    }
    if (minDistanceToObstacle > 1.2 && params.granulation > 1u) {
        let lateralRatio = clamp(0.35 + 0.30 * (localDensity - 0.50), 0.35, 0.70);
        let smoothPerpCap = max(0.02, forwardSpeed * lateralRatio);
        maxPerpSpeed = min(maxPerpSpeed, smoothPerpCap);
    }
    perpComponent = clamp(perpComponent, -maxPerpSpeed, maxPerpSpeed);

    // 9. Scaled wall repulsion & Composition
    let wallSpeedRatio = max(0.05, effectiveGoalSpeed / agent.desiredSpeed);
    let scaledWallRepulsion = wallRepulsionForce * wallSpeedRatio;

    var finalForce = travelDir * forwardSpeed + perpFlow * perpComponent + scaledWallRepulsion;
    let calcSpeed = length(finalForce);
    let speedCap = min(agent.desiredSpeed, max(0.002, min(effectiveGoalSpeed, sqrt(forwardSpeed * forwardSpeed + perpComponent * perpComponent))));
    if (calcSpeed > speedCap && calcSpeed > 0.0001) {
        finalForce = (finalForce / calcSpeed) * speedCap;
    }

    // 10. Velocity smoothing inertia
    let currentVelMag = length(agent.vel);
    var velInertia = 0.25;
    if (localDensity >= 4.70) {
        velInertia = select(0.35, 0.85, currentVelMag > speedCap);
    } else if (isInsideCorridor) {
        velInertia = 0.55;
    }
    if (params.granulation > 1u) {
        let inertiaDamping = min(1.75, pow(f32(params.granulation), 0.18));
        velInertia = max(0.12, velInertia / inertiaDamping);
    }

    let previousPosition = agent.pos;
    agent.vel = agent.vel * (1.0 - velInertia) + finalForce * velInertia;
    agent.pos += agent.vel * params.dt;

    // 11. Circle-box collision resolution
    if (params.isMap == 1u) {
        let cell = params.mapCellSize;
        let minCol = max(0, i32(floor((agent.pos.x - agent.radius) / cell)));
        let maxCol = min(i32(params.mapCols) - 1, i32(floor((agent.pos.x + agent.radius) / cell)));
        let minRow = max(0, i32(floor((agent.pos.y - agent.radius) / cell)));
        let maxRow = min(i32(params.mapRows) - 1, i32(floor((agent.pos.y + agent.radius) / cell)));

        for (var r = minRow; r <= maxRow; r++) {
            for (var c = minCol; c <= maxCol; c++) {
                let cellIdx = u32(r) * params.mapCols + u32(c);
                if ((blockedRaster[cellIdx] & 1u) == 0u) { continue; }

                let minX = f32(c) * cell;
                let maxX = minX + cell;
                let minY = f32(r) * cell;
                let maxY = minY + cell;
                let qx = clamp(agent.pos.x, minX, maxX);
                let qy = clamp(agent.pos.y, minY, maxY);
                let dbox = agent.pos - vec2<f32>(qx, qy);
                let dboxSq = dot(dbox, dbox);
                if (dboxSq < agent.radius * agent.radius) {
                    if (dboxSq > 0.000001) {
                        let d = sqrt(dboxSq);
                        let norm = dbox / d;
                        let pen = agent.radius - d;
                        agent.pos += norm * pen;
                        let nVel = dot(agent.vel, norm);
                        if (nVel < 0.0) { agent.vel -= nVel * norm; }
                    } else {
                        let distances = vec4<f32>(agent.pos.x - minX, maxX - agent.pos.x, agent.pos.y - minY, maxY - agent.pos.y);
                        let nearest = min(min(distances.x, distances.y), min(distances.z, distances.w));
                        if (nearest == distances.x) { agent.pos.x = minX - agent.radius; agent.vel.x = min(0.0, agent.vel.x); }
                        else if (nearest == distances.y) { agent.pos.x = maxX + agent.radius; agent.vel.x = max(0.0, agent.vel.x); }
                        else if (nearest == distances.z) { agent.pos.y = minY - agent.radius; agent.vel.y = min(0.0, agent.vel.y); }
                        else { agent.pos.y = maxY + agent.radius; agent.vel.y = max(0.0, agent.vel.y); }
                    }
                }
            }
        }

        // Resolving one raster box can push into its neighbor at a concave
        // corner. Keep the last walkable position instead of crossing a wall.
        agent.pos = clamp(agent.pos, vec2<f32>(agent.radius), vec2<f32>(params.worldWidth - agent.radius, params.worldHeight - agent.radius));
        if (!mapPositionBlocked(previousPosition) && (mapPositionBlocked(agent.pos) || mapMovementBlocked(previousPosition, agent.pos))) {
            agent.pos = previousPosition;
            agent.vel = vec2<f32>(0.0);
        } else if (mapPositionBlocked(agent.pos)) {
            // Repair an already embedded snapshot against the whole building,
            // rather than pushing back and forth between adjacent wall cells.
            let center = vec2<i32>(floor(previousPosition / cell));
            let reach = i32(ceil(20.0 / cell));
            var bestDistance = 400.0;
            var bestPosition = agent.pos;
            for (var row = max(0, center.y - reach); row <= min(i32(params.mapRows) - 1, center.y + reach); row++) {
                for (var col = max(0, center.x - reach); col <= min(i32(params.mapCols) - 1, center.x + reach); col++) {
                    if ((blockedRaster[u32(row) * params.mapCols + u32(col)] & 1u) != 0u) { continue; }
                    let candidate = (vec2<f32>(f32(col), f32(row)) + vec2<f32>(0.5)) * cell;
                    let d = candidate - previousPosition;
                    let distance = dot(d, d);
                    if (distance > bestDistance || !mapCircleClear(candidate, agent.radius)) { continue; }
                    let pc = clamp(vec2<i32>(floor(candidate / params.potCellSize)), vec2<i32>(0), vec2<i32>(i32(params.potCols) - 1, i32(params.potRows) - 1));
                    if (potentialGrid[u32(pc.y) * params.potCols + u32(pc.x)] >= 1.7014117e38) { continue; }
                    bestDistance = distance;
                    bestPosition = candidate;
                }
            }
            agent.pos = bestPosition;
            agent.vel = vec2<f32>(0.0);
        }

        // Evacuation check: agent reached an exit
        let catchMargin = params.potCellSize;
        let exitCell = clamp(vec2<i32>(floor(agent.pos / params.mapCellSize)), vec2<i32>(0), vec2<i32>(i32(params.mapCols) - 1, i32(params.mapRows) - 1));
        let nearExit = (blockedRaster[u32(exitCell.y) * params.mapCols + u32(exitCell.x)] & 4u) != 0u;
        for (var e = 0u; nearExit && e < params.numExits; e++) {
            let exit = exits[e];
            if (params.routingEnabled == 1u && assignedTarget >= 0 && i32(exit.w) != assignedTarget) { continue; }
            let reach = exit.z + catchMargin;
            if (abs(agent.pos.x - exit.x) <= reach && abs(agent.pos.y - exit.y) <= reach) {
                agent.radius = 0.0;
                agent.vel = vec2<f32>(0.0, 0.0);
                agent.flags = 0u;
                agents[i] = agent;
                return;
            }
        }
        if (params.pad0 == 1u) {
            var state = recovery[i];
            let displacement = agent.pos - state.anchor;
            if (hasRecoveryNeighbor || dot(displacement, displacement) >= 0.0625) {
                state.anchor = agent.pos;
                state.stationaryTicks = 0u;
            } else {
                state.stationaryTicks += 1u;
                // Exactly 180 simulated seconds at the fixed 16 ms physics step.
                if (state.stationaryTicks >= 11250u) {
                    let street = nearestRecoveryStreet(agent.pos, agent.radius, i);
                    if (street.x >= 0.0) {
                        agent.pos = street;
                        agent.vel = vec2<f32>(0.0);
                        agent.pad = 0u;
                    }
                    state.anchor = agent.pos;
                    state.stationaryTicks = select(0u, 11249u, street.x == -2.0);
                }
            }
            recovery[i] = state;
        }
    } else {
        let obs1 = params.obs1;
        let nbp1 = clamp(agent.pos, obs1.xy, obs1.xy + obs1.zw);
        let d1 = agent.pos - nbp1;
        let d1Sq = dot(d1, d1);
        if (d1Sq < agent.radius * agent.radius) {
            if (d1Sq > 0.000001) {
                let d = sqrt(d1Sq);
                let norm = d1 / d;
                let pen = agent.radius - d;
                agent.pos += norm * pen;
                let nVel = dot(agent.vel, norm);
                if (nVel < 0.0) {
                    agent.vel -= nVel * norm;
                }
            } else {
                let dLeft = agent.pos.x - obs1.x;
                let dRight = (obs1.x + obs1.z) - agent.pos.x;
                let dTop = agent.pos.y - obs1.y;
                let dBottom = (obs1.y + obs1.w) - agent.pos.y;
                let minD = min(min(dLeft, dRight), min(dTop, dBottom));
                if (minD == dLeft) { agent.pos.x = obs1.x - agent.radius; if (agent.vel.x > 0.0) { agent.vel.x = 0.0; } }
                else if (minD == dRight) { agent.pos.x = obs1.x + obs1.z + agent.radius; if (agent.vel.x < 0.0) { agent.vel.x = 0.0; } }
                else if (minD == dTop) { agent.pos.y = obs1.y - agent.radius; if (agent.vel.y > 0.0) { agent.vel.y = 0.0; } }
                else { agent.pos.y = obs1.y + obs1.w + agent.radius; if (agent.vel.y < 0.0) { agent.vel.y = 0.0; } }
            }
        }

        let obs2 = params.obs2;
        let nbp2 = clamp(agent.pos, obs2.xy, obs2.xy + obs2.zw);
        let d2 = agent.pos - nbp2;
        let d2Sq = dot(d2, d2);
        if (d2Sq < agent.radius * agent.radius) {
            if (d2Sq > 0.000001) {
                let d = sqrt(d2Sq);
                let norm = d2 / d;
                let pen = agent.radius - d;
                agent.pos += norm * pen;
                let nVel = dot(agent.vel, norm);
                if (nVel < 0.0) {
                    agent.vel -= nVel * norm;
                }
            } else {
                let dLeft = agent.pos.x - obs2.x;
                let dRight = (obs2.x + obs2.z) - agent.pos.x;
                let dTop = agent.pos.y - obs2.y;
                let dBottom = (obs2.y + obs2.w) - agent.pos.y;
                let minD = min(min(dLeft, dRight), min(dTop, dBottom));
                if (minD == dLeft) { agent.pos.x = obs2.x - agent.radius; if (agent.vel.x > 0.0) { agent.vel.x = 0.0; } }
                else if (minD == dRight) { agent.pos.x = obs2.x + obs2.z + agent.radius; if (agent.vel.x < 0.0) { agent.vel.x = 0.0; } }
                else if (minD == dTop) { agent.pos.y = obs2.y - agent.radius; if (agent.vel.y > 0.0) { agent.vel.y = 0.0; } }
                else { agent.pos.y = obs2.y + obs2.w + agent.radius; if (agent.vel.y < 0.0) { agent.vel.y = 0.0; } }
            }
        }
    }

    if (params.isMap == 0u) {
        let margin = max(2.5, params.potCellSize * 0.5);
        for (var e = 0u; e < params.numExits; e++) {
            let z = exits[e];
            if (all(agent.pos >= z.xy - vec2<f32>(margin)) && all(agent.pos <= z.xy + z.zw + vec2<f32>(margin))) {
                agent.flags = 0u; agent.radius = 0.0; agent.vel = vec2<f32>(0.0);
                agents[i] = agent; return;
            }
        }
    }
    agent.pos = clamp(agent.pos, vec2<f32>(agent.radius), vec2<f32>(params.worldWidth - agent.radius, params.worldHeight - agent.radius));

    agents[i] = agent;
}
`;
