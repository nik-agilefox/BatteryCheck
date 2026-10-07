using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace BatteryCheck
{
    /// <summary>
    /// Кто тратит, пока пользователь не работает: тихие минуты — из battery_*.csv (все замеры минуты от батареи, ввода не было
    /// ≥ 60 с, замеров хотя бы 3), энергия программ — из process_energy_*.csv за те же минуты. Итог — средняя мощность
    /// каждой программы в тихие минуты. Тихие минуты законченных логов кэшируются по файлам.
    /// </summary>
    static class QuietProcesses
    {
        public sealed class Result
        {
            public int Minutes;                 // тихих минут с данными о программах
            public double BatteryW = double.NaN;  // средний расход батареи в эти минуты
            public readonly List<KeyValuePair<string, double>> Top = new List<KeyValuePair<string, double>>();  // программа → Вт, по убыванию
        }

        sealed class FileMinutes
        {
            public long Length;
            public DateTime Written;
            public HashSet<DateTime> Quiet;
        }

        static readonly Dictionary<string, FileMinutes> cache = new Dictionary<string, FileMinutes>(StringComparer.OrdinalIgnoreCase);
        static readonly object sync = new object();

        public static Result Compute(string dir, int days, DateTime now)
        {
            DateTime from = now.AddDays(-days);
            var quiet = new HashSet<DateTime>();
            if (Directory.Exists(dir))
                lock (sync)
                    foreach (var f in Directory.GetFiles(dir, "battery_*.csv"))
                    {
                        var fi = new FileInfo(f);
                        if (fi.LastWriteTime < from) continue;
                        FileMinutes fm;
                        if (!cache.TryGetValue(f, out fm) || fm.Length != fi.Length || fm.Written != fi.LastWriteTimeUtc)
                        {
                            fm = new FileMinutes { Length = fi.Length, Written = fi.LastWriteTimeUtc, Quiet = QuietMinutes(LogReplay.Load(new[] { f })) };
                            cache[f] = fm;
                        }
                        foreach (var m in fm.Quiet) if (m >= from) quiet.Add(m);
                    }
            return FromEnergy(dir, quiet, from);
        }

        /// <summary>Минуты, все замеры которых — от батареи без ввода ≥ 60 с (в логах без idle_s тихих минут нет).</summary>
        public static HashSet<DateTime> QuietMinutes(LogReplay log)
        {
            var ok = new Dictionary<DateTime, int>();  // минута → число замеров; −1 — не тихая
            LogReplay.Row row;
            while ((row = log.Next()) != null)
            {
                var s = row.Sample;
                var t = new DateTime(s.Time.Year, s.Time.Month, s.Time.Day, s.Time.Hour, s.Time.Minute, 0);
                int n;
                ok.TryGetValue(t, out n);
                if (n < 0) continue;
                bool quiet = !double.IsNaN(s.DischargeW) && !double.IsNaN(s.IdleS) && s.IdleS >= 60;
                ok[t] = quiet ? n + 1 : -1;
            }
            var r = new HashSet<DateTime>();
            foreach (var kv in ok) if (kv.Value >= 3) r.Add(kv.Key);
            return r;
        }

        public static Result FromEnergy(string dir, HashSet<DateTime> quiet, DateTime from)
        {
            var r = new Result();
            if (quiet.Count == 0 || !Directory.Exists(dir)) return r;
            var inv = CultureInfo.InvariantCulture;
            var wh = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            var minutes = new HashSet<DateTime>();
            double batteryWh = 0, seconds = 0;
            foreach (var f in Directory.GetFiles(dir, "process_energy_*.csv"))
            {
                DateTime day;
                string stamp = Path.GetFileNameWithoutExtension(f).Substring("process_energy_".Length);
                if (DateTime.TryParseExact(stamp, "yyyyMMdd", inv, DateTimeStyles.None, out day) && day < from.Date) continue;
                try
                {
                    using (var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    using (var sr = new StreamReader(fs))
                    {
                        sr.ReadLine();
                        string line;
                        while ((line = sr.ReadLine()) != null)
                        {
                            var c = line.Split(',');
                            DateTime m;
                            double v;
                            if (c.Length < 6 || !DateTime.TryParseExact(c[0], "yyyy-MM-ddTHH:mm", inv, DateTimeStyles.None, out m) || !quiet.Contains(m)) continue;
                            if (!double.TryParse(c[2], NumberStyles.Float, inv, out v)) continue;
                            if (c[1] == ProcessEnergyLog.BatteryRow)
                            {
                                double s;
                                if (double.TryParse(c[5], NumberStyles.Float, inv, out s)) seconds += s;
                                batteryWh += v;
                                minutes.Add(m);
                                continue;
                            }
                            double old;
                            wh.TryGetValue(c[1], out old);
                            wh[c[1]] = old + v;
                        }
                    }
                }
                catch (IOException) { }
            }
            r.Minutes = minutes.Count;
            if (seconds <= 0) return r;
            double hours = seconds / 3600;
            r.BatteryW = batteryWh / hours;
            foreach (var kv in wh) r.Top.Add(new KeyValuePair<string, double>(kv.Key, kv.Value / hours));
            r.Top.Sort((a, b) => b.Value.CompareTo(a.Value));
            return r;
        }
    }
}
