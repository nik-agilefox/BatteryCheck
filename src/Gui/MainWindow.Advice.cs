using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

namespace BatteryCheck
{
    /// <summary>
    /// Вкладка «Советы»: рекомендации по автономности и кто тратил батарею (встроенные правила, см. Advisor).
    /// Анализ — в фоне при открытии вкладки и раз в минуту, пока она открыта.
    /// </summary>
    sealed partial class MainWindow
    {
        FrameworkElement adviceView;
        StackPanel adviceItems;
        Grid adviceTable;
        TextBlock adviceSummary, quietSummary;
        Grid quietTable;
        DateTime adviceLoadedAt = DateTime.MinValue;
        int adviceLoading;

        FrameworkElement BuildAdviceView()
        {
            var panel = new StackPanel();
            adviceSummary = Label(L.T("Analyzing…", "Аналізую…"), 12, FontWeights.Normal, Keys.Text2);
            adviceSummary.TextWrapping = TextWrapping.Wrap;
            panel.Children.Add(adviceSummary);
            panel.Children.Add(BuildExperimentCard());
            panel.Children.Add(BuildProfileCard());

            panel.Children.Add(SectionTitle(L.T("What can be improved", "Що можна покращити")));
            adviceItems = new StackPanel();
            panel.Children.Add(adviceItems);

            panel.Children.Add(SectionTitle(L.T("While you are away", "Поки ви не працюєте")));
            quietSummary = Label("", 12, FontWeights.Normal, Keys.Text2);
            quietSummary.TextWrapping = TextWrapping.Wrap;
            panel.Children.Add(quietSummary);
            quietTable = new Grid { Margin = new Thickness(0, 4, 0, 0) };
            for (int i = 0; i < 3; i++) quietTable.ColumnDefinitions.Add(new ColumnDefinition { Width = i == 0 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
            panel.Children.Add(quietTable);

            panel.Children.Add(SectionTitle(L.T("What used the battery", "Хто витрачав батарею")));
            adviceTable = new Grid();
            for (int i = 0; i < 7; i++)
                adviceTable.ColumnDefinitions.Add(new ColumnDefinition { Width = i == 0 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
            panel.Children.Add(adviceTable);
            return new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Visibility = Visibility.Collapsed };
        }

        static TextBlock SectionTitle(string text)
        {
            var t = Label(text, 14, FontWeights.SemiBold, Keys.Text);
            t.Margin = new Thickness(0, 16, 0, 8);
            return t;
        }

        void RefreshAdvice(bool force)
        {
            if (!force && (DateTime.UtcNow - adviceLoadedAt).TotalSeconds < 60) return;
            if (Interlocked.Exchange(ref adviceLoading, 1) == 1) return;
            adviceLoadedAt = DateTime.UtcNow;

            var ctx = new AdviceContext
            {
                Brightness = brightness,
                Luma = luma,
                Screen = screenModel,
                DisplaysOnGpu = lastSnap != null ? Math.Max(0, lastSnap.Sample.DisplaysOnGpu) : 0,
                FullWh = lastSnap != null ? lastSnap.Info.FullChargedCapacity / 1000.0 : double.NaN,
                Manufacturer = Machine.Manufacturer,
                GpuDisabled = lastSnap != null && lastSnap.Sample.GpuDisabled,
                TimerMs = lastSnap != null && lastSnap.Sample.Battery.Discharging ? lastSnap.Sample.TimerMs : double.NaN,
            };
            string dir = LogDirectory;
            bool onBattery = lastSnap != null && lastSnap.Sample.Battery.Discharging;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                AdviceReport report = null;
                try
                {
                    string screen = ScreenLuma.InternalScreen().DeviceName;
                    ctx.RefreshHz = Advisor.RefreshRate(screen);
                    ctx.MinRefreshHz = Advisor.MinRefreshRate(screen);
                    ctx.PowerMode = Advisor.PowerMode(onBattery);
                    foreach (var p in Process.GetProcesses())
                        using (p) ctx.Running.Add(p.ProcessName);
                    report = Advisor.Analyze(dir, ctx);
                }
                catch (Exception)
                {
                    report = null;
                }
                finally
                {
                    Interlocked.Exchange(ref adviceLoading, 0);
                    Dispatcher.BeginInvoke(new Action(() => FillAdvice(report)));
                }
            });
        }

        void FillAdvice(AdviceReport r)
        {
            adviceItems.Children.Clear();
            adviceTable.Children.Clear();
            adviceTable.RowDefinitions.Clear();
            if (r == null)
            {
                adviceSummary.Text = L.T("Could not analyze the logs.", "Не вдалося проаналізувати логи.");
                return;
            }

            adviceSummary.Inlines.Clear();
            adviceSummary.Inlines.Add(new Run(r.Hours > 0
                ? string.Format(L.T("Last {0} days: {1} on battery with data collection · {2:0.0} Wh · average power {3}.", "За останні {0} днів: від батареї {1} зі збором даних · {2:0.0} Вт·год · середня потужність {3}."),
                    Advisor.Days, Fmt.Hours(r.Hours), r.BatteryWh, Fmt.W(r.AvgW))
                : L.T("No battery data yet: app statistics are collected in the background while the laptop runs on battery.", "Даних про роботу від батареї поки немає: статистика програм збирається у фоні, коли ноутбук працює від батареї.")));
            adviceSummary.Inlines.Add(new LineBreak());
            adviceSummary.Inlines.Add(new Run(L.T(
                "App energy is an estimate (from CPU cycles and GPU load). “Typical” is your own usual value for the app once there are 3 days on battery with it, otherwise a guideline for its category. " +
                "“+min” is how much longer the laptop would last on a full battery.",
                "Енергія програм — оцінка (за тактами процесора і завантаженням відеокарт). «Зазвичай» — ваша власна норма програми, коли з нею набереться 3 дні від батареї, інакше — орієнтир для її категорії. " +
                "«+хв» — на скільки довше ноутбук пропрацював би від повної батареї.") +
                (r.Vendor != null ? string.Format(L.T(" Laptop vendor: {0}.", " Виробник ноутбука: {0}."), r.Vendor) : "") +
                (r.RulesNote != null ? " " + r.RulesNote : "")) { FontSize = 11 });

            if (r.Items.Count == 0)
                adviceItems.Children.Add(Label(L.T("No issues: app and system consumption is within the usual range.", "Зауважень немає: споживання програм і системи в межах звичайного."), 13, FontWeights.Normal, Keys.Text2));
            foreach (var it in r.Items)
                adviceItems.Children.Add(AdviceItem(it));

            FillQuiet(r.Quiet);

            if (r.Consumers.Count == 0)
            {
                var none = Label(L.T("No data yet: app energy is recorded in the background while the laptop runs on battery.", "Поки немає даних: енергія програм записується у фоні, коли ноутбук працює від батареї."), 13, FontWeights.Normal, Keys.Text2);
                none.TextWrapping = TextWrapping.Wrap;
                AddAdviceRow(new UIElement[] { none });
                Grid.SetColumnSpan(none, 7);
                return;
            }
            AddAdviceRow(new UIElement[]
            {
                HistoryHead(L.T("app", "програма")), HistoryHead(L.T("category", "категорія")), HistoryHead(L.T("total", "всього")), HistoryHead(L.T("share", "частка")),
                HistoryHead(L.T("background, avg", "у фоні, сер.")), HistoryHead(L.T("typical", "зазвичай")), HistoryHead(""),
            });
            int shown = 0;
            foreach (var c in r.Consumers)
            {
                if (shown++ >= 15 || c.Wh < 0.01) break;
                var status = Label(c.Atypical ? L.T("⚠ above typical", "⚠ вище звичайного") : "✓", 12, c.Atypical ? FontWeights.SemiBold : FontWeights.Normal, c.Atypical ? Keys.Text : Keys.Muted);
                if (c.Atypical) status.SetResourceReference(TextBlock.ForegroundProperty, Keys.Warning);
                var name = HistoryCell(c.Name, true);
                name.TextTrimming = TextTrimming.CharacterEllipsis;
                string procName = c.Name;
                AttachProcessMenu(name, () => procName);
                name.ToolTip = string.Format(L.T("{0}: total {1:0.00} Wh, in the active window {2:0.00}, in the background {3:0.00}, on GPUs {4:0.00} Wh", "{0}: всього {1:0.00} Вт·год, в активному вікні {2:0.00}, у фоні {3:0.00}, на відеокарти {4:0.00} Вт·год"),
                    c.Name, c.Wh, c.FgWh, c.BgWh, c.GpuWh);
                AddAdviceRow(new UIElement[]
                {
                    name,
                    HistoryCell(c.Category, false),
                    HistoryCell(c.Wh.ToString("0.00") + " " + Fmt.WhUnit, true),
                    HistoryCell(r.BatteryWh > 0 ? (c.Wh / r.BatteryWh * 100).ToString("0") + " %" : "—", false),
                    HistoryCell(Fmt.W(c.BgAvgW), true),
                    TypicalCell(c),
                    status,
                });
            }
        }

        /// <summary>«Обычно»: своя норма этой программы, если набралась, иначе ориентир категории.</summary>
        static TextBlock TypicalCell(Consumer c)
        {
            bool own = !double.IsNaN(c.OwnNormW);
            var t = HistoryCell(own ? string.Format(L.T("≈ {0:0.0} {1} (yours)", "≈ {0:0.0} {1} (ваша)"), c.OwnNormW, Fmt.WUnit)
                                    : L.T("up to ", "до ") + c.TypicalBgW.ToString("0.0") + " " + Fmt.WUnit, false);
            t.ToolTip = own
                ? string.Format(L.T("Your usual background power for this app: median of {0} days on battery before this week (now {1:0.0} W). Category guideline: up to {2:0.0} W.",
                                    "Ваша звичайна фонова потужність цієї програми: медіана за {0} дн. від батареї до цього тижня (зараз {1:0.0} Вт). Орієнтир категорії: до {2:0.0} Вт."),
                    c.OwnDays, c.OwnNowW, c.TypicalBgW)
                : string.Format(L.T("Guideline for the category; your own usual value appears after {0} days on battery with this app.",
                                    "Орієнтир для категорії; ваша власна норма з'явиться після {0} дн. від батареї з цією програмою."), ProcessHistory.MinDays);
            return t;
        }

        /// <summary>«Пока вы не работаете»: средняя мощность программ в тихие минуты от батареи.</summary>
        void FillQuiet(QuietProcesses.Result q)
        {
            quietTable.Children.Clear();
            quietTable.RowDefinitions.Clear();
            if (q == null || q.Minutes == 0)
            {
                quietSummary.Text = L.T("No quiet minutes on battery yet: a quiet minute is a minute on battery without keyboard or mouse input, with process analysis on.",
                                        "Тихих хвилин від батареї поки немає: тиха хвилина — хвилина від батареї без вводу з клавіатури й миші, з увімкненим аналізом процесів.");
                return;
            }
            double sum = 0;
            foreach (var kv in q.Top) sum += kv.Value;
            quietSummary.Text = string.Format(L.T("{0} quiet minutes on battery over {1} days: the laptop used {2} on average, apps {3} of it (the rest is the screen, board, memory and the shared part of the CPU).",
                                                  "{0} тихих хвилин від батареї за {1} днів: ноутбук витрачав у середньому {2}, з них програми — {3} (решта — екран, плата, пам'ять і спільна частина процесора)."),
                q.Minutes, Advisor.Days, Fmt.W(q.BatteryW), Fmt.W(sum));
            int shown = 0;
            foreach (var kv in q.Top)
            {
                if (shown++ >= 10 || kv.Value < 0.05) break;
                var name = HistoryCell(kv.Key, true);
                string procName = kv.Key;
                AttachProcessMenu(name, () => procName);
                quietTable.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                int row = quietTable.RowDefinitions.Count - 1;
                var cells = new FrameworkElement[]
                {
                    name,
                    HistoryCell(Fmt.W(kv.Value), true),
                    HistoryCell(q.BatteryW > 0 ? (kv.Value / q.BatteryW * 100).ToString("0") + " %" : "—", false),
                };
                for (int c = 0; c < cells.Length; c++)
                {
                    cells[c].Margin = new Thickness(c == 0 ? 0 : 18, 2, 0, 2);
                    if (c > 0) cells[c].HorizontalAlignment = HorizontalAlignment.Right;
                    Grid.SetRow(cells[c], row);
                    Grid.SetColumn(cells[c], c);
                    quietTable.Children.Add(cells[c]);
                }
            }
        }

        FrameworkElement AdviceItem(Recommendation it)
        {
            var b = new Border { CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1), Padding = new Thickness(12, 8, 12, 9), Margin = new Thickness(0, 0, 0, 8) };
            b.SetResourceReference(Border.BorderBrushProperty, Keys.Border);
            b.SetResourceReference(Border.BackgroundProperty, Keys.Page);
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var text = new StackPanel();
            var title = Label(it.Title, 13, FontWeights.SemiBold, Keys.Text);
            title.TextWrapping = TextWrapping.Wrap;
            text.Children.Add(title);
            var detail = Label(it.Detail, 12, FontWeights.Normal, Keys.Text2);
            detail.TextWrapping = TextWrapping.Wrap;
            detail.Margin = new Thickness(0, 3, 0, 0);
            text.Children.Add(detail);
            g.Children.Add(text);

            var right = new StackPanel { Margin = new Thickness(16, 0, 0, 0), HorizontalAlignment = HorizontalAlignment.Right };
            if (!double.IsNaN(it.GainMin) && it.GainMin >= 1)
            {
                var gain = Label("+" + Fmt.Hours(it.GainMin / 60), 15, FontWeights.SemiBold, Keys.Text);
                gain.HorizontalAlignment = HorizontalAlignment.Right;
                right.Children.Add(gain);
            }
            var basis = Label((double.IsNaN(it.SavingW) ? "" : "≈ " + it.SavingW.ToString("0.0") + " " + Fmt.WUnit + " · ") + (it.Basis ?? ""), 11, FontWeights.Normal, Keys.Muted);
            basis.HorizontalAlignment = HorizontalAlignment.Right;
            right.Children.Add(basis);
            var check = new FlatButton(L.T("Check", "Перевірити"));
            check.HorizontalAlignment = HorizontalAlignment.Right;
            check.Margin = new Thickness(0, 6, 0, 0);
            check.ToolTip = L.T("Measure before / after for this advice", "Виміряти до / після для цієї поради");
            check.Click += () => CheckRecommendation(it);
            if (!string.IsNullOrEmpty(it.Basis)) right.Children.Add(check);  // «мало данных» и т. п. проверять нечем
            if (it.Process != null)
            {
                string proc = it.Process;
                var eff = new FlatButton(profile.HasApp(proc) ? L.T("Efficiency mode: on", "Режим ефективності: увімк.") : L.T("Efficiency mode on battery", "Режим ефективності від батареї"));
                eff.HorizontalAlignment = HorizontalAlignment.Right;
                eff.Margin = new Thickness(0, 6, 0, 0);
                eff.ToolTip = L.T("Add or remove this app in the “On battery” profile list", "Додати або прибрати цю програму зі списку профілю «Від батареї»");
                eff.Click += () =>
                {
                    ToggleEfficiencyApp(proc);
                    eff.Text = profile.HasApp(proc) ? L.T("Efficiency mode: on", "Режим ефективності: увімк.") : L.T("Efficiency mode on battery", "Режим ефективності від батареї");
                };
                right.Children.Add(eff);
            }
            Grid.SetColumn(right, 1);
            g.Children.Add(right);
            b.Child = g;
            return b;
        }

        void AddAdviceRow(UIElement[] cells)
        {
            adviceTable.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            int row = adviceTable.RowDefinitions.Count - 1;
            for (int c = 0; c < cells.Length; c++)
            {
                var fe = (FrameworkElement)cells[c];
                fe.Margin = new Thickness(c == 0 ? 0 : 18, 3, 0, 3);
                fe.VerticalAlignment = VerticalAlignment.Center;
                if (c >= 2 && c <= 5) fe.HorizontalAlignment = HorizontalAlignment.Right;
                Grid.SetRow(fe, row);
                Grid.SetColumn(fe, c);
                adviceTable.Children.Add(fe);
            }
        }
    }
}
