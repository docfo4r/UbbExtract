using System.IO;
using System.IO.Compression;
using System.Text;

namespace UbbExtract
{
    // Minimal writer for 8 bit palette PNGs, so no image library is needed.
    static class PngWriter
    {
        public static void WriteIndexed(string path, byte[] pixels, int width, int height, byte[] palette)
        {
            using (var f = File.Create(path))
            {
                f.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, 0, 8);

                var ihdr = new byte[13];
                PutBE(ihdr, 0, (uint)width);
                PutBE(ihdr, 4, (uint)height);
                ihdr[8] = 8; // bit depth
                ihdr[9] = 3; // color type: palette
                WriteChunk(f, "IHDR", ihdr);
                WriteChunk(f, "PLTE", palette);

                // every line gets a leading filter byte (0 = none)
                var raw = new byte[(width + 1) * height];
                for (int y = 0; y < height; y++)
                    System.Array.Copy(pixels, y * width, raw, y * (width + 1) + 1, width);
                WriteChunk(f, "IDAT", Zlib(raw));
                WriteChunk(f, "IEND", new byte[0]);
            }
        }

        static byte[] Zlib(byte[] data)
        {
            using (var ms = new MemoryStream())
            {
                ms.WriteByte(0x78);
                ms.WriteByte(0x9C);
                using (var ds = new DeflateStream(ms, CompressionMode.Compress, true))
                    ds.Write(data, 0, data.Length);

                uint a = 1, b = 0; // Adler-32
                foreach (byte v in data) { a = (a + v) % 65521; b = (b + a) % 65521; }
                var tail = new byte[4];
                PutBE(tail, 0, b << 16 | a);
                ms.Write(tail, 0, 4);
                return ms.ToArray();
            }
        }

        static void WriteChunk(Stream s, string type, byte[] data)
        {
            var buf = new byte[data.Length + 12];
            PutBE(buf, 0, (uint)data.Length);
            Encoding.ASCII.GetBytes(type, 0, 4, buf, 4);
            data.CopyTo(buf, 8);

            uint crc = 0xFFFFFFFF; // CRC-32 over type + data
            for (int i = 4; i < data.Length + 8; i++)
            {
                crc ^= buf[i];
                for (int k = 0; k < 8; k++)
                    crc = (crc & 1) != 0 ? 0xEDB88320 ^ (crc >> 1) : crc >> 1;
            }
            PutBE(buf, data.Length + 8, ~crc);
            s.Write(buf, 0, buf.Length);
        }

        static void PutBE(byte[] b, int o, uint v)
        {
            b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16);
            b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v;
        }
    }
}
