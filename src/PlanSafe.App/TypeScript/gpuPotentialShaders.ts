export const initRelaxShader = `
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

export const relaxStepShader = `
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
