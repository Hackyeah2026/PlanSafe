/** Greedy reservations include arrivals and agents already travelling to each safe zone. */
export function assignMapSafeZones(
  fields: Float32Array,
  agents: Float32Array,
  columns: number,
  rows: number,
  cellSize: number,
  exitTargets: readonly number[],
  evacuatedPerExit: readonly number[],
  distanceWeight: number,
  occupancyWeight: number,
  worldWidth: number,
  worldHeight: number,
): Float32Array {
  const cells = columns * rows;
  const targetCount = fields.length / cells;
  const dispatched = new Array<number>(targetCount).fill(0);
  for (let exit = 0; exit < evacuatedPerExit.length; exit++)
    dispatched[exitTargets[exit]] += evacuatedPerExit[exit];
  const assignments = new Float32Array(agents.length / 8).fill(-1);
  let population = evacuatedPerExit.reduce((sum, count) => sum + count, 0);
  for (let agent = 0; agent < assignments.length; agent++)
    if (agents[agent * 8 + 4] > 0) population++;
  population = Math.max(1, population);
  const distanceScale = Math.max(1, Math.hypot(worldWidth, worldHeight));
  for (let agent = 0; agent < assignments.length; agent++) {
    if (agents[agent * 8 + 4] <= 0) continue;
    const column = Math.max(
      0,
      Math.min(columns - 1, Math.floor(agents[agent * 8] / cellSize)),
    );
    const row = Math.max(
      0,
      Math.min(rows - 1, Math.floor(agents[agent * 8 + 1] / cellSize)),
    );
    const cell = row * columns + column;
    let selected = -1;
    let bestCost = Infinity;
    for (let target = 0; target < targetCount; target++) {
      const distance = fields[target * cells + cell];
      if (distance >= 1.7014117e38) continue;
      const cost =
        (distanceWeight * distance) / distanceScale +
        (occupancyWeight * dispatched[target]) / population;
      if (cost < bestCost) {
        bestCost = cost;
        selected = target;
      }
    }
    assignments[agent] = selected;
    if (selected >= 0) dispatched[selected]++;
  }
  return assignments;
}
