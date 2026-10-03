import { CrowdCanvasRenderer, RenderOptions } from "./renderer.js";

declare global {
  interface Window {
    PlanSafeRenderer: {
      rendererInstance: CrowdCanvasRenderer | null;
      init: (
        canvasId: string,
        width: number,
        height: number,
        cellSize: number,
        cols: number,
        rows: number,
        cells: number[] | Uint8Array,
      ) => boolean;
      updateGrid: (
        width: number,
        height: number,
        cellSize: number,
        cols: number,
        rows: number,
        cells: number[] | Uint8Array,
      ) => void;
      setFieldBuffers: (
        density: Float32Array,
        gradX: Float32Array,
        gradY: Float32Array,
      ) => void;
      render: (
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
      ) => void;
    };
  }
}

let rendererInstance: CrowdCanvasRenderer | null = null;

window.PlanSafeRenderer = {
  rendererInstance,
  init: (canvasId, width, height, cellSize, cols, rows, cells) => {
    const canvas = document.getElementById(canvasId) as HTMLCanvasElement;
    if (!canvas) {
      console.error(`Canvas #${canvasId} not found`);
      return false;
    }

    const uintCells =
      cells instanceof Uint8Array ? cells : new Uint8Array(cells);
    rendererInstance = new CrowdCanvasRenderer(canvas);
    rendererInstance.setGrid(width, height, cellSize, cols, rows, uintCells);
    window.PlanSafeRenderer.rendererInstance = rendererInstance;
    return true;
  },

  updateGrid: (width, height, cellSize, cols, rows, cells) => {
    if (!rendererInstance) return;
    const uintCells =
      cells instanceof Uint8Array ? cells : new Uint8Array(cells);
    rendererInstance.setGrid(width, height, cellSize, cols, rows, uintCells);
  },

  setFieldBuffers: (density, gradX, gradY) => {
    if (!rendererInstance) return;
    rendererInstance.setFieldBuffers(density, gradX, gradY);
  },

  render: (
    agentCount,
    posX,
    posY,
    velX,
    velY,
    speed,
    headX,
    headY,
    active,
    options,
  ) => {
    if (!rendererInstance) return;
    rendererInstance.render(
      agentCount,
      posX,
      posY,
      velX,
      velY,
      speed,
      headX,
      headY,
      active,
      options,
    );
  },
};
