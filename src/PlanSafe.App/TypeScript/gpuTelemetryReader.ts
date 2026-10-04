/// <reference types="@webgpu/types" />

const telemetryShader = `
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
        }`;

export interface GpuTelemetryMoments {
  activeDots: number;
  totalSpeed: number;
  totalSpeedSquared: number;
  totalDensity: number;
  totalDensitySquared: number;
  peakDensity: number;
}

/** Owns telemetry reductions and their compact readback buffers. */
export class GpuTelemetryReader {
  private reductionPipeline?: GPUComputePipeline;
  private reductionBuffer?: GPUBuffer;
  private readbackBuffer?: GPUBuffer;
  private workgroupCount = 0;

  constructor(private readonly device: GPUDevice) {}

  async capture<TResult>(
    agents: GPUBuffer,
    parameters: GPUBuffer,
    agentCount: number,
    complete: (moments: GpuTelemetryMoments) => TResult,
  ): Promise<TResult> {
    this.reductionPipeline ??= this.device.createComputePipeline({
      layout: "auto",
      compute: {
        module: this.device.createShaderModule({
          code: telemetryShader,
        }),
        entryPoint: "main",
      },
    });
    const workgroupCount = Math.max(1, Math.ceil(agentCount / 64));
    if (workgroupCount !== this.workgroupCount) {
      this.reductionBuffer?.destroy();
      this.readbackBuffer?.destroy();
      this.workgroupCount = workgroupCount;
      this.reductionBuffer = this.device.createBuffer({
        size: workgroupCount * 32,
        usage: GPUBufferUsage.STORAGE | GPUBufferUsage.COPY_SRC,
      });
      this.readbackBuffer = this.device.createBuffer({
        size: workgroupCount * 32,
        usage: GPUBufferUsage.MAP_READ | GPUBufferUsage.COPY_DST,
      });
    }
    // SimParams.agentCount occupies bytes 16..31.
    const bindGroup = this.device.createBindGroup({
      layout: this.reductionPipeline.getBindGroupLayout(0),
      entries: [
        { binding: 0, resource: { buffer: agents } },
        { binding: 1, resource: { buffer: this.reductionBuffer! } },
        {
          binding: 2,
          resource: { buffer: parameters, offset: 0, size: 32 },
        },
      ],
    });
    const encoder = this.device.createCommandEncoder();
    const pass = encoder.beginComputePass();
    pass.setPipeline(this.reductionPipeline);
    pass.setBindGroup(0, bindGroup);
    pass.dispatchWorkgroups(workgroupCount);
    pass.end();
    encoder.copyBufferToBuffer(
      this.reductionBuffer!,
      0,
      this.readbackBuffer!,
      0,
      workgroupCount * 32,
    );
    this.device.queue.submit([encoder.finish()]);
    await this.readbackBuffer!.mapAsync(GPUMapMode.READ);
    const values = new Float32Array(this.readbackBuffer!.getMappedRange());
    let activeDots = 0,
      totalSpeed = 0,
      totalSpeedSquared = 0,
      totalDensity = 0,
      totalDensitySquared = 0,
      peakDensity = 0;
    for (let i = 0; i < workgroupCount; i++) {
      const offset = i * 8;
      activeDots += values[offset];
      totalSpeed += values[offset + 1];
      totalSpeedSquared += values[offset + 2];
      totalDensity += values[offset + 3];
      totalDensitySquared += values[offset + 4];
      peakDensity = Math.max(peakDensity, values[offset + 5]);
    }
    this.readbackBuffer!.unmap();
    // Complete synchronously after readback so simulation metadata is observed
    // at the same point as the original telemetry implementation.
    return complete({
      activeDots,
      totalSpeed,
      totalSpeedSquared,
      totalDensity,
      totalDensitySquared,
      peakDensity,
    });
  }

  dispose(): void {
    this.reductionBuffer?.destroy();
    this.readbackBuffer?.destroy();
  }
}
