using System.Reflection;
using System.Runtime.InteropServices;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace FrameBurst.Capture;

public enum GpuVendor { Unknown, Nvidia, Amd, Intel, Microsoft, Qualcomm }

public static class GpuVendors
{
    public static GpuVendor FromId(uint vendorId) => vendorId switch
    {
        0x10DE => GpuVendor.Nvidia,
        0x1002 or 0x1022 => GpuVendor.Amd,
        0x8086 or 0x8087 => GpuVendor.Intel,
        0x1414 => GpuVendor.Microsoft,
        0x5143 or 0x4D4F4351 => GpuVendor.Qualcomm,
        _ => GpuVendor.Unknown,
    };

    public static string Label(GpuVendor v) => v switch
    {
        GpuVendor.Nvidia => "NVIDIA",
        GpuVendor.Amd => "AMD",
        GpuVendor.Intel => "Intel",
        GpuVendor.Microsoft => "Microsoft",
        GpuVendor.Qualcomm => "Qualcomm",
        _ => "Unknown",
    };
}

[StructLayout(LayoutKind.Sequential)]
internal struct ShaderParams
{
    public uint SrcIsFloat, Rotation, OutW, OutH;
}

/// <summary>
/// A D3D11 device bound to one physical adapter. Each monitor is captured on the GPU that scans it out,
/// so each GPU in the system (NVIDIA / AMD / Intel) gets its own device and processes the monitors it
/// owns without a cross-adapter copy.
/// </summary>
internal sealed class GpuDevice : IDisposable
{
    private static readonly Lazy<byte[]> Bytecode = new(() => CompileShader("Convert"));
    private static readonly Lazy<byte[]> HdrBytecode = new(() => CompileShader("ConvertHdr"));

    public ID3D11Device Device { get; }
    public ID3D11DeviceContext Context { get; }
    public string Name { get; }
    public GpuVendor Vendor { get; }
    public FeatureLevel FeatureLevel { get; }
    public ID3D11ComputeShader ConvertShader { get; }
    public ID3D11Buffer ConstantBuffer { get; }

    private ID3D11ComputeShader? _hdrShader;
    /// <summary>scRGB to 16-bit PQ shader, created the first time an HDR copy is asked for.</summary>
    public ID3D11ComputeShader HdrShader => _hdrShader ??= Device.CreateComputeShader(HdrBytecode.Value);

    private Windows.Graphics.DirectX.Direct3D11.IDirect3DDevice? _winRtDevice;
    /// <summary>The same device wrapped for Windows Graphics Capture frame pools.</summary>
    public Windows.Graphics.DirectX.Direct3D11.IDirect3DDevice WinRtDevice => _winRtDevice ??= WgcCapture.CreateWinRtDevice(Device);

    public GpuDevice(IDXGIAdapter1 adapter)
    {
        var desc = adapter.Description1;
        Name = desc.Description.Trim();
        Vendor = GpuVendors.FromId(desc.VendorId);

        var levels = new[] { FeatureLevel.Level_12_1, FeatureLevel.Level_12_0, FeatureLevel.Level_11_1, FeatureLevel.Level_11_0 };
        D3D11.D3D11CreateDevice(adapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport, levels,
            out ID3D11Device device, out FeatureLevel fl, out ID3D11DeviceContext ctx).CheckError();
        Device = device;
        Context = ctx;
        FeatureLevel = fl;

        // Windows Graphics Capture touches the device from its own threads.
        using (var mt = Context.QueryInterface<ID3D11Multithread>()) mt.SetMultithreadProtected(true);

        ConvertShader = Device.CreateComputeShader(Bytecode.Value);

        ConstantBuffer = Device.CreateBuffer((uint)Marshal.SizeOf<ShaderParams>(), BindFlags.ConstantBuffer, ResourceUsage.Default, CpuAccessFlags.None, ResourceOptionFlags.None, 0);
    }

    private static byte[] CompileShader(string entry)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("FrameBurst.Shaders.hlsl")
            ?? throw new InvalidOperationException("Embedded shader source missing.");
        string src = new StreamReader(stream).ReadToEnd();
        const ShaderFlags flags = ShaderFlags.OptimizationLevel3 | ShaderFlags.EnableStrictness;
        return Compiler.Compile(src, entry, "Shaders.hlsl", "cs_5_0", flags, EffectFlags.None).ToArray();
    }

    /// <summary>Forces shader compilation up front so the first capture isn't slowed down.</summary>
    public static void WarmUp() => _ = Bytecode.Value;

    /// <summary>Compiles the HDR shader (used by the self-test).</summary>
    public static void WarmUpHdr() => _ = HdrBytecode.Value;

    public void Dispose()
    {
        _winRtDevice?.Dispose();
        ConstantBuffer.Dispose();
        ConvertShader.Dispose();
        _hdrShader?.Dispose();
        Context.ClearState();
        Context.Dispose();
        Device.Dispose();
    }
}
