/// <reference types="@webgpu/types" />

const shader = `
struct Agent {
  pos: vec2<f32>, vel: vec2<f32>, radius: f32, desiredSpeed: f32,
  flags: u32, density: u32,
};
struct Camera {
  transform: vec4<f32>, viewport: vec4<f32>, style: vec4<f32>,
};
@group(0) @binding(0) var<storage, read> agents: array<Agent>;
@group(0) @binding(1) var<uniform> camera: Camera;
struct Vertex {
  @builtin(position) position: vec4<f32>,
  @location(0) uv: vec2<f32>,
  @location(1) color: vec4<f32>,
  @location(2) @interpolate(flat) circle: u32,
  @location(3) @interpolate(flat) radius: f32,
};
fn corner(index: u32) -> vec2<f32> {
  var corners = array<vec2<f32>, 6>(
    vec2(-1., -1.), vec2(1., -1.), vec2(-1., 1.),
    vec2(-1., 1.), vec2(1., -1.), vec2(1., 1.));
  return corners[index];
}
fn clip(pixel: vec2<f32>) -> vec4<f32> {
  return vec4(pixel.x / camera.viewport.x * 2. - 1.,
    1. - pixel.y / camera.viewport.y * 2., 0., 1.);
}
@vertex fn agentVertex(@builtin(vertex_index) index: u32,
  @builtin(instance_index) instance: u32) -> Vertex {
  let agent = agents[instance];
  let uv = corner(index);
  let radius = max(agent.radius * min(camera.transform.x, camera.transform.y), camera.viewport.z);
  let center = agent.pos * camera.transform.xy + camera.transform.zw;
  let bucket = min(31., floor(length(agent.vel) * (32. / 2.2))) / 31.;
  var color: vec3<f32>;
  if (bucket < 0.5) {
    color = vec3(255., 230. * bucket * 2., 85. * (1. - bucket * 2.)) / 255.;
  } else {
    let t = (bucket - 0.5) * 2.;
    color = vec3(255. * (1. - t), 230. + 10. * t, 255. * t) / 255.;
  }
  var output: Vertex;
  output.position = clip(center + uv * (radius + 1.));
  if (agent.flags == 0u || agent.radius <= 0.001) { output.position = vec4(2., 2., 0., 1.); }
  output.uv = uv * (radius + 1.) / radius;
  output.color = vec4(color, 1.);
  output.circle = 1u;
  output.radius = radius;
  return output;
}
@vertex fn whiskerVertex(@builtin(vertex_index) index: u32,
  @builtin(instance_index) instance: u32) -> Vertex {
  let agent = agents[instance];
  let uv = corner(index);
  let speed = length(agent.vel);
  let direction = agent.vel / max(speed, 0.001);
  let normal = vec2(-direction.y, direction.x);
  let center = agent.pos * camera.transform.xy + camera.transform.zw;
  let extent = camera.style.x * min(camera.transform.x, camera.transform.y);
  var output: Vertex;
  output.position = clip(center + direction * ((uv.x + 1.) * 0.5 * extent) + normal * uv.y * 0.5);
  if (agent.flags == 0u || speed * speed <= 0.001) { output.position = vec4(2., 2., 0., 1.); }
  output.uv = uv;
  output.color = vec4(1., 1., 1., 0.35);
  output.circle = 0u;
  output.radius = 1.;
  return output;
}
@fragment fn fragment(input: Vertex) -> @location(0) vec4<f32> {
  var color = input.color;
  let distance = length(input.uv);
  let aa = max(fwidth(distance), 0.001);
  if (input.circle == 1u) {
    let alpha = 1. - smoothstep(1. - aa, 1. + aa, distance);
    if (alpha <= 0.) { discard; }
    if (camera.viewport.w > 1.) {
      let outline = smoothstep(1. - 1.2 / input.radius - aa, 1. - 1.2 / input.radius + aa, distance);
      color = vec4(mix(color.rgb, vec3(1.), outline * 0.45), alpha);
    } else { color.a = alpha; }
  }
  return vec4(color.rgb * color.a, color.a);
}
`;

