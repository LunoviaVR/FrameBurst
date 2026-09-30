using System.Diagnostics;
using System.Drawing;
using SharpGen.Runtime;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace GpuShot.Capture;

public sealed record OutputInfo(string DeviceName, Rectangle Bounds, string Gpu, GpuVendor Vendor, uint BitsPerColor, string Rotation);

public sealed record AdapterInfo(string Name, GpuVendor Vendor, ulong VramBytes, uint VendorId, uint DeviceId, List<OutputInfo> Outputs);

/// <summary>
/// Captures every monitor with DXGI Desktop Duplication (a zero-copy GPU surface straight from the DWM
/// compositor), converts it to 8-bit sRGB on the GPU with a compute shader and reads back only the final pixels.
/// </summary>
public sealed class DesktopCapturer : IDisposable
{
    private readonly Dictionary<string, GpuDevice> _devices = new();
    private readonly object _lock = new();

    // Prefer the compositor's FP16 surface when it composes in FP16 (Windows Auto Color Management does this
    // even on SDR displays); otherwise DXGI hands us the exact BGRA8 surface.
    private static readonly Format[] SurfaceFormats = { Format.R16G16B16A16_Float, Format.B8G8R8A8_UNorm };

    public static List<AdapterInfo> EnumerateAdapters()
    {
        var result = new List<AdapterInfo>();
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        for (uint ai = 0; factory.EnumAdapters1(ai, out var adapter).Success; ai++)
        {
            using (adapter)
            {
                var ad = adapter.Description1;
                var vendor = GpuVendors.FromId(ad.VendorId);
                var outputs = new List<OutputInfo>();
                for (uint oi = 0; adapter.EnumOutputs(oi, out var output).Success; oi++)
                {
                    using (output)
                    {
                        var info = Describe(output, ad.Description.Trim(), vendor);
                        if (info != null) outputs.Add(info);
                    }
                }
                result.Add(new AdapterInfo(ad.Description.Trim(), vendor, (ulong)ad.DedicatedVideoMemory, ad.VendorId, ad.DeviceId, outputs));
            }
        }
        return result;
    }

    private static OutputInfo? Describe(IDXGIOutput output, string gpu, GpuVendor vendor)
    {
        using var o6 = output.QueryInterfaceOrNull<IDXGIOutput6>();
        if (o6 != null)
        {
            var d = o6.Description1;
            if (!d.AttachedToDesktop) return null;
            return new OutputInfo(d.DeviceName, ToRect(d.DesktopCoordinates), gpu, vendor, d.BitsPerColor, d.Rotation.ToString());
        }
        var dd = output.Description;
        if (!dd.AttachedToDesktop) return null;
        return new OutputInfo(dd.DeviceName, ToRect(dd.DesktopCoordinates), gpu, vendor, 8, dd.Rotation.ToString());
    }

