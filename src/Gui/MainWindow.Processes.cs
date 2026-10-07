using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;

namespace BatteryCheck
{
    /// <summary>Режимы графика «Компоненты» и «Процессы», история данных, таблица процессов.</summary>
    sealed partial class MainWindow
    {
        enum ChartMode { Components = 0, Processes = 1, History = 2, Advice = 3, Optimize = 4 }

        const int TopProcesses = 5;     // на графике и в таблице; остальные — одной линией
        const int ShortNameLength = 12;

        ChartMode mode;
        readonly History history = new History();
        Snapshot lastSnap;
        Segmented modeSwitch, rangeSwitch;
        FlatButton liveButton;
        TextBlock chartTitle, procAvgHeader;
        WrapPanel legendPanel;
        string legendKey;
        TextBlock leftCardTitle;
        FrameworkElement componentsView, processesView;
        ProcRow[] procRows;
        ProcRow othersRow, unattributedRow;
        TextBlock procNote;

        // Цвет закрепляется за процессом, а не за местом в рейтинге: при перестановках цвета не скачут.
        readonly Dictionary<string, int> procSlots = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        // Результаты последнего построения графика процессов — для таблицы
        List<string> topNames = new List<string>();
        Dictionary<string, double> procAvg = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        double othersAvg = double.NaN, unattributedAvg = double.NaN;

        /// <summary>Сбор по процессам дорогой, поэтому включён, только когда его видно.</summary>
        public void SyncProcessCollection()
        {
            sampler.ProcessesEnabled = IsOnScreen && (mode == ChartMode.Processes || mode == ChartMode.Optimize) && calPhase < 0;  // и пауза на замер экрана
        }

        void ApplyMode()
        {
            bool procs = mode == ChartMode.Processes;
            if (leftCardTitle != null) leftCardTitle.Text = procs ? L.T("Processes · estimate", "Процеси · оцінка") : L.T("Components", "Компоненти");
            if (componentsView != null) componentsView.Visibility = procs ? Visibility.Collapsed : Visibility.Visible;
            if (processesView != null) processesView.Visibility = procs ? Visibility.Visible : Visibility.Collapsed;

            bool hist = mode == ChartMode.History, advice = mode == ChartMode.Advice, optimize = mode == ChartMode.Optimize;
            if (chartBody != null) chartBody.Visibility = hist || advice || optimize ? Visibility.Collapsed : Visibility.Visible;
            if (optimizeView != null) optimizeView.Visibility = optimize ? Visibility.Visible : Visibility.Collapsed;
            if (optimize && optimizeTable != null) RefreshOptimize(true);
            if (historyView != null) historyView.Visibility = hist ? Visibility.Visible : Visibility.Collapsed;
            if (adviceView != null) adviceView.Visibility = advice ? Visibility.Visible : Visibility.Collapsed;
            if (hist && historyGrid != null) RefreshHistory(true);
            if (advice && adviceItems != null) RefreshAdvice(true);
            UpdateChartTitle();

            SyncProcessCollection();
            RenderChart();
            UpdateProcessTable();
        }

        // ---------------- График ----------------

        /// <summary>Период или сдвиг графика изменились: подпись, переключатель, кнопка «К текущему» и пересчёт по видимому периоду.</summary>
        void OnChartViewChanged()
        {
            int i = Array.IndexOf(PowerChart.Windows, chart.Window);
            if (i >= 0) rangeSwitch.Select(i);
            SaveChartWindow(chart.Window);
            UpdateChartTitle();
            RenderChart();
            UpdateProcessTable();
        }

