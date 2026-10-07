using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace BatteryCheck
{
    /// <summary>
    /// Пробуждения NVIDIA: сводка в карточке «Компоненты», окно со списком и CSV-журнал logs\gpu_wakes.csv.
    /// Стоимость пробуждения — энергия батареи сверх обычного уровня от начала пробуждения до 9 с после засыпания
    /// (само засыпание ещё ~6 с стоит +15 Вт). Обычный уровень — медиана за 30 с до пробуждения.
    /// </summary>
    sealed partial class MainWindow
    {
        const double WakeTailSeconds = 9;

        FlatButton wakesButton;
        WakesWindow wakesWindow;
        List<GpuWake> wakes;
        readonly HashSet<DateTime> wakesLogged = new HashSet<DateTime>();
        int prevDisplays = -1;

        internal static string WakesTitle { get { return L.T("NVIDIA wake-ups", "Пробудження NVIDIA"); } }

        FlatButton BuildWakesButton()
        {
            wakesButton = new FlatButton(WakesTitle);
            wakesButton.Visibility = Visibility.Collapsed;
            wakesButton.ToolTip = L.T("When and why the discrete GPU woke up and what it cost the battery", "Коли і чому прокидалася дискретна відеокарта і скільки це коштувало батареї");
            wakesButton.Click += () =>
            {
                if (wakesWindow == null || !wakesWindow.IsLoaded)
                {
                    wakesWindow = new WakesWindow(this);
                    wakesWindow.Owner = this;
                }
                wakesWindow.Show();
                wakesWindow.Activate();
                wakesWindow.Fill(wakes);
            };
            return wakesButton;
        }

        /// <summary>Каждый замер (и когда окно скрыто — для журнала и подсказок).</summary>
        void UpdateWakes(Snapshot snap)
        {
            var x = snap.Sample;
            if (x.GpuDState >= 0 && x.GpuDState != 3) gpuAwakeAt = DateTime.UtcNow;
            if (x.Displays >= 0 && prevDisplays >= 0 && x.Displays != prevDisplays) sampler.WakeHint(GpuWake.HintDisplay);
            if (x.Displays >= 0) prevDisplays = x.Displays;

            wakes = snap.GpuWakes;
            if (wakes == null) return;
            foreach (var w in wakes)
                if (!w.Active && (DateTime.Now - w.End).TotalSeconds >= WakeTailSeconds + 1 && wakesLogged.Add(w.Start))
                    LogWake(w);

            if (!IsOnScreen) return;
            DateTime hourAgo = DateTime.Now.AddHours(-1);
            int n = 0;
            double cost = 0;
            bool costKnown = false;
            foreach (var w in wakes)
            {
                if (w.Start < hourAgo) continue;
                n++;
                double c = WakeCostWh(w);
                if (!double.IsNaN(c)) { cost += c; costKnown = true; }
            }
            wakesButton.Visibility = Visibility.Visible;
            // Коротко — кнопка в заголовке карточки; полная фраза и стоимость — в подсказке.
            wakesButton.Text = string.Format(L.T("NVIDIA: {0}×/h", "NVIDIA: {0}×/год"), n);
            wakesButton.ToolTip = (n == 0
                ? L.T("NVIDIA did not wake in the last hour", "NVIDIA не прокидалася за годину")
                : string.Format(L.T("NVIDIA woke {0} {1} in the last hour{2}", "NVIDIA прокидалася {0} {1} за годину{2}"),
                    n, L.Plural(n, "time", "times", "раз", "рази", "разів"),
                    costKnown ? string.Format(" (≈ {0:0.00} {1})", cost, Fmt.WhUnit) : "")) +
                L.T(". Click for the list: when, how long, what it cost and the likely cause.", ". Клік — список: коли, як довго, скільки коштувало і ймовірна причина.");
            if (wakesWindow != null && wakesWindow.IsVisible) wakesWindow.Fill(wakes);
        }

        /// <summary>Энергия батареи сверх обычного уровня за пробуждение, Вт·ч; NaN — от сети или нет данных.</summary>
        internal double WakeCostWh(GpuWake w)
        {
            var t = history.Times;
            var bat = history.Battery;
            var before = new List<double>();
            for (int i = 0; i < t.Count; i++)
                if (t[i] >= w.Start.AddSeconds(-30) && t[i] < w.Start.AddSeconds(-1) && !double.IsNaN(bat[i])) before.Add(bat[i]);
            if (before.Count < 3) return double.NaN;
            double baseW = Median(before);
            DateTime end = (w.Active ? DateTime.Now : w.End).AddSeconds(WakeTailSeconds);
            double wh = 0;
            int used = 0;
            for (int i = 1; i < t.Count; i++)
            {
                if (t[i] <= w.Start || t[i - 1] >= end || double.IsNaN(bat[i])) continue;
                double dt = (t[i] - t[i - 1]).TotalSeconds;
                if (dt <= 0 || dt > 35) continue;
                wh += Math.Max(0, bat[i] - baseW) * dt / 3600;
                used++;
            }
            return used == 0 ? double.NaN : wh;
        }

        void LogWake(GpuWake w)
        {
            try
            {
                string dir = sampler.LogPath != null ? Path.GetDirectoryName(sampler.LogPath)
                    : Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "logs"));
                Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, "gpu_wakes.csv");
                GpuWakeLog.Migrate(path);
                bool header = !File.Exists(path);
                var inv = CultureInfo.InvariantCulture;
                using (var sw = new StreamWriter(path, true, new UTF8Encoding(false)))
                {
                    if (header) sw.WriteLine(GpuWakeLog.Header);
                    double c = WakeCostWh(w);
                    sw.WriteLine(string.Join(",", new[]
                    {
                        w.Start.ToString("yyyy-MM-ddTHH:mm:ss", inv),
                        w.End.ToString("yyyy-MM-ddTHH:mm:ss", inv),
                        w.Seconds.ToString("0", inv),
                        double.IsNaN(c) ? "" : c.ToString("0.0000", inv),
                        w.SelfCaused ? "self" : w.Unknown ? "unknown" : "",
                        "\"" + w.Cause.Replace("\"", "'") + "\"",
                    }));
                }
            }
            catch (Exception)
            {
                // Журнал не записался — не страшно, список в окне остаётся.
            }
        }

        internal string LogDirectory
        {
            get
            {
                return sampler.LogPath != null ? Path.GetDirectoryName(sampler.LogPath)
                    : Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "logs"));
            }
        }
    }

    /// <summary>Список пробуждений NVIDIA: время, длительность, стоимость, вероятная причина.</summary>
    sealed class WakesWindow : Window
    {
        readonly MainWindow main;
        readonly Grid grid = new Grid();
        readonly TextBlock summary;

        public WakesWindow(MainWindow main)
        {
            this.main = main;
            Title = MainWindow.WakesTitle;
            Width = 860;
            Height = 560;
            MinWidth = 600;
            MinHeight = 300;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            FontFamily = new FontFamily("Segoe UI");
            FontSize = 13;
            UseLayoutRounding = true;
            SetResourceReference(BackgroundProperty, Keys.Page);

            var root = new DockPanel { Margin = new Thickness(20, 16, 20, 16) };
            var head = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
            head.Children.Add(MainWindow.Label(L.T("Discrete GPU wake-ups", "Пробудження дискретної відеокарти"), 18, FontWeights.SemiBold, Keys.Text));
            summary = MainWindow.Label("", 12, FontWeights.Normal, Keys.Text2);
            summary.TextWrapping = TextWrapping.Wrap;
            summary.Margin = new Thickness(0, 4, 0, 0);
            head.Children.Add(summary);
            DockPanel.SetDock(head, Dock.Top);
            root.Children.Add(head);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0), HorizontalAlignment = HorizontalAlignment.Right };
            var open = new FlatButton(L.T("Log folder", "Тека з журналом"));
            open.Click += () => { try { System.Diagnostics.Process.Start("explorer.exe", "\"" + main.LogDirectory + "\""); } catch (Exception) { } };
            buttons.Children.Add(open);
            DockPanel.SetDock(buttons, Dock.Bottom);
            root.Children.Add(buttons);

            var card = new Border { CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Padding = new Thickness(16, 12, 16, 12) };
            card.SetResourceReference(Border.BackgroundProperty, Keys.Surface);
            card.SetResourceReference(Border.BorderBrushProperty, Keys.Border);
            card.Child = new ScrollViewer { Content = grid, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            root.Children.Add(card);
            Content = root;

            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }

        public void Fill(List<GpuWake> wakes)
        {
            grid.Children.Clear();
            grid.RowDefinitions.Clear();
            AddRow(new[] { L.T("start", "початок"), L.T("dur.", "трив."), L.T("energy", "енергія"), L.T("likely cause", "ймовірна причина") }, true);
            if (wakes == null || wakes.Count == 0)
            {
                summary.Text = L.T(
                    "No wake-ups yet. The NVIDIA GPU wakes when an app accesses it, when brightness changes and when a display is connected. " +
                    "Each wake-up plus falling asleep again costs the battery a few watts for 10–20 seconds.",
                    "Пробуджень поки не було. Відеокарта NVIDIA прокидається, коли до неї звертається програма, " +
                    "при зміні яскравості й підключенні екрана. Кожне пробудження плюс засинання коштують батареї кілька ватів на 10–20 секунд.");
                return;
            }
            double total = 0;
            for (int i = wakes.Count - 1; i >= 0; i--)
            {
                var w = wakes[i];
                double c = main.WakeCostWh(w);
                if (!double.IsNaN(c)) total += c;
                AddRow(new[]
                {
                    w.Start.ToString("HH:mm:ss"),
                    w.Active ? L.T("ongoing", "триває") : w.Seconds.ToString("0") + " " + Fmt.SecUnit,
                    double.IsNaN(c) ? "—" : c.ToString("0.000") + " " + Fmt.WhUnit,
                    w.Cause,
                }, false);
            }
            summary.Text = string.Format(
                L.T("{0} {1} since the app started, ≈ {2:0.00} Wh. “Energy” is on top of the usual draw, including falling asleep " +
                    "(~6 s at +15 W). “Opened” — processes that accessed the GPU at the moment it woke; “work” — what loaded it. " +
                    "“Unknown” — the GPU was polled with no load (for example, an app checked its status). Log: logs\\gpu_wakes.csv.",
                    "{0} {1} з запуску програми, ≈ {2:0.00} Вт·год. «Енергія» — понад звичайне споживання, включно із засинанням " +
                    "(~6 с при +15 Вт). «Відкрили» — процеси, що звернулися до відеокарти в момент пробудження; «робота» — хто її навантажував. " +
                    "«Невідомо» — відеокарту опитали без навантаження (наприклад, програма перевірила її стан). Журнал: logs\\gpu_wakes.csv."),
                wakes.Count, L.Plural(wakes.Count, "wake-up", "wake-ups", "пробудження", "пробудження", "пробуджень"), total);
        }

        void AddRow(string[] cells, bool header)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            int row = grid.RowDefinitions.Count - 1;
            for (int c = 0; c < cells.Length; c++)
            {
                var t = MainWindow.Label(cells[c], header ? 11 : 13, FontWeights.Normal, header ? Keys.Muted : c == 3 ? Keys.Text2 : Keys.Text);
                t.Margin = new Thickness(c == 0 ? 0 : 18, 3, 0, 3);
                if (c == 3) t.TextWrapping = TextWrapping.Wrap;
                if (c > 0 && c < 3) t.HorizontalAlignment = HorizontalAlignment.Right;
                Grid.SetRow(t, row);
                Grid.SetColumn(t, c);
                grid.Children.Add(t);
            }
        }
    }
}
