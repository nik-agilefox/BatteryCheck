using System;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace BatteryCheck
{
    /// <summary>
    /// Мини-график мощности за последние 5 минут для плитки: линия — вся мощность (от блока питания или из батареи),
    /// заливка снизу — часть, уходящая в аккумулятор (если есть). С пределом (мощность зарядного) верх графика — предел,
    /// он отмечен пунктиром; без предела шкала подстраивается под данные. Сверху — подписи: слева предел или период,
    /// справа — текущая доля от предела.
    /// </summary>
    sealed class PowerSpark : FrameworkElement
    {
        public const double SpanSeconds = 300;
        const double GapSeconds = 35, TextRow = 18;

        DateTime[] times = new DateTime[0];
        double[] values = new double[0];
        double[] part;  // часть мощности, уходящая в аккумулятор; null — не показывать
        DateTime now = DateTime.Now;
        double limit = double.NaN;
        string topLeft = "", topRight = "";
        Color color = Colors.Gray, partColor = Colors.Green;
        Theme theme;
        readonly Typeface face = new Typeface("Segoe UI");
        readonly Typeface faceBold = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

        public PowerSpark()
        {
            Width = 116;
            SnapsToDevicePixels = true;
            Cursor = Cursors.Hand;
        }

        public void Set(DateTime[] times, double[] values, double[] part, Color partColor, DateTime now, double limit,
                        string topLeft, string topRight, Color color, Theme theme)
        {
            this.times = times;
            this.values = values;
            this.part = part;
            this.partColor = partColor;
            this.now = now;
            this.limit = limit;
            this.topLeft = topLeft ?? "";
            this.topRight = topRight ?? "";
            this.color = color;
            this.theme = theme;
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));  // щелчок по всей площади
            if (theme == null || h < TextRow + 12) return;
            double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

            dc.DrawText(Text(topLeft, 11, Theme.Brush(theme.Muted), face, dpi), new Point(0, 0));
            var right = Text(topRight, 11, Theme.Brush(theme.TextSecondary), faceBold, dpi);
            dc.DrawText(right, new Point(w - right.Width, 0));

            var plot = new Rect(0, TextRow + 2, w, h - TextRow - 4);
            DateTime start = now.AddSeconds(-SpanSeconds);
            double max = limit;
            if (double.IsNaN(max) || max <= 0)
            {
                max = 0;
                for (int i = 0; i < times.Length; i++)
                    if (times[i] >= start && !double.IsNaN(values[i]) && values[i] > max) max = values[i];
                max = Math.Max(5, max * 1.15);
            }
            Func<DateTime, double> X = t => plot.Right - (now - t).TotalSeconds / SpanSeconds * plot.Width;
            Func<double, double> Y = v => plot.Bottom - Math.Max(0, Math.Min(v, max)) / max * plot.Height;

            var axis = new Pen(Theme.Brush(theme.AxisLine), 1);
            axis.Freeze();
            double yb = Math.Round(plot.Bottom) + 0.5;
            dc.DrawLine(axis, new Point(plot.Left, yb), new Point(plot.Right, yb));
            if (!double.IsNaN(limit))
            {
                var dash = new Pen(Theme.Brush(theme.AxisLine), 1) { DashStyle = new DashStyle(new double[] { 2, 3 }, 0) };
                dash.Freeze();
                double yt = Math.Round(plot.Top) + 0.5;
                dc.DrawLine(dash, new Point(plot.Left, yt), new Point(plot.Right, yt));
            }

            dc.PushClip(new RectangleGeometry(new Rect(plot.Left, plot.Top - 2, plot.Width, plot.Height + 3)));
            if (part != null) DrawArea(dc, part, X, Y, plot, start, Theme.Brush(Color.FromArgb(150, partColor.R, partColor.G, partColor.B)));
            dc.Pop();

            // Заливка под линией (бледный шаг того же цвета) и сама линия; разрыв на пропусках.
            var fill = Theme.Brush(Color.FromArgb(theme.IsDark ? (byte)40 : (byte)34, color.R, color.G, color.B));
            var line = new Pen(Theme.Brush(color), 1.5) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            line.Freeze();
            int first = 0;
            while (first < times.Length && times[first] < start.AddSeconds(-GapSeconds)) first++;
            Point? lastPt = null;
            int i0 = first;
            while (i0 < times.Length)
            {
                while (i0 < times.Length && double.IsNaN(values[i0])) i0++;
                int i1 = i0;
                while (i1 + 1 < times.Length && !double.IsNaN(values[i1 + 1]) && (times[i1 + 1] - times[i1]).TotalSeconds <= GapSeconds) i1++;
                if (i0 < times.Length && i1 > i0)
                {
                    var area = new StreamGeometry();
                    var stroke = new StreamGeometry();
                    using (var a = area.Open())
                    using (var s = stroke.Open())
                    {
                        a.BeginFigure(new Point(X(times[i0]), plot.Bottom), true, true);
                        s.BeginFigure(new Point(X(times[i0]), Y(values[i0])), false, false);
                        for (int j = i0; j <= i1; j++)
                        {
                            var p = new Point(X(times[j]), Y(values[j]));
                            a.LineTo(p, false, false);
                            if (j > i0) s.LineTo(p, true, true);
                        }
                        a.LineTo(new Point(X(times[i1]), plot.Bottom), false, false);
                    }
                    area.Freeze();
                    stroke.Freeze();
                    dc.PushClip(new RectangleGeometry(new Rect(plot.Left, plot.Top - 2, plot.Width, plot.Height + 3)));
                    dc.DrawGeometry(fill, null, area);
                    dc.DrawGeometry(null, line, stroke);
                    dc.Pop();
                }
                if (i0 < times.Length) lastPt = new Point(X(times[i1]), Y(values[i1]));
                i0 = i1 + 1;
            }
            if (lastPt != null && (now - times[times.Length - 1]).TotalSeconds <= GapSeconds)
            {
                var ring = new Pen(Theme.Brush(theme.Surface), 2);
                ring.Freeze();
                dc.DrawEllipse(Theme.Brush(color), ring, lastPt.Value, 3, 3);
            }
        }

        /// <summary>Заливка от нуля до значений (часть, уходящая в аккумулятор), с разрывами на пропусках.</summary>
        void DrawArea(DrawingContext dc, double[] v, Func<DateTime, double> X, Func<double, double> Y, Rect plot, DateTime start, Brush brush)
        {
            int i0 = 0;
            while (i0 < times.Length && times[i0] < start.AddSeconds(-GapSeconds)) i0++;
            while (i0 < times.Length)
            {
                while (i0 < times.Length && double.IsNaN(v[i0])) i0++;
                int i1 = i0;
                while (i1 + 1 < times.Length && !double.IsNaN(v[i1 + 1]) && (times[i1 + 1] - times[i1]).TotalSeconds <= GapSeconds) i1++;
                if (i0 < times.Length && i1 > i0)
                {
                    var g = new StreamGeometry();
                    using (var a = g.Open())
                    {
                        a.BeginFigure(new Point(X(times[i0]), plot.Bottom), true, true);
                        for (int j = i0; j <= i1; j++) a.LineTo(new Point(X(times[j]), Y(v[j])), false, false);
                        a.LineTo(new Point(X(times[i1]), plot.Bottom), false, false);
                    }
                    g.Freeze();
                    dc.DrawGeometry(brush, null, g);
                }
                i0 = i1 + 1;
            }
        }

        static FormattedText Text(string s, double size, Brush brush, Typeface tf, double dpi)
        {
            return new FormattedText(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, tf, size, brush, dpi);
        }
    }
}
