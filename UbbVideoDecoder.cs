using System;
using System.IO;

namespace UbbExtract
{
    // Decoder for the "IV" video chunk, based on https://wiki.multimedia.cx/index.php/UBB
    // The picture is built line by line from blocks of w x h pixels (h = 2, 3 or 4).
    sealed class UbbVideoDecoder
    {
        public const int Width = 640;
        const int BufferSize = Width * 480;

        byte[] _current = new byte[BufferSize];
        byte[] _previous = new byte[BufferSize];

        public byte[] Frame { get { return _current; } }

        // Block width for a 5 bit index, depending on the block height
        static readonly int[][] Widths =
        {
            new[] {1,2,3,4,5,6,7,8,9,10,11,12,13,14,15,16,18,20,22,24,26,28,30,32,34,36,38,40,44,48,52,56},
            new[] {1,2,3,4,5,6,7,8,9,10,11,12,13,14,15,16,17,18,19,20,22,24,26,28,30,32,34,36,38,40,42,44},
            new[] {1,2,3,4,5,6,7,8,9,10,11,12,13,14,15,16,17,18,19,20,21,22,23,24,25,26,28,30,32,34,36,38}
        };

        // Base offsets of modes 8..31: the same eight pairs for each height
        static readonly int[,] PrevBases =
        {
            {-2,-8}, {-14,-8}, {-8,-2}, {-8,-14}, {-2,-2}, {-14,-2}, {-2,-14}, {-14,-14}
        };

        // Modes 33..39 (and 41..47): base X, base Y, number of X bits
        static readonly int[,] CurBases =
        {
            {-16,-8,5}, {-8,-16,4}, {-24,-16,4}, {8,-16,4}, {-4,-32,3}, {-12,-32,3}, {4,-32,3}
        };

        struct Mode
        {
            public int Height;
            public bool FromPrevious;
            public int AbsoluteBase; // != 0: 16 bit offset follows, else 8 bit X/Y offset
            public int BaseX, BaseY, XBits;
        }

        static Mode GetMode(int m)
        {
            var r = new Mode();
            if (m >= 2 && m <= 4)
            {
                r.Height = m; r.FromPrevious = true; r.AbsoluteBase = -32768;
            }
            else if (m >= 5 && m <= 7)
            {
                r.Height = m - 3; r.FromPrevious = true; r.BaseX = -8; r.BaseY = -8; r.XBits = 4;
            }
            else if (m >= 8 && m <= 31)
            {
                r.Height = 2 + (m - 8) / 8; r.FromPrevious = true; r.XBits = 4;
                r.BaseX = PrevBases[(m - 8) % 8, 0];
                r.BaseY = PrevBases[(m - 8) % 8, 1];
            }
            else if (m >= 32 && m <= 47)
            {
                r.Height = 2 + (m - 32) / 8;
                int k = (m - 32) % 8;
                if (k == 0) r.AbsoluteBase = -65536;
                else
                {
                    r.BaseX = CurBases[k - 1, 0];
                    r.BaseY = CurBases[k - 1, 1];
                    r.XBits = CurBases[k - 1, 2];
                }
            }
            else throw new InvalidDataException("Unknown mode " + m);
            return r;
        }

        public void SwapBuffers()
        {
            var t = _current; _current = _previous; _previous = t;
        }

        // Decodes one frame into Frame and returns its height in lines.
        public int Decode(byte[] s, int pos, int length)
        {
            int end = pos + length;
            int x = 0, y = 0;
            var mode = new Mode();

            while (pos < end)
            {
                int code = s[pos++];

                if (code == 0xE0) // fill, mode change or end
                {
                    int b = s[pos++];
                    if (b == 0)
                    {
                        int count = s[pos++] + 1;
                        byte color = s[pos++];
                        for (int i = 0; i < count; i++)
                        {
                            for (int r = 0; r < mode.Height; r++)
                                _current[(y + r) * Width + x] = color;
                            Advance(ref x, ref y, 1, mode.Height);
                        }
                    }
                    else if (b == 1) return y;
                    else mode = GetMode(b);
                }
                else if (code == 0x20) // skip: keep pixels of the previous frame
                {
                    int count = s[pos++] + 1;
                    for (int i = 0; i < count; i++)
                    {
                        int p = y * Width + x;
                        for (int r = 0; r < mode.Height; r++)
                            _current[p + r * Width] = _previous[p + r * Width];
                        Advance(ref x, ref y, 1, mode.Height);
                    }
                }
                else if (code == 0x60 || code == 0xA0) // raw pixels, column by column
                {
                    int count = code == 0x60 ? 1 : s[pos++] + 2;
                    for (int i = 0; i < count; i++)
                    {
                        int p = y * Width + x;
                        for (int r = 0; r < mode.Height; r++)
                            _current[p + r * Width] = s[pos++];
                        Advance(ref x, ref y, 1, mode.Height);
                    }
                }
                else // block copy
                {
                    int w = Widths[mode.Height - 2][code & 31];
                    int dst = y * Width + x;
                    int src;
                    if (mode.AbsoluteBase != 0)
                    {
                        src = dst + (s[pos] | s[pos + 1] << 8) + mode.AbsoluteBase;
                        pos += 2;
                    }
                    else
                    {
                        int b = s[pos++];
                        int dx = (b & ((1 << mode.XBits) - 1)) + mode.BaseX;
                        int dy = (b >> mode.XBits) + mode.BaseY;
                        src = dst + dx + dy * Width;
                    }

                    bool swap = (code & 0x80) != 0;
                    bool vertical = (code & 0x40) != 0;
                    bool horizontal = (code & 0x20) != 0;

                    // Source step per destination pixel (stepX) and per destination line (stepY)
                    int stepX, stepY;
                    if (swap)
                    {
                        stepX = horizontal ? -Width : Width;
                        stepY = vertical ? -1 : 1;
                    }
                    else
                    {
                        stepX = horizontal ? -1 : 1;
                        stepY = vertical ? -Width : Width;
                    }

                    byte[] source = mode.FromPrevious ? _previous : _current;
                    for (int row = 0; row < mode.Height; row++)
                    {
                        for (int col = 0; col < w; col++)
                        {
                            int q = src + col * stepX + row * stepY;
                            if (q < 0 || q >= BufferSize)
                                throw new InvalidDataException("Copy source outside the picture");
                            _current[dst + row * Width + col] = source[q];
                        }
                    }
                    Advance(ref x, ref y, w, mode.Height);
                }
            }
            throw new InvalidDataException("Video data ended without end code");
        }

        static void Advance(ref int x, ref int y, int w, int h)
        {
            x += w;
            if (x >= Width) { x = 0; y += h; }
        }
    }
}
