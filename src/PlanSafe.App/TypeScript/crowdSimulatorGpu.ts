/// <reference types="@webgpu/types" />

import {
  impassablePotential,
  rasterizeObstacles,
  seedPotentialField,
  solvePotentialField,
  type PotentialSink,
  type Rectangle,
} from "./potentialField.js";

import type { CoreCommandValues } from "./crowdCoreCommand.js";
import { GpuAgentRenderer } from "./gpuAgentRenderer.js";
import {
  type BrowserCoreCommand,
  type ISimulationEngine,
  type OwnedPreview,
} from "./crowdRuntimeBridge.js";
import {
  protocolVersion,
  type DensityGridData,
  type EvacuationStatus,
  type ProfilingWindow,
} from "./crowdProtocol.js";

export function isWebGpuSupported(): boolean {
  return (
    typeof navigator !== "undefined" &&
    typeof navigator.gpu !== "undefined" &&
    typeof navigator.gpu.requestAdapter === "function"
  );
}

// --- WGSL Compute Shaders ---

const clearGridShader = `
@group(0) @binding(0) var<storage, read_write> cellCounts: array<atomic<u32>>;

@compute @workgroup_size(64)
fn main(@builtin(global_invocation_id) global_id: vec3<u32>) {
    let idx = global_id.x;
    if (idx < arrayLength(&cellCounts)) {
        atomicStore(&cellCounts[idx], 0u);
    }
}
`;

const binAgentsShader = `
struct Agent {
    pos: vec2<f32>,
    vel: vec2<f32>,
    radius: f32,
    desiredSpeed: f32,
    flags: u32,
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
    pad1: u32,
    pad2: u32,
};

@group(0) @binding(0) var<storage, read> agents: array<Agent>;
@group(0) @binding(1) var<storage, read_write> cellCounts: array<atomic<u32>>;
@group(0) @binding(2) var<storage, read_write> cellAgents: array<u32>;
@group(0) @binding(3) var<uniform> params: SimParams;

@compute @workgroup_size(64)
fn main(@builtin(global_invocation_id) global_id: vec3<u32>) {
    let i = global_id.x;
    if (i >= params.agentCount) { return; }

    let agent = agents[i];
    if (agent.flags == 0u) { return; }

    let cellW = params.worldWidth / f32(params.gridCols);
    let cellH = params.worldHeight / f32(params.gridRows);

    let col = clamp(u32(max(agent.pos.x, 0.0) / cellW), 0u, params.gridCols - 1u);
    let row = clamp(u32(max(agent.pos.y, 0.0) / cellH), 0u, params.gridRows - 1u);
    let cellId = row * params.gridCols + col;

    let slot = atomicAdd(&cellCounts[cellId], 1u);
    if (slot < params.maxPerCell) {
        cellAgents[cellId * params.maxPerCell + slot] = i;
    }
}
`;

const clearDensityShader = `
@group(0) @binding(0) var<storage, read_write> rawDensity: array<atomic<u32>>;

@compute @workgroup_size(64)
fn main(@builtin(global_invocation_id) global_id: vec3<u32>) {
    let idx = global_id.x;
    if (idx < arrayLength(&rawDensity)) {
        atomicStore(&rawDensity[idx], 0u);
    }
}
`;

const accumulateDensityShader = `
struct Agent {
    pos: vec2<f32>,
    vel: vec2<f32>,
    radius: f32,
    desiredSpeed: f32,
    flags: u32,
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
    pad1: u32,
    pad2: u32,
};

@group(0) @binding(0) var<storage, read> agents: array<Agent>;
@group(0) @binding(1) var<storage, read_write> rawDensity: array<atomic<u32>>;
@group(0) @binding(2) var<uniform> params: SimParams;

fn addDensity(idx: u32, mass: f32) {
    var old = atomicLoad(&rawDensity[idx]);
    loop {
        let result = atomicCompareExchangeWeak(&rawDensity[idx], old, bitcast<u32>(bitcast<f32>(old) + mass));
        if (result.exchanged) { break; }
        old = result.old_value;
    }
}

@compute @workgroup_size(64)
fn main(@builtin(global_invocation_id) global_id: vec3<u32>) {
    let i = global_id.x;
    if (i >= params.agentCount) { return; }
    let agent = agents[i];
    if (agent.flags == 0u) { return; }

    let pCols = params.potCols;
    let pRows = params.potRows;
    let invCell = 1.0 / params.potCellSize;
    let invCellArea = invCell * invCell;

    if (params.granulation >= 16u) {
        let m = invCellArea * f32(params.granulation);
        let u = agent.pos.x * invCell - 0.5;
        let v = agent.pos.y * invCell - 0.5;
        let c0 = i32(floor(u));
        let r0 = i32(floor(v));
        let s = clamp(u - f32(c0), 0.0, 1.0);
        let t = clamp(v - f32(r0), 0.0, 1.0);

        let w00 = m * (1.0 - s) * (1.0 - t);
        let w10 = m * s * (1.0 - t);
        let w01 = m * (1.0 - s) * t;
        let w11 = m * s * t;

        let maxC = i32(pCols) - 1;
        let maxR = i32(pRows) - 1;

        if (c0 >= 0 && c0 <= maxC && r0 >= 0 && r0 <= maxR && w00 > 0.0) {
            addDensity(u32(r0) * pCols + u32(c0), w00);
        }
        if (c0 + 1 >= 0 && c0 + 1 <= maxC && r0 >= 0 && r0 <= maxR && w10 > 0.0) {
            addDensity(u32(r0) * pCols + u32(c0 + 1), w10);
        }
        if (c0 >= 0 && c0 <= maxC && r0 + 1 >= 0 && r0 + 1 <= maxR && w01 > 0.0) {
            addDensity(u32(r0 + 1) * pCols + u32(c0), w01);
        }
        if (c0 + 1 >= 0 && c0 + 1 <= maxC && r0 + 1 >= 0 && r0 + 1 <= maxR && w11 > 0.0) {
            addDensity(u32(r0 + 1) * pCols + u32(c0 + 1), w11);
        }
    } else {
        let col = i32(floor(agent.pos.x * invCell));
        let row = i32(floor(agent.pos.y * invCell));
        if (col >= 0 && col < i32(pCols) && row >= 0 && row < i32(pRows)) {
            let idx = u32(row) * pCols + u32(col);
            let mass = invCellArea * f32(params.granulation);
            addDensity(idx, mass);
        }
    }
}
`;

const decodeDensityShader = `
@group(0) @binding(0) var<storage, read_write> rawDensity: array<atomic<u32>>;
@group(0) @binding(1) var<storage, read_write> density: array<f32>;
@compute @workgroup_size(64)
fn main(@builtin(global_invocation_id) id: vec3<u32>) {
    if (id.x < arrayLength(&density)) { density[id.x] = bitcast<f32>(atomicLoad(&rawDensity[id.x])); }
}
`;

const smoothDensityOnlyShader = `
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
    pad1: u32,
    pad2: u32,
};


@group(0) @binding(0) var<storage, read> source: array<f32>;
@group(0) @binding(1) var<storage, read_write> smoothedTarget: array<f32>;
@group(0) @binding(2) var<uniform> params: SimParams;
@compute @workgroup_size(16, 16)
fn main(@builtin(global_invocation_id) id: vec3<u32>) {
    let col = id.x; let row = id.y;
    let cols = params.potCols; let rows = params.potRows;
    if (col >= cols || row >= rows) { return; }
    let idx = row * cols + col;
    let left = row * cols + max(1u, col) - 1u;
    let right = row * cols + min(cols - 1u, col + 1u);
    let top = (max(1u, row) - 1u) * cols + col;
    let bottom = min(rows - 1u, row + 1u) * cols + col;
    smoothedTarget[idx] = 0.50 * source[idx] + 0.125 * (source[left] + source[right] + source[top] + source[bottom]);
}
`;

const updatePenaltyShader = `
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
    pad1: u32,
    pad2: u32,
};


@group(0) @binding(0) var<storage, read> density: array<f32>;
@group(0) @binding(1) var<storage, read> sinkProtected: array<u32>;
@group(0) @binding(2) var<storage, read_write> penalty: array<f32>;
@group(0) @binding(3) var<storage, read> blocked: array<u32>;
@group(0) @binding(4) var<uniform> params: SimParams;
@group(0) @binding(5) var<storage, read_write> state: array<atomic<u32>>;
@compute @workgroup_size(16, 16)
fn main(@builtin(global_invocation_id) id: vec3<u32>) {
    if (id.x >= params.potCols || id.y >= params.potRows) { return; }
    let idx = id.y * params.potCols + id.x;
    if (blocked[idx] != 0u || sinkProtected[idx] != 0u) { penalty[idx] = 0.0; return; }
    let rho = density[idx];
    var targetPenalty = 0.0;
    if (rho > 0.80) {
        let excess = rho - 0.80;
        targetPenalty = min(120.0, excess * 6.0 + excess * excess * 2.0);
    }
    penalty[idx] = 0.70 * penalty[idx] + 0.30 * targetPenalty;
    if (penalty[idx] > 0.5) { atomicStore(&state[0], 1u); }
}
`;

// --- Dedicated KDE Compute Shaders (Continuous Euclidean Kernel & Adaptive Resolution) ---

const kdeClearShader = `
@group(0) @binding(0) var<storage, read_write> mass: array<atomic<u32>>;
@group(0) @binding(1) var<storage, read_write> speed: array<atomic<u32>>;

@compute @workgroup_size(64)
fn main(@builtin(global_invocation_id) global_id: vec3<u32>) {
    let idx = global_id.x;
    if (idx < arrayLength(&mass)) {
        atomicStore(&mass[idx], 0u);
        atomicStore(&speed[idx], 0u);
    }
}
`;

const kdeDepositShader = `
struct Agent {
    pos: vec2<f32>,
    vel: vec2<f32>,
    radius: f32,
    desiredSpeed: f32,
    flags: u32,
    pad: u32,
};

struct KdeParams {
    worldWidth: f32,
    worldHeight: f32,
    kdeWidth: u32,
    kdeHeight: u32,
    kernelRadius: f32,
    granulation: u32,
    agentCount: u32,
    mode: u32,
    pad1: u32,
    pad2: u32,
    pad3: u32,
    pad4: u32,
};

@group(0) @binding(0) var<storage, read> agents: array<Agent>;
@group(0) @binding(1) var<storage, read_write> mass: array<atomic<u32>>;
@group(0) @binding(2) var<storage, read_write> speed: array<atomic<u32>>;
@group(0) @binding(3) var<uniform> params: KdeParams;

@compute @workgroup_size(64)
fn main(@builtin(global_invocation_id) global_id: vec3<u32>) {
    let i = global_id.x;
    if (i >= params.agentCount) { return; }

    let agent = agents[i];
    if (agent.flags == 0u || agent.radius <= 0.0) { return; }

    let cellW = params.worldWidth / f32(params.kdeWidth);
    let cellH = params.worldHeight / f32(params.kdeHeight);

    let col = clamp(u32(max(agent.pos.x, 0.0) / cellW), 0u, params.kdeWidth - 1u);
    let row = clamp(u32(max(agent.pos.y, 0.0) / cellH), 0u, params.kdeHeight - 1u);
    let idx = row * params.kdeWidth + col;

    let m = params.granulation * 1000u;
    atomicAdd(&mass[idx], m);

    if (params.mode == 1u) {
        let spd = length(agent.vel);
        let sScaled = u32(round(spd * f32(m)));
        atomicAdd(&speed[idx], sScaled);
    }
}
`;

const kdeSmoothKernelShader = `
struct KdeParams {
    worldWidth: f32,
    worldHeight: f32,
    kdeWidth: u32,
    kdeHeight: u32,
    kernelRadius: f32,
    granulation: u32,
    agentCount: u32,
    mode: u32,
    pad1: u32,
    pad2: u32,
    pad3: u32,
    pad4: u32,
};

@group(0) @binding(0) var<storage, read_write> mass: array<atomic<u32>>;
@group(0) @binding(1) var<storage, read_write> speed: array<atomic<u32>>;
@group(0) @binding(2) var<storage, read_write> output: array<f32>;
@group(0) @binding(3) var<uniform> params: KdeParams;

@compute @workgroup_size(16, 16)
fn main(@builtin(global_invocation_id) id: vec3<u32>) {
    let col = id.x;
    let row = id.y;
    if (col >= params.kdeWidth || row >= params.kdeHeight) { return; }

    let cellW = params.worldWidth / f32(params.kdeWidth);
    let cellH = params.worldHeight / f32(params.kdeHeight);

    let cellCenterX = (f32(col) + 0.5) * cellW;
    let cellCenterY = (f32(row) + 0.5) * cellH;

    let R = params.kernelRadius;
    let RSq = R * R;
    let radCellsX = u32(ceil(R / cellW));
    let radCellsY = u32(ceil(R / cellH));

    let minCol = u32(max(0, i32(col) - i32(radCellsX)));
    let maxCol = min(params.kdeWidth - 1u, col + radCellsX);
    let minRow = u32(max(0, i32(row) - i32(radCellsY)));
    let maxRow = min(params.kdeHeight - 1u, row + radCellsY);

    var weightSum: f32 = 0.0;
    var speedSum: f32 = 0.0;

    for (var r = minRow; r <= maxRow; r = r + 1u) {
        let sampleY = (f32(r) + 0.5) * cellH;
        let dy = sampleY - cellCenterY;
        let dySq = dy * dy;
        let rowOffset = r * params.kdeWidth;

        for (var c = minCol; c <= maxCol; c = c + 1u) {
            let sampleX = (f32(c) + 0.5) * cellW;
            let dx = sampleX - cellCenterX;
            let distSq = dx * dx + dySq;

            if (distSq <= RSq) {
                let dist = sqrt(distSq);
                let w = 1.0 - (dist / R);
                let nIdx = rowOffset + c;

                let mVal = f32(atomicLoad(&mass[nIdx])) * 0.001;
                if (mVal > 0.0) {
                    weightSum = weightSum + mVal * w;
                    if (params.mode == 1u) {
                        let sVal = f32(atomicLoad(&speed[nIdx])) * 0.001;
                        speedSum = speedSum + sVal * w;
                    }
                }
            }
        }
    }

    let outIdx = row * params.kdeWidth + col;
    if (params.mode == 0u) {
        output[outIdx] = weightSum;
    } else {
        if (weightSum > 0.001) {
            output[outIdx] = speedSum / weightSum;
        } else {
            output[outIdx] = 0.0;
        }
    }
}
`;

