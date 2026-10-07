using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows;

namespace BatteryCheck
{
    /// <summary>
    /// Переключатель режимов в заголовке: «⚡ Стандарт | 🍃 Eco | ❄ Subzero».
    /// Eco (см. MaxEconomy, BackgroundEfficiency, GpuPreferenceManager) — значения схемы питания для батареи, режим эффективности
    /// фоновым программам, программы с дискретной карты — на встроенную. Subzero — Eco и остановка служб из rules.json
    /// (subzero_services: службы Acer, которые держат системный таймер на 1 мс; замер 05.10 — до 9 Вт в простое).
    /// Остановка и запуск служб — по окну UAC на действие (ServiceSwitch); после перезагрузки службы стартуют сами,
    /// поэтому при запуске программы Subzero, чьи службы снова работают, становится Eco.
    /// </summary>
    sealed partial class MainWindow
    {
        const int ModeStandard = 0, ModeEco = 1, ModeSubzero = 2;

        Segmented economyButton;
        Dictionary<string, uint> economySaved = MaxEconomy.Parse(Settings.GetString("MaxEconomySaved", ""));
        bool economyOn = Settings.GetDouble("MaxEconomy", 0) != 0;
        bool subzeroOn = Settings.GetDouble("Subzero", 0) != 0;
        List<string> subzeroStopped = new List<string>(Settings.GetString("SubzeroStopped", "").Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries));
        bool modeBusy;  // ждём ответа UAC — другие нажатия не принимаем
        int pendingMode = -1;  // куда переключаемся, пока ждём UAC: переключатель показывает его сразу
        BackgroundEfficiency bgEfficiency;
        GpuPreferenceManager gpuPrefs;
        DateTime gpuPrefsAt = DateTime.MinValue;

        int CurrentMode { get { return subzeroOn ? ModeSubzero : economyOn ? ModeEco : ModeStandard; } }

        /// <summary>Раз в 10 с, пока экономия от батареи: программам на дискретной карте — встроенная графика (со следующего запуска).</summary>
        void UpdateGpuPreferences(bool active)
        {
            if (gpuPrefs == null)
                gpuPrefs = new GpuPreferenceManager(new RegistryGpuPreferenceStore(), RegistryGpuPreferenceStore.ExeOf,
                                                    GpuPreferenceManager.Parse(Settings.GetString("GpuPrefSaved", "")));
            Action<string, string, string> log = (app, from, to) => ProfileHost.Log("gpu_pref", app + ": " + from, to, ProfileLogic.ByUser);
            if (!active)
            {
                if (gpuPrefs.Saved.Count == 0) return;
                gpuPrefs.RestoreAll(log);
                Settings.SetString("GpuPrefSaved", "");
                return;
            }
            if ((DateTime.UtcNow - gpuPrefsAt).TotalSeconds < 10) return;
            gpuPrefsAt = DateTime.UtcNow;
            var moved = gpuPrefs.Update(lastSnap != null ? lastSnap.Processes : null, log);
            if (moved.Count == 0) return;
            Settings.SetString("GpuPrefSaved", GpuPreferenceManager.Serialize(gpuPrefs.Saved));
            string apps = string.Join(", ", moved);
            var n = new Notice
            {
                Kind = "gpu_pref",
                Title = L.T("Restart to move off the discrete GPU", "Перезапустіть, щоб піти з дискретної відеокарти"),
                Text = string.Format(L.T("{0}: switched to the integrated graphics (Power saving). It takes effect after the app is restarted — then the discrete GPU can sleep. Restored when you plug in or switch to Standard.",
                                         "{0}: переведено на вбудовану графіку («Енергозбереження»). Діє після перезапуску програми — тоді дискретна відеокарта зможе спати. Повернеться при підключенні зарядки або перемиканні на «Стандарт»."), apps),
            };
            ShowNote(n.Title + ": " + apps);
            var h = Notify;
            if (h != null && notificationsEnabled) h(n);
        }

        System.Windows.Threading.DispatcherTimer economyTimer;

