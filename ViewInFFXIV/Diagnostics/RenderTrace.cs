using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;
using FFXIVClientStructs.FFXIV.Client.Graphics.Render;
using RenderCamera = FFXIVClientStructs.FFXIV.Client.Graphics.Render.Camera;

namespace ViewInFFXIV.Diagnostics;

/// <summary>
/// /matinee rtrace: records how the game switches render targets for a few frames (which texture, which thread,
/// which camera), writes a report next to the config and unhooks. Used to find where the wall screen can be drawn
/// inside the game's frame, after the 3D scene and before the UI.
/// </summary>
/// <remarks>
/// The first tool to reach for after a patch breaks the in-world screen: it shows whether GAME ASSUMPTIONS A1–A4
/// and A8 still hold. docs/renderer.md ("Reading a render trace") walks through a healthy report. Texture names come
/// from RenderTargetManager's own field names (by reflection), so they follow ClientStructs. Draw counts come from
/// hooking the D3D11 draw calls of the immediate context for the duration of the trace only.
/// </remarks>
public sealed unsafe class RenderTrace : IDisposable
{
    private const int FramesToCapture = 3;
    private const int MaxEvents = 20000;

    private delegate void SetTargetDelegate(ImmediateContext* ctx, RenderCommandSetTarget* command);

    private delegate void ProcessCommandsDelegate(ImmediateContext* ctx, RenderCommandBufferGroup* commands, uint count);

    private delegate void DrawIndexedDelegate(nint ctx, uint indexCount, uint startIndex, int baseVertex);

    private delegate void DrawDelegate(nint ctx, uint vertexCount, uint startVertex);

    private delegate void DrawIndexedInstancedDelegate(nint ctx, uint indexCount, uint instances, uint startIndex, int baseVertex, uint startInstance);

    private delegate void DrawInstancedDelegate(nint ctx, uint vertexCount, uint instances, uint startVertex, uint startInstance);

    // ImmediateContext._currentRenderTargets has no public accessor in this ClientStructs; read it at its declared offset.
    private static readonly int CurrentTargetsOffset =
        typeof(ImmediateContext).GetField("_currentRenderTargets", BindingFlags.NonPublic | BindingFlags.Instance)
            ?.GetCustomAttribute<FieldOffsetAttribute>()?.Value ?? -1;

    private readonly Event[] events = new Event[MaxEvents];
    private Hook<SetTargetDelegate>? setTargetHook;
    private Hook<ProcessCommandsDelegate>? processHook;
    private Hook<DrawIndexedDelegate>? drawIndexedHook;
    private Hook<DrawDelegate>? drawHook;
    private Hook<DrawIndexedInstancedDelegate>? drawIndexedInstancedHook;
    private Hook<DrawInstancedDelegate>? drawInstancedHook;
    private int renderThread;
    private int lastSetTarget = -1;
    private int drawsInBlock;
    private Dictionary<nint, string> names = new();
    private volatile int state; // 0 idle, 1 armed (waiting for a frame start), 2 capturing, 3 finished
    private int eventCount;
    private int frame;
    private nint lastTarget0;

    public bool Busy => state != 0;

    public void Start()
    {
        if (Busy)
            return;

        // The hooks are made once and then only switched on and off; they live until the plugin unloads. Disposing
        // them after each trace crashed the game once (NullReferenceException inside the frame, on the second trace):
        // these functions are also hooked by FrameInjector, and a render-thread call could still reach a detour whose
        // hook had been set to null.
        try
        {
            if (setTargetHook == null)
                CreateHooks();
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Render trace: could not hook the game's render commands");
            Plugin.ChatGui.PrintError($"Render trace unavailable: {ex.Message}", "Matinee");
            DisposeHooks();
            return;
        }

        names = BuildNames();
        eventCount = 0;
        frame = 0;
        lastTarget0 = 0;
        lastSetTarget = -1;
        drawsInBlock = 0;
        renderThread = 0;
        state = 1;
        setTargetHook!.Enable();
        processHook!.Enable();
        drawIndexedHook!.Enable();
        drawHook!.Enable();
        drawIndexedInstancedHook!.Enable();
        drawInstancedHook!.Enable();
        Plugin.ChatGui.Print("Render trace: capturing 3 frames…", "Matinee");
    }

