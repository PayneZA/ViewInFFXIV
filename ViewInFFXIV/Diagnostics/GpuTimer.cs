using SharpDX.Direct3D11;

namespace ViewInFFXIV.Diagnostics;

/// <summary>
/// Measures how long the GPU spends on marked sections of our per-frame work, with D3D11 timestamp queries.
/// Results are read back a few frames later without stalling the game; a frame whose queries are still busy
/// is simply not timed.
/// </summary>
internal sealed class GpuTimer : IDisposable
{
    private const int Latency = 5;

    private readonly DeviceContext context;
    private readonly Slot[] slots;
    private int frame;
    private Slot? current;

    public GpuTimer(Device device, int marks)
    {
        context = device.ImmediateContext;
        slots = new Slot[Latency];
        for (var i = 0; i < slots.Length; i++)
            slots[i] = new Slot(device, marks);
    }

    /// <summary>Called with the milliseconds between consecutive marks of a finished frame.</summary>
    public event Action<double[]>? Measured;

    public void BeginFrame()
    {
        var slot = slots[frame++ % slots.Length];
        if (slot.Pending && !TryRead(slot))
        {
            current = null;
            return;
        }

        current = slot;
        context.Begin(slot.Disjoint);
    }

    /// <summary>Timestamps the point between two sections (call once per mark index, in order).</summary>
    public void Mark(int index)
    {
        if (current != null)
            context.End(current.Stamps[index]);
    }

    public void EndFrame()
    {
        if (current == null)
            return;
        context.End(current.Disjoint);
        current.Pending = true;
        current = null;
    }

    private bool TryRead(Slot slot)
    {
        if (!context.GetData(slot.Disjoint, AsynchronousFlags.DoNotFlush, out QueryDataTimestampDisjoint disjoint))
            return false;

        var stamps = new long[slot.Stamps.Length];
        for (var i = 0; i < stamps.Length; i++)
        {
            if (!context.GetData(slot.Stamps[i], AsynchronousFlags.DoNotFlush, out stamps[i]))
                return false;
        }

        slot.Pending = false;
        if (disjoint.Disjoint || disjoint.Frequency == 0)
            return true;

        var ms = new double[stamps.Length - 1];
        for (var i = 0; i < ms.Length; i++)
            ms[i] = (stamps[i + 1] - stamps[i]) * 1000.0 / disjoint.Frequency;
        Measured?.Invoke(ms);
        return true;
    }

    public void Dispose()
    {
        foreach (var slot in slots)
            slot.Dispose();
    }

    private sealed class Slot : IDisposable
    {
        public Slot(Device device, int marks)
        {
            Disjoint = new Query(device, new QueryDescription { Type = QueryType.TimestampDisjoint });
            Stamps = new Query[marks];
            for (var i = 0; i < marks; i++)
                Stamps[i] = new Query(device, new QueryDescription { Type = QueryType.Timestamp });
        }

        public Query Disjoint { get; }

        public Query[] Stamps { get; }

        public bool Pending { get; set; }

        public void Dispose()
        {
            Disjoint.Dispose();
            foreach (var stamp in Stamps)
                stamp.Dispose();
        }
    }
}
