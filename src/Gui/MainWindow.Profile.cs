using System;
using System.Windows;
using System.Windows.Controls;

namespace BatteryCheck
{
    /// <summary>
    /// Профиль «На батарее» (см. ProfileLogic): через 5 с после отключения зарядки — яркость и частота экрана,
    /// при подключении — откат того, что пользователь не менял сам. Режим питания Windows для батареи — постоянная
    /// настройка, меняется сразу. Карточка — во вкладке «Советы», журнал — logs\actions.csv.
    /// </summary>
    sealed partial class MainWindow
    {
        const double ProfileDelaySeconds = 5;  // дребезг разъёма зарядки — не повод менять яркость
        static readonly int[] ProfileBrightnessValues = { 0, 30, 40, 50, 60, 70 };

        readonly ProfileSettings profile = ProfileStore.LoadSettings();
        readonly ProfileState profileState = ProfileStore.LoadState();
        SystemProfileHost profileHost;
        DateTime? onBatterySince;
        DateTime effRefreshAt = DateTime.MinValue;
        const double EffRefreshSeconds = 30;
        WrapPanel effApps;

        TextBlock profileStatus;
        FlatButton profileRevert;
        StackPanel profileJournal;

        IProfileHost ProfileHost
        {
            get
            {
                return profileHost ?? (profileHost = new SystemProfileHost(() => LogDirectory, () => sampler.WakeHint(GpuWake.HintBrightness)));
            }
        }

        /// <summary>Каждый замер, и когда окно скрыто.</summary>
        void UpdateProfile(Snapshot snap)
        {
            var b = snap.Sample.Battery;
            if (b.OnLine)
            {
                onBatterySince = null;
                if (profileState.Applied)
                {
                    ProfileLogic.Revert(profileState, ProfileHost);
                    ProfileStore.SaveState(profileState);
                    RefreshProfileUi();
                }
                return;
            }
            if (!b.Discharging) return;  // состояние неизвестно (ошибка чтения) — ничего не делаем
            if (onBatterySince == null) onBatterySince = DateTime.UtcNow;
            if (profileState.Applied && profileState.EffApplied.Count > 0 && (DateTime.UtcNow - effRefreshAt).TotalSeconds >= EffRefreshSeconds)
            {
                effRefreshAt = DateTime.UtcNow;
                ProfileLogic.Refresh(profileState, ProfileHost);  // новые процессы тех же программ
            }
            if (profileState.Applied || calPhase >= 0 || !profile.Enabled) return;
            if ((DateTime.UtcNow - onBatterySince.Value).TotalSeconds < ProfileDelaySeconds) return;

            ProfileLogic.Apply(profile, profileState, ProfileHost);
            ProfileStore.SaveState(profileState);
            if (profileState.SetBrightness >= 0) brightness = profileState.SetBrightness;
            RefreshProfileUi();
        }

        /// <summary>
        /// Смена настроек профиля. Действует (от батареи) — сразу к новым значениям, без мигания экрана;
        /// не действует — применится через 5 с после отключения зарядки.
        /// </summary>
        void ChangeProfile(Action<ProfileSettings> change)
        {
            change(profile);
            if (profileState.Applied && lastSnap != null && lastSnap.Sample.Battery.Discharging)
            {
                ProfileLogic.Update(profile, profileState, ProfileHost);
                if (profileState.SetBrightness >= 0) brightness = profileState.SetBrightness;
            }
            else if (profileState.Applied) ProfileLogic.Revert(profileState, ProfileHost);
            ProfileStore.SaveSettings(profile);
            ProfileStore.SaveState(profileState);
            RefreshProfileUi();
        }