        void UpdateChartTitle()
        {
            if (chartTitle == null) return;
            if (mode == ChartMode.History || mode == ChartMode.Advice || mode == ChartMode.Optimize)
            {
                chartTitle.Text = mode == ChartMode.History ? L.T("Discharge history", "Історія розрядів")
                                : mode == ChartMode.Advice ? L.T("Recommendations", "Рекомендації")
                                : L.T("Third-party services", "Сторонні служби");
                liveButton.Visibility = Visibility.Collapsed;
                return;
            }
            if (chart.IsLive)
            {
                chartTitle.Text = PowerTitle + " · " + PeriodText(chart.Window, true);
            }
            else
            {
                string fmt = chart.Window < 600 ? "HH:mm:ss" : "HH:mm";
                chartTitle.Text = string.Format("{0} · {1}–{2}", PowerTitle, chart.ViewStart.ToString(fmt), chart.ViewEnd.ToString(fmt));
            }
            liveButton.Visibility = chart.IsLive ? Visibility.Collapsed : Visibility.Visible;
            if (procAvgHeader != null) procAvgHeader.Text = chart.IsLive ? L.T("avg ", "сер. за ") + PeriodText(chart.Window, false) : AvgPeriodHeader;
        }

        /// <summary>«последние 10 минут» / «10 мин»</summary>
        static string PeriodText(double seconds, bool longForm)
        {
            int i = Array.IndexOf(PowerChart.Windows, seconds);
            if (!longForm) return i >= 0 ? PowerChart.WindowLabels[i] : Fmt.Hours(seconds / 3600);
            if (seconds < 3600)
            {
                int m = (int)Math.Round(seconds / 60);
                return m == 1 ? L.T("last minute", "остання хвилина")
                    : L.T("last ", "останні ") + m + " " + L.Plural(m, "minute", "minutes", "хвилина", "хвилини", "хвилин");
            }
            int h = (int)Math.Round(seconds / 3600);
            return h == 1 ? L.T("last hour", "остання година")
                : L.T("last ", "останні ") + h + " " + L.Plural(h, "hour", "hours", "година", "години", "годин");
        }

        DateTime lastChartRender = DateTime.MinValue;

        void RenderChart()
        {
            if (!IsOnScreen || theme == null) return;
            lastChartRender = DateTime.UtcNow;
            if (mode == ChartMode.History)
            {
                RefreshHistory(false);  // раз в минуту, пока вкладка открыта
                return;
            }
            if (mode == ChartMode.Advice)
            {
                RefreshAdvice(false);
                return;
            }
            if (mode == ChartMode.Optimize)
            {
                RefreshOptimize(false);
                return;
            }
            // Графику — только копии: история растёт каждую секунду, а перерисовка на длинных периодах реже,
            // и живой список времени разошёлся бы по длине со скопированными значениями.
            var allTimes = history.Times.ToArray();
            IList<DateTime> times;
            List<ChartSeries> series;
            string empty;
            if (mode == ChartMode.Components)
            {
                times = allTimes;
                // Слагаемые не пересекаются: процессор без графики + встроенная графика + NVIDIA + остальное = батарея.
                bool discharging = lastSnap != null && lastSnap.Sample.Battery.Discharging;
                var cpu = new double[history.Cpu.Count];
                for (int i = 0; i < cpu.Length; i++) cpu[i] = CpuWithoutGraphics(history.Cpu[i], history.Igpu[i]);
                string gpuName = lastSnap != null && lastSnap.GpuName != null ? lastSnap.GpuName : L.T("GPU", "відеокарта");
                // «Всего» — потребление системы: от батареи — измеренный разряд, от сети — оценка (процессор + видеокарты +
                // «остальное», без мощности на заряд аккумулятора); «остальное» от сети — последнее измеренное от батареи.
                series = new List<ChartSeries>
                {
                    new ChartSeries { Name = !HasBattery ? L.T("CPU + graphics", "Процесор + графіка") : discharging ? L.T("Total (battery, measured)", "Всього (батарея, виміряно)") : L.T("Total (AC, estimate)", "Всього (від мережі, оцінка)"), ShortName = L.T("Total", "Всього"), Color = theme.Battery, Values = history.SystemPower.ToArray() },
                    new ChartSeries { Name = CpuNoGraphicsText, ShortName = "CPU", Color = theme.Cpu, Values = cpu },
                    new ChartSeries { Name = L.T("Discrete: ", "Дискретна: ") + gpuName, ShortName = "dGPU", Color = theme.Gpu, Values = history.Gpu.ToArray() },
                    new ChartSeries { Name = IgpuText, ShortName = "iGPU", Color = theme.Igpu, Values = history.Igpu.ToArray() },
                    new ChartSeries { Name = discharging ? L.T("Rest (screen, board, SSD…)", "Решта (екран, плата, SSD…)") : L.T("Rest (last battery measurement)", "Решта (останній вимір від батареї)"), ShortName = L.T("Rest", "Решта"), Color = theme.Slots[4], Values = history.RestPower.ToArray() },
                };
                if (!HasBattery) series.RemoveAt(series.Count - 1);  // «остальное» на ПК не измерить
                empty = L.T("Waiting for data…", "Очікування даних…");
            }
            else
            {
                BuildProcessSeries(out times, out series);
                empty = string.Format(L.T("Collecting process data… (sampled every {0} s)", "Збір даних про процеси… (вимір раз на {0} с)"), Sampler.ProcessIntervalSeconds);
            }
            chart.SetData(times, series, empty, allTimes, history.Soc.ToArray());
            UpdateLegend(series);
            if (socLegend != null)
            {
                double soc = lastSnap != null ? lastSnap.SocPct : double.NaN;
                socLegend.Text = L.T("Charge ", "Заряд ") + (double.IsNaN(soc) ? "—" : soc.ToString("0") + " %") + L.T(" · background, 0–100 % scale", " · фон, шкала 0–100 %");
            }
        }

