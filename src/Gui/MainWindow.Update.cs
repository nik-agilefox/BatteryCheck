using System;
using System.Threading;
using System.Windows.Threading;

namespace BatteryCheck
{
    /// <summary>
    /// Кнопка обновления в нижней строке: «Check updates» → «Update to X» (если на GitHub вышел релиз новее) → загрузка →
    /// перезапуск. Проверка — сама при запуске и раз в 6 часов, и по нажатию. Из папки с исходниками не обновляется:
    /// релиз затёр бы свежую сборку.
    /// </summary>
    sealed partial class MainWindow
    {
        static readonly TimeSpan UpdateCheckEvery = TimeSpan.FromHours(6);

        enum UpdateState { Idle, Checking, UpToDate, Available, Installing, Failed }

        FlatButton updateButton;
        UpdateState updateState = UpdateState.Idle;
        ReleaseInfo updateRelease;
        string updateError;
        int updateProgress = -1;
        DispatcherTimer updateTimer;

        /// <summary>Новая версия установлена: перезапустить программу (Controller запускает новую копию и выходит).</summary>
        public event Action RestartForUpdate;

        FlatButton BuildUpdateButton()
        {
            updateButton = new FlatButton("");
            updateButton.Margin = new System.Windows.Thickness(0, 0, 8, 0);
            updateButton.Click += () =>
            {
                if (updateState == UpdateState.Available) InstallUpdate();
                else if (updateState != UpdateState.Checking && updateState != UpdateState.Installing) CheckForUpdate(true);
            };
            UpdateUpdateButton();
            if (updateTimer == null)
            {
                updateTimer = new DispatcherTimer { Interval = UpdateCheckEvery };
                updateTimer.Tick += (s, e) => CheckForUpdate(false);
                updateTimer.Start();
                Dispatcher.BeginInvoke(new Action(() => CheckForUpdate(false)), DispatcherPriority.ApplicationIdle);
            }
            return updateButton;
        }

        /// <summary>manual — по нажатию: показать и «последняя версия», и ошибку; фоновая проверка молчит о них.</summary>
        void CheckForUpdate(bool manual)
        {
            if (updateState == UpdateState.Checking || updateState == UpdateState.Installing || updateState == UpdateState.Available) return;
            updateState = UpdateState.Checking;
            UpdateUpdateButton();
            ThreadPool.QueueUserWorkItem(_ =>
            {
                string error;
                var r = Updater.Check(out error);
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    updateError = error;
                    if (r != null && ReleaseInfo.IsNewer(r.Version, AppInfo.Version))
                    {
                        updateRelease = r;
                        updateState = UpdateState.Available;
                    }
                    else if (!manual) updateState = UpdateState.Idle;
                    else
                    {
                        updateState = error != null ? UpdateState.Failed : UpdateState.UpToDate;
                        // «Последняя версия» / ошибка — на 10 с, потом снова «Check updates».
                        var back = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
                        back.Tick += (s, e) =>
                        {
                            back.Stop();
                            if (updateState == UpdateState.UpToDate || updateState == UpdateState.Failed) updateState = UpdateState.Idle;
                            UpdateUpdateButton();
                        };
                        back.Start();
                    }
                    UpdateUpdateButton();
                }));
            });
        }

        void InstallUpdate()
        {
            if (Updater.IsDevBuild)
            {
                ShowNote(L.T("This is a development build (next to the sources): update it with git and build.cmd, not from a release.",
                             "Це збірка розробника (поруч із вихідним кодом): оновлюйте її через git і build.cmd, а не з релізу."));
                return;
            }
            var r = updateRelease;
            updateState = UpdateState.Installing;
            updateProgress = 0;
            UpdateUpdateButton();
            ThreadPool.QueueUserWorkItem(_ =>
            {
                string error;
                bool ok = Updater.Install(r, pct => Dispatcher.BeginInvoke(new Action(() => { updateProgress = pct; UpdateUpdateButton(); })), out error);
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (ok)
                    {
                        var h = RestartForUpdate;
                        if (h != null) h();
                        return;
                    }
                    updateError = error;
                    updateState = UpdateState.Available;  // можно попробовать ещё раз
                    UpdateUpdateButton();
                    ShowNote(string.Format(L.T("Update to {0} failed: {1}. The installed version was not changed.", "Оновлення до {0} не вдалося: {1}. Встановлену версію не змінено."), r.Version, error));
                }));
            });
        }

        void UpdateUpdateButton()
        {
            if (updateButton == null) return;
            string text, tip = string.Format(L.T("Installed version {0}. Updates come from GitHub releases ({1}); logs and settings are kept.",
                                                 "Встановлена версія {0}. Оновлення — з релізів GitHub ({1}); логи й налаштування зберігаються."), AppInfo.Version, AppInfo.Repo);
            string accent = null;
            switch (updateState)
            {
                case UpdateState.Checking: text = L.T("Checking…", "Перевіряю…"); break;
                case UpdateState.UpToDate: text = L.T("Up to date ✓", "Остання версія ✓"); break;
                case UpdateState.Failed:
                    text = L.T("No connection", "Немає зв'язку");
                    tip += "\n" + updateError;
                    break;
                case UpdateState.Available:
                    text = string.Format(L.T("Update to {0}", "Оновити до {0}"), updateRelease.Version);
                    accent = Keys.Battery;
                    if (!string.IsNullOrWhiteSpace(updateRelease.Notes)) tip += "\n\n" + updateRelease.Notes.Trim();
                    break;
                case UpdateState.Installing:
                    text = updateProgress >= 0 && updateProgress < 100 ? string.Format(L.T("Downloading {0} %", "Завантаження {0} %"), updateProgress) : L.T("Installing…", "Встановлення…");
                    accent = Keys.Battery;
                    break;
                default: text = L.T("Check updates", "Перевірити оновлення"); break;
            }
            updateButton.Text = text;
            updateButton.ToolTip = tip;
            updateButton.SetAccent(accent);
        }
    }
}
