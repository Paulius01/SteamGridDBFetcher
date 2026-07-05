// One-off tool: generates app.ico (Steam-style piston logo, drawn from
// scratch) for embedding into SteamGridDBFetcher.exe.
// Build+run:  csc /r:System.Drawing.dll MakeIcon.cs && MakeIcon.exe

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

static class MakeIcon
{
    static Bitmap Draw(int s)
    {
        var bmp = new Bitmap(s, s);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;

            // dark blue Steam-like gradient disc
            var r = new RectangleF(0.5f, 0.5f, s - 1f, s - 1f);
            using (var lg = new LinearGradientBrush(new Rectangle(0, 0, s, s),
                       Color.FromArgb(42, 71, 94), Color.FromArgb(23, 26, 33), 55f))
                g.FillEllipse(lg, r);

            // piston: small hub bottom-left, rod, big wheel top-right
            float cx1 = s * 0.32f, cy1 = s * 0.70f, r1 = s * 0.135f;
            float cx2 = s * 0.63f, cy2 = s * 0.37f, r2 = s * 0.225f;

            using (var rod = new Pen(Color.White, s * 0.11f))
            {
                rod.StartCap = LineCap.Round;
                rod.EndCap = LineCap.Round;
                g.DrawLine(rod, cx1, cy1, cx2, cy2);
            }
            using (var white = new SolidBrush(Color.White))
            {
                g.FillEllipse(white, cx1 - r1, cy1 - r1, r1 * 2, r1 * 2);
                g.FillEllipse(white, cx2 - r2, cy2 - r2, r2 * 2, r2 * 2);
            }
            using (var hole = new SolidBrush(Color.FromArgb(27, 35, 46)))
            {
                float h1 = r1 * 0.45f, h2 = r2 * 0.48f;
                g.FillEllipse(hole, cx1 - h1, cy1 - h1, h1 * 2, h1 * 2);
                g.FillEllipse(hole, cx2 - h2, cy2 - h2, h2 * 2, h2 * 2);
            }
        }
        return bmp;
    }

    static byte[] Png(Bitmap b)
    {
        using (var ms = new MemoryStream())
        {
            b.Save(ms, ImageFormat.Png);
            return ms.ToArray();
        }
    }

    // Classic uncompressed icon entry (32bpp BGRA + empty AND mask) for
    // maximum compatibility; PNG entries are only safe at 256px.
    static byte[] Bmp(Bitmap b)
    {
        int w = b.Width, h = b.Height;
        using (var ms = new MemoryStream())
        using (var bw = new BinaryWriter(ms))
        {
            bw.Write(40); bw.Write(w); bw.Write(h * 2);
            bw.Write((short)1); bw.Write((short)32);
            bw.Write(0); bw.Write(0); bw.Write(0); bw.Write(0); bw.Write(0); bw.Write(0);
            for (int y = h - 1; y >= 0; y--)
                for (int x = 0; x < w; x++)
                {
                    Color c = b.GetPixel(x, y);
                    bw.Write(c.B); bw.Write(c.G); bw.Write(c.R); bw.Write(c.A);
                }
            var maskRow = new byte[((w + 31) / 32) * 4];
            for (int y = 0; y < h; y++) bw.Write(maskRow);
            bw.Flush();
            return ms.ToArray();
        }
    }

    static void Main()
    {
        int[] sizes = { 16, 24, 32, 48, 64, 128, 256 };
        var entries = new byte[sizes.Length][];
        for (int i = 0; i < sizes.Length; i++)
            using (var b = Draw(sizes[i]))
            {
                entries[i] = sizes[i] >= 256 ? Png(b) : Bmp(b);
                if (sizes[i] == 256) b.Save("icon_preview.png", ImageFormat.Png);
            }

        using (var bw = new BinaryWriter(File.Create("app.ico")))
        {
            bw.Write((short)0);              // reserved
            bw.Write((short)1);              // type: icon
            bw.Write((short)sizes.Length);
            int offset = 6 + 16 * sizes.Length;
            for (int i = 0; i < sizes.Length; i++)
            {
                byte dim = sizes[i] >= 256 ? (byte)0 : (byte)sizes[i];
                bw.Write(dim); bw.Write(dim);
                bw.Write((byte)0); bw.Write((byte)0);
                bw.Write((short)1); bw.Write((short)32);
                bw.Write(entries[i].Length);
                bw.Write(offset);
                offset += entries[i].Length;
            }
            foreach (byte[] e in entries) bw.Write(e);
        }
        Console.WriteLine("app.ico written");
    }
}
