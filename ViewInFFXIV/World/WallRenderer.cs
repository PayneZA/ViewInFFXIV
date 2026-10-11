using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using D3D11Device = SharpDX.Direct3D11.Device;
using KernelDevice = FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.Device;
using KernelTexture = FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.Texture;

namespace ViewInFFXIV.World;

/// <summary>What the wall screen looks like this frame, published from the Draw thread to whichever pass draws it.</summary>
public sealed class WallFrame
{
    public required Vector3 Center { get; init; }

    /// <summary>From the centre to the right edge of the picture.</summary>
    public required Vector3 HalfRight { get; init; }

    /// <summary>From the centre to the bottom edge of the picture.</summary>
    public required Vector3 HalfDown { get; init; }

    /// <summary>Scenery (walls, furniture) up to this many metres in front of the screen is covered by it.</summary>
    public required float CoverDepth { get; init; }

    /// <summary>Name plates and housing item markers do not show over the screen (in-world drawing only).</summary>
    public bool HideWorldUi { get; init; }

    /// <summary>Do not test against scene depth: the screen shows over everything (setting for graphics cards that hide it).</summary>
    public bool IgnoreDepth { get; init; }

    /// <summary>The video, or null for a switched-off screen. Owned by <see cref="ScreenRenderer"/>.</summary>
    public ShaderResourceView? Video { get; init; }

    /// <summary>Render-thread frame number this was published at (see <see cref="FrameInjector"/>).</summary>
    public long Stamp { get; init; }
}

/// <summary>
/// Draws the wall screen straight into one of the game's render targets: one quad, bezel and picture in a single
/// draw, hidden by scene depth. Shaders and states are shared; each thread that draws owns a <see cref="Pass"/>.
/// Everything is recorded on a deferred context and run with RestoreContextState, so the game's cached GPU state
/// is untouched.
/// </summary>
/// <remarks>
/// <para>
/// Why a deferred context: the game keeps its own cache of what is bound on the immediate context
/// (<c>Kernel.ImmediateContext</c>: shaders, blend, depth state…). Binding things directly would leave that cache
/// wrong and the game would draw with our state. <c>ExecuteCommandList(…, restoreContextState: true)</c> puts every
/// binding back exactly as it was, so to the game our draw never happened.
/// </para>
/// <para>
/// Game textures are borrowed by pointer and wrapped with views of our own (<see cref="Borrow{T}"/> takes a COM
/// reference). The game recreates them on resize or settings changes, so every cache below is keyed on the
/// texture pointer and rebuilt when it changes.
/// </para>
/// </remarks>
public sealed unsafe class WallRenderer : IDisposable
{
    /// <summary>Bezel thickness as a fraction of the picture width.</summary>
    private const float BezelFraction = 0.018f;

    private readonly D3D11Device device;
    private readonly DeviceContext immediate;
    private readonly VertexShader vertexShader;
    private readonly PixelShader pixelShader;
    private readonly SamplerState sampler;
    private readonly BlendState blend;
    private readonly RasterizerState rasterizer;
    private readonly DepthStencilState noDepth;
    private readonly DepthStencilState writeDepth;
    private VertexShader? probeVertex;
    private PixelShader? probePixel;

