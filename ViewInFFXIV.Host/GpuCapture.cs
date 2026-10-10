using System.Runtime.InteropServices;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using ViewInFFXIV.Shared;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;
using D3D11Device = SharpDX.Direct3D11.Device;

namespace ViewInFFXIV.Host;

/// <summary>
/// Captures the off-screen Brave window with Windows.Graphics.Capture and copies the video area, on the GPU,
/// into a 1920x1080 texture shared with the game (NT handle + keyed mutex). No pixels touch the CPU.
/// Keyed mutex protocol: the helper acquires key 0 and releases key 1 when a frame is ready; the plugin
/// acquires key 1, copies it into its own texture, and releases key 0. An unread frame is simply skipped.
/// </summary>
internal sealed class GpuCapture : IDisposable
{
    private const long WriterKey = 0;
    private const long ReaderKey = 1;

    // Room for one frame being copied, one waiting and one Windows is writing. The pool size does not change the
    // delivery rate: Windows.Graphics.Capture delivers about 45-48 frames/s at most (docs/renderer.md, section 9).
    private const int PoolBuffers = 3;
    private static readonly Guid Texture2DGuid = typeof(Texture2D).GUID;

    private readonly D3D11Device device;
    private readonly IDirect3DDevice winrtDevice;
    private readonly Texture2D shared;
    private readonly KeyedMutex sharedMutex;
    private readonly RenderTargetView sharedTarget;
    private readonly IntPtr sharedHandle;
    private readonly object sync = new();
    private GraphicsCaptureItem? item;
    private Direct3D11CaptureFramePool? pool;
    private GraphicsCaptureSession? session;
    private Windows.Graphics.SizeInt32 poolSize;
    private int framesThisSecond;
    private DateTime fpsAnchor = DateTime.UtcNow;
    private DateTime lastFrameUtc;
    private float fps;
    private ulong rateArrived;
    private ulong rateSkipped;
    private DateTime rateAnchor = DateTime.UtcNow;
    private DateTime lastArrivedUtc;
    private float arrivedFps;
    private float skippedFps;
    private bool disposed;

    private static int instance;

