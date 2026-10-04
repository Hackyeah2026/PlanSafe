export const clearDensityShader = `
@group(0) @binding(0) var<storage, read_write> rawDensity: array<atomic<u32>>;

@compute @workgroup_size(64)
fn main(@builtin(global_invocation_id) global_id: vec3<u32>) {
    let idx = global_id.x;
    if (idx < arrayLength(&rawDensity)) {
        atomicStore(&rawDensity[idx], 0u);
    }
}
`;

export const accumulateDensityShader = `
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

export const decodeDensityShader = `
@group(0) @binding(0) var<storage, read_write> rawDensity: array<atomic<u32>>;
@group(0) @binding(1) var<storage, read_write> density: array<f32>;
@compute @workgroup_size(64)
fn main(@builtin(global_invocation_id) id: vec3<u32>) {
    if (id.x < arrayLength(&density)) { density[id.x] = bitcast<f32>(atomicLoad(&rawDensity[id.x])); }
}
`;

export const smoothDensityOnlyShader = `
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

export const updatePenaltyShader = `
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
