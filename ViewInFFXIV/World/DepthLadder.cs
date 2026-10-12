namespace ViewInFFXIV.World;

/// <summary>How people and furniture hide the wall screen.</summary>
public enum DepthMode
{
    /// <summary>The pixel shader reads the scene depth (a copy of it) and hides what is behind something.</summary>
    Shader,

    /// <summary>
    /// The GPU's own depth test hides the screen against the scene depth; the shader never reads it. Name plates of
    /// people in front of the screen show over it.
    /// </summary>
    Hardware,

    /// <summary>Nothing hides the screen; it still writes its depth so name plates behind it stay hidden.</summary>
    None,
}

/// <summary>What one depth self-test measured on the render thread (occlusion queries).</summary>
public sealed class DepthTestResult
{
    public required long Id { get; init; }

    /// <summary>The test produced no numbers (queries not ready in time, an error); not a failure.</summary>
    public bool NoResult { get; init; }

    public string Note { get; init; } = "";

    /// <summary>Pixels of the screen drawn with nothing hiding it.</summary>
    public long Total { get; init; }

    /// <summary>Share of <see cref="Total"/> each variant shows, in <see cref="DepthLadder.Variants"/> order.</summary>
    public double[] Visible { get; init; } = [];
}

/// <summary>
/// On cards where the shader depth read is not known to work (AMD, Intel), finds out on this machine which way of
/// hiding the screen behind people and furniture works, by measuring it: once a second while the screen is in view,
/// the render thread draws the real screen invisibly with each variant and counts the pixels that survive (GPU
/// occlusion queries, no readback of the depth). A variant that shows at least <see cref="PassShare"/> of the screen
/// three times is kept for the session. Until then, and if none ever does, the screen shows over everything.
/// Never demotes: a wall can hide the screen for real. Main thread only.
/// </summary>
public sealed class DepthLadder
{
    /// <summary>Best first. The same order the render thread measures them in.</summary>
    public static readonly (DepthMode Mode, bool UseSceneInfo, string Name)[] Variants =
    [
        (DepthMode.Shader, true, "shader"),
        (DepthMode.Hardware, true, "hardware"),
        (DepthMode.Shader, false, "shader without scene info"),
        (DepthMode.Hardware, false, "hardware without scene info"),
    ];

    private const int PassesToKeep = 3;
    private const long MinPixels = 2000;
    private const double PassShare = 0.75;
    private const int MaxTests = 900;
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ResultTimeout = TimeSpan.FromSeconds(5);

    private readonly int[] passes = new int[Variants.Length];
    private long lastId;
    private long waitingId;
    private DateTime waitingSince;
    private DateTime nextTest;
    private int tests;
    private int counted;

    /// <summary>The variant kept for this session, or null while testing (the screen then shows over everything).</summary>
    public (DepthMode Mode, bool UseSceneInfo)? Kept { get; private set; }

    /// <summary>One line for /matinee rstat.</summary>
    public string Status { get; private set; } = "not run";

    public void Reset()
    {
        Array.Clear(passes);
        waitingId = 0;
        nextTest = default;
        tests = 0;
        counted = 0;
        Kept = null;
        Status = "not run";
    }

    /// <summary>Call every Draw. Returns the test the render thread should run (0: none).</summary>
    public long Tick(bool screenInView)
    {
        var now = DateTime.UtcNow;
        if (waitingId != 0)
        {
            if (WallRenderer.Pass.LadderResult is { } result && result.Id == waitingId)
            {
                waitingId = 0;
                Consume(result);
            }
            else if (now - waitingSince > ResultTimeout)
            {
                waitingId = 0;
                Status = $"test {tests}: no result in time";
            }
            else
            {
                return waitingId;
            }
        }

        if (Kept != null || WallRenderer.Pass.LadderBroken || tests >= MaxTests || !screenInView || now < nextTest)
        {
            if (WallRenderer.Pass.LadderBroken)
                Status = "stopped after an error; showing over everything";
            return 0;
        }

        tests++;
        nextTest = now + Interval;
        waitingSince = now;
        waitingId = ++lastId;
        return waitingId;
    }

    private void Consume(DepthTestResult result)
    {
        if (result.NoResult)
        {
            Status = $"test {tests}: no result ({result.Note})";
            Log(Status);
            return;
        }

        if (result.Total < MinPixels)
        {
            Status = $"test {tests}: too little of the screen in view ({result.Total} px)";
            return;
        }

        counted++;
        var shares = new List<string>();
        for (var i = 0; i < Variants.Length && i < result.Visible.Length; i++)
        {
            if (result.Visible[i] >= PassShare)
                passes[i]++;
            shares.Add($"{Variants[i].Name} {result.Visible[i]:P0} ({passes[i]}/{PassesToKeep})");
        }

        Status = $"test {counted}: {result.Total} px · " + string.Join(" · ", shares);
        if (counted <= 10 || counted % 30 == 0)
            Log(Status);

        for (var i = 0; i < Variants.Length; i++)
        {
            if (passes[i] < PassesToKeep)
                continue;
            Kept = (Variants[i].Mode, Variants[i].UseSceneInfo);
            Status = $"using {Variants[i].Name} (after {counted} tests) · last: " + string.Join(" · ", shares);
            Log(Status);
            return;
        }
    }

    private static void Log(string line) => Plugin.Log.Information("Wall screen depth self-test: {Line}", line);
}