        FrameworkElement BuildProfileCard()
        {
            var b = new Border { CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1), Padding = new Thickness(12, 10, 12, 10), Margin = new Thickness(0, 10, 0, 0) };
            b.SetResourceReference(Border.BorderBrushProperty, Keys.Border);
            b.SetResourceReference(Border.BackgroundProperty, Keys.Page);
            var panel = new StackPanel();
            panel.Children.Add(Label(L.T("Profile “On battery”", "Профіль «Від батареї»"), 13, FontWeights.SemiBold, Keys.Text));
            var hint = Label(L.T("Applied 5 s after unplugging and undone when plugged in; a value you changed yourself in the meantime is left as is. Every change goes to logs\\actions.csv.",
                                 "Застосовується через 5 с після відключення зарядки й скасовується при підключенні; значення, яке ви тим часом змінили самі, не чіпається. Кожна зміна — в logs\\actions.csv."),
                             11, FontWeights.Normal, Keys.Muted);
            hint.TextWrapping = TextWrapping.Wrap;
            hint.Margin = new Thickness(0, 2, 0, 8);
            panel.Children.Add(hint);

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            // Яркость
            var labels = new string[ProfileBrightnessValues.Length];
            for (int i = 0; i < labels.Length; i++) labels[i] = ProfileBrightnessValues[i] == 0 ? L.T("don't change", "не змінювати") : ProfileBrightnessValues[i] + " %";
            var bright = new Segmented(labels) { HorizontalAlignment = HorizontalAlignment.Left };
            bright.Select(NearestIndex(ProfileBrightnessValues, profile.Brightness));
            bright.Changed += i => ChangeProfile(s => s.Brightness = ProfileBrightnessValues[i]);
            AddProfileRow(grid, L.T("Brightness on battery", "Яскравість від батареї"), bright);

            // Частота экрана — только если у экрана есть частота ниже
            int cur = Advisor.RefreshRate(ScreenLuma.InternalScreen().DeviceName);
            int min = Advisor.MinRefreshRate(ScreenLuma.InternalScreen().DeviceName);
            if (min > 0 && (min < cur || profileState.SetHz > 0))
            {
                var refresh = new Segmented(new[] { L.T("don't change", "не змінювати"), string.Format("{0} {1}", min, L.T("Hz", "Гц")) }) { HorizontalAlignment = HorizontalAlignment.Left };
                refresh.Select(profile.LowerRefresh ? 1 : 0);
                refresh.Changed += i => ChangeProfile(s => s.LowerRefresh = i == 1);
                AddProfileRow(grid, L.T("Refresh rate on battery", "Частота екрана від батареї"), refresh);
            }
            else if (cur > 0)
            {
                var only = Label(string.Format(L.T("the screen offers only {0} Hz", "екран підтримує лише {0} Гц"), cur), 12, FontWeights.Normal, Keys.Muted);
                only.VerticalAlignment = VerticalAlignment.Center;
                AddProfileRow(grid, L.T("Refresh rate on battery", "Частота екрана від батареї"), only);
            }

            // Режим питания Windows для батареи
            string mode = PowerModeControl.GetDc();
            if (mode != null)
            {
                var names = new string[PowerModeControl.Codes.Length];
                for (int i = 0; i < names.Length; i++) names[i] = Advisor.PowerModeName(PowerModeControl.Codes[i]);
                var modes = new Segmented(names) { HorizontalAlignment = HorizontalAlignment.Left };
                modes.Select(Array.IndexOf(PowerModeControl.Codes, mode));
                modes.Changed += i => SetPowerModeDc(modes, PowerModeControl.Codes[i]);
                modes.ToolTip = L.T("The same as Settings → System → Power → Power mode “On battery”. A permanent Windows setting.",
                                    "Те саме, що Параметри → Система → Живлення → Режим живлення «Від батареї». Постійне налаштування Windows.");
                AddProfileRow(grid, L.T("Windows power mode on battery", "Режим живлення Windows від батареї"), modes);
            }
            effApps = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
            effApps.ToolTip = L.T("Like “Efficiency mode” in Task Manager: Windows keeps the app on efficient cores at low clocks and gives it the lowest priority. Only for your own apps — services and apps run as administrator need administrator rights.",
                                  "Як «Режим ефективності» в диспетчері завдань: Windows тримає програму на енергоефективних ядрах на низькій частоті й дає їй найнижчий пріоритет. Лише для ваших програм — служби й програми від адміністратора потребують прав адміністратора.");
            AddProfileRow(grid, L.T("Efficiency mode on battery", "Режим ефективності від батареї"), effApps);
            FillEffApps();
            panel.Children.Add(grid);