const initRelaxShader = `
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
    pad1: u32,
    pad2: u32,
};


@group(0) @binding(0) var<storage, read_write> pot: array<f32>;
@group(0) @binding(1) var<storage, read> seeds: array<f32>;
@group(0) @binding(2) var<uniform> params: SimParams;
@group(0) @binding(3) var<storage, read> staticPot: array<f32>;
@group(0) @binding(4) var<storage, read_write> state: array<atomic<u32>>;
@compute @workgroup_size(16, 16)
fn main(@builtin(global_invocation_id) id: vec3<u32>) {
    if (id.x >= params.potCols || id.y >= params.potRows) { return; }
    let idx = id.y * params.potCols + id.x;
    pot[idx] = select(staticPot[idx], seeds[idx], atomicLoad(&state[0]) != 0u);
}
`;

const relaxStepShader = `
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
    pad1: u32,
    pad2: u32,
};


@group(0) @binding(0) var<storage, read> inPot: array<f32>;
@group(0) @binding(1) var<storage, read_write> outPot: array<f32>;
@group(0) @binding(2) var<storage, read> penalty: array<f32>;
@group(0) @binding(3) var<storage, read> blocked: array<u32>;
@group(0) @binding(4) var<uniform> params: SimParams;
@group(0) @binding(5) var<storage, read_write> state: array<atomic<u32>>;
const dCol = array<i32, 8>(1, -1, 0, 0, 1, 1, -1, -1);
const dRow = array<i32, 8>(0, 0, 1, -1, 1, -1, 1, -1);
@compute @workgroup_size(16, 16)
fn main(@builtin(global_invocation_id) id: vec3<u32>) {
    let col = id.x; let row = id.y;
    let cols = params.potCols; let rows = params.potRows;
    if (col >= cols || row >= rows) { return; }
    let idx = row * cols + col;
    var best = inPot[idx];
    if (atomicLoad(&state[0]) != 0u && blocked[idx] == 0u) {
        for (var i = 0u; i < 8u; i++) {
            let nc = i32(col) + dCol[i]; let nr = i32(row) + dRow[i];
            if (nc < 0 || nc >= i32(cols) || nr < 0 || nr >= i32(rows)) { continue; }
            let ni = u32(nr) * cols + u32(nc);
            if (blocked[ni] != 0u) { continue; }
            if (i >= 4u && blocked[row * cols + u32(nc)] != 0u && blocked[u32(nr) * cols + col] != 0u) { continue; }
            let edgeStep = select(params.potCellSize, params.potCellSize * 1.41421356, i >= 4u);
            let avgPenalty = 0.5 * (penalty[idx] + penalty[ni]);
            let edgeCost = edgeStep * (1.0 + avgPenalty);
            let candidate = inPot[ni] + edgeCost;
            best = min(best, candidate);
        }
    }
    outPot[idx] = best;
    if (best < inPot[idx]) { atomicStore(&state[1], 1u); }
}
`;

