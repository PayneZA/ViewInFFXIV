using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace InView.Shared;

/// <summary>
/// Double-buffered BGRA frames in an NT memory-mapped section.
/// The host copies captured video here; the plugin uploads the latest slot to a Dalamud texture.
/// </summary>
public sealed unsafe class FrameBuffer : IDisposable
{
    public const int HeaderSize = 256;

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct Header
    {
        public uint Magic;
        public int Width;
        public int Height;
        public int Stride;
        public int WriteIndex;
        public uint Ready;
        public ulong Generation;
        public float Fps;
        public float CropLeft;
        public float CropTop;
        public float CropRight;
        public float CropBottom;
        public int AllocWidth;
        public int AllocHeight;
    }

    private readonly MemoryMappedFile file;
    private readonly MemoryMappedViewAccessor view;
    private readonly byte* basePtr;
    private readonly int slotBytes;
    private bool disposed;

    public int AllocWidth { get; }
    public int AllocHeight { get; }

    private FrameBuffer(MemoryMappedFile file, MemoryMappedViewAccessor view, byte* basePtr, int allocWidth, int allocHeight)
    {
        this.file = file;
        this.view = view;
        this.basePtr = basePtr;
        AllocWidth = allocWidth;
        AllocHeight = allocHeight;
        slotBytes = allocWidth * allocHeight * 4;
    }

    public static FrameBuffer Create(int allocWidth = IpcConstants.MaxWidth, int allocHeight = IpcConstants.MaxHeight)
    {
        var size = HeaderSize + (long)allocWidth * allocHeight * 4 * 2;
        var file = MemoryMappedFile.CreateOrOpen(IpcConstants.FrameMapName, size, MemoryMappedFileAccess.ReadWrite);
        var view = file.CreateViewAccessor(0, size, MemoryMappedFileAccess.ReadWrite);
        byte* ptr = null;
        view.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr);
        if (ptr == null)
        {
            view.Dispose();
            file.Dispose();
            throw new InvalidOperationException("Failed to map InView frame buffer.");
        }

        var header = (Header*)ptr;
        header->Magic = IpcConstants.FrameMagic;
        header->AllocWidth = allocWidth;
        header->AllocHeight = allocHeight;
        header->CropRight = 1f;
        header->CropBottom = 1f;
        return new FrameBuffer(file, view, ptr, allocWidth, allocHeight);
    }

    public static FrameBuffer? TryOpen()
    {
        try
        {
            var size = HeaderSize + (long)IpcConstants.MaxWidth * IpcConstants.MaxHeight * 4 * 2;
            var file = MemoryMappedFile.OpenExisting(IpcConstants.FrameMapName, MemoryMappedFileRights.ReadWrite);
            var view = file.CreateViewAccessor(0, size, MemoryMappedFileAccess.ReadWrite);
            byte* ptr = null;
            view.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr);
            if (ptr == null)
            {
                view.Dispose();
                file.Dispose();
                return null;
            }

            var header = (Header*)ptr;
            var w = header->AllocWidth > 0 ? header->AllocWidth : IpcConstants.MaxWidth;
            var h = header->AllocHeight > 0 ? header->AllocHeight : IpcConstants.MaxHeight;
            return new FrameBuffer(file, view, ptr, w, h);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }

    public Header ReadHeader()
    {
        ThrowIfDisposed();
        return Unsafe.Read<Header>(basePtr);
    }

    public void WriteFrame(int width, int height, int srcStride, ReadOnlySpan<byte> bgra, float fps, float cropL, float cropT, float cropR, float cropB)
    {
        ThrowIfDisposed();
        width = Math.Clamp(width, 1, AllocWidth);
        height = Math.Clamp(height, 1, AllocHeight);
        var header = (Header*)basePtr;
        var next = 1 - header->WriteIndex;
        if (next is not (0 or 1))
            next = 0;

        var dest = Slot(next);
        var destStride = AllocWidth * 4;
        var copyWidth = Math.Min(width, AllocWidth);
        var copyHeight = Math.Min(height, AllocHeight);
        var rowBytes = copyWidth * 4;
        for (var y = 0; y < copyHeight; y++)
        {
            var srcOff = y * srcStride;
            if (srcOff + rowBytes > bgra.Length)
                break;
            bgra.Slice(srcOff, rowBytes).CopyTo(new Span<byte>(dest + (y * destStride), rowBytes));
        }

        header->Width = copyWidth;
        header->Height = copyHeight;
        header->Stride = destStride;
        header->Fps = fps;
        header->CropLeft = cropL;
        header->CropTop = cropT;
        header->CropRight = cropR;
        header->CropBottom = cropB;
        header->Magic = IpcConstants.FrameMagic;
        header->AllocWidth = AllocWidth;
        header->AllocHeight = AllocHeight;
        Volatile.Write(ref header->WriteIndex, next);
        header->Generation++;
        Volatile.Write(ref header->Ready, 1u);
    }

    public bool TryCopyLatest(Span<byte> destPacked, out int width, out int height, out ulong generation, out float fps)
    {
        width = 0;
        height = 0;
        generation = 0;
        fps = 0;
        ThrowIfDisposed();
        var header = (Header*)basePtr;
        if (header->Magic != IpcConstants.FrameMagic || header->Ready == 0)
            return false;

        var gen1 = Volatile.Read(ref header->Generation);
        var index = Volatile.Read(ref header->WriteIndex);
        if (index is not (0 or 1))
            return false;

        width = header->Width;
        height = header->Height;
        fps = header->Fps;
        if (width <= 0 || height <= 0)
            return false;

        var srcStride = header->Stride > 0 ? header->Stride : AllocWidth * 4;
        var packedStride = width * 4;
        var needed = packedStride * height;
        if (destPacked.Length < needed)
            return false;

        var src = Slot(index);
        for (var y = 0; y < height; y++)
        {
            new ReadOnlySpan<byte>(src + (y * srcStride), packedStride)
                .CopyTo(destPacked.Slice(y * packedStride, packedStride));
        }

        var gen2 = Volatile.Read(ref header->Generation);
        generation = gen2;
        return gen1 == gen2;
    }

    public (float Left, float Top, float Right, float Bottom) ReadCrop()
    {
        var h = ReadHeader();
        return (h.CropLeft, h.CropTop, h.CropRight, h.CropBottom);
    }

    private byte* Slot(int index) => basePtr + HeaderSize + (index * slotBytes);

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        view.SafeMemoryMappedViewHandle.ReleasePointer();
        view.Dispose();
        file.Dispose();
    }
}
