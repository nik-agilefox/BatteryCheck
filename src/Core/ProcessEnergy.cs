using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace BatteryCheck
{
    /// <summary>Процесс активного окна — почти бесплатно (два вызова user32).</summary>
    static class ForegroundApp
    {
        public static long Pid()
        {
            IntPtr h = GetForegroundWindow();
            if (h == IntPtr.Zero) return 0;
            uint pid;
            GetWindowThreadProcessId(h, out pid);
            return pid;
        }

        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    }

    /// <summary>Сколько секунд не было ввода с клавиатуры и мыши (GetLastInputInfo — доли микросекунды).</summary>
    static class UserIdle
    {
        public static double Seconds()
        {
            var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf(typeof(LASTINPUTINFO)) };
            if (!GetLastInputInfo(ref info)) return double.NaN;
            return unchecked((uint)Environment.TickCount - info.dwTime) / 1000.0;  // оба счётчика 32-битные и переполняются вместе
        }

        [StructLayout(LayoutKind.Sequential)]
        struct LASTINPUTINFO
        {
            public uint cbSize, dwTime;
        }

        [DllImport("user32.dll")] static extern bool GetLastInputInfo(ref LASTINPUTINFO info);
    }

    /// <summary>
    /// Энергия процессов при работе от батареи, по минутам: logs\process_energy_ГГГГММДД.csv.
    /// Строки: minute, process, wh (всего), fg_wh (пока окно процесса было активным), gpu_wh (на видеокарты), seconds.
    /// Особая строка «_battery» — вся энергия батареи и время работы от неё в эту минуту: знаменатель для средних.
    /// Процессы с энергией меньше 0,0005 Вт·ч за минуту не пишутся, чтобы лог оставался небольшим.
    /// </summary>
    sealed class ProcessEnergyLog : IDisposable
    {
        public const string BatteryRow = "_battery";
        const double MinWh = 0.0005;
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        readonly string dir;
        DateTime minute = DateTime.MinValue;
        readonly Dictionary<string, double[]> acc = new Dictionary<string, double[]>(StringComparer.OrdinalIgnoreCase);  // wh, fg_wh, gpu_wh
        double batteryWh, batterySeconds;

        ProcessPaths paths;  // при первом сбросе: чтение файла не в конструкторе Sampler

        public ProcessEnergyLog(string dir)
        {
            this.dir = dir;
        }

        public void Add(DateTime t, string name, double wh, bool foreground, double gpuWh)
        {
            Roll(t);
            double[] a;
            if (!acc.TryGetValue(name, out a)) acc[name] = a = new double[3];
            a[0] += wh;
            if (foreground) a[1] += wh;
            a[2] += gpuWh;
        }

        public void AddBattery(DateTime t, double wh, double seconds)
        {
            Roll(t);
            batteryWh += wh;
            batterySeconds += seconds;
        }

        void Roll(DateTime t)
        {
            var m = new DateTime(t.Year, t.Month, t.Day, t.Hour, t.Minute, 0);
            if (m == minute) return;
            Flush();
            minute = m;
        }

        public void Flush()
        {
            if (minute == DateTime.MinValue || (acc.Count == 0 && batterySeconds == 0)) return;
            try
            {
                Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, "process_energy_" + minute.ToString("yyyyMMdd", Inv) + ".csv");
                bool header = !File.Exists(path);
                using (var w = new StreamWriter(path, true, new UTF8Encoding(false)))
                {
                    if (header) w.WriteLine("minute,process,wh,fg_wh,gpu_wh,seconds");
                    string m = minute.ToString("yyyy-MM-ddTHH:mm", Inv);
                    w.WriteLine(string.Format(Inv, "{0},{1},{2:0.####},,,{3:0}", m, BatteryRow, batteryWh, batterySeconds));
                    foreach (var kv in acc)
                        if (kv.Value[0] >= MinWh)
                            w.WriteLine(string.Format(Inv, "{0},{1},{2:0.####},{3:0.####},{4:0.####},", m, kv.Key.Replace(",", " "), kv.Value[0], kv.Value[1], kv.Value[2]));
                }
            }
            catch (IOException)
            {
                // Не записалось — потеряна минута статистики, не страшно.
            }
            if (acc.Count > 0)
            {
                if (paths == null) paths = new ProcessPaths(dir);
                var names = new List<string>();
                foreach (var kv in acc) if (kv.Value[0] >= MinWh) names.Add(kv.Key);
                paths.Remember(names);  // для подсказок во вкладке «История»
            }
            acc.Clear();
            batteryWh = batterySeconds = 0;
        }

        public void Dispose()
        {
            Flush();
        }
    }
}
