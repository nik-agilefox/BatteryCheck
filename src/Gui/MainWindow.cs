using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using Microsoft.Win32;

namespace BatteryCheck
{
    sealed partial class MainWindow : Window
    {
        readonly Sampler sampler;
        Theme theme;

        readonly PowerChart chart = new PowerChart();
        readonly PowerSpark powerSpark = new PowerSpark();
        TextBlock subtitle, stateText, footerText;
        Ellipse stateDot;
        TextBlock powerLabel, powerValue, powerSub, socValue, socSub, leftLabel, leftValue, leftSub, healthValue, healthSub;
        ColumnDefinition socFilled, socEmpty;
        Border socFill;
        TextBlock[] cpuRow, coresRow, igpuRow, gpuRow, restRow;
        TextBlock restNote, sleepNote;
        TextBlock bDesign, bFull, bWear, bCycles, bVoltage, bCurrent, bDisplays;
        TextBlock sDuration, sByRate, sByCap, sMismatch, sAvg, sMinMax, sSleep, sHint;
        FlatButton gpuToggle;
        Segmented langSwitch;

        public MainWindow(Sampler sampler, Theme theme)
        {
            this.sampler = sampler;
            this.theme = theme;
            LastTotalW = double.NaN;

            Title = "Battery Check";
            Width = 1120;
            Height = 860;
            MinWidth = 980;  // уже — в таблице компонентов обрезаются подписи, а в заголовке карточки не помещаются кнопки
            MinHeight = 720;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            FontFamily = new FontFamily("Segoe UI");
            FontSize = 13;
            UseLayoutRounding = true;
            SetResourceReference(BackgroundProperty, Keys.Page);
            SetResourceReference(ForegroundProperty, Keys.Text);

            RestorePendingBrightness();
            mode = LoadChartMode();
            SeedRestFromLogs();
            chart.Window = LoadChartWindow();
            // Подписки на постоянные элементы — здесь, а не в Build*: разметка пересобирается при смене языка.
            chart.ViewChanged += OnChartViewChanged;
            chart.ToolTip = null;
            powerSpark.MouseLeftButtonUp += (s, e) => ShowChargerMenu();
            StartEconomyTimer();
            Content = BuildLayout();
            chart.Theme = theme;
            ApplyMode();

            SourceInitialized += (s, e) => ApplyTitleBar();
            StateChanged += (s, e) =>
            {
                if (cpuRow != null) cpuRow[0].Text = CpuRowText;
                RenderChart();
            };
            IsVisibleChanged += (s, e) => RenderChart();
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
            Closed += (s, e) =>
            {
                SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
                SetThreadExecutionState(EsContinuous);  // если шла проверка «до / после» — вернуть гашение экрана
            };
        }

        /// <summary>Окно видно на экране: не скрыто в трей и не свёрнуто.</summary>
        public bool IsOnScreen
        {
            get { return IsVisible && WindowState != WindowState.Minimized; }
        }

        public Theme CurrentTheme
        {
            get { return theme; }
        }

        /// <summary>Полная мощность последнего замера: от батареи — разряд, от сети — оценка входа от блока (для значка в трее).</summary>
        public double LastTotalW { get; private set; }

        /// <summary>Тема сменилась (вслед за Windows) — значку в трее тоже нужно перекраситься.</summary>
        public event Action ThemeChanged;

        /// <summary>Язык интерфейса сменился — меню и подсказке значка в трее тоже.</summary>
        public event Action LanguageChanged;

        public void SyncGpuToggle()
        {
            UpdateGpuToggle();
        }

        /// <summary>Кнопки в заголовке карточки «Компоненты»: пробуждения NVIDIA и замер экрана — вверху, чтобы не занимать строку внизу.</summary>
        UIElement ComponentsHeaderButtons()
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var wakesBtn = BuildWakesButton();
            wakesBtn.Margin = new Thickness(0, 0, 8, 0);
            sp.Children.Add(wakesBtn);
            sp.Children.Add(BuildScreenButton());
            return sp;
        }

        /// <summary>«RTX 5070 Ti Laptop GPU» → «RTX 5070 Ti»: в таблице компонентов мало места, полное имя — в подсказке.</summary>
        static string ShortGpuName(string name)
        {
            if (name == null) return null;
            return name.Replace(" Laptop GPU", "").Replace(" Laptop", "").Replace(" GPU", "").Trim();
        }

        /// <summary>В таблице компонентов: полное «without» — только в развёрнутом окне, иначе не помещается.</summary>
        string CpuRowText
        {
            get { return WindowState == WindowState.Maximized ? CpuNoGraphicsText : L.T("CPU (w/o graphics)", "Процесор (без графіки)"); }
        }

        static string RemainingText { get { return L.T("Remaining", "Залишилось"); } }
        internal static string GpuDisabledText
        {
            get
            {
                return L.T("The discrete GPU is disabled in Device Manager. Without its driver nothing puts it to sleep, and it may draw more than a sleeping one (on a Predator PHN16S-71: +17 W). Enable it: Device Manager → Display adapters → right-click → Enable device.",
                           "Дискретну відеокарту вимкнено в диспетчері пристроїв. Без драйвера її нікому приспати, і вона може споживати більше, ніж спляча (на Predator PHN16S-71: +17 Вт). Увімкніть її: Диспетчер пристроїв → Відеоадаптери → правий клік → Увімкнути пристрій.");
            }
        }
        static string GpuText { get { return L.T("GPU", "Відеокарта"); } }
        static string RestNoteText
        {
            get { return L.T("screen, SSD, network, board = battery − CPU − GPU", "екран, SSD, мережа, плата = батарея − процесор − відеокарта"); }
        }

