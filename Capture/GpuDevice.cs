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
/// A D3D11 device bound to one physical adapter. Desktop Duplication must run on the adapter that
/// scans out the monitor, so each GPU in the system (NVIDIA / AMD / Intel) gets its own device and
/// processes the monitors it owns.
/// </summary>
internal sealed class GpuDevice : IDisposable
{
    private static readonly Lazy<byte[]> Bytecode = new(CompileShaders);

    public ID3D11Device Device { get; }
    public ID3D11DeviceContext Context { get; }
    public string Name { get; }
    public GpuVendor Vendor { get; }
    public FeatureLevel FeatureLevel { get; }
    public ID3D11ComputeShader ConvertShader { get; }
    public ID3D11Buffer ConstantBuffer { get; }

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

        ConvertShader = Device.CreateComputeShader(Bytecode.Value);

        ConstantBuffer = Device.CreateBuffer((uint)Marshal.SizeOf<ShaderParams>(), BindFlags.ConstantBuffer, ResourceUsage.Default, CpuAccessFlags.None, ResourceOptionFlags.None, 0);
    }

    private static byte[] CompileShaders()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("FrameBurst.Shaders.hlsl")
            ?? throw new InvalidOperationException("Embedded shader source missing.");
        string src = new StreamReader(stream).ReadToEnd();
        const ShaderFlags flags = ShaderFlags.OptimizationLevel3 | ShaderFlags.EnableStrictness;
        return Compiler.Compile(src, "Convert", "Shaders.hlsl", "cs_5_0", flags, EffectFlags.None).ToArray();
    }

    /// <summary>Forces shader compilation up front so the first capture isn't slowed down.</summary>
    public static void WarmUp() => _ = Bytecode.Value;

    public void Dispose()
    {
        ConstantBuffer.Dispose();
        ConvertShader.Dispose();
        Context.ClearState();
        Context.Dispose();
        Device.Dispose();
    }
}
