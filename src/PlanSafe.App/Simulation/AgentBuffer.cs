namespace PlanSafe.App.Simulation;

/// <summary>
/// Contiguous flat Struct-of-Arrays (SoA) buffer for pedestrian crowd agents.
/// Designed for zero heap allocations per tick and seamless transition to WebGPU compute storage buffers.
/// </summary>
public class AgentBuffer
{
    public int Capacity { get; }
    public int Count { get; set; }

    public float[] PosX { get; }
    public float[] PosY { get; }
    public float[] VelX { get; }
    public float[] VelY { get; }
    public float[] Speed { get; }
    public float[] HeadX { get; }
    public float[] HeadY { get; }
    public byte[] Active { get; }

    public AgentBuffer(int capacity)
    {
        Capacity = capacity;
        Count = 0;

        PosX = new float[capacity];
        PosY = new float[capacity];
        VelX = new float[capacity];
        VelY = new float[capacity];
        Speed = new float[capacity];
        HeadX = new float[capacity];
        HeadY = new float[capacity];
        Active = new byte[capacity];
    }

    public void CopyFrom(AgentBuffer source)
    {
        Count = source.Count;
        Array.Copy(source.PosX, PosX, Count);
        Array.Copy(source.PosY, PosY, Count);
        Array.Copy(source.VelX, VelX, Count);
        Array.Copy(source.VelY, VelY, Count);
        Array.Copy(source.Speed, Speed, Count);
        Array.Copy(source.HeadX, HeadX, Count);
        Array.Copy(source.HeadY, HeadY, Count);
        Array.Copy(source.Active, Active, Count);
    }

    public void Clear()
    {
        Count = 0;
        Array.Clear(Active, 0, Capacity);
        Array.Clear(HeadX, 0, Capacity);
        Array.Clear(HeadY, 0, Capacity);
    }

    public int AddAgent(float x, float y, float vx = 0f, float vy = 0f, float headX = 1f, float headY = 0f)
    {
        if (Count >= Capacity) return -1;
        int idx = Count++;
        PosX[idx] = x;
        PosY[idx] = y;
        VelX[idx] = vx;
        VelY[idx] = vy;
        Speed[idx] = (float)Math.Sqrt(vx * vx + vy * vy);
        HeadX[idx] = headX;
        HeadY[idx] = headY;
        Active[idx] = 1;
        return idx;
    }
}