        TextBlock socLegend;

        static string PowerTitle { get { return L.T("Power, W", "Споживання, Вт"); } }
        static string AvgPeriodHeader { get { return L.T("avg for period", "сер. за період"); } }
        static string OtherProcessesText { get { return L.T("Other processes", "Інші процеси"); } }
        internal static string CpuNoGraphicsText { get { return L.T("CPU (without graphics)", "Процесор (без графіки)"); } }
        internal static string IgpuText { get { return L.T("Integrated graphics", "Вбудована графіка"); } }

        /// <summary>
        /// Пять процессов с наибольшей средней мощностью за видимый период + «Остальные процессы».
        /// Значения линий — за всю историю, чтобы график можно было сдвигать; лидеры и средние — по видимому периоду.
        /// </summary>
        void BuildProcessSeries(out IList<DateTime> times, out List<ChartSeries> series)
        {
            var idx = new List<int>();
            for (int i = 0; i < history.Times.Count; i++)
                if (history.Procs[i] != null) idx.Add(i);

            DateTime viewStart = chart.ViewStart, viewEnd = chart.ViewEnd;
            var sums = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            int inView = 0;
            foreach (int i in idx)
            {
                if (history.Times[i] < viewStart || history.Times[i] > viewEnd) continue;
                inView++;
                foreach (var kv in history.Procs[i])
                {
                    double s;
                    sums.TryGetValue(kv.Key, out s);
                    sums[kv.Key] = s + kv.Value;
                }
            }
            procAvg = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in sums) procAvg[kv.Key] = kv.Value / Math.Max(1, inView);

            var ranked = new List<KeyValuePair<string, double>>(procAvg);
            ranked.Sort((a, b) => b.Value.CompareTo(a.Value));
            topNames = new List<string>();
            foreach (var kv in ranked)
            {
                if (topNames.Count >= TopProcesses || kv.Value <= 0.005) break;
                topNames.Add(kv.Key);
            }
            AssignSlots(topNames);

            var t = new DateTime[idx.Count];
            for (int j = 0; j < idx.Count; j++) t[j] = history.Times[idx[j]];
            times = t;

