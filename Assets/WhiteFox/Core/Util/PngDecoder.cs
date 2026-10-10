// WhiteFox - Unity port of Minosoft (https://gitlab.bixilon.de/bixilon/minosoft), Copyright (C) 2020-2023 Moritz Zwerger
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.IO;
using System.IO.Compression;

namespace WhiteFox.Core.Util
{
    /// <summary>Decoded RGBA8 image. Row 0 is the TOP row (PNG order).</summary>
    public sealed class RgbaImage
    {
        public readonly int Width;
        public readonly int Height;
        public readonly byte[] Pixels;

        public RgbaImage(int width, int height, byte[] pixels)
        {
            Width = width;
            Height = height;
            Pixels = pixels;
        }

        public RgbaImage(int width, int height) : this(width, height, new byte[width * height * 4])
        {
        }

        public int GetRgb(int x, int y)
        {
            var i = (y * Width + x) * 4;
            return (Pixels[i] << 16) | (Pixels[i + 1] << 8) | Pixels[i + 2];
        }

        public RgbaImage Crop(int x, int y, int width, int height)
        {
            var result = new RgbaImage(width, height);
            for (var row = 0; row < height; row++)
            {
                Buffer.BlockCopy(Pixels, ((y + row) * Width + x) * 4, result.Pixels, row * width * 4, width * 4);
            }
            return result;
        }

        /// <summary>Nearest neighbour scaling (used to bring textures to the array resolution).</summary>
        public RgbaImage Scale(int width, int height)
        {
            if (width == Width && height == Height) return this;
            var result = new RgbaImage(width, height);
            for (var y = 0; y < height; y++)
            {
                var sy = y * Height / height;
                for (var x = 0; x < width; x++)
                {
                    var sx = x * Width / width;
                    Buffer.BlockCopy(Pixels, (sy * Width + sx) * 4, result.Pixels, (y * width + x) * 4, 4);
                }
            }
            return result;
        }
    }

    /// <summary>
    /// Minimal PNG decoder (no engine dependency, so assets can be processed off the main thread and in tests).
    /// Supports 8 bit grayscale, gray+alpha, RGB, RGBA and 1/2/4/8 bit palette images, non interlaced.
    /// </summary>
    public static class PngDecoder
    {
        private static readonly byte[] Signature = { 137, 80, 78, 71, 13, 10, 26, 10 };