    private void CreateHooks()
    {
        setTargetHook = Plugin.GameInterop.HookFromAddress<SetTargetDelegate>(
            ImmediateContext.Addresses.DoSetTargetCommand.Value, SetTargetDetour);
        processHook = Plugin.GameInterop.HookFromAddress<ProcessCommandsDelegate>(
            ImmediateContext.Addresses.ProcessCommands.Value, ProcessDetour);

        // ID3D11DeviceContext vtable: 12 DrawIndexed, 13 Draw, 20 DrawIndexedInstanced, 21 DrawInstanced.
        var vtable = *(nint**)Device.Instance()->D3D11DeviceContext;
        drawIndexedHook = Plugin.GameInterop.HookFromAddress<DrawIndexedDelegate>(vtable[12], DrawIndexedDetour);
        drawHook = Plugin.GameInterop.HookFromAddress<DrawDelegate>(vtable[13], DrawDetour);
        drawIndexedInstancedHook = Plugin.GameInterop.HookFromAddress<DrawIndexedInstancedDelegate>(vtable[20], DrawIndexedInstancedDetour);
        drawInstancedHook = Plugin.GameInterop.HookFromAddress<DrawInstancedDelegate>(vtable[21], DrawInstancedDetour);
    }

    /// <summary>Call from UiBuilder.Draw (inside the game's Present). Only a marker: Draw does not run while the UI is hidden.</summary>
    public void OnDraw()
    {
        if (state == 2)
            Add(new Event { Kind = EventKind.Draw });
    }

    /// <summary>Call from Framework.Update: writes the report and releases the hooks once a capture is done.</summary>
    public void Tick()
    {
        if (state == 3)
        {
            state = 0;
            setTargetHook?.Disable();
            processHook?.Disable();
            drawIndexedHook?.Disable();
            drawHook?.Disable();
            drawIndexedInstancedHook?.Disable();
            drawInstancedHook?.Disable();
            WriteReport();
        }
    }

    public void Dispose()
    {
        state = 0;
        DisposeHooks();
    }

    private void DisposeHooks()
    {
        setTargetHook?.Dispose();
        setTargetHook = null;
        processHook?.Dispose();
        processHook = null;
        drawIndexedHook?.Dispose();
        drawIndexedHook = null;
        drawHook?.Dispose();
        drawHook = null;
        drawIndexedInstancedHook?.Dispose();
        drawIndexedInstancedHook = null;
        drawInstancedHook?.Dispose();
        drawInstancedHook = null;
    }

    private void CountDraw()
    {
        if (state == 2 && Environment.CurrentManagedThreadId == renderThread)
            drawsInBlock++;
    }

    /// <summary>Closes the current target block: its draw count goes on the SetTarget that opened it.</summary>
    private void CloseBlock()
    {
        if (lastSetTarget >= 0 && lastSetTarget < MaxEvents)
            events[lastSetTarget].Draws = drawsInBlock;
        drawsInBlock = 0;
    }

    private void DrawIndexedDetour(nint ctx, uint indexCount, uint startIndex, int baseVertex)
    {
        CountDraw();
        drawIndexedHook!.Original(ctx, indexCount, startIndex, baseVertex);
    }

    private void DrawDetour(nint ctx, uint vertexCount, uint startVertex)
    {
        CountDraw();
        drawHook!.Original(ctx, vertexCount, startVertex);
    }

    private void DrawIndexedInstancedDetour(nint ctx, uint indexCount, uint instances, uint startIndex, int baseVertex, uint startInstance)
    {
        CountDraw();
        drawIndexedInstancedHook!.Original(ctx, indexCount, instances, startIndex, baseVertex, startInstance);
    }

    private void DrawInstancedDetour(nint ctx, uint vertexCount, uint instances, uint startVertex, uint startInstance)
    {
        CountDraw();
        drawInstancedHook!.Original(ctx, vertexCount, instances, startVertex, startInstance);
    }

    private void SetTargetDetour(ImmediateContext* ctx, RenderCommandSetTarget* command)
    {
        if (state == 2)
        {
            try
            {
                var e = new Event { Kind = EventKind.SetTarget, Count = command->RenderTargetCount };
                var current = CurrentTarget0(ctx);
                e.Mismatch = lastTarget0 != 0 && current != 0 && current != lastTarget0;
                var targets = command->RenderTargets;
                e.Rt0 = (nint)targets[0].Value;
                e.Rt1 = (nint)targets[1].Value;
                e.Rt2 = (nint)targets[2].Value;
                e.Ds = (nint)command->DepthBuffer;
                CaptureCameras(ref e);
                CloseBlock();
                lastSetTarget = Add(e);
            }
            catch
            {
                // Never let a diagnostic break the game's frame.
            }
        }

        setTargetHook!.Original(ctx, command);
        if (state == 2)
            lastTarget0 = CurrentTarget0(ctx);
    }