            var statusRow = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
            profileRevert = new FlatButton(L.T("Undo now", "Скасувати зараз"));
            profileRevert.Margin = new Thickness(8, 0, 0, 0);
            profileRevert.Click += () =>
            {
                ProfileLogic.Revert(profileState, ProfileHost);
                profileState.Applied = true;  // до следующего отключения зарядки — не применять снова
                ProfileStore.SaveState(profileState);
                RefreshProfileUi();
            };
            DockPanel.SetDock(profileRevert, Dock.Right);
            statusRow.Children.Add(profileRevert);
            profileStatus = Label("", 12, FontWeights.Normal, Keys.Text2);
            profileStatus.TextWrapping = TextWrapping.Wrap;
            profileStatus.VerticalAlignment = VerticalAlignment.Center;
            statusRow.Children.Add(profileStatus);
            panel.Children.Add(statusRow);

            profileJournal = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
            panel.Children.Add(profileJournal);
            b.Child = panel;
            RefreshProfileUi();
            return b;
        }

        void FillEffApps()
        {
            if (effApps == null) return;
            effApps.Children.Clear();
            if (profile.EfficiencyApps.Count == 0)
            {
                var hint = Label(L.T("none — right-click an app in the Processes table or in “What used the battery”", "немає — правий клік по програмі в таблиці «Процеси» або в «Хто витрачав батарею»"),
                                 12, FontWeights.Normal, Keys.Muted);
                hint.TextWrapping = TextWrapping.Wrap;
                effApps.Children.Add(hint);
                return;
            }
            foreach (var app in profile.EfficiencyApps)
            {
                string name = app;
                var chip = new FlatButton(name + "  ×");
                chip.Margin = new Thickness(0, 0, 6, 4);
                chip.ToolTip = L.T("Remove from the list", "Прибрати зі списку");
                chip.Click += () => ToggleEfficiencyApp(name);
                effApps.Children.Add(chip);
            }
        }

        /// <summary>Добавить программу в список режима эффективности на батарее или убрать её.</summary>
        void ToggleEfficiencyApp(string name)
        {
            if (!profile.HasApp(name) && Efficiency.CanSet(name) == false)
            {
                ShowNote(string.Format(L.T("{0} runs as a service or as administrator: efficiency mode needs administrator rights.",
                                           "{0} працює як служба або від адміністратора: режим ефективності потребує прав адміністратора."), name));
                return;
            }
            ChangeProfile(s =>
            {
                int i = s.EfficiencyApps.FindIndex(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
                if (i >= 0) s.EfficiencyApps.RemoveAt(i);
                else s.EfficiencyApps.Add(name);
            });
            FillEffApps();
        }

        /// <summary>Меню по правой кнопке на имени программы (таблицы процессов и «Кто тратил батарею»).</summary>
        void AttachProcessMenu(FrameworkElement target, Func<string> name)
        {
            target.MouseRightButtonUp += (s, e) =>
            {
                string n = name();
                if (string.IsNullOrEmpty(n)) return;
                var menu = new System.Windows.Forms.ContextMenuStrip();
                menu.Renderer = new System.Windows.Forms.ToolStripProfessionalRenderer(new TrayIcon.MenuColors(theme)) { RoundedEdges = false };
                var item = new System.Windows.Forms.ToolStripMenuItem(string.Format(L.T("Efficiency mode on battery: {0}", "Режим ефективності від батареї: {0}"), n))
                {
                    Checked = profile.HasApp(n),
                    ForeColor = TrayIcon.ToGdi(theme.TextPrimary),
                };
                item.Click += (s2, e2) => ToggleEfficiencyApp(n);
                menu.Items.Add(item);
                menu.Closed += (s2, e2) => Dispatcher.BeginInvoke(new Action(menu.Dispose));
                menu.Show(System.Windows.Forms.Control.MousePosition);
                e.Handled = true;
            };
        }

        static void AddProfileRow(Grid grid, string label, FrameworkElement control)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            int row = grid.RowDefinitions.Count - 1;
            var l = Label(label, 12, FontWeights.Normal, Keys.Text2);
            l.VerticalAlignment = VerticalAlignment.Center;
            l.Margin = new Thickness(0, 3, 16, 3);
            Grid.SetRow(l, row);
            grid.Children.Add(l);
            control.Margin = new Thickness(0, 3, 0, 3);
            Grid.SetRow(control, row);
            Grid.SetColumn(control, 1);
            grid.Children.Add(control);
        }

