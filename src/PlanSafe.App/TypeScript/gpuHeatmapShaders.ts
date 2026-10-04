export const kdeClearShader = `
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

export const kdeDepositShader = `
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

export const kdeSmoothKernelShader = `
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
