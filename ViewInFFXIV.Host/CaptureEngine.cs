using System.Runtime.InteropServices;
using ViewInFFXIV.Shared;

namespace ViewInFFXIV.Host;

internal sealed class CaptureEngine : IDisposable
{
    [DllImport("user32.dll")]
    private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, uint nFlags);

    private const uint PwRenderFullContent = 2;

    private readonly FrameBuffer frames;
    private int capturing;
    private int framesThisSecond;
    private int lastFps;
    private DateTime fpsAnchor = DateTime.UtcNow;
    private Bitmap? printNative;
    private Bitmap? printOutput;

    public string Mode { get; } = "print";
    public ulong SharedHandle { get; }
    public string? LastError { get; private set; }
    public bool IsBusy => Volatile.Read(ref capturing) != 0;

    public CaptureEngine(FrameBuffer frames)
    {
        this.frames = frames;
    }

    public bool TryTick(IntPtr hwnd, int width, int height)
    {
        if (hwnd == IntPtr.Zero || width < 2 || height < 2)
            return false;

        if (Interlocked.CompareExchange(ref capturing, 1, 0) != 0)
            return false;

        try
        {
            TickFps();
            CapturePrintWindow(hwnd, width, height);
            LastError = null;
            return true;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            return false;
        }
        finally
        {
            Volatile.Write(ref capturing, 0);
        }
    }

    private unsafe void CapturePrintWindow(IntPtr hwnd, int width, int height)
    {
        // PrintWindow does not scale — bitmap must match HWND size. Contain-scale happens in ScalePrintNativeToOutput.
        var native = CaptureFit.FitInside(width, height, IpcConstants.MaxNativeWidth, IpcConstants.MaxNativeHeight);
        EnsurePrintBitmaps(native.Width, native.Height);
        using (var g = Graphics.FromImage(printNative!))
        {
            var hdc = g.GetHdc();
            try
            {
                PrintWindow(hwnd, hdc, PwRenderFullContent);
            }
            finally
            {
                g.ReleaseHdc(hdc);
            }
        }

        ScalePrintNativeToOutput();
    }

    private void EnsurePrintBitmaps(int nativeW, int nativeH)
    {
        if (printNative == null || printNative.Width != nativeW || printNative.Height != nativeH)
        {
            printNative?.Dispose();
            printNative = new Bitmap(nativeW, nativeH, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        }

        if (printOutput == null
            || printOutput.Width != IpcConstants.MaxWidth
            || printOutput.Height != IpcConstants.MaxHeight)
        {
            printOutput?.Dispose();
            printOutput = new Bitmap(
                IpcConstants.MaxWidth,
                IpcConstants.MaxHeight,
                System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        }
    }

    private unsafe void ScalePrintNativeToOutput()
    {
        var dest = CaptureFit.DestRect(printNative!.Width, printNative.Height, IpcConstants.MaxWidth, IpcConstants.MaxHeight);
        using (var g = Graphics.FromImage(printOutput!))
        {
            g.Clear(Color.Black);
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.Bilinear;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighSpeed;
            g.DrawImage(printNative, new Rectangle(dest.X, dest.Y, dest.Width, dest.Height));
        }

        var data = printOutput!.LockBits(
            new Rectangle(0, 0, IpcConstants.MaxWidth, IpcConstants.MaxHeight),
            System.Drawing.Imaging.ImageLockMode.ReadOnly,
            System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            var span = new ReadOnlySpan<byte>((void*)data.Scan0, data.Stride * IpcConstants.MaxHeight);
            frames.WriteFrame(IpcConstants.MaxWidth, IpcConstants.MaxHeight, data.Stride, span, lastFps, 0f, 0f, 1f, 1f);
        }
        finally
        {
            printOutput.UnlockBits(data);
        }
    }

    private void TickFps()
    {
        framesThisSecond++;
        var now = DateTime.UtcNow;
        if ((now - fpsAnchor).TotalSeconds < 1)
            return;
        lastFps = framesThisSecond;
        framesThisSecond = 0;
        fpsAnchor = now;
    }

    public void Dispose()
    {
        printNative?.Dispose();
        printOutput?.Dispose();
    }
}