            series = new List<ChartSeries>();
            var topSum = new double[idx.Count];
            foreach (string name in topNames)
            {
                var v = new double[idx.Count];
                for (int j = 0; j < idx.Count; j++)
                {
                    double p;
                    history.Procs[idx[j]].TryGetValue(name, out p);
                    v[j] = p;
                    topSum[j] += p;
                }
                series.Add(new ChartSeries { Name = name, ShortName = Short(name), Color = theme.Slots[procSlots[name]], Values = v });
            }

            var others = new double[idx.Count];
            double othersSum = 0, unSum = 0;
            int unN = 0;
            for (int j = 0; j < idx.Count; j++)
            {
                others[j] = Math.Max(0, history.ProcAttributed[idx[j]] - topSum[j]);
                if (t[j] < viewStart || t[j] > viewEnd) continue;
                othersSum += others[j];
                double u = history.ProcUnattributed[idx[j]];
                if (!double.IsNaN(u)) { unSum += u; unN++; }
            }
            othersAvg = inView > 0 ? othersSum / inView : double.NaN;
            unattributedAvg = unN > 0 ? unSum / unN : double.NaN;
            if (idx.Count > 0)
                series.Add(new ChartSeries { Name = OtherProcessesText, ShortName = L.T("Others", "Інші"), Color = theme.Muted, Values = others });
        }

        /// <summary>Каждому показанному процессу — свой цвет; уже выданный цвет сохраняется, новые берут первый свободный.</summary>
        void AssignSlots(List<string> shown)
        {
            var used = new HashSet<int>();
            var need = new List<string>();
            foreach (string name in shown)
            {
                int slot;
                if (procSlots.TryGetValue(name, out slot) && used.Add(slot)) continue;
                need.Add(name);
            }
            foreach (string name in need)
            {
                int slot = 0;
                while (used.Contains(slot)) slot++;  // процессов на графике меньше, чем цветов
                procSlots[name] = slot;
                used.Add(slot);
            }
        }

        static string Short(string name)
        {
            return name.Length <= ShortNameLength ? name : name.Substring(0, ShortNameLength - 1) + "…";
        }

