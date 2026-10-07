using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace BatteryCheck
{
    /// <summary>
    /// Пути к exe процессов из лога энергии: logs\process_paths.csv (process,path). Имя пишется, когда процесс впервые
    /// попадает в лог; путь — первого найденного процесса с этим именем. Защищённые системные процессы пути не отдают.
    /// </summary>
    sealed class ProcessPaths
    {
        const string FileName = "process_paths.csv";

        readonly string dir;
        readonly HashSet<string> known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);  // и те, чей путь не узнать: не спрашивать снова

        public ProcessPaths(string dir)
        {
            this.dir = dir;
            foreach (var kv in Load(dir)) known.Add(kv.Key);
        }

        /// <summary>Запомнить пути новых имён (вызывать раз в минуту: на каждое новое имя — перебор процессов).</summary>
        public void Remember(IEnumerable<string> names)
        {
            List<string> lines = null;
            foreach (var n in names)
            {
                if (!known.Add(n)) continue;
                string path = RegistryGpuPreferenceStore.ExeOf(n);
                if (string.IsNullOrEmpty(path) || path.IndexOf(',') >= 0) continue;
                if (lines == null) lines = new List<string>();
                lines.Add(n.Replace(",", " ") + "," + path);
            }
            if (lines == null) return;
            try
            {
                Directory.CreateDirectory(dir);
                string file = Path.Combine(dir, FileName);
                bool header = !File.Exists(file);
                using (var w = new StreamWriter(file, true, new UTF8Encoding(false)))
                {
                    if (header) w.WriteLine("process,path");
                    foreach (var l in lines) w.WriteLine(l);
                }
            }
            catch (IOException) { }
        }

        public static Dictionary<string, string> Load(string dir)
        {
            var r = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string file = Path.Combine(dir, FileName);
            if (!File.Exists(file)) return r;
            try
            {
                using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var sr = new StreamReader(fs))
                {
                    sr.ReadLine();
                    string line;
                    while ((line = sr.ReadLine()) != null)
                    {
                        int i = line.IndexOf(',');
                        if (i > 0) r[line.Substring(0, i)] = line.Substring(i + 1);  // позже записанный путь — свежее
                    }
                }
            }
            catch (IOException) { }
            return r;
        }
    }

    /// <summary>Энергия одного процесса за цикл разряда.</summary>
    sealed class CycleProcess
    {
        public string Name, Path;
        public double Wh, FgWh;      // всего и пока окно процесса было активным
        public double AvgW;          // Wh ÷ время от батареи со сбором данных в этом цикле
        public double BgWh { get { return Math.Max(0, Wh - FgWh); } }
    }

    /// <summary>Процессы одного разряда: самые прожорливые и сумма по всем.</summary>
    sealed class CycleApps
    {
        public readonly List<CycleProcess> Top = new List<CycleProcess>();
        public double AllWh;    // все процессы, и не попавшие в Top
        public double Seconds;  // время от батареи со сбором по процессам
    }

    /// <summary>
    /// Самые прожорливые процессы каждого разряда — по logs\process_energy_*.csv (минуты от батареи внутри разряда).
    /// </summary>
    static class CycleProcesses
    {
        public const int Top = 12;  // сколько поместится в строку — остальное обрезается многоточием
        public const double MinWh = 1;  // меньше за разряд — не показывать

        /// <summary>Для каждого разряда (в том же порядке) — до Top процессов по убыванию энергии; Seconds = 0 — нет данных.</summary>
        public static List<CycleApps> Load(string dir, IList<DischargeSession> sessions)
        {
            var result = new List<CycleApps>();
            var acc = new List<Dictionary<string, double[]>>();
            var seconds = new double[sessions.Count];
            foreach (var s in sessions)
            {
                result.Add(new CycleApps());
                acc.Add(new Dictionary<string, double[]>(StringComparer.OrdinalIgnoreCase));
            }
            if (sessions.Count == 0 || !Directory.Exists(dir)) return result;

            var inv = CultureInfo.InvariantCulture;
            foreach (var f in Directory.GetFiles(dir, "process_energy_*.csv"))
            {
                DateTime day;
                string stamp = System.IO.Path.GetFileNameWithoutExtension(f).Substring("process_energy_".Length);
                if (!DateTime.TryParseExact(stamp, "yyyyMMdd", inv, DateTimeStyles.None, out day)) continue;
                bool needed = false;
                foreach (var s in sessions)
                    if (s.Start.Date <= day && day <= s.End.Date) { needed = true; break; }
                if (!needed) continue;
                try
                {
                    using (var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    using (var sr = new StreamReader(fs))
                    {
                        sr.ReadLine();
                        string line;
                        int idx = -1;
                        string lastMinute = null;
                        while ((line = sr.ReadLine()) != null)
                        {
                            var c = line.Split(',');
                            if (c.Length < 6) continue;
                            if (c[0] != lastMinute)
                            {
                                lastMinute = c[0];
                                DateTime m;
                                idx = DateTime.TryParseExact(c[0], "yyyy-MM-ddTHH:mm", inv, DateTimeStyles.None, out m) ? Find(sessions, m) : -1;
                            }
                            if (idx < 0) continue;
                            double v;
                            if (c[1] == ProcessEnergyLog.BatteryRow)
                            {
                                if (double.TryParse(c[5], NumberStyles.Float, inv, out v)) seconds[idx] += v;
                                continue;
                            }
                            if (!double.TryParse(c[2], NumberStyles.Float, inv, out v)) continue;
                            double fg;
                            if (!double.TryParse(c[3], NumberStyles.Float, inv, out fg)) fg = 0;
                            double[] a;
                            if (!acc[idx].TryGetValue(c[1], out a)) acc[idx][c[1]] = a = new double[2];
                            a[0] += v;
                            a[1] += fg;
                        }
                    }
                }
                catch (IOException) { }
            }

            var paths = ProcessPaths.Load(dir);
            for (int i = 0; i < sessions.Count; i++)
            {
                if (seconds[i] < 60) continue;
                result[i].Seconds = seconds[i];
                var list = result[i].Top;
                foreach (var kv in acc[i])
                {
                    result[i].AllWh += kv.Value[0];
                    string path;
                    paths.TryGetValue(kv.Key, out path);
                    list.Add(new CycleProcess { Name = kv.Key, Path = path, Wh = kv.Value[0], FgWh = kv.Value[1], AvgW = kv.Value[0] * 3600 / seconds[i] });
                }
                list.RemoveAll(p => p.Wh < MinWh);
                list.Sort((a, b) => b.Wh.CompareTo(a.Wh));
                if (list.Count > Top) list.RemoveRange(Top, list.Count - Top);
            }
            return result;
        }

        /// <summary>Разряд, в который попадает минута (минута лога — начало; разряд — с точностью до минуты).</summary>
        static int Find(IList<DischargeSession> sessions, DateTime minute)
        {
            for (int i = 0; i < sessions.Count; i++)
            {
                var s = sessions[i];
                var start = new DateTime(s.Start.Year, s.Start.Month, s.Start.Day, s.Start.Hour, s.Start.Minute, 0);
                if (minute >= start && minute <= s.End) return i;
            }
            return -1;
        }
    }
}