        /// <summary>
        /// Смена языка: разметка строится заново (все подписи берутся при построении), данные — история графика,
        /// журналы, замеры — остаются. График и мини-график живут дольше разметки, их только переносим.
        /// </summary>
        void SetLanguage(bool ua)
        {
            if (ua == L.Ua) return;
            if (calPhase >= 0)  // замер экрана идёт — его подписи на экране, не переключаем посреди
            {
                langSwitch.Select(L.Ua ? 1 : 0);
                return;
            }
            L.Apply(ua);
            L.Save();
            Detach(chart);
            Detach(powerSpark);
            if (wakesWindow != null) wakesWindow.Close();
            legendKey = null;
            Content = BuildLayout();
            ApplyMode();
            if (lastSnap != null) UpdateView(lastSnap);
            var h = LanguageChanged;
            if (h != null) h();
        }

        static void Detach(FrameworkElement e)
        {
            var p = e.Parent as Panel;
            if (p != null) p.Children.Remove(e);
        }

        // ---------------- Разметка ----------------

        UIElement BuildLayout()
        {
            var root = new Grid { Margin = new Thickness(20, 16, 20, 14) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                     // заголовок
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                     // плитки
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 310 }); // график
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                     // карточки
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                     // подвал

            Add(root, BuildHeader(), 0);
            Add(root, BuildTiles(), 1);
            Add(root, BuildChartCard(), 2);
            Add(root, BuildCards(), 3);
            Add(root, BuildFooter(), 4);
            return root;
        }

        UIElement BuildHeader()
        {
            var dp = new DockPanel { Margin = new Thickness(0, 0, 0, 14) };

            var pill = new Border { CornerRadius = new CornerRadius(12), Padding = new Thickness(10, 4, 12, 4), VerticalAlignment = VerticalAlignment.Center };
            pill.SetResourceReference(Border.BackgroundProperty, Keys.Hover);
            var pillContent = new StackPanel { Orientation = Orientation.Horizontal };
            stateDot = new Ellipse { Width = 8, Height = 8, Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center };
            stateDot.SetResourceReference(Shape.FillProperty, Keys.Muted);
            stateText = Label("—", 13, FontWeights.SemiBold, Keys.Text);
            pillContent.Children.Add(stateDot);
            pillContent.Children.Add(stateText);
            pill.Child = pillContent;
            DockPanel.SetDock(pill, Dock.Right);
            dp.Children.Add(pill);

            // Язык — слева от состояния питания (следующий элемент с Dock.Right встаёт левее предыдущего).
            langSwitch = new Segmented(new[] { "EN", "UA" });
            langSwitch.Select(L.Ua ? 1 : 0);
            langSwitch.Margin = new Thickness(0, 0, 10, 0);
            langSwitch.ToolTip = L.T("Interface language", "Мова інтерфейсу");
            langSwitch.Changed += i => Dispatcher.BeginInvoke(new Action(() => SetLanguage(i == 1)));  // после клика: разметка с самим переключателем заменится
            DockPanel.SetDock(langSwitch, Dock.Right);
            dp.Children.Add(langSwitch);

            var economy = BuildEconomyButton();
            economy.Margin = new Thickness(0, 0, 10, 0);
            DockPanel.SetDock(economy, Dock.Right);
            dp.Children.Add(economy);

            var titles = new StackPanel();
            titles.Children.Add(Label("Battery Check", 20, FontWeights.SemiBold, Keys.Text));
            subtitle = Label("", 12, FontWeights.Normal, Keys.Text2);
            titles.Children.Add(subtitle);
            dp.Children.Add(titles);
            return dp;
        }

        UIElement BuildTiles()
        {
            // Плитка мощности шире: в ней число, две строки и мини-график; «Осталось» и «Здоровью» хватает меньшего.
            var g = new Grid { Margin = new Thickness(0, 0, 0, 12) };
            double[] widths = { 1.35, 1, 1, 0.9 };
            for (int i = 0; i < 7; i++)
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = i % 2 == 1 ? new GridLength(12) : new GridLength(widths[i / 2], GridUnitType.Star) });

            g.Children.Add(At(Tile(out powerLabel, L.T("Power draw", "Споживання"), out powerValue, out powerSub, null, powerSpark), 0, 0));

            var track = new Grid { Height = 6, Margin = new Thickness(0, 8, 0, 0) };
            socFilled = new ColumnDefinition { Width = new GridLength(0, GridUnitType.Star) };
            socEmpty = new ColumnDefinition { Width = new GridLength(100, GridUnitType.Star) };
            track.ColumnDefinitions.Add(socFilled);
            track.ColumnDefinitions.Add(socEmpty);
            var trackBg = new Border { CornerRadius = new CornerRadius(3) };
            trackBg.SetResourceReference(Border.BackgroundProperty, Keys.Hover);
            Grid.SetColumnSpan(trackBg, 2);
            socFill = new Border { CornerRadius = new CornerRadius(3) };
            socFill.SetResourceReference(Border.BackgroundProperty, Keys.Battery);
            track.Children.Add(trackBg);
            track.Children.Add(socFill);
            TextBlock socLabel;
            g.Children.Add(At(Tile(out socLabel, L.T("Charge", "Заряд"), out socValue, out socSub, track), 0, 2));

            g.Children.Add(At(Tile(out leftLabel, RemainingText, out leftValue, out leftSub, null), 0, 4));
            TextBlock healthLabel;
            g.Children.Add(At(Tile(out healthLabel, L.T("Battery health", "Здоров'я батареї"), out healthValue, out healthSub, null), 0, 6));
            return g;
        }

        Border Tile(out TextBlock label, string text, out TextBlock value, out TextBlock sub, UIElement extra)
        {
            return Tile(out label, text, out value, out sub, extra, null);
        }

        /// <summary>Плитка: подпись, крупное значение, пояснение; right — необязательный элемент справа (шкала).</summary>
        Border Tile(out TextBlock label, string text, out TextBlock value, out TextBlock sub, UIElement extra, FrameworkElement right)
        {
            var sp = new StackPanel();
            label = Label(text, 12, FontWeights.Normal, Keys.Text2);
            sp.Children.Add(label);
            value = Label("—", 28, FontWeights.SemiBold, Keys.Text);
            value.Margin = new Thickness(0, 4, 0, 0);
            sp.Children.Add(value);
            if (extra != null) sp.Children.Add(extra);
            sub = Label("", 12, FontWeights.Normal, Keys.Text2);
            sub.Margin = new Thickness(0, 6, 0, 0);
            sub.TextTrimming = TextTrimming.CharacterEllipsis;
            sp.Children.Add(sub);
            if (right == null) return Card(sp, null, null);

            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.Children.Add(sp);
            right.Margin = new Thickness(10, 0, 0, 0);
            Grid.SetColumn(right, 1);
            g.Children.Add(right);
            return Card(g, null, null);
        }

        UIElement BuildChartCard()
        {
            modeSwitch = new Segmented(new[] { L.T("Components", "Компоненти"), L.T("Processes", "Процеси"), L.T("History", "Історія"), L.T("Advice", "Поради"), L.T("Optimization", "Оптимізація") });
            modeSwitch.Select((int)mode);
            modeSwitch.Changed += i =>
            {
                mode = (ChartMode)i;
                SaveChartMode(mode);
                ApplyMode();
            };

            rangeSwitch = new Segmented(PowerChart.WindowLabels);
            rangeSwitch.Select(Array.IndexOf(PowerChart.Windows, chart.Window));
            rangeSwitch.Changed += i =>
            {
                chart.Window = PowerChart.Windows[i];
                OnChartViewChanged();
            };
            liveButton = new FlatButton(L.T("Back to now", "До поточного"));
            liveButton.ToolTip = L.T("Return to the latest data (or double-click the chart)", "Повернутися до останніх даних (або подвійний клік по графіку)");
            liveButton.Click += () =>
            {
                chart.GoLive();
                OnChartViewChanged();
            };
            // В заголовке — только вкладки; период и «К текущему» относятся к графику и стоят под ним справа,
            // чтобы легенда помещалась в одну строку.
            var viewControls = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 6, 0, 0) };
            liveButton.Margin = new Thickness(0, 0, 10, 0);
            viewControls.Children.Add(liveButton);
            viewControls.Children.Add(rangeSwitch);

            legendPanel = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
            var body = new DockPanel();
            DockPanel.SetDock(legendPanel, Dock.Top);
            body.Children.Add(legendPanel);
            DockPanel.SetDock(viewControls, Dock.Bottom);
            body.Children.Add(viewControls);
            chart.MinHeight = 180;
            body.Children.Add(chart);

            // «История» и «Советы» занимают то же место, что график: переключаются видимостью.
            chartBody = body;
            historyView = BuildHistoryView();
            adviceView = BuildAdviceView();
            optimizeView = BuildOptimizeView();
            var holder = new Grid();
            holder.Children.Add(chartBody);
            holder.Children.Add(historyView);
            holder.Children.Add(adviceView);
            holder.Children.Add(optimizeView);

            var card = Card(holder, PowerTitle, modeSwitch, out chartTitle);
            card.Margin = new Thickness(0, 0, 0, 12);
            UpdateChartTitle();
            return card;
        }

        UIElement BuildCards()
        {
            var g = new Grid { Margin = new Thickness(0, 0, 0, 10) };
            // Компонентам — больше: в строках длинные подписи («Процессор (без графики)»), а в «Батарее» и «Сессии» — короткие.
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.5, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            // Компоненты
            var comp = new Table(new[] { "", L.T("now", "зараз"), L.T("avg 10 s", "сер. 10 с"), "" });
            // Порядок и цвета — как у линий графика; процессор без графики, чтобы слагаемые не пересекались.
            cpuRow = comp.Row(CpuRowText, Keys.Cpu);
            coresRow = comp.Row(L.T("   cores", "   ядра"), null);
            igpuRow = comp.Row(IgpuText, Keys.Igpu);
            gpuRow = comp.Row(GpuText, Keys.Gpu);
            screenRow = comp.Row(L.T("Screen (OLED)", "Екран (OLED)"), null);
            restRow = comp.Row(L.T("Rest", "Решта"), null);
            var compPanel = new StackPanel();
            compPanel.Children.Add(comp.Grid);
            restNote = Label(RestNoteText, 11, FontWeights.Normal, Keys.Muted);
            restNote.Margin = new Thickness(0, 6, 0, 0);
            restNote.TextWrapping = TextWrapping.Wrap;
            compPanel.Children.Add(restNote);
            sleepNote = Label("", 11, FontWeights.Normal, Keys.Muted);
            sleepNote.Margin = new Thickness(0, 2, 0, 0);
            sleepNote.TextWrapping = TextWrapping.Wrap;
            compPanel.Children.Add(sleepNote);
            componentsView = compPanel;
            processesView = BuildProcessesView();
            var leftContent = new Grid();
            leftContent.Children.Add(componentsView);
            leftContent.Children.Add(processesView);
            g.Children.Add(At(Card(leftContent, L.T("Components", "Компоненти"), ComponentsHeaderButtons(), out leftCardTitle), 0, 0));

            // Батарея
            var bat = new Table(null);
            bDesign = bat.Pair(L.T("Design capacity", "Паспортна ємність"));
            bFull = bat.Pair(L.T("Full charge capacity", "Повна ємність"));
            bWear = bat.Pair(L.T("Wear", "Знос"));
            bCycles = bat.Pair(L.T("Charge cycles", "Цикли заряду"));
            bVoltage = bat.Pair(L.T("Voltage", "Напруга"));
            bCurrent = bat.Pair(L.T("Current", "Струм"));
            bDisplays = bat.Pair(L.T("Displays", "Екрани"));
            g.Children.Add(At(Card(bat.Grid, L.T("Battery", "Батарея"), null), 0, 2));

            // Сессия разряда
            var reset = new FlatButton(L.T("Reset", "Скинути"));
            reset.Click += () => sampler.ResetSession();
            var ses = new Table(null);
            sDuration = ses.Pair(L.T("Duration", "Тривалість"));
            sByRate = ses.Pair(L.T("Energy by power", "Енергія за потужністю"));
            sByCap = ses.Pair(L.T("By remaining charge", "За залишком заряду"));
            sMismatch = ses.Pair(L.T("Mismatch", "Розбіжність"));
            sAvg = ses.Pair(L.T("Average power", "Середня потужність"));
            sMinMax = ses.Pair(L.T("Min / max", "Мін / макс"));
            sSleep = ses.Pair(L.T("Last sleep", "Останній сон"));
            var sesPanel = new StackPanel();
            sesPanel.Children.Add(ses.Grid);
            sHint = Label("", 11, FontWeights.Normal, Keys.Muted);
            sHint.Margin = new Thickness(0, 6, 0, 0);
            sHint.TextWrapping = TextWrapping.Wrap;
            sesPanel.Children.Add(sHint);
            g.Children.Add(At(Card(sesPanel, L.T("Discharge session", "Сесія розряду"), reset), 0, 4));
            return g;
        }

        UIElement BuildFooter()
        {
            var dp = new DockPanel();
            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            gpuToggle = new FlatButton("");
            gpuToggle.Click += () =>
            {
                sampler.GpuPolling = !sampler.GpuPolling;
                UpdateGpuToggle();
            };
            UpdateGpuToggle();
            var logs = new FlatButton(L.T("Log folder", "Тека логів"));
            logs.Margin = new Thickness(8, 0, 0, 0);
            logs.Click += OpenLogFolder;
            buttons.Children.Add(BuildUpdateButton());
            buttons.Children.Add(gpuToggle);
            buttons.Children.Add(logs);
            DockPanel.SetDock(buttons, Dock.Right);
            dp.Children.Add(buttons);

            // Слева — выключатель сбора данных о программах для рекомендаций (в фоне от батареи, ~0,2 % ядра).
            analysisToggle = new FlatButton("");
            analysisToggle.Margin = new Thickness(0, 0, 12, 0);
            analysisToggle.Click += () =>
            {
                sampler.BackgroundProcesses = !sampler.BackgroundProcesses;
                Settings.SetDouble("BackgroundProcesses", sampler.BackgroundProcesses ? 1 : 0);
                UpdateAnalysisToggle();
            };
            sampler.BackgroundProcesses = Settings.GetDouble("BackgroundProcesses", 1) != 0;
            UpdateAnalysisToggle();
            DockPanel.SetDock(analysisToggle, Dock.Left);
            dp.Children.Add(analysisToggle);

            footerText = Label("", 12, FontWeights.Normal, Keys.Muted);
            footerText.VerticalAlignment = VerticalAlignment.Center;
            footerText.TextTrimming = TextTrimming.CharacterEllipsis;
            dp.Children.Add(footerText);
            return dp;
        }

        FlatButton analysisToggle;

        void UpdateAnalysisToggle()
        {
            analysisToggle.Text = sampler.BackgroundProcesses ? L.T("Process analysis: on", "Аналіз процесів: увімк.") : L.T("Process analysis: off", "Аналіз процесів: вимк.");
            analysisToggle.ToolTip = L.T(
                "Collects app data for the Advice tab: on battery, a process snapshot every 10 s (~0.2 % of one core);\n" +
                "app energy is written to logs\\process_energy_*.csv. Nothing is collected on AC. The Processes view on screen works either way.",
                "Збір даних про програми для вкладки «Поради»: від батареї знімок процесів раз на 10 с (~0,2 % одного ядра),\n" +
                "енергія програм пишеться в logs\\process_energy_*.csv. Від мережі збір не йде. Режим «Процеси» на екрані працює в будь-якому разі.");
        }

        void UpdateGpuToggle()
        {
            gpuToggle.Text = sampler.GpuPolling ? L.T("NVIDIA polling: on", "Опитування NVIDIA: увімк.") : L.T("NVIDIA polling: off", "Опитування NVIDIA: вимк.");
            gpuToggle.ToolTip = L.T("GPU power is read through NVML only while the GPU is already active: the app never wakes a sleeping GPU.", "Потужність відеокарти читається через NVML, лише коли вона вже працює: сплячу програма не будить.");
        }

        // ---------------- Данные ----------------

        public void ShowError(string message)
        {
            footerText.Text = message;
        }

        string footerNote;
        DateTime footerNoteUntil = DateTime.MinValue;

        /// <summary>Сообщение в нижней строке на 30 с — иначе его сразу перезапишет строка об опросе.</summary>
        void ShowNote(string text)
        {
            footerNote = text;
            footerNoteUntil = DateTime.UtcNow.AddSeconds(30);
            footerText.Text = text;
        }

        public void ShowSnapshot(Snapshot snap)
        {
            var s = snap.Sample;
            var b = s.Battery;
            var info = snap.Info;
            bool rel = info.IsRelative;

            double totalW = TrackPowerInput(snap);  // от батареи — измеренный разряд, от сети — оценка входа от блока
            LastTotalW = totalW;
            UpdateTitle();
            // Для графика: потребление системы и «остальное» — и от сети (оценкой по последнему замеру от батареи).
            double systemW = b.Discharging ? s.DischargeW
                : b.OnLine && !double.IsNaN(s.CpuPkgW) ? s.CpuPkgW + Nz(s.GpuW) + Nz(restRefW) : double.NaN;
            double restLine = b.Discharging ? snap.RestW : b.OnLine ? restRefW : double.NaN;
            // «Остальное» сглажено за 10 с (батарея и счётчики процессора обновляются не синхронно) и после пика ещё
            // несколько секунд «помнит» его — на графике выходило выше «Всего». Не выше мгновенного остатка.
            if (b.Discharging && !double.IsNaN(restLine) && !double.IsNaN(s.DischargeW) && !double.IsNaN(s.CpuPkgW))
                restLine = Math.Min(restLine, Math.Max(0, s.DischargeW - s.CpuPkgW - Nz(s.GpuW)));
            // NVIDIA работает, но не опрашивается (выключен опрос или идёт замер экрана): её ватты сидели бы в «остальном» —
            // на графике это выглядело как скачок Rest. Лучше разрыв линии.
            if (s.GpuDState == 0 && double.IsNaN(s.GpuW)) restLine = double.NaN;
            history.Add(snap, totalW, systemW, restLine);
            lastSnap = snap;
            UpdateWakes(snap);          // журнал пробуждений NVIDIA — и когда окно скрыто
            MaybeRefreshBaseline();
            RecordExperimentSample(snap);  // проверка «до / после» — и когда окно скрыто
            UpdateProfile(snap);           // профиль «На батарее»: применить через 5 с после отключения, откатить при подключении
            CheckNotifications(snap);
            OnCalibrationSample(snap);  // замер экрана: следующая фаза или прерывание (в том числе если окно скрыто)
            if (!IsOnScreen) return;  // в трее или свёрнуто — копим данные для графика, но не рисуем
            UpdateView(snap);
        }

        /// <summary>Показывает замер, ничего не записывая (повторно — после пересборки разметки).</summary>
        void UpdateView(Snapshot snap)
        {
            var s = snap.Sample;
            var b = s.Battery;
            var info = snap.Info;
            bool rel = info.IsRelative;
            double totalW = LastTotalW;

            // Заголовок и состояние питания
            var model = new System.Collections.Generic.List<string>();
            foreach (var part in new[] { info.DeviceName, info.Manufacturer, info.Chemistry })
                if (!string.IsNullOrEmpty(part)) model.Add(part);
            subtitle.Text = string.Join(" · ", model);

            string dotKey;
            if (b.Critical) { stateText.Text = L.T("Critical charge", "Критичний заряд"); dotKey = Keys.Critical; }
            else if (b.Discharging) { stateText.Text = L.T("On battery", "Від батареї"); dotKey = Keys.Battery; }
            else if (b.Charging) { stateText.Text = L.T("Charging", "Заряджається"); dotKey = Keys.Good; }
            else if (b.OnLine) { stateText.Text = L.T("Plugged in", "Від мережі"); dotKey = Keys.Muted; }
            else { stateText.Text = "—"; dotKey = Keys.Muted; }
            stateDot.SetResourceReference(Shape.FillProperty, dotKey);

            // Плитки
            UpdatePowerTile(snap, totalW);
            if (b.Discharging || b.Charging)
            {
                leftLabel.Text = b.Discharging ? RemainingText : L.T("Until full", "До повного заряду");
                SetHero(leftValue, "≈ " + Fmt.Hours(snap.HoursLeft), null);
                leftSub.Text = L.T("based on 30 s average power", "за середньою потужністю за 30 с");
            }
            else
            {
                leftLabel.Text = RemainingText;
                SetHero(leftValue, "—", null);
                leftSub.Text = b.OnLine ? L.T("running on AC", "робота від мережі") : "";
            }

            SetHero(socValue, Fmt.Num(snap.SocPct, "0.0"), "%");
            socSub.Text = (b.HasCapacity ? Fmt.Wh(b.Capacity, rel) : "—") + L.T(" of ", " з ") + Fmt.Wh(info.FullChargedCapacity, rel);
            double soc = double.IsNaN(snap.SocPct) ? 0 : Math.Max(0, Math.Min(100, snap.SocPct));
            socFilled.Width = new GridLength(soc, GridUnitType.Star);
            socEmpty.Width = new GridLength(100 - soc, GridUnitType.Star);
            socFill.SetResourceReference(Border.BackgroundProperty, soc < 10 ? Keys.Critical : soc < 20 ? Keys.Warning : Keys.Battery);

            double health = info.DesignedCapacity > 0 ? 100.0 * info.FullChargedCapacity / info.DesignedCapacity : double.NaN;
            SetHero(healthValue, Fmt.Num(health, "0.0"), "%");
            string cycles = info.CycleCount > 0 ? info.CycleCount + " " + L.Plural(info.CycleCount, "cycle", "cycles", "цикл", "цикли", "циклів") : L.T("cycles: n/a", "цикли: н/д");
            healthSub.Text = (health < 80 ? L.T("⚠ below 80 % · ", "⚠ нижче 80 % · ") : "") + L.T("wear ", "знос ") + Fmt.Num(info.WearPercent, "0.0") + " % · " + cycles;

            // Компоненты
            if (snap.RaplAvailable)
            {
                SetRow(cpuRow, CpuWithoutGraphics(s.CpuPkgW, s.IgpuW), CpuWithoutGraphics(snap.Cpu10W, snap.Igpu10W), "");
                SetRow(coresRow, s.CpuCoresW, double.NaN, "");
                SetRow(igpuRow, s.IgpuW, snap.Igpu10W, "");
                cpuRow[0].ToolTip = string.Format(L.T("Whole CPU package: {0} = CPU (cores and shared part) + integrated graphics", "Увесь чип процесора: {0} = процесор (ядра і спільна частина) + вбудована графіка"), Fmt.W(s.CpuPkgW));
            }
            else
            {
                SetRow(cpuRow, double.NaN, double.NaN, L.T("RAPL unavailable", "RAPL недоступний"));
            }

            gpuRow[0].Text = ShortGpuName(snap.GpuName) ?? GpuText;
            gpuRow[0].ToolTip = snap.GpuName;
            gpuRow[3].ToolTip = s.GpuDisabled ? GpuDisabledText : null;
            if (snap.GpuName == null)
                SetRow(gpuRow, double.NaN, double.NaN, L.T("not found", "не знайдено"));
            else if (s.GpuDisabled)
                SetRow(gpuRow, double.NaN, double.NaN, L.T("disabled — stays powered", "вимкнена — не спить"));
            else if (s.GpuDState == 3)
                SetRow(gpuRow, 0, snap.Gpu10W, L.T("asleep", "спить"));
            else if (!double.IsNaN(s.GpuW))
                SetRow(gpuRow, s.GpuW, snap.Gpu10W, string.Format("P{0} · {1}",
                    s.GpuPState >= 0 ? s.GpuPState.ToString() : "?", s.GpuUtil >= 0 ? s.GpuUtil + " %" : "—"));
            else
                SetRow(gpuRow, double.NaN, double.NaN, snap.GpuPolling ? L.T("active", "працює") : L.T("active, not polled", "працює, не опитується"));

            // С замеренной подсветкой «остальное» делится на подсветку и прочее (панель, плата, SSD, сеть, вентиляторы).
            UpdateScreenRow(snap);
            double screenW = ScreenW();
            bool split = !double.IsNaN(screenW) && calPhase < 0;
            restRow[0].Text = (split ? L.T("Other", "Інше") : L.T("Rest", "Решта")) + (!double.IsNaN(s.GpuW) ? "" : L.T(" + GPU", " + відеокарта"));
            SetRow(restRow, double.NaN, split && !double.IsNaN(snap.RestW) ? Math.Max(0, snap.RestW - screenW) : snap.RestW, "");
            restNote.Text = !b.Discharging
                ? L.T("“Rest” is measured only on battery", "«Решта» рахується лише від батареї")
                : split ? (double.IsNaN(screenModel.PanelW)
                    ? L.T("other: board, memory, SSD, network, fans, black screen = battery − CPU − GPU − screen",
                          "інше: плата, пам'ять, SSD, мережа, вентилятори, чорний екран = батарея − процесор − відеокарта − екран")
                    : L.T("other: board, memory, SSD, network, fans = battery − CPU − GPU − screen",
                          "інше: плата, пам'ять, SSD, мережа, вентилятори = батарея − процесор − відеокарта − екран"))
                        : RestNoteText;

            // Сон ядер и системный таймер: частый таймер не даёт чипу уходить в глубокий сон
            bool fastTimer = s.TimerMs <= 2;
            sleepNote.Text = string.Format(L.T("cores in deepest sleep {0} · system timer {1}", "ядра в найглибшому сні {0} · системний таймер {1}"),
                double.IsNaN(snap.DeepSleep10) ? "—" : snap.DeepSleep10.ToString("0") + " %",
                double.IsNaN(s.TimerMs) ? "—" : s.TimerMs.ToString("0.0") + L.T(" ms", " мс") + (fastTimer ? " ⚠" : ""));
            sleepNote.ToolTip = L.T(
                "Deepest sleep: share of time the cores spend in their deepest idle state (Windows “% C3 Time”) — the higher, the less the CPU uses at idle.\n" +
                "System timer: how often Windows ticks. 15.6 ms is normal; 1 ms or less means an app asked Windows to wake up a thousand times a second, and the chip cannot sleep deeply.\n" +
                "Eco and efficiency mode make Windows ignore such requests from background apps; Subzero also stops the vendor services that hold the timer.",
                "Найглибший сон: частка часу, яку ядра проводять у найглибшому стані простою (лічильник Windows «% C3 Time»), — що більше, то менше процесор витрачає у простої.\n" +
                "Системний таймер: як часто «тікає» Windows. 15,6 мс — норма; 1 мс і менше — програма попросила будити систему тисячу разів на секунду, і чип не може глибоко заснути.\n" +
                "Eco і режим ефективності змушують Windows ігнорувати такі запити від фонових програм; Subzero ще й зупиняє служби виробника, що тримають таймер.");
            if (fastTimer) sleepNote.SetResourceReference(TextBlock.ForegroundProperty, Keys.Warning);
            else sleepNote.SetResourceReference(TextBlock.ForegroundProperty, Keys.Muted);

            // Батарея
            bDesign.Text = Fmt.Wh(info.DesignedCapacity, rel);
            bFull.Text = Fmt.Wh(info.FullChargedCapacity, rel);
            bWear.Text = Fmt.Num(info.WearPercent, "0.0") + " %";
            bCycles.Text = info.CycleCount > 0 ? info.CycleCount.ToString() : L.T("n/a", "н/д");
            bVoltage.Text = b.HasVoltage ? (b.Voltage / 1000.0).ToString("0.000") + L.T(" V", " В") : "—";
            bCurrent.Text = double.IsNaN(b.CurrentA) ? "—" : b.CurrentA.ToString("0.00") + L.T(" A", " А");
            bDisplays.Text = s.Displays < 0 ? "—"
                : s.DisplaysOnGpu > 0 ? string.Format(L.T("{0}, via NVIDIA: {1}", "{0}, через NVIDIA: {1}"), s.Displays, s.DisplaysOnGpu)
                : s.Displays.ToString();
            bDisplays.ToolTip = s.DisplaysOnGpu > 0 ? L.T("A display connected to the NVIDIA GPU keeps it awake (≈ 8 W).", "Екран, підключений до відеокарти NVIDIA, не дає їй заснути (≈ 8 Вт).") : null;

            // Сессия
            sDuration.Text = Fmt.Duration(snap.SessionS);
            bool hasSession = snap.SessionS > 0;
            sByRate.Text = hasSession ? snap.SessionByRateWh.ToString("0.00") + " " + Fmt.WhUnit : "—";
            sByCap.Text = hasSession ? snap.SessionByCapacityWh.ToString("0.00") + " " + Fmt.WhUnit : "—";
            sMismatch.Text = double.IsNaN(snap.SessionMismatchPct) ? "—" : snap.SessionMismatchPct.ToString("+0.0;−0.0;0.0") + " %";
            sAvg.Text = Fmt.W(snap.SessionAvgW);
            sMinMax.Text = hasSession ? string.Format("{0:0.0} / {1:0.0} {2}", snap.SessionMinW, snap.SessionMaxW, Fmt.WUnit) : "—";
            sHint.Text = hasSession ? "" : L.T("Unplug the charger to start tracking the discharge.", "Відключіть зарядний пристрій, щоб почати облік розряду.");
            if (double.IsNaN(snap.LastSleepS))
            {
                sSleep.Text = "—";
                sSleep.ToolTip = null;
            }
            else
            {
                sSleep.Text = string.Format("{0} · {1:0.00} {2}", Fmt.Hours(snap.LastSleepS / 3600), snap.LastSleepWh, Fmt.WhUnit);
                sSleep.ToolTip = string.Format(L.T("Woke at {0:HH:mm}; average draw while asleep {1:0.00} W", "Прокинувся о {0:HH:mm}; середня витрата уві сні {1:0.00} Вт"), snap.LastSleepEnd, snap.LastSleepW);
            }

            string interval = snap.IntervalMs >= 60000 ? (snap.IntervalMs / 60000) + " " + Fmt.MinUnit : (snap.IntervalMs / 1000.0).ToString("0.#") + " " + Fmt.SecUnit;
            footerText.Text = snap.Error ?? (DateTime.UtcNow < footerNoteUntil ? footerNote : L.T("Polling every ", "Опитування раз на ") + interval + " · " +
                (snap.LogPath != null ? L.T("Log: ", "Лог: ") + snap.LogPath : L.T("log off", "лог вимкнено")));

            // На длинных периодах пиксель — это десятки секунд: перерисовываем не чаще раза в (период / 600),
            // то есть каждую секунду на 10 минутах и раз в минуту на 10 часах. Сдвиг и масштаб рисуются сразу.
            double every = chart.IsLive ? Math.Max(1, chart.Window / 600) - 0.2 : double.MaxValue;
            if ((DateTime.UtcNow - lastChartRender).TotalSeconds >= every)
            {
                RenderChart();
                UpdateProcessTable();
            }
        }

        /// <summary>Счётчик всего чипа (PKG) включает встроенную графику; без неё — чтобы она шла отдельным слагаемым.</summary>
        internal static double CpuWithoutGraphics(double pkg, double igpu)
        {
            if (double.IsNaN(pkg)) return double.NaN;
            return double.IsNaN(igpu) ? pkg : Math.Max(0, pkg - igpu);
        }

        void SetRow(TextBlock[] row, double now, double avg, string note)
        {
            row[1].Text = Fmt.W(now);
            row[2].Text = Fmt.W(avg);
            row[3].Text = note;
        }

        static void SetHero(TextBlock tb, string number, string unit)
        {
            tb.Inlines.Clear();
            tb.Inlines.Add(new Run(number));
            if (unit != null && number != "—")
            {
                var r = new Run(" " + unit) { FontSize = 16, FontWeight = FontWeights.Normal };
                r.SetResourceReference(TextElement.ForegroundProperty, Keys.Text2);
                tb.Inlines.Add(r);
            }
        }

        void OpenLogFolder()
        {
            string path = sampler.LogPath;
            string dir = path != null ? System.IO.Path.GetDirectoryName(path)
                                      : System.IO.Path.GetFullPath(System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "logs"));
            if (!Directory.Exists(dir)) return;
            Process.Start("explorer.exe", "\"" + dir + "\"");
        }

        // ---------------- Тема ----------------

        void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
        {
            if (e.Category != UserPreferenceCategory.General || !Theme.FollowSystem) return;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                bool dark = Theme.SystemPrefersDark();
                if (dark == theme.IsDark) return;
                theme = Theme.Create(dark);
                theme.Apply(Application.Current.Resources);
                chart.Theme = theme;
                ApplyTitleBar();
                RenderChart();
                UpdateProcessTable();
                var h = ThemeChanged;
                if (h != null) h();
            }));
        }

        void ApplyTitleBar()
        {
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;
            int dark = theme.IsDark ? 1 : 0;
            DwmSetWindowAttribute(hwnd, 20, ref dark, 4);  // DWMWA_USE_IMMERSIVE_DARK_MODE
            int caption = theme.Page.R | (theme.Page.G << 8) | (theme.Page.B << 16);
            DwmSetWindowAttribute(hwnd, 35, ref caption, 4);  // DWMWA_CAPTION_COLOR (Windows 11)
        }

        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        // ---------------- Вспомогательное ----------------

        static Border Card(UIElement content, string title, UIElement headerRight)
        {
            TextBlock unused;
            return Card(content, title, headerRight, out unused);
        }

        static Border Card(UIElement content, string title, UIElement headerRight, out TextBlock titleBlock)
        {
            titleBlock = null;
            var b = new Border { CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Padding = new Thickness(16, 12, 16, 14) };
            b.SetResourceReference(Border.BackgroundProperty, Keys.Surface);
            b.SetResourceReference(Border.BorderBrushProperty, Keys.Border);
            if (title == null)
            {
                b.Child = content;
                return b;
            }
            var dp = new DockPanel();
            // Фиксированная высота: заголовки карточек с кнопкой и без неё стоят на одной линии.
            var header = new DockPanel { Margin = new Thickness(0, 0, 0, 10), Height = 28 };
            if (headerRight != null)
            {
                DockPanel.SetDock(headerRight, Dock.Right);
                header.Children.Add(headerRight);
            }
            var t = Label(title, 14, FontWeights.SemiBold, Keys.Text);
            t.VerticalAlignment = VerticalAlignment.Center;
            t.TextTrimming = TextTrimming.CharacterEllipsis;
            header.Children.Add(t);
            titleBlock = t;
            DockPanel.SetDock(header, Dock.Top);
            dp.Children.Add(header);
            dp.Children.Add(content);
            b.Child = dp;
            return b;
        }

        internal static TextBlock Label(string text, double size, FontWeight weight, string brushKey)
        {
            var t = new TextBlock { Text = text, FontSize = size, FontWeight = weight };
            t.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
            return t;
        }

        static void Add(Grid g, UIElement e, int row)
        {
            Grid.SetRow(e, row);
            g.Children.Add(e);
        }

        static UIElement At(UIElement e, int row, int col)
        {
            Grid.SetRow(e, row);
            Grid.SetColumn(e, col);
            return e;
        }
    }

    /// <summary>Простая таблица: подписи слева, значения справа, выравнивание цифр по разрядам.</summary>
    sealed class Table
    {
        public readonly Grid Grid = new Grid();
        readonly int columns;
        int rows;

        public Table(string[] headers)
        {
            columns = headers == null ? 2 : headers.Length;
            Grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            // Колонки значений — с запасом под «99,9 Вт» (70), чтобы не дёргались при смене чисел.
            for (int i = 1; i < columns; i++)
                Grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, MinWidth = headers != null && i < columns - 1 ? 70 : 0 });
            if (headers != null)
            {
                NewRow();
                for (int i = 0; i < headers.Length; i++)
                {
                    var h = MainWindow.Label(headers[i], 11, FontWeights.Normal, Keys.Muted);
                    h.HorizontalAlignment = i == 0 || i == columns - 1 ? HorizontalAlignment.Left : HorizontalAlignment.Right;
                    h.Margin = new Thickness(i == 0 ? 0 : 12, 0, 0, 2);
                    Place(h, i);
                }
            }
        }

        void NewRow()
        {
            Grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            rows++;
        }

        void Place(UIElement e, int col)
        {
            Grid.SetRow(e, rows - 1);
            Grid.SetColumn(e, col);
            Grid.Children.Add(e);
        }

        /// <summary>Строка таблицы компонентов: [подпись, сейчас, среднее, примечание].</summary>
        public TextBlock[] Row(string label, string swatchKey)
        {
            NewRow();
            var cells = new TextBlock[columns];
            var name = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 3) };
            var sw = new Border { Width = 10, Height = 10, CornerRadius = new CornerRadius(2), Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            if (swatchKey != null) sw.SetResourceReference(Border.BackgroundProperty, swatchKey);
            name.Children.Add(sw);
            cells[0] = MainWindow.Label(label, 13, FontWeights.Normal, swatchKey != null ? Keys.Text : Keys.Text2);
            cells[0].TextTrimming = TextTrimming.CharacterEllipsis;
            name.Children.Add(cells[0]);
            Place(name, 0);
            for (int i = 1; i < columns; i++)
            {
                bool note = i == columns - 1;
                cells[i] = MainWindow.Label("—", note ? 12 : 13, note ? FontWeights.Normal : FontWeights.SemiBold, note ? Keys.Text2 : Keys.Text);
                cells[i].HorizontalAlignment = note ? HorizontalAlignment.Left : HorizontalAlignment.Right;
                cells[i].VerticalAlignment = VerticalAlignment.Center;
                cells[i].Margin = new Thickness(12, 3, 0, 3);
                Typography.SetNumeralAlignment(cells[i], FontNumeralAlignment.Tabular);
                Place(cells[i], i);
            }
            return cells;
        }

        /// <summary>Пара «подпись — значение».</summary>
        public TextBlock Pair(string label)
        {
            NewRow();
            var l = MainWindow.Label(label, 13, FontWeights.Normal, Keys.Text2);
            l.Margin = new Thickness(0, 3, 0, 3);
            Place(l, 0);
            var v = MainWindow.Label("—", 13, FontWeights.SemiBold, Keys.Text);
            v.HorizontalAlignment = HorizontalAlignment.Right;
            v.Margin = new Thickness(16, 3, 0, 3);
            Typography.SetNumeralAlignment(v, FontNumeralAlignment.Tabular);
            Place(v, 1);
            return v;
        }
    }

    /// <summary>Плоская кнопка в стиле карточек — стандартная Button WPF не следует тёмной теме.</summary>
    sealed class FlatButton : Border
    {
        readonly TextBlock label;
        public event Action Click;

        public FlatButton(string text)
        {
            CornerRadius = new CornerRadius(6);
            BorderThickness = new Thickness(1);
            Padding = new Thickness(12, 4, 12, 5);
            Cursor = Cursors.Hand;
            Focusable = true;
            VerticalAlignment = VerticalAlignment.Center;
            SetResourceReference(BorderBrushProperty, Keys.Border);
            SetResourceReference(BackgroundProperty, Keys.Surface);
            label = MainWindow.Label(text, 12, FontWeights.Normal, Keys.Text);
            Child = label;

            MouseEnter += (s, e) => SetResourceReference(BackgroundProperty, Keys.Hover);
            MouseLeave += (s, e) => SetResourceReference(BackgroundProperty, Keys.Surface);
            MouseLeftButtonUp += (s, e) => Fire();
            KeyDown += (s, e) => { if (e.Key == Key.Enter || e.Key == Key.Space) Fire(); };
        }

        public string Text { set { label.Text = value; } }

        /// <summary>Выделить рамку и текст цветом темы (ключ Keys.*); null — обычный вид.</summary>
        public void SetAccent(string key)
        {
            SetResourceReference(BorderBrushProperty, key ?? Keys.Border);
            label.SetResourceReference(TextBlock.ForegroundProperty, key ?? Keys.Text);
            label.FontWeight = key != null ? FontWeights.SemiBold : FontWeights.Normal;
        }

        void Fire()
        {
            var h = Click;
            if (h != null) h();
        }
    }
}
