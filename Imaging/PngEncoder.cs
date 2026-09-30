using System.Buffers.Binary;
using System.IO.Compression;

namespace GpuShot.Imaging;

/// <summary>
/// Lossless PNG writer tuned for screenshots:
///  * per-row adaptive filter selection (None/Sub/Up/Average/Paeth, min-sum-of-abs heuristic), in parallel
///  * multi-threaded deflate: the filtered stream is split into chunks that are compressed concurrently and
///    stitched into one valid zlib stream with sync-flush boundaries
///  * 8-bit sRGB, RGB or RGBA
/// </summary>
public static class PngEncoder
{
    private static readonly byte[] Signature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
    private const int ChunkSize = 1 << 20;

    /// <summary>Writes an 8-bit PNG from tightly packed BGRA pixels. Alpha is dropped if the image is opaque.</summary>
    public static void WriteBgra8(Stream output, byte[] bgra, int width, int height, CompressionLevel level)
    {
        bool opaque = IsOpaque(bgra);
        int bpp = opaque ? 3 : 4;
        int stride = width * bpp;

        var raw = new byte[height * (stride + 1)];
        Parallel.For(0, height, () => (new byte[stride], new byte[stride]), (y, _, bufs) =>
        {
            var (cur, prev) = bufs;
            ToRgb(bgra, y, width, opaque, cur);
            if (y > 0) ToRgb(bgra, y - 1, width, opaque, prev); else Array.Clear(prev);
            FilterRow(cur, prev, bpp, raw.AsSpan(y * (stride + 1), stride + 1));
            return bufs;
        }, _ => { });

        WriteHeader(output, width, height, 8, opaque ? (byte)2 : (byte)6);
        WriteChunk(output, "sRGB", new byte[] { 0 }); // perceptual intent
        WriteIdat(output, raw, level);
        WriteChunk(output, "IEND", Array.Empty<byte>());
    }

    private static bool IsOpaque(byte[] bgra)
    {
        int n = bgra.Length / 4;
        bool opaque = true;
        Parallel.For(0, (n + 65535) / 65536, (block, state) =>
        {
            int end = Math.Min(n, (block + 1) * 65536);
            for (int i = block * 65536; i < end; i++)
                if (bgra[i * 4 + 3] != 255) { opaque = false; state.Stop(); return; }
        });
        return opaque;
    }

    private static void ToRgb(byte[] bgra, int y, int width, bool dropAlpha, byte[] dst)
    {
        int s = y * width * 4, d = 0;
        if (dropAlpha)
            for (int x = 0; x < width; x++, s += 4) { dst[d++] = bgra[s + 2]; dst[d++] = bgra[s + 1]; dst[d++] = bgra[s]; }
        else
            for (int x = 0; x < width; x++, s += 4) { dst[d++] = bgra[s + 2]; dst[d++] = bgra[s + 1]; dst[d++] = bgra[s]; dst[d++] = bgra[s + 3]; }
    }

    private static void FilterRow(byte[] cur, byte[] prev, int bpp, Span<byte> dst)
    {
        int n = cur.Length;
        Span<byte> best = dst[1..];
        Span<byte> trial = n <= 16384 ? stackalloc byte[n] : new byte[n];
        long bestSum = long.MaxValue;
        byte bestType = 0;

        for (byte type = 0; type <= 4; type++)
        {
            long sum = 0;
            for (int i = 0; i < n; i++)
            {
                int a = i >= bpp ? cur[i - bpp] : 0, b = prev[i], c = i >= bpp ? prev[i - bpp] : 0;
                byte v = type switch
                {
                    0 => cur[i],
                    1 => (byte)(cur[i] - a),
                    2 => (byte)(cur[i] - b),
                    3 => (byte)(cur[i] - ((a + b) >> 1)),
                    _ => (byte)(cur[i] - Paeth(a, b, c)),
                };
                trial[i] = v;
                sum += v < 128 ? v : 256 - v;
                if (sum >= bestSum) break;
            }
            if (sum < bestSum)
            {
                bestSum = sum;
                bestType = type;
                trial.CopyTo(best);
                if (sum == 0) break;
            }
        }
        dst[0] = bestType;
    }

    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    private static void WriteHeader(Stream s, int w, int h, byte depth, byte colorType)
    {
        s.Write(Signature);
        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(0), w);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), h);
        ihdr[8] = depth; ihdr[9] = colorType; ihdr[10] = 0; ihdr[11] = 0; ihdr[12] = 0;
        WriteChunk(s, "IHDR", ihdr);
    }

    private static void WriteIdat(Stream s, byte[] raw, CompressionLevel level)
    {
        byte[] z = ParallelZlib(raw, level);
        // Split into moderately sized IDAT chunks.
        const int maxIdat = 1 << 20;
        for (int off = 0; off < z.Length; off += maxIdat)
            WriteChunk(s, "IDAT", z.AsSpan(off, Math.Min(maxIdat, z.Length - off)));
    }

    /// <summary>Compresses chunks in parallel and joins them into one RFC 1950 zlib stream.</summary>
    public static byte[] ParallelZlib(byte[] data, CompressionLevel level)
    {
        int chunks = Math.Max(1, (data.Length + ChunkSize - 1) / ChunkSize);
        var parts = new byte[chunks][];
        Parallel.For(0, chunks, i =>
        {
            int off = i * ChunkSize, len = Math.Min(ChunkSize, data.Length - off);
            using var ms = new MemoryStream(len / 2 + 64);
            var ds = new DeflateStream(ms, level, leaveOpen: true);
            ds.Write(data, off, len);
            ds.Flush();                    // Z_SYNC_FLUSH: byte-aligned, BFINAL = 0
            long aligned = ms.Length;
            ds.Dispose();                  // writes a final block, which we drop again
            ms.SetLength(aligned);
            parts[i] = ms.ToArray();
        });

        using var outMs = new MemoryStream(parts.Sum(p => p.Length) + 16);
        outMs.WriteByte(0x78);
        outMs.WriteByte(level == CompressionLevel.Fastest ? (byte)0x01 : level == CompressionLevel.SmallestSize ? (byte)0xDA : (byte)0x9C);
        foreach (var p in parts) outMs.Write(p);
        outMs.WriteByte(0x03); outMs.WriteByte(0x00); // empty final fixed-Huffman block
        Span<byte> adler = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(adler, Adler32(data));
        outMs.Write(adler);
        return outMs.ToArray();
    }

    private static uint Adler32(byte[] data)
    {
        const uint Mod = 65521;
        uint a = 1, b = 0;
        int i = 0;
        while (i < data.Length)
        {
            int n = Math.Min(5552, data.Length - i);
            for (int k = 0; k < n; k++) { a += data[i + k]; b += a; }
            a %= Mod; b %= Mod;
            i += n;
        }
        return (b << 16) | a;
    }

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var t = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            t[n] = c;
        }
        return t;
    }

    private static uint Crc(ReadOnlySpan<byte> data, uint crc = 0xFFFFFFFFu)
    {
        foreach (byte b in data) crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc;
    }

    private static void WriteChunk(Stream s, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> buf = stackalloc byte[8];
        BinaryPrimitives.WriteInt32BigEndian(buf, data.Length);
        for (int i = 0; i < 4; i++) buf[4 + i] = (byte)type[i];
        s.Write(buf);
        s.Write(data);
        uint crc = Crc(data, Crc(buf[4..])) ^ 0xFFFFFFFFu;
        BinaryPrimitives.WriteUInt32BigEndian(buf, crc);
        s.Write(buf[..4]);
    }
}
