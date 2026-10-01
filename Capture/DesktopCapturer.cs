using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using SharpGen.Runtime;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace FrameBurst.Capture;

public sealed record OutputInfo(string DeviceName, Rectangle Bounds, string Gpu, GpuVendor Vendor, uint BitsPerColor, string Rotation, bool IsHdr = false);

public sealed record AdapterInfo(string Name, GpuVendor Vendor, ulong VramBytes, uint VendorId, uint DeviceId, List<OutputInfo> Outputs);

/// <summary>
/// Captures every monitor with Windows Graphics Capture (a GPU surface straight from the DWM compositor),
/// converts it to 8-bit sRGB on the GPU with a compute shader and reads back only the final pixels.
/// Falls back to GDI BitBlt (<see cref="GdiCapture"/>) when the GPU can't run that path.
/// </summary>
public sealed class DesktopCapturer : IDisposable
{
    private readonly Dictionary<string, GpuDevice> _devices = new();
    private readonly object _lock = new();

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
            return new OutputInfo(d.DeviceName, ToRect(d.DesktopCoordinates), gpu, vendor, d.BitsPerColor, d.Rotation.ToString(),
                d.ColorSpace == ColorSpaceType.RgbFullG2084NoneP2020);
        }
        var dd = output.Description;
        if (!dd.AttachedToDesktop) return null;
        return new OutputInfo(dd.DeviceName, ToRect(dd.DesktopCoordinates), gpu, vendor, 8, dd.Rotation.ToString());
    }

    private static Rectangle ToRect(Vortice.RawRect r) => Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);

    private bool _gpuUnsupported;

    /// <summary>Skip the GPU path and always capture with GDI BitBlt (self-test / diagnostics).</summary>
    public bool ForceGdi { get; set; }

    /// <summary>True once the GPU path has been found unusable on this system; captures then go through GDI.</summary>
    public bool UsingGdiFallback => ForceGdi || _gpuUnsupported;

    /// <summary>Compile shaders and create a device per GPU ahead of the first hotkey press.</summary>
    public void WarmUp()
    {
        lock (_lock)
        {
            if (UsingGdiFallback) return;
            try
            {
                if (!WgcCapture.IsSupported) throw new GpuUnsupportedException("Windows Graphics Capture is not available on this system.");
                GpuDevice.WarmUp();
                WgcCapture.WarmUp();
                using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
                for (uint ai = 0; factory.EnumAdapters1(ai, out var adapter).Success; ai++)
                    using (adapter)
                    {
                        if (adapter.EnumOutputs(0, out var o).Success) { o.Dispose(); _ = GetDevice(adapter).WinRtDevice; }
                    }
            }
            catch (GpuUnsupportedException ex) { DisableGpu(ex); }
        }
    }

    /// <param name="hdr">Also produce a 16-bit PQ copy of every monitor that is in HDR mode.</param>
    public CaptureSet CaptureAll(bool hdr = false)
    {
        lock (_lock)
        {
            if (UsingGdiFallback) return GdiCapture.CaptureAll();
            try
            {
                try { return CaptureCore(hdr); }
                catch (SharpGenException ex) when (IsDeviceLost(ex))
                {
                    // Driver update / TDR / GPU switch: rebuild devices once and retry.
                    ResetDevices();
                    return CaptureCore(hdr);
                }
            }
            catch (GpuUnsupportedException ex)
            {
                DisableGpu(ex);
                return GdiCapture.CaptureAll();
            }
            catch (Exception ex)
            {
                // Transient GPU failure: try GDI for this capture only, but report the original error if that fails too.
                Log.Write("GPU capture failed, trying GDI for this capture: " + ex.Message);
                try { return GdiCapture.CaptureAll(); }
                catch (Exception gdiEx) { Log.Write("GDI capture failed too: " + gdiEx.Message); }
                throw;
            }
        }
    }

    private void DisableGpu(Exception ex)
    {
        _gpuUnsupported = true;
        ResetDevices();
        Log.Write($"GPU capture unavailable, falling back to GDI BitBlt: {ex.Message}" + (ex.InnerException != null ? $" ({ex.InnerException.Message})" : ""));
    }

    /// <summary>The GPU path cannot run on this system (no D3D11 FL11 device, no compute shaders, no WGC).</summary>
    private sealed class GpuUnsupportedException(string message, Exception? inner = null) : Exception(message, inner);

    private static bool IsDeviceLost(SharpGenException ex) =>
        ex.ResultCode == Vortice.DXGI.ResultCode.DeviceRemoved || ex.ResultCode == Vortice.DXGI.ResultCode.DeviceReset ||
        ex.ResultCode == Vortice.DXGI.ResultCode.AccessLost;

    private GpuDevice GetDevice(IDXGIAdapter1 adapter)
    {
        var key = adapter.Description1.Luid.ToString()!;
        if (!_devices.TryGetValue(key, out var dev))
        {
            try { dev = new GpuDevice(adapter); }
            catch (Exception ex) when (ex is SharpGenException or COMException or InvalidOperationException)
            {
                throw new GpuUnsupportedException($"Cannot create a Direct3D 11 (feature level 11.0) device on {adapter.Description1.Description.Trim()}.", ex);
            }
            _devices[key] = dev;
        }
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
        public ID3D11Texture2D? Staging, HdrStaging;
        public ID3D11Query? TsStart, TsEnd, TsDisjoint;
        public readonly List<IDisposable> Trash = new();
        public void Dispose() { foreach (var t in Trash) t.Dispose(); Staging?.Dispose(); HdrStaging?.Dispose(); TsStart?.Dispose(); TsEnd?.Dispose(); TsDisjoint?.Dispose(); }
    }

    private CaptureSet CaptureCore(bool hdr)
    {
        var total = Stopwatch.StartNew();
        var timings = new CaptureTimings();
        var pending = new List<Pending>();
        var frames = new List<MonitorFrame>();

        try
        {
            // ---- 1. Grab every monitor's current frame from the compositor, each on its own GPU -------
            if (!WgcCapture.IsSupported) throw new GpuUnsupportedException("Windows Graphics Capture is not available on this system.");
            var targets = new List<(GpuDevice Dev, OutputInfo Info)>();
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
                                if (info != null) targets.Add((GetDevice(adapter), info));
                            }
                        }
                    }
                }
            }
            var textures = WgcCapture.CaptureMonitors(targets.Select(t => (t.Dev, MonitorOf(t.Info))).ToList());
            var acquired = new List<(GpuDevice Dev, OutputInfo Info, ID3D11Texture2D Tex)>();
            for (int i = 0; i < targets.Count; i++)
                if (textures[i] is { } tex) acquired.Add((targets[i].Dev, targets[i].Info, tex));
            timings.AcquireMs = acquire.Elapsed.TotalMilliseconds;
            if (acquired.Count == 0)
                throw new InvalidOperationException("No monitor could be captured. (The secure desktop, such as a UAC prompt or the lock screen, cannot be captured.)");

            // ---- 2. GPU pass: rotation + conversion to 8-bit sRGB -----------------------------------
            var gpu = Stopwatch.StartNew();
            foreach (var (dev, info, tex) in acquired)
            {
                pending.Add(Dispatch(dev, info, tex, hdr && info.IsHdr));
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
                    IsHdr = p.Info.IsHdr, Hdr = p.HdrStaging != null ? ReadBack16(p.Dev.Context, p.HdrStaging, p.W, p.H) : null,
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

    private static Pending Dispatch(GpuDevice dev, OutputInfo info, ID3D11Texture2D src, bool hdr)
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
            Rotation = (uint)ModeRotation.Identity, // WGC frames are already in desktop orientation
            OutW = (uint)w, OutH = (uint)h,
        };
        ctx.UpdateSubresource(in prm, dev.ConstantBuffer);
        ctx.CSSetShader(dev.ConvertShader);
        ctx.CSSetConstantBuffer(0, dev.ConstantBuffer);
        ctx.CSSetShaderResource(0, srv);
        ctx.CSSetUnorderedAccessView(0, outUav, unchecked((uint)-1));
        ctx.Dispatch(((uint)w + 15) / 16, ((uint)h + 15) / 16, 1);

        ID3D11Texture2D? hdrTex = null;
        if (hdr && src.Description.Format == Format.R16G16B16A16_Float)
        {
            hdrTex = dev.Device.CreateTexture2D(new Texture2DDescription(Format.R16G16B16A16_UNorm, (uint)w, (uint)h, 1, 1,
                BindFlags.UnorderedAccess, ResourceUsage.Default, CpuAccessFlags.None, 1, 0, ResourceOptionFlags.None));
            var hdrUav = dev.Device.CreateUnorderedAccessView(hdrTex, null);
            p.Trash.Add(hdrTex); p.Trash.Add(hdrUav);
            ctx.CSSetUnorderedAccessView(0, null!, unchecked((uint)-1));
            ctx.CSSetShader(dev.HdrShader);
            ctx.CSSetUnorderedAccessView(1, hdrUav, unchecked((uint)-1));
            ctx.Dispatch(((uint)w + 15) / 16, ((uint)h + 15) / 16, 1);
            ctx.CSSetUnorderedAccessView(1, null!, unchecked((uint)-1));
        }

        ctx.End(p.TsEnd);
        ctx.End(p.TsDisjoint);

        // Unbind so the resources can be copied / released.
        ctx.CSSetShaderResource(0, null!);
        ctx.CSSetUnorderedAccessView(0, null!, unchecked((uint)-1));

        p.Staging = dev.Device.CreateTexture2D(new Texture2DDescription(Format.R8G8B8A8_UNorm, (uint)w, (uint)h, 1, 1,
            BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read, 1, 0, ResourceOptionFlags.None));
        ctx.CopyResource(p.Staging, outTex);
        if (hdrTex != null)
        {
            p.HdrStaging = dev.Device.CreateTexture2D(new Texture2DDescription(Format.R16G16B16A16_UNorm, (uint)w, (uint)h, 1, 1,
                BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read, 1, 0, ResourceOptionFlags.None));
            ctx.CopyResource(p.HdrStaging, hdrTex);
        }
        return p;
    }

    private static IntPtr MonitorOf(OutputInfo info)
    {
        var b = info.Bounds;
        return Win32.Native.MonitorFromPoint(new Win32.Native.POINT { X = b.Left + b.Width / 2, Y = b.Top + b.Height / 2 }, Win32.Native.MONITOR_DEFAULTTONULL);
    }

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

    private static unsafe ushort[] ReadBack16(ID3D11DeviceContext ctx, ID3D11Texture2D staging, int w, int h)
    {
        var map = ctx.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            var dst = new ushort[w * h * 4];
            int rowBytes = w * 8;
            byte* basePtr = (byte*)map.DataPointer;
            uint pitch = map.RowPitch;
            fixed (ushort* d = dst)
            {
                byte* dp = (byte*)d;
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