    public GpuCapture(long adapterLuid)
    {
        device = CreateDevice(adapterLuid, out var adapterName, out var matched);
        AdapterName = adapterName;
        AdapterMatched = matched;

        using (var dxgiDevice = device.QueryInterface<SharpDX.DXGI.Device>())
        {
            Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice.NativePointer, out var inspectable));
            winrtDevice = MarshalInterface<IDirect3DDevice>.FromAbi(inspectable);
            Marshal.Release(inspectable);
        }

        shared = new Texture2D(device, new Texture2DDescription
        {
            Width = IpcConstants.MaxWidth,
            Height = IpcConstants.MaxHeight,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget,
            OptionFlags = ResourceOptionFlags.SharedKeyedmutex | ResourceOptionFlags.SharedNthandle,
        });
        sharedMutex = shared.QueryInterface<KeyedMutex>();
        sharedTarget = new RenderTargetView(device, shared);

        // A fresh name per texture, so the plugin knows to reopen after a helper restart.
        SharedName = $"{IpcConstants.SharedTexturePrefix}.{Environment.ProcessId}.{Interlocked.Increment(ref instance)}";
        using var resource = shared.QueryInterface<SharpDX.DXGI.Resource1>();
        sharedHandle = resource.CreateSharedHandle(SharedName, SharedResourceFlags.Read | SharedResourceFlags.Write, null);

        // Start black.
        if (sharedMutex.Acquire(WriterKey, 1000).Code == 0)
        {
            device.ImmediateContext.ClearRenderTargetView(sharedTarget, new SharpDX.Mathematics.Interop.RawColor4(0, 0, 0, 1));
            sharedMutex.Release(WriterKey);
        }
    }

    /// <summary>Kernel object name the plugin opens the shared texture by.</summary>
    public string SharedName { get; }

    public string AdapterName { get; }

    /// <summary>False when the game's GPU was not found; the game then cannot open the shared texture.</summary>
    public bool AdapterMatched { get; }

    /// <summary>Frames per second written for the game; 0 once frames stop (Brave does not repaint a paused video).</summary>
    public float Fps => DateTime.UtcNow - lastFrameUtc > TimeSpan.FromSeconds(2) ? 0 : fps;

    public ulong Frames { get; private set; }

    /// <summary>Frames Windows delivered / frames skipped because the game had not taken the previous one yet.</summary>
    public ulong Arrived { get; private set; }

    public ulong Skipped { get; private set; }

    /// <summary>Frames per second Windows delivers / drops because the game still held the previous one.</summary>
    public float ArrivedFps => DateTime.UtcNow - lastArrivedUtc > TimeSpan.FromSeconds(2) ? 0 : arrivedFps;

    public float SkippedFps => DateTime.UtcNow - lastArrivedUtc > TimeSpan.FromSeconds(2) ? 0 : skippedFps;

    public string? LastError { get; private set; }

    /// <summary>Normalised window area to copy (the video player); null copies the whole window.</summary>
    public CropRect? Crop { get; set; }

    public IntPtr Window { get; private set; }

    public void Start(IntPtr hwnd)
    {
        lock (sync)
        {
            StopLocked();
            Window = hwnd;
            item = CreateItemForWindow(hwnd);
            poolSize = item.Size;
            pool = Direct3D11CaptureFramePool.CreateFreeThreaded(winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, PoolBuffers, poolSize);
            pool.FrameArrived += OnFrameArrived;
            session = pool.CreateCaptureSession(item);
            try
            {
                session.IsCursorCaptureEnabled = false;
            }
            catch
            {
                // older Windows 10 builds
            }

            session.StartCapture();
            LastError = null;
        }
    }

    public void Stop()
    {
        lock (sync)
            StopLocked();
    }

    private void StopLocked()
    {
        session?.Dispose();
        session = null;
        if (pool != null)
        {
            pool.FrameArrived -= OnFrameArrived;
            pool.Dispose();
            pool = null;
        }

        item = null;
        Window = IntPtr.Zero;
        fps = 0;
    }

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        lock (sync)
        {
            if (disposed || sender != pool)
                return;

            try
            {
                // Take everything that is waiting and keep only the newest, so the pool always has a free buffer
                // for Windows to write the next frame into.
                using var frame = TakeNewest(sender);
                if (frame == null)
                    return;
                var content = frame.ContentSize;
                if (content.Width != poolSize.Width || content.Height != poolSize.Height)
                {
                    // The window was resized (e.g. fitted to 1080p): new buffers from the next frame on.
                    poolSize = content;
                    sender.Recreate(winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, PoolBuffers, content);
                }

                // Only write when the plugin took the last frame; otherwise drop this one.
                if (sharedMutex.Acquire(WriterKey, 0).Code != 0)
                {
                    Skipped++;
                    return;
                }

                try
                {
                    using var source = GetTexture(frame.Surface);
                    CopyVideo(source, content.Width, content.Height);
                }
                finally
                {
                    sharedMutex.Release(ReaderKey);
                }

                Frames++;
                TickFps();
                LastError = null;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
            }
        }
    }

    private Direct3D11CaptureFrame? TakeNewest(Direct3D11CaptureFramePool pool)
    {
        Direct3D11CaptureFrame? newest = null;
        while (pool.TryGetNextFrame() is { } next)
        {
            newest?.Dispose();
            newest = next;
            Arrived++;
            TickRates();
        }

        return newest;
    }

    /// <summary>Copies the cropped video 1:1 into the shared texture, centred; letterboxes when it is smaller.</summary>
    private void CopyVideo(Texture2D source, int width, int height)
    {
        var crop = Crop;
        int left = 0, top = 0, right = width, bottom = height;
        if (crop != null)
        {
            left = Math.Clamp((int)MathF.Round(crop.Left * width), 0, width - 1);
            top = Math.Clamp((int)MathF.Round(crop.Top * height), 0, height - 1);
            right = Math.Clamp((int)MathF.Round(crop.Right * width), left + 1, width);
            bottom = Math.Clamp((int)MathF.Round(crop.Bottom * height), top + 1, height);
        }

        // Larger than the texture: keep the centre. Smaller: centre it on black.
        var copyW = Math.Min(right - left, IpcConstants.MaxWidth);
        var copyH = Math.Min(bottom - top, IpcConstants.MaxHeight);
        left += ((right - left) - copyW) / 2;
        top += ((bottom - top) - copyH) / 2;
        var destX = (IpcConstants.MaxWidth - copyW) / 2;
        var destY = (IpcConstants.MaxHeight - copyH) / 2;

        var context = device.ImmediateContext;
        if (copyW < IpcConstants.MaxWidth || copyH < IpcConstants.MaxHeight)
            context.ClearRenderTargetView(sharedTarget, new SharpDX.Mathematics.Interop.RawColor4(0, 0, 0, 1));
        context.CopySubresourceRegion(
            source, 0,
            new ResourceRegion(left, top, 0, left + copyW, top + copyH, 1),
            shared, 0, destX, destY);
    }

    private void TickFps()
    {
        framesThisSecond++;
        var now = DateTime.UtcNow;
        lastFrameUtc = now;
        var elapsed = (now - fpsAnchor).TotalSeconds;
        if (elapsed < 1)
            return;
        fps = (float)(framesThisSecond / elapsed);
        framesThisSecond = 0;
        fpsAnchor = now;
    }

    private void TickRates()
    {
        var now = DateTime.UtcNow;
        lastArrivedUtc = now;
        var elapsed = (now - rateAnchor).TotalSeconds;
        if (elapsed < 1)
            return;
        arrivedFps = (float)((Arrived - rateArrived) / elapsed);
        skippedFps = (float)((Skipped - rateSkipped) / elapsed);
        rateArrived = Arrived;
        rateSkipped = Skipped;
        rateAnchor = now;
    }

    // The two capture interop interfaces are classic COM; call them through their vtables rather than
    // CsWinRT casts (which reject them). Both methods sit in slot 3, right after IUnknown.
    private static readonly Guid DxgiInterfaceAccessGuid = new("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1");
    private static readonly Guid CaptureItemInteropGuid = new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");

    // IGraphicsCaptureItem (the WinRT interface, not the projected class).
    private static readonly Guid GraphicsCaptureItemGuid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");

    private static unsafe Texture2D GetTexture(IDirect3DSurface surface)
    {
        var abi = ((IWinRTObject)surface).NativeObject.ThisPtr;
        var accessGuid = DxgiInterfaceAccessGuid;
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(abi, in accessGuid, out var access));
        try
        {
            var textureGuid = Texture2DGuid;
            IntPtr texture;
            var getInterface = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)(*(*(void***)access + 3));
            Marshal.ThrowExceptionForHR(getInterface(access, &textureGuid, &texture));
            return new Texture2D(texture);
        }
        finally
        {
            Marshal.Release(access);
        }
    }

    private static unsafe GraphicsCaptureItem CreateItemForWindow(IntPtr hwnd)
    {
        const string className = "Windows.Graphics.Capture.GraphicsCaptureItem";
        Marshal.ThrowExceptionForHR(WindowsCreateString(className, className.Length, out var hstring));
        try
        {
            var interopGuid = CaptureItemInteropGuid;
            Marshal.ThrowExceptionForHR(RoGetActivationFactory(hstring, in interopGuid, out var interop));
            try
            {
                var itemGuid = GraphicsCaptureItemGuid;
                IntPtr item;
                var createForWindow = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, Guid*, IntPtr*, int>)(*(*(void***)interop + 3));
                Marshal.ThrowExceptionForHR(createForWindow(interop, hwnd, &itemGuid, &item));
                try
                {
                    return GraphicsCaptureItem.FromAbi(item);
                }
                finally
                {
                    Marshal.Release(item);
                }
            }
            finally
            {
                Marshal.Release(interop);
            }
        }
        finally
        {
            WindowsDeleteString(hstring);
        }
    }

    /// <summary>The game's GPU (by LUID) so the texture can be shared; laptops often have two.</summary>
    private static D3D11Device CreateDevice(long adapterLuid, out string adapterName, out bool matched)
    {
        matched = true;
        using var factory = new Factory1();
        foreach (var adapter in factory.Adapters1)
        {
            using (adapter)
            {
                if (adapterLuid != 0 && adapter.Description1.Luid != adapterLuid)
                    continue;
                adapterName = adapter.Description1.Description;
                return new D3D11Device(adapter, DeviceCreationFlags.BgraSupport, FeatureLevel.Level_11_0);
            }
        }

        adapterName = "default";
        matched = adapterLuid == 0;
        return new D3D11Device(DriverType.Hardware, DeviceCreationFlags.BgraSupport, FeatureLevel.Level_11_0);
    }

    public void Dispose()
    {
        lock (sync)
        {
            if (disposed)
                return;
            disposed = true;
            StopLocked();
        }

        sharedTarget.Dispose();
        sharedMutex.Dispose();
        CloseHandle(sharedHandle);
        shared.Dispose();
        winrtDevice.Dispose();
        device.Dispose();
    }

    [DllImport("combase.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int WindowsCreateString(string sourceString, int length, out IntPtr hstring);

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int WindowsDeleteString(IntPtr hstring);

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int RoGetActivationFactory(IntPtr activatableClassId, in Guid iid, out IntPtr factory);

    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
}
