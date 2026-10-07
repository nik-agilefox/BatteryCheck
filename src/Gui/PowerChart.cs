using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace BatteryCheck
{
    /// <summary>Линия графика: значения в ваттах по моментам времени (NaN — нет данных).</summary>
    sealed class ChartSeries
    {
        public string Name;       // в подсказке
        public string ShortName;  // у конца линии
        public Color Color;
        public double[] Values;
    }

    /// <summary>
    /// Линейный график мощности с масштабом по времени. Одна ось — все линии в ваттах; уровень заряда — бледный фон
    /// со своей шкалой 0–100 %. Колесо мыши меняет период (относительно точки под курсором), перетаскивание сдвигает
    /// в прошлое, двойной щелчок возвращает к текущему моменту. Рисуется напрямую в OnRender.
    /// </summary>
    sealed class PowerChart : FrameworkElement
    {
        /// <summary>Готовые периоды, с. Колесо мыши переключает их по шагам.</summary>
        public static readonly double[] Windows = { 60, 300, 600, 1800, 3600, 10800, 36000 };
        public static string[] WindowLabels
        {
            get
            {
                string m = Fmt.MinUnit, h = Fmt.HourUnit;
                return new[] { "1 " + m, "5 " + m, "10 " + m, "30 " + m, "1 " + h, "3 " + h, "10 " + h };
            }
        }
        public static double MaxWindowSeconds { get { return Windows[Windows.Length - 1]; } }

        const double GapSeconds = 35;  // разрыв линии, если между точками больше (в фоне опрос раз в 10 с)
        const double LeftPad = 36, RightPad = 128, TopPad = 8, BottomPad = 22;

        IList<DateTime> times = new DateTime[0];
        IList<ChartSeries> series = new ChartSeries[0];
        IList<DateTime> socTimes = new DateTime[0];
        double[] soc = new double[0];  // уровень заряда, %
        string emptyText = "…";
        readonly Typeface face = new Typeface("Segoe UI");
        readonly Typeface faceBold = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        Theme theme;
        Point? mouse;

        double window = 600;
        DateTime? viewEnd;  // null — «живой» режим: правый край следует за последним замером
        Point? dragFrom;
        DateTime dragEnd;

        public PowerChart()
        {
            ClipToBounds = true;
            SnapsToDevicePixels = true;
            Focusable = false;
        }

        public Theme Theme
        {
            set { theme = value; InvalidateVisual(); }
        }

        /// <summary>Период или положение изменились (колесо, перетаскивание, двойной щелчок).</summary>
        public event Action ViewChanged;

        public double Window
        {
            get { return window; }
            set { window = Math.Max(Windows[0], Math.Min(MaxWindowSeconds, value)); InvalidateVisual(); }
        }

        public bool IsLive { get { return viewEnd == null; } }

        public DateTime ViewEnd
        {
            get { return viewEnd ?? Latest; }
        }

        public DateTime ViewStart
        {
            get { return ViewEnd.AddSeconds(-window); }
        }

        public void GoLive()
        {
            viewEnd = null;
            InvalidateVisual();
        }

        DateTime Latest
        {
            get
            {
                DateTime t = times.Count > 0 ? times[times.Count - 1] : DateTime.MinValue;
                if (socTimes.Count > 0 && socTimes[socTimes.Count - 1] > t) t = socTimes[socTimes.Count - 1];
                return t == DateTime.MinValue ? DateTime.Now : t;
            }
        }

        DateTime Earliest
        {
            get
            {
                DateTime t = DateTime.MaxValue;
                if (times.Count > 0) t = times[0];
                if (socTimes.Count > 0 && socTimes[0] < t) t = socTimes[0];
                return t == DateTime.MaxValue ? DateTime.Now : t;
            }
        }

        /// <summary>
        /// Новые данные; times по возрастанию, у каждой линии столько же значений.
        /// socTimes/soc — уровень заряда для фона (своя временная сетка: в режиме процессов линии реже).
        /// </summary>
        public void SetData(IList<DateTime> times, IList<ChartSeries> series, string emptyText, IList<DateTime> socTimes, double[] soc)
        {
            this.times = times;
            this.series = series;
            this.emptyText = emptyText;
            this.socTimes = socTimes;
            this.soc = soc;
            InvalidateVisual();
        }

        Rect Plot
        {
            get { return new Rect(LeftPad, TopPad, Math.Max(10, ActualWidth - LeftPad - RightPad), Math.Max(10, ActualHeight - TopPad - BottomPad)); }
        }

        // ---------------- Мышь: подсказка, масштаб, сдвиг ----------------

        protected override void OnMouseMove(MouseEventArgs e)
        {
            mouse = e.GetPosition(this);
            if (dragFrom != null && e.LeftButton == MouseButtonState.Pressed)
            {
                double dx = mouse.Value.X - dragFrom.Value.X;
                SetViewEnd(dragEnd.AddSeconds(-dx / Plot.Width * window));
            }
            InvalidateVisual();
        }

        protected override void OnMouseLeave(MouseEventArgs e)
        {
            mouse = null;
            InvalidateVisual();
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
            {
                dragFrom = null;
                if (!IsLive) { GoLive(); Raise(); }
                return;
            }
            dragFrom = e.GetPosition(this);
            dragEnd = ViewEnd;
            CaptureMouse();
            Cursor = Cursors.SizeWE;
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            dragFrom = null;
            ReleaseMouseCapture();
            Cursor = null;
        }

        protected override void OnMouseWheel(MouseWheelEventArgs e)
        {
            int i = Array.IndexOf(Windows, window);
            if (i < 0) i = NearestWindow(window);
            int next = Math.Max(0, Math.Min(Windows.Length - 1, i + (e.Delta > 0 ? -1 : 1)));
            if (next == i) return;
            double newWindow = Windows[next];

            if (!IsLive)
            {
                // Точка под курсором остаётся на месте.
                var plot = Plot;
                double frac = Math.Max(0, Math.Min(1, (plot.Right - e.GetPosition(this).X) / plot.Width));
                DateTime anchor = ViewEnd.AddSeconds(-frac * window);
                window = newWindow;
                SetViewEnd(anchor.AddSeconds(frac * newWindow));
            }
            else
            {
                window = newWindow;  // в живом режиме правый край — «сейчас»
            }
            e.Handled = true;
            Raise();
            InvalidateVisual();
        }

        static int NearestWindow(double w)
        {
            int best = 0;
            for (int i = 1; i < Windows.Length; i++)
                if (Math.Abs(Windows[i] - w) < Math.Abs(Windows[best] - w)) best = i;
            return best;
        }

        /// <summary>Сдвиг: дальше последнего замера — снова живой режим; в прошлое — не дальше начала истории.</summary>
        void SetViewEnd(DateTime end)
        {
            DateTime latest = Latest;
            DateTime min = Earliest.AddSeconds(Math.Min(window, (latest - Earliest).TotalSeconds) * 0.5);
            if (end >= latest.AddSeconds(-0.5)) viewEnd = null;
            else viewEnd = end < min ? min : end;
            Raise();
        }

        void Raise()
        {
            var h = ViewChanged;
            if (h != null) h();
        }

        // ---------------- Отрисовка ----------------

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));  // чтобы ловить мышь по всей площади
            if (theme == null || w < 180 || h < 80) return;

            double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            var plot = Plot;
            DateTime end = ViewEnd, start = end.AddSeconds(-window);
            Func<DateTime, double> X = t => plot.Right - (end - t).TotalSeconds / window * plot.Width;

            // Видимые диапазоны индексов (с запасом по точке слева и справа, чтобы линии доходили до краёв).
            int i0, i1, s0, s1;
            Range(times, start, end, out i0, out i1);
            Range(socTimes, start, end, out s0, out s1);

            dc.PushClip(new RectangleGeometry(new Rect(plot.Left, 0, plot.Width + 1, plot.Bottom + 1)));
            DrawSoc(dc, plot, X, s0, s1);
            dc.Pop();
            DrawSocLabel(dc, plot, s0, s1, ViewEnd, dpi);  // фоновый слой: всё остальное рисуется поверх

            // Шкала Y по видимым данным: «круглый» шаг, 3–5 линий сетки.
            double max = 0;
            bool any = false;
            foreach (var s in series)
                for (int i = i0; i <= i1; i++)
                {
                    double v = s.Values[i];
                    if (!double.IsNaN(v)) { any = true; if (v > max) max = v; }
                }
            double step = NiceStep(Math.Max(max, 5) * 1.1 / 4);
            double top = Math.Ceiling(Math.Max(max, 5) * 1.1 / step) * step;
            Func<double, double> Y = v => plot.Bottom - Math.Max(0, v) / top * plot.Height;

            var gridPen = MakePen(theme.GridLine, 1);
            var axisPen = MakePen(theme.AxisLine, 1);
            var muted = Theme.Brush(theme.Muted);
            for (double v = step; v <= top + 1e-9; v += step)
            {
                double y = Math.Round(Y(v)) + 0.5;
                dc.DrawLine(gridPen, new Point(plot.Left, y), new Point(plot.Right, y));
                DrawText(dc, Text(v.ToString("0.#"), 11, muted, face, dpi), plot.Left - 6, y, 1, true);
            }
            double y0 = Math.Round(plot.Bottom) + 0.5;
            dc.DrawLine(axisPen, new Point(plot.Left, y0), new Point(plot.Right, y0));
            DrawText(dc, Text("0", 11, muted, face, dpi), plot.Left - 6, y0, 1, true);
            DrawTimeAxis(dc, plot, X, start, end, muted, dpi);

            if (!any)
            {
                DrawText(dc, Text(emptyText, 13, muted, face, dpi), plot.Left + plot.Width / 2, plot.Top + plot.Height / 2, 0, true);
                return;
            }

            dc.PushClip(new RectangleGeometry(new Rect(plot.Left, plot.Top - 2, plot.Width + 2, plot.Height + 4)));
            foreach (var s in series)
                DrawLine(dc, s, X, Y, i0, i1, plot);
            dc.Pop();

            DrawEndLabels(dc, plot, X, Y, end, i0, i1, dpi);
            DrawHover(dc, plot, X, Y, i0, i1, dpi);
        }

        /// <summary>Индексы точек, попадающих в [start, end], плюс по одной соседней с каждой стороны.</summary>
        static void Range(IList<DateTime> t, DateTime start, DateTime end, out int from, out int to)
        {
            from = LowerBound(t, start) - 1;
            to = LowerBound(t, end.AddTicks(1));
            if (from < 0) from = 0;
            if (to > t.Count - 1) to = t.Count - 1;
        }

        static int LowerBound(IList<DateTime> t, DateTime x)
        {
            int lo = 0, hi = t.Count;
            while (lo < hi)
            {
                int mid = (lo + hi) / 2;
                if (t[mid] < x) lo = mid + 1; else hi = mid;
            }
            return lo;
        }

        /// <summary>
        /// Линия 2 px. Если точек больше, чем пикселей, в каждом столбце пикселей остаются минимум и максимум —
        /// пики не теряются, а рисовать в разы меньше.
        /// </summary>
        void DrawLine(DrawingContext dc, ChartSeries s, Func<DateTime, double> X, Func<double, double> Y, int i0, int i1, Rect plot)
        {
            if (i1 < i0) return;
            bool decimate = (i1 - i0) > plot.Width * 2;
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                bool open = false;
                DateTime last = DateTime.MinValue;
                int bucket = int.MinValue;
                double bMin = 0, bMax = 0, bMinX = 0, bMaxX = 0;
                bool bMinFirst = true;
                Action flush = null;
                flush = () =>
                {
                    if (bucket == int.MinValue) return;
                    var a = new Point(bMinFirst ? bMinX : bMaxX, Y(bMinFirst ? bMin : bMax));
                    var b = new Point(bMinFirst ? bMaxX : bMinX, Y(bMinFirst ? bMax : bMin));
                    if (!open) { ctx.BeginFigure(a, false, false); open = true; } else ctx.LineTo(a, true, true);
                    ctx.LineTo(b, true, true);
                    bucket = int.MinValue;
                };

                for (int i = i0; i <= i1; i++)
                {
                    double v = s.Values[i];
                    bool gap = double.IsNaN(v) || (open || bucket != int.MinValue) && (times[i] - last).TotalSeconds > GapSeconds;
                    if (gap)
                    {
                        if (decimate) flush();
                        open = false;
                        if (double.IsNaN(v)) continue;
                    }
                    double x = X(times[i]);
                    last = times[i];
                    if (!decimate)
                    {
                        var pt = new Point(x, Y(v));
                        if (!open) { ctx.BeginFigure(pt, false, false); open = true; } else ctx.LineTo(pt, true, true);
                        continue;
                    }
                    int col = (int)Math.Floor(x);
                    if (col != bucket)
                    {
                        flush();
                        bucket = col;
                        bMin = bMax = v;
                        bMinX = bMaxX = x;
                        bMinFirst = true;
                    }
                    else
                    {
                        if (v < bMin) { bMin = v; bMinX = x; bMinFirst = bMinX <= bMaxX; }
                        if (v > bMax) { bMax = v; bMaxX = x; bMinFirst = bMinX <= bMaxX; }
                    }
                }
                if (decimate) flush();
            }
            g.Freeze();
            dc.DrawGeometry(null, MakePen(s.Color, 2), g);
        }

        /// <summary>
        /// Подписи времени. В живом режиме — «сколько назад» («−10 мин … сейчас»), при сдвиге в прошлое — часы.
        /// </summary>
        void DrawTimeAxis(DrawingContext dc, Rect plot, Func<DateTime, double> X, DateTime start, DateTime end, Brush brush, double dpi)
        {
            double[] steps = { 10, 15, 30, 60, 120, 300, 600, 900, 1800, 3600, 7200 };
            double step = steps[steps.Length - 1];
            foreach (double s in steps)
                if (window / s <= 6) { step = s; break; }

            if (IsLive)
            {
                // Часы — только для длинных периодов: на «1 ч» шаг 10 мин дал бы −0,8, −0,7…
                bool hours = window >= 10800, minutes = !hours && window >= 120;
                for (double back = window; back >= -1e-6; back -= step)
                {
                    double x = plot.Right - back / window * plot.Width;
                    string label;
                    if (back < 1e-6) label = L.T("now", "зараз");
                    else if (hours) label = "−" + (back / 3600).ToString("0.#") + (back == window ? " " + Fmt.HourUnit : "");
                    else if (minutes) label = "−" + (back / 60).ToString("0.#") + (back == window ? " " + Fmt.MinUnit : "");
                    else label = "−" + back.ToString("0") + (back == window ? " " + Fmt.SecUnit : "");
                    int align = back == window ? -1 : back < 1e-6 ? 1 : 0;
                    DrawText(dc, Text(label, 11, brush, face, dpi), x, plot.Bottom + 5, align, false);
                }
                return;
            }

            // Часы: отметки на «круглом» времени.
            long stepTicks = TimeSpan.FromSeconds(step).Ticks;
            var first = new DateTime((start.Ticks + stepTicks - 1) / stepTicks * stepTicks, start.Kind);
            string fmt = step < 60 ? "HH:mm:ss" : "HH:mm";
            for (var t = first; t <= end; t = t.AddTicks(stepTicks))
            {
                double x = X(t);
                var ft = Text(t.ToString(fmt), 11, brush, face, dpi);
                if (x - ft.Width / 2 < plot.Left - 4 || x + ft.Width / 2 > plot.Right + RightPad - 4) continue;
                DrawText(dc, ft, x, plot.Bottom + 5, 0, false);
            }
        }

        /// <summary>
        /// Уровень заряда — очень бледная нейтральная заливка на всю высоту (верх области = 100 %) с тонкой кромкой.
        /// Своей оси нет намеренно: шкала названа в легенде, значение — в подсказке, чтобы проценты не путали с ваттами.
        /// </summary>
        void DrawSoc(DrawingContext dc, Rect plot, Func<DateTime, double> X, int s0, int s1)
        {
            if (soc.Length == 0 || s1 < s0) return;
            Func<double, double> Ys = p => plot.Bottom - Math.Max(0, Math.Min(100, p)) / 100 * plot.Height;

            var c = theme.TextPrimary;
            var fill = Theme.Brush(Color.FromArgb((byte)(theme.IsDark ? 14 : 12), c.R, c.G, c.B));
            var edge = MakePen(Color.FromArgb((byte)(theme.IsDark ? 56 : 46), c.R, c.G, c.B), 1);

            // Непрерывные куски [s..e] без пропусков: заливка до нуля и кромка сверху.
            // Заряд меняется медленно, поэтому при густых точках достаточно одной на пиксель.
            int stride = Math.Max(1, (int)((s1 - s0) / Math.Max(1, plot.Width)));
            int n = Math.Min(s1 + 1, soc.Length), i = s0;
            while (i < n)
            {
                while (i < n && double.IsNaN(soc[i])) i++;
                int s = i;
                while (i + 1 < n && !double.IsNaN(soc[i + 1]) && (socTimes[i + 1] - socTimes[i]).TotalSeconds <= GapSeconds) i++;
                int e = i;
                i++;
                if (s >= n || e == s) continue;

                var area = new StreamGeometry();
                var line = new StreamGeometry();
                using (var a = area.Open())
                using (var l = line.Open())
                {
                    a.BeginFigure(new Point(X(socTimes[s]), plot.Bottom), true, true);
                    l.BeginFigure(new Point(X(socTimes[s]), Ys(soc[s])), false, false);
                    for (int j = s; j <= e; j = j == e ? e + 1 : Math.Min(e, j + stride))
                    {
                        var pt = new Point(X(socTimes[j]), Ys(soc[j]));
                        a.LineTo(pt, false, false);
                        if (j > s) l.LineTo(pt, true, true);
                    }
                    a.LineTo(new Point(X(socTimes[e]), plot.Bottom), false, false);
                }
                area.Freeze();
                line.Freeze();
                dc.DrawGeometry(fill, null, area);
                dc.DrawGeometry(null, edge, line);
            }
        }

        /// <summary>
        /// Подпись фона — только «58 %», справа от графика (в колонке подписей линий), на уровне кромки.
        /// Рисуется до остальных подписей и без подложки: при совпадении по высоте подписи линий оказываются сверху.
        /// </summary>
        void DrawSocLabel(DrawingContext dc, Rect plot, int s0, int s1, DateTime end, double dpi)
        {
            if (soc.Length == 0 || s1 < s0) return;
            int i = Math.Min(s1, soc.Length - 1);
            while (i >= s0 && (socTimes[i] > end || double.IsNaN(soc[i]))) i--;
            if (i < s0 || (end - socTimes[i]).TotalSeconds > GapSeconds) return;

            double y = plot.Bottom - Math.Max(0, Math.Min(100, soc[i])) / 100 * plot.Height;
            DrawText(dc, Text(soc[i].ToString("0") + " %", 11, Theme.Brush(theme.Muted), face, dpi), plot.Right + 10, y, -1, true);
        }

        double SocAt(DateTime t)
        {
            int k = LowerBound(socTimes, t);
            int best = -1;
            double bestDist = double.MaxValue;
            for (int i = Math.Max(0, k - 1); i <= Math.Min(socTimes.Count - 1, k); i++)
            {
                double d = Math.Abs((socTimes[i] - t).TotalSeconds);
                if (d < bestDist) { bestDist = d; best = i; }
            }
            return best >= 0 && bestDist <= GapSeconds ? soc[best] : double.NaN;
        }

        /// <summary>Маркер на конце каждой линии (у правого края видимой области) и подпись «значение + имя», без наложений.</summary>
        void DrawEndLabels(DrawingContext dc, Rect plot, Func<DateTime, double> X, Func<double, double> Y, DateTime end, int i0, int i1, double dpi)
        {
            var items = new List<double[]>();  // [линия, x маркера, y маркера, y подписи, значение]
            for (int s = 0; s < series.Count; s++)
            {
                var vals = series[s].Values;
                for (int i = i1; i >= i0; i--)
                {
                    if (times[i] > end) continue;
                    if ((end - times[i]).TotalSeconds > GapSeconds) break;  // у края давно нет данных — не подписываем
                    if (double.IsNaN(vals[i])) continue;
                    items.Add(new[] { s, X(times[i]), Y(vals[i]), Y(vals[i]), vals[i] });
                    break;
                }
            }
            items.Sort((a, b) => a[3].CompareTo(b[3]));
            const double minGap = 17;
            for (int i = 1; i < items.Count; i++)
                if (items[i][3] < items[i - 1][3] + minGap) items[i][3] = items[i - 1][3] + minGap;
            for (int i = items.Count - 1; i >= 0; i--)
            {
                double limit = i == items.Count - 1 ? plot.Bottom : items[i + 1][3] - minGap;
                if (items[i][3] > limit) items[i][3] = limit;
            }

            var surfacePen = MakePen(theme.Surface, 2);
            var text = Theme.Brush(theme.TextPrimary);
            var text2 = Theme.Brush(theme.TextSecondary);
            foreach (var it in items)
            {
                var s = series[(int)it[0]];
                dc.DrawEllipse(Theme.Brush(s.Color), surfacePen, new Point(it[1], it[2]), 4, 4);
                var value = Text(Fmt.Num(it[4], "0.0") + " " + Fmt.WUnit, 12, text, faceBold, dpi);
                double x = plot.Right + 10;
                DrawText(dc, value, x, it[3], -1, true);
                var name = Text(s.ShortName, 11, Theme.Brush(s.Color), faceBold, dpi);  // название — в цвете своей линии
                name.MaxTextWidth = Math.Max(10, RightPad - 18 - value.Width);
                name.MaxLineCount = 1;
                name.Trimming = TextTrimming.CharacterEllipsis;
                DrawText(dc, name, x + value.Width + 5, it[3], -1, true);
            }
        }

        /// <summary>Перекрестие и подсказка со значениями всех линий в ближайшей к курсору точке (не дальше 12 px).</summary>
        void DrawHover(DrawingContext dc, Rect plot, Func<DateTime, double> X, Func<double, double> Y, int i0, int i1, double dpi)
        {
            if (mouse == null || dragFrom != null || times.Count == 0 || i1 < i0) return;
            Point m = mouse.Value;
            if (m.X < plot.Left || m.X > plot.Right + 4 || m.Y < plot.Top || m.Y > plot.Bottom) return;

            DateTime t = ViewEnd.AddSeconds(-(plot.Right - m.X) / plot.Width * window);
            int k = Math.Max(i0, Math.Min(i1, LowerBound(times, t)));
            int best = -1;
            double bestDx = double.MaxValue;
            for (int i = Math.Max(i0, k - 1); i <= Math.Min(i1, k + 1); i++)
            {
                double dx = Math.Abs(X(times[i]) - m.X);
                if (dx < bestDx) { bestDx = dx; best = i; }
            }
            if (best < 0 || bestDx > 12) return;
            double x = Math.Round(X(times[best])) + 0.5;

            dc.DrawLine(MakePen(theme.AxisLine, 1), new Point(x, plot.Top), new Point(x, plot.Bottom));
            var surfacePen = MakePen(theme.Surface, 2);
            foreach (var s in series)
            {
                double v = s.Values[best];
                if (!double.IsNaN(v)) dc.DrawEllipse(Theme.Brush(s.Color), surfacePen, new Point(x, Y(v)), 4, 4);
            }

            var text = Theme.Brush(theme.TextPrimary);
            var text2 = Theme.Brush(theme.TextSecondary);
            var header = Text(times[best].ToString("HH:mm:ss"), 12, text2, face, dpi);
            var rows = new List<FormattedText[]>();
            double nameW = 0, valueW = 0;
            foreach (var s in series)
            {
                var n = Text(s.Name, 12, text2, face, dpi);
                var v = Text(Fmt.W(s.Values[best]), 12, text, faceBold, dpi);
                nameW = Math.Max(nameW, n.Width);
                valueW = Math.Max(valueW, v.Width);
                rows.Add(new[] { n, v });
            }
            double socNow = SocAt(times[best]);
            FormattedText[] socRow = null;
            if (!double.IsNaN(socNow))
            {
                socRow = new[] { Text(L.T("Battery charge", "Заряд батареї"), 12, text2, face, dpi), Text(socNow.ToString("0.0") + " %", 12, text, faceBold, dpi) };
                nameW = Math.Max(nameW, socRow[0].Width);
                valueW = Math.Max(valueW, socRow[1].Width);
            }
            const double pad = 10, rowH = 19;
            double boxW = Math.Max(header.Width, 14 + nameW + 16 + valueW) + pad * 2;
            double boxH = pad * 2 + rowH * (rows.Count + 1 + (socRow != null ? 1 : 0)) - 3;
            double bx = x + 14;
            if (bx + boxW > ActualWidth - 2) bx = x - 14 - boxW;
            double by = Math.Max(plot.Top, Math.Min(m.Y - boxH / 2, plot.Bottom - boxH));

            dc.DrawRoundedRectangle(Theme.Brush(theme.Surface), MakePen(theme.BorderColor, 1), new Rect(bx, by, boxW, boxH), 6, 6);
            DrawText(dc, header, bx + pad, by + pad + rowH / 2 - 2, -1, true);
            for (int i = 0; i < rows.Count; i++)
            {
                double ry = by + pad + rowH * (i + 1) + rowH / 2 - 2;
                dc.DrawEllipse(Theme.Brush(series[i].Color), null, new Point(bx + pad + 4, ry), 4, 4);
                DrawText(dc, rows[i][0], bx + pad + 14, ry, -1, true);
                DrawText(dc, rows[i][1], bx + boxW - pad, ry, 1, true);
            }
            if (socRow != null)
            {
                // Отдельная строка после разделителя: проценты, а не ватты.
                double ry = by + pad + rowH * (rows.Count + 1) + rowH / 2 - 2;
                dc.DrawLine(MakePen(theme.GridLine, 1), new Point(bx + pad, ry - rowH / 2 + 1), new Point(bx + boxW - pad, ry - rowH / 2 + 1));
                var c = theme.TextPrimary;
                dc.DrawRectangle(Theme.Brush(Color.FromArgb(70, c.R, c.G, c.B)), null, new Rect(bx + pad, ry - 4, 8, 8));
                DrawText(dc, socRow[0], bx + pad + 14, ry, -1, true);
                DrawText(dc, socRow[1], bx + boxW - pad, ry, 1, true);
            }
        }

        static double NiceStep(double raw)
        {
            if (raw <= 0) return 1;
            double mag = Math.Pow(10, Math.Floor(Math.Log10(raw)));
            double n = raw / mag;
            double nice = n <= 1 ? 1 : n <= 2 ? 2 : n <= 2.5 ? 2.5 : n <= 5 ? 5 : 10;
            return nice * mag;
        }

        static Pen MakePen(Color c, double thickness)
        {
            var p = new Pen(Theme.Brush(c), thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
            p.Freeze();
            return p;
        }

        static FormattedText Text(string s, double size, Brush brush, Typeface tf, double dpi)
        {
            return new FormattedText(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, tf, size, brush, dpi);
        }

        /// <summary>align: -1 — от x вправо, 0 — по центру, 1 — до x; vcenter — y это середина строки.</summary>
        static void DrawText(DrawingContext dc, FormattedText t, double x, double y, int align, bool vcenter)
        {
            double tx = align < 0 ? x : align == 0 ? x - t.Width / 2 : x - t.Width;
            double ty = vcenter ? y - t.Height / 2 : y;
            dc.DrawText(t, new Point(tx, ty));
        }
    }
}