        static int NearestIndex(int[] values, int v)
        {
            int best = 0;
            for (int i = 1; i < values.Length; i++)
                if (Math.Abs(values[i] - v) < Math.Abs(values[best] - v)) best = i;
            return best;
        }

        void SetPowerModeDc(Segmented control, string code)
        {
            string before = PowerModeControl.GetDc();
            if (before == code) return;
            if (PowerModeControl.SetDc(code))
                ProfileHost.Log("power_mode_dc", Advisor.PowerModeName(before), Advisor.PowerModeName(code), ProfileLogic.ByUser);
            else
                ShowNote(L.T("Windows did not accept the power mode change.", "Windows не прийняла зміну режиму живлення."));
            control.Select(Array.IndexOf(PowerModeControl.Codes, PowerModeControl.GetDc()));
            RefreshProfileUi();
        }

        void RefreshProfileUi()
        {
            if (profileStatus == null) return;
            bool onBattery = lastSnap != null && lastSnap.Sample.Battery.Discharging;
            bool enabled = profile.Enabled;
            profileRevert.Visibility = profileState.Applied && profileState.ChangedSomething ? Visibility.Visible : Visibility.Collapsed;
            if (profileState.Applied && profileState.ChangedSomething)
                profileStatus.Text = string.Format(L.T("Active: {0}. Undone when you plug in.", "Діє: {0}. Скасується при підключенні зарядки."), ProfileLogic.Describe(profileState));
            else if (profileState.Applied && onBattery)
                profileStatus.Text = L.T("Active: nothing needed changing.", "Діє: змінювати нічого не довелося.");
            else if (enabled)
                profileStatus.Text = L.T("Will apply 5 s after you unplug the charger.", "Застосується через 5 с після відключення зарядки.");
            else
                profileStatus.Text = L.T("Nothing is changed on battery.", "Від батареї нічого не змінюється.");

            profileJournal.Children.Clear();
            System.Collections.Generic.List<ActionEntry> list;
            try { list = ActionLog.Load(LogDirectory, 5); }
            catch (Exception) { return; }
            if (list.Count == 0) return;
            profileJournal.Children.Add(Label(L.T("Recent changes", "Останні зміни"), 11, FontWeights.Normal, Keys.Muted));
            foreach (var a in list)
            {
                var t = Label(string.Format("{0:dd.MM HH:mm} · {1}: {2} → {3} · {4}", a.At, ActionLog.ActionName(a.Action), a.From, a.To,
                    a.By == ProfileLogic.ByUser ? L.T("by you", "вами") : L.T("profile", "профіль")), 12, FontWeights.Normal, Keys.Text2);
                t.TextTrimming = TextTrimming.CharacterEllipsis;
                profileJournal.Children.Add(t);
            }
        }
    }
}
