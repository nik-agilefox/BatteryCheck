using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Management;

namespace BatteryCheck
{
    /// <summary>Сторонняя служба Windows (не Microsoft, не внутри svchost).</summary>
    sealed class ServiceEntry
    {
        public string Name, DisplayName, State, StartMode, Exe, Company, Process;
        public int Pid;
        public bool Running { get { return State == "Running"; } }
    }

    /// <summary>
    /// Сторонние службы для вкладки «Оптимизация» — только просмотр: программа службы не останавливает
    /// (это требует прав администратора); пользователь останавливает их сам в «Службах».
    /// Сторонние — исполняемый файл не от Microsoft и не svchost: службы производителей ставятся и в System32, и в DriverStore,
    /// поэтому отбор по производителю файла, а не по папке.
    /// </summary>
    static class ServiceControl
    {
        /// <summary>Службы безопасности не показываем: их нельзя и не нужно останавливать.</summary>
        static readonly HashSet<string> Protected = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "WinDefend", "MDCoreSvc", "WdNisSvc", "Sense", "SecurityHealthService", "wscsvc",
        };

        /// <summary>Сторонние службы (WMI, ~0,1–0,3 с — вызывать не из потока окна).</summary>
        public static List<ServiceEntry> List()
        {
            var r = new List<ServiceEntry>();
            using (var q = new ManagementObjectSearcher("SELECT Name, DisplayName, State, StartMode, ProcessId, PathName FROM Win32_Service"))
                foreach (ManagementObject o in q.Get())
                    using (o)
                    {
                        string name = o["Name"] as string, path = o["PathName"] as string;
                        if (name == null || path == null || Protected.Contains(name)) continue;
                        string exe = ExePath(path);
                        if (exe == null || Path.GetFileName(exe).Equals("svchost.exe", StringComparison.OrdinalIgnoreCase)) continue;
                        string company = "";
                        try { company = FileVersionInfo.GetVersionInfo(Environment.ExpandEnvironmentVariables(exe)).CompanyName ?? ""; }
                        catch (Exception) { }
                        if (company.StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase)) continue;
                        r.Add(new ServiceEntry
                        {
                            Name = name, DisplayName = o["DisplayName"] as string ?? name,
                            State = o["State"] as string ?? "", StartMode = o["StartMode"] as string ?? "",
                            Exe = exe, Company = company.Trim(),
                            Pid = Convert.ToInt32(o["ProcessId"] ?? 0),
                            Process = Path.GetFileNameWithoutExtension(exe),
                        });
                    }
            return r;
        }

        /// <summary>Путь к exe из PathName службы («"C:\x\y.exe" -s N» или «C:\x\y.exe -k»).</summary>
        static string ExePath(string pathName)
        {
            string p = pathName.Trim();
            if (p.StartsWith("\""))
            {
                int q = p.IndexOf('"', 1);
                return q > 1 ? p.Substring(1, q - 1) : null;
            }
            int i = p.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            return i > 0 ? p.Substring(0, i + 4) : p;
        }

        /// <summary>Открыть оснастку «Службы» (Windows сама спросит права администратора).</summary>
        public static void OpenServices()
        {
            try { System.Diagnostics.Process.Start(new ProcessStartInfo("services.msc") { UseShellExecute = true }); }
            catch (Exception) { }  // отказ в UAC — ничего не делаем
        }
    }
}
