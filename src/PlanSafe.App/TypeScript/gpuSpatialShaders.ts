export const clearGridShader = `
@group(0) @binding(0) var<storage, read_write> cellCounts: array<atomic<u32>>;

@compute @workgroup_size(64)
fn main(@builtin(global_invocation_id) global_id: vec3<u32>) {
    let idx = global_id.x;
    if (idx < arrayLength(&cellCounts)) {
        atomicStore(&cellCounts[idx], 0u);
    }
}
`;

export const binAgentsShader = `
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
