using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Text;

namespace BatteryCheck
{
    /// <summary>
    /// Остановка и запуск служб для режима Subzero — одним окном UAC на действие, без постоянных компонентов с правами.
    /// Команда передаётся целиком в -EncodedCommand (без промежуточного скрипта, который можно подменить); итог по каждой
    /// службе — в файл во временной папке пользователя. Тип запуска служб не меняется: после перезагрузки они стартуют сами.
    /// </summary>
    static class ServiceSwitch
    {
        /// <summary>Какие из служб сейчас работают (без прав администратора).</summary>
        public static List<string> Running(IEnumerable<string> names)
        {
            var r = new List<string>();
            foreach (var n in names)
            {
                if (!Safe(n)) continue;
                try
                {
                    using (var q = new ManagementObjectSearcher("SELECT State FROM Win32_Service WHERE Name='" + n + "'"))
                        foreach (ManagementObject o in q.Get())
                            using (o)
                                if ((o["State"] as string) == "Running") r.Add(n);
                }
                catch (Exception) { }
            }
            return r;
        }

        /// <summary>Службы из списка, которые остановлены, но запускаются автоматически (не отключены пользователем).</summary>
        public static List<string> StoppedAutomatic(IEnumerable<string> names)
        {
            var r = new List<string>();
            foreach (var n in names)
            {
                if (!Safe(n)) continue;
                try
                {
                    using (var q = new ManagementObjectSearcher("SELECT State, StartMode FROM Win32_Service WHERE Name='" + n + "'"))
                        foreach (ManagementObject o in q.Get())
                            using (o)
                                if ((o["State"] as string) == "Stopped" && (o["StartMode"] as string) == "Auto") r.Add(n);
                }
                catch (Exception) { }
            }
            return r;
        }

        static bool Safe(string name)
        {
            return !string.IsNullOrEmpty(name) && name.IndexOfAny(new[] { '\'', '"', '`', '$', ';', '\r', '\n', '\\' }) < 0;
        }

        /// <summary>
        /// Остановить (stop = true) или запустить службы. Спрашивает права администратора (UAC) и ждёт до минуты — вызывать
        /// не из потока окна. Возвращает службы, с которыми действие удалось; error — «cancelled», если в UAC отказали.
        /// </summary>
        public static List<string> Run(bool stop, IList<string> names, out string error)
        {
            error = null;
            var done = new List<string>();
            var list = new List<string>();
            foreach (var n in names) if (Safe(n)) list.Add("'" + n + "'");
            if (list.Count == 0) return done;
            string outFile = Path.Combine(Path.GetTempPath(), "bc_subzero_" + Guid.NewGuid().ToString("N") + ".txt");
            string script =
                "$r = foreach ($n in @(" + string.Join(",", list) + ")) { try { " +
                (stop ? "Stop-Service -Name $n -Force -ErrorAction Stop" : "Start-Service -Name $n -ErrorAction Stop") +
                "; \"ok $n\" } catch { \"err $n $($_.Exception.Message)\" } }; $r | Out-File '" + outFile + "' -Encoding utf8";
            var psi = new ProcessStartInfo("powershell.exe",
                "-NoProfile -NonInteractive -WindowStyle Hidden -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(script)))
            {
                Verb = "runas",
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            try
            {
                using (var p = Process.Start(psi))
                    if (!p.WaitForExit(60000)) { error = "timeout"; return done; }
            }
            catch (Win32Exception e)
            {
                error = e.NativeErrorCode == 1223 ? "cancelled" : e.Message;  // 1223 — отказ в UAC
                return done;
            }
            try
            {
                if (File.Exists(outFile))
                {
                    foreach (var line in File.ReadAllLines(outFile))
                    {
                        var l = line.Trim();
                        if (l.StartsWith("ok ")) done.Add(l.Substring(3).Trim());
                        else if (l.StartsWith("err ") && error == null) error = l.Substring(4);
                    }
                    File.Delete(outFile);
                }
            }
            catch (Exception) { }
            return done;
        }
    }
}