    public WallRenderer()
    {
        // GAME ASSUMPTION A9: the game's ID3D11Device (behind its forwarder), whose immediate context the render
        // thread uses for ProcessCommands.
        var gameDevice = (nint)KernelDevice.Instance()->D3D11Forwarder;
        if (gameDevice == 0)
            throw new InvalidOperationException("The game's D3D11 device is not available.");

        device = Borrow<D3D11Device>(gameDevice);
        immediate = device.ImmediateContext;
        vertexShader = new VertexShader(device, LoadShader("Wall.vs.cso"));
        pixelShader = new PixelShader(device, LoadShader("Wall.ps.cso"));

        sampler = new SamplerState(device, new SamplerStateDescription
        {
            Filter = Filter.Anisotropic,
            MaximumAnisotropy = 16,
            AddressU = TextureAddressMode.Clamp,
            AddressV = TextureAddressMode.Clamp,
            AddressW = TextureAddressMode.Clamp,
            ComparisonFunction = Comparison.Never,
            MaximumLod = float.MaxValue,
        });

        var blendDesc = BlendStateDescription.Default();
        blendDesc.RenderTarget[0].IsBlendEnabled = true;
        blendDesc.RenderTarget[0].SourceBlend = BlendOption.SourceAlpha;
        blendDesc.RenderTarget[0].DestinationBlend = BlendOption.InverseSourceAlpha;
        blendDesc.RenderTarget[0].BlendOperation = BlendOperation.Add;
        blendDesc.RenderTarget[0].SourceAlphaBlend = BlendOption.Zero;
        blendDesc.RenderTarget[0].DestinationAlphaBlend = BlendOption.One;
        blendDesc.RenderTarget[0].AlphaBlendOperation = BlendOperation.Add;
        // Leave the target's alpha alone: the game may still read it.
        blendDesc.RenderTarget[0].RenderTargetWriteMask = ColorWriteMaskFlags.Red | ColorWriteMaskFlags.Green | ColorWriteMaskFlags.Blue;
        blend = new BlendState(device, blendDesc);

        var rasterDesc = RasterizerStateDescription.Default();
        rasterDesc.CullMode = CullMode.None;
        rasterDesc.IsDepthClipEnabled = true;
        rasterizer = new RasterizerState(device, rasterDesc);

        var depthDesc = DepthStencilStateDescription.Default();
        depthDesc.IsDepthEnabled = false;
        depthDesc.DepthWriteMask = DepthWriteMask.Zero;
        depthDesc.IsStencilEnabled = false;
        noDepth = new DepthStencilState(device, depthDesc);

        // Writes the screen's own depth (no test: the pixel shader already decided what shows), stencil untouched.
        var writeDesc = DepthStencilStateDescription.Default();
        writeDesc.IsDepthEnabled = true;
        writeDesc.DepthComparison = Comparison.Always;
        writeDesc.DepthWriteMask = DepthWriteMask.All;
        writeDesc.IsStencilEnabled = false;
        writeDepth = new DepthStencilState(device, writeDesc);
    }

    /// <summary>Our reference to the game's device, for wrapping video views.</summary>
    public D3D11Device Device => device;

    public Pass CreatePass() => new(this);

    /// <summary>The /matinee rprobe shaders, created the first time a probe runs (render thread).</summary>
    private (VertexShader Vertex, PixelShader Pixel) ProbeShaders()
    {
        probeVertex ??= new VertexShader(device, LoadShader("Probe.vs.cso"));
        probePixel ??= new PixelShader(device, LoadShader("Probe.ps.cso"));
        return (probeVertex, probePixel);
    }

    public void Dispose()
    {
        probePixel?.Dispose();
        probeVertex?.Dispose();
        noDepth.Dispose();
        writeDepth.Dispose();
        rasterizer.Dispose();
        blend.Dispose();
        sampler.Dispose();
        pixelShader.Dispose();
        vertexShader.Dispose();
        immediate.Dispose();
        device.Dispose();
    }

    /// <summary>Wraps a COM pointer we do not own, taking our own reference (released when the wrapper is disposed).</summary>
    public static T Borrow<T>(nint pointer)
        where T : SharpDX.ComObject
    {
        Marshal.AddRef(pointer);
        return (T)Activator.CreateInstance(typeof(T), pointer)!;
    }