        /// <summary>Раз в секунду: режим эффективности фоновым программам, пока Eco/Subzero и ноутбук от батареи.</summary>
        void StartEconomyTimer()
        {
            CheckSubzeroAfterRestart();
            SetSubzeroPower(subzeroOn);  // потолок процессора — по режиму и после перезапуска программы
            economyTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            economyTimer.Tick += (s, e) =>
            {
                bool active = economyOn && Sampler.SavingApplies(lastSnap);  // ноутбук от батареи или ПК без неё
                UpdateGpuPreferences(active && HasBattery);  // на ПК монитор обычно на видеокарте: перевод программ на встроенную только мешает
                if (!active && (bgEfficiency == null || !bgEfficiency.Active)) return;
                if (bgEfficiency == null)
                {
                    var rules = Rules.Load(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(LogDirectory)));
                    bgEfficiency = new BackgroundEfficiency(new SystemProcessSource(), rules.EfficiencyExclude, System.Diagnostics.Process.GetCurrentProcess().Id);
                }
                int before = bgEfficiency.Count;
                if (bgEfficiency.Tick(active, DateTime.Now))
                    ProfileHost.Log("economy:background", active ? L.T("off", "вимк.") : string.Format(L.T("on ({0} proc.)", "увімк. ({0} проц.)"), before),
                        active ? string.Format(L.T("on ({0} proc.)", "увімк. ({0} проц.)"), bgEfficiency.Count) : L.T("off", "вимк."), ProfileLogic.ByUser);
                UpdateEconomyButton();
            };
            economyTimer.Start();
            Closed += (s, e) => { if (bgEfficiency != null) bgEfficiency.RestoreAll(); };  // выход — фоновым программам вернуть обычный режим
        }

        /// <summary>После перезагрузки службы снова работают: Subzero без них — это Eco. Окно UAC без действия пользователя не показываем.</summary>
        void CheckSubzeroAfterRestart()
        {
            if (!subzeroOn) return;
            var running = ServiceSwitch.Running(subzeroStopped);
            if (subzeroStopped.Count > 0 && running.Count == 0) return;  // всё ещё остановлены — Subzero действует
            subzeroOn = false;
            subzeroStopped.Clear();
            SaveSubzero();
            if (running.Count > 0)
                ShowNote(L.T("The services stopped by Subzero are running again (after a restart) — the mode is Eco now. Select Subzero to stop them again.",
                             "Служби, зупинені Subzero, знову працюють (після перезапуску) — режим тепер Eco. Виберіть Subzero, щоб зупинити їх знову."));
        }

        void SaveSubzero()
        {
            Settings.SetDouble("Subzero", subzeroOn ? 1 : 0);
            Settings.SetString("SubzeroStopped", string.Join("|", subzeroStopped));
            SetSubzeroPower(subzeroOn);  // потолок частоты процессора — вместе с режимом
        }

        /// <summary>Потолок частоты процессора от батареи (MaxEconomy.SubzeroSettings): включить или вернуть прежние значения.</summary>
        void SetSubzeroPower(bool on)
        {
            string savedText = Settings.GetString("SubzeroPowerSaved", "");
            bool applied = Settings.GetDouble("SubzeroPowerOn", 0) != 0;
            if (on == applied) return;
            try
            {
                var scheme = new ActivePowerScheme(!HasBattery);  // ПК без батареи — значения «от сети»
                Action<string, string, string> log = (id, from, to) => ProfileHost.Log("economy:" + id, from, to, ProfileLogic.ByUser);
                if (on) Settings.SetString("SubzeroPowerSaved", MaxEconomy.Serialize(MaxEconomy.Apply(scheme, log, MaxEconomy.SubzeroSettings)));
                else { MaxEconomy.Revert(scheme, MaxEconomy.Parse(savedText), log, MaxEconomy.SubzeroSettings); Settings.SetString("SubzeroPowerSaved", ""); }
                Settings.SetDouble("SubzeroPowerOn", on ? 1 : 0);
            }
            catch (Exception e)
            {
                ShowNote(L.T("Could not change the CPU limit: ", "Не вдалося змінити обмеження процесора: ") + e.Message);
            }
        }

        /// <summary>Переключатель «⚡ Стандарт | 🍃 Eco | ❄ Subzero»: Eco — зелёный, Subzero — голубой.</summary>
        Segmented BuildEconomyButton()
        {
            economyButton = new Segmented(new[] { L.T("⚡ Standard", "⚡ Стандарт"), "🍃 Eco", "❄ Subzero" });
            economyButton.Accents[ModeEco] = Keys.Good;
            economyButton.Accents[ModeSubzero] = Keys.Battery;
            economyButton.Changed += SetMode;
            UpdateEconomyButton();
            return economyButton;
        }

        void SetMode(int target)
        {
            if (modeBusy || target == CurrentMode) { UpdateEconomyButton(); return; }
            // Схема питания: Eco и Subzero — экономичные значения, Стандарт — прежние.
            if (target >= ModeEco && !economyOn) SetEconomy(true);
            if (target == ModeStandard && economyOn && !subzeroOn) SetEconomy(false);

            if (target == ModeSubzero && !subzeroOn) SwitchServices(true, ModeSubzero);
            else if (target < ModeSubzero && subzeroOn) SwitchServices(false, target);
            UpdateEconomyButton();
        }

        /// <summary>Остановить (Subzero) или запустить службы — окно UAC, ожидание — в фоне; затем режим target.</summary>
        void SwitchServices(bool stop, int target)
        {
            var rules = Rules.Load(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(LogDirectory)));
            var names = stop ? ServiceSwitch.Running(rules.SubzeroServices) : new List<string>(subzeroStopped);
            if (stop && names.Count == 0)
            {
                // Уже остановлены (например, вручную) — принять их: при выходе из Subzero запустятся обратно.
                var already = ServiceSwitch.StoppedAutomatic(rules.SubzeroServices);
                if (already.Count > 0)
                {
                    subzeroOn = true;
                    subzeroStopped = already;
                    SaveSubzero();
                    ShowNote(string.Format(L.T("Subzero is on: {0} were already stopped; they start again when you leave Subzero.",
                                               "Subzero увімкнено: {0} вже були зупинені; вони запустяться знову, коли ви вийдете з Subzero."), string.Join(", ", already)));
                    UpdateEconomyButton();
                    return;
                }
            }
            if (names.Count == 0)
            {
                subzeroOn = stop;
                subzeroStopped.Clear();
                SaveSubzero();
                if (!stop && target == ModeStandard && economyOn) SetEconomy(false);
                if (stop) ShowNote(L.T("Subzero: there are no running services from the list on this laptop — it works as Eco.",
                                       "Subzero: на цьому ноутбуці немає запущених служб зі списку — режим працює як Eco."));
                UpdateEconomyButton();
                return;
            }
            modeBusy = true;
            pendingMode = target;
            UpdateEconomyButton();  // показать новое положение до окна UAC: после него WPF может не перерисоваться сам
            ShowNote(stop ? L.T("Subzero: confirm stopping the services in the Windows prompt…", "Subzero: підтвердьте зупинку служб у вікні Windows…")
                          : L.T("Confirm starting the services again in the Windows prompt…", "Підтвердьте запуск служб у вікні Windows…"));
            ThreadPool.QueueUserWorkItem(_ =>
            {
                string error;
                var done = ServiceSwitch.Run(stop, names, out error);
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    modeBusy = false;
                    pendingMode = -1;
                    foreach (var n in done)
                        ProfileHost.Log("subzero", n, stop ? L.T("stopped", "зупинено") : L.T("started", "запущено"), ProfileLogic.ByUser);
                    if (stop)
                    {
                        subzeroStopped = done;
                        subzeroOn = done.Count > 0;
                        ShowNote(done.Count > 0
                            ? string.Format(L.T("Subzero is on: stopped {0}. They start again when you leave Subzero or after a reboot.",
                                                "Subzero увімкнено: зупинено {0}. Вони запустяться знову, коли ви вийдете з Subzero, або після перезавантаження."), string.Join(", ", done))
                            : L.T("Subzero was not turned on: ", "Subzero не ввімкнено: ") + (error == "cancelled" ? L.T("the Windows prompt was declined.", "у вікні Windows відмовлено.") : error));
                    }
                    else if (done.Count > 0 || error == null)
                    {
                        subzeroOn = false;
                        subzeroStopped.Clear();
                        if (target == ModeStandard && economyOn) SetEconomy(false);
                        ShowNote(L.T("Services are running again.", "Служби знову працюють."));
                    }
                    else
                    {
                        ShowNote(L.T("The services were not started: ", "Служби не запущено: ") + (error == "cancelled" ? L.T("the Windows prompt was declined — Subzero stays on.", "у вікні Windows відмовлено — Subzero лишається.") : error));
                    }
                    SaveSubzero();
                    UpdateEconomyButton();
                    RefreshProfileUi();
                    ForceRepaint();
                }));
            });
        }

        /// <summary>
        /// После окна UAC (защищённый рабочий стол) WPF может не перерисовать окно, пока его не тронуть: перерисовать
        /// сразу и ещё дважды с задержкой, заново выставив переключатель.
        /// </summary>
        /// <summary>Заголовок окна (виден на панели задач): «12❄ Battery Check» — мощность и значок режима.</summary>
        void UpdateTitle()
        {
            int mode = modeBusy && pendingMode >= 0 ? pendingMode : CurrentMode;
            string icon = mode == ModeSubzero ? "❄" : mode == ModeEco ? "🍃" : "⚡";
            string watts = double.IsNaN(LastTotalW) ? "" : Math.Min(999, Math.Round(LastTotalW)).ToString("0");
            string title = watts + icon + " Battery Check";
            if (Title != title) Title = title;
        }

        void ForceRepaint()
        {
            InvalidateVisual();
            UpdateLayout();
            foreach (int ms in new[] { 500, 1500 })
            {
                var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
                timer.Tick += (s, e) =>
                {
                    timer.Stop();
                    UpdateEconomyButton();
                    economyButton.InvalidateVisual();
                    InvalidateVisual();
                };
                timer.Start();
            }
        }

        /// <summary>Значения схемы питания для батареи: экономичные (on) или прежние.</summary>
        void SetEconomy(bool on)
        {
            try
            {
                var scheme = new ActivePowerScheme(!HasBattery);  // ПК без батареи — значения «от сети»
                Action<string, string, string> log = (id, from, to) => ProfileHost.Log("economy:" + id, from, to, ProfileLogic.ByUser);
                if (on)
                {
                    economySaved = MaxEconomy.Apply(scheme, log);
                    economyOn = true;
                    ShowNote(L.T("Eco is on: on battery the CPU no longer boosts, cores prefer efficiency, Energy saver is always on.",
                                 "Eco увімкнено: від батареї процесор більше не прискорюється, ядра віддають перевагу ефективності, економія енергії — завжди."));
                }
                else
                {
                    MaxEconomy.Revert(scheme, economySaved, log);
                    economySaved.Clear();
                    economyOn = false;
                    ShowNote(L.T("Standard: the previous power settings are back.", "Стандарт: попередні параметри живлення повернуто."));
                }
                Settings.SetDouble("MaxEconomy", economyOn ? 1 : 0);
                Settings.SetString("MaxEconomySaved", MaxEconomy.Serialize(economySaved));
            }
            catch (Exception e)
            {
                ShowNote(L.T("Could not change the power settings: ", "Не вдалося змінити параметри живлення: ") + e.Message);
            }
            RefreshProfileUi();  // журнал изменений под карточкой профиля
        }

        void UpdateEconomyButton()
        {
            UpdateTitle();
            if (economyButton == null) return;
            economyButton.Select(modeBusy && pendingMode >= 0 ? pendingMode : CurrentMode);  // и после ошибки или отказа в UAC — настоящее состояние
            string bg = bgEfficiency != null && bgEfficiency.Active
                ? string.Format(L.T("\nNow: {0} background processes in efficiency mode.", "\nЗараз: {0} фонових процесів у режимі ефективності."), bgEfficiency.Count) : "";
            string sz = subzeroOn && subzeroStopped.Count > 0
                ? string.Format(L.T("\nStopped by Subzero: {0}.", "\nЗупинено Subzero: {0}."), string.Join(", ", subzeroStopped)) : "";
            economyButton.ToolTip = L.T(
                "⚡ Standard — nothing is changed.\n" +
                "🍃 Eco — on battery: CPU boost off (no frequency spikes — the main source of heat), energy preference 100 on P- and E-cores, Windows Energy saver at any charge; " +
                "background apps in efficiency mode (except the active window with its child processes, the Windows shell, players and call apps); " +
                "apps found on the discrete GPU switch to the integrated graphics from their next start. Plugged in — nothing changes.\n" +
                "❄ Subzero — Eco plus a hard CPU cap on battery (max processor state 60 %) and stopping the vendor services that keep the system timer at 1 ms (Acer Quick Access, Care Center, Device Info). " +
                "Needs administrator rights: one Windows prompt to stop them and one to start them again when you leave Subzero; a reboot starts them anyway.\n" +
                "Every change goes to logs\\actions.csv.",
                "⚡ Стандарт — нічого не змінюється.\n" +
                "🍃 Eco — від батареї: прискорення процесора вимкнено (немає стрибків частоти — головного джерела нагріву), пріоритет енергії 100 для P- і E-ядер, економія енергії Windows за будь-якого заряду; " +
                "фонові програми в режимі ефективності (крім активного вікна з дочірніми процесами, оболонки Windows, плеєрів і програм для дзвінків); " +
                "програми з дискретної відеокарти переходять на вбудовану графіку з наступного запуску. Від мережі нічого не змінюється.\n" +
                "❄ Subzero — Eco плюс жорстка межа процесора від батареї (макс. стан 60 %) і зупинка служб виробника, що тримають системний таймер на 1 мс (Acer Quick Access, Care Center, Device Info). " +
                "Потрібні права адміністратора: одне вікно Windows, щоб зупинити, і одне — щоб запустити знову, коли ви вийдете з Subzero; після перезавантаження вони запускаються самі.\n" +
                "Кожна зміна — в logs\\actions.csv.") + bg + sz;
        }
    }
}