    private static nint CurrentTarget0(ImmediateContext* ctx) =>
        CurrentTargetsOffset < 0 ? 0 : *(nint*)((byte*)ctx + CurrentTargetsOffset);

    private void ProcessDetour(ImmediateContext* ctx, RenderCommandBufferGroup* commands, uint count)
    {
        // Frames are counted by the game's own command processing, so the trace also works with the UI hidden.
        if (state == 1)
        {
            renderThread = Environment.CurrentManagedThreadId;
            state = 2;
        }

        if (state == 2)
        {
            drawsInBlock = 0;
            lastSetTarget = -1;
            Add(new Event { Kind = EventKind.ProcessBegin, Count = (int)count });
        }
        processHook!.Original(ctx, commands, count);
        if (state == 2)
        {
            CloseBlock();
            lastSetTarget = -1;
            Add(new Event { Kind = EventKind.ProcessEnd, Count = (int)count });
            if (++frame >= FramesToCapture)
                state = 3;
        }
    }

    private int Add(Event e)
    {
        e.Thread = Environment.CurrentManagedThreadId;
        e.Frame = frame;
        var i = Interlocked.Increment(ref eventCount) - 1;
        if (i < MaxEvents)
            events[i] = e;
        return i;
    }

    private static void CaptureCameras(ref Event e)
    {
        var control = Control.Instance();
        if (control != null)
        {
            var m = control->ViewProjectionMatrix;
            e.ControlVp = new Vector4(m.M41, m.M42, m.M43, m.M44);
        }

        var manager = FFXIVClientStructs.FFXIV.Client.Graphics.Render.Manager.Instance();
        RenderCamera* camera = manager != null ? manager->MainCamera : null;
        if (camera != null)
        {
            var vp = (Matrix4x4)camera->ViewMatrix * (Matrix4x4)camera->ProjectionMatrix;
            e.MainVp = new Vector4(vp.M41, vp.M42, vp.M43, vp.M44);
        }
    }

    private static Dictionary<nint, string> BuildNames()
    {
        var map = new Dictionary<nint, string>();
        var device = Device.Instance();
        if (device != null && device->SwapChain != null)
        {
            map.TryAdd((nint)device->SwapChain->BackBuffer, "SwapChain.BackBuffer");
            map.TryAdd((nint)device->SwapChain->DepthStencil, "SwapChain.DepthStencil");
        }

        var rtm = RenderTargetManager.Instance();
        if (rtm != null)
            AddFields(map, typeof(RenderTargetManager), (byte*)rtm, "");

        map.Remove(0);
        return map;
    }

    private static void AddFields(Dictionary<nint, string> map, Type type, byte* baseAddress, string prefix)
    {
        foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
        {
            var offset = field.GetCustomAttribute<FieldOffsetAttribute>()?.Value;
            if (offset == null)
                continue;

            var name = prefix + field.Name.TrimStart('_');
            if (field.FieldType == typeof(Texture*))
            {
                map.TryAdd(*(nint*)(baseAddress + offset.Value), name);
            }
            else if (field.FieldType.Name.StartsWith("FixedSizeArray", StringComparison.Ordinal)
                     && field.FieldType.GenericTypeArguments is [var element]
                     && element.Name.StartsWith("Pointer", StringComparison.Ordinal)
                     && int.TryParse(field.FieldType.Name["FixedSizeArray".Length..].Split('`')[0], out var length))
            {
                for (var i = 0; i < length; i++)
                    map.TryAdd(*(nint*)(baseAddress + offset.Value + (i * sizeof(nint))), $"{name}[{i}]");
            }
        }
    }

    private string Describe(nint texture)
    {
        if (texture == 0)
            return "-";

        var t = (Texture*)texture;
        var name = names.TryGetValue(texture, out var n) ? n : $"0x{texture:X}";
        return $"{name}({t->ActualWidth}x{t->ActualHeight}/{t->AllocatedWidth}x{t->AllocatedHeight} {t->TextureFormat})";
    }

