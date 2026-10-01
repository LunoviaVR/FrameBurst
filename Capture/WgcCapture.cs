using System.Diagnostics;
using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.DXGI;
using ApiInformation = Windows.Foundation.Metadata.ApiInformation;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace FrameBurst.Capture;

/// <summary>
/// Windows Graphics Capture: grabs one frame of each monitor straight from the DWM compositor as a D3D11
/// texture on the caller's device. Frames are requested in FP16 (linear scRGB) so the result is exact whether
/// DWM composes in 8-bit or FP16; the compute shader encodes it to sRGB.
/// </summary>
internal static class WgcCapture
{
    [ComImport, Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        IntPtr CreateForWindow(IntPtr window, ref Guid iid);
        IntPtr CreateForMonitor(IntPtr monitor, ref Guid iid);
    }

    [ComImport, Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDirect3DDxgiInterfaceAccess
    {
        IntPtr GetInterface(ref Guid iid);
    }

    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

    private static Guid GraphicsCaptureItemIid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    private static Guid Texture2DIid = typeof(ID3D11Texture2D).GUID;

    private static readonly Lazy<bool> CanHideBorder = new(() =>
    {
        if (!ApiInformation.IsPropertyPresent("Windows.Graphics.Capture.GraphicsCaptureSession", "IsBorderRequired")) return false;
        // Unpackaged apps are normally granted borderless capture; asking is harmless either way.
        try { GraphicsCaptureAccess.RequestAccessAsync(GraphicsCaptureAccessKind.Borderless).AsTask().Wait(2000); } catch { }
        return true;
    });

    public static bool IsSupported
    {
        get
        {
            try { return GraphicsCaptureSession.IsSupported(); }
            catch { return false; } // Windows builds older than 1803 don't have the API at all
        }
    }

    public static void WarmUp() => _ = CanHideBorder.Value;

    /// <summary>Wraps a D3D11 device as the WinRT IDirect3DDevice that frame pools need.</summary>
    public static IDirect3DDevice CreateWinRtDevice(ID3D11Device device)
    {
        using var dxgi = device.QueryInterface<IDXGIDevice>();
        Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgi.NativePointer, out var ptr));
        try { return MarshalInterface<IDirect3DDevice>.FromAbi(ptr); }
        finally { Marshal.Release(ptr); }
    }

    private static GraphicsCaptureItem CreateItemForMonitor(IntPtr hmonitor)
    {
        var interop = ActivationFactory.Get("Windows.Graphics.Capture.GraphicsCaptureItem").AsInterface<IGraphicsCaptureItemInterop>();
        var ptr = interop.CreateForMonitor(hmonitor, ref GraphicsCaptureItemIid);
        try { return GraphicsCaptureItem.FromAbi(ptr); }
        finally { Marshal.Release(ptr); }
    }

    private sealed class Session : IDisposable
    {
        public required GpuDevice Dev;
        public required GraphicsCaptureItem Item;
        public required Direct3D11CaptureFramePool Pool;
        public required GraphicsCaptureSession Capture;
        public readonly ManualResetEventSlim Arrived = new();
        public void Dispose() { Capture.Dispose(); Pool.Dispose(); Arrived.Dispose(); }
    }

    /// <summary>
    /// Starts a capture session on every monitor at once, then collects the first frame of each, so all
    /// monitors are captured at (nearly) the same instant. Returns null for monitors that could not be captured.
    /// </summary>
    public static ID3D11Texture2D?[] CaptureMonitors(IReadOnlyList<(GpuDevice Dev, IntPtr Monitor)> targets)
    {
        var sessions = new Session?[targets.Count];
        var result = new ID3D11Texture2D?[targets.Count];
        try
        {
            for (int i = 0; i < targets.Count; i++)
            {
                var (dev, hmon) = targets[i];
                try
                {
                    var item = CreateItemForMonitor(hmon);
                    var pool = Direct3D11CaptureFramePool.CreateFreeThreaded(dev.WinRtDevice, DirectXPixelFormat.R16G16B16A16Float, 1, item.Size);
                    var s = new Session { Dev = dev, Item = item, Pool = pool, Capture = pool.CreateCaptureSession(item) };
                    s.Capture.IsCursorCaptureEnabled = false; // the cursor is overlaid separately
                    if (CanHideBorder.Value) s.Capture.IsBorderRequired = false;
                    pool.FrameArrived += (_, _) => s.Arrived.Set();
                    s.Capture.StartCapture();
                    sessions[i] = s;
                }
                catch (Exception ex)
                {
                    Log.Write($"wgc: monitor 0x{hmon:X} could not be captured: {ex.Message}");
                }
            }

            var deadline = Stopwatch.StartNew();
            for (int i = 0; i < sessions.Length; i++)
            {
                var s = sessions[i];
                if (s == null) continue;
                int left = Math.Max(0, 1000 - (int)deadline.ElapsedMilliseconds);
                if (!s.Arrived.Wait(left)) { Log.Write($"wgc: no frame from monitor {i} within 1 s"); continue; }
                using var frame = s.Pool.TryGetNextFrame();
                if (frame == null) continue;
                result[i] = CopyFrame(s.Dev, frame);
            }
            return result;
        }
        finally
        {
            foreach (var s in sessions) s?.Dispose();
        }
    }

    private static ID3D11Texture2D CopyFrame(GpuDevice dev, Direct3D11CaptureFrame frame)
    {
        var access = frame.Surface.As<IDirect3DDxgiInterfaceAccess>();
        var ptr = access.GetInterface(ref Texture2DIid);
        using var tex = new ID3D11Texture2D(ptr);
        var d = tex.Description;
        uint w = Math.Min((uint)frame.ContentSize.Width, d.Width), h = Math.Min((uint)frame.ContentSize.Height, d.Height);
        Log.Write($"wgc frame {w}x{h} format={d.Format} surface={d.Width}x{d.Height}");

        var copy = dev.Device.CreateTexture2D(new Texture2DDescription(d.Format, w, h, 1, 1,
            BindFlags.ShaderResource, ResourceUsage.Default, CpuAccessFlags.None, 1, 0, ResourceOptionFlags.None));
        dev.Context.CopySubresourceRegion(copy, 0, 0, 0, 0, tex, 0, new Vortice.Mathematics.Box(0, 0, 0, (int)w, (int)h, 1));
        return copy;
    }
}
