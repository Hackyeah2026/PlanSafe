namespace PlanSafe.App.Simulation;

/// <summary>Transfers the prepared map and agent state once using binary JS interop.</summary>
public sealed record MapGpuSnapshot(
    int Count, int Granulation, double SocialRepulsionWeight,
    int Columns, int Rows, double CellSize,
    byte[] Scenario, byte[] Agents, byte[] Fields, byte[] Blocked,
    byte[]? RoutingFields = null, int[]? ExitTargets = null, double WeightDistance = 1, double WeightOccupancy = 0,
    string[]? SafeZoneIds = null)
{
    public static MapGpuSnapshot Capture(CrowdSimulationEngine engine)
    {
        var scenario = engine.MapScenario ?? throw new ArgumentException("A map scenario is required.", nameof(engine));
        int dots = engine.SimulatedAgentCount;
        var agents = new float[dots * 8];
        for (int i = 0; i < dots; i++)
        {
            int offset = i * 8;
            agents[offset] = (float)engine.AgentPositionX[i];
            agents[offset + 1] = (float)engine.AgentPositionY[i];
            agents[offset + 2] = (float)engine.AgentVelocityX[i];
            agents[offset + 3] = (float)engine.AgentVelocityY[i];
            agents[offset + 4] = (float)engine.AgentRadius[i];
            agents[offset + 5] = (float)engine.AgentMaxSpeed[i];
            agents[offset + 6] = BitConverter.Int32BitsToSingle(i < engine.ActiveAgentCount ? 1 : 0);
            agents[offset + 7] = (float)engine.AgentLocalDensity[i];
        }
        var agentBytes = new byte[agents.Length * sizeof(float)];
        Buffer.BlockCopy(agents, 0, agentBytes, 0, agentBytes.Length);

        var grid = engine.PotentialFieldMap;
        int laneBytes = grid.TotalCells * sizeof(float);
        var fields = new byte[laneBytes * 4];
        Buffer.BlockCopy(grid.StaticPotentialFieldMatrix, 0, fields, 0, laneBytes);
        Buffer.BlockCopy(grid.DynamicPotentialFieldMatrix, 0, fields, laneBytes, laneBytes);
        Buffer.BlockCopy(grid.DynamicCrowdPenalty, 0, fields, laneBytes * 2, laneBytes);
        Buffer.BlockCopy(grid.SmoothedDensity.ToArray(), 0, fields, laneBytes * 3, laneBytes);
        var blocked = new byte[grid.TotalCells];
        for (int i = 0; i < blocked.Length; i++) blocked[i] = grid.ObstacleMask[i] ? (byte)1 : (byte)0;

        byte[]? routingFields = null;
        if (engine.MapRouting is not null)
        {
            routingFields = new byte[laneBytes * engine.MapRouting.DistanceFields.Length];
            for (int target = 0; target < engine.MapRouting.DistanceFields.Length; target++)
                Buffer.BlockCopy(engine.MapRouting.DistanceFields[target], 0, routingFields, target * laneBytes, laneBytes);
        }
        var exitZoneIds = scenario.Exits.Select((exit, index) => exit.TargetId ?? $"exit-{index}").ToArray();
        var safeZoneIds = exitZoneIds.Distinct().ToArray();
        var zoneIndices = safeZoneIds.Select((id, index) => (Id: id, Index: index)).ToDictionary(item => item.Id, item => item.Index);
        var exitTargets = exitZoneIds.Select(id => zoneIndices[id]).ToArray();
        return new MapGpuSnapshot(engine.AgentCount, engine.Granulation, engine.SocialRepulsionWeight,
            grid.ColumnCount, grid.RowCount, grid.CellSize, scenario.Serialize(), agentBytes, fields, blocked,
            routingFields, exitTargets, engine.WeightDistance, engine.WeightOccupancy, safeZoneIds);
    }
}
