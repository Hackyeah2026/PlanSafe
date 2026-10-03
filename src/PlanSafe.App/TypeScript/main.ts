import {
  CrowdCanvasRenderer,
  ICrowdRenderer,
  RenderOptions,
} from "./renderer.js";
import { CrowdWebGLRenderer } from "./webglRenderer.js";

declare global {
  interface Window {
    PlanSafeRenderer: {
      rendererInstance: ICrowdRenderer | null;
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

let rendererInstance: ICrowdRenderer | null = null;

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
    try {
      rendererInstance = new CrowdWebGLRenderer(canvas);
      console.log(
        "[PlanSafe] Initialized WebGL 2.0 hardware-accelerated crowd renderer",
      );
    } catch (err) {
      console.warn(
        "[PlanSafe] WebGL 2.0 unavailable, falling back to 2D Canvas renderer:",
        err,
      );
      rendererInstance = new CrowdCanvasRenderer(canvas);
    }
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
