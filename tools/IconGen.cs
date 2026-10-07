// Генератор иконки приложения: src\Gui\app.ico (16–256 px, каждый размер — PNG внутри ICO) и превью bin\icon-preview.png.
// Сборка и запуск: tools\make-icon.cmd
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

static class IconGen
{
    static readonly Color Tile = Color.FromArgb(0x1a, 0x1a, 0x19);
    static readonly Color TileEdge = Color.FromArgb(0x38, 0x38, 0x35);
    static readonly Color Outline = Color.FromArgb(0xe8, 0xe8, 0xe4);
    static readonly Color FillTop = Color.FromArgb(0x55, 0x98, 0xe7);
    static readonly Color FillBottom = Color.FromArgb(0x2a, 0x78, 0xd6);

    static void Main(string[] args)
    {
        int[] sizes = { 16, 20, 24, 32, 40, 48, 64, 256 };
        var pngs = new List<byte[]>();
        foreach (int s in sizes)
            using (var bmp = Render(s))
            using (var ms = new MemoryStream())
            {
                bmp.Save(ms, ImageFormat.Png);
                pngs.Add(ms.ToArray());
            }

        using (var f = File.Create(args[0]))
        using (var w = new BinaryWriter(f))
        {
            w.Write((short)0); w.Write((short)1); w.Write((short)sizes.Length);  // ICONDIR
            int offset = 6 + 16 * sizes.Length;
            for (int i = 0; i < sizes.Length; i++)
            {
                w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i])); w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                w.Write((byte)0); w.Write((byte)0); w.Write((short)1); w.Write((short)32);
                w.Write(pngs[i].Length); w.Write(offset);
                offset += pngs[i].Length;
            }
            foreach (var p in pngs) w.Write(p);
        }

        // Превью: все размеры на светлом и тёмном фоне, мелкие — увеличены в 4 раза.
        using (var sheet = new Bitmap(1100, 360))
        using (var g = Graphics.FromImage(sheet))
        {
            g.Clear(Color.FromArgb(0xf3, 0xf3, 0xf3));
            g.FillRectangle(new SolidBrush(Color.FromArgb(0x20, 0x20, 0x20)), 0, 180, 1100, 180);
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            for (int row = 0; row < 2; row++)
            {
                int x = 20;
                foreach (int s in new[] { 16, 24, 32, 48 })
                    using (var b = Render(s)) { g.DrawImage(b, x, row * 180 + 26, s * 4 > 128 ? 128 : s * 4, s * 4 > 128 ? 128 : s * 4); x += 150; }
                using (var b = Render(256)) g.DrawImage(b, x + 20, row * 180 + 14, 152, 152);
                foreach (int s in new[] { 16, 24, 32 })
                    using (var b = Render(s)) { g.DrawImage(b, x + 220, row * 180 + 70, s, s); x += 50; }
            }
            sheet.Save(args[1], ImageFormat.Png);
        }
    }

    static Bitmap Render(int s)
    {
        var bmp = new Bitmap(s, s, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            float u = s / 32f;  // единица рисунка: сетка 32×32

            // Плитка
            using (var tile = Rounded(new RectangleF(0.5f * u, 0.5f * u, 31 * u, 31 * u), 7 * u))
            {
                using (var b = new SolidBrush(Tile)) g.FillPath(b, tile);
                if (s >= 24) using (var p = new Pen(TileEdge, Math.Max(1f, 0.8f * u))) g.DrawPath(p, tile);
            }

            // Батарейка: корпус, клемма справа, заливка ~65 %
            var body = new RectangleF(5.5f * u, 10f * u, 19f * u, 12f * u);
            float stroke = Math.Max(1.2f, 1.7f * u);
            using (var path = Rounded(body, 2.6f * u))
            using (var p = new Pen(Outline, stroke))
                g.DrawPath(p, path);
            using (var nub = Rounded(new RectangleF(25.2f * u, 13.5f * u, 2.6f * u, 5f * u), 1f * u))
            using (var b = new SolidBrush(Outline))
                g.FillPath(b, nub);

            float pad = stroke + 0.9f * u;
            var fill = new RectangleF(body.X + pad, body.Y + pad, (body.Width - 2 * pad) * 0.66f, body.Height - 2 * pad);
            using (var path = Rounded(fill, 1.3f * u))
            using (var b = new LinearGradientBrush(new PointF(0, fill.Top - 1), new PointF(0, fill.Bottom + 1), FillTop, FillBottom))
                g.FillPath(b, path);

            // «Пульс» поверх батарейки — только на крупных размерах, на мелких он сливается
            if (s >= 32)
            {
                float cy = body.Y + body.Height / 2;
                var pts = new[]
                {
                    new PointF(body.X + 1.5f * u, cy), new PointF(11.5f * u, cy), new PointF(13.3f * u, cy - 3.6f * u),
                    new PointF(15.6f * u, cy + 3.6f * u), new PointF(17.6f * u, cy - 1.4f * u), new PointF(18.8f * u, cy),
                    new PointF(body.Right - 1.5f * u, cy),
                };
                using (var p = new Pen(Color.White, Math.Max(1f, 1.15f * u)) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round })
                    g.DrawLines(p, pts);
            }
        }
        return bmp;
    }

    static GraphicsPath Rounded(RectangleF r, float radius)
    {
        float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        var p = new GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}
