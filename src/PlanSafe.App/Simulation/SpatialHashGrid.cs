using System;

namespace PlanSafe.App.Simulation;

public class SpatialHashGrid
{
    private readonly double _cellSize;
    private readonly double _inverseCellSize;
    private readonly int _bucketMask;
    private readonly int[] _firstAgentInBucket;
    private readonly int[] _nextAgentInBucket;
    private readonly int[] _queryResults = new int[2048];

    public SpatialHashGrid(double cellSize, int maxAgents = 100000, int bucketCount = 65536)
    {
        _cellSize = Math.Max(0.5, cellSize);
        _inverseCellSize = 1.0 / _cellSize;
        _bucketMask = bucketCount - 1;
        _firstAgentInBucket = new int[bucketCount];
        _nextAgentInBucket = new int[maxAgents];
        Array.Fill(_firstAgentInBucket, -1);
    }

    public void Clear()
    {
        Array.Fill(_firstAgentInBucket, -1);
    }

    public void Insert(int agentIndex, double positionX, double positionY)
    {
        int column = (int)(positionX * _inverseCellSize);
        int row = (int)(positionY * _inverseCellSize);
        int hash = ((column * 73856093) ^ (row * 19349663)) & _bucketMask;
        _nextAgentInBucket[agentIndex] = _firstAgentInBucket[hash];
        _firstAgentInBucket[hash] = agentIndex;
    }

    public int QueryNearby(double positionX, double positionY, double searchRange, out int[] buffer)
    {
        int paddedRadius = (int)Math.Ceiling(searchRange * _inverseCellSize);
        int centerColumn = (int)(positionX * _inverseCellSize);
        int centerRow = (int)(positionY * _inverseCellSize);
        int count = 0;

        for (int columnOffset = -paddedRadius; columnOffset <= paddedRadius; columnOffset++)
        {
            int columnHash = (centerColumn + columnOffset) * 73856093;
            for (int rowOffset = -paddedRadius; rowOffset <= paddedRadius; rowOffset++)
            {
                int hash = (columnHash ^ ((centerRow + rowOffset) * 19349663)) & _bucketMask;
                int agentIndex = _firstAgentInBucket[hash];
                while (agentIndex != -1 && count < _queryResults.Length)
                {
                    _queryResults[count++] = agentIndex;
                    agentIndex = _nextAgentInBucket[agentIndex];
                }
            }
        }
        buffer = _queryResults;
        return count;
    }
}