    private void WriteReport()
    {
        var count = Math.Min(eventCount, MaxEvents);
        var sb = new StringBuilder();
        var device = Device.Instance();
        var rtm = RenderTargetManager.Instance();
        var uiHidden = Plugin.GameGui.GameUiHidden || Plugin.ClientState.IsGPosing;
        sb.AppendLine($"Matinee render trace {DateTime.Now:yyyy-MM-dd HH:mm:ss}, {count} events, {FramesToCapture} frames, UI hidden {uiHidden}, GPose {Plugin.ClientState.IsGPosing}");
        if (device != null)
            sb.AppendLine($"Device {device->Width}x{device->Height} gamma {device->GammaCorrection} colorFilter {device->ColorFilter}/{device->ColorFilterRange}");
        if (rtm != null)
        {
            sb.AppendLine($"Resolution {rtm->Resolution_Width}x{rtm->Resolution_Height} rezo {rtm->GraphicsRezoScale}/{rtm->GraphicsRezoScaleY}");
            sb.AppendLine($"Key textures: backbuffer {Describe((nint)rtm->SwapChainBackBuffer)} · toneAdjust {Describe((nint)rtm->ToneAdjustSource)} · depth {Describe((nint)rtm->DepthStencil)}");
        }

        var threads = new Dictionary<int, int>();
        var firstBackbuffer = new Dictionary<int, int>();
        var mismatches = 0;
        var backbuffer = rtm != null ? (nint)rtm->SwapChainBackBuffer : 0;
        var toneAdjust = rtm != null ? (nint)rtm->ToneAdjustSource : 0;
        for (var i = 0; i < count; i++)
        {
            var e = events[i];
            threads[e.Thread] = threads.GetValueOrDefault(e.Thread) + 1;
            sb.Append($"#{i,5} f{e.Frame} t{e.Thread} ");
            switch (e.Kind)
            {
                case EventKind.Draw:
                    sb.AppendLine("──── UiBuilder.Draw (Present) ────");
                    break;
                case EventKind.ProcessBegin:
                    sb.AppendLine($"ProcessCommands begin ({e.Count})");
                    break;
                case EventKind.ProcessEnd:
                    sb.AppendLine($"ProcessCommands end ({e.Count})");
                    break;
                default:
                    if (e.Mismatch)
                        mismatches++;
                    if ((e.Rt0 == backbuffer || (toneAdjust != 0 && e.Rt0 == toneAdjust)) && !firstBackbuffer.ContainsKey(e.Frame))
                        firstBackbuffer[e.Frame] = i;
                    sb.AppendLine($"SetTarget draws={e.Draws,4} n={e.Count} RT0={Describe(e.Rt0)} RT1={Describe(e.Rt1)} RT2={Describe(e.Rt2)} DS={Describe(e.Ds)}"
                                  + (e.Mismatch ? " [TARGET CHANGED OUTSIDE SetTarget]" : "")
                                  + $" ctrlVP={e.ControlVp:F3} mainVP={e.MainVp:F3}");
                    break;
            }
        }

        sb.AppendLine();
        sb.AppendLine($"Threads (id: events): {string.Join(", ", threads.Select(t => $"{t.Key}: {t.Value}"))}");
        sb.AppendLine($"Framework thread: {Environment.CurrentManagedThreadId}");
        sb.AppendLine($"First SetTarget to backbuffer/toneAdjust per frame: {string.Join(", ", firstBackbuffer.Select(f => $"f{f.Key}@#{f.Value}"))}");
        sb.AppendLine($"Target changes outside SetTarget: {mismatches}");
        if (eventCount > MaxEvents)
            sb.AppendLine($"TRUNCATED: {eventCount} events");

        var path = Path.Combine(Plugin.PluginInterface.ConfigDirectory.FullName, $"rtrace-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, sb.ToString());
            Plugin.ChatGui.Print($"Render trace: {count} events, {threads.Count} thread(s), {mismatches} outside changes → {path}", "Matinee");
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Render trace: could not write {Path}", path);
        }

        Plugin.Log.Information("Render trace written to {Path}", path);
    }

    private enum EventKind : byte
    {
        SetTarget,
        ProcessBegin,
        ProcessEnd,
        Draw,
    }

    private struct Event
    {
        public EventKind Kind;
        public int Frame;
        public int Thread;
        public int Count;
        public int Draws;
        public bool Mismatch;
        public nint Rt0;
        public nint Rt1;
        public nint Rt2;
        public nint Ds;
        public Vector4 ControlVp;
        public Vector4 MainVp;
    }
}