    private static byte[] LoadShader(string name)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resource = assembly.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith(name, StringComparison.Ordinal))
                       ?? throw new InvalidOperationException($"Shader {name} is not embedded.");
        using var stream = assembly.GetManifestResourceStream(resource)!;
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Constants
    {
        public Matrix4x4 ViewProj;
        public Vector3 Center;
        public float NearPlane;
        public Vector3 AxisRight;
        public float WorldTolerance;
        public Vector3 AxisDown;
        public float HasVideo;
        public Vector2 OuterHalf;
        public Vector2 InnerHalf;
        public Vector2 DepthScale;
        public Vector2 DepthMax;
        public float OuterRadius;
        public float InnerRadius;
        public float UseDepth;
        public float Opacity;
        public Vector2 InfoScale;
        public Vector2 InfoMax;
        public float CharacterTolerance;
        public float UseInfo;
        public float DebugView;
        public float HideWorldUi;
        public Matrix4x4 InvViewProj;
        public Vector2 TargetSize;
        public Vector2 Padding2;
        public Vector3 CameraPos;
        public float Padding3;
    }

    /// <summary>
    /// One drawing thread's resources: deferred context, constants and views onto the game's textures (cached by
    /// texture pointer; the game recreates them on resize). Use a pass from one thread only.
    /// </summary>
    public sealed class Pass : IDisposable
    {
        /// <summary>How far a character may be "in front" and still not hide the screen (depth noise).</summary>
        private const float CharacterTolerance = 0.05f;

        /// <summary>Debug (/matinee rdebug): ignore scene depth.</summary>
        public static bool IgnoreDepth;

        /// <summary>Debug (/matinee rdebug info): paint what the occlusion test sees.</summary>
        public static bool ShowInfo;

        /// <summary>Debug (/matinee rdebug depth): paint what the occlusion test reads from the scene depth.</summary>
        public static bool ShowDepth;

        /// <summary>Debug (/matinee rdebug nocopy): read the game's live depth, no copy of it (the screen then writes no depth).</summary>
        public static bool NoDepthCopy;

        /// <summary>/matinee rprobe: take one exact reading of the scene depth and its copy, then report it in chat.</summary>
        public static volatile bool ProbeArmed;

        private static string? probeResult;

        /// <summary>The scene depth the game itself calls its own (RenderTargetManager.DepthStencil); set by the caller before Draw.</summary>
        public KernelTexture* SceneDepth;

        private const int ProbeWidth = 64;
        private const int ProbeHeight = 36;

        private Texture2D? probeTarget;
        private RenderTargetView? probeView;
        private Texture2D? probeStage;
        private string probeHeader = "";
        private Vector2 probeCenterTexel;
        private float probeExpected;
        private int probeWaited;

        /// <summary>The finished probe report, once (null while there is none).</summary>
        public static string? TakeProbeResult() => Interlocked.Exchange(ref probeResult, null);

        private readonly WallRenderer owner;
        private readonly DeviceContext context;
        private readonly SharpDX.Direct3D11.Buffer constants;
        private readonly Diagnostics.GpuStopwatch gpu;
        private nint targetTexture;
        private RenderTargetView? targetView;
        private nint depthTexture;
        private Texture2D? depthResource;
        private Format depthReadFormat;
        private ShaderResourceView? depthView;
        private Texture2D? depthCopy;
        private ShaderResourceView? depthCopyView;
        private DepthStencilView? depthWriteView;
        private bool depthFailed;
        private nint infoPointer;
        private ShaderResourceView? infoView;

        internal Pass(WallRenderer owner)
        {
            this.owner = owner;
            context = new DeviceContext(owner.device);
            gpu = new Diagnostics.GpuStopwatch(owner.device, owner.immediate);
            constants = new SharpDX.Direct3D11.Buffer(owner.device, new BufferDescription
            {
                SizeInBytes = (Marshal.SizeOf<Constants>() + 15) & ~15,
                Usage = ResourceUsage.Dynamic,
                BindFlags = BindFlags.ConstantBuffer,
                CpuAccessFlags = CpuAccessFlags.Write,
            });
        }

        /// <summary>Why the last draw drew nothing, for diagnostics.</summary>
        public string? LastProblem { get; private set; }

        /// <summary>Average GPU milliseconds per draw since the last call (read on the drawing thread's schedule).</summary>
        public double TakeGpuMilliseconds() => gpu.TakeAverage();

        /// <summary>The scene depth texture as last seen (format, size, bind flags), for /matinee rstat.</summary>
        public string DepthInfo { get; private set; } = "none";

        /// <summary>Whether the last draw could test against scene depth.</summary>
        public bool Occluding => depthView != null;

        /// <summary>
        /// Draws <paramref name="frame"/> into <paramref name="target"/> with the given camera, hidden by
        /// <paramref name="depth"/> (may be null: no occlusion). With <paramref name="writeDepth"/>, the screen also
        /// writes itself into that depth buffer, so whatever the game draws afterwards with depth testing (name plates,
        /// housing markers) is hidden behind it like behind a real object.
        /// </summary>
        /// <remarks>
        /// GPU work, in order: (1) with <paramref name="writeDepth"/>, copy the scene depth (the shader cannot read a
        /// depth buffer it is writing); (2) one 4-vertex draw. <paramref name="info"/> is G-buffer 3 (A7); its size
        /// may differ from the target's, hence the separate scale factors.
        /// </remarks>
        public bool Draw(WallFrame frame, KernelTexture* target, KernelTexture* depth, KernelTexture* info, in Matrix4x4 viewProj, Vector3 cameraPos, float nearPlane, bool writeDepth)
        {
            if (target == null || target->D3D11Texture2D == null || target->ActualWidth == 0 || target->ActualHeight == 0)
            {
                LastProblem = "no render target";
                return false;
            }

            var rtv = TargetView((nint)target->D3D11Texture2D);
            if (rtv == null)
                return false;
            var liveDepth = DepthView(depth);
            if (probeStage != null)
                ReadProbe();
            var sceneDepth = IgnoreDepth || frame.IgnoreDepth ? null : liveDepth;
            DepthStencilView? depthOut = null;
            if (sceneDepth != null && writeDepth && !NoDepthCopy && DepthWriteViews() is { } views)
            {
                // The screen reads the scene's depth and writes its own into the same buffer: read from a copy.
                // About 15 MB at 1440p, a few hundredths of a millisecond on the GPU (measured with rstat).
                context.CopyResource(depthResource, depthCopy);
                sceneDepth = views.Read;
                depthOut = views.Write;
            }

            if (ProbeArmed && probeStage == null)
                StartProbe(frame, target, depth, viewProj, nearPlane);

            var infoView = sceneDepth != null ? InfoView(info) : null;

            var width = target->ActualWidth;
            var height = target->ActualHeight;
            var innerHalf = new Vector2(frame.HalfRight.Length(), frame.HalfDown.Length());
            if (innerHalf.X < 1e-3f || innerHalf.Y < 1e-3f)
            {
                LastProblem = "screen has no size";
                return false;
            }

            var bezel = innerHalf.X * 2f * BezelFraction;
            var outerHalf = innerHalf + new Vector2(bezel);
            var c = new Constants
            {
                ViewProj = viewProj,
                Center = frame.Center,
                NearPlane = nearPlane > 0 ? nearPlane : 0.1f,
                AxisRight = Vector3.Normalize(frame.HalfRight) * outerHalf.X,
                WorldTolerance = frame.CoverDepth,
                CharacterTolerance = CharacterTolerance,
                AxisDown = Vector3.Normalize(frame.HalfDown) * outerHalf.Y,
                HasVideo = frame.Video != null ? 1f : 0f,
                OuterHalf = outerHalf,
                InnerHalf = innerHalf,
                OuterRadius = bezel * 1.4f,
                InnerRadius = bezel * 0.35f,
                UseDepth = sceneDepth != null ? 1f : 0f,
                HideWorldUi = frame.HideWorldUi ? 1f : 0f,
                Opacity = 1f,
                TargetSize = new Vector2(width, height),
                CameraPos = cameraPos,
            };
            if (Matrix4x4.Invert(viewProj, out var inverse))
                c.InvViewProj = inverse;
            if (sceneDepth != null)
            {
                c.DepthScale = new Vector2((float)depth->ActualWidth / width, (float)depth->ActualHeight / height);
                c.DepthMax = new Vector2(depth->ActualWidth - 1, depth->ActualHeight - 1);
            }

            if (infoView != null)
            {
                c.UseInfo = 1f;
                c.DebugView = ShowInfo ? 1f : 0f;
                c.InfoScale = new Vector2((float)info->ActualWidth / width, (float)info->ActualHeight / height);
                c.InfoMax = new Vector2(info->ActualWidth - 1, info->ActualHeight - 1);
            }

            if (ShowDepth && sceneDepth != null)
                c.DebugView = 2f;

            var box = context.MapSubresource(constants, 0, MapMode.WriteDiscard, SharpDX.Direct3D11.MapFlags.None);
            Marshal.StructureToPtr(c, box.DataPointer, false);
            context.UnmapSubresource(constants, 0);

            context.Rasterizer.SetViewport(0, 0, width, height);
            context.Rasterizer.State = owner.rasterizer;
            context.OutputMerger.SetRenderTargets(depthOut, rtv);
            context.OutputMerger.SetBlendState(owner.blend);
            context.OutputMerger.SetDepthStencilState(depthOut != null ? owner.writeDepth : owner.noDepth);
            context.InputAssembler.InputLayout = null;
            context.InputAssembler.PrimitiveTopology = PrimitiveTopology.TriangleStrip;
            context.VertexShader.Set(owner.vertexShader);
            context.VertexShader.SetConstantBuffer(0, constants);
            context.PixelShader.Set(owner.pixelShader);
            context.PixelShader.SetConstantBuffer(0, constants);
            context.PixelShader.SetShaderResource(0, frame.Video);
            context.PixelShader.SetShaderResource(1, sceneDepth);
            context.PixelShader.SetShaderResource(2, infoView);
            context.PixelShader.SetSampler(0, owner.sampler);
            context.Draw(4, 0);

            using var commands = context.FinishCommandList(false);
            gpu.Start();
            owner.immediate.ExecuteCommandList(commands, true);
            gpu.Stop();
            LastProblem = null;
            return true;
        }

        /// <summary>
        /// Records a small shader pass that reads the live scene depth and our copy of it side by side and writes both
        /// as plain floats into a colour target, then copies that to a CPU-readable texture. All in the same command
        /// list as the draw and before it (so before the screen changes any depth). ReadProbe reads it a few frames
        /// later without waiting for the GPU.
        /// </summary>
        private void StartProbe(WallFrame frame, KernelTexture* target, KernelTexture* depth, in Matrix4x4 viewProj, float nearPlane)
        {
            ProbeArmed = false;
            try
            {
                if (depthResource == null || depthView == null || DepthWriteViews() is not { } views)
                {
                    Finish("probe: no readable scene depth at the injection point (" + DepthInfo + ")");
                    return;
                }

                var desc = depthResource.Description;
                if (desc.SampleDescription.Count != 1)
                {
                    Finish($"probe: the scene depth is multisampled ({desc.SampleDescription.Count}x), which the probe does not read");
                    return;
                }

                var (vertex, pixel) = owner.ProbeShaders();
                var description = new Texture2DDescription
                {
                    Width = ProbeWidth,
                    Height = ProbeHeight,
                    MipLevels = 1,
                    ArraySize = 1,
                    Format = Format.R32G32B32A32_Float,
                    SampleDescription = new SampleDescription(1, 0),
                    Usage = ResourceUsage.Default,
                    BindFlags = BindFlags.RenderTarget,
                };
                probeTarget = new Texture2D(owner.device, description);
                probeView = new RenderTargetView(owner.device, probeTarget);
                description.Usage = ResourceUsage.Staging;
                description.BindFlags = BindFlags.None;
                description.CpuAccessFlags = CpuAccessFlags.Read;
                probeStage = new Texture2D(owner.device, description);

                context.CopyResource(depthResource, depthCopy);
                context.Rasterizer.SetViewport(0, 0, ProbeWidth, ProbeHeight);
                context.Rasterizer.State = owner.rasterizer;
                context.OutputMerger.SetRenderTargets((DepthStencilView?)null, probeView);
                context.InputAssembler.InputLayout = null;
                context.InputAssembler.PrimitiveTopology = PrimitiveTopology.TriangleStrip;
                context.VertexShader.Set(vertex);
                context.PixelShader.Set(pixel);
                context.PixelShader.SetShaderResource(1, views.Read);
                context.PixelShader.SetShaderResource(3, depthView);
                context.Draw(4, 0);
                context.PixelShader.SetShaderResource(1, null);
                context.PixelShader.SetShaderResource(3, null);
                context.CopyResource(probeTarget, probeStage);

                // Where the screen's centre lands in the depth texture, and the depth the screen itself has there.
                var clip = Vector4.Transform(new Vector4(frame.Center, 1f), viewProj);
                var ndc = new Vector2(clip.X / clip.W, clip.Y / clip.W);
                var pixelPosition = new Vector2((ndc.X * 0.5f + 0.5f) * target->ActualWidth, (0.5f - ndc.Y * 0.5f) * target->ActualHeight);
                probeCenterTexel = new Vector2(pixelPosition.X * depth->ActualWidth / target->ActualWidth, pixelPosition.Y * depth->ActualHeight / target->ActualHeight);
                probeExpected = clip.Z / clip.W;
                probeWaited = 0;
                var sameResource = SceneDepth != null && SceneDepth->D3D11Texture2D == depth->D3D11Texture2D;
                probeHeader =
                    $"v{typeof(WallRenderer).Assembly.GetName().Version} · bound depth is the game's scene depth: {(SceneDepth == null ? "unknown" : sameResource ? "yes" : "NO")}" +
                    $" · depth {desc.Format} {desc.Width}x{desc.Height} (active {depth->ActualWidth}x{depth->ActualHeight}) · target {target->ActualWidth}x{target->ActualHeight}" +
                    $" · near {nearPlane:0.###} · screen centre at texel {probeCenterTexel.X:0},{probeCenterTexel.Y:0}, in front of camera {(clip.W > 0 ? "yes" : "NO")}, its own depth {probeExpected:0.0000}" +
                    $" · ignore depth {(IgnoreDepth || frame.IgnoreDepth)}";
            }
            catch (Exception ex)
            {
                DropProbe();
                Finish($"probe failed to start: {ex.Message}");
            }
        }

        /// <summary>Reads the probe target once the GPU is done with it (never waits); gives up after a while.</summary>
        private void ReadProbe()
        {
            if (probeStage == null)
                return;

            if (++probeWaited > 300)
            {
                DropProbe();
                Finish("probe: inconclusive, the GPU did not finish the readback in time. " + probeHeader);
                return;
            }

            SharpDX.DataBox box;
            try
            {
                box = owner.immediate.MapSubresource(probeStage, 0, MapMode.Read, SharpDX.Direct3D11.MapFlags.DoNotWait);
            }
            catch (SharpDX.SharpDXException ex) when (ex.ResultCode == SharpDX.DXGI.ResultCode.WasStillDrawing)
            {
                return;
            }
            catch (Exception ex)
            {
                DropProbe();
                Finish($"probe failed reading back: {ex.Message}. {probeHeader}");
                return;
            }

            string report;
            try
            {
                report = Summarise(box);
            }
            catch (Exception ex)
            {
                report = $"probe failed reading back: {ex.Message}. {probeHeader}";
            }
            finally
            {
                owner.immediate.UnmapSubresource(probeStage, 0);
            }

            DropProbe();
            Finish(report);
        }

        private string Summarise(SharpDX.DataBox box)
        {
            const int rowBytes = ProbeWidth * 16;
            if (box.DataPointer == IntPtr.Zero || box.RowPitch < rowBytes)
                return $"probe: the readback mapping is unusable (pointer {(box.DataPointer == IntPtr.Zero ? "null" : "set")}, row pitch {box.RowPitch}). {probeHeader}";

            var cells = new float[ProbeWidth * ProbeHeight * 4];
            for (var y = 0; y < ProbeHeight; y++)
                Marshal.Copy(box.DataPointer + y * box.RowPitch, cells, y * ProbeWidth * 4, ProbeWidth * 4);

            long count = 0, invalid = 0, sky = 0, near = 0, equal = 0, copyZero = 0;
            float min = float.MaxValue, max = float.MinValue, maxDiff = 0f;
            double sum = 0;
            for (var i = 0; i < ProbeWidth * ProbeHeight; i++)
            {
                var a = cells[i * 4];
                var b = cells[i * 4 + 1];
                if (!float.IsFinite(a) || !float.IsFinite(b))
                {
                    invalid++;
                    continue;
                }

                count++;
                sum += a;
                min = Math.Min(min, a);
                max = Math.Max(max, a);
                if (a <= 1e-7f)
                    sky++;
                if (a >= 0.5f)
                    near++;
                if (b <= 1e-7f)
                    copyZero++;
                var diff = Math.Abs(a - b);
                maxDiff = Math.Max(maxDiff, diff);
                if (diff <= 1e-7f)
                    equal++;
            }

            // The grid cell the screen's centre falls in.
            var cx = Math.Clamp((int)(probeCenterTexel.X / (depthResource?.Description.Width ?? 1) * ProbeWidth), 0, ProbeWidth - 1);
            var cy = Math.Clamp((int)(probeCenterTexel.Y / (depthResource?.Description.Height ?? 1) * ProbeHeight), 0, ProbeHeight - 1);
            var centre = (cy * ProbeWidth + cx) * 4;
            double Pct(long n) => count > 0 ? 100.0 * n / count : 0;
            return probeHeader + " · LIVE depth: " +
                   $"min {min:0.00000} max {max:0.00000} mean {sum / Math.Max(count, 1):0.00000}, sky {Pct(sky):0}%, nearer than 0.5: {Pct(near):0}%, not a number {invalid} of {ProbeWidth * ProbeHeight} · " +
                   $"COPY vs live: identical {Pct(equal):0}%, max difference {maxDiff:0.00000}, copy is zero {Pct(copyZero):0}% · " +
                   $"at the screen centre: live {cells[centre]:0.00000}, copy {cells[centre + 1]:0.00000}";
        }

        private void DropProbe()
        {
            probeStage?.Dispose();
            probeStage = null;
            probeView?.Dispose();
            probeView = null;
            probeTarget?.Dispose();
            probeTarget = null;
        }

        private static void Finish(string report) => Interlocked.Exchange(ref probeResult, report);

        public void Dispose()
        {
            DropProbe();
            targetView?.Dispose();
            DropDepth();
            infoView?.Dispose();
            gpu.Dispose();
            constants.Dispose();
            context.Dispose();
        }

        private RenderTargetView? TargetView(nint texture)
        {
            if (texture == targetTexture && targetView != null)
                return targetView;

            targetView?.Dispose();
            targetView = null;
            targetTexture = texture;
            try
            {
                using var tex = Borrow<Texture2D>(texture);
                var format = tex.Description.Format;
                targetView = new RenderTargetView(owner.device, tex, new RenderTargetViewDescription
                {
                    Format = TypedColorFormat(format),
                    Dimension = RenderTargetViewDimension.Texture2D,
                });
            }
            catch (Exception ex)
            {
                LastProblem = $"cannot draw into the game's image ({ex.Message})";
            }

            return targetView;
        }

        private ShaderResourceView? DepthView(KernelTexture* depth)
        {
            var texture = depth != null ? (nint)depth->D3D11Texture2D : 0;
            if (texture == 0)
                return null;
            if (texture == depthTexture)
                return depthView;

            DropDepth();
            depthTexture = texture;
            try
            {
                depthResource = Borrow<Texture2D>(texture);
                var desc = depthResource.Description;
                DepthInfo = $"{desc.Format} {desc.Width}x{desc.Height} bind {desc.BindFlags} samples {desc.SampleDescription.Count} array {desc.ArraySize} mips {desc.MipLevels}";
                depthReadFormat = DepthReadFormat(desc.Format);
                if (depthReadFormat == Format.Unknown || (desc.BindFlags & BindFlags.ShaderResource) == 0)
                {
                    if (!depthFailed)
                        Plugin.Log.Warning("Wall screen: scene depth {Format} cannot be read; the screen will not hide behind objects", desc.Format);
                    depthFailed = true;
                    return null;
                }

                depthView = new ShaderResourceView(owner.device, depthResource, ReadDescription(depthReadFormat));
            }
            catch (Exception ex)
            {
                if (!depthFailed)
                    Plugin.Log.Warning(ex, "Wall screen: could not read scene depth");
                depthFailed = true;
            }

            return depthView;
        }

        /// <summary>A copy of the scene depth to read from, and a view to write the screen's depth into the real one.</summary>
        private (ShaderResourceView Read, DepthStencilView Write)? DepthWriteViews()
        {
            if (depthResource == null)
                return null;
            if (depthCopyView != null && depthWriteView != null)
                return (depthCopyView, depthWriteView);

            try
            {
                var desc = depthResource.Description;
                var writeFormat = DepthWriteFormat(desc.Format);
                if (writeFormat == Format.Unknown || (desc.BindFlags & BindFlags.DepthStencil) == 0)
                    return null;

                var copyDesc = desc;
                copyDesc.BindFlags = BindFlags.ShaderResource;
                copyDesc.Usage = ResourceUsage.Default;
                copyDesc.CpuAccessFlags = CpuAccessFlags.None;
                copyDesc.OptionFlags = ResourceOptionFlags.None;
                depthCopy = new Texture2D(owner.device, copyDesc);
                depthCopyView = new ShaderResourceView(owner.device, depthCopy, ReadDescription(depthReadFormat));
                depthWriteView = new DepthStencilView(owner.device, depthResource, new DepthStencilViewDescription
                {
                    Format = writeFormat,
                    Dimension = DepthStencilViewDimension.Texture2D,
                });
                return (depthCopyView, depthWriteView);
            }
            catch (Exception ex)
            {
                Plugin.Log.Warning(ex, "Wall screen: cannot write depth; markers behind the screen will show through");
                depthCopyView?.Dispose();
                depthCopyView = null;
                depthCopy?.Dispose();
                depthCopy = null;
                depthWriteView?.Dispose();
                depthWriteView = null;
                return null;
            }
        }

        private void DropDepth()
        {
            depthWriteView?.Dispose();
            depthWriteView = null;
            depthCopyView?.Dispose();
            depthCopyView = null;
            depthCopy?.Dispose();
            depthCopy = null;
            depthView?.Dispose();
            depthView = null;
            depthResource?.Dispose();
            depthResource = null;
            depthTexture = 0;
        }

        private static ShaderResourceViewDescription ReadDescription(Format format)
        {
            var srv = new ShaderResourceViewDescription { Format = format, Dimension = ShaderResourceViewDimension.Texture2D };
            srv.Texture2D.MipLevels = 1;
            return srv;
        }

        private static Format DepthWriteFormat(Format format) => format switch
        {
            Format.R24G8_Typeless => Format.D24_UNorm_S8_UInt,
            Format.R32_Typeless => Format.D32_Float,
            Format.R32G8X24_Typeless => Format.D32_Float_S8X24_UInt,
            Format.R16_Typeless => Format.D16_UNorm,
            _ => Format.Unknown,
        };

        /// <summary>The game's own view of G-buffer 3 (tells characters from scenery), borrowed.</summary>
        private ShaderResourceView? InfoView(KernelTexture* info)
        {
            var pointer = info != null ? (nint)info->D3D11ShaderResourceView : 0;
            if (pointer == infoPointer)
                return infoView;

            infoView?.Dispose();
            infoView = pointer != 0 ? Borrow<ShaderResourceView>(pointer) : null;
            infoPointer = pointer;
            return infoView;
        }

        private static Format TypedColorFormat(Format format) => format switch
        {
            Format.B8G8R8A8_Typeless => Format.B8G8R8A8_UNorm,
            Format.B8G8R8X8_Typeless => Format.B8G8R8X8_UNorm,
            Format.R8G8B8A8_Typeless => Format.R8G8B8A8_UNorm,
            Format.R10G10B10A2_Typeless => Format.R10G10B10A2_UNorm,
            Format.R16G16B16A16_Typeless => Format.R16G16B16A16_Float,
            _ => format,
        };

        // GAME ASSUMPTION A6: the scene depth is a typeless resource bound for shader reads (R24G8 as of 7.x:
        // D24_UNORM_S8_UINT_3). A patch moving to another format only needs a new case here and in DepthWriteFormat.
        private static Format DepthReadFormat(Format format) => format switch
        {
            Format.R24G8_Typeless => Format.R24_UNorm_X8_Typeless,
            Format.R32_Typeless => Format.R32_Float,
            Format.R32G8X24_Typeless => Format.R32_Float_X8X24_Typeless,
            Format.R16_Typeless => Format.R16_UNorm,
            Format.R32_Float or Format.R16_UNorm => format,
            _ => Format.Unknown,
        };
    }
}
