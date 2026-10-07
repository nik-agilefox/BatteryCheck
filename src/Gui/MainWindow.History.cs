using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

namespace BatteryCheck
{
    /// <summary>
    /// Вкладка «История»: прошлые разряды по CSV-логам — сколько ноутбук проработал от батареи, диапазон заряда,
    /// израсходованная энергия, средняя мощность и прогноз полного разряда. Логи разбираются в фоне при открытии
    /// вкладки и раз в минуту, пока она открыта (лог сбрасывается на диск раз в минуту).
    /// </summary>
    sealed partial class MainWindow
    {
        const double HistoryRefreshSeconds = 60;

        FrameworkElement chartBody, historyView;
        Grid historyGrid;
        TextBlock historySummary;
        DateTime historyLoadedAt = DateTime.MinValue;
        int historyLoading;

        FrameworkElement BuildHistoryView()
        {
            var panel = new StackPanel();
            historySummary = Label(L.T("Reading logs…", "Читаю логи…"), 12, FontWeights.Normal, Keys.Text2);
            historySummary.TextWrapping = TextWrapping.Wrap;
            historySummary.Margin = new Thickness(0, 0, 0, 10);
            panel.Children.Add(historySummary);
            historyGrid = new Grid();
            foreach (var w in new[] { GridLength.Auto, GridLength.Auto, GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto, GridLength.Auto, GridLength.Auto })
                historyGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = w });
            panel.Children.Add(historyGrid);
            return new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Visibility = Visibility.Collapsed };
        }

        void RefreshHistory(bool force)
        {
            if (!force && (DateTime.UtcNow - historyLoadedAt).TotalSeconds < HistoryRefreshSeconds) return;
            if (Interlocked.Exchange(ref historyLoading, 1) == 1) return;
            historyLoadedAt = DateTime.UtcNow;
            string dir = LogDirectory;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                List<DischargeSession> list = null;
                List<CycleApps> procs = null;
                try
                {
                    list = DischargeHistory.Load(dir);
                    procs = CycleProcesses.Load(dir, list);
                    // Путь ещё не записан (процесс попал в лог до того, как пути стали запоминаться) — у запущенного узнать сейчас.
                    var live = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var l in procs)
                        foreach (var p in l.Top)
                            if (p.Path == null)
                            {
                                string path;
                                if (!live.TryGetValue(p.Name, out path)) live[p.Name] = path = RegistryGpuPreferenceStore.ExeOf(p.Name);
                                p.Path = path;
                            }
                }
                finally
                {
                    Interlocked.Exchange(ref historyLoading, 0);
                    Dispatcher.BeginInvoke(new Action(() => FillHistory(list, procs)));
                }
            });
        }

        void FillHistory(List<DischargeSession> list, List<CycleApps> procs)
        {
            historyGrid.Children.Clear();
            historyGrid.RowDefinitions.Clear();
            if (list == null || list.Count == 0)
            {
                historySummary.Text = L.T(
                    "No discharges yet. They appear once the laptop runs on battery with the app running " +
                    "(discharges of at least 2 minutes and 1 % count). Data comes from the CSV logs in the logs folder.",
                    "Розрядів поки немає. Вони з'являться, коли ноутбук попрацює від батареї із запущеною програмою " +
                    "(враховуються розряди від 2 хвилин і від 1 %). Дані беруться з CSV-логів у теці logs.");
                return;
            }

            double activeS = 0, energy = 0;
            var runtimes = new List<double>();
            foreach (var s in list)
            {
                activeS += s.ActiveS;
                energy += s.EnergyByRateWh;
                if (!double.IsNaN(s.FullRuntimeH) && s.ActiveS >= 600) runtimes.Add(s.FullRuntimeH);  // прогноз по разрядам от 10 мин
            }
            double avgW = activeS > 0 ? energy * 3600 / activeS : double.NaN;
            runtimes.Sort();
            historySummary.Inlines.Clear();
            historySummary.Inlines.Add(new Run(string.Format(L.T("Discharges: {0} · on battery in total {1} · used {2:0.0} Wh · average power {3}", "Розрядів: {0} · від батареї всього {1} · витрачено {2:0.0} Вт·год · середня потужність {3}"),
                list.Count, Fmt.Hours(activeS / 3600), energy, Fmt.W(avgW))));
            if (runtimes.Count > 0)
                historySummary.Inlines.Add(new Run(string.Format(L.T(" · typical runtime from 100 % to 0 % ≈ {0}", " · типова автономність від 100 % до 0 % ≈ {0}"), Fmt.Hours(runtimes[runtimes.Count / 2]))) { FontWeight = FontWeights.SemiBold });
            historySummary.Inlines.Add(new LineBreak());
            var verdict = DischargeHistory.Evaluate(list);
            var verdictRun = new Run(verdict.Describe()) { FontWeight = verdict.Warning ? FontWeights.SemiBold : FontWeights.Normal };
            if (verdict.Warning) verdictRun.SetResourceReference(TextElement.ForegroundProperty, Keys.Warning);
            historySummary.Inlines.Add(verdictRun);
            historySummary.Inlines.Add(new LineBreak());
            historySummary.Inlines.Add(new Run(L.T("From the app's CSV logs: there is no data for times it was not running. “Full discharge” is how long the laptop would last from 100 % to 0 % at that discharge's average power.", "За CSV-логами програми: поки вона не була запущена, даних немає. «Повний розряд» — скільки ноутбук пропрацював би від 100 % до 0 % за середньої потужності цього розряду.")) { FontSize = 11 });

            AddHistoryRow(new UIElement[]
            {
                HistoryHead(L.T("date", "дата")), HistoryHead(L.T("time", "час")), HistoryHead(L.T("on battery", "від батареї")), HistoryHead(L.T("charge", "заряд")),
                TopAppsHead(), HistoryHead(L.T("used", "витрачено")), HistoryHead(L.T("avg power", "сер. потужність")), HistoryHead(L.T("full discharge", "повний розряд")),
                GaugeHead(),
            });
            for (int i = 0; i < list.Count; i++)
            {
                var s = list[i];
                var range = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                range.Children.Add(SocBar(s.SocStart, s.SocEnd));
                var pct = Label(string.Format("{0:0} → {1:0} %", s.SocStart, s.SocEnd), 13, FontWeights.SemiBold, Keys.Text);
                pct.Margin = new Thickness(10, 0, 0, 0);
                range.Children.Add(pct);

                var active = HistoryCell(Fmt.Hours(s.ActiveS / 3600), true);
                active.ToolTip = string.Format(L.T("From {0:HH:mm} to {1:HH:mm} — {2}; of that, asleep or no data — {3}", "З {0:HH:mm} до {1:HH:mm} — {2}; з них сон або без даних — {3}"),
                    s.Start, s.End, Fmt.Hours((s.End - s.Start).TotalHours), Fmt.Hours(s.PausedS / 3600));

                AddHistoryRow(new UIElement[]
                {
                    HistoryCell(s.Start.ToString(L.T("MMM d", "dd.MM")), false),
                    HistoryCell(s.Start.ToString("HH:mm") + "–" + (s.Ongoing ? L.T("ongoing", "триває") : s.End.ToString("HH:mm")), false),
                    active,
                    range,
                    TopAppsCell(s, procs != null && i < procs.Count ? procs[i] : null),
                    HistoryCell(double.IsNaN(s.UsedWh) ? "—" : s.UsedWh.ToString("0.0") + " " + Fmt.WhUnit, true),
                    HistoryCell(Fmt.W(s.AvgW), true),
                    HistoryCell(s.ActiveS >= 600 ? "≈ " + Fmt.Hours(s.FullRuntimeH) : "—", true),
                    GaugeCell(s),
                });
            }
        }

        static TextBlock TopAppsHead()
        {
            var h = HistoryHead(L.T("where the energy went", "куди пішла енергія"));
            h.ToolTip = L.T("Screen, idle (what is spent with any apps), then processes that used at least 1 Wh: their share of the CPU cores (by cycles) and of the GPUs (by load).\n" +
                            "Hover over an item for details and the average power.",
                            "Екран, простій (те, що витрачається за будь-яких програм), далі процеси, що витратили від 1 Вт·год: їхня частка ядер процесора (за тактами) і відеокарт (за завантаженням).\n" +
                            "Наведіть на пункт — подробиці й середня потужність.");
            return h;
        }

        /// <summary>
        /// В одну строку: экран и простой (всё, что не зависит от программ), затем самые прожорливые процессы.
        /// Подсказки: у экрана и простоя — из чего сложились, у процесса — активное окно / фон и путь.
        /// </summary>
        TextBlock TopAppsCell(DischargeSession s, CycleApps apps)
        {
            var t = HistoryCell("", false);
            t.TextTrimming = TextTrimming.WordEllipsis;

            // Экран: изображение по логу (растянуто на всё время, если оценка была не всегда) + сама панель, пока экран горел.
            double content = s.ScreenKnownS >= 60 ? s.ScreenContentWh * s.ActiveS / s.ScreenKnownS : double.NaN;
            double panelW = screenModel != null ? screenModel.PanelW : double.NaN;
            double panel = double.IsNaN(panelW) ? double.NaN : panelW * s.DisplayOnS / 3600;
            double screen = double.IsNaN(content) && double.IsNaN(panel) ? double.NaN : (double.IsNaN(content) ? 0 : content) + (double.IsNaN(panel) ? 0 : panel);
            // Простой: счётчик батареи минус программы (растянуты на всё время, если сбор был не всегда) минус экран.
            double appsWh = apps != null && apps.Seconds >= 60 ? apps.AllWh * s.ActiveS / apps.Seconds : double.NaN;
            double idle = double.IsNaN(appsWh) ? double.NaN : Math.Max(0, s.EnergyByRateWh - appsWh - (double.IsNaN(screen) ? 0 : screen));

            bool any = false;
            if (!double.IsNaN(screen))
            {
                AddEnergyItem(t, L.T("screen", "екран"), screen, false, ScreenTip(s, screen, content, panel, panelW));
                any = true;
            }
            if (!double.IsNaN(idle))
            {
                AddEnergyItem(t, L.T("idle", "простій"), idle, false, IdleTip(s, idle, appsWh, screen, panel));
                any = true;
            }
            var top = apps != null ? apps.Top : null;
            if (top != null)
                foreach (var p in top)
                {
                    if (any && p == top[0]) t.Inlines.Add(new Run("   ·"));
                    AddEnergyItem(t, p.Name, p.Wh, true, AppTip(p));
                    any = true;
                }
            if (!any)
            {
                t.Text = "—";
                t.ToolTip = L.T("No app data for this discharge: it is collected on battery while the app is running.",
                                "Немає даних про програми за цей розряд: вони збираються від батареї, поки програма запущена.");
            }
            return t;
        }

        /// <summary>Имя и Вт·ч одним неразрывным куском: при обрезке строки не останется имени без числа.</summary>
        static void AddEnergyItem(TextBlock t, string name, double wh, bool app, string tip)
        {
            if (t.Inlines.Count > 0) t.Inlines.Add(new Run("   "));
            var n = new Run(name) { FontWeight = app ? FontWeights.SemiBold : FontWeights.Normal, ToolTip = tip };
            n.SetResourceReference(TextElement.ForegroundProperty, app ? Keys.Text : Keys.Muted);
            t.Inlines.Add(n);
            t.Inlines.Add(new Run((" " + wh.ToString("0.0") + " " + Fmt.WhUnit).Replace(' ', ' ')) { ToolTip = tip });
        }

        static string ScreenTip(DischargeSession s, double screen, double content, double panel, double panelW)
        {
            var lines = new List<string>
            {
                string.Format(L.T("Screen: {0:0.0} Wh over the discharge, ≈ {1}", "Екран: {0:0.0} Вт·год за розряд, ≈ {1}"), screen, Fmt.W(screen * 3600 / Math.Max(1, s.ActiveS))),
                double.IsNaN(content)
                    ? L.T("image: unknown — the screen had not been measured yet", "зображення: невідомо — екран тоді ще не був виміряний")
                    : string.Format(L.T("image: {0:0.00} Wh (by the screen measurement and the white share on screen)", "зображення: {0:0.00} Вт·год (за виміром екрана і часткою білого)"), content),
                double.IsNaN(panel)
                    ? (DisplayState.ModernStandby
                        ? L.T("the panel itself: counted in idle — on this laptop it cannot be measured (switching the screen off puts it to sleep).",
                              "сама панель: врахована в простої — на цьому ноутбуці її не виміряти (вимкнення екрана присипляє його).")
                        : L.T("the panel itself: not measured — it is counted in idle. Re-measure the screen on battery.", "сама панель: не виміряна — її враховано в простої. Перевиміряйте екран від батареї."))
                    : string.Format(L.T("the panel itself: {0:0.00} Wh = {1:0.0} W × {2} with the screen on", "сама панель: {0:0.00} Вт·год = {1:0.0} Вт × {2} з увімкненим екраном"), panel, panelW, Fmt.Hours(s.DisplayOnS / 3600)),
            };
            return string.Join("\n", lines);
        }

        static string IdleTip(DischargeSession s, double idle, double appsWh, double screen, double panel)
        {
            return string.Format(L.T(
                "Idle: {0:0.0} Wh over the discharge, ≈ {1} — what is spent with any apps: board, memory, SSD, Wi-Fi, the shared part of the CPU, the idle NVIDIA GPU{2}.\n" +
                "= by the battery counter {3:0.0} Wh − all apps {4:0.0} Wh − screen {5:0.0} Wh",
                "Простій: {0:0.0} Вт·год за розряд, ≈ {1} — те, що витрачається за будь-яких програм: плата, пам'ять, SSD, Wi-Fi, спільна частина процесора, відеокарта NVIDIA без роботи{2}.\n" +
                "= за лічильником батареї {3:0.0} Вт·год − усі програми {4:0.0} Вт·год − екран {5:0.0} Вт·год"),
                idle, Fmt.W(idle * 3600 / Math.Max(1, s.ActiveS)), double.IsNaN(panel) ? L.T(", the screen panel itself", ", сама панель екрана") : "",
                s.EnergyByRateWh, appsWh, double.IsNaN(screen) ? 0 : screen);
        }

        static string AppTip(CycleProcess p)
        {
            double fgPct = p.Wh > 0 ? p.FgWh / p.Wh * 100 : 0;
            return string.Format(L.T("{0}: {1:0.00} Wh over the discharge, ≈ {2}\nwindow active: {3:0.00} Wh ({4:0} %)\nin the background: {5:0.00} Wh ({6:0} %)\n{7}",
                                     "{0}: {1:0.00} Вт·год за розряд, ≈ {2}\nвікно активне: {3:0.00} Вт·год ({4:0} %)\nу фоні: {5:0.00} Вт·год ({6:0} %)\n{7}"),
                p.Name, p.Wh, Fmt.W(p.AvgW), p.FgWh, fgPct, p.BgWh, 100 - fgPct,
                p.Path ?? L.T("path unknown: the app is not running now or Windows hides it (system process)",
                              "шлях невідомий: програма зараз не запущена або Windows його приховує (системний процес)"));
        }

        static TextBlock GaugeHead()
        {
            var h = HistoryHead(L.T("indicator", "індикатор"));
            h.ToolTip = L.T("Energy delivered by the counter ÷ drop of the remaining capacity that Windows percentages are based on.\n" +
                            "1.00 — the percentages are honest; below 0.92 — they drop faster than energy is used.",
                            "Віддана енергія за лічильником ÷ спад залишку, від якого рахуються відсотки Windows.\n" +
                            "1,00 — відсотки чесні; нижче 0,92 — падають швидше, ніж витрачається енергія.");
            return h;
        }

        static TextBlock GaugeCell(DischargeSession s)
        {
            double r = s.GaugeRatio;
            bool low = r < DischargeHistory.FasterThreshold;
            var t = HistoryCell(double.IsNaN(r) ? "—" : (low ? "⚠ " : "") + r.ToString("0.00") + (s.Jumps > 0 ? " ↯" : ""), low);
            if (low) t.SetResourceReference(TextBlock.ForegroundProperty, Keys.Warning);
            t.ToolTip = s.GaugeDetails();
            return t;
        }

        /// <summary>Полоска на шкале 0–100 %: закрашен диапазон от конечного до начального заряда.</summary>
        static FrameworkElement SocBar(double from, double to)
        {
            double hi = double.IsNaN(from) ? 0 : Math.Max(0, Math.Min(100, from));
            double lo = double.IsNaN(to) ? hi : Math.Max(0, Math.Min(hi, to));
            var g = new Grid { Width = 140, Height = 8, VerticalAlignment = VerticalAlignment.Center };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(lo, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0.5, hi - lo), GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100 - hi, GridUnitType.Star) });
            var track = new Border { CornerRadius = new CornerRadius(4) };
            track.SetResourceReference(Border.BackgroundProperty, Keys.Hover);
            Grid.SetColumnSpan(track, 3);
            var fill = new Border { CornerRadius = new CornerRadius(4) };
            fill.SetResourceReference(Border.BackgroundProperty, Keys.Battery);
            Grid.SetColumn(fill, 1);
            g.Children.Add(track);
            g.Children.Add(fill);
            g.ToolTip = string.Format(L.T("Charge from {0:0} to {1:0} %", "Заряд від {0:0} до {1:0} %"), from, to);
            return g;
        }

        static TextBlock HistoryHead(string text)
        {
            return Label(text, 11, FontWeights.Normal, Keys.Muted);
        }

        static TextBlock HistoryCell(string text, bool strong)
        {
            var t = Label(text, 13, strong ? FontWeights.SemiBold : FontWeights.Normal, strong ? Keys.Text : Keys.Text2);
            Typography.SetNumeralAlignment(t, FontNumeralAlignment.Tabular);
            return t;
        }

        void AddHistoryRow(UIElement[] cells)
        {
            historyGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            int row = historyGrid.RowDefinitions.Count - 1;
            for (int c = 0; c < cells.Length; c++)
            {
                var fe = (FrameworkElement)cells[c];
                fe.Margin = new Thickness(c == 0 ? 0 : 18, 4, 0, 4);
                fe.VerticalAlignment = VerticalAlignment.Center;
                if (c >= 5 || c == 2) fe.HorizontalAlignment = HorizontalAlignment.Right;
                Grid.SetRow(fe, row);
                Grid.SetColumn(fe, c);
                historyGrid.Children.Add(fe);
            }
        }
    }
}
