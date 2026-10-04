/** Keep distant crowds legible without a fixed pixel footprint at every zoom. */
export function agentRenderMinRadius(
  scaleX: number,
  scaleY: number,
  preferredRadius: number,
): number {
  const pixelsPerMeter = Math.min(Math.abs(scaleX), Math.abs(scaleY));
  return Math.min(preferredRadius, 0.95 * Math.pow(pixelsPerMeter, 0.3));
}