        public static RgbaImage Decode(byte[] data)
        {
            for (var i = 0; i < Signature.Length; i++)
            {
                if (data[i] != Signature[i]) throw new InvalidDataException("Not a png file");
            }

            int width = 0, height = 0, bitDepth = 0, colorType = 0, interlace = 0;
            byte[] palette = null;
            byte[] paletteAlpha = null;
            var idat = new MemoryStream();

            var offset = 8;
            while (offset + 8 <= data.Length)
            {
                var length = ReadInt(data, offset);
                var type = System.Text.Encoding.ASCII.GetString(data, offset + 4, 4);
                var chunk = offset + 8;
                switch (type)
                {
                    case "IHDR":
                        width = ReadInt(data, chunk);
                        height = ReadInt(data, chunk + 4);
                        bitDepth = data[chunk + 8];
                        colorType = data[chunk + 9];
                        interlace = data[chunk + 12];
                        break;
                    case "PLTE":
                        palette = new byte[length];
                        Buffer.BlockCopy(data, chunk, palette, 0, length);
                        break;
                    case "tRNS":
                        paletteAlpha = new byte[length];
                        Buffer.BlockCopy(data, chunk, paletteAlpha, 0, length);
                        break;
                    case "IDAT":
                        idat.Write(data, chunk, length);
                        break;
                }
                if (type == "IEND") break;
                offset = chunk + length + 4; // + crc
            }

            if (interlace != 0) throw new NotSupportedException("Interlaced png is not supported");

            int channels;
            switch (colorType)
            {
                case 0: channels = 1; break;
                case 2: channels = 3; break;
                case 3: channels = 1; break;
                case 4: channels = 2; break;
                case 6: channels = 4; break;
                default: throw new NotSupportedException("Png color type " + colorType);
            }
            if (bitDepth != 8 && !((colorType == 3 || colorType == 0) && bitDepth < 8)) throw new NotSupportedException("Png bit depth " + bitDepth + " for color type " + colorType);

            var bitsPerPixel = channels * bitDepth;
            var stride = (width * bitsPerPixel + 7) / 8;
            var bpp = Math.Max(1, bitsPerPixel / 8);
            var raw = Inflate(idat.ToArray(), (stride + 1) * height);

            var current = new byte[stride];
            var previous = new byte[stride];
            var result = new RgbaImage(width, height);
            var pixels = result.Pixels;
            var src = 0;
            for (var y = 0; y < height; y++)
            {
                var filter = raw[src++];
                Buffer.BlockCopy(raw, src, current, 0, stride);
                src += stride;
                Unfilter(filter, current, previous, bpp);

                for (var x = 0; x < width; x++)
                {
                    var o = (y * width + x) * 4;
                    switch (colorType)
                    {
                        case 0:
                            pixels[o] = pixels[o + 1] = pixels[o + 2] = bitDepth == 8 ? current[x] : (byte)(ReadPacked(current, x, bitDepth) * 255 / ((1 << bitDepth) - 1));
                            pixels[o + 3] = 255;
                            break;
                        case 2:
                            pixels[o] = current[x * 3];
                            pixels[o + 1] = current[x * 3 + 1];
                            pixels[o + 2] = current[x * 3 + 2];
                            pixels[o + 3] = 255;
                            break;
                        case 3:
                        {
                            var index = bitDepth == 8 ? current[x] : ReadPacked(current, x, bitDepth);
                            pixels[o] = palette[index * 3];
                            pixels[o + 1] = palette[index * 3 + 1];
                            pixels[o + 2] = palette[index * 3 + 2];
                            pixels[o + 3] = paletteAlpha != null && index < paletteAlpha.Length ? paletteAlpha[index] : (byte)255;
                            break;
                        }
                        case 4:
                            pixels[o] = pixels[o + 1] = pixels[o + 2] = current[x * 2];
                            pixels[o + 3] = current[x * 2 + 1];
                            break;
                        case 6:
                            Buffer.BlockCopy(current, x * 4, pixels, o, 4);
                            break;
                    }
                }

                var swap = previous;
                previous = current;
                current = swap;
            }
            return result;
        }

        private static int ReadPacked(byte[] line, int x, int bitDepth)
        {
            var bit = x * bitDepth;
            return (line[bit >> 3] >> (8 - bitDepth - (bit & 7))) & ((1 << bitDepth) - 1);
        }

        private static void Unfilter(byte filter, byte[] line, byte[] previous, int bpp)
        {
            switch (filter)
            {
                case 0:
                    return;
                case 1:
                    for (var i = bpp; i < line.Length; i++) line[i] += line[i - bpp];
                    return;
                case 2:
                    for (var i = 0; i < line.Length; i++) line[i] += previous[i];
                    return;
                case 3:
                    for (var i = 0; i < line.Length; i++)
                    {
                        var left = i >= bpp ? line[i - bpp] : 0;
                        line[i] += (byte)((left + previous[i]) >> 1);
                    }
                    return;
                case 4:
                    for (var i = 0; i < line.Length; i++)
                    {
                        int a = i >= bpp ? line[i - bpp] : 0;
                        int b = previous[i];
                        int c = i >= bpp ? previous[i - bpp] : 0;
                        var p = a + b - c;
                        var pa = Math.Abs(p - a);
                        var pb = Math.Abs(p - b);
                        var pc = Math.Abs(p - c);
                        line[i] += (byte)(pa <= pb && pa <= pc ? a : pb <= pc ? b : c);
                    }
                    return;
                default:
                    throw new InvalidDataException("Invalid png filter " + filter);
            }
        }

        /// <summary>zlib = 2 byte header + raw deflate + adler32.</summary>
        private static byte[] Inflate(byte[] zlib, int expected)
        {
            var output = new byte[expected];
            using (var stream = new DeflateStream(new MemoryStream(zlib, 2, zlib.Length - 2), CompressionMode.Decompress))
            {
                var read = 0;
                while (read < expected)
                {
                    var n = stream.Read(output, read, expected - read);
                    if (n <= 0) break;
                    read += n;
                }
                if (read != expected) throw new InvalidDataException("Png data too short");
            }
            return output;
        }

        private static int ReadInt(byte[] data, int offset) => (data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3];
    }
}
