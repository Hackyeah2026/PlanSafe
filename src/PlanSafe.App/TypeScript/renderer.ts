export interface RenderOptions {
  showDensityHeatmap: boolean;
  showFlowField: boolean;
  showSpawnZones: boolean;
  agentRadius: number;
}

export class CrowdCanvasRenderer {
  private canvas: HTMLCanvasElement;
  private ctx: CanvasRenderingContext2D;
  private worldWidth: number = 30;
  private worldHeight: number = 16;
  private cellSize: number = 0.5;
  private cols: number = 60;
  private rows: number = 32;
  private cells: Uint8Array = new Uint8Array(0);
  private densityBuffer: Float32Array | null = null;
  private gradientXBuffer: Float32Array | null = null;
  private gradientYBuffer: Float32Array | null = null;

  constructor(canvas: HTMLCanvasElement) {
    this.canvas = canvas;
    const ctx = canvas.getContext("2d", { alpha: false });
    if (!ctx) throw new Error("Could not acquire 2D context");
    this.ctx = ctx;
  }

  public setGrid(
    width: number,
    height: number,
    cellSize: number,
    cols: number,
    rows: number,
    cells: Uint8Array,
  ) {
    this.worldWidth = width;
    this.worldHeight = height;
    this.cellSize = cellSize;
    this.cols = cols;
    this.rows = rows;
    this.cells = cells;
  }

  public setFieldBuffers(
    density: Float32Array,
    gradX: Float32Array,
    gradY: Float32Array,
  ) {
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
  ) {
    const ctx = this.ctx;
    const cw = this.canvas.width;
    const ch = this.canvas.height;

    const scaleX = cw / this.worldWidth;
    const scaleY = ch / this.worldHeight;

    // 1. Clear background
    ctx.fillStyle = "#0f172a"; // Deep slate
    ctx.fillRect(0, 0, cw, ch);

    // 2. Draw Walkable Grid Lines
    ctx.strokeStyle = "#1e293b";
    ctx.lineWidth = 1;
    ctx.beginPath();
    for (let c = 0; c <= this.cols; c += 2) {
      const x = c * this.cellSize * scaleX;
      ctx.moveTo(x, 0);
      ctx.lineTo(x, ch);
    }
    for (let r = 0; r <= this.rows; r += 2) {
      const y = r * this.cellSize * scaleY;
      ctx.moveTo(0, y);
      ctx.lineTo(cw, y);
    }
    ctx.stroke();

    // 3. Optional Density Heatmap Overlay
    if (options.showDensityHeatmap && this.densityBuffer) {
      this.drawDensityHeatmap(scaleX, scaleY);
    }

    // 4. Optional Flow Vectors Overlay
    if (options.showFlowField && this.gradientXBuffer && this.gradientYBuffer) {
      this.drawFlowVectors(scaleX, scaleY);
    }

    // 5. Draw Obstacles, Exits and Spawn Zones
    this.drawGridFeatures(scaleX, scaleY, options.showSpawnZones);

    // 6. Draw Agents
    this.drawAgents(
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
    );
  }

  private drawGridFeatures(scaleX: number, scaleY: number, showSpawn: boolean) {
    const ctx = this.ctx;
    const cellW = this.cellSize * scaleX;
    const cellH = this.cellSize * scaleY;

    for (let r = 0; r < this.rows; r++) {
      for (let c = 0; c < this.cols; c++) {
        const type = this.cells[r * this.cols + c];
        const x = c * cellW;
        const y = r * cellH;

        if (type === 1) {
          // Obstacle: Solid architectural pillar/wall
          ctx.fillStyle = "#334155";
          ctx.fillRect(x, y, cellW + 0.5, cellH + 0.5);
          ctx.strokeStyle = "#475569";
          ctx.lineWidth = 1;
          ctx.strokeRect(x, y, cellW, cellH);
        } else if (type === 2) {
          // Exit: Emerald green zone
          ctx.fillStyle = "rgba(16, 185, 129, 0.4)";
          ctx.fillRect(x, y, cellW, cellH);
          ctx.strokeStyle = "#10b981";
          ctx.lineWidth = 1.5;
          ctx.strokeRect(x, y, cellW, cellH);
        } else if (type === 3 && showSpawn) {
          // Spawn zone: Subtle cyan outline
          ctx.fillStyle = "rgba(56, 189, 248, 0.12)";
          ctx.fillRect(x, y, cellW, cellH);
          ctx.strokeStyle = "rgba(56, 189, 248, 0.4)";
          ctx.lineWidth = 1;
          ctx.strokeRect(x, y, cellW, cellH);
        }
      }
    }
  }

  private drawDensityHeatmap(scaleX: number, scaleY: number) {
    if (!this.densityBuffer) return;
    const ctx = this.ctx;
    const cellW = this.cellSize * scaleX;
    const cellH = this.cellSize * scaleY;

    for (let r = 0; r < this.rows; r++) {
      for (let c = 0; c < this.cols; c++) {
        const rho = this.densityBuffer[r * this.cols + c];
        if (rho > 0.3) {
          // Normalize density [0, 5.4 ped/m²]
          const ratio = Math.min(1.0, rho / 5.0);
          if (ratio < 0.3) {
            ctx.fillStyle = `rgba(59, 130, 246, ${ratio * 0.5})`; // Blue
          } else if (ratio < 0.6) {
            ctx.fillStyle = `rgba(234, 179, 8, ${ratio * 0.6})`; // Yellow
          } else {
            ctx.fillStyle = `rgba(239, 68, 68, ${ratio * 0.75})`; // Red (dense crowd)
          }
          ctx.fillRect(c * cellW, r * cellH, cellW, cellH);
        }
      }
    }
  }

  private drawFlowVectors(scaleX: number, scaleY: number) {
    if (!this.gradientXBuffer || !this.gradientYBuffer) return;
    const ctx = this.ctx;
    ctx.strokeStyle = "rgba(148, 163, 184, 0.35)";
    ctx.lineWidth = 1.2;

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

        ctx.beginPath();
        ctx.moveTo(startX, startY);
        ctx.lineTo(endX, endY);
        ctx.stroke();
      }
    }
  }

  private drawAgents(
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
  ) {
    const ctx = this.ctx;
    const rPixels = Math.max(2.5, radiusMeters * ((scaleX + scaleY) * 0.5));

    for (let i = 0; i < count; i++) {
      if (active[i] === 0) continue;

      const px = posX[i] * scaleX;
      const py = posY[i] * scaleY;
      const spd = speed[i];

      // Color code based on speed / Weidmann congestion
      if (spd >= 1.05) {
        ctx.fillStyle = "#22c55e"; // Emerald green (Free flow)
      } else if (spd >= 0.6) {
        ctx.fillStyle = "#eab308"; // Amber (Moderate slowing)
      } else if (spd >= 0.2) {
        ctx.fillStyle = "#f97316"; // Orange (Heavy bottlenecking)
      } else {
        ctx.fillStyle = "#ef4444"; // Red (Crush / Jammed / Stopped)
      }

      ctx.beginPath();
      ctx.arc(px, py, rPixels, 0, Math.PI * 2);
      ctx.fill();

      // Heading directional pointer: strictly points along the path of least resistance to goal!
      const hx = headX[i];
      const hy = headY[i];
      ctx.strokeStyle = "rgba(255, 255, 255, 0.85)";
      ctx.lineWidth = 1.2;
      ctx.beginPath();
      ctx.moveTo(px, py);
      ctx.lineTo(px + hx * (rPixels * 1.5), py + hy * (rPixels * 1.5));
      ctx.stroke();
    }
  }
}
