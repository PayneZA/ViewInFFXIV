using System.Numerics;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;
using FFXIVClientStructs.FFXIV.Client.Graphics.Render;

namespace ViewInFFXIV.World;

/// <summary>
/// Draws the wall screen inside the game's own frame, on the render thread: after all post-processing and before
/// any UI, so name plates, chat bubbles, the HUD and loading fades draw over it, with no frame of lag.
/// <para>
/// Where: the game ends each frame by binding its final image (the tone-adjust source when gamma or a colour filter
/// is set, otherwise the swap chain's back buffer) first for post-processing, then together with a depth buffer for
/// the name plates, then for the 2D UI. We draw right before that first depth-bound bind, testing against the same
/// depth buffer. Found with <c>/matinee rtrace</c>.
/// </para>
/// <para>
/// This is the part most likely to break on a game patch. Every line that depends on how the game renders is
/// tagged <c>GAME ASSUMPTION A&lt;n&gt;</c>; docs/renderer.md explains each one, its symptoms when broken and how to
/// re-check it. If anything here fails, <see cref="ScreenRenderer"/> falls back to the overlay on its own, so a
/// broken patch shows the screen over the UI rather than not at all.
/// </para>
/// </summary>
public sealed unsafe class FrameInjector : IDisposable
{
    private delegate void SetTargetDelegate(ImmediateContext* ctx, RenderCommandSetTarget* command);

    private delegate void ProcessCommandsDelegate(ImmediateContext* ctx, RenderCommandBufferGroup* commands, uint count);

    private readonly WallRenderer.Pass pass;
    private readonly Hook<SetTargetDelegate> setTargetHook;
    private readonly Hook<ProcessCommandsDelegate> processHook;
    private volatile WallFrame? frame;
    private long renderFrames;
    private bool drewThisFrame;
    private bool wantedThisFrame;
    private int inside;
    private bool faulted;

    public FrameInjector(WallRenderer renderer)
    {
        pass = renderer.CreatePass();

        // GAME ASSUMPTION A1: ClientStructs resolves these two functions every patch (no signatures of our own).
        // DoSetTargetCommand runs for every render-target switch; ProcessCommands runs once per frame and executes
        // the whole frame's command list. A wrong address throws here and we stay on the overlay.
        setTargetHook = Plugin.GameInterop.HookFromAddress<SetTargetDelegate>(
            ImmediateContext.Addresses.DoSetTargetCommand.Value, SetTargetDetour);
        processHook = Plugin.GameInterop.HookFromAddress<ProcessCommandsDelegate>(
            ImmediateContext.Addresses.ProcessCommands.Value, ProcessDetour);
        setTargetHook.Enable();
        processHook.Enable();
    }

    /// <summary>Frames the game has rendered since we hooked it (render thread).</summary>
    public long RenderFrames => Interlocked.Read(ref renderFrames);

    /// <summary>Rendered frames in a row that should have shown the screen but did not find the spot to draw it.</summary>
    public int ConsecutiveMisses { get; private set; }

    /// <summary>Total frames drawn in-world.</summary>
    public long Drawn { get; private set; }

    // Diagnostics for /matinee rstat.
    public long Stale { get; private set; }

    public long Missed { get; private set; }

    public long Failed { get; private set; }

    /// <summary>What the last missed frame looked like (diagnostics).</summary>
    public string LastMiss { get; private set; } = "";

    // Taken at the start of each rendered frame (see BeginFrame).
    private Texture* toneAdjust;
    private Texture* backBuffer;
    private Texture* sceneInfo;
    private Matrix4x4 viewProj;
    private Vector3 cameraPos;
    private float nearPlane;
    private int finalBinds;

    /// <summary>True while the render thread is executing the game's commands (to detect overlap with other threads).</summary>
    public static volatile bool Rendering;

    /// <summary>Stopped after an error; the overlay takes over.</summary>
    public bool Faulted => faulted;

    public bool Occluding => pass.Occluding;

    /// <summary>Average GPU time of the in-world draw since the last call.</summary>
    public double TakeGpuMilliseconds() => pass.TakeGpuMilliseconds();

    public string? LastProblem => pass.LastProblem;

    public string DepthInfo => pass.DepthInfo;

    /// <summary>Shows <paramref name="next"/> from the next rendered frame on, or nothing for null.</summary>
    public void Publish(WallFrame? next) => frame = next;

    public void Dispose()
    {
        frame = null;
        setTargetHook.Disable();
        processHook.Disable();

        // A render-thread call may still be inside a detour; let it leave before freeing what it uses.
        var wait = System.Diagnostics.Stopwatch.StartNew();
        while (Volatile.Read(ref inside) != 0 && wait.ElapsedMilliseconds < 500)
            Thread.Sleep(1);

        setTargetHook.Dispose();
        processHook.Dispose();
        pass.Dispose();
    }

