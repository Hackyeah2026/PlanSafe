import { ICrowdRenderer, RenderOptions } from "./renderer.js";

// --- GLSL ES 3.00 Shaders ---

const agentVsSource = `#version 300 es
precision highp float;

layout(location = 0) in vec2 a_quad;      // Unit quad coords [-1, 1]
layout(location = 1) in vec2 a_worldPos;  // Agent world position in meters
layout(location = 2) in vec2 a_velocity;  // Velocity (vx, vy)
layout(location = 3) in vec2 a_heading;   // Heading direction unit vector (hx, hy)
layout(location = 4) in float a_speed;    // Empirical speed in m/s
layout(location = 5) in float a_radius;   // Radius in meters
layout(location = 6) in float a_active;   // 1.0 = active, 0.0 = evacuated

uniform vec2 u_scale;
uniform vec2 u_canvasSize;

out vec2 v_quad;
out float v_speed;
out vec2 v_heading;
out float v_pixelRadius;
out float v_quadRadius;

void main() {
    if (a_active <= 0.0 || a_radius <= 0.0) {
        gl_Position = vec4(2.0, 2.0, 2.0, 1.0);
        return;
    }

    v_quad = a_quad;
    v_speed = a_speed;
    v_heading = a_heading;

    float avgScale = 0.5 * (u_scale.x + u_scale.y);
    float pixelRadius = max(2.5, a_radius * avgScale);
    float quadRadius = pixelRadius * 1.6 + 2.0;

    v_pixelRadius = pixelRadius;
    v_quadRadius = quadRadius;

    vec2 screenCenter = a_worldPos * u_scale;
    vec2 screenPos = screenCenter + a_quad * quadRadius;

    vec2 ndc = vec2(
        (screenPos.x / u_canvasSize.x) * 2.0 - 1.0,
        1.0 - (screenPos.y / u_canvasSize.y) * 2.0
    );
    gl_Position = vec4(ndc, 0.0, 1.0);
}
`;

const agentFsSource = `#version 300 es
precision highp float;

in vec2 v_quad;
in float v_speed;
in vec2 v_heading;
in float v_pixelRadius;
in float v_quadRadius;

out vec4 fragColor;

vec3 getSpeedColor(float spd) {
    if (spd >= 1.05) {
        return vec3(0.133, 0.773, 0.369); // #22c55e Emerald green (Free flow)
    } else if (spd >= 0.6) {
        return vec3(0.918, 0.702, 0.031); // #eab308 Amber (Moderate slowing)
    } else if (spd >= 0.2) {
        return vec3(0.976, 0.451, 0.086); // #f97316 Orange (Heavy bottleneck)
    } else {
        return vec3(0.937, 0.267, 0.267); // #ef4444 Red (Jam / Stopped)
    }
}

float distToSegment(vec2 p, vec2 a, vec2 b) {
    vec2 pa = p - a;
    vec2 ba = b - a;
    float lenSq = dot(ba, ba);
    if (lenSq < 0.0001) return length(pa);
    float h = clamp(dot(pa, ba) / lenSq, 0.0, 1.0);
    return length(pa - ba * h);
}

void main() {
    vec2 p = v_quad * v_quadRadius;
    float dist = length(p);

    // 1. Antialiased circular body
    float circleAlpha = 1.0 - smoothstep(v_pixelRadius - 0.75, v_pixelRadius + 0.25, dist);

    // Subtle dark border at perimeter of agent body for high contrast in dense crowds
    float borderFactor = smoothstep(v_pixelRadius - 1.2, v_pixelRadius, dist);
    vec3 bodyCol = mix(getSpeedColor(v_speed), vec3(0.08, 0.08, 0.12), borderFactor * 0.45);

    // 2. Directional heading needle pointing strictly along path of least resistance to exit
    float lineAlpha = 0.0;
    float hLen = length(v_heading);
    if (hLen > 0.05) {
        vec2 hDir = v_heading / hLen;
        vec2 lineEnd = hDir * (v_pixelRadius * 1.5);
        float lineDist = distToSegment(p, vec2(0.0), lineEnd);
        lineAlpha = 1.0 - smoothstep(0.4, 1.1, lineDist);
    }

    vec4 circlePremul = vec4(bodyCol * circleAlpha, circleAlpha);
    vec4 linePremul = vec4(vec3(1.0) * (lineAlpha * 0.85), lineAlpha * 0.85);

    // Blend needle over body with premultiplied alpha: linePremul + circlePremul * (1 - linePremul.a)
    vec4 finalColor = linePremul + circlePremul * (1.0 - linePremul.a);

    if (finalColor.a <= 0.002) {
        discard;
    }

    fragColor = finalColor;
}
`;

