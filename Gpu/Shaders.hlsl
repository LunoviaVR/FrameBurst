// FrameBurst compute shader (Shader Model 5.0 — runs on any D3D11 FL11 GPU: NVIDIA, AMD, Intel).
//
// Input : the desktop surface from DXGI Desktop Duplication, either
//         B8G8R8A8_UNORM (sRGB-encoded, passed through bit-exactly) or
//         R16G16B16A16_FLOAT (linear, used when Windows composes in FP16, e.g. Auto Color Management).
// Output: 8-bit sRGB, stored BGRA so it maps 1:1 to GDI / clipboard bitmaps, in desktop orientation.

cbuffer Params : register(b0)
{
    uint SrcIsFloat;  // 1 = linear FP16 source
    uint Rotation;    // DXGI_MODE_ROTATION
    uint OutW;        // output size in desktop orientation
    uint OutH;
};

Texture2D<float4>         Src    : register(t0);
RWTexture2D<unorm float4> Output : register(u0);
RWTexture2D<unorm float4> HdrOutput : register(u1);

// scRGB (linear BT.709, 1.0 = 80 nits) -> BT.2100 PQ (BT.2020 primaries), as stored in an HDR PNG.
float3 ScRgbToPq(float3 c)
{
    static const float3x3 Bt709To2020 =
    {
        0.627404, 0.329283, 0.043313,
        0.069097, 0.919540, 0.011362,
        0.016391, 0.088013, 0.895595,
    };
    float3 y = saturate(mul(Bt709To2020, c) * (80.0 / 10000.0));
    const float m1 = 0.1593017578125, m2 = 78.84375, c1 = 0.8359375, c2 = 18.8515625, c3 = 18.6875;
    float3 p = pow(y, m1);
    return pow((c1 + c2 * p) / (1.0 + c3 * p), m2);
}

float3 LinearToSrgb(float3 c)
{
    c = saturate(c);
    return c <= 0.0031308 ? c * 12.92 : 1.055 * pow(c, 1.0 / 2.4) - 0.055;
}

uint2 SourceCoord(uint2 p)
{
    // Desktop Duplication returns the surface in the panel's native scan orientation.
    switch (Rotation)
    {
        case 2:  return uint2(OutH - 1 - p.y, p.x);            // ROTATE90
        case 3:  return uint2(OutW - 1 - p.x, OutH - 1 - p.y); // ROTATE180
        case 4:  return uint2(p.y, OutW - 1 - p.x);            // ROTATE270
        default: return p;
    }
}

[numthreads(16, 16, 1)]
void Convert(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= OutW || id.y >= OutH) return;
    float4 s = Src[SourceCoord(id.xy)];
    float3 c = SrcIsFloat ? LinearToSrgb(s.rgb) : s.rgb;
    Output[id.xy] = float4(c.b, c.g, c.r, 1.0); // BGRA byte order
}

// Optional second pass for HDR captures: 16-bit RGBA in PQ. The source is always the FP16 scRGB WGC frame.
[numthreads(16, 16, 1)]
void ConvertHdr(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= OutW || id.y >= OutH) return;
    float4 s = Src[SourceCoord(id.xy)];
    HdrOutput[id.xy] = float4(ScRgbToPq(max(s.rgb, 0.0)), 1.0);
}