    private void ProcessDetour(ImmediateContext* ctx, RenderCommandBufferGroup* commands, uint count)
    {
        Interlocked.Increment(ref inside);
        try
        {
            BeginFrame();
            Rendering = true;
            processHook.Original(ctx, commands, count);
            Rendering = false;
            EndFrame(count);
        }
        finally
        {
            Interlocked.Decrement(ref inside);
        }
    }

    /// <summary>
    /// Takes everything the draw needs the moment the game hands this frame to the render thread. The game thread
    /// is already preparing the next frame while we render (camera, render-target fields), so nothing of its
    /// state may be read later in the frame.
    /// </summary>
    /// <remarks>
    /// GAME ASSUMPTION A4: the game thread overlaps the render thread completely; reading its state mid-frame lost
    /// ~30% of frames (flicker only while the game is focused and running fast). Snapshot here, use later.
    /// </remarks>
    private void BeginFrame()
    {
        drewThisFrame = false;
        finalBinds = 0;
        var current = frame;
        wantedThisFrame = current != null && Interlocked.Read(ref renderFrames) - current.Stamp <= 2;
        if (current != null && !wantedThisFrame)
            Stale++;
        if (!wantedThisFrame)
            return;

        var rtm = RenderTargetManager.Instance();
        var device = Device.Instance();
        var manager = FFXIVClientStructs.FFXIV.Client.Graphics.Render.Manager.Instance();
        var camera = manager != null ? manager->MainCamera : null;
        if (rtm == null || device == null || device->SwapChain == null || camera == null)
        {
            wantedThisFrame = false;
            return;
        }

        // GAME ASSUMPTION A3: the frame ends in the swap chain's back buffer, or in the tone-adjust source when
        // gamma or a colour filter is set. Both pointers are stable (unlike RenderTargetManager.SwapChainBackBuffer,
        // which the game thread switches between them while we render).
        toneAdjust = rtm->ToneAdjustSource;
        backBuffer = device->SwapChain->BackBuffer;

        // GAME ASSUMPTION A7: G-buffer 3 is black on characters (and sky), coloured on scenery.
        sceneInfo = rtm->GBuffers[3].Value;
        pass.SceneDepth = rtm->DepthStencil;

        // GAME ASSUMPTION A5: at the start of ProcessCommands the main render camera still describes the frame
        // being rendered (checked: equal to Control.ViewProjectionMatrix during the frame).
        viewProj = (Matrix4x4)camera->ViewMatrix * (Matrix4x4)camera->ProjectionMatrix;
        nearPlane = camera->NearPlane;
        cameraPos = camera->Origin;
    }

    private void EndFrame(uint count)
    {
        Interlocked.Increment(ref renderFrames);
        if (wantedThisFrame && !drewThisFrame)
        {
            ConsecutiveMisses++;
            Missed++;
            LastMiss = $"final binds {finalBinds}, commands {count}";
        }
        else if (drewThisFrame)
        {
            ConsecutiveMisses = 0;
        }
    }

    private void SetTargetDetour(ImmediateContext* ctx, RenderCommandSetTarget* command)
    {
        Interlocked.Increment(ref inside);
        try
        {
            if (wantedThisFrame && !drewThisFrame && !faulted)
                TryDraw(command);
            setTargetHook.Original(ctx, command);
        }
        finally
        {
            Interlocked.Decrement(ref inside);
        }
    }

    private void TryDraw(RenderCommandSetTarget* command)
    {
        // GAME ASSUMPTION A2: the injection rule. The first bind of the final target *with* a depth buffer is the
        // game's name plate pass; everything before it on that target is post-processing, everything after it UI.
        // RenderTargets[0] and DepthBuffer are ClientStructs fields of the SetTarget command (A1).
        var target = command->RenderTargets[0].Value;
        if (target == null || (target != toneAdjust && target != backBuffer))
            return;

        finalBinds++;
        if (command->DepthBuffer == null)
            return;

        // Only once per frame, whether or not the draw below succeeds.
        drewThisFrame = true;
        var current = frame;
        if (current == null)
            return;

        try
        {
            // GAME ASSUMPTION A6/A8: the depth buffer bound here is the scene depth the name plates test against, so
            // we both read it (occlusion) and write the screen into it (name plates and markers behind the screen).
            if (pass.Draw(current, target, command->DepthBuffer, sceneInfo, viewProj, cameraPos, nearPlane, writeDepth: true))
                Drawn++;
            else
                Failed++;
        }
        catch (Exception ex)
        {
            // Never take the game down with us: stop drawing in-world and let the overlay show the screen.
            faulted = true;
            Plugin.Log.Error(ex, "Wall screen: in-world drawing failed; switching to the overlay");
        }
    }
}