const flatVsSource = `#version 300 es
precision highp float;

layout(location = 0) in vec2 a_position;

uniform vec2 u_canvasSize;

void main() {
    vec2 ndc = vec2(
        (a_position.x / u_canvasSize.x) * 2.0 - 1.0,
        1.0 - (a_position.y / u_canvasSize.y) * 2.0
    );
    gl_Position = vec4(ndc, 0.0, 1.0);
}
`;

const flatFsSource = `#version 300 es
precision highp float;

uniform vec4 u_color;

out vec4 fragColor;

void main() {
    fragColor = vec4(u_color.rgb * u_color.a, u_color.a);
}
`;

const heatmapVsSource = `#version 300 es
precision highp float;

layout(location = 0) in vec2 a_position;
layout(location = 1) in vec2 a_texCoord;

uniform vec2 u_canvasSize;

out vec2 v_texCoord;

void main() {
    v_texCoord = a_texCoord;
    vec2 ndc = vec2(
        (a_position.x / u_canvasSize.x) * 2.0 - 1.0,
        1.0 - (a_position.y / u_canvasSize.y) * 2.0
    );
    gl_Position = vec4(ndc, 0.0, 1.0);
}
`;

const heatmapFsSource = `#version 300 es
precision highp float;

in vec2 v_texCoord;

uniform sampler2D u_texture;
uniform vec2 u_texSize;
uniform float u_opacity;

out vec4 fragColor;

// Smoothstep texture sampling (Inigo Quilez C2 continuous quintic)
vec4 sampleSmooth(vec2 uv) {
    vec2 coord = uv * u_texSize - 0.5;
    vec2 i = floor(coord);
    vec2 f = fract(coord);
    vec2 s = f * f * f * (f * (f * 6.0 - 15.0) + 10.0);
    vec2 smoothUv = (i + 0.5 + s) / u_texSize;
    return texture(u_texture, smoothUv);
}

void main() {
    vec4 col = sampleSmooth(v_texCoord);
    float finalAlpha = col.a * u_opacity;
    fragColor = vec4(col.rgb * finalAlpha, finalAlpha);
}
`;

// --- Shader & Resource Helpers ---

function compileShader(
  gl: WebGL2RenderingContext,
  type: number,
  source: string,
): WebGLShader {
  const shader = gl.createShader(type);
  if (!shader) throw new Error("Failed to allocate shader.");
  gl.shaderSource(shader, source);
  gl.compileShader(shader);
  if (!gl.getShaderParameter(shader, gl.COMPILE_STATUS)) {
    const info = gl.getShaderInfoLog(shader) ?? "Unknown compile error";
    gl.deleteShader(shader);
    throw new Error(`Shader compile error: ${info}`);
  }
  return shader;
}

function createProgram(
  gl: WebGL2RenderingContext,
  vsSource: string,
  fsSource: string,
): WebGLProgram {
  const vs = compileShader(gl, gl.VERTEX_SHADER, vsSource);
  const fs = compileShader(gl, gl.FRAGMENT_SHADER, fsSource);
  const prog = gl.createProgram();
  if (!prog) throw new Error("Failed to allocate program.");
  gl.attachShader(prog, vs);
  gl.attachShader(prog, fs);
  gl.linkProgram(prog);
  if (!gl.getProgramParameter(prog, gl.LINK_STATUS)) {
    const info = gl.getProgramInfoLog(prog) ?? "Unknown link error";
    gl.deleteProgram(prog);
    throw new Error(`Program link error: ${info}`);
  }
  gl.deleteShader(vs);
  gl.deleteShader(fs);
  return prog;
}

function pushRectTriangles(
  arr: number[],
  x: number,
  y: number,
  w: number,
  h: number,
): void {
  const x2 = x + w;
  const y2 = y + h;
  arr.push(x, y, x2, y, x, y2, x, y2, x2, y, x2, y2);
}

