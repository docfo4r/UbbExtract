// UbbExtract - extracts video frames from MegaRace 2 .UBB files as PNG.
// Usage: UbbExtract <input.ubb> [outputFolder] [frameCount | all]
// Works with .NET Framework 4.x and .NET (Core), C# 7.3 or newer, no packages.
using System;
using System.IO;
using System.Text;

namespace UbbExtract
{
    static class Program
    {
        static int Main(string[] args)
        {
            if (args.Length < 1)
            {
                Console.WriteLine("Usage: UbbExtract <input.ubb> [outputFolder] [frameCount | all]");
                return 1;
            }

            string input = args[0];
            string outDir = args.Length > 1 ? args[1] : "frames";
            int maxFrames = 1; // default: only the first frame
            if (args.Length > 2)
                maxFrames = args[2].Equals("all", StringComparison.OrdinalIgnoreCase)
                    ? int.MaxValue : int.Parse(args[2]);

            Directory.CreateDirectory(outDir);
            var decoder = new UbbVideoDecoder();
            var palette = new byte[256 * 3];
            int frames = 0;

            using (var fs = File.OpenRead(input))
            {
                var header = new byte[4];
                while (frames < maxFrames)
                {
                    // Superchunk: 24 bit size (including these 4 bytes) + 8 bit flags
                    long start = fs.Position;
                    if (!ReadFully(fs, header, 4)) break;
                    int size = Read24(header, 0);
                    if (size < 4 || start + size > fs.Length)
                    {
                        Console.WriteLine("Stopped at offset 0x{0:X}: no complete superchunk ({1} bytes left).",
                            start, fs.Length - start);
                        break;
                    }

                    var data = new byte[size - 4];
                    if (!ReadFully(fs, data, data.Length)) break;

                    // Chunks: 24 bit size, 8 bit flags, 2 char id, 2 bytes extra, payload
                    int videoPos = -1, videoLen = 0;
                    int p = 0;
                    while (p + 8 <= data.Length)
                    {
                        int len = Read24(data, p);
                        if (len < 8 || p + len > data.Length) break;
                        string id = Encoding.ASCII.GetString(data, p + 4, 2);
                        if (id == "IV") { videoPos = p + 8; videoLen = len - 8; }
                        else if (id == "PL") ReadPalette(data, p + 8, len - 8, palette);
                        else if (id == "IZ" || id == "IU")
                            Console.WriteLine("Warning: unsupported video chunk {0} at 0x{1:X}", id, start + 4 + p);
                        p += len;
                    }

                    // The palette follows the video chunk, so decode after reading all chunks.
                    if (videoPos < 0) continue;

                    try
                    {
                        int height = decoder.Decode(data, videoPos, videoLen);
                        string file = Path.Combine(outDir, string.Format("frame_{0:D6}.png", frames));
                        PngWriter.WriteIndexed(file, decoder.Frame, UbbVideoDecoder.Width, height, palette);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("Frame {0} (offset 0x{1:X}) failed: {2}", frames, start, ex.Message);
                        return 2;
                    }

                    decoder.SwapBuffers();
                    frames++;
                    if (frames % 100 == 0) Console.WriteLine("{0} frames...", frames);
                }
            }

            Console.WriteLine("Done: {0} frame(s) written to {1}", frames, Path.GetFullPath(outDir));
            return 0;
        }

        static int Read24(byte[] b, int o)
        {
            return b[o] | b[o + 1] << 8 | b[o + 2] << 16;
        }

        static bool ReadFully(Stream s, byte[] buffer, int count)
        {
            int total = 0;
            while (total < count)
            {
                int n = s.Read(buffer, total, count - total);
                if (n <= 0) return false;
                total += n;
            }
            return true;
        }

        // Palette block: [first, count (0 = 256), RGB...] repeated, ends with FF FF. 6 bit colors.
        static void ReadPalette(byte[] d, int pos, int len, byte[] palette)
        {
            int end = pos + len;
            while (pos + 2 <= end)
            {
                int first = d[pos], count = d[pos + 1];
                pos += 2;
                if (first == 0xFF && count == 0xFF) break;
                if (count == 0) count = 256;
                for (int i = first * 3; i < (first + count) * 3; i++)
                {
                    int v = d[pos++] & 0x3F;
                    palette[i] = (byte)(v << 2 | v >> 4);
                }
            }
        }
    }
}
