using SharpDX.Direct3D11;

namespace ViewInFFXIV.Diagnostics;

/// <summary>
/// Times one section of GPU work per frame with D3D11 timestamp queries, read back a few frames later without
/// stalling. Unlike <see cref="GpuTimer"/>, everything (issue and read-back) happens on the thread that calls it,
/// so it can time work on the game's render thread.
/// </summary>
internal sealed class GpuStopwatch : IDisposable
{
    private const int Latency = 5;

    private readonly DeviceContext context;
    private readonly Slot[] slots;
    private int frame;
    private Slot? current;
    private double total;
    private int count;

    public GpuStopwatch(Device device, DeviceContext context)
    {
        this.context = context;
        slots = new Slot[Latency];
        for (var i = 0; i < slots.Length; i++)
            slots[i] = new Slot(device);
    }

    /// <summary>Average milliseconds per timed frame since the last <see cref="TakeAverage"/>.</summary>
    public double TakeAverage()
    {
        var average = count > 0 ? total / count : 0;
        total = 0;
        count = 0;
        return average;
    }

    public void Start()
    {
        var slot = slots[frame++ % slots.Length];
        if (slot.Pending && !TryRead(slot))
        {
            current = null;
            return;
        }

        current = slot;
        context.Begin(slot.Disjoint);
        context.End(slot.Begin);
    }

    public void Stop()
    {
        if (current == null)
            return;
        context.End(current.End);
        context.End(current.Disjoint);
        current.Pending = true;
        current = null;
    }

    private bool TryRead(Slot slot)
    {
        if (!context.GetData(slot.Disjoint, AsynchronousFlags.DoNotFlush, out QueryDataTimestampDisjoint disjoint)
            || !context.GetData(slot.Begin, AsynchronousFlags.DoNotFlush, out long begin)
            || !context.GetData(slot.End, AsynchronousFlags.DoNotFlush, out long end))
            return false;

        slot.Pending = false;
        if (!disjoint.Disjoint && disjoint.Frequency > 0)
        {
            total += (end - begin) * 1000.0 / disjoint.Frequency;
            count++;
        }

        return true;
    }

    public void Dispose()
    {
        foreach (var slot in slots)
            slot.Dispose();
    }

    private sealed class Slot : IDisposable
    {
        public Slot(Device device)
        {
            Disjoint = new Query(device, new QueryDescription { Type = QueryType.TimestampDisjoint });
            Begin = new Query(device, new QueryDescription { Type = QueryType.Timestamp });
            End = new Query(device, new QueryDescription { Type = QueryType.Timestamp });
        }

        public Query Disjoint { get; }

        public Query Begin { get; }

        public Query End { get; }

        public bool Pending { get; set; }

        public void Dispose()
        {
            Disjoint.Dispose();
            Begin.Dispose();
            End.Dispose();
        }
    }
}
