using System;
using System.Threading;
using System.Windows;

// Без этого атрибута .NET включает режим совместимости с 4.0 и отключает масштабирование под DPI каждого монитора.
[assembly: System.Runtime.Versioning.TargetFramework(".NETFramework,Version=v4.8", FrameworkDisplayName = ".NET Framework 4.8")]

namespace BatteryCheck
{
    static class GuiProgram
    {
        const string MutexName = @"Local\BatteryCheck.Gui";
        const string ShowEventName = @"Local\BatteryCheck.Gui.Show";
        const string ExitEventName = @"Local\BatteryCheck.Gui.Exit";

        /// <summary>
        /// Окно рисуется на встроенной графике: иначе WPF выбирает дискретную карту, и каждое изменение окна (показать,
        /// свернуть, перерисовать) будит её — замер 05.10: +16 Вт на 20–60 с. Настройка Windows «Энергосбережение» для своего exe
        /// (Параметры → Дисплей → Графика), только если её ещё нет: выбор пользователя не трогаем. Действует со следующего запуска.
        /// </summary>
        static void PreferIntegratedGpu()
        {
            try
            {
                var store = new RegistryGpuPreferenceStore();
                string exe = System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName;
                string cur = store.Get(exe);
                if (GpuPreferenceManager.Field(cur, "GpuPreference") == null)
                    store.Set(exe, GpuPreferenceManager.WithField(cur, "GpuPreference", GpuPreferenceManager.PowerSaving));
            }
            catch (Exception) { }  // не записалось — окно просто рисуется там, где решит Windows
        }

        [STAThread]
        static int Main(string[] args)
        {
            L.Apply(L.LoadSaved());  // английский по умолчанию; переключатель EN|UA в окне
            PreferIntegratedGpu();

            var o = new SamplerOptions();
            bool dark = Theme.SystemPrefersDark();
            bool background = false, exit = false;
            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i].ToLowerInvariant())
                {
                    case "--no-gpu": o.Gpu = false; break;
                    case "--no-log": o.Log = false; break;
                    case "--background": background = true; break;
                    case "--exit": exit = true; break;
                    case "--software-render":  // отрисовка без Direct3D: проверка, не будит ли видеокарту смена состояния окна
                        System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
                        break;
                    case "--theme":
                        if (i + 1 < args.Length)
                        {
                            dark = args[++i].ToLowerInvariant() == "dark";
                            Theme.FollowSystem = false;
                        }
                        break;
                }
            }

            // Один экземпляр: повторный запуск открывает окно уже работающей программы
            // (иначе два процесса писали бы два лога и показывали два значка).
            bool first;
            using (var mutex = new Mutex(true, MutexName, out first))
            using (var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName))
            using (var exitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ExitEventName))
            {
                if (!first)
                {
                    if (exit) exitEvent.Set();             // --exit: закрыть работающую копию
                    else if (!background) showEvent.Set();
                    return 0;
                }
                if (exit) return 0;  // закрывать нечего

                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                var theme = Theme.Create(dark);
                theme.Apply(app.Resources);
                ScrollStyle.Install(app.Resources);  // полосы прокрутки в стиле окна (цвета — из темы)

                Sampler sampler;
                try
                {
                    sampler = new Sampler(o);
                }
                catch (Exception e)
                {
                    MessageBox.Show(e.Message, "Battery Check", MessageBoxButton.OK, MessageBoxImage.Error);
                    return 1;
                }

                var controller = new Controller(app, sampler, theme);
                ThreadPool.RegisterWaitForSingleObject(showEvent,
                    (state, timedOut) => app.Dispatcher.BeginInvoke(new Action(controller.ShowWindow)), null, -1, false);
                ThreadPool.RegisterWaitForSingleObject(exitEvent,
                    (state, timedOut) => app.Dispatcher.BeginInvoke(new Action(controller.Exit)), null, -1, true);
                controller.Start(!background);
                return app.Run();
            }
        }
    }
}
