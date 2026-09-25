using System.Runtime.InteropServices;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Services;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using D3D11Device = SharpDX.Direct3D11.Device;

namespace ViewInFFXIV.Host;

/// <summary>
/// Receives the helper's video on the GPU: opens its named shared texture on the game's own D3D11 device and,
/// whenever a new frame is ready (keyed mutex key 1), copies it into a texture the game can sample.
/// Must be driven from the Draw callback, where the game's immediate context may be used.
/// </summary>
public sealed class SharedVideo : IDisposable
{
    private const long WriterKey = 0;
    private const long ReaderKey = 1;
    private const int WaitAbandoned = 0x80;

    private readonly IPluginLog log;
    private readonly D3D11Device device;
    private readonly SharpDX.Direct3D11.Device1 device1;
    private readonly DeviceContext context;
    private string openName = "";
    private Texture2D? shared;
    private KeyedMutex? mutex;
    private Texture2D? local;
    private ShaderResourceView? view;
    private int framesThisSecond;
    private DateTime fpsAnchor = DateTime.UtcNow;

    public SharedVideo(nint gameDevice, IPluginLog log)
    {
        this.log = log;
        // Our own reference to the game's device, released on Dispose.
        Marshal.AddRef(gameDevice);
        device = new D3D11Device(gameDevice);
        device1 = device.QueryInterface<SharpDX.Direct3D11.Device1>();
        context = device.ImmediateContext;

        using var dxgiDevice = device.QueryInterface<SharpDX.DXGI.Device>();
        using var adapter = dxgiDevice.Adapter;
        AdapterLuid = adapter.Description.Luid;
    }

    /// <summary>The game's GPU; the helper must capture on the same one to share textures.</summary>
    public long AdapterLuid { get; }

    /// <summary>Texture to draw, or null while there is no video.</summary>
    public ImTextureID? Handle => view == null ? null : new ImTextureID(view.NativePointer);

    public int Width { get; private set; }

    public int Height { get; private set; }

    /// <summary>New frames received per second (what actually reaches the wall).</summary>
    public float Fps { get; private set; }

    public ulong Frames { get; private set; }

    /// <summary>Call once per Draw with the helper's current shared texture name ("" when not capturing).</summary>
    public void Update(string sharedName)
    {
        if (sharedName != openName)
            Open(sharedName);
        if (mutex == null || shared == null || local == null)
            return;

        try
        {
            var result = mutex.Acquire(ReaderKey, 0).Code;
            if (result == WaitAbandoned)
            {
                // The helper died mid-frame; the next status names a fresh texture.
                Close();
                return;
            }

            if (result != 0)
            {
                // No new frame yet; let the fps readout drop to 0 when the video stops.
                if ((DateTime.UtcNow - fpsAnchor).TotalSeconds > 2)
                {
                    Fps = 0;
                    framesThisSecond = 0;
                    fpsAnchor = DateTime.UtcNow;
                }

                return;
            }

            context.CopyResource(shared, local);
            mutex.Release(WriterKey);
            Frames++;
            TickFps();
        }
        catch (Exception ex)
        {
            log.Debug(ex, "ViewInFFXIV shared video update failed");
            Close();
        }
    }

    private void Open(string name)
    {
        Close();
        openName = name;
        if (name.Length == 0)
            return;

        try
        {
            shared = device1.OpenSharedResource1<Texture2D>(name, SharedResourceFlags.Read | SharedResourceFlags.Write);
            mutex = shared.QueryInterface<KeyedMutex>();
            var desc = shared.Description;
            desc.OptionFlags = ResourceOptionFlags.None;
            desc.BindFlags = BindFlags.ShaderResource;
            local = new Texture2D(device, desc);
            view = new ShaderResourceView(device, local);
            Width = desc.Width;
            Height = desc.Height;
            log.Information("ViewInFFXIV video texture opened: {Name} {Width}x{Height}", name, Width, Height);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Could not open the helper's video texture {Name}", name);
            Close();
            openName = name; // do not retry every frame; a new name comes with a new texture
        }
    }

    private void Close()
    {
        view?.Dispose();
        view = null;
        local?.Dispose();
        local = null;
        mutex?.Dispose();
        mutex = null;
        shared?.Dispose();
        shared = null;
        openName = "";
        Width = Height = 0;
        Fps = 0;
    }

    private void TickFps()
    {
        framesThisSecond++;
        var now = DateTime.UtcNow;
        var elapsed = (now - fpsAnchor).TotalSeconds;
        if (elapsed < 1)
            return;
        Fps = (float)(framesThisSecond / elapsed);
        framesThisSecond = 0;
        fpsAnchor = now;
    }

    public void Dispose()
    {
        Close();
        context.Dispose();
        device1.Dispose();
        device.Dispose();
    }
}