    private static Rectangle ToRect(Vortice.RawRect r) => Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);

    /// <summary>Compile shaders and create a device per GPU ahead of the first hotkey press.</summary>
    public void WarmUp()
    {
        GpuDevice.WarmUp();
        lock (_lock)
        {
            using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
            for (uint ai = 0; factory.EnumAdapters1(ai, out var adapter).Success; ai++)
                using (adapter)
                {
                    if (adapter.EnumOutputs(0, out var o).Success) { o.Dispose(); GetDevice(adapter); }
                }
        }
    }

    public CaptureSet CaptureAll()
    {
        lock (_lock)
        {
            try { return CaptureCore(); }
            catch (SharpGenException ex) when (IsDeviceLost(ex))
            {
                // Driver update / TDR / GPU switch: rebuild devices once and retry.
                ResetDevices();
                return CaptureCore();
            }
        }
    }

    private static bool IsDeviceLost(SharpGenException ex) =>
        ex.ResultCode == Vortice.DXGI.ResultCode.DeviceRemoved || ex.ResultCode == Vortice.DXGI.ResultCode.DeviceReset ||
        ex.ResultCode == Vortice.DXGI.ResultCode.AccessLost;

    private GpuDevice GetDevice(IDXGIAdapter1 adapter)
    {
        var key = adapter.Description1.Luid.ToString()!;
        if (!_devices.TryGetValue(key, out var dev))
            _devices[key] = dev = new GpuDevice(adapter);
        return dev;
    }

    private void ResetDevices()
    {
        foreach (var d in _devices.Values) d.Dispose();
        _devices.Clear();
    }

    private sealed class Pending : IDisposable
    {
        public required GpuDevice Dev;
        public required OutputInfo Info;
        public required int W, H;
        public ID3D11Texture2D? Staging;
        public ID3D11Query? TsStart, TsEnd, TsDisjoint;
        public readonly List<IDisposable> Trash = new();
        public void Dispose() { foreach (var t in Trash) t.Dispose(); Staging?.Dispose(); TsStart?.Dispose(); TsEnd?.Dispose(); TsDisjoint?.Dispose(); }
    }

    private CaptureSet CaptureCore()
    {
        var total = Stopwatch.StartNew();
        var timings = new CaptureTimings();
        var pending = new List<Pending>();
        var frames = new List<MonitorFrame>();

        try
        {
            // ---- 1. Acquire every monitor's current desktop surface on its own GPU ----------------
            var acquired = new List<(GpuDevice Dev, OutputInfo Info, ID3D11Texture2D Tex)>();
            var acquire = Stopwatch.StartNew();
            using (var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>())
            {
                for (uint ai = 0; factory.EnumAdapters1(ai, out var adapter).Success; ai++)
                {
                    using (adapter)
                    {
                        var ad = adapter.Description1;
                        for (uint oi = 0; adapter.EnumOutputs(oi, out var output).Success; oi++)
                        {
                            using (output)
                            {
                                var info = Describe(output, ad.Description.Trim(), GpuVendors.FromId(ad.VendorId));
                                if (info == null) continue;
                                var dev = GetDevice(adapter);
                                var tex = AcquireSurface(dev, output);
                                if (tex == null) continue;
                                acquired.Add((dev, info, tex));
                            }
                        }
                    }
                }
            }
            timings.AcquireMs = acquire.Elapsed.TotalMilliseconds;
            if (acquired.Count == 0)
                throw new InvalidOperationException("No monitor could be captured. (Secure desktop, UAC prompt, or a full-screen exclusive app may be blocking Desktop Duplication.)");

            // ---- 2. GPU pass: rotation + conversion to 8-bit sRGB -----------------------------------
            var gpu = Stopwatch.StartNew();
            foreach (var (dev, info, tex) in acquired)
            {
                pending.Add(Dispatch(dev, info, tex));
                tex.Dispose();
            }
            foreach (var d in pending.Select(p => p.Dev).Distinct()) d.Context.Flush();
            timings.GpuMs = gpu.Elapsed.TotalMilliseconds;

            // ---- 3. Read back (Map blocks until each GPU finishes) ----------------------------------
            var rb = Stopwatch.StartNew();
            double shaderMs = 0; bool haveTs = false;
            foreach (var p in pending)
            {
                var bgra = ReadBack(p.Dev.Context, p.Staging!, p.W, p.H);
                frames.Add(new MonitorFrame
                {
                    DeviceName = p.Info.DeviceName, Bounds = p.Info.Bounds, GpuName = p.Dev.Name, Vendor = p.Dev.Vendor, Bgra = bgra,
                });
                Log.Write($"readback {p.Info.DeviceName}: {p.W}x{p.H} nonBlack={NonBlackPercent(bgra):0.0}%");
                if (TryGetShaderTime(p, out var ms)) { shaderMs += ms; haveTs = true; }
            }
            timings.ReadbackMs = rb.Elapsed.TotalMilliseconds;
            if (haveTs) timings.GpuShaderMs = shaderMs;
            timings.TotalMs = total.Elapsed.TotalMilliseconds;

            return new CaptureSet { Monitors = frames, Timings = timings };
        }
        finally
        {
            foreach (var p in pending) p.Dispose();
        }
    }

    private static ID3D11Texture2D? AcquireSurface(GpuDevice dev, IDXGIOutput output)
    {
        IDXGIOutputDuplication? dup = null;
        try
        {
            // DuplicateOutput1 is required: when DWM composes in FP16 the legacy DuplicateOutput returns
            // *linear* values stored as 8-bit without the sRGB encoding, which makes every capture much too
            // dark. The FP16 surface is linear, and the shader encodes it to sRGB exactly.
            using (var o5 = output.QueryInterfaceOrNull<IDXGIOutput5>())
            {
                if (o5 != null)
                {
                    try { dup = o5.DuplicateOutput1(dev.Device, SurfaceFormats); }
                    catch (SharpGenException) { dup = null; }
                }
            }
            if (dup == null)
            {
                using var o1 = output.QueryInterface<IDXGIOutput1>();
                dup = o1.DuplicateOutput(dev.Device);
            }
        }
        catch (SharpGenException ex) when (ex.ResultCode == Vortice.DXGI.ResultCode.NotCurrentlyAvailable ||
                                           ex.ResultCode == Vortice.DXGI.ResultCode.Unsupported ||
                                           ex.ResultCode.Code == unchecked((int)0x80070005)) // E_ACCESSDENIED (secure desktop)
        {
            dup?.Dispose();
            return null;
        }

        Log.Write($"duplicate format={dup.Description.ModeDescription.Format} rot={dup.Description.Rotation} inSysMem={(bool)dup.Description.DesktopImageInSystemMemory}");
        using (dup)
        {
            // Only a frame with LastPresentTime != 0 carries a desktop image. Frames that report just a mouse
            // move (LastPresentTime == 0) can arrive first and their surface may still be empty/black, so
            // they are skipped. A fresh duplication delivers the current desktop image right away, so this
            // normally takes one or two calls.
            var deadline = Stopwatch.StartNew();
            for (int attempt = 0; deadline.ElapsedMilliseconds < 1000; attempt++)
            {
                var hr = dup.AcquireNextFrame(100u, out var frameInfo, out var resource);
                Log.Write($"  acquire #{attempt}: hr=0x{hr.Code:X8} present={frameInfo.LastPresentTime} mouse={frameInfo.LastMouseUpdateTime} accum={frameInfo.AccumulatedFrames} protectedMasked={(bool)frameInfo.ProtectedContentMaskedOut}");
                if (hr == Vortice.DXGI.ResultCode.WaitTimeout) continue;
                if (hr == Vortice.DXGI.ResultCode.AccessLost) return null;
                hr.CheckError();

                try
                {
                    using (resource)
                    {
                        if (frameInfo.LastPresentTime == 0) continue;
                        using var tex = resource.QueryInterface<ID3D11Texture2D>();
                        var d = tex.Description;
                        var copy = dev.Device.CreateTexture2D(new Texture2DDescription(d.Format, d.Width, d.Height, 1, 1,
                            BindFlags.ShaderResource, ResourceUsage.Default, CpuAccessFlags.None, 1, 0, ResourceOptionFlags.None));
                        dev.Context.CopyResource(copy, tex);
                        return copy;
                    }
                }
                finally
                {
                    dup.ReleaseFrame();
                }
            }
            Log.Write("  no desktop image received within 1 s");
            return null;
        }
    }

    private static Pending Dispatch(GpuDevice dev, OutputInfo info, ID3D11Texture2D src)
    {
        var ctx = dev.Context;
        int w = info.Bounds.Width, h = info.Bounds.Height;
        var p = new Pending { Dev = dev, Info = info, W = w, H = h };

        var srv = dev.Device.CreateShaderResourceView(src, null);
        var outTex = dev.Device.CreateTexture2D(new Texture2DDescription(Format.R8G8B8A8_UNorm, (uint)w, (uint)h, 1, 1,
            BindFlags.UnorderedAccess, ResourceUsage.Default, CpuAccessFlags.None, 1, 0, ResourceOptionFlags.None));
        var outUav = dev.Device.CreateUnorderedAccessView(outTex, null);
        p.Trash.Add(srv); p.Trash.Add(outTex); p.Trash.Add(outUav);

        // Timestamp queries measure the pure GPU execution time of the shader.
        p.TsDisjoint = dev.Device.CreateQuery(new QueryDescription(QueryType.TimestampDisjoint));
        p.TsStart = dev.Device.CreateQuery(new QueryDescription(QueryType.Timestamp));
        p.TsEnd = dev.Device.CreateQuery(new QueryDescription(QueryType.Timestamp));
        ctx.Begin(p.TsDisjoint);
        ctx.End(p.TsStart);

        var prm = new ShaderParams
        {
            SrcIsFloat = src.Description.Format == Format.R16G16B16A16_Float ? 1u : 0u,
            Rotation = (uint)RotationOf(info),
            OutW = (uint)w, OutH = (uint)h,
        };
        ctx.UpdateSubresource(in prm, dev.ConstantBuffer);
        ctx.CSSetShader(dev.ConvertShader);
        ctx.CSSetConstantBuffer(0, dev.ConstantBuffer);
        ctx.CSSetShaderResource(0, srv);
        ctx.CSSetUnorderedAccessView(0, outUav, unchecked((uint)-1));
        ctx.Dispatch(((uint)w + 15) / 16, ((uint)h + 15) / 16, 1);

        ctx.End(p.TsEnd);
        ctx.End(p.TsDisjoint);

        // Unbind so the resources can be copied / released.
        ctx.CSSetShaderResource(0, null!);
        ctx.CSSetUnorderedAccessView(0, null!, unchecked((uint)-1));

        p.Staging = dev.Device.CreateTexture2D(new Texture2DDescription(Format.R8G8B8A8_UNorm, (uint)w, (uint)h, 1, 1,
            BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read, 1, 0, ResourceOptionFlags.None));
        ctx.CopyResource(p.Staging, outTex);
        return p;
    }

    private static ModeRotation RotationOf(OutputInfo info) =>
        Enum.TryParse<ModeRotation>(info.Rotation, out var r) ? r : ModeRotation.Identity;

    private static unsafe byte[] ReadBack(ID3D11DeviceContext ctx, ID3D11Texture2D staging, int w, int h)
    {
        var map = ctx.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            var dst = new byte[w * h * 4];
            int rowBytes = w * 4;
            byte* basePtr = (byte*)map.DataPointer;
            uint pitch = map.RowPitch;
            fixed (byte* d = dst)
            {
                byte* dp = d;
                Parallel.For(0, h, y => Buffer.MemoryCopy(basePtr + y * pitch, dp + (long)y * rowBytes, rowBytes, rowBytes));
            }
            return dst;
        }
        finally
        {
            ctx.Unmap(staging, 0);
        }
    }

    private static double NonBlackPercent(byte[] bgra)
    {
        long n = 0, nb = 0;
        for (int i = 0; i < bgra.Length; i += 4 * 97) { n++; if ((bgra[i] | bgra[i + 1] | bgra[i + 2]) != 0) nb++; }
        return n == 0 ? 0 : 100.0 * nb / n;
    }

    private static bool TryGetShaderTime(Pending p, out double ms)
    {
        ms = 0;
        try
        {
            var ctx = p.Dev.Context;
            var sw = Stopwatch.StartNew();
            QueryDataTimestampDisjoint dj;
            while (!ctx.GetData(p.TsDisjoint!, out dj))
            {
                if (sw.ElapsedMilliseconds > 50) return false;
                Thread.Yield();
            }
            if (dj.Disjoint) return false;
            if (!ctx.GetData(p.TsStart!, out ulong t0) || !ctx.GetData(p.TsEnd!, out ulong t1)) return false;
            ms = (t1 - t0) * 1000.0 / dj.Frequency;
            return true;
        }
        catch { return false; }
    }

    public void Dispose()
    {
        lock (_lock) ResetDevices();
    }
}
