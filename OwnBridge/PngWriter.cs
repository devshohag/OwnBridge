using System.Buffers.Binary;
using System.IO.Compression;

namespace OwnBridge;

// Minimal PNG encoder (RGB, 8 bits per channel) using the built-in zlib stream; no image libraries.
internal static class PngWriter
{
    // CF_DIB = BITMAPINFOHEADER (+ optional bit masks) followed by the pixels. Supports 24 and 32 bits per pixel.
    public static byte[]? FromDib(byte[] dib)
    {
        if (dib.Length < 40) return null;
        var headerSize = BinaryPrimitives.ReadInt32LittleEndian(dib.AsSpan(0));
        var width = BinaryPrimitives.ReadInt32LittleEndian(dib.AsSpan(4));
        var rawHeight = BinaryPrimitives.ReadInt32LittleEndian(dib.AsSpan(8));
        var bitCount = BinaryPrimitives.ReadInt16LittleEndian(dib.AsSpan(14));
        var compression = BinaryPrimitives.ReadInt32LittleEndian(dib.AsSpan(16));
        var height = Math.Abs(rawHeight);
        var topDown = rawHeight < 0;
        if (width <= 0 || height <= 0 || width > 20000 || height > 20000) return null;
        if (bitCount != 24 && bitCount != 32) return null;
        if (compression != 0 && compression != 3) return null; // BI_RGB or BI_BITFIELDS only.

        var offset = headerSize + (compression == 3 && headerSize == 40 ? 12 : 0);
        var bytesPerPixel = bitCount / 8;
        var stride = ((width * bitCount + 31) / 32) * 4;
        if (offset + (long)stride * height > dib.Length) return null;

        var raw = new byte[(width * 3 + 1) * height];
        var target = 0;
        for (var y = 0; y < height; y++)
        {
            var sourceRow = topDown ? y : height - 1 - y;
            var source = offset + sourceRow * stride;
            raw[target++] = 0; // Filter type: none.
            for (var x = 0; x < width; x++)
            {
                var p = source + x * bytesPerPixel;
                raw[target++] = dib[p + 2]; // R
                raw[target++] = dib[p + 1]; // G
                raw[target++] = dib[p];     // B
            }
        }
        return Encode(width, height, raw);
    }

    private static byte[] Encode(int width, int height, byte[] filteredRgb)
    {
        using var output = new MemoryStream();
        output.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });

        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(0), width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8;  // Bit depth.
        header[9] = 2;  // Color type: RGB.
        WriteChunk(output, "IHDR", header);

        using (var compressed = new MemoryStream())
        {
            using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
                zlib.Write(filteredRgb);
            WriteChunk(output, "IDAT", compressed.ToArray());
        }
        WriteChunk(output, "IEND", Array.Empty<byte>());
        return output.ToArray();
    }

    private static void WriteChunk(Stream output, string type, byte[] data)
    {
        var length = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        output.Write(length);
        var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        output.Write(typeBytes);
        output.Write(data);
        var crc = Crc32(typeBytes, data);
        var crcBytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
        output.Write(crcBytes);
    }

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }

    private static uint Crc32(byte[] type, byte[] data)
    {
        var c = 0xFFFFFFFFu;
        foreach (var b in type) c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        foreach (var b in data) c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }
}