/** Renders directly from the physics storage buffer, without a CPU agent copy. */
export class GpuAgentRenderer {
  readonly canvas = document.createElement("canvas");
  private readonly context: GPUCanvasContext;
  private readonly camera: GPUBuffer;
  private readonly agentsPipeline: GPURenderPipeline;
  private readonly whiskersPipeline: GPURenderPipeline;
  private readonly layout: GPUBindGroupLayout;
  private boundAgents?: GPUBuffer;
  private bindGroup?: GPUBindGroup;

  constructor(private readonly device: GPUDevice) {
    this.context = this.canvas.getContext("webgpu")!;
    const format = navigator.gpu.getPreferredCanvasFormat();
    this.context.configure({ device, format, alphaMode: "premultiplied" });
    this.camera = device.createBuffer({
      size: 48,
      usage: GPUBufferUsage.UNIFORM | GPUBufferUsage.COPY_DST,
    });
    this.layout = device.createBindGroupLayout({
      entries: [
        {
          binding: 0,
          visibility: GPUShaderStage.VERTEX,
          buffer: { type: "read-only-storage" },
        },
        {
          binding: 1,
          visibility: GPUShaderStage.VERTEX | GPUShaderStage.FRAGMENT,
          buffer: { type: "uniform" },
        },
      ],
    });
    const module = device.createShaderModule({ code: shader });
    const layout = device.createPipelineLayout({
      bindGroupLayouts: [this.layout],
    });
    const pipeline = (entryPoint: string) =>
      device.createRenderPipeline({
        layout,
        vertex: { module, entryPoint },
        fragment: {
          module,
          entryPoint: "fragment",
          targets: [
            {
              format,
              blend: {
                color: { srcFactor: "one", dstFactor: "one-minus-src-alpha" },
                alpha: { srcFactor: "one", dstFactor: "one-minus-src-alpha" },
              },
            },
          ],
        },
        primitive: { topology: "triangle-list" },
      });
    this.agentsPipeline = pipeline("agentVertex");
    this.whiskersPipeline = pipeline("whiskerVertex");
  }

  draw(
    agents: GPUBuffer,
    count: number,
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
  ): HTMLCanvasElement {
    if (this.canvas.width !== width) this.canvas.width = width;
    if (this.canvas.height !== height) this.canvas.height = height;
    if (this.boundAgents !== agents) {
      this.boundAgents = agents;
      this.bindGroup = this.device.createBindGroup({
        layout: this.layout,
        entries: [
          { binding: 0, resource: { buffer: agents } },
          { binding: 1, resource: { buffer: this.camera } },
        ],
      });
    }
    this.device.queue.writeBuffer(
      this.camera,
      0,
      new Float32Array([
        scaleX,
        scaleY,
        offsetX,
        offsetY,
        width,
        height,
        minRadius,
        granulation,
        whiskerLength,
        0,
        0,
        0,
      ]),
    );
    const encoder = this.device.createCommandEncoder();
    const pass = encoder.beginRenderPass({
      colorAttachments: [
        {
          view: this.context.getCurrentTexture().createView(),
          loadOp: "clear",
          storeOp: "store",
          clearValue: { r: 0, g: 0, b: 0, a: 0 },
        },
      ],
    });
    pass.setBindGroup(0, this.bindGroup!);
    if (showWhiskers) {
      pass.setPipeline(this.whiskersPipeline);
      pass.draw(6, count);
    }
    pass.setPipeline(this.agentsPipeline);
    pass.draw(6, count);
    pass.end();
    this.device.queue.submit([encoder.finish()]);
    return this.canvas;
  }

  dispose(): void {
    this.context.unconfigure();
    this.camera.destroy();
  }
}
