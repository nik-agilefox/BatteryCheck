using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

namespace BatteryCheck
{
    /// <summary>
    /// Вкладка «Оптимизация»: сторонние службы (не Microsoft) — процесс, состояние, средний расход от батареи по логам
    /// и сейчас. Программа службы не останавливает (нужны права администратора): пользователь останавливает их сам
    /// в «Службах», а кнопка «Проверить» запускает проверку «до / после», которая попросит остановить службу в нужный момент.
    /// Пока вкладка открыта, идёт замер по процессам (как в «Процессах») — для колонки «сейчас».
    /// </summary>
    sealed partial class MainWindow
    {
        const double ServicesRefreshSeconds = 60, OptimizeFillSeconds = 5, EnergyRefreshSeconds = 300;

        FrameworkElement optimizeView;
        Grid optimizeTable;
        TextBlock optimizeSummary;
        List<ServiceEntry> services;
        ProcessEnergyStats.Result serviceEnergy;
        Rules optimizeRules;
        DateTime servicesAt = DateTime.MinValue, serviceEnergyAt = DateTime.MinValue, optimizeFilledAt = DateTime.MinValue;
        int servicesLoading;

        FrameworkElement BuildOptimizeView()
        {
            var panel = new StackPanel();
            var intro = Label(L.T(
                "Services of other vendors (not Microsoft), the heaviest on battery first. Battery Check does not stop services — that needs administrator rights. " +
                "To stop one: “Open Services” → right-click the service → Stop; it starts again after a reboot. " +
                "“Check” starts a before / after check: it will tell you when to stop the service and then measure the difference.",
                "Служби інших виробників (не Microsoft), найважчі від батареї — вгорі. Battery Check служби не зупиняє — для цього потрібні права адміністратора. " +
                "Щоб зупинити: «Відкрити Служби» → правий клік по службі → Зупинити; після перезавантаження вона запуститься знову. " +
                "«Перевірити» запускає перевірку до / після: вона підкаже, коли зупинити службу, і виміряє різницю."),
                12, FontWeights.Normal, Keys.Text2);
            intro.TextWrapping = TextWrapping.Wrap;
            panel.Children.Add(intro);
            var warn = Label(L.T(
                "Stop only what you understand: some vendor services control fans, keyboard lighting and performance modes; do not stop the services of apps you are using right now (VPN, sync, assistants).",
                "Зупиняйте лише те, що розумієте: частина служб виробника керує вентиляторами, підсвіткою клавіатури й режимами продуктивності; не зупиняйте служби програм, якими зараз користуєтеся (VPN, синхронізація, асистенти)."),
                11, FontWeights.Normal, Keys.Muted);
            warn.TextWrapping = TextWrapping.Wrap;
            warn.Margin = new Thickness(0, 4, 0, 8);
            panel.Children.Add(warn);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
            var open = new FlatButton(L.T("Open Services", "Відкрити Служби"));
            open.ToolTip = L.T("services.msc — Windows asks for administrator rights", "services.msc — Windows спитає права адміністратора");
            open.Click += ServiceControl.OpenServices;
            buttons.Children.Add(open);
            panel.Children.Add(buttons);

            optimizeSummary = Label(L.T("Reading services…", "Читаю служби…"), 12, FontWeights.Normal, Keys.Text2);
            optimizeSummary.TextWrapping = TextWrapping.Wrap;
            optimizeSummary.Margin = new Thickness(0, 0, 0, 6);
            panel.Children.Add(optimizeSummary);

            optimizeTable = new Grid();
            for (int i = 0; i < 6; i++)
                optimizeTable.ColumnDefinitions.Add(new ColumnDefinition { Width = i == 0 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
            panel.Children.Add(optimizeTable);
            if (services != null) FillOptimize();
            return new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Visibility = Visibility.Collapsed };
        }

        /// <summary>Список служб — в фоне раз в минуту, расход по логам — раз в 5 минут; таблица (колонка «сейчас») — раз в 5 с.</summary>
        void RefreshOptimize(bool force)
        {
            bool reload = force || (DateTime.UtcNow - servicesAt).TotalSeconds >= ServicesRefreshSeconds;
            if (reload && Interlocked.Exchange(ref servicesLoading, 1) == 0)
            {
                servicesAt = DateTime.UtcNow;
                bool energy = force || serviceEnergy == null || (DateTime.UtcNow - serviceEnergyAt).TotalSeconds >= EnergyRefreshSeconds;
                if (energy) serviceEnergyAt = DateTime.UtcNow;
                string dir = LogDirectory;
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    List<ServiceEntry> list = null;
                    ProcessEnergyStats.Result stats = null;
                    Rules rules = null;
                    try
                    {
                        list = ServiceControl.List();
                        if (energy) stats = ProcessEnergyStats.Load(dir, Advisor.Days, DateTime.Now);
                        rules = Rules.Load(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(dir)));
                    }
                    catch (Exception) { }
                    finally
                    {
                        Interlocked.Exchange(ref servicesLoading, 0);
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            if (list != null) services = list;
                            if (stats != null) serviceEnergy = stats;
                            if (rules != null) optimizeRules = rules;
                            FillOptimize();
                        }));
                    }
                });
            }
            else if ((DateTime.UtcNow - optimizeFilledAt).TotalSeconds >= OptimizeFillSeconds && services != null)
            {
                FillOptimize();
            }
        }

        double ServiceAvgW(ServiceEntry s)
        {
            double w;
            return serviceEnergy != null && serviceEnergy.AvgW.TryGetValue(s.Process, out w) ? w : double.NaN;
        }

        double ServiceNowW(ServiceEntry s)
        {
            if (lastSnap == null || lastSnap.Processes == null || !s.Running) return double.NaN;
            foreach (var p in lastSnap.Processes)
                if (string.Equals(p.Name, s.Process, StringComparison.OrdinalIgnoreCase)) return p.PowerW;
            return 0;  // в замере не нашлось — меньше самых мелких из сохранённых (их 30)
        }

        void FillOptimize()
        {
            if (optimizeTable == null || services == null) return;
            optimizeFilledAt = DateTime.UtcNow;
            optimizeTable.Children.Clear();
            optimizeTable.RowDefinitions.Clear();

            var list = new List<ServiceEntry>(services);
            list.Sort((a, b) =>
            {
                double wa = ServiceAvgW(a), wb = ServiceAvgW(b);
                if (double.IsNaN(wa) != double.IsNaN(wb)) return double.IsNaN(wa) ? 1 : -1;
                int c = (double.IsNaN(wb) ? 0 : wb).CompareTo(double.IsNaN(wa) ? 0 : wa);
                if (c != 0) return c;
                c = b.Running.CompareTo(a.Running);
                return c != 0 ? c : string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase);
            });

            int running = 0;
            double total = 0;
            foreach (var s in list)
            {
                if (s.Running) running++;
                double w = ServiceAvgW(s);
                if (!double.IsNaN(w)) total += w;
            }
            optimizeSummary.Inlines.Clear();
            optimizeSummary.Inlines.Add(new Run(string.Format(L.T("{0} third-party services, {1} running.", "{0} сторонніх служб, працює {1}."), list.Count, running)));
            if (serviceEnergy != null && serviceEnergy.Hours > 0)
                optimizeSummary.Inlines.Add(new Run(string.Format(L.T(" On battery over the last {0} days ({1} with data) they used ≈ {2} together.",
                                                                      " Від батареї за останні {0} днів ({1} з даними) разом ≈ {2}."),
                    Advisor.Days, Fmt.Hours(serviceEnergy.Hours), Fmt.W(total))));
            else
                optimizeSummary.Inlines.Add(new Run(L.T(" No battery data yet: the “on battery” column fills in after some time on battery with process analysis on.",
                                                        " Даних від батареї поки немає: колонка «від батареї» заповниться після роботи від батареї з увімкненим аналізом процесів.")));

            AddOptimizeRow(new UIElement[]
            {
                HistoryHead(L.T("service", "служба")), HistoryHead(L.T("process", "процес")), HistoryHead(L.T("state", "стан")),
                HistoryHead(L.T("on battery, avg", "від батареї, сер.")), HistoryHead(L.T("now", "зараз")), HistoryHead(""),
            });
            foreach (var s in list)
            {
                var entry = s;
                bool poller = optimizeRules != null && optimizeRules.GpuPollers.Contains(s.Process);
                var name = HistoryCell((poller ? "⚑ " : "") + s.DisplayName, s.Running);
                name.TextTrimming = TextTrimming.CharacterEllipsis;
                name.ToolTip = string.Format("{0}\n{1}{2}\n{3}: {4}{5}", s.Name, s.Company.Length > 0 ? s.Company + "\n" : "", s.Exe,
                    L.T("startup", "запуск"), s.StartMode,
                    poller ? "\n" + L.T("⚑ known to poll the discrete GPU", "⚑ відомо, що опитує дискретну відеокарту") : "");
                if (poller) name.SetResourceReference(TextBlock.ForegroundProperty, Keys.Warning);
                var check = new FlatButton(L.T("Check", "Перевірити"));
                check.ToolTip = L.T("Before / after check: you will be asked to stop this service in Services", "Перевірка до / після: вас попросять зупинити цю службу в «Службах»");
                check.Click += () => CheckService(entry);
                check.Visibility = s.Running ? Visibility.Visible : Visibility.Hidden;
                AddOptimizeRow(new UIElement[]
                {
                    name,
                    HistoryCell(s.Process, false),
                    HistoryCell(s.Running ? L.T("running", "працює") : L.T("stopped", "зупинена"), false),
                    HistoryCell(Fmt.W(ServiceAvgW(s)), true),
                    HistoryCell(Fmt.W(ServiceNowW(s)), false),
                    check,
                });
            }
        }

        void AddOptimizeRow(UIElement[] cells)
        {
            optimizeTable.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            int row = optimizeTable.RowDefinitions.Count - 1;
            for (int c = 0; c < cells.Length; c++)
            {
                var fe = (FrameworkElement)cells[c];
                fe.Margin = new Thickness(c == 0 ? 0 : 16, 2, 0, 2);
                fe.VerticalAlignment = VerticalAlignment.Center;
                if (c == 3 || c == 4) fe.HorizontalAlignment = HorizontalAlignment.Right;
                Grid.SetRow(fe, row);
                Grid.SetColumn(fe, c);
                optimizeTable.Children.Add(fe);
            }
        }

        /// <summary>«Проверить» у службы: во вкладку «Советы» и проверка «до / после» с её названием.</summary>
        void CheckService(ServiceEntry s)
        {
            mode = ChartMode.Advice;
            SaveChartMode(mode);
            modeSwitch.Select((int)mode);
            ApplyMode();
            StartExperiment(L.T("stopped ", "зупинив ") + s.DisplayName + " (" + s.Name + ")");
            Dispatcher.BeginInvoke(new Action(() => { if (expCard != null) expCard.BringIntoView(); }), System.Windows.Threading.DispatcherPriority.Loaded);
        }
    }
}
