using System;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using WinForms = System.Windows.Forms;

namespace BatteryCheck
{
    /// <summary>
    /// Плитка мощности: от батареи — измеренный разряд; от сети — оценка мощности, приходящей от блока питания,
    /// и сколько из неё уходит в аккумулятор, на мини-графике — относительно мощности зарядного устройства.
    ///
    /// Windows не измеряет вход от блока питания и не сообщает его мощность. Поэтому вход оценивается как
    /// заряд аккумулятора + процессор + видеокарта + «остальное» (экран, плата, накопитель), а «остальное»
    /// берётся последним измеренным при работе от батареи. Мощность зарядного задаётся вручную или оценивается
    /// снизу по наибольшему наблюдённому входу: блок питания точно не слабее того, что он уже выдавал.
    /// </summary>
    sealed partial class MainWindow
    {
        /// <summary>Стандартный ряд мощностей блоков питания ноутбуков, Вт.</summary>
        static readonly int[] ChargerRatings = { 65, 90, 100, 120, 135, 150, 180, 200, 230, 240, 280, 300, 330 };

        double chargerManualW = Settings.GetDouble("ChargerW", 0);           // 0 — оценивать автоматически
        double chargerPeakW = Settings.GetDouble("ChargerPeakW", double.NaN);  // наибольший вход от блока (сглаженный)
        double savedPeakW = Settings.GetDouble("ChargerPeakW", double.NaN);
        double restRefW = Settings.GetDouble("RestW", double.NaN);             // «остальное», измеренное от батареи
        DateTime restRefAt = DateTime.MinValue, restSavedAt = DateTime.MinValue, lastTrack = DateTime.MinValue;
        readonly Ema inputEma = new Ema(10);  // пик считаем по сглаженному входу, чтобы короткий всплеск не завышал оценку

        /// <summary>Всего: от батареи — измеренный разряд, от сети — оценка входа от блока питания; NaN — неизвестно.</summary>
        double TrackPowerInput(Snapshot snap)
        {
            var x = snap.Sample;
            var b = x.Battery;
            double dt = lastTrack == DateTime.MinValue ? 0 : (x.TimeUtc - lastTrack).TotalSeconds;
            lastTrack = x.TimeUtc;
            // ПК без батареи: измеримо только то, что считают сами чипы (весь процессор с графикой + NVIDIA).
            if (!HasBattery) return double.IsNaN(x.CpuPkgW) ? double.NaN : x.CpuPkgW + Nz(x.GpuW);

            if (b.Discharging)
            {
                inputEma.Reset();
                if (!double.IsNaN(snap.RestW) && snap.RestW > 0)
                {
                    restRefW = snap.RestW;
                    restRefAt = x.Time;
                    if ((DateTime.UtcNow - restSavedAt).TotalSeconds >= 60)
                    {
                        Settings.SetDouble("RestW", restRefW);
                        restSavedAt = DateTime.UtcNow;
                    }
                }
                return x.DischargeW;
            }
            if (!b.OnLine) return double.NaN;

            double total = EstimateInput(x);
            if (!double.IsNaN(total))
            {
                inputEma.Add(total, dt);
                double smooth = inputEma.Value;
                if (!double.IsNaN(smooth) && (double.IsNaN(chargerPeakW) || smooth > chargerPeakW))
                {
                    chargerPeakW = smooth;
                    if (double.IsNaN(savedPeakW) || smooth > savedPeakW + 1)
                    {
                        Settings.SetDouble("ChargerPeakW", smooth);
                        savedPeakW = smooth;
                    }
                }
            }
            return total;
        }

