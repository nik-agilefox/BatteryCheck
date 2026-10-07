using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

// Установщик Battery Check для одного пользователя, без прав администратора: программа — в %LOCALAPPDATA%\Programs\BatteryCheck\bin,
// логи — рядом в ..\logs (как в папке разработки), ярлык в меню «Пуск», запись в «Установленных приложениях».
// Сами exe вшиты ресурсами (build.cmd). Тот же файл, скопированный как uninstall.exe, удаляет программу (/uninstall).
namespace BatteryCheck
{
    static class Setup
    {
        const string AppName = "Battery Check";
        const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\BatteryCheck";
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        static readonly string[] Payload = { "BatteryCheckGui.exe", "BatteryCheck.exe" };

        static string Root { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "BatteryCheck"); } }
        static string BinDir { get { return Path.Combine(Root, "bin"); } }
        static string GuiExe { get { return Path.Combine(BinDir, "BatteryCheckGui.exe"); } }
        static string Shortcut { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), AppName + ".lnk"); } }

        static bool silent, offline;

        [STAThread]
        static int Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            bool uninstall = false;
            foreach (var a in args)
            {
                if (a.Equals("/uninstall", StringComparison.OrdinalIgnoreCase)) uninstall = true;
                if (a.Equals("/silent", StringComparison.OrdinalIgnoreCase)) silent = true;  // без окон: без автозапуска и открытия; данные сохраняются
                if (a.Equals("/offline", StringComparison.OrdinalIgnoreCase)) offline = true;  // не проверять GitHub: ставить вшитую версию
            }
            try
            {
                return uninstall ? Uninstall() : Install();
            }
            catch (Exception e)
            {
                MessageBox.Show(e.Message, AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
        }

        // ---------------- Установка ----------------

        static int Install()
        {
            string installed = InstalledVersion();
            // Есть ли на GitHub версия новее вшитой: тогда ставим её (архив тот же, что у кнопки обновления в программе).
            // Без сети или при ошибке — вшитая версия; ждём недолго, чтобы установщик не «висел».
            ReleaseInfo online = null;
            if (!offline)
            {
                Cursor.Current = Cursors.WaitCursor;
                string checkError;
                online = Updater.Check(out checkError, 6000);
                Cursor.Current = Cursors.Default;
                if (online != null && !ReleaseInfo.IsNewer(online.Version, AppInfo.Version)) online = null;
            }
            string version = online != null ? online.Version : AppInfo.Version;

            var form = new SetupForm(
                AppName + " " + version,
                (online != null ? "A newer version " + online.Version + " is available on GitHub: it will be downloaded (" +
                                  (online.PackageSize / 1024) + " KB) and installed instead of " + AppInfo.Version + " from this installer.\n\n" : "") +
                (installed != null ? "Installed: " + installed + " — it will be replaced; logs and settings are kept.\n\n" : "") +
                "Installs to:\n" + BinDir + "\n\nNo administrator rights needed. Logs are kept in " + Path.Combine(Root, "logs") + ".",
                installed != null ? "Update" : "Install",
                new[] { "Start with Windows (in the background, icon in the tray)", "Open Battery Check when done" },
                new[] { false, true });
            if (!silent && form.ShowDialog() != DialogResult.OK) return 2;

            CloseRunning();
            Directory.CreateDirectory(BinDir);
            string fetchError = null;
            if (online == null || !InstallOnline(online, out fetchError))
            {
                version = AppInfo.Version;
                InstallEmbedded();
                if (online != null && !silent)
                    MessageBox.Show("Could not download version " + online.Version + " (" + fetchError + "). Version " + AppInfo.Version +
                                    " from this installer is installed instead; it will offer the update itself (Check updates).",
                                    AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            var asm = Assembly.GetExecutingAssembly();
            string self = asm.Location, uninstaller = Path.Combine(Root, "uninstall.exe");
            if (!string.Equals(self, uninstaller, StringComparison.OrdinalIgnoreCase)) File.Copy(self, uninstaller, true);

            CreateShortcut(Shortcut, GuiExe, "", "Battery health and power draw");
            using (var k = Registry.CurrentUser.CreateSubKey(UninstallKey))
            {
                k.SetValue("DisplayName", AppName);
                k.SetValue("DisplayVersion", version);
                k.SetValue("Publisher", AppInfo.Repo.Split('/')[0]);
                k.SetValue("URLInfoAbout", "https://github.com/" + AppInfo.Repo);
                k.SetValue("InstallLocation", Root);
                k.SetValue("DisplayIcon", GuiExe);
                k.SetValue("UninstallString", "\"" + uninstaller + "\" /uninstall");
                k.SetValue("NoModify", 1, RegistryValueKind.DWord);
                k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                k.SetValue("EstimatedSize", (int)(DirSize(Root) / 1024), RegistryValueKind.DWord);
            }
            // Та же строка, что пишет пункт «Запускать разом з Windows» в программе: она узнает свою запись.
            using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
                if (!silent && form.Checked[0]) k.SetValue("BatteryCheck", "\"" + GuiExe + "\" --background");

            if (silent) return 0;
            if (form.Checked[1]) Process.Start(new ProcessStartInfo(GuiExe) { WorkingDirectory = BinDir });
            else MessageBox.Show(AppName + " " + version + " is installed. Find it in the Start menu.", AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }

        /// <summary>Файлы программы, вшитые в установщик.</summary>
        static void InstallEmbedded()
        {
            var asm = Assembly.GetExecutingAssembly();
            foreach (var name in Payload)
                using (var src = asm.GetManifestResourceStream("payload." + name))
                {
                    if (src == null) throw new InvalidOperationException("The installer is damaged: " + name + " is missing.");
                    WriteFile(Path.Combine(BinDir, name), src);
                }
        }

        /// <summary>
        /// Скачать релиз (размер и SHA-256 сверяются) и поставить его файлы; false — ошибка. Если она случилась на середине
        /// замены, вызывающий ставит вшитую версию поверх: файлы снова из одной версии.
        /// </summary>
        static bool InstallOnline(ReleaseInfo r, out string error)
        {
            string work = Path.Combine(Path.GetTempPath(), "BatteryCheck-setup-" + r.Version);
            try
            {
                Cursor.Current = Cursors.WaitCursor;
                var files = Updater.Fetch(r, work, null, out error);
                if (files == null) return false;
                foreach (var name in files)
                    using (var src = File.OpenRead(Path.Combine(work, "files", name)))
                        WriteFile(Path.Combine(BinDir, name), src);
                return true;
            }
            catch (Exception e)
            {
                error = e.Message;
                return false;
            }
            finally
            {
                Cursor.Current = Cursors.Default;
                try { if (Directory.Exists(work)) Directory.Delete(work, true); } catch (Exception) { }
            }
        }

        static string InstalledVersion()
        {
            using (var k = Registry.CurrentUser.OpenSubKey(UninstallKey))
                return k == null || !File.Exists(GuiExe) ? null : k.GetValue("DisplayVersion") as string;
        }

        /// <summary>Записать через временный файл: запущенный exe нельзя перезаписать, но можно переименовать.</summary>
        static void WriteFile(string target, Stream src)
        {
            string tmp = target + ".new";
            using (var dst = File.Create(tmp)) src.CopyTo(dst);
            if (File.Exists(target))
            {
                string old = target + ".old";
                try { if (File.Exists(old)) File.Delete(old); } catch (Exception) { old = target + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".old"; }
                File.Move(target, old);  // программа сама удалит *.old при следующем запуске
            }
            File.Move(tmp, target);
        }

        /// <summary>Закрыть работающую программу её же командой --exit (она вернёт яркость, если шёл замер экрана) и дождаться.</summary>
        static void CloseRunning()
        {
            if (!File.Exists(GuiExe)) return;
            foreach (var p in Process.GetProcessesByName("BatteryCheckGui"))
                using (p)
                {
                    // Только копию из папки установки: --exit действует на любую запущенную, а копию из другой папки
                    // (например, сборку разработчика) установщик закрывать не должен.
                    string path = null;
                    try { path = p.MainModule.FileName; } catch (Exception) { }
                    if (!string.Equals(path, GuiExe, StringComparison.OrdinalIgnoreCase)) continue;
                    try { Process.Start(new ProcessStartInfo(GuiExe, "--exit") { UseShellExecute = false }).WaitForExit(5000); } catch (Exception) { }
                    if (!p.WaitForExit(10000)) try { p.Kill(); p.WaitForExit(3000); } catch (Exception) { }
                }
        }

        // ---------------- Удаление ----------------

        static int Uninstall()
        {
            bool modeOn = false;
            using (var k = Registry.CurrentUser.OpenSubKey(@"Software\BatteryCheck"))
                if (k != null) modeOn = IsOn(k.GetValue("MaxEconomy")) || IsOn(k.GetValue("Subzero"));
            var form = new SetupForm(
                "Remove " + AppName,
                (modeOn ? "⚠ Eco or Subzero mode is on. Cancel, open Battery Check and switch to Standard first: " +
                          "that restores the Windows power settings the mode changed.\n\n" : "") +
                "Removes the program from " + Root + ", the Start menu shortcut and autostart.",
                "Remove",
                new[] { "Also delete my measurements: logs and saved settings (screen measurement, idle baseline…)" },
                new[] { false });
            if (!silent && form.ShowDialog() != DialogResult.OK) return 2;
            bool deleteData = !silent && form.Checked[0];

            CloseRunning();
            foreach (var name in Payload) TryDelete(Path.Combine(BinDir, name));
            foreach (var f in Directory.Exists(BinDir) ? Directory.GetFiles(BinDir, "*.old") : new string[0]) TryDelete(f);
            TryDelete(Shortcut);
            using (var k = Registry.CurrentUser.OpenSubKey(RunKey, true))
            {
                var v = k == null ? null : k.GetValue("BatteryCheck") as string;
                if (v != null && v.IndexOf(BinDir, StringComparison.OrdinalIgnoreCase) >= 0) k.DeleteValue("BatteryCheck");
            }
            using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\DirectX\UserGpuPreferences", true))
                if (k != null && k.GetValue(GuiExe) != null) k.DeleteValue(GuiExe);  // «встроенная графика» для своего окна
            Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false);
            if (deleteData) Registry.CurrentUser.DeleteSubKeyTree(@"Software\BatteryCheck", false);

            // uninstall.exe сейчас запущен и сам себя не удалит: папку уберёт cmd, когда этот процесс выйдет.
            string keep = deleteData ? "" : Path.Combine(Root, "logs");
            string cmd = "/c ping -n 3 127.0.0.1 >nul & del /f /q \"" + Path.Combine(Root, "uninstall.exe") + "\" & rmdir /s /q \"" + BinDir + "\"" +
                         (deleteData ? " & rmdir /s /q \"" + Root + "\"" : " & rmdir \"" + Root + "\" 2>nul");
            Process.Start(new ProcessStartInfo("cmd.exe", cmd) { CreateNoWindow = true, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden });
            if (!silent) MessageBox.Show(AppName + " is removed." + (deleteData ? "" : "\nYour logs are kept in " + keep + "."), AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }

        static bool IsOn(object v)
        {
            double d;
            return v != null && double.TryParse(Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture),
                System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out d) && d != 0;
        }

        static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch (Exception) { }
        }

        static long DirSize(string dir)
        {
            long s = 0;
            foreach (var f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories)) s += new FileInfo(f).Length;
            return s;
        }

        /// <summary>Ярлык через WScript.Shell (есть в любой Windows), без ссылки на COM-сборку.</summary>
        static void CreateShortcut(string lnk, string target, string args, string description)
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            object shell = Activator.CreateInstance(shellType);
            object sc = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { lnk });
            var t = sc.GetType();
            t.InvokeMember("TargetPath", BindingFlags.SetProperty, null, sc, new object[] { target });
            t.InvokeMember("Arguments", BindingFlags.SetProperty, null, sc, new object[] { args });
            t.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, sc, new object[] { Path.GetDirectoryName(target) });
            t.InvokeMember("IconLocation", BindingFlags.SetProperty, null, sc, new object[] { target + ",0" });
            t.InvokeMember("Description", BindingFlags.SetProperty, null, sc, new object[] { description });
            t.InvokeMember("Save", BindingFlags.InvokeMethod, null, sc, null);
        }
    }

    /// <summary>Простое окно установщика: текст, флажки, кнопка действия и «Отмена».</summary>
    sealed class SetupForm : Form
    {
        readonly CheckBox[] boxes;

        public bool[] Checked
        {
            get
            {
                var r = new bool[boxes.Length];
                for (int i = 0; i < boxes.Length; i++) r[i] = boxes[i].Checked;
                return r;
            }
        }

        public SetupForm(string title, string text, string action, string[] options, bool[] defaults)
        {
            Text = title;
            Font = new Font("Segoe UI", 9.5f);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            AutoScaleMode = AutoScaleMode.Dpi;
            try { Icon = Icon.ExtractAssociatedIcon(Assembly.GetExecutingAssembly().Location); } catch (Exception) { }

            var panel = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, Padding = new Padding(20, 18, 20, 12), WrapContents = false };
            panel.Controls.Add(new Label { Text = title, Font = new Font("Segoe UI Semibold", 13f), AutoSize = true, Margin = new Padding(0, 0, 0, 10) });
            panel.Controls.Add(new Label { Text = text, AutoSize = true, MaximumSize = new Size(520, 0), Margin = new Padding(0, 0, 0, 12) });
            boxes = new CheckBox[options.Length];
            for (int i = 0; i < options.Length; i++)
            {
                boxes[i] = new CheckBox { Text = options[i], Checked = defaults[i], AutoSize = true, MaximumSize = new Size(520, 0), Margin = new Padding(0, 2, 0, 2) };
                panel.Controls.Add(boxes[i]);
            }
            var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Width = 520, Margin = new Padding(0, 14, 0, 0) };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true, MinimumSize = new Size(90, 30) };
            var ok = new Button { Text = action, DialogResult = DialogResult.OK, AutoSize = true, MinimumSize = new Size(90, 30) };
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(ok);
            panel.Controls.Add(buttons);
            Controls.Add(panel);
            AcceptButton = ok;
            CancelButton = cancel;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
        }
    }
}