        void UpdateLegend(List<ChartSeries> series)
        {
            var key = new System.Text.StringBuilder(theme.IsDark ? "d;" : "l;");  // тема меняет цвет фона в легенде
            foreach (var s in series) key.Append(s.Name).Append('|').Append(s.Color.ToString()).Append(';');
            if (key.ToString() == legendKey) return;
            legendKey = key.ToString();

            legendPanel.Children.Clear();
            foreach (var s in series)
            {
                var item = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 16, 2) };
                item.Children.Add(new Border
                {
                    Width = 14, Height = 3, CornerRadius = new CornerRadius(1.5), Margin = new Thickness(0, 0, 6, 0),
                    VerticalAlignment = VerticalAlignment.Center, Background = Theme.Brush(s.Color),
                });
                var t = Label(s.Name, 12, FontWeights.Normal, Keys.Text2);
                t.VerticalAlignment = VerticalAlignment.Center;
                item.Children.Add(t);
                legendPanel.Children.Add(item);
            }

            // Фон — уровень заряда: бледный квадрат, как сама заливка, и явное указание своей шкалы.
            var c = theme.TextPrimary;
            var socItem = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 16, 2) };
            socItem.Children.Add(new Border
            {
                Width = 12, Height = 10, CornerRadius = new CornerRadius(2), Margin = new Thickness(0, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Background = Theme.Brush(Color.FromArgb((byte)(theme.IsDark ? 30 : 24), c.R, c.G, c.B)),
                BorderBrush = Theme.Brush(Color.FromArgb((byte)(theme.IsDark ? 80 : 64), c.R, c.G, c.B)),
                BorderThickness = new Thickness(0, 1, 0, 0),
            });
            socLegend = Label(L.T("Charge", "Заряд"), 12, FontWeights.Normal, Keys.Muted);
            socLegend.VerticalAlignment = VerticalAlignment.Center;
            socItem.Children.Add(socLegend);
            legendPanel.Children.Add(BatteryOnly(socItem));  // заряд на фоне графика — только у ноутбука
        }

        // ---------------- Таблица процессов ----------------

        sealed class ProcRow
        {
            public Border Swatch;
            public TextBlock Name, Now, Avg, Cpu, Gpu;
            public FrameworkElement[] Cells;

            public bool Visible
            {
                set { foreach (var c in Cells) c.Visibility = value ? Visibility.Visible : Visibility.Collapsed; }
            }
        }

        FrameworkElement BuildProcessesView()
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            // Минимальная ширина под самые широкие значения («99,9 Вт», «100,0 %», «100 %») — иначе колонки дёргаются.
            double[] minWidths = { 78, 96, 70, 58 };
            for (int i = 0; i < 4; i++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, MinWidth = minWidths[i] });

            string[] headers = { "", L.T("now", "зараз"), AvgPeriodHeader, L.T("CPU", "ЦП"), L.T("GPU", "ГП") };
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            for (int c = 0; c < headers.Length; c++)
            {
                var h = Label(headers[c], 11, FontWeights.Normal, Keys.Muted);
                h.HorizontalAlignment = c == 0 ? HorizontalAlignment.Left : HorizontalAlignment.Right;
                h.Margin = new Thickness(c == 0 ? 0 : 14, 0, 0, 2);
                Grid.SetColumn(h, c);
                grid.Children.Add(h);
                if (c == 2) procAvgHeader = h;  // «ср. за 10 мин» — следует за периодом графика
            }

            procRows = new ProcRow[TopProcesses];
            for (int i = 0; i < TopProcesses; i++)
            {
                var row = procRows[i] = AddProcRow(grid, true);
                AttachProcessMenu(row.Cells[0], () => row.Name.Text);  // правый клик — режим эффективности на батарее
            }
            othersRow = AddProcRow(grid, true);
            othersRow.Name.Text = OtherProcessesText;
            unattributedRow = AddProcRow(grid, false);
            unattributedRow.Name.Text = L.T("Unattributed", "Не розподілено");
            unattributedRow.Name.ToolTip = L.T("Screen, board, storage, network and the shared part of the CPU: this power cannot be assigned to processes.", "Екран, плата, накопичувач, мережа і спільна частина процесора: цю потужність не можна віднести до процесів.");

            var panel = new StackPanel();
            panel.Children.Add(grid);
            procNote = Label("", 11, FontWeights.Normal, Keys.Muted);
            procNote.Margin = new Thickness(0, 6, 0, 0);
            procNote.TextWrapping = TextWrapping.Wrap;
            panel.Children.Add(procNote);
            return panel;
        }

        ProcRow AddProcRow(Grid grid, bool withSwatch)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            int row = grid.RowDefinitions.Count - 1;
            var r = new ProcRow();

            var name = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
            r.Swatch = new Border { Width = 10, Height = 10, CornerRadius = new CornerRadius(2), Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            if (!withSwatch) r.Swatch.Width = 10;  // пустое место, чтобы имена стояли ровно
            name.Children.Add(r.Swatch);
            r.Name = Label("", 13, FontWeights.Normal, withSwatch ? Keys.Text : Keys.Text2);
            r.Name.TextTrimming = TextTrimming.CharacterEllipsis;
            name.Children.Add(r.Name);
            Grid.SetRow(name, row);
            grid.Children.Add(name);

            r.Now = Cell(grid, row, 1, true);
            r.Avg = Cell(grid, row, 2, true);
            r.Cpu = Cell(grid, row, 3, false);
            r.Gpu = Cell(grid, row, 4, false);
            r.Cells = new FrameworkElement[] { name, r.Now, r.Avg, r.Cpu, r.Gpu };
            return r;
        }

        static TextBlock Cell(Grid grid, int row, int col, bool strong)
        {
            var t = Label("—", strong ? 13 : 12, strong ? FontWeights.SemiBold : FontWeights.Normal, strong ? Keys.Text : Keys.Text2);
            t.HorizontalAlignment = HorizontalAlignment.Right;
            t.VerticalAlignment = VerticalAlignment.Center;
            t.Margin = new Thickness(14, 2, 0, 2);
            Typography.SetNumeralAlignment(t, FontNumeralAlignment.Tabular);
            Grid.SetRow(t, row);
            Grid.SetColumn(t, col);
            grid.Children.Add(t);
            return t;
        }

        void UpdateProcessTable()
        {
            if (procRows == null || mode != ChartMode.Processes || !IsOnScreen) return;
            var list = lastSnap != null ? lastSnap.Processes : null;
            if (list == null || topNames.Count == 0)
            {
                foreach (var r in procRows) r.Visible = false;
                othersRow.Visible = false;
                unattributedRow.Visible = false;
                procNote.Text = string.Format(L.T("Collecting process data… Sampled every {0} s.", "Збір даних про процеси… Вимір раз на {0} с."), Sampler.ProcessIntervalSeconds);
                procNote.ToolTip = L.T("A sample costs about 20 ms of CPU time, so it runs only while this view is on screen.", "Вимір коштує близько 20 мс процесора, тому він іде, лише поки цей режим відкрито на екрані.");
                return;
            }

            var now = new Dictionary<string, ProcessPower>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in list) now[p.Name] = p;
            double topNow = 0;
            for (int i = 0; i < procRows.Length; i++)
            {
                var r = procRows[i];
                if (i >= topNames.Count) { r.Visible = false; continue; }
                r.Visible = true;
                string name = topNames[i];
                ProcessPower p;
                now.TryGetValue(name, out p);
                double w = p != null ? p.PowerW : 0;
                topNow += w;
                r.Swatch.Background = Theme.Brush(theme.Slots[procSlots[name]]);
                r.Name.Text = name;
                r.Now.Text = Fmt.W(w);
                double avg;
                r.Avg.Text = procAvg.TryGetValue(name, out avg) ? Fmt.W(avg) : "—";
                r.Cpu.Text = p != null ? p.CpuPct.ToString("0.0") + " %" : "0 %";
                bool holds = p != null && p.GpuPct <= 0 && p.DgpuMemMB >= 16;
                r.Gpu.Text = p != null && p.GpuPct > 0 ? p.GpuPct.ToString("0") + " %" : holds ? L.T("holds", "тримає") : "—";
                r.Gpu.ToolTip = holds ? string.Format(L.T("Keeps the discrete GPU awake: {0:0} MB of video memory, no load. Its power is counted to this app.",
                                                          "Не дає дискретній відеокарті заснути: {0:0} МБ відеопам'яті, без навантаження. Її потужність зараховано цій програмі."), p.DgpuMemMB) : null;
            }

            othersRow.Visible = true;
            othersRow.Swatch.Background = Theme.Brush(theme.Muted);
            othersRow.Now.Text = Fmt.W(Math.Max(0, lastSnap.ProcessesAttributedW - topNow));
            othersRow.Avg.Text = Fmt.W(othersAvg);
            othersRow.Cpu.Text = othersRow.Gpu.Text = "";

            unattributedRow.Visible = true;
            unattributedRow.Now.Text = Fmt.W(lastSnap.ProcessesUnattributedW);
            unattributedRow.Avg.Text = Fmt.W(unattributedAvg);
            unattributedRow.Cpu.Text = unattributedRow.Gpu.Text = "";

            procNote.Text = L.T("Estimate: cores by CPU cycles, graphics by GPU load.", "Оцінка: ядра — за тактами процесора, графіка — за завантаженням відеокарт.");
            procNote.ToolTip = !HasBattery
                ? L.T("A PC without a battery has no total draw sensor, so “Unattributed” is only the shared part of the CPU (without the board, storage and monitor).", "У ПК без батареї немає датчика загального споживання, тому «Не розподілено» — лише спільна частина процесора (без плати, накопичувачів і монітора).")
                : lastSnap.Sample.Battery.Discharging
                ? L.T("Rows add up to the battery power. “Unattributed” is the screen, board, storage, network and the shared part of the CPU.", "Сума рядків дорівнює потужності батареї. «Не розподілено» — екран, плата, накопичувач, мережа і спільна частина процесора.")
                : L.T("On AC the total draw is not measured, so “Unattributed” is only the shared part of the CPU (without the screen and board).", "Від мережі загальне споживання не вимірюється, тому «Не розподілено» — лише спільна частина процесора (без екрана і плати).");
        }

        // ---------------- Запоминание режима ----------------

        static ChartMode LoadChartMode()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\BatteryCheck"))
                {
                    string v = k == null ? null : k.GetValue("ChartMode") as string;
                    return v == "processes" ? ChartMode.Processes : v == "history" ? ChartMode.History : v == "advice" ? ChartMode.Advice : v == "optimize" ? ChartMode.Optimize : ChartMode.Components;
                }
            }
            catch (Exception)
            {
                return ChartMode.Components;
            }
        }

        static double LoadChartWindow()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\BatteryCheck"))
                {
                    object v = k == null ? null : k.GetValue("ChartWindow");
                    if (v is int && Array.IndexOf(PowerChart.Windows, (double)(int)v) >= 0) return (int)v;
                }
            }
            catch (Exception)
            {
            }
            return 600;
        }

        static void SaveChartWindow(double seconds)
        {
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(@"Software\BatteryCheck"))
                    k.SetValue("ChartWindow", (int)seconds, RegistryValueKind.DWord);
            }
            catch (Exception)
            {
            }
        }

        static void SaveChartMode(ChartMode m)
        {
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(@"Software\BatteryCheck"))
                    k.SetValue("ChartMode", m == ChartMode.Processes ? "processes" : m == ChartMode.History ? "history" : m == ChartMode.Advice ? "advice" : m == ChartMode.Optimize ? "optimize" : "components");
            }
            catch (Exception)
            {
                // Не запомнили — не страшно.
            }
        }
    }

    /// <summary>
    /// История замеров для графика — на самый длинный период (10 ч). Раз в секунду это до 36 000 точек, несколько МБ.
    /// Для процессов в каждой точке хранятся только лидеры: их хватает, чтобы выбрать пятёрку на любом периоде.
    /// </summary>
    sealed class History
    {
        const int ProcessesPerPoint = 15;

        public readonly List<DateTime> Times = new List<DateTime>();
        public readonly List<double> Battery = new List<double>(), Cpu = new List<double>(), Gpu = new List<double>();
        public readonly List<double> Igpu = new List<double>();  // встроенная графика, Вт (входит в Cpu — весь чип)
        public readonly List<double> Soc = new List<double>();  // уровень заряда, %
        public readonly List<double> Charge = new List<double>();  // мощность заряда, Вт (NaN — не заряжается)
        public readonly List<double> Total = new List<double>();   // всего: от батареи — разряд, от сети — оценка входа от блока
        public readonly List<double> SystemPower = new List<double>();  // потребление системы: от батареи — разряд, от сети — оценка без заряда аккумулятора
        public readonly List<double> RestPower = new List<double>();    // «остальное»: от батареи — измерено, от сети — последнее измеренное
        public readonly List<Dictionary<string, double>> Procs = new List<Dictionary<string, double>>();  // null — в этой точке нет оценки
        public readonly List<double> ProcAttributed = new List<double>(), ProcUnattributed = new List<double>();

        public void Add(Snapshot s, double totalW, double systemW, double restW)
        {
            Total.Add(totalW);
            SystemPower.Add(systemW);
            RestPower.Add(restW);
            var x = s.Sample;
            Times.Add(x.Time);
            Battery.Add(x.DischargeW);
            Cpu.Add(x.CpuPkgW);
            Gpu.Add(x.GpuW);
            Igpu.Add(x.IgpuW);
            Soc.Add(s.SocPct);
            Charge.Add(x.Battery.Charging && x.Battery.HasRate ? x.Battery.RateW : double.NaN);
            if (s.ProcessesUpdated && s.Processes != null)
            {
                var d = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < s.Processes.Count && i < ProcessesPerPoint; i++)  // список уже по убыванию мощности
                    d[s.Processes[i].Name] = s.Processes[i].PowerW;
                Procs.Add(d);
                ProcAttributed.Add(s.ProcessesAttributedW);
                ProcUnattributed.Add(s.ProcessesUnattributedW);
            }
            else
            {
                Procs.Add(null);
                ProcAttributed.Add(double.NaN);
                ProcUnattributed.Add(double.NaN);
            }

            DateTime cut = x.Time.AddSeconds(-PowerChart.MaxWindowSeconds - 40);
            int k = 0;
            while (k < Times.Count && Times[k] < cut) k++;
            if (k > 0)
            {
                Times.RemoveRange(0, k);
                Battery.RemoveRange(0, k);
                Cpu.RemoveRange(0, k);
                Gpu.RemoveRange(0, k);
                Igpu.RemoveRange(0, k);
                Soc.RemoveRange(0, k);
                Charge.RemoveRange(0, k);
                Total.RemoveRange(0, k);
                SystemPower.RemoveRange(0, k);
                RestPower.RemoveRange(0, k);
                Procs.RemoveRange(0, k);
                ProcAttributed.RemoveRange(0, k);
                ProcUnattributed.RemoveRange(0, k);
            }
        }
    }

    /// <summary>Переключатель из нескольких вариантов в стиле карточек.</summary>
    sealed class Segmented : Border
    {
        readonly List<Border> items = new List<Border>();
        readonly List<TextBlock> labels = new List<TextBlock>();
        public event Action<int> Changed;

        /// <summary>Сегменты с особой подсветкой, когда выбраны (Eco — зелёный, Subzero — голубой): номер → ключ цвета темы; текст белый.</summary>
        public readonly Dictionary<int, string> Accents = new Dictionary<int, string>();

        public Segmented(string[] options)
        {
            CornerRadius = new CornerRadius(6);
            BorderThickness = new Thickness(1);
            Padding = new Thickness(2);
            VerticalAlignment = VerticalAlignment.Center;
            SetResourceReference(BorderBrushProperty, Keys.Border);
            SetResourceReference(BackgroundProperty, Keys.Surface);
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            for (int i = 0; i < options.Length; i++)
            {
                int index = i;
                var label = MainWindow.Label(options[i], 12, FontWeights.Normal, Keys.Text2);
                var item = new Border { CornerRadius = new CornerRadius(4), Padding = new Thickness(10, 2, 10, 3), Cursor = Cursors.Hand, Child = label };
                item.MouseLeftButtonUp += (s, e) =>
                {
                    Select(index);
                    var h = Changed;
                    if (h != null) h(index);
                };
                items.Add(item);
                labels.Add(label);
                row.Children.Add(item);
            }
            Child = row;
        }

        public void Select(int index)
        {
            for (int i = 0; i < items.Count; i++)
            {
                bool on = i == index;
                string accent;
                if (on && Accents.TryGetValue(i, out accent))
                {
                    items[i].SetResourceReference(BackgroundProperty, accent);
                    labels[i].Foreground = Brushes.White;
                }
                else
                {
                    items[i].SetResourceReference(BackgroundProperty, on ? Keys.Hover : Keys.Surface);
                    labels[i].SetResourceReference(TextBlock.ForegroundProperty, on ? Keys.Text : Keys.Text2);
                }
                labels[i].FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal;
            }
        }
    }
}