const stepPhysicsShader = `
struct Agent {
    pos: vec2<f32>,
    vel: vec2<f32>,
    radius: f32,
    desiredSpeed: f32,
    flags: u32,
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
    pad1: u32,
    pad2: u32,
};

@group(0) @binding(0) var<storage, read_write> agents: array<Agent>;
@group(0) @binding(1) var<storage, read_write> cellCounts: array<atomic<u32>>;
@group(0) @binding(2) var<storage, read> cellAgents: array<u32>;
@group(0) @binding(3) var<uniform> params: SimParams;
@group(0) @binding(4) var<storage, read> potentialGrid: array<f32>;
@group(0) @binding(5) var<storage, read> smoothedDensityGrid: array<f32>;
@group(0) @binding(6) var<storage, read> blockedRaster: array<u32>;
@group(0) @binding(7) var<storage, read> exits: array<vec4<f32>>;

const cOffsets = array<i32, 8>(0, 0, -1, 1, -1, 1, -1, 1);
const rOffsets = array<i32, 8>(-1, 1, 0, 0, -1, -1, 1, 1);

// Bilinear gradient interpolation matching C# GetFlowDirection exactly
fn getFlowDirection(pos: vec2<f32>) -> vec2<f32> {
    let pCols = params.potCols;
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

    let p00 = potentialGrid[idx00];
    let p10 = potentialGrid[idx10];
    let p01 = potentialGrid[idx01];
    let p11 = potentialGrid[idx11];

    let maxValidPot = select(1.7014117e38, 900000.0, params.isMap == 1u);
    let v00 = p00 < maxValidPot;
    let v10 = p10 < maxValidPot;
    let v01 = p01 < maxValidPot;
    let v11 = p11 < maxValidPot;

    let nearestCol = clamp(i32(round(u)), 0, i32(pCols) - 1);
    let nearestRow = clamp(i32(round(v)), 0, i32(pRows) - 1);
    let nearestIdx = u32(nearestRow * i32(pCols) + nearestCol);

    if (potentialGrid[nearestIdx] >= maxValidPot || (!v00 && !v10 && !v01 && !v11)) {
        var bestPot = maxValidPot;
        var bestDx = 1.0;
        var bestDy = 0.0;

        for (var k = 0u; k < 8u; k++) {
            let nc = nearestCol + cOffsets[k];
            let nr = nearestRow + rOffsets[k];
            if (nc >= 0 && nc < i32(pCols) && nr >= 0 && nr < i32(pRows)) {
                let nIdx = u32(nr * i32(pCols) + nc);
                let nPot = potentialGrid[nIdx];
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
    let flowDirection = getFlowDirection(agent.pos);

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
                if (blockedRaster[cellIdx] == 0u) { continue; }

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
            let nCellCount = min(atomicLoad(&cellCounts[nCellId]), params.maxPerCell);

            for (var k = 0u; k < nCellCount; k++) {
                let neighborIdx = cellAgents[nCellId * params.maxPerCell + k];
                if (neighborIdx == i) { continue; }

                let neighbor = agents[neighborIdx];
                if (neighbor.flags == 0u) { continue; }

                let diff = neighbor.pos - agent.pos;
                let distSq = dot(diff, diff);
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
            let nCellCount = min(atomicLoad(&cellCounts[nCellId]), params.maxPerCell);

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

    agent.vel = agent.vel * (1.0 - velInertia) + finalForce * velInertia;
    agent.pos += agent.vel * params.dt;

    // 11. Circle-box collision resolution
    if (params.isMap == 1u) {
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
                if (blockedRaster[cellIdx] == 0u) { continue; }

                let minX = f32(c) * cell;
                let maxX = minX + cell;
                let minY = f32(r) * cell;
                let maxY = minY + cell;
                let qx = clamp(agent.pos.x, minX, maxX);
                let qy = clamp(agent.pos.y, minY, maxY);
                let dbox = agent.pos - vec2<f32>(qx, qy);
                let dboxSq = dot(dbox, dbox);
                if (dboxSq < agent.radius * agent.radius && dboxSq > 0.000001) {
                    let d = sqrt(dboxSq);
                    let norm = dbox / d;
                    let pen = agent.radius - d;
                    agent.pos += norm * pen;
                    let nVel = dot(agent.vel, norm);
                    if (nVel < 0.0) {
                        agent.vel -= nVel * norm;
                    }
                }
            }
        }

        // Evacuation check: agent reached an exit
        let catchMargin = params.mapCellSize;
        for (var e = 0u; e < params.numExits; e++) {
            let exit = exits[e];
            let reach = exit.z + catchMargin;
            if (abs(agent.pos.x - exit.x) <= reach && abs(agent.pos.y - exit.y) <= reach) {
                agent.radius = 0.0;
                agent.vel = vec2<f32>(0.0, 0.0);
                agent.flags = 0u;
                agents[i] = agent;
                return;
            }
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

// --- Potential Field Generator (16-Direction Dijkstra Flood Fill matching C# PotentialFieldGrid.cs) ---

export function buildPotentialField(
  worldWidth: number,
  worldHeight: number,
  obs1: [number, number, number, number],
  obs2: [number, number, number, number],
  exitZone: [number, number, number, number],
  cols = 100,
  rows = 100,
  cellSize?: number,
): Float32Array {
  const size = cellSize ?? worldWidth / cols;
  const blocked = rasterizeObstacles(cols, rows, size, [obs1, obs2]);
  const seeds = seedPotentialField(cols, rows, size, blocked, [
    { zone: exitZone, potential: 0 },
  ]);
  return solvePotentialField(cols, rows, size, blocked, seeds);
}

// --- Map Scenario Helpers ---

export interface ParsedMapExit {
  readonly x: number;
  readonly y: number;
  readonly radius: number;
}

export interface ParsedMapSpawnZone {
  readonly people: number;
  readonly xs: number[];
  readonly ys: number[];
}

export interface ParsedMapScenario {
  readonly originLatitude: number;
  readonly originLongitude: number;
  readonly metersPerDegreeLatitude: number;
  readonly metersPerDegreeLongitude: number;
  readonly cellSize: number;
  readonly columns: number;
  readonly rows: number;
  readonly blocked: Uint8Array;
  readonly exits: readonly ParsedMapExit[];
  readonly spawnZones: readonly ParsedMapSpawnZone[];
  readonly worldWidth: number;
  readonly worldHeight: number;
  readonly totalPeople: number;
}

export function parseMapScenario(data: Uint8Array): ParsedMapScenario {
  const magic = "EFMAP";
  for (let i = 0; i < 5; i++) {
    if (data[i] !== magic.charCodeAt(i))
      throw new Error("Invalid EFMAP magic.");
  }
  const view = new DataView(data.buffer, data.byteOffset, data.byteLength);
  let offset = 5;
  const version = view.getInt32(offset, true);
  offset += 4;
  if (version !== 1) throw new Error(`Unsupported EFMAP version: ${version}`);

  const originLatitude = view.getFloat64(offset, true);
  offset += 8;
  const originLongitude = view.getFloat64(offset, true);
  offset += 8;
  const metersPerDegreeLatitude = view.getFloat64(offset, true);
  offset += 8;
  const metersPerDegreeLongitude = view.getFloat64(offset, true);
  offset += 8;
  const cellSize = view.getFloat64(offset, true);
  offset += 8;
  const columns = view.getInt32(offset, true);
  offset += 4;
  const rows = view.getInt32(offset, true);
  offset += 4;

  const runCount = view.getInt32(offset, true);
  offset += 4;
  const totalCells = columns * rows;
  const blocked = new Uint8Array(totalCells);
  let bOffset = 0;
  let val = 0;
  for (let i = 0; i < runCount; i++) {
    const run = view.getInt32(offset, true);
    offset += 4;
    if (val === 1) {
      blocked.fill(1, bOffset, bOffset + run);
    }
    bOffset += run;
    val = 1 - val;
  }

  const exitCount = view.getInt32(offset, true);
  offset += 4;
  const exits: ParsedMapExit[] = [];
  for (let i = 0; i < exitCount; i++) {
    const x = view.getFloat64(offset, true);
    offset += 8;
    const y = view.getFloat64(offset, true);
    offset += 8;
    const radius = view.getFloat64(offset, true);
    offset += 8;
    exits.push({ x, y, radius });
  }

  const zoneCount = view.getInt32(offset, true);
  offset += 4;
  const spawnZones: ParsedMapSpawnZone[] = [];
  let totalPeople = 0;
  for (let i = 0; i < zoneCount; i++) {
    const people = view.getInt32(offset, true);
    offset += 4;
    const points = view.getInt32(offset, true);
    offset += 4;
    const xs = new Array<number>(points);
    const ys = new Array<number>(points);
    for (let p = 0; p < points; p++) {
      xs[p] = view.getFloat64(offset, true);
      offset += 8;
      ys[p] = view.getFloat64(offset, true);
      offset += 8;
    }
    totalPeople += people;
    spawnZones.push({ people, xs, ys });
  }

  return {
    originLatitude,
    originLongitude,
    metersPerDegreeLatitude,
    metersPerDegreeLongitude,
    cellSize,
    columns,
    rows,
    blocked,
    exits,
    spawnZones,
    worldWidth: columns * cellSize,
    worldHeight: rows * cellSize,
    totalPeople,
  };
}

export function isPointInPolygon(
  x: number,
  y: number,
  xs: readonly number[],
  ys: readonly number[],
): boolean {
  let inside = false;
  for (let i = 0, j = xs.length - 1; i < xs.length; j = i++) {
    if (
      ys[i] > y !== ys[j] > y &&
      x < ((xs[j] - xs[i]) * (y - ys[i])) / (ys[j] - ys[i]) + xs[i]
    ) {
      inside = !inside;
    }
  }
  return inside;
}

export function buildMapPotentialField(
  scenario: ParsedMapScenario,
): Float32Array {
  const { columns, rows, cellSize, exits, blocked } = scenario;
  const totalCells = columns * rows;
  const field = new Float32Array(totalCells).fill(1e6);

  const heapIdx = new Int32Array(totalCells * 4);
  const heapCost = new Float32Array(totalCells * 4);
  let heapSize = 0;

  function pushHeap(idx: number, cost: number) {
    let i = heapSize++;
    heapIdx[i] = idx;
    heapCost[i] = cost;
    while (i > 0) {
      const p = (i - 1) >> 1;
      if (heapCost[p] <= heapCost[i]) break;
      const ti = heapIdx[i];
      heapIdx[i] = heapIdx[p];
      heapIdx[p] = ti;
      const tc = heapCost[i];
      heapCost[i] = heapCost[p];
      heapCost[p] = tc;
      i = p;
    }
  }

  function popHeap(): { idx: number; cost: number } | null {
    if (heapSize === 0) return null;
    const retIdx = heapIdx[0];
    const retCost = heapCost[0];
    heapSize--;
    if (heapSize > 0) {
      heapIdx[0] = heapIdx[heapSize];
      heapCost[0] = heapCost[heapSize];
      let i = 0;
      while (true) {
        let best = i;
        const left = 2 * i + 1;
        const right = 2 * i + 2;
        if (left < heapSize && heapCost[left] < heapCost[best]) best = left;
        if (right < heapSize && heapCost[right] < heapCost[best]) best = right;
        if (best === i) break;
        const ti = heapIdx[i];
        heapIdx[i] = heapIdx[best];
        heapIdx[best] = ti;
        const tc = heapCost[i];
        heapCost[i] = heapCost[best];
        heapCost[best] = tc;
        i = best;
      }
    }
    return { idx: retIdx, cost: retCost };
  }

  for (const exit of exits) {
    const minCol = Math.max(
      0,
      Math.min(columns - 1, Math.floor((exit.x - exit.radius) / cellSize)),
    );
    const maxCol = Math.max(
      0,
      Math.min(columns - 1, Math.floor((exit.x + exit.radius) / cellSize)),
    );
    const minRow = Math.max(
      0,
      Math.min(rows - 1, Math.floor((exit.y - exit.radius) / cellSize)),
    );
    const maxRow = Math.max(
      0,
      Math.min(rows - 1, Math.floor((exit.y + exit.radius) / cellSize)),
    );

    for (let r = minRow; r <= maxRow; r++) {
      for (let c = minCol; c <= maxCol; c++) {
        const idx = r * columns + c;
        if (blocked[idx] === 0 && field[idx] !== 0) {
          field[idx] = 0;
          pushHeap(idx, 0);
        }
      }
    }
  }

  const step = cellSize;
  const diagStep = cellSize * 1.41421356;
  const knightStep = cellSize * 2.23606798;

  const dCol = [1, -1, 0, 0, 1, 1, -1, -1, 1, 1, -1, -1, 2, 2, -2, -2];
  const dRow = [0, 0, 1, -1, 1, -1, 1, -1, 2, -2, 2, -2, 1, -1, 1, -1];
  const costs = [
    step,
    step,
    step,
    step,
    diagStep,
    diagStep,
    diagStep,
    diagStep,
    knightStep,
    knightStep,
    knightStep,
    knightStep,
    knightStep,
    knightStep,
    knightStep,
    knightStep,
  ];

  while (heapSize > 0) {
    const top = popHeap();
    if (!top) break;
    const { idx: currIdx, cost: currCost } = top;
    if (currCost > field[currIdx]) continue;

    const currCol = currIdx % columns;
    const currRow = Math.floor(currIdx / columns);

    for (let i = 0; i < 16; i++) {
      const nCol = currCol + dCol[i];
      const nRow = currRow + dRow[i];
      if (nCol < 0 || nCol >= columns || nRow < 0 || nRow >= rows) continue;

      const nIdx = nRow * columns + nCol;
      if (blocked[nIdx] !== 0) continue;

      if (i >= 4 && i < 8) {
        if (
          blocked[currRow * columns + nCol] !== 0 &&
          blocked[nRow * columns + currCol] !== 0
        ) {
          continue;
        }
      } else if (i >= 8) {
        const midCol = currCol + Math.sign(dCol[i]);
        const midRow = currRow + Math.sign(dRow[i]);
        if (
          blocked[midRow * columns + midCol] !== 0 ||
          blocked[currRow * columns + midCol] !== 0 ||
          blocked[midRow * columns + currCol] !== 0
        ) {
          continue;
        }
      }

      const candPot = currCost + costs[i];
      if (candPot < field[nIdx]) {
        field[nIdx] = candPot;
        pushHeap(nIdx, candPot);
      }
    }
  }

  return field;
}

export function spawnMapAgents(
  scenario: ParsedMapScenario,
  activeCount: number,
  granulation: number,
  potentialField: Float32Array,
  radius: number,
): Float32Array {
  const { columns, rows, cellSize, spawnZones, blocked } = scenario;
  const spawnCells: number[][] = [];

  for (let z = 0; z < spawnZones.length; z++) {
    const zone = spawnZones[z];
    const cells: number[] = [];
    let minX = Infinity;
    let maxX = -Infinity;
    let minY = Infinity;
    let maxY = -Infinity;
    for (let i = 0; i < zone.xs.length; i++) {
      if (zone.xs[i] < minX) minX = zone.xs[i];
      if (zone.xs[i] > maxX) maxX = zone.xs[i];
      if (zone.ys[i] < minY) minY = zone.ys[i];
      if (zone.ys[i] > maxY) maxY = zone.ys[i];
    }

    const minCol = Math.max(
      0,
      Math.min(columns - 1, Math.floor(minX / cellSize)),
    );
    const maxCol = Math.max(
      0,
      Math.min(columns - 1, Math.floor(maxX / cellSize)),
    );
    const minRow = Math.max(0, Math.min(rows - 1, Math.floor(minY / cellSize)));
    const maxRow = Math.max(0, Math.min(rows - 1, Math.floor(maxY / cellSize)));

    for (let r = minRow; r <= maxRow; r++) {
      const cy = (r + 0.5) * cellSize;
      for (let c = minCol; c <= maxCol; c++) {
        const cx = (c + 0.5) * cellSize;
        const idx = r * columns + c;
        if (
          potentialField[idx] < 900000 &&
          blocked[idx] === 0 &&
          isPointInPolygon(cx, cy, zone.xs, zone.ys)
        ) {
          cells.push(idx);
        }
      }
    }
    spawnCells.push(cells);
  }

  let totalWeight = 0;
  const weights = new Array<number>(spawnZones.length);
  for (let z = 0; z < spawnZones.length; z++) {
    if (spawnCells[z].length > 0) {
      weights[z] = spawnZones[z].people;
      totalWeight += weights[z];
    } else {
      weights[z] = 0;
    }
  }

  if (totalWeight <= 0) {
    for (let z = 0; z < spawnZones.length; z++) {
      if (spawnCells[z].length > 0) {
        weights[z] = 1;
        totalWeight += 1;
      }
    }
  }

  if (totalWeight <= 0) {
    throw new Error(
      "No walkable street inside the evacuation zones is connected to an evacuation point.",
    );
  }

  const allocation = new Array<number>(spawnZones.length).fill(0);
  const remainders: { remainder: number; zone: number }[] = [];
  let assigned = 0;

  for (let z = 0; z < spawnZones.length; z++) {
    const exact = (activeCount * weights[z]) / totalWeight;
    allocation[z] = Math.floor(exact);
    assigned += allocation[z];
    if (weights[z] > 0) {
      remainders.push({ remainder: exact - allocation[z], zone: z });
    }
  }

  remainders.sort((a, b) => b.remainder - a.remainder);
  const remaining = activeCount - assigned;
  for (let i = 0; i < remaining && i < remainders.length; i++) {
    allocation[remainders[i].zone]++;
  }

  const initData = new Float32Array(activeCount * 8);
  const u32View = new Uint32Array(initData.buffer);

  let seed = 12345;
  const nextRandom = () => {
    seed = (seed * 1664525 + 1013904223) >>> 0;
    return seed / 4294967296;
  };

  let agentIndex = 0;
  for (let z = 0; z < spawnZones.length; z++) {
    const cells = spawnCells[z];
    for (let k = 0; k < allocation[z]; k++, agentIndex++) {
      const cellIdx = cells[Math.floor(nextRandom() * cells.length)];
      const c = cellIdx % columns;
      const r = Math.floor(cellIdx / columns);
      let x = (c + 0.5) * cellSize;
      let y = (r + 0.5) * cellSize;

      for (let attempt = 0; attempt < 8; attempt++) {
        const candX = (c + nextRandom()) * cellSize;
        const candY = (r + nextRandom()) * cellSize;
        const cc = Math.floor(candX / cellSize);
        const cr = Math.floor(candY / cellSize);
        if (
          cc >= 0 &&
          cc < columns &&
          cr >= 0 &&
          cr < rows &&
          blocked[cr * columns + cc] === 0
        ) {
          x = candX;
          y = candY;
          break;
        }
      }

      const speed = 1.3 + nextRandom() * 0.4;
      const off = agentIndex * 8;
      initData[off + 0] = x;
      initData[off + 1] = y;
      initData[off + 2] = 0.0;
      initData[off + 3] = 0.0;
      initData[off + 4] = radius;
      initData[off + 5] = speed;
      u32View[off + 6] = 1; // flags = 1 (active)
      u32View[off + 7] = 0;
    }
  }

  return initData;
}

// --- GPU Simulation Engine Implementation ---

export class GpuSimulationEngine implements ISimulationEngine {
  private device: GPUDevice | undefined;

  // Compute pipelines
  private clearPipeline: GPUComputePipeline | undefined;
  private binPipeline: GPUComputePipeline | undefined;
  private clearDensityPipeline: GPUComputePipeline | undefined;
  private accumulateDensityPipeline: GPUComputePipeline | undefined;
  private decodeDensityPipeline: GPUComputePipeline | undefined;
  private updatePenaltyPipeline: GPUComputePipeline | undefined;
  private smoothDensityOnlyPipeline: GPUComputePipeline | undefined;
  private initRelaxPipeline: GPUComputePipeline | undefined;
  private relaxStepPipeline: GPUComputePipeline | undefined;
  private physicsPipeline: GPUComputePipeline | undefined;

  // GPU Buffers
  private agentsBuffer: GPUBuffer | undefined;
  private cellCountsBuffer: GPUBuffer | undefined;
  private cellAgentsBuffer: GPUBuffer | undefined;
  private paramsBuffer: GPUBuffer | undefined;
  private rawDensityBuffer: GPUBuffer | undefined;
  private smoothedDensityBuffer: GPUBuffer | undefined;
  private densityScratchBuffer: GPUBuffer | undefined;
  private seedBuffer: GPUBuffer | undefined;
  private protectedBuffer: GPUBuffer | undefined;
  private relaxStateBuffer: GPUBuffer | undefined;
  private relaxStateStaging: GPUBuffer | undefined;
  private penaltyBuffer: GPUBuffer | undefined;
  private blockedBuffer: GPUBuffer | undefined;
  private staticPotentialBuffer: GPUBuffer | undefined;
  private potentialBufferA: GPUBuffer | undefined;
  private potentialBufferB: GPUBuffer | undefined;
  private stagingBuffer: GPUBuffer | undefined;

  // Bind groups
  private clearBindGroup: GPUBindGroup | undefined;
  private binBindGroup: GPUBindGroup | undefined;
  private densityBindGroup: GPUBindGroup | undefined;
  private accumulateDensityBindGroup: GPUBindGroup | undefined;
  private decodeDensityBindGroup: GPUBindGroup | undefined;
  private smoothDensityReverseBindGroup: GPUBindGroup | undefined;
  private penaltyBindGroup: GPUBindGroup | undefined;
  private smoothDensityOnlyBindGroup: GPUBindGroup | undefined;
  private initRelaxBindGroup: GPUBindGroup | undefined;
  private relaxBindGroupA: GPUBindGroup | undefined;
  private relaxBindGroupB: GPUBindGroup | undefined;
  private bindGroup: GPUBindGroup | undefined;

  // Dedicated KDE Compute Pipelines and Buffers (Adaptive Grid & Smooth Kernel)
  private kdeClearPipeline: GPUComputePipeline | undefined;
  private kdeDepositPipeline: GPUComputePipeline | undefined;
  private kdeSmoothKernelPipeline: GPUComputePipeline | undefined;
  private kdeParamsBuffer: GPUBuffer | undefined;
  private kdeDepositMassBuffer: GPUBuffer | undefined;
  private kdeDepositSpeedBuffer: GPUBuffer | undefined;
  private kdeDensityOutputBuffer: GPUBuffer | undefined;
  private kdeStagingBuffer: GPUBuffer | undefined;
  private kdeClearBindGroup: GPUBindGroup | undefined;
  private kdeDepositBindGroup: GPUBindGroup | undefined;
  private kdeKernelBindGroup: GPUBindGroup | undefined;
  private kdeWidth = 256;
  private kdeHeight = 256;
  private currentRenderMode = "agents";

  private activeCount = 0;
  private rawCount = 1000;
  private activeSession: string | undefined;
  private activeRunGeneration = 1;
  private activeTick = 0;
  private dispatchSequence = 0;
  private dynamicFieldTimer = 0.2;
  private initialDensityPending = false;
  private inFlightBatches = 0;
  private inFlightWorkPromise: Promise<void> | null = null;
  private tickWork: Promise<number> = Promise.resolve(0);

  private worldWidth = 200;
  private worldHeight = 200;
  private socialWeight = 4.5;
  private granulation = 1;

  private gridCols = 50;
  private gridRows = 50;
  private readonly maxPerCell = 128;

  private exitsBuffer: GPUBuffer | undefined;
  private mapBlockedBuffer: GPUBuffer | undefined;
  private isMapScenario = false;
  private mapScenario: ParsedMapScenario | null = null;
  private mapCols = 100;
  private mapRows = 100;
  private mapCellSize = 2.0;
  private potCols = 100;
  private potRows = 100;
  private potCellSize = 2.0;
  private numExits = 1;
  private totalPeople = 0;
  private activeMapAgents = 0;
  private evacuatedCount = 0;
  private evacuatedPerExit: number[] = [];
  private evacuationTimes: number[] = [];
  private lastEvacuationTime = 0;
  private simulationTime = 0;
  private agentEvacuated: Uint8Array | undefined;

  private cpuValues: Float64Array | undefined;
  private isCapturing = false;
  private agentRenderer?: GpuAgentRenderer;
  private telemetryPipeline?: GPUComputePipeline;
  private telemetryBuffer?: GPUBuffer;
  private telemetryStaging?: GPUBuffer;
  private telemetryGroups = 0;

  async drawAgents(
    width: number,
    height: number,
    scaleX: number,
    scaleY: number,
    offsetX: number,
    offsetY: number,
    minRadius: number,
    granulation: number,
    showWhiskers: boolean,
    whiskerLength: number,
  ): Promise<HTMLCanvasElement> {
    await this.tickWork;
    if (!this.device || !this.agentsBuffer)
      throw new Error("GPU engine is not initialized.");
    this.agentRenderer ??= new GpuAgentRenderer(this.device);
    return this.agentRenderer.draw(
      this.agentsBuffer,
      this.activeCount,
      width,
      height,
      scaleX,
      scaleY,
      offsetX,
      offsetY,
      minRadius,
      granulation,
      showWhiskers,
      whiskerLength,
    );
  }

  /** One 32-byte reduction per workgroup, sampled independently of rendering. */
  async captureTelemetry() {
    await this.tickWork;
    if (!this.device || !this.agentsBuffer)
      throw new Error("GPU engine is not initialized.");
    this.telemetryPipeline ??= this.device.createComputePipeline({
      layout: "auto",
      compute: {
        module: this.device.createShaderModule({
          code: `
        struct Agent { pos: vec2<f32>, vel: vec2<f32>, radius: f32,
          desiredSpeed: f32, flags: u32, density: u32, };
        @group(0) @binding(0) var<storage, read> agents: array<Agent>;
        @group(0) @binding(1) var<storage, read_write> output: array<f32>;
        struct Counts { unused: vec4<u32>, count: vec4<u32>, };
        @group(0) @binding(2) var<uniform> params: Counts;
        var<workgroup> moments: array<vec4<f32>, 64>;
        var<workgroup> densities: array<vec2<f32>, 64>;
        var<workgroup> liveCount: array<f32, 64>;
        @compute @workgroup_size(64) fn main(
          @builtin(global_invocation_id) id: vec3<u32>,
          @builtin(local_invocation_index) lane: u32,
          @builtin(workgroup_id) group: vec3<u32>) {
          moments[lane] = vec4(0.); densities[lane] = vec2(0.); liveCount[lane] = 0.;
          if (id.x < params.count.x) {
            let agent = agents[id.x];
            if (agent.flags != 0u && agent.radius > 0.001) {
              let speed = length(agent.vel); let density = bitcast<f32>(agent.density);
              moments[lane] = vec4(speed, speed * speed, density, density * density);
              densities[lane] = vec2(density, 0.); liveCount[lane] = 1.;
            }
          }
          workgroupBarrier();
          for (var stride = 32u; stride > 0u; stride /= 2u) {
            if (lane < stride) {
              moments[lane] += moments[lane + stride];
              densities[lane].x = max(densities[lane].x, densities[lane + stride].x);
              liveCount[lane] += liveCount[lane + stride];
            }
            workgroupBarrier();
          }
          if (lane == 0u) {
            let offset = group.x * 8u;
            output[offset] = liveCount[0]; output[offset + 1u] = moments[0].x;
            output[offset + 2u] = moments[0].y; output[offset + 3u] = moments[0].z;
            output[offset + 4u] = moments[0].w; output[offset + 5u] = densities[0].x;
          }
        }`,
        }),
        entryPoint: "main",
      },
    });
    const groups = Math.max(1, Math.ceil(this.activeCount / 64));
    if (groups !== this.telemetryGroups) {
      this.telemetryBuffer?.destroy();
      this.telemetryStaging?.destroy();
      this.telemetryGroups = groups;
      this.telemetryBuffer = this.device.createBuffer({
        size: groups * 32,
        usage: GPUBufferUsage.STORAGE | GPUBufferUsage.COPY_SRC,
      });
      this.telemetryStaging = this.device.createBuffer({
        size: groups * 32,
        usage: GPUBufferUsage.MAP_READ | GPUBufferUsage.COPY_DST,
      });
    }
    // SimParams.agentCount occupies bytes 16..31.
    const bindGroup = this.device.createBindGroup({
      layout: this.telemetryPipeline.getBindGroupLayout(0),
      entries: [
        { binding: 0, resource: { buffer: this.agentsBuffer } },
        { binding: 1, resource: { buffer: this.telemetryBuffer! } },
        {
          binding: 2,
          resource: { buffer: this.paramsBuffer!, offset: 0, size: 32 },
        },
      ],
    });
    const encoder = this.device.createCommandEncoder();
    const pass = encoder.beginComputePass();
    pass.setPipeline(this.telemetryPipeline);
    pass.setBindGroup(0, bindGroup);
    pass.dispatchWorkgroups(groups);
    pass.end();
    encoder.copyBufferToBuffer(
      this.telemetryBuffer!,
      0,
      this.telemetryStaging!,
      0,
      groups * 32,
    );
    this.device.queue.submit([encoder.finish()]);
    await this.telemetryStaging!.mapAsync(GPUMapMode.READ);
    const values = new Float32Array(this.telemetryStaging!.getMappedRange());
    let active = 0,
      speed = 0,
      speedSquared = 0,
      density = 0,
      densitySquared = 0,
      peak = 0;
    for (let i = 0; i < groups; i++) {
      const offset = i * 8;
      active += values[offset];
      speed += values[offset + 1];
      speedSquared += values[offset + 2];
      density += values[offset + 3];
      densitySquared += values[offset + 4];
      peak = Math.max(peak, values[offset + 5]);
    }
    this.telemetryStaging!.unmap();
    const meanSpeed = active ? speed / active : 0;
    const meanDensity = active ? density / active : 0;
    const evacuated = this.activeCount - active;
    const evacuatedPeople =
      active === 0
        ? this.rawCount
        : Math.min(this.rawCount, evacuated * this.granulation);
    return {
      simulationTime: this.simulationTime,
      activeDots: active,
      activeAgents: this.rawCount - evacuatedPeople,
      evacuatedAgents: evacuatedPeople,
      meanSpeed,
      meanDensity,
      peakDensity: peak,
      speedStdDev: active
        ? Math.sqrt(Math.max(0, speedSquared / active - meanSpeed ** 2))
        : 0,
      densityStdDev: active
        ? Math.sqrt(Math.max(0, densitySquared / active - meanDensity ** 2))
        : 0,
    };
  }

  get count(): number {
    return this.activeCount;
  }

  private obs1: [number, number, number, number] = [70, 36, 24, 56];
  private obs2: [number, number, number, number] = [70, 108, 24, 56];
  private exitZone: [number, number, number, number] = [184, 80, 12, 40];
  private sinks: PotentialSink[] = [];
  private weightDistance = 1.0;
  private weightOccupancy = 0.0;
  private multipleTargets = false;

  private parseObstacle(o: any): [number, number, number, number] | null {
    if (!o) return null;
    if (Array.isArray(o) && o.length >= 4) {
      return [Number(o[0]), Number(o[1]), Number(o[2]), Number(o[3])];
    }
    const x = Number(o.x ?? o.X);
    const y = Number(o.y ?? o.Y);
    const w = Number(o.width ?? o.Width ?? o.w ?? o.W);
    const h = Number(o.height ?? o.Height ?? o.h ?? o.H);
    if (
      Number.isFinite(x) &&
      Number.isFinite(y) &&
      Number.isFinite(w) &&
      Number.isFinite(h)
    ) {
      return [x, y, w, h];
    }
    return null;
  }

  private applyEnvironment(values?: CoreCommandValues): void {
    const rawObs = (values as any)?.obstacles;
    if (Array.isArray(rawObs) && rawObs.length > 0) {
      const o0 = this.parseObstacle(rawObs[0]);
      if (o0) this.obs1 = o0;
      if (rawObs.length > 1) {
        const o1 = this.parseObstacle(rawObs[1]);
        if (o1) this.obs2 = o1;
      }
    } else if (this.worldWidth === 60 && this.worldHeight === 40) {
      // Hala 60m preset matching C# InitDefaultEnvironment()
      this.obs1 = [20.0, 0.0, 8.0, 14.0];
      this.obs2 = [20.0, 26.0, 8.0, 14.0];
    } else {
      // Default formula for 200m, 500m, 1000m presets
      this.obs1 = [
        this.worldWidth * 0.35,
        this.worldHeight * 0.18,
        this.worldWidth * 0.12,
        this.worldHeight * 0.28,
      ];
      this.obs2 = [
        this.worldWidth * 0.35,
        this.worldHeight * 0.54,
        this.worldWidth * 0.12,
        this.worldHeight * 0.28,
      ];
    }

    const rawExit = (values as any)?.exitZone;
    const rawTargets = (values as any)?.targets;
    const parsedExit = this.parseObstacle(rawExit);
    if (parsedExit) {
      this.exitZone = parsedExit;
    } else if (Array.isArray(rawTargets) && rawTargets.length > 0) {
      const parsedTgt = this.parseObstacle(rawTargets[0]);
      if (parsedTgt) this.exitZone = parsedTgt;
    } else if (this.worldWidth === 60 && this.worldHeight === 40) {
      this.exitZone = [56.0, 15.0, 4.0, 10.0];
    } else {
      this.exitZone = [
        this.worldWidth * 0.92,
        this.worldHeight * 0.4,
        this.worldWidth * 0.06,
        this.worldHeight * 0.2,
      ];
    }
    if (typeof values?.weightDistance === "number")
      this.weightDistance = values.weightDistance;
    if (typeof values?.weightOccupancy === "number")
      this.weightOccupancy = values.weightOccupancy;
    const active = Array.isArray(rawTargets)
      ? rawTargets.filter(
          (t: any) =>
            (t.isActive ?? t.IsActive ?? true) && this.parseObstacle(t),
        )
      : [];
    const hasCapacity = active.some(
      (t: any) =>
        Number(t.currentOccupancy ?? t.CurrentOccupancy ?? 0) <
        Number(t.capacity ?? t.Capacity ?? 1000),
    );
    this.sinks = active.map((t: any) => {
      const cap = Math.max(1, Number(t.capacity ?? t.Capacity ?? 1000));
      const occupancy = Number(t.currentOccupancy ?? t.CurrentOccupancy ?? 0);
      const base =
        ((Math.max(0, this.weightOccupancy) /
          Math.max(0.001, this.weightDistance)) *
          Math.max(1, this.worldWidth) *
          occupancy) /
        cap;
      return {
        zone: this.parseObstacle(t)!,
        potential: Math.fround(
          base + (occupancy >= cap && hasCapacity ? 100000 : 0),
        ),
      };
    });
    if (!Array.isArray(rawTargets) || rawTargets.length === 0)
      this.sinks = [{ zone: this.exitZone, potential: 0 }];
    this.numExits = this.sinks.length;
    this.multipleTargets = Array.isArray(rawTargets) && rawTargets.length > 1;
  }

  private getAgentRadius(granulation: number): number {
    return granulation > 1
      ? 0.35 * (1.0 + 0.2 * Math.sqrt(granulation - 1))
      : 0.35;
  }

  async boot(_runtimeUrl?: string): Promise<void> {
    if (this.device) return;
    if (!isWebGpuSupported()) {
      throw new Error("WebGPU is not supported in this environment.");
    }

    // On Windows, requesting adapter with powerPreference logs a Chromium warning (crbug.com/369219127).
    const isWindows =
      typeof navigator !== "undefined" &&
      (/Win/i.test(navigator.userAgent || "") ||
        /Win/i.test((navigator as any).userAgentData?.platform || "") ||
        /Win/i.test(navigator.platform || ""));

    let adapter: GPUAdapter | null = null;
    if (!isWindows) {
      adapter = await navigator.gpu.requestAdapter({
        powerPreference: "high-performance",
      });
    }
    if (!adapter) {
      adapter = await navigator.gpu.requestAdapter();
    }
    if (!adapter) throw new Error("No suitable GPU adapter found.");

    this.device = await adapter.requestDevice();

    // Prevent uncaptured errors from bubbling to Worker.onerror
    this.device.onuncapturederror = (event: GPUUncapturedErrorEvent) => {
      console.warn(
        "WebGPU uncaptured device error:",
        event.error?.message ?? event.error,
      );
    };

    this.device.pushErrorScope("validation");

    // 1. Shaders & Pipelines
    const clearModule = this.device.createShaderModule({
      code: clearGridShader,
    });
    const binModule = this.device.createShaderModule({ code: binAgentsShader });
    const clearDensityModule = this.device.createShaderModule({
      code: clearDensityShader,
    });
    const accDensityModule = this.device.createShaderModule({
      code: accumulateDensityShader,
    });
    const decodeDensityModule = this.device.createShaderModule({
      code: decodeDensityShader,
    });
    this.decodeDensityPipeline = this.device.createComputePipeline({
      layout: "auto",
      compute: { module: decodeDensityModule, entryPoint: "main" },
    });
    const updatePenaltyModule = this.device.createShaderModule({
      code: updatePenaltyShader,
    });
    const smoothDensityOnlyModule = this.device.createShaderModule({
      code: smoothDensityOnlyShader,
    });
    const initRelaxModule = this.device.createShaderModule({
      code: initRelaxShader,
    });
    const relaxStepModule = this.device.createShaderModule({
      code: relaxStepShader,
    });
    const physicsModule = this.device.createShaderModule({
      code: stepPhysicsShader,
    });

    this.clearPipeline = this.device.createComputePipeline({
      layout: "auto",
      compute: { module: clearModule, entryPoint: "main" },
    });
    this.binPipeline = this.device.createComputePipeline({
      layout: "auto",
      compute: { module: binModule, entryPoint: "main" },
    });
    this.clearDensityPipeline = this.device.createComputePipeline({
      layout: "auto",
      compute: { module: clearDensityModule, entryPoint: "main" },
    });
    this.accumulateDensityPipeline = this.device.createComputePipeline({
      layout: "auto",
      compute: { module: accDensityModule, entryPoint: "main" },
    });
    this.updatePenaltyPipeline = this.device.createComputePipeline({
      layout: "auto",
      compute: { module: updatePenaltyModule, entryPoint: "main" },
    });
    this.smoothDensityOnlyPipeline = this.device.createComputePipeline({
      layout: "auto",
      compute: { module: smoothDensityOnlyModule, entryPoint: "main" },
    });
    this.initRelaxPipeline = this.device.createComputePipeline({
      layout: "auto",
      compute: { module: initRelaxModule, entryPoint: "main" },
    });
    this.relaxStepPipeline = this.device.createComputePipeline({
      layout: "auto",
      compute: { module: relaxStepModule, entryPoint: "main" },
    });
    this.physicsPipeline = this.device.createComputePipeline({
      layout: "auto",
      compute: { module: physicsModule, entryPoint: "main" },
    });

    const kdeClearModule = this.device.createShaderModule({
      code: kdeClearShader,
    });
    const kdeDepositModule = this.device.createShaderModule({
      code: kdeDepositShader,
    });
    const kdeSmoothKernelModule = this.device.createShaderModule({
      code: kdeSmoothKernelShader,
    });

    this.kdeClearPipeline = this.device.createComputePipeline({
      layout: "auto",
      compute: { module: kdeClearModule, entryPoint: "main" },
    });
    this.kdeDepositPipeline = this.device.createComputePipeline({
      layout: "auto",
      compute: { module: kdeDepositModule, entryPoint: "main" },
    });
    this.kdeSmoothKernelPipeline = this.device.createComputePipeline({
      layout: "auto",
      compute: { module: kdeSmoothKernelModule, entryPoint: "main" },
    });

    const validationError = await this.device.popErrorScope();
    if (validationError) {
      throw new Error(
        `WebGPU pipeline initialization failed: ${validationError.message}`,
      );
    }

    // 2. Spatial Grid Buffers (Adaptive grid based on physical cell size ~4.0m)
    this.ensureSpatialGrid();

    // 2b. Adaptive KDE Grid Buffers
    this.ensureKdeGrid();

    // 3. SimParams Uniform Buffer (128 bytes: aligned to 16 bytes)
    this.paramsBuffer = this.device.createBuffer({
      size: 128,
      usage: GPUBufferUsage.UNIFORM | GPUBufferUsage.COPY_DST,
    });

    // 4. Density & Dynamic Potential Buffers (100x100 = 10,000 cells)
    const potCells = 100 * 100;
    this.rawDensityBuffer = this.device.createBuffer({
      size: potCells * 4,
      usage: GPUBufferUsage.STORAGE | GPUBufferUsage.COPY_DST,
    });
    this.smoothedDensityBuffer = this.device.createBuffer({
      size: potCells * 4,
      usage:
        GPUBufferUsage.STORAGE |
        GPUBufferUsage.COPY_DST |
        GPUBufferUsage.COPY_SRC,
    });
    this.densityScratchBuffer = this.device.createBuffer({
      size: potCells * 4,
      usage: GPUBufferUsage.STORAGE | GPUBufferUsage.COPY_DST,
    });
    this.seedBuffer = this.device.createBuffer({
      size: potCells * 4,
      usage: GPUBufferUsage.STORAGE | GPUBufferUsage.COPY_DST,
    });
    this.protectedBuffer = this.device.createBuffer({
      size: potCells * 4,
      usage: GPUBufferUsage.STORAGE | GPUBufferUsage.COPY_DST,
    });
    this.relaxStateBuffer = this.device.createBuffer({
      size: 8,
      usage:
        GPUBufferUsage.STORAGE |
        GPUBufferUsage.COPY_SRC |
        GPUBufferUsage.COPY_DST,
    });
    this.relaxStateStaging = this.device.createBuffer({
      size: 8,
      usage: GPUBufferUsage.MAP_READ | GPUBufferUsage.COPY_DST,
    });
    this.penaltyBuffer = this.device.createBuffer({
      size: potCells * 4,
      usage:
        GPUBufferUsage.STORAGE |
        GPUBufferUsage.COPY_DST |
        GPUBufferUsage.COPY_SRC,
    });
    this.blockedBuffer = this.device.createBuffer({
      size: potCells * 4,
      usage: GPUBufferUsage.STORAGE | GPUBufferUsage.COPY_DST,
    });
    this.staticPotentialBuffer = this.device.createBuffer({
      size: potCells * 4,
      usage:
        GPUBufferUsage.STORAGE |
        GPUBufferUsage.COPY_SRC |
        GPUBufferUsage.COPY_DST,
    });
    this.potentialBufferA = this.device.createBuffer({
      size: potCells * 4,
      usage:
        GPUBufferUsage.STORAGE |
        GPUBufferUsage.COPY_DST |
        GPUBufferUsage.COPY_SRC,
    });
    this.potentialBufferB = this.device.createBuffer({
      size: potCells * 4,
      usage: GPUBufferUsage.STORAGE | GPUBufferUsage.COPY_DST,
    });

    this.exitsBuffer = this.device.createBuffer({
      size: 64,
      usage: GPUBufferUsage.STORAGE | GPUBufferUsage.COPY_DST,
    });
    this.mapBlockedBuffer = this.device.createBuffer({
      size: 64,
      usage: GPUBufferUsage.STORAGE | GPUBufferUsage.COPY_DST,
    });
    this.applyEnvironment();
  }

  private updatePotentialGrid(): void {
    if (this.isMapScenario) {
      return;
    }
    if (
      !this.device ||
      !this.staticPotentialBuffer ||
      !this.potentialBufferA ||
      !this.blockedBuffer ||
      !this.penaltyBuffer ||
      !this.paramsBuffer
    ) {
      return;
    }

    this.potCellSize = Math.max(
      2.0,
      Math.max(this.worldWidth, this.worldHeight) / 100.0,
    );
    this.potCols = Math.max(5, Math.ceil(this.worldWidth / this.potCellSize));
    this.potRows = Math.max(5, Math.ceil(this.worldHeight / this.potCellSize));
    const totalCells = this.potCols * this.potRows;

    const blockedData = rasterizeObstacles(
      this.potCols,
      this.potRows,
      this.potCellSize,
      [this.obs1, this.obs2],
    );
    const seeds = seedPotentialField(
      this.potCols,
      this.potRows,
      this.potCellSize,
      blockedData,
      this.sinks,
    );
    // C# ignores the base cost when refining a single target under congestion.
    const dynamicSeeds =
      !this.multipleTargets && this.sinks.length === 1
        ? seedPotentialField(
            this.potCols,
            this.potRows,
            this.potCellSize,
            blockedData,
            [{ zone: this.sinks[0].zone, potential: 0 }],
          )
        : seeds;
    const grid = solvePotentialField(
      this.potCols,
      this.potRows,
      this.potCellSize,
      blockedData,
      seeds,
    );
    const protectedSeeds = seedPotentialField(
      this.potCols,
      this.potRows,
      this.potCellSize,
      blockedData,
      this.sinks,
      2.5,
    );
    const protectedData = Uint32Array.from(protectedSeeds, (v) =>
      v < impassablePotential ? 1 : 0,
    );
    this.device.queue.writeBuffer(this.staticPotentialBuffer, 0, grid.buffer);
    this.device.queue.writeBuffer(this.potentialBufferA, 0, grid.buffer);
    this.device.queue.writeBuffer(this.blockedBuffer, 0, blockedData.buffer);
    this.device.queue.writeBuffer(this.seedBuffer!, 0, dynamicSeeds.buffer);
    this.device.queue.writeBuffer(
      this.protectedBuffer!,
      0,
      protectedData.buffer,
    );
    this.exitsBuffer?.destroy();
    const exitData = new Float32Array(Math.max(1, this.sinks.length) * 4);
    this.sinks.forEach((sink, i) => exitData.set(sink.zone, i * 4));
    this.exitsBuffer = this.device.createBuffer({
      size: Math.max(64, exitData.byteLength),
      usage: GPUBufferUsage.STORAGE | GPUBufferUsage.COPY_DST,
    });
    this.device.queue.writeBuffer(this.exitsBuffer, 0, exitData.buffer);

    // Reset penalties to 0
    const zeroPenalties = new Float32Array(totalCells);
    this.device.queue.writeBuffer(this.penaltyBuffer, 0, zeroPenalties.buffer);
    this.dynamicFieldTimer = 0.2;

    // Create bind groups for dynamic potential passes
    this.densityBindGroup = this.device.createBindGroup({
      layout: this.clearDensityPipeline!.getBindGroupLayout(0),
      entries: [{ binding: 0, resource: { buffer: this.rawDensityBuffer! } }],
    });

    this.decodeDensityBindGroup = this.device.createBindGroup({
      layout: this.decodeDensityPipeline!.getBindGroupLayout(0),
      entries: [
        { binding: 0, resource: { buffer: this.rawDensityBuffer! } },
        { binding: 1, resource: { buffer: this.densityScratchBuffer! } },
      ],
    });
    this.penaltyBindGroup = this.device.createBindGroup({
      layout: this.updatePenaltyPipeline!.getBindGroupLayout(0),
      entries: [
        { binding: 0, resource: { buffer: this.smoothedDensityBuffer! } },
        { binding: 1, resource: { buffer: this.protectedBuffer! } },
        { binding: 2, resource: { buffer: this.penaltyBuffer! } },
        { binding: 3, resource: { buffer: this.blockedBuffer! } },
        { binding: 4, resource: { buffer: this.paramsBuffer! } },
        { binding: 5, resource: { buffer: this.relaxStateBuffer! } },
      ],
    });
    const smoothEntries = (reverse: boolean) => [
      {
        binding: 0,
        resource: {
          buffer: reverse
            ? this.smoothedDensityBuffer!
            : this.densityScratchBuffer!,
        },
      },
      {
        binding: 1,
        resource: {
          buffer: reverse
            ? this.densityScratchBuffer!
            : this.smoothedDensityBuffer!,
        },
      },
      { binding: 2, resource: { buffer: this.paramsBuffer! } },
    ];
    this.smoothDensityOnlyBindGroup = this.device.createBindGroup({
      layout: this.smoothDensityOnlyPipeline!.getBindGroupLayout(0),
      entries: smoothEntries(false),
    });
    this.smoothDensityReverseBindGroup = this.device.createBindGroup({
      layout: this.smoothDensityOnlyPipeline!.getBindGroupLayout(0),
      entries: smoothEntries(true),
    });

    this.initRelaxBindGroup = this.device.createBindGroup({
      layout: this.initRelaxPipeline!.getBindGroupLayout(0),
      entries: [
        { binding: 0, resource: { buffer: this.potentialBufferA! } },
        { binding: 1, resource: { buffer: this.seedBuffer! } },
        { binding: 2, resource: { buffer: this.paramsBuffer! } },
        { binding: 3, resource: { buffer: this.staticPotentialBuffer! } },
        { binding: 4, resource: { buffer: this.relaxStateBuffer! } },
      ],
    });

    this.relaxBindGroupA = this.device.createBindGroup({
      layout: this.relaxStepPipeline!.getBindGroupLayout(0),
      entries: [
        { binding: 0, resource: { buffer: this.potentialBufferA! } },
        { binding: 1, resource: { buffer: this.potentialBufferB! } },
        { binding: 2, resource: { buffer: this.penaltyBuffer! } },
        { binding: 3, resource: { buffer: this.blockedBuffer! } },
        { binding: 4, resource: { buffer: this.paramsBuffer! } },
        { binding: 5, resource: { buffer: this.relaxStateBuffer! } },
      ],
    });

    this.relaxBindGroupB = this.device.createBindGroup({
      layout: this.relaxStepPipeline!.getBindGroupLayout(0),
      entries: [
        { binding: 0, resource: { buffer: this.potentialBufferB! } },
        { binding: 1, resource: { buffer: this.potentialBufferA! } },
        { binding: 2, resource: { buffer: this.penaltyBuffer! } },
        { binding: 3, resource: { buffer: this.blockedBuffer! } },
        { binding: 4, resource: { buffer: this.paramsBuffer! } },
        { binding: 5, resource: { buffer: this.relaxStateBuffer! } },
      ],
    });
  }

  private updateSimParams(): void {
    if (!this.device || !this.paramsBuffer) return;
    const buf = new ArrayBuffer(128);
    const f32 = new Float32Array(buf);
    const u32 = new Uint32Array(buf);

    f32[0] = this.worldWidth;
    f32[1] = this.worldHeight;
    f32[2] = 0.016; // dt: 16 ms per tick matching C# Step(0.016, 1.0)
    f32[3] = this.socialWeight;
    u32[4] = this.activeCount;
    u32[5] = this.gridCols;
    u32[6] = this.gridRows;
    u32[7] = this.maxPerCell;

    // Obstacle 1: [x, y, w, h] (floats 8..11, bytes 32..47)
    f32[8] = this.obs1[0];
    f32[9] = this.obs1[1];
    f32[10] = this.obs1[2];
    f32[11] = this.obs1[3];

    // Obstacle 2: [x, y, w, h] (floats 12..15, bytes 48..63)
    f32[12] = this.obs2[0];
    f32[13] = this.obs2[1];
    f32[14] = this.obs2[2];
    f32[15] = this.obs2[3];

    // ExitZone: [x, y, w, h] (floats 16..19, bytes 64..79)
    f32[16] = this.exitZone[0];
    f32[17] = this.exitZone[1];
    f32[18] = this.exitZone[2];
    f32[19] = this.exitZone[3];

    u32[20] = this.granulation;
    u32[21] = this.isMapScenario ? 1 : 0;
    u32[22] = this.mapCols;
    u32[23] = this.mapRows;
    f32[24] = this.mapCellSize;
    u32[25] = this.numExits;
    u32[26] = this.potCols;
    u32[27] = this.potRows;
    f32[28] = this.potCellSize;
    u32[29] = 0;
    u32[30] = 0;
    u32[31] = 0;

    this.device.queue.writeBuffer(this.paramsBuffer, 0, buf);
  }

  private reallocateAgents(count: number): void {
    if (!this.device) return;
    this.activeCount = count;
    const agentByteSize = 32; // 8 floats per agent
    const totalBytes = count * agentByteSize;

    this.agentsBuffer?.destroy();
    this.stagingBuffer?.destroy();

    this.agentsBuffer = this.device.createBuffer({
      size: Math.max(totalBytes, 64),
      usage:
        GPUBufferUsage.STORAGE |
        GPUBufferUsage.COPY_SRC |
        GPUBufferUsage.COPY_DST,
    });

    this.stagingBuffer = this.device.createBuffer({
      size: Math.max(totalBytes, 64),
      usage: GPUBufferUsage.MAP_READ | GPUBufferUsage.COPY_DST,
    });

    this.cpuValues = new Float64Array(count * 5);
    const radius = this.getAgentRadius(this.granulation);

    this.simulationTime = 0;

    let initData: Float32Array;
    if (this.isMapScenario && this.mapScenario) {
      this.activeMapAgents = count;
      this.evacuatedCount = 0;
      this.evacuatedPerExit = new Array(this.numExits).fill(0);
      this.evacuationTimes = [];
      this.lastEvacuationTime = 0;
      this.simulationTime = 0;
      this.agentEvacuated = new Uint8Array(count);
      const staticField = buildMapPotentialField(this.mapScenario);
      initData = spawnMapAgents(
        this.mapScenario,
        count,
        this.granulation,
        staticField,
        radius,
      );
    } else {
      initData = new Float32Array(count * 8);
      const u32View = new Uint32Array(initData.buffer);

      const spawnMinX = this.worldWidth * 0.03;
      const spawnMaxX = this.worldWidth * 0.18;
      const spawnMinY = this.worldHeight * 0.15;
      const spawnMaxY = this.worldHeight * 0.85;

      let seed = 12345;
      const nextRandom = () => {
        seed = (seed * 1664525 + 1013904223) >>> 0;
        return seed / 4294967296;
      };

      for (let i = 0; i < count; i++) {
        const px = spawnMinX + nextRandom() * (spawnMaxX - spawnMinX);
        const py = spawnMinY + nextRandom() * (spawnMaxY - spawnMinY);
        const vx = 1.2;
        const vy = 0.0;
        const speed = 1.3 + nextRandom() * 0.4;

        const off = i * 8;
        initData[off + 0] = px;
        initData[off + 1] = py;
        initData[off + 2] = vx;
        initData[off + 3] = vy;
        initData[off + 4] = radius;
        initData[off + 5] = speed;
        u32View[off + 6] = 1; // flags = 1 (active)
        u32View[off + 7] = 0;
      }
    }
    this.device.queue.writeBuffer(this.agentsBuffer, 0, initData.buffer);
    this.initialDensityPending = true;

    this.recreateSpatialBindGroups();

    this.accumulateDensityBindGroup = this.device.createBindGroup({
      layout: this.accumulateDensityPipeline!.getBindGroupLayout(0),
      entries: [
        { binding: 0, resource: { buffer: this.agentsBuffer } },
        { binding: 1, resource: { buffer: this.rawDensityBuffer! } },
        { binding: 2, resource: { buffer: this.paramsBuffer! } },
      ],
    });
    this.recreateKdeDepositBindGroup();
  }

  private recreateSpatialBindGroups(): void {
    if (
      !this.device ||
      !this.agentsBuffer ||
      !this.cellCountsBuffer ||
      !this.cellAgentsBuffer ||
      !this.paramsBuffer ||
      !this.potentialBufferA ||
      !this.smoothedDensityBuffer ||
      !this.blockedBuffer ||
      !this.exitsBuffer ||
      !this.clearPipeline ||
      !this.binPipeline ||
      !this.physicsPipeline
    ) {
      return;
    }

    this.clearBindGroup = this.device.createBindGroup({
      layout: this.clearPipeline.getBindGroupLayout(0),
      entries: [{ binding: 0, resource: { buffer: this.cellCountsBuffer } }],
    });

    this.binBindGroup = this.device.createBindGroup({
      layout: this.binPipeline.getBindGroupLayout(0),
      entries: [
        { binding: 0, resource: { buffer: this.agentsBuffer } },
        { binding: 1, resource: { buffer: this.cellCountsBuffer } },
        { binding: 2, resource: { buffer: this.cellAgentsBuffer } },
        { binding: 3, resource: { buffer: this.paramsBuffer } },
      ],
    });

    const blockedRes =
      this.isMapScenario && this.mapBlockedBuffer
        ? this.mapBlockedBuffer
        : this.blockedBuffer;
    const exitsRes = this.exitsBuffer;

    this.bindGroup = this.device.createBindGroup({
      layout: this.physicsPipeline.getBindGroupLayout(0),
      entries: [
        { binding: 0, resource: { buffer: this.agentsBuffer } },
        { binding: 1, resource: { buffer: this.cellCountsBuffer } },
        { binding: 2, resource: { buffer: this.cellAgentsBuffer } },
        { binding: 3, resource: { buffer: this.paramsBuffer } },
        { binding: 4, resource: { buffer: this.potentialBufferA } },
        { binding: 5, resource: { buffer: this.smoothedDensityBuffer } },
        { binding: 6, resource: { buffer: blockedRes } },
        { binding: 7, resource: { buffer: exitsRes } },
      ],
    });
  }

  private reallocateSpatialGrid(cols: number, rows: number): void {
    if (!this.device) return;
    if (
      this.cellCountsBuffer &&
      this.cellAgentsBuffer &&
      this.gridCols === cols &&
      this.gridRows === rows
    ) {
      return;
    }
    this.gridCols = cols;
    this.gridRows = rows;
    const totalCells = this.gridCols * this.gridRows;

    this.cellCountsBuffer?.destroy();
    this.cellAgentsBuffer?.destroy();

    this.cellCountsBuffer = this.device.createBuffer({
      size: totalCells * 4,
      usage: GPUBufferUsage.STORAGE | GPUBufferUsage.COPY_DST,
    });
    this.cellAgentsBuffer = this.device.createBuffer({
      size: totalCells * this.maxPerCell * 4,
      usage: GPUBufferUsage.STORAGE,
    });
    this.recreateSpatialBindGroups();
  }

  private ensureSpatialGrid(): void {
    const targetCellSize = 4.0;
    let nextCols = Math.max(10, Math.ceil(this.worldWidth / targetCellSize));
    let nextRows = Math.max(10, Math.ceil(this.worldHeight / targetCellSize));
    const maxDimension = 250;
    if (nextCols > maxDimension || nextRows > maxDimension) {
      const maxDim = Math.max(nextCols, nextRows);
      nextCols = Math.max(10, Math.round((nextCols / maxDim) * maxDimension));
      nextRows = Math.max(10, Math.round((nextRows / maxDim) * maxDimension));
    }
    this.reallocateSpatialGrid(nextCols, nextRows);
  }

  private ensureKdeGrid(): void {
    if (!this.device) return;
    // Lift rigid 100x100: target ~1.5m to 2.5m physical cell size with square cells
    const targetCellSize = Math.max(
      1.5,
      Math.min(this.worldWidth, this.worldHeight) / 200.0,
    );
    const cols = Math.min(
      512,
      Math.max(128, Math.round(this.worldWidth / targetCellSize)),
    );
    const rows = Math.min(
      512,
      Math.max(128, Math.round(this.worldHeight / targetCellSize)),
    );

    if (
      this.kdeWidth === cols &&
      this.kdeHeight === rows &&
      this.kdeDensityOutputBuffer &&
      this.kdeStagingBuffer
    ) {
      return;
    }

    this.kdeWidth = cols;
    this.kdeHeight = rows;
    const totalCells = cols * rows;

    this.kdeParamsBuffer?.destroy();
    this.kdeDepositMassBuffer?.destroy();
    this.kdeDepositSpeedBuffer?.destroy();
    this.kdeDensityOutputBuffer?.destroy();
    this.kdeStagingBuffer?.destroy();

    this.kdeParamsBuffer = this.device.createBuffer({
      size: 48,
      usage: GPUBufferUsage.UNIFORM | GPUBufferUsage.COPY_DST,
    });

    this.kdeDepositMassBuffer = this.device.createBuffer({
      size: totalCells * 4,
      usage: GPUBufferUsage.STORAGE | GPUBufferUsage.COPY_DST,
    });

    this.kdeDepositSpeedBuffer = this.device.createBuffer({
      size: totalCells * 4,
      usage: GPUBufferUsage.STORAGE | GPUBufferUsage.COPY_DST,
    });

    this.kdeDensityOutputBuffer = this.device.createBuffer({
      size: totalCells * 4,
      usage: GPUBufferUsage.STORAGE | GPUBufferUsage.COPY_SRC,
    });

    this.kdeStagingBuffer = this.device.createBuffer({
      size: totalCells * 4,
      usage: GPUBufferUsage.MAP_READ | GPUBufferUsage.COPY_DST,
    });

    if (this.kdeClearPipeline) {
      this.kdeClearBindGroup = this.device.createBindGroup({
        layout: this.kdeClearPipeline.getBindGroupLayout(0),
        entries: [
          { binding: 0, resource: { buffer: this.kdeDepositMassBuffer } },
          { binding: 1, resource: { buffer: this.kdeDepositSpeedBuffer } },
        ],
      });
    }

    this.recreateKdeDepositBindGroup();
    this.recreateKdeKernelBindGroup();
  }

  private recreateKdeDepositBindGroup(): void {
    if (
      !this.device ||
      !this.agentsBuffer ||
      !this.kdeDepositMassBuffer ||
      !this.kdeDepositSpeedBuffer ||
      !this.kdeParamsBuffer ||
      !this.kdeDepositPipeline
    ) {
      return;
    }
    this.kdeDepositBindGroup = this.device.createBindGroup({
      layout: this.kdeDepositPipeline.getBindGroupLayout(0),
      entries: [
        { binding: 0, resource: { buffer: this.agentsBuffer } },
        { binding: 1, resource: { buffer: this.kdeDepositMassBuffer } },
        { binding: 2, resource: { buffer: this.kdeDepositSpeedBuffer } },
        { binding: 3, resource: { buffer: this.kdeParamsBuffer } },
      ],
    });
  }

  private recreateKdeKernelBindGroup(): void {
    if (
      !this.device ||
      !this.kdeDepositMassBuffer ||
      !this.kdeDepositSpeedBuffer ||
      !this.kdeDensityOutputBuffer ||
      !this.kdeParamsBuffer ||
      !this.kdeSmoothKernelPipeline
    ) {
      return;
    }
    this.kdeKernelBindGroup = this.device.createBindGroup({
      layout: this.kdeSmoothKernelPipeline.getBindGroupLayout(0),
      entries: [
        { binding: 0, resource: { buffer: this.kdeDepositMassBuffer } },
        { binding: 1, resource: { buffer: this.kdeDepositSpeedBuffer } },
        { binding: 2, resource: { buffer: this.kdeDensityOutputBuffer } },
        { binding: 3, resource: { buffer: this.kdeParamsBuffer } },
      ],
    });
  }

  private updateKdeParams(): void {
    if (!this.device || !this.kdeParamsBuffer) return;
    const buf = new ArrayBuffer(48);
    const f32 = new Float32Array(buf);
    const u32 = new Uint32Array(buf);
    f32[0] = this.worldWidth;
    f32[1] = this.worldHeight;
    u32[2] = this.kdeWidth;
    u32[3] = this.kdeHeight;
    const isSpeedMode = this.currentRenderMode === "heatmap";
    f32[4] = isSpeedMode ? 18.0 : 25.0; // kernelRadius in meters
    u32[5] = this.granulation;
    u32[6] = this.activeCount;
    u32[7] = isSpeedMode ? 1 : 0;
    this.device.queue.writeBuffer(this.kdeParamsBuffer, 0, buf);
  }

  dispatch(
    session: string,
    kind: BrowserCoreCommand,
    values: CoreCommandValues,
    _profiler: ProfilingWindow,
  ): void {
    this.activeSession = session;
    this.dispatchSequence++;

    if (kind === "init" || kind === "reset") {
      this.activeRunGeneration++;
      this.activeTick = 0;
      this.dynamicFieldTimer = 0.2;
      this.inFlightBatches = 0;
      this.inFlightWorkPromise = null;
      if (typeof values.worldWidth === "number")
        this.worldWidth = values.worldWidth;
      if (typeof values.worldHeight === "number")
        this.worldHeight = values.worldHeight;
      if (typeof values.socialRepulsionWeight === "number")
        this.socialWeight = values.socialRepulsionWeight;
      if (typeof values.granulation === "number")
        this.granulation = values.granulation;

      this.applyEnvironment(values);
      if (typeof values.count === "number") this.rawCount = values.count;
      const simCount = Math.max(1, Math.ceil(this.rawCount / this.granulation));
      this.ensureSpatialGrid();
      this.ensureKdeGrid();
      this.updatePotentialGrid();
      this.reallocateAgents(simCount);
      this.updateSimParams();
      return;
    }

    if (kind === "count") {
      this.activeRunGeneration++;
      this.activeTick = 0;
      this.dynamicFieldTimer = 0.2;
      this.inFlightBatches = 0;
      this.inFlightWorkPromise = null;
      if (typeof values.count === "number") this.rawCount = values.count;
      const simCount = Math.max(1, Math.ceil(this.rawCount / this.granulation));
      this.reallocateAgents(simCount);
      this.updateSimParams();
      return;
    }

    if (kind === "preset") {
      this.activeRunGeneration++;
      this.activeTick = 0;
      this.dynamicFieldTimer = 0.2;
      this.inFlightBatches = 0;
      this.inFlightWorkPromise = null;
      if (typeof values.worldWidth === "number")
        this.worldWidth = values.worldWidth;
      if (typeof values.worldHeight === "number")
        this.worldHeight = values.worldHeight;
      if (typeof values.count === "number") this.rawCount = values.count;
      if (typeof values.granulation === "number")
        this.granulation = values.granulation;
      this.applyEnvironment(values);
      const simCount = Math.max(1, Math.ceil(this.rawCount / this.granulation));
      this.ensureSpatialGrid();
      this.ensureKdeGrid();
      this.updatePotentialGrid();
      this.reallocateAgents(simCount);
      this.updateSimParams();
      return;
    }

    if (kind === "set-environment") {
      this.activeRunGeneration++;
      this.activeTick = 0;
      this.dynamicFieldTimer = 0.2;
      this.inFlightBatches = 0;
      this.inFlightWorkPromise = null;
      if (typeof values.worldWidth === "number")
        this.worldWidth = values.worldWidth;
      if (typeof values.worldHeight === "number")
        this.worldHeight = values.worldHeight;
      this.applyEnvironment(values);
      this.ensureSpatialGrid();
      this.ensureKdeGrid();
      this.updatePotentialGrid();
      this.recreateSpatialBindGroups();
      this.updateSimParams();
      return;
    }

    if (kind === "granulation") {
      if (typeof values.granulation === "number")
        this.granulation = values.granulation;
      const simCount = Math.max(1, Math.ceil(this.rawCount / this.granulation));
      this.ensureSpatialGrid();
      this.reallocateAgents(simCount);
      this.updateSimParams();
      return;
    }

    if (kind === "weight") {
      if (typeof values.socialRepulsionWeight === "number") {
        this.socialWeight = values.socialRepulsionWeight;
        this.updateSimParams();
      }
      return;
    }

    if (kind === "render") {
      if (typeof values.renderMode === "string") {
        this.currentRenderMode = values.renderMode;
      }
      return;
    }
  }

  advanceFixedTicks(ticks: number): Promise<number> {
    const work = this.tickWork.then(() => this.advanceGpuTicks(ticks));
    this.tickWork = work.catch(() => this.activeTick);
    return work;
  }

  private async advanceGpuTicks(ticks: number): Promise<number> {
    if (
      !this.device ||
      !this.clearPipeline ||
      !this.binPipeline ||
      !this.physicsPipeline ||
      !this.bindGroup ||
      !this.binBindGroup ||
      !this.clearBindGroup ||
      this.activeCount === 0
    ) {
      return this.activeTick;
    }

    let commandEncoder = this.device.createCommandEncoder();
    const workgroups = Math.ceil(this.activeCount / 64);
    const gridWorkgroups = Math.ceil((this.gridCols * this.gridRows) / 64);
    const potWorkgroups = Math.ceil((this.potCols * this.potRows) / 64);
    const pot2D_X = Math.ceil(this.potCols / 16);
    const pot2D_Y = Math.ceil(this.potRows / 16);

    for (let step = 0; step < ticks; step++) {
      this.simulationTime += 0.016;
      this.dynamicFieldTimer += 0.016;

      const refine =
        !this.isMapScenario &&
        (this.initialDensityPending ||
          this.dynamicFieldTimer >= 0.2 ||
          this.activeTick + step === 0);
      if (refine || (!this.isMapScenario && this.granulation > 1)) {
        const run = (
          pipeline: GPUComputePipeline,
          bindGroup: GPUBindGroup,
          x: number,
          y = 1,
        ) => {
          const pass = commandEncoder.beginComputePass();
          pass.setPipeline(pipeline);
          pass.setBindGroup(0, bindGroup);
          pass.dispatchWorkgroups(x, y);
          pass.end();
        };
        run(this.clearDensityPipeline!, this.densityBindGroup!, potWorkgroups);
        run(
          this.accumulateDensityPipeline!,
          this.accumulateDensityBindGroup!,
          workgroups,
        );
        run(
          this.decodeDensityPipeline!,
          this.decodeDensityBindGroup!,
          potWorkgroups,
        );
        for (let pass = 0; pass < (this.granulation >= 16 ? 3 : 1); pass++) {
          run(
            this.smoothDensityOnlyPipeline!,
            pass % 2 === 0
              ? this.smoothDensityOnlyBindGroup!
              : this.smoothDensityReverseBindGroup!,
            pot2D_X,
            pot2D_Y,
          );
        }
        if (refine) {
          this.dynamicFieldTimer = 0;
          commandEncoder.clearBuffer(this.relaxStateBuffer!);
          run(
            this.updatePenaltyPipeline!,
            this.penaltyBindGroup!,
            pot2D_X,
            pot2D_Y,
          );
          // WASM updates the EMA during InitializeAgents and again on the first
          // physics tick. Both observe the initial positions before integration.
          if (this.initialDensityPending) {
            run(
              this.updatePenaltyPipeline!,
              this.penaltyBindGroup!,
              pot2D_X,
              pot2D_Y,
            );
            this.initialDensityPending = false;
          }
          run(
            this.initRelaxPipeline!,
            this.initRelaxBindGroup!,
            pot2D_X,
            pot2D_Y,
          );
          commandEncoder.copyBufferToBuffer(
            this.relaxStateBuffer!,
            0,
            this.relaxStateStaging!,
            0,
            8,
          );
          this.device.queue.submit([commandEncoder.finish()]);
          await this.relaxStateStaging!.mapAsync(GPUMapMode.READ);
          const congested =
            new Uint32Array(this.relaxStateStaging!.getMappedRange())[0] !== 0;
          this.relaxStateStaging!.unmap();
          commandEncoder = this.device.createCommandEncoder();
          if (congested) {
            // Positive edge costs imply convergence in at most V-1 edges.
            // Check after batches of 32 pairs to avoid a readback on every pass.
            const maxPairs =
              Math.ceil((this.potCols * this.potRows - 1) / 2) + 1;
            for (let pair = 0; pair < maxPairs; pair += 32) {
              const batchPairs = Math.min(32, maxPairs - pair);
              for (let j = 0; j < batchPairs; j++) {
                commandEncoder.clearBuffer(this.relaxStateBuffer!, 4, 4);
                run(
                  this.relaxStepPipeline!,
                  this.relaxBindGroupA!,
                  pot2D_X,
                  pot2D_Y,
                );
                run(
                  this.relaxStepPipeline!,
                  this.relaxBindGroupB!,
                  pot2D_X,
                  pot2D_Y,
                );
              }
              commandEncoder.copyBufferToBuffer(
                this.relaxStateBuffer!,
                0,
                this.relaxStateStaging!,
                0,
                8,
              );
              this.device.queue.submit([commandEncoder.finish()]);
              await this.relaxStateStaging!.mapAsync(GPUMapMode.READ);
              const changed =
                new Uint32Array(this.relaxStateStaging!.getMappedRange())[1] !==
                0;
              this.relaxStateStaging!.unmap();
              commandEncoder = this.device.createCommandEncoder();
              if (!changed) break;
            }
          }
        }
      }

      // Pass 1: Clear spatial hash grid
      const pass1 = commandEncoder.beginComputePass();
      pass1.setPipeline(this.clearPipeline);
      pass1.setBindGroup(0, this.clearBindGroup);
      pass1.dispatchWorkgroups(gridWorkgroups);
      pass1.end();

      // Pass 2: Bin agents into grid cells
      const pass2 = commandEncoder.beginComputePass();
      pass2.setPipeline(this.binPipeline);
      pass2.setBindGroup(0, this.binBindGroup);
      pass2.dispatchWorkgroups(workgroups);
      pass2.end();

      // Pass 3: Step physics (forces, obstacles, social repulsion, integration)
      const pass3 = commandEncoder.beginComputePass();
      pass3.setPipeline(this.physicsPipeline);
      pass3.setBindGroup(0, this.bindGroup);
      pass3.dispatchWorkgroups(workgroups);
      pass3.end();
    }

    this.device.queue.submit([commandEncoder.finish()]);
    this.activeTick += ticks;
    this.inFlightBatches++;
    const completion = this.device.queue
      .onSubmittedWorkDone()
      .then(() => {
        if (this.inFlightWorkPromise === completion) {
          this.inFlightBatches = 0;
          this.inFlightWorkPromise = null;
        }
      })
      .catch(() => {
        if (this.inFlightWorkPromise === completion) {
          this.inFlightBatches = 0;
          this.inFlightWorkPromise = null;
        }
      });
    this.inFlightWorkPromise = completion;
    return this.activeTick;
  }

  async syncInFlight(maxAllowed = 1): Promise<void> {
    await this.tickWork;
    if (this.device && maxAllowed === 0) {
      // Configuration/reset also needs to wait for draws that consume agents.
      await this.device.queue.onSubmittedWorkDone();
      return;
    }
    if (
      !this.device ||
      this.inFlightBatches <= maxAllowed ||
      !this.inFlightWorkPromise
    ) {
      return;
    }
    try {
      await this.inFlightWorkPromise;
    } catch {
      // Ignored - device loss or queue error
    }
  }

  async capturePreview(): Promise<OwnedPreview> {
    await this.tickWork;
    if (
      !this.device ||
      !this.agentsBuffer ||
      !this.stagingBuffer ||
      !this.cpuValues
    ) {
      throw new Error("GPU simulation engine is not initialized.");
    }

    if (this.isCapturing) {
      return {
        version: protocolVersion,
        session: this.activeSession ?? "",
        sequence: this.dispatchSequence,
        runGeneration: this.activeRunGeneration,
        tick: this.activeTick,
        count: this.activeCount,
        values: this.cpuValues,
      };
    }

    this.isCapturing = true;
    try {
      const agentByteSize = 32;
      const totalBytes = this.activeCount * agentByteSize;

      const isKdeMode =
        this.currentRenderMode === "density" ||
        this.currentRenderMode === "heatmap";

      const copyEncoder = this.device.createCommandEncoder();

      if (isKdeMode && this.activeCount > 0) {
        this.ensureKdeGrid();
        this.updateKdeParams();

        // 1. Clear KDE deposit buffers
        const clearPass = copyEncoder.beginComputePass();
        clearPass.setPipeline(this.kdeClearPipeline!);
        clearPass.setBindGroup(0, this.kdeClearBindGroup!);
        clearPass.dispatchWorkgroups(
          Math.ceil((this.kdeWidth * this.kdeHeight) / 64),
        );
        clearPass.end();

        // 2. Deposit agent mass/speed
        const depositPass = copyEncoder.beginComputePass();
        depositPass.setPipeline(this.kdeDepositPipeline!);
        depositPass.setBindGroup(0, this.kdeDepositBindGroup!);
        depositPass.dispatchWorkgroups(Math.ceil(this.activeCount / 64));
        depositPass.end();

        // 3. Smooth continuous kernel pass
        const kernelPass = copyEncoder.beginComputePass();
        kernelPass.setPipeline(this.kdeSmoothKernelPipeline!);
        kernelPass.setBindGroup(0, this.kdeKernelBindGroup!);
        kernelPass.dispatchWorkgroups(
          Math.ceil(this.kdeWidth / 16),
          Math.ceil(this.kdeHeight / 16),
        );
        kernelPass.end();

        // Copy KDE output to staging buffer
        const kdeTotalBytes = this.kdeWidth * this.kdeHeight * 4;
        copyEncoder.copyBufferToBuffer(
          this.kdeDensityOutputBuffer!,
          0,
          this.kdeStagingBuffer!,
          0,
          kdeTotalBytes,
        );
      }

      copyEncoder.copyBufferToBuffer(
        this.agentsBuffer,
        0,
        this.stagingBuffer,
        0,
        totalBytes,
      );
      this.device.queue.submit([copyEncoder.finish()]);

      const mapPromises: Promise<void>[] = [
        this.stagingBuffer.mapAsync(GPUMapMode.READ),
      ];
      if (isKdeMode && this.activeCount > 0 && this.kdeStagingBuffer) {
        mapPromises.push(this.kdeStagingBuffer.mapAsync(GPUMapMode.READ));
      }
      await Promise.all(mapPromises);

      const mappedArray = new Float32Array(
        this.stagingBuffer.getMappedRange(0, totalBytes),
      );

      // Pack into 5 contiguous lanes for FrameTransport:
      // [posX (count), posY (count), vx (count), vy (count), radius (count)]
      const count = this.activeCount;
      const target = this.cpuValues;

      for (let i = 0; i < count; i++) {
        const srcOff = i * 8;
        target[i] = mappedArray[srcOff + 0];
        target[count + i] = mappedArray[srcOff + 1];
        target[2 * count + i] = mappedArray[srcOff + 2];
        target[3 * count + i] = mappedArray[srcOff + 3];
        const r = mappedArray[srcOff + 4];
        target[4 * count + i] = r;

        if (
          this.isMapScenario &&
          this.agentEvacuated &&
          !this.agentEvacuated[i] &&
          r <= 0.0
        ) {
          this.agentEvacuated[i] = 1;
          this.evacuatedCount++;
          if (this.activeMapAgents > 0) this.activeMapAgents--;
          let bestExit = 0;
          let minExitDist = Infinity;
          if (this.mapScenario) {
            for (let e = 0; e < this.mapScenario.exits.length; e++) {
              const ex = this.mapScenario.exits[e];
              const dx = target[i] - ex.x;
              const dy = target[count + i] - ex.y;
              const d = Math.sqrt(dx * dx + dy * dy);
              if (d < minExitDist) {
                minExitDist = d;
                bestExit = e;
              }
            }
          }
          this.evacuatedPerExit[bestExit]++;
          this.evacuationTimes.push(this.simulationTime);
          this.lastEvacuationTime = this.simulationTime;
        }
      }

      this.stagingBuffer.unmap();

      let densityResult: DensityGridData | undefined;
      if (isKdeMode && this.activeCount > 0 && this.kdeStagingBuffer) {
        const kdeTotalBytes = this.kdeWidth * this.kdeHeight * 4;
        const mappedKde = new Float32Array(
          this.kdeStagingBuffer.getMappedRange(0, kdeTotalBytes),
        );
        const densityData = new Float32Array(mappedKde);
        this.kdeStagingBuffer.unmap();

        let maxDensity = 0.0;
        for (let i = 0; i < densityData.length; i++) {
          if (densityData[i] > maxDensity) maxDensity = densityData[i];
        }

        densityResult = {
          width: this.kdeWidth,
          height: this.kdeHeight,
          data: densityData,
          maxDensity,
        };
      }

      return {
        version: protocolVersion,
        session: this.activeSession ?? "",
        sequence: this.dispatchSequence,
        runGeneration: this.activeRunGeneration,
        tick: this.activeTick,
        count,
        values: target,
        density: densityResult,
      };
    } finally {
      this.isCapturing = false;
    }
  }

  loadMapScenario(data: Uint8Array): void {
    const scenario = parseMapScenario(data);
    this.mapScenario = scenario;
    this.isMapScenario = true;
    this.worldWidth = scenario.worldWidth;
    this.worldHeight = scenario.worldHeight;
    this.mapCols = scenario.columns;
    this.mapRows = scenario.rows;
    this.mapCellSize = scenario.cellSize;
    this.potCols = scenario.columns;
    this.potRows = scenario.rows;
    this.potCellSize = scenario.cellSize;
    this.numExits = scenario.exits.length;
    this.totalPeople = scenario.totalPeople;

    this.evacuatedCount = 0;
    this.evacuatedPerExit = new Array(this.numExits).fill(0);
    this.evacuationTimes = [];
    this.lastEvacuationTime = 0;
    this.simulationTime = 0;

    if (this.device) {
      // 1. Exits buffer: vec4<f32>(x, y, radius, 0.0) per exit
      this.exitsBuffer?.destroy();
      const exitFloats = new Float32Array(Math.max(1, this.numExits) * 4);
      for (let i = 0; i < scenario.exits.length; i++) {
        exitFloats[i * 4 + 0] = scenario.exits[i].x;
        exitFloats[i * 4 + 1] = scenario.exits[i].y;
        exitFloats[i * 4 + 2] = scenario.exits[i].radius;
        exitFloats[i * 4 + 3] = 0.0;
      }
      this.exitsBuffer = this.device.createBuffer({
        size: Math.max(64, exitFloats.byteLength),
        usage: GPUBufferUsage.STORAGE | GPUBufferUsage.COPY_DST,
      });
      this.device.queue.writeBuffer(this.exitsBuffer, 0, exitFloats.buffer);

      // 2. Blocked raster buffer: u32 per cell
      this.mapBlockedBuffer?.destroy();
      const blockedU32 = new Uint32Array(scenario.columns * scenario.rows);
      for (let i = 0; i < scenario.blocked.length; i++) {
        blockedU32[i] = scenario.blocked[i];
      }
      this.mapBlockedBuffer = this.device.createBuffer({
        size: Math.max(64, blockedU32.byteLength),
        usage: GPUBufferUsage.STORAGE | GPUBufferUsage.COPY_DST,
      });
      this.device.queue.writeBuffer(
        this.mapBlockedBuffer,
        0,
        blockedU32.buffer,
      );

      // 3. Static potential field Dijkstra
      const staticField = buildMapPotentialField(scenario);
      const potCells = scenario.columns * scenario.rows;

      this.staticPotentialBuffer?.destroy();
      this.potentialBufferA?.destroy();
      this.potentialBufferB?.destroy();
      this.penaltyBuffer?.destroy();
      this.rawDensityBuffer?.destroy();
      this.smoothedDensityBuffer?.destroy();

      this.staticPotentialBuffer = this.device.createBuffer({
        size: potCells * 4,
        usage:
          GPUBufferUsage.STORAGE |
          GPUBufferUsage.COPY_SRC |
          GPUBufferUsage.COPY_DST,
      });
      this.potentialBufferA = this.device.createBuffer({
        size: potCells * 4,
        usage:
          GPUBufferUsage.STORAGE |
          GPUBufferUsage.COPY_DST |
          GPUBufferUsage.COPY_SRC,
      });
      this.potentialBufferB = this.device.createBuffer({
        size: potCells * 4,
        usage: GPUBufferUsage.STORAGE | GPUBufferUsage.COPY_DST,
      });
      this.penaltyBuffer = this.device.createBuffer({
        size: potCells * 4,
        usage: GPUBufferUsage.STORAGE | GPUBufferUsage.COPY_DST,
      });
      this.rawDensityBuffer = this.device.createBuffer({
        size: potCells * 4,
        usage: GPUBufferUsage.STORAGE | GPUBufferUsage.COPY_DST,
      });
      this.smoothedDensityBuffer = this.device.createBuffer({
        size: potCells * 4,
        usage: GPUBufferUsage.STORAGE | GPUBufferUsage.COPY_DST,
      });

      this.device.queue.writeBuffer(
        this.staticPotentialBuffer,
        0,
        staticField.buffer,
      );
      this.device.queue.writeBuffer(
        this.potentialBufferA,
        0,
        staticField.buffer,
      );
    }
  }

  evacuationStatus(): EvacuationStatus {
    const dots = this.activeCount;
    const peoplePerDot = Math.max(1, this.granulation);
    const evacuatedPeople = Math.min(
      this.totalPeople,
      this.evacuatedCount * peoplePerDot,
    );
    const perExit = this.evacuatedPerExit.map((count) => count * peoplePerDot);
    const finished = this.activeMapAgents === 0;
    const stalled =
      !finished &&
      (this.evacuatedCount === 0
        ? this.simulationTime > 3600
        : this.simulationTime - this.lastEvacuationTime > 600);

    const timeForFraction = (fraction: number): number | undefined => {
      const required = Math.max(1, Math.ceil(dots * fraction));
      return this.evacuatedCount >= required
        ? this.evacuationTimes[required - 1]
        : undefined;
    };

    return {
      isMap: this.isMapScenario,
      simulationSeconds: this.simulationTime,
      totalPeople: this.totalPeople,
      evacuatedPeople,
      remainingAgents: this.activeMapAgents,
      evacuatedPerExit: perExit,
      halfEvacuatedSeconds: timeForFraction(0.5),
      ninetyPercentEvacuatedSeconds: timeForFraction(0.9),
      lastEvacuationSeconds:
        this.evacuatedCount > 0
          ? this.evacuationTimes[this.evacuatedCount - 1]
          : undefined,
      finished,
      stalled,
    };
  }

  dispose(): void {
    this.agentRenderer?.dispose();
    this.telemetryBuffer?.destroy();
    this.telemetryStaging?.destroy();
    this.densityScratchBuffer?.destroy();
    this.seedBuffer?.destroy();
    this.protectedBuffer?.destroy();
    this.relaxStateBuffer?.destroy();
    this.relaxStateStaging?.destroy();
    this.agentsBuffer?.destroy();
    this.stagingBuffer?.destroy();
    this.cellCountsBuffer?.destroy();
    this.cellAgentsBuffer?.destroy();
    this.paramsBuffer?.destroy();
    this.rawDensityBuffer?.destroy();
    this.smoothedDensityBuffer?.destroy();
    this.penaltyBuffer?.destroy();
    this.blockedBuffer?.destroy();
    this.staticPotentialBuffer?.destroy();
    this.potentialBufferA?.destroy();
    this.potentialBufferB?.destroy();
    this.exitsBuffer?.destroy();
    this.mapBlockedBuffer?.destroy();
    this.kdeParamsBuffer?.destroy();
    this.kdeDepositMassBuffer?.destroy();
    this.kdeDepositSpeedBuffer?.destroy();
    this.kdeDensityOutputBuffer?.destroy();
    this.kdeStagingBuffer?.destroy();
    this.inFlightBatches = 0;
    this.inFlightWorkPromise = null;
    this.device?.destroy();
    this.device = undefined;
  }
}