        /// <summary>
        /// Если «остальное» ещё не запомнено — медиана последних замеров rest_w (до 120) из свежих CSV-логов.
        /// Так оценка от сети полная сразу, без отключения зарядки.
        /// </summary>
        void SeedRestFromLogs()
        {
            if (!double.IsNaN(restRefW)) return;
            try
            {
                string dir = sampler.LogPath != null ? System.IO.Path.GetDirectoryName(sampler.LogPath)
                    : System.IO.Path.GetFullPath(System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "logs"));
                if (!System.IO.Directory.Exists(dir)) return;
                var files = new System.IO.DirectoryInfo(dir).GetFiles("battery_*.csv");
                Array.Sort(files, (a, b) => b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc));
                var values = new System.Collections.Generic.List<double>();
                DateTime at = DateTime.MinValue;
                foreach (var f in files)
                {
                    using (var fs = new System.IO.FileStream(f.FullName, System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.ReadWrite))
                    using (var r = new System.IO.StreamReader(fs))
                    {
                        string[] head = (r.ReadLine() ?? "").Split(',');
                        int iRest = Array.IndexOf(head, "rest_w"), iTime = Array.IndexOf(head, "time");
                        if (iRest < 0) continue;
                        var fileValues = new System.Collections.Generic.List<double>();
                        DateTime fileAt = DateTime.MinValue;
                        string line;
                        while ((line = r.ReadLine()) != null)
                        {
                            var c = line.Split(',');
                            double v;
                            if (c.Length > iRest && double.TryParse(c[iRest], System.Globalization.NumberStyles.Float,
                                    System.Globalization.CultureInfo.InvariantCulture, out v) && v > 0)
                            {
                                fileValues.Add(v);
                                if (iTime >= 0) DateTime.TryParse(c[iTime], System.Globalization.CultureInfo.InvariantCulture,
                                    System.Globalization.DateTimeStyles.None, out fileAt);
                            }
                        }
                        if (fileValues.Count == 0) continue;
                        values = fileValues.GetRange(Math.Max(0, fileValues.Count - 120), Math.Min(120, fileValues.Count));
                        at = fileAt;
                        break;
                    }
                }
                if (values.Count == 0) return;
                values.Sort();
                restRefW = values[values.Count / 2];
                restRefAt = at;
            }
            catch (Exception)
            {
                // Логов нет или они не читаются — «остальное» появится после работы от батареи.
            }
        }

        double ChargeW(Sample x)
        {
            return x.Battery.Charging && x.Battery.HasRate ? Math.Max(0, x.Battery.RateW) : 0;
        }

        double EstimateInput(Sample x)
        {
            if (double.IsNaN(x.CpuPkgW)) return double.NaN;
            return ChargeW(x) + x.CpuPkgW + Nz(x.GpuW) + Nz(restRefW);
        }

        static double Nz(double v)
        {
            return double.IsNaN(v) ? 0 : v;
        }

        /// <summary>Мощность зарядного: заданная вручную или нижняя оценка — ближайшая стандартная не меньше пика.</summary>
        double ChargerLimit(out bool estimated)
        {
            estimated = chargerManualW <= 0;
            if (!estimated) return chargerManualW;
            if (double.IsNaN(chargerPeakW)) return double.NaN;
            foreach (int r in ChargerRatings)
                if (r >= chargerPeakW) return r;
            return Math.Ceiling(chargerPeakW / 10) * 10;
        }

        void UpdatePowerTile(Snapshot snap, double total)
        {
            var x = snap.Sample;
            var b = x.Battery;

            // Для мини-графика нужны только последние 5 минут истории.
            int from = history.Times.Count;
            DateTime cut = x.Time.AddSeconds(-PowerSpark.SpanSeconds - 40);
            while (from > 0 && history.Times[from - 1] >= cut) from--;
            int n = history.Times.Count - from;
            var t = history.Times.GetRange(from, n).ToArray();
            var tot = history.Total.GetRange(from, n).ToArray();

            powerSub.Inlines.Clear();
            powerSub.ToolTip = null;
            if (!HasBattery)
            {
                powerLabel.Text = L.T("CPU + graphics", "Процесор + графіка");
                SetHero(powerValue, Fmt.Num(total, "0.0"), Fmt.WUnit);
                AddLegendLine(theme.Cpu, L.T("CPU ", "процесор ") + Fmt.W(CpuWithoutGraphics(x.CpuPkgW, x.IgpuW)) + "   ");
                AddLegendLine(theme.Igpu, L.T("integrated ", "вбудована ") + Fmt.W(x.IgpuW));
                if (snap.GpuName != null)
                {
                    powerSub.Inlines.Add(new LineBreak());
                    AddLegendLine(theme.Gpu, (ShortGpuName(snap.GpuName) ?? "NVIDIA") + " " + Fmt.W(x.GpuW));
                }
                powerSpark.Set(t, tot, null, theme.Good, x.Time, double.NaN, "5 " + Fmt.MinUnit, "", theme.Battery, theme);
                powerSpark.ToolTip = L.T("CPU (with integrated graphics) and NVIDIA GPU over the last 5 minutes.\n" +
                                         "The whole PC draws more: board, storage, fans and the monitor are not measured — a desktop has no sensor for that.",
                                         "Процесор (з вбудованою графікою) і відеокарта NVIDIA за останні 5 хвилин.\n" +
                                         "Увесь ПК споживає більше: плата, накопичувачі, вентилятори й монітор не вимірюються — у настільного ПК немає такого датчика.");
                return;
            }
            if (b.Discharging)
            {
                powerLabel.Text = L.T("Power draw", "Споживання");
                SetHero(powerValue, Fmt.Num(Math.Abs(b.RateW), "0.0"), Fmt.WUnit);
                powerSub.Inlines.Add(new Run(L.T("avg 30 s: ", "сер. за 30 с: ") + Fmt.W(snap.BatteryAvgW)));
                AddExcessLine(snap);
                powerSpark.Set(t, tot, null, theme.Good, x.Time, double.NaN, "5 " + Fmt.MinUnit, "", theme.Battery, theme);
                powerSpark.ToolTip = L.T("Battery discharge over the last 5 minutes.\n", "Розряд акумулятора за останні 5 хвилин.\n") + ClickChargerText;
                return;
            }
            if (!b.OnLine)
            {
                powerLabel.Text = L.T("Power draw", "Споживання");
                SetHero(powerValue, "—", null);
                powerSpark.Set(t, tot, null, theme.Good, x.Time, double.NaN, "", "", theme.Muted, theme);
                return;
            }

            bool estimated;
            double limit = ChargerLimit(out estimated);
            double charge = ChargeW(x);
            double frac = !double.IsNaN(limit) && !double.IsNaN(total) ? total / limit : double.NaN;

            powerLabel.Text = L.T("From the power adapter", "Від блока живлення");
            SetHero(powerValue, double.IsNaN(total) ? "—" : "≈ " + total.ToString("0"), Fmt.WUnit);
            AddLegendLine(theme.Good, b.Charging ? L.T("to battery ", "в акумулятор ") + charge.ToString("0.0") + " " + Fmt.WUnit : L.T("battery not charging", "акумулятор не заряджається"));
            powerSub.Inlines.Add(new LineBreak());
            AddLegendLine(theme.Battery, L.T("to the system ≈ ", "на роботу ≈ ") + Fmt.Num(double.IsNaN(total) ? double.NaN : total - charge, "0") + " " + Fmt.WUnit);

            // Предупреждающие цвета — только при известной мощности зарядного: нижняя оценка завышает долю.
            Color line = theme.Battery;
            if (!estimated && !double.IsNaN(frac)) line = frac >= 0.95 ? theme.Critical : frac >= 0.8 ? theme.Warning : theme.Battery;
            string left = double.IsNaN(limit) ? L.T("limit unknown", "межа невідома") : (estimated ? L.T("of ≥", "з ≥") : L.T("of ", "з ")) + limit.ToString("0") + " " + Fmt.WUnit;
            string right = double.IsNaN(frac) ? "" : (frac * 100).ToString("0") + " %";
            var chg = history.Charge.GetRange(from, n).ToArray();
            powerSpark.Set(t, tot, chg, theme.Good, x.Time, limit, left, right, line, theme);

            powerSpark.ToolTip = string.Format(
                L.T("Estimated power from the adapter:\n" +
                    "  to battery {0:0.0} + CPU {1:0.0} + GPU {2:0.0} + rest {3} = {4} W\n" +
                    "“Rest” (screen, board, storage): {5}.\n\n" +
                    "Charger rating: {6}\n",
                    "Оцінка потужності від блока живлення:\n" +
                    "  в акумулятор {0:0.0} + процесор {1:0.0} + відеокарта {2:0.0} + решта {3} = {4} Вт\n" +
                    "«Решта» (екран, плата, накопичувач) — {5}.\n\n" +
                    "Потужність зарядного: {6}\n") + ClickChargerText,
                charge, Nz(x.CpuPkgW), Nz(x.GpuW),
                double.IsNaN(restRefW) ? "—" : restRefW.ToString("0.0"),
                Fmt.Num(total, "0.0"),
                double.IsNaN(restRefW) ? L.T("not measured yet: it is measured on battery and not counted until then", "ще не виміряно: вимірюється при роботі від батареї, поки не враховано")
                    : restRefAt != DateTime.MinValue ? L.T("measured on battery at ", "виміряно при роботі від батареї о ") + restRefAt.ToString("HH:mm")
                    : L.T("last value measured on battery", "останнє виміряне при роботі від батареї"),
                !estimated ? limit.ToString("0") + L.T(" W (set manually)", " Вт (задано)")
                    : double.IsNaN(limit) ? L.T("unknown — click to set it", "невідома — клацніть, щоб вказати")
                    : string.Format(L.T("at least {0:0} W — estimated from the highest draw seen, {1:0} W; the exact rating is printed on the adapter", "не менше {0:0} Вт — оцінка за найбільшим споживанням {1:0} Вт; точне значення вказано на блоці живлення"), limit, chargerPeakW));
        }

        static string ClickChargerText { get { return L.T("Click to set the charger power rating.", "Клік — вказати потужність зарядного пристрою."); } }

        void AddLegendLine(Color color, string text)
        {
            powerSub.Inlines.Add(new Run("■ ") { Foreground = Theme.Brush(color) });
            powerSub.Inlines.Add(new Run(text));
        }

        /// <summary>Меню выбора мощности зарядного (в тон теме, как меню значка в трее).</summary>
        void ShowChargerMenu()
        {
            var menu = new WinForms.ContextMenuStrip();
            menu.Renderer = new WinForms.ToolStripProfessionalRenderer(new TrayIcon.MenuColors(theme)) { RoundedEdges = false };
            var fg = TrayIcon.ToGdi(theme.TextPrimary);

            menu.Items.Add(new WinForms.ToolStripMenuItem(L.T("Charger power rating", "Потужність зарядного пристрою")) { Enabled = false });
            var auto = new WinForms.ToolStripMenuItem(double.IsNaN(chargerPeakW)
                ? L.T("Estimate automatically", "Оцінювати автоматично")
                : string.Format(L.T("Estimate automatically (peak {0:0} W)", "Оцінювати автоматично (пік {0:0} Вт)"), chargerPeakW)) { Checked = chargerManualW <= 0, ForeColor = fg };
            auto.Click += (s, e) => SetCharger(0);
            menu.Items.Add(auto);
            menu.Items.Add(new WinForms.ToolStripSeparator());
            foreach (int r in ChargerRatings)
            {
                int rating = r;
                var item = new WinForms.ToolStripMenuItem(r + " " + Fmt.WUnit) { Checked = chargerManualW == r, ForeColor = fg };
                item.Click += (s, e) => SetCharger(rating);
                menu.Items.Add(item);
            }
            menu.Items.Add(new WinForms.ToolStripSeparator());
            var reset = new WinForms.ToolStripMenuItem(L.T("Reset the measured peak", "Скинути виміряний пік")) { ForeColor = fg, Enabled = !double.IsNaN(chargerPeakW) };
            reset.Click += (s, e) =>
            {
                chargerPeakW = savedPeakW = double.NaN;
                Settings.SetDouble("ChargerPeakW", double.NaN);
                RefreshPowerTile();
            };
            menu.Items.Add(reset);

            menu.Closed += (s, e) => Dispatcher.BeginInvoke(new Action(menu.Dispose));
            menu.Show(WinForms.Control.MousePosition);
        }

        void SetCharger(double watts)
        {
            chargerManualW = watts;
            Settings.SetDouble("ChargerW", watts);
            RefreshPowerTile();
        }

        void RefreshPowerTile()
        {
            if (lastSnap != null && history.Total.Count > 0)
                UpdatePowerTile(lastSnap, history.Total[history.Total.Count - 1]);
        }
    }
}