function pushStrokeRectTriangles(
  arr: number[],
  x: number,
  y: number,
  w: number,
  h: number,
  t: number,
): void {
  // Top
  pushRectTriangles(arr, x, y, w, t);
  // Bottom
  pushRectTriangles(arr, x, y + h - t, w, t);
  // Left
  pushRectTriangles(arr, x, y + t, t, h - 2 * t);
  // Right
  pushRectTriangles(arr, x + w - t, y + t, t, h - 2 * t);
}

export class CrowdWebGLRenderer implements ICrowdRenderer {
  private canvas: HTMLCanvasElement;
  private gl: WebGL2RenderingContext;

  private worldWidth: number = 30;
  private worldHeight: number = 16;
  private cellSize: number = 0.5;
  private cols: number = 60;
  private rows: number = 32;
  private cells: Uint8Array = new Uint8Array(0);

  private densityBuffer: Float32Array | null = null;
  private gradientXBuffer: Float32Array | null = null;
  private gradientYBuffer: Float32Array | null = null;

  // Agent Program & Buffers
  private agentProgram: WebGLProgram;
  private agentUniformScale: WebGLUniformLocation | null;
  private agentUniformCanvasSize: WebGLUniformLocation | null;
  private agentVao: WebGLVertexArrayObject;
  private quadVbo: WebGLBuffer;
  private instanceVbo: WebGLBuffer;
  private instanceData: Float32Array;

  // Flat Program & Buffers
  private flatProgram: WebGLProgram;
  private flatUniformCanvasSize: WebGLUniformLocation | null;
  private flatUniformColor: WebGLUniformLocation | null;
  private flatVao: WebGLVertexArrayObject;
  private flatVbo: WebGLBuffer;
  private dynamicBufferFloats: Float32Array = new Float32Array(8192);

  // Heatmap Program & Buffers
  private heatmapProgram: WebGLProgram;
  private heatmapUniformCanvasSize: WebGLUniformLocation | null;
  private heatmapUniformTexSize: WebGLUniformLocation | null;
  private heatmapUniformOpacity: WebGLUniformLocation | null;
  private heatmapUniformTexture: WebGLUniformLocation | null;
  private heatmapVao: WebGLVertexArrayObject;
  private heatmapVbo: WebGLBuffer;
  private heatmapTexture: WebGLTexture;
  private heatmapPixelData: Uint8Array = new Uint8Array(0);

  // Cached Geometry for Static Grid Features
  private gridDirty: boolean = true;
  private cachedGridLines: number[] = [];
  private cachedObstacleFills: number[] = [];
  private cachedObstacleBorders: number[] = [];
  private cachedExitFills: number[] = [];
  private cachedExitBorders: number[] = [];
  private cachedSpawnFills: number[] = [];
  private cachedSpawnBorders: number[] = [];
  private lastCanvasWidth: number = 0;
  private lastCanvasHeight: number = 0;

