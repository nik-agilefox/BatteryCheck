using System;
using System.ComponentModel;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;

namespace BatteryCheck
{
    /// <summary>
    /// Связывает опрос, окно и значок в трее. Закрытие окна прячет его в трей; программа работает,
    /// пока не выбран «Выход» в меню значка. Частота опроса зависит от того, видно ли окно и откуда питание.
    /// </summary>
    sealed class Controller
    {
        // Интервалы опроса, мс
        const int OnScreenMs = 1000;           // окно на экране: живой график
        const int BackgroundBatteryMs = 10000; // в фоне от батареи: точности хватает, а пробуждений в 10 раз меньше
        // От сети — тоже раз в 10 с: на значке в трее полная мощность от блока, и она должна быть свежей.
        // Замер стоит около миллисекунды процессора; смену источника питания всё равно ловим по событию.
        const int BackgroundChargingMs = 10000;
        const int BackgroundAcMs = 10000;
        const int WakeWatchMs = 2000;          // в фоне от батареи, пока NVIDIA не спит

        readonly Application app;
        readonly Sampler sampler;
        readonly MainWindow window;
        readonly TrayIcon tray;
        readonly ManualResetEvent stop = new ManualResetEvent(false);
        readonly AutoResetEvent poke = new AutoResetEvent(false);
        readonly Dispatcher dispatcher;
        Thread worker;
        volatile bool onScreen;
        bool exiting;

        public Controller(Application app, Sampler sampler, Theme theme)
        {
            this.app = app;
            this.sampler = sampler;
            dispatcher = Dispatcher.CurrentDispatcher;

            window = new MainWindow(sampler, theme);
            window.Closing += OnWindowClosing;
            window.IsVisibleChanged += (s, e) => OnScreenChanged();
            window.StateChanged += (s, e) => OnScreenChanged();
            window.ThemeChanged += () => tray.Update(lastSnapshot ?? EmptySnapshot(), window.CurrentTheme, window.LastTotalW);
            window.LanguageChanged += () =>
            {
                tray.Relabel();
                tray.Update(lastSnapshot ?? EmptySnapshot(), window.CurrentTheme, window.LastTotalW);
            };

            tray = new TrayIcon();
            tray.GetTheme = () => window.CurrentTheme;
            tray.GetGpuPolling = () => sampler.GpuPolling;
            tray.OpenRequested += ShowWindow;
            tray.ResetRequested += () => { sampler.ResetSession(); poke.Set(); };
            tray.GpuPollingChanged += on => { sampler.GpuPolling = on; window.SyncGpuToggle(); };
            tray.ExitRequested += Exit;
            tray.GetNotifications = () => window.NotificationsEnabled;
            tray.NotificationsChanged += on => window.NotificationsEnabled = on;
            window.Notify += n => tray.ShowHint(n.Title, n.Text);

            SystemEvents.PowerModeChanged += OnPowerModeChanged;
        }

        Snapshot lastSnapshot;

        public void Start(bool showWindow)
        {
            if (showWindow) window.Show();
            onScreen = window.IsOnScreen;  // без внеочередного замера: первый и так будет сразу
            window.SyncProcessCollection();
            worker = new Thread(WorkerLoop) { IsBackground = true, Name = "Sampler" };
            worker.Start();
        }

        void WorkerLoop()
        {
            try
            {
                sampler.Run(IntervalFor, 0, stop, poke, snap => dispatcher.BeginInvoke(new Action(() => OnSnapshot(snap))));
            }
            catch (Exception e)
            {
                dispatcher.BeginInvoke(new Action(() => window.ShowError(L.T("Polling error: ", "Помилка опитування: ") + e.Message)));
            }
        }

        /// <summary>Вызывается из потока опроса после каждого замера: пауза до следующего.</summary>
        int IntervalFor(Snapshot s)
        {
            if (onScreen) return OnScreenMs;
            var b = s.Sample.Battery;
            // Пока NVIDIA не спит, опрос чаще: иначе короткие пробуждения (10–20 с) проскакивают мимо журнала.
            if (b.Discharging && s.Sample.GpuDState == 0) return WakeWatchMs;
            if (b.Discharging) return BackgroundBatteryMs;
            if (b.Charging) return BackgroundChargingMs;
            return BackgroundAcMs;
        }

        void OnSnapshot(Snapshot snap)
        {
            if (exiting) return;
            lastSnapshot = snap;
            window.ShowSnapshot(snap);
            tray.Update(snap, window.CurrentTheme, window.LastTotalW);
        }

        void OnScreenChanged()
        {
            bool now = window.IsOnScreen;
            window.SyncProcessCollection();  // оценка по процессам — только пока её видно
            if (now == onScreen) return;
            onScreen = now;
            if (now) poke.Set();  // окно открыли — сразу свежий замер и опрос раз в секунду
        }

        void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
        {
            // Смена источника питания или пробуждение — замер сразу, не дожидаясь паузы (в фоне она до 5 минут).
            if (e.Mode == PowerModes.StatusChange || e.Mode == PowerModes.Resume) poke.Set();
        }

        public void ShowWindow()
        {
            if (exiting) return;
            window.Show();
            if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
            window.Activate();
            OnScreenChanged();
        }

        void OnWindowClosing(object sender, CancelEventArgs e)
        {
            if (exiting) return;
            e.Cancel = true;
            window.AbortScreenCalibration(L.T("window hidden", "вікно приховано"));
            window.Hide();
            if (!HintShown)
            {
                tray.ShowHint(L.T("Battery Check keeps running in the background", "Battery Check працює у фоні"),
                    L.T("Polling continues less often and the log is still written. Click the icon to open the window; Exit is in its menu.",
                        "Опитування триває рідше, лог пишеться. Клік по значку відкриває вікно, вихід — у його меню."));
                HintShown = true;
            }
        }

        public void Exit()
        {
            if (exiting) return;
            exiting = true;
            window.AbortScreenCalibration(L.T("app exit", "вихід з програми"));  // вернуть яркость до выхода
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            stop.Set();
            if (worker != null) worker.Join(3000);
            sampler.Dispose();
            tray.Dispose();
            window.Close();
            app.Shutdown();
        }

        Snapshot EmptySnapshot()
        {
            return new Snapshot { Sample = new Sample(), Info = new BatteryInfo() };
        }

        /// <summary>Подсказку о работе в фоне показываем один раз.</summary>
        static bool HintShown
        {
            get
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\BatteryCheck"))
                    return k != null && k.GetValue("TrayHintShown") != null;
            }
            set
            {
                using (var k = Registry.CurrentUser.CreateSubKey(@"Software\BatteryCheck"))
                    k.SetValue("TrayHintShown", value ? 1 : 0);
            }
        }
    }
}
