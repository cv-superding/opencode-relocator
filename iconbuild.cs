using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;

internal static class IconBuild
{
    private static void Main(string[] args)
    {
        string dir = args.Length > 0 ? args[0] : @"icon-build";
        string ico = args.Length > 1 ? args[1] : @"app.ico";
        int[] sizes = new int[] { 16, 20, 24, 32, 40, 48, 64, 128, 256 };

        var frames = new List<byte[]>();
        var dims = new List<int>();
        foreach (int size in sizes)
        {
            string path = Path.Combine(dir, "icon-" + size + ".png");
            if (!File.Exists(path)) { Console.WriteLine("skip missing " + path); continue; }
            frames.Add(DibFrame(path, size));
            dims.Add(size);
        }

        using (var fs = new FileStream(ico, FileMode.Create, FileAccess.Write))
        using (var w = new BinaryWriter(fs))
        {
            w.Write((ushort)0);
            w.Write((ushort)1);
            w.Write((ushort)frames.Count);

            int offset = 6 + 16 * frames.Count;
            for (int i = 0; i < frames.Count; i++)
            {
                int s = dims[i];
                w.Write((byte)(s >= 256 ? 0 : s));
                w.Write((byte)(s >= 256 ? 0 : s));
                w.Write((byte)0);
                w.Write((byte)0);
                w.Write((ushort)1);
                w.Write((ushort)32);
                w.Write(frames[i].Length);
                w.Write(offset);
                offset += frames[i].Length;
            }
            foreach (byte[] frame in frames) w.Write(frame);
        }

        Console.WriteLine("wrote " + ico + " with " + frames.Count + " sizes");
    }

    // 32bpp DIB 帧（含 AND 掩码），兼容性最好
    private static byte[] DibFrame(string pngPath, int size)
    {
        using (var src = new Bitmap(pngPath))
        using (var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb))
        {
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                g.DrawImage(src, 0, 0, size, size);
            }

            int maskStride = ((size + 31) / 32) * 4;
            var buffer = new byte[40 + size * size * 4 + maskStride * size];

            using (var ms = new MemoryStream(buffer))
            using (var w = new BinaryWriter(ms))
            {
                w.Write(40);
                w.Write(size);
                w.Write(size * 2);
                w.Write((ushort)1);
                w.Write((ushort)32);
                w.Write(0);
                w.Write(0);
                w.Write(0);
                w.Write(0);
                w.Write(0);
                w.Write(0);

                for (int y = size - 1; y >= 0; y--)
                    for (int x = 0; x < size; x++)
                    {
                        Color c = bmp.GetPixel(x, y);
                        w.Write(c.B);
                        w.Write(c.G);
                        w.Write(c.R);
                        w.Write(c.A);
                    }

                // AND 掩码全 0：完全交给 alpha 通道
                for (int i = 0; i < maskStride * size; i++) w.Write((byte)0);
            }
            return buffer;
        }
    }
}