  constructor(canvas: HTMLCanvasElement) {
    this.canvas = canvas;
    const gl = canvas.getContext("webgl2", {
      alpha: false,
      antialias: true,
      powerPreference: "high-performance",
    });

    if (!gl) {
      throw new Error("WebGL 2.0 is not supported on this browser/device.");
    }
    this.gl = gl;

    // 1. Configure Alpha Blending with premultiplied alpha
    gl.enable(gl.BLEND);
    gl.blendFunc(gl.ONE, gl.ONE_MINUS_SRC_ALPHA);
    gl.disable(gl.DEPTH_TEST);
    gl.pixelStorei(gl.UNPACK_PREMULTIPLY_ALPHA_WEBGL, true);

    // 2. Agent Program & Hardware Instancing Setup
    this.agentProgram = createProgram(gl, agentVsSource, agentFsSource);
    this.agentUniformScale = gl.getUniformLocation(
      this.agentProgram,
      "u_scale",
    );
    this.agentUniformCanvasSize = gl.getUniformLocation(
      this.agentProgram,
      "u_canvasSize",
    );

    const agentVao = gl.createVertexArray();
    if (!agentVao) throw new Error("Failed to create agent VAO");
    this.agentVao = agentVao;
    gl.bindVertexArray(this.agentVao);

    // Quad geometry (Triangle Strip [-1,-1] to [1,1])
    const quadVbo = gl.createBuffer();
    if (!quadVbo) throw new Error("Failed to create quad VBO");
    this.quadVbo = quadVbo;
    gl.bindBuffer(gl.ARRAY_BUFFER, this.quadVbo);
    const quadCoords = new Float32Array([
      -1.0, -1.0, 1.0, -1.0, -1.0, 1.0, 1.0, 1.0,
    ]);
    gl.bufferData(gl.ARRAY_BUFFER, quadCoords, gl.STATIC_DRAW);
    gl.enableVertexAttribArray(0);
    gl.vertexAttribPointer(0, 2, gl.FLOAT, false, 8, 0);
    gl.vertexAttribDivisor(0, 0); // Per-vertex

    // Instance VBO (Stride: 9 floats = 36 bytes)
    const instanceVbo = gl.createBuffer();
    if (!instanceVbo) throw new Error("Failed to create instance VBO");
    this.instanceVbo = instanceVbo;
    gl.bindBuffer(gl.ARRAY_BUFFER, this.instanceVbo);
    const initialInstanceCap = 5000 * 9 * 4; // Up to 5000 agents initial capacity
    gl.bufferData(gl.ARRAY_BUFFER, initialInstanceCap, gl.DYNAMIC_DRAW);
    this.instanceData = new Float32Array(5000 * 9);

    const stride = 9 * 4; // 36 bytes
    // Attr 1: a_worldPos (vec2, offset 0)
    gl.enableVertexAttribArray(1);
    gl.vertexAttribPointer(1, 2, gl.FLOAT, false, stride, 0);
    gl.vertexAttribDivisor(1, 1);

    // Attr 2: a_velocity (vec2, offset 8)
    gl.enableVertexAttribArray(2);
    gl.vertexAttribPointer(2, 2, gl.FLOAT, false, stride, 8);
    gl.vertexAttribDivisor(2, 1);

    // Attr 3: a_heading (vec2, offset 16)
    gl.enableVertexAttribArray(3);
    gl.vertexAttribPointer(3, 2, gl.FLOAT, false, stride, 16);
    gl.vertexAttribDivisor(3, 1);

    // Attr 4: a_speed (float, offset 24)
    gl.enableVertexAttribArray(4);
    gl.vertexAttribPointer(4, 1, gl.FLOAT, false, stride, 24);
    gl.vertexAttribDivisor(4, 1);

    // Attr 5: a_radius (float, offset 28)
    gl.enableVertexAttribArray(5);
    gl.vertexAttribPointer(5, 1, gl.FLOAT, false, stride, 28);
    gl.vertexAttribDivisor(5, 1);

    // Attr 6: a_active (float, offset 32)
    gl.enableVertexAttribArray(6);
    gl.vertexAttribPointer(6, 1, gl.FLOAT, false, stride, 32);
    gl.vertexAttribDivisor(6, 1);

    // 3. Flat Geometry Program Setup
    this.flatProgram = createProgram(gl, flatVsSource, flatFsSource);
    this.flatUniformCanvasSize = gl.getUniformLocation(
      this.flatProgram,
      "u_canvasSize",
    );
    this.flatUniformColor = gl.getUniformLocation(this.flatProgram, "u_color");

    const flatVao = gl.createVertexArray();
    if (!flatVao) throw new Error("Failed to create flat VAO");
    this.flatVao = flatVao;
    gl.bindVertexArray(this.flatVao);
    const flatVbo = gl.createBuffer();
    if (!flatVbo) throw new Error("Failed to create flat VBO");
    this.flatVbo = flatVbo;
    gl.bindBuffer(gl.ARRAY_BUFFER, this.flatVbo);
    gl.enableVertexAttribArray(0);
    gl.vertexAttribPointer(0, 2, gl.FLOAT, false, 8, 0);

    // 4. Heatmap Program & Texture Setup
    this.heatmapProgram = createProgram(gl, heatmapVsSource, heatmapFsSource);
    this.heatmapUniformCanvasSize = gl.getUniformLocation(
      this.heatmapProgram,
      "u_canvasSize",
    );
    this.heatmapUniformTexSize = gl.getUniformLocation(
      this.heatmapProgram,
      "u_texSize",
    );
    this.heatmapUniformOpacity = gl.getUniformLocation(
      this.heatmapProgram,
      "u_opacity",
    );
    this.heatmapUniformTexture = gl.getUniformLocation(
      this.heatmapProgram,
      "u_texture",
    );

    const heatmapVao = gl.createVertexArray();
    if (!heatmapVao) throw new Error("Failed to create heatmap VAO");
    this.heatmapVao = heatmapVao;
    gl.bindVertexArray(this.heatmapVao);
    const heatmapVbo = gl.createBuffer();
    if (!heatmapVbo) throw new Error("Failed to create heatmap VBO");
    this.heatmapVbo = heatmapVbo;
    gl.bindBuffer(gl.ARRAY_BUFFER, this.heatmapVbo);
    gl.enableVertexAttribArray(0);
    gl.vertexAttribPointer(0, 2, gl.FLOAT, false, 16, 0);
    gl.enableVertexAttribArray(1);
    gl.vertexAttribPointer(1, 2, gl.FLOAT, false, 16, 8);

    const heatmapTexture = gl.createTexture();
    if (!heatmapTexture) throw new Error("Failed to create heatmap texture");
    this.heatmapTexture = heatmapTexture;
    gl.bindTexture(gl.TEXTURE_2D, this.heatmapTexture);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.LINEAR);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.LINEAR);

    gl.bindVertexArray(null);
  }

  public setGrid(
    width: number,
    height: number,
    cellSize: number,
    cols: number,
    rows: number,
    cells: Uint8Array,
  ): void {
    this.worldWidth = width;
    this.worldHeight = height;
    this.cellSize = cellSize;
    this.cols = cols;
    this.rows = rows;
    this.cells = cells;
    this.gridDirty = true;

    if (this.heatmapPixelData.length !== cols * rows * 4) {
      this.heatmapPixelData = new Uint8Array(cols * rows * 4);
    }
  }

  public setFieldBuffers(
    density: Float32Array,
    gradX: Float32Array,
    gradY: Float32Array,
  ): void {
    this.densityBuffer = density;
    this.gradientXBuffer = gradX;
    this.gradientYBuffer = gradY;
  }

  public render(
    agentCount: number,
    posX: Float32Array,
    posY: Float32Array,
    velX: Float32Array,
    velY: Float32Array,
    speed: Float32Array,
    headX: Float32Array,
    headY: Float32Array,
    active: Uint8Array,
    options: RenderOptions,
  ): void {
    const gl = this.gl;
    const cw = this.canvas.width;
    const ch = this.canvas.height;

    gl.viewport(0, 0, cw, ch);

    // 1. Clear background: #0f172a (Deep Slate)
    gl.clearColor(15 / 255, 23 / 255, 42 / 255, 1.0);
    gl.clear(gl.COLOR_BUFFER_BIT);

    const scaleX = cw / this.worldWidth;
    const scaleY = ch / this.worldHeight;

    // Check if cached grid geometry needs rebuilding
    if (
      this.gridDirty ||
      cw !== this.lastCanvasWidth ||
      ch !== this.lastCanvasHeight
    ) {
      this.rebuildGridGeometry(scaleX, scaleY, cw, ch);
      this.lastCanvasWidth = cw;
      this.lastCanvasHeight = ch;
      this.gridDirty = false;
    }

    // 2. Draw Walkable Grid Lines
    this.renderFlatLines(
      this.cachedGridLines,
      30 / 255,
      41 / 255,
      59 / 255,
      0.75,
      cw,
      ch,
    );

    // 3. Optional Density Heatmap Overlay
    if (options.showDensityHeatmap && this.densityBuffer) {
      this.renderDensityHeatmap(cw, ch);
    }

    // 4. Optional Flow Vectors Overlay
    if (options.showFlowField && this.gradientXBuffer && this.gradientYBuffer) {
      this.renderFlowVectors(scaleX, scaleY, cw, ch);
    }

    // 5. Draw Obstacles, Exits, and Spawn Zones
    this.renderGridFeatures(options.showSpawnZones, cw, ch);

    // 6. Draw Agents via Hardware Instancing
    this.renderAgents(
      agentCount,
      posX,
      posY,
      velX,
      velY,
      speed,
      headX,
      headY,
      active,
      scaleX,
      scaleY,
      options.agentRadius,
      cw,
      ch,
    );
  }

  private rebuildGridGeometry(
    scaleX: number,
    scaleY: number,
    cw: number,
    ch: number,
  ): void {
    const cellW = this.cellSize * scaleX;
    const cellH = this.cellSize * scaleY;

    // Grid Lines
    this.cachedGridLines = [];
    for (let c = 0; c <= this.cols; c += 2) {
      const x = c * cellW;
      this.cachedGridLines.push(x, 0, x, ch);
    }
    for (let r = 0; r <= this.rows; r += 2) {
      const y = r * cellH;
      this.cachedGridLines.push(0, y, cw, y);
    }

    // Obstacles, Exits, and Spawn Zones
    this.cachedObstacleFills = [];
    this.cachedObstacleBorders = [];
    this.cachedExitFills = [];
    this.cachedExitBorders = [];
    this.cachedSpawnFills = [];
    this.cachedSpawnBorders = [];

    for (let r = 0; r < this.rows; r++) {
      for (let c = 0; c < this.cols; c++) {
        const type = this.cells[r * this.cols + c];
        if (type === 0) continue;
        const x = c * cellW;
        const y = r * cellH;

        if (type === 1) {
          // Obstacle: solid pillar / wall
          pushRectTriangles(
            this.cachedObstacleFills,
            x,
            y,
            cellW + 0.5,
            cellH + 0.5,
          );
          pushStrokeRectTriangles(
            this.cachedObstacleBorders,
            x,
            y,
            cellW,
            cellH,
            1.0,
          );
        } else if (type === 2) {
          // Exit: Emerald zone
          pushRectTriangles(this.cachedExitFills, x, y, cellW, cellH);
          pushStrokeRectTriangles(
            this.cachedExitBorders,
            x,
            y,
            cellW,
            cellH,
            1.5,
          );
        } else if (type === 3) {
          // Spawn zone: Subtle cyan outline
          pushRectTriangles(this.cachedSpawnFills, x, y, cellW, cellH);
          pushStrokeRectTriangles(
            this.cachedSpawnBorders,
            x,
            y,
            cellW,
            cellH,
            1.0,
          );
        }
      }
    }
  }

  private renderGridFeatures(showSpawn: boolean, cw: number, ch: number): void {
    // Obstacles: #334155 fill (rgb: 51, 65, 85), #475569 border (rgb: 71, 85, 105)
    this.renderFlatTriangles(
      this.cachedObstacleFills,
      51 / 255,
      65 / 255,
      85 / 255,
      1.0,
      cw,
      ch,
    );
    this.renderFlatTriangles(
      this.cachedObstacleBorders,
      71 / 255,
      85 / 255,
      105 / 255,
      1.0,
      cw,
      ch,
    );

    // Exits: rgba(16, 185, 129, 0.4) fill, #10b981 border (rgb: 16, 185, 129)
    this.renderFlatTriangles(
      this.cachedExitFills,
      16 / 255,
      185 / 255,
      129 / 255,
      0.4,
      cw,
      ch,
    );
    this.renderFlatTriangles(
      this.cachedExitBorders,
      16 / 255,
      185 / 255,
      129 / 255,
      1.0,
      cw,
      ch,
    );

    // Spawn zones: rgba(56, 189, 248, 0.12) fill, rgba(56, 189, 248, 0.4) border
    if (showSpawn) {
      this.renderFlatTriangles(
        this.cachedSpawnFills,
        56 / 255,
        189 / 255,
        248 / 255,
        0.12,
        cw,
        ch,
      );
      this.renderFlatTriangles(
        this.cachedSpawnBorders,
        56 / 255,
        189 / 255,
        248 / 255,
        0.4,
        cw,
        ch,
      );
    }
  }

  private renderDensityHeatmap(cw: number, ch: number): void {
    if (!this.densityBuffer) return;
    const gl = this.gl;
    const totalCells = this.cols * this.rows;
    const pixels = this.heatmapPixelData;

    for (let i = 0; i < totalCells; i++) {
      const rho = this.densityBuffer[i];
      const offset = i * 4;
      if (rho > 0.3) {
        const ratio = Math.min(1.0, rho / 5.0);
        if (ratio < 0.3) {
          // Blue (#3b82f6)
          pixels[offset] = 59;
          pixels[offset + 1] = 130;
          pixels[offset + 2] = 246;
          pixels[offset + 3] = Math.round(ratio * 0.5 * 255);
        } else if (ratio < 0.6) {
          // Yellow (#eab308)
          pixels[offset] = 234;
          pixels[offset + 1] = 179;
          pixels[offset + 2] = 8;
          pixels[offset + 3] = Math.round(ratio * 0.6 * 255);
        } else {
          // Red (#ef4444)
          pixels[offset] = 239;
          pixels[offset + 1] = 68;
          pixels[offset + 2] = 68;
          pixels[offset + 3] = Math.round(ratio * 0.75 * 255);
        }
      } else {
        pixels[offset] = 0;
        pixels[offset + 1] = 0;
        pixels[offset + 2] = 0;
        pixels[offset + 3] = 0;
      }
    }

    gl.bindTexture(gl.TEXTURE_2D, this.heatmapTexture);
    gl.texImage2D(
      gl.TEXTURE_2D,
      0,
      gl.RGBA,
      this.cols,
      this.rows,
      0,
      gl.RGBA,
      gl.UNSIGNED_BYTE,
      pixels,
    );

    const quadData = [
      0,
      0,
      0,
      0,
      cw,
      0,
      1,
      0,
      0,
      ch,
      0,
      1,
      0,
      ch,
      0,
      1,
      cw,
      0,
      1,
      0,
      cw,
      ch,
      1,
      1,
    ];

    gl.useProgram(this.heatmapProgram);
    gl.uniform2f(this.heatmapUniformCanvasSize, cw, ch);
    gl.uniform2f(this.heatmapUniformTexSize, this.cols, this.rows);
    gl.uniform1f(this.heatmapUniformOpacity, 0.9);
    gl.uniform1i(this.heatmapUniformTexture, 0);

    gl.activeTexture(gl.TEXTURE0);
    gl.bindTexture(gl.TEXTURE_2D, this.heatmapTexture);

    gl.bindVertexArray(this.heatmapVao);
    gl.bindBuffer(gl.ARRAY_BUFFER, this.heatmapVbo);
    gl.bufferData(gl.ARRAY_BUFFER, new Float32Array(quadData), gl.DYNAMIC_DRAW);
    gl.drawArrays(gl.TRIANGLES, 0, 6);
    gl.bindVertexArray(null);
  }

  private renderFlowVectors(
    scaleX: number,
    scaleY: number,
    cw: number,
    ch: number,
  ): void {
    if (!this.gradientXBuffer || !this.gradientYBuffer) return;
    const lineVerts: number[] = [];
    const step = 2; // Sample every 2 cells

    for (let r = 1; r < this.rows; r += step) {
      for (let c = 1; c < this.cols; c += step) {
        const idx = r * this.cols + c;
        if (this.cells[idx] === 1 || this.cells[idx] === 2) continue;

        const gx = this.gradientXBuffer[idx];
        const gy = this.gradientYBuffer[idx];
        if (Math.abs(gx) < 0.01 && Math.abs(gy) < 0.01) continue;

        const startX = (c + 0.5) * this.cellSize * scaleX;
        const startY = (r + 0.5) * this.cellSize * scaleY;
        const arrowLen = this.cellSize * scaleX * 0.8;
        const endX = startX + gx * arrowLen;
        const endY = startY + gy * arrowLen;

        // Shaft
        lineVerts.push(startX, startY, endX, endY);

        // Arrow head ticks
        const angle = Math.atan2(gy, gx);
        const headLen = arrowLen * 0.3;
        lineVerts.push(
          endX,
          endY,
          endX - headLen * Math.cos(angle - Math.PI / 6),
          endY - headLen * Math.sin(angle - Math.PI / 6),
          endX,
          endY,
          endX - headLen * Math.cos(angle + Math.PI / 6),
          endY - headLen * Math.sin(angle + Math.PI / 6),
        );
      }
    }

    this.renderFlatLines(
      lineVerts,
      148 / 255,
      163 / 255,
      184 / 255,
      0.45,
      cw,
      ch,
    );
  }

  private renderAgents(
    count: number,
    posX: Float32Array,
    posY: Float32Array,
    velX: Float32Array,
    velY: Float32Array,
    speed: Float32Array,
    headX: Float32Array,
    headY: Float32Array,
    active: Uint8Array,
    scaleX: number,
    scaleY: number,
    radiusMeters: number,
    cw: number,
    ch: number,
  ): void {
    if (count <= 0) return;
    const gl = this.gl;
    const totalFloats = count * 9;

    if (this.instanceData.length < totalFloats) {
      this.instanceData = new Float32Array(
        Math.max(totalFloats, count * 9 * 2),
      );
      gl.bindBuffer(gl.ARRAY_BUFFER, this.instanceVbo);
      gl.bufferData(
        gl.ARRAY_BUFFER,
        this.instanceData.byteLength,
        gl.DYNAMIC_DRAW,
      );
    }

    const data = this.instanceData;
    let offset = 0;
    for (let i = 0; i < count; i++) {
      data[offset++] = posX[i];
      data[offset++] = posY[i];
      data[offset++] = velX[i];
      data[offset++] = velY[i];
      data[offset++] = headX[i];
      data[offset++] = headY[i];
      data[offset++] = speed[i];
      data[offset++] = radiusMeters;
      data[offset++] = active[i];
    }

    gl.useProgram(this.agentProgram);
    gl.uniform2f(this.agentUniformScale, scaleX, scaleY);
    gl.uniform2f(this.agentUniformCanvasSize, cw, ch);

    gl.bindVertexArray(this.agentVao);
    gl.bindBuffer(gl.ARRAY_BUFFER, this.instanceVbo);
    gl.bufferSubData(gl.ARRAY_BUFFER, 0, data.subarray(0, totalFloats));

    // Single Hardware Instanced Draw Call!
    gl.drawArraysInstanced(gl.TRIANGLE_STRIP, 0, 4, count);
    gl.bindVertexArray(null);
  }

  private ensureDynamicBufferCapacity(requiredFloats: number): void {
    if (this.dynamicBufferFloats.length < requiredFloats) {
      let cap = this.dynamicBufferFloats.length * 2;
      while (cap < requiredFloats) cap *= 2;
      this.dynamicBufferFloats = new Float32Array(cap);
    }
  }

  private renderFlatTriangles(
    vertices: number[],
    r: number,
    g: number,
    b: number,
    a: number,
    cw: number,
    ch: number,
  ): void {
    if (vertices.length === 0) return;
    const gl = this.gl;
    this.ensureDynamicBufferCapacity(vertices.length);
    this.dynamicBufferFloats.set(vertices);

    gl.useProgram(this.flatProgram);
    gl.uniform2f(this.flatUniformCanvasSize, cw, ch);
    gl.uniform4f(this.flatUniformColor, r, g, b, a);

    gl.bindVertexArray(this.flatVao);
    gl.bindBuffer(gl.ARRAY_BUFFER, this.flatVbo);
    gl.bufferData(
      gl.ARRAY_BUFFER,
      this.dynamicBufferFloats.subarray(0, vertices.length),
      gl.DYNAMIC_DRAW,
    );
    gl.drawArrays(gl.TRIANGLES, 0, vertices.length / 2);
    gl.bindVertexArray(null);
  }

  private renderFlatLines(
    vertices: number[],
    r: number,
    g: number,
    b: number,
    a: number,
    cw: number,
    ch: number,
  ): void {
    if (vertices.length === 0) return;
    const gl = this.gl;
    this.ensureDynamicBufferCapacity(vertices.length);
    this.dynamicBufferFloats.set(vertices);

    gl.useProgram(this.flatProgram);
    gl.uniform2f(this.flatUniformCanvasSize, cw, ch);
    gl.uniform4f(this.flatUniformColor, r, g, b, a);

    gl.bindVertexArray(this.flatVao);
    gl.bindBuffer(gl.ARRAY_BUFFER, this.flatVbo);
    gl.bufferData(
      gl.ARRAY_BUFFER,
      this.dynamicBufferFloats.subarray(0, vertices.length),
      gl.DYNAMIC_DRAW,
    );
    gl.drawArrays(gl.LINES, 0, vertices.length / 2);
    gl.bindVertexArray(null);
  }
}
