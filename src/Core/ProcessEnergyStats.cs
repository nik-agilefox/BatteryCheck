using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace BatteryCheck
{
    /// <summary>
    /// Средняя мощность процессов при работе от батареи за последние дни — по logs\process_energy_*.csv:
    /// энергия процесса ÷ всё время от батареи со сбором данных. Для вкладки «Оптимизация» (службы).
    /// </summary>
    static class ProcessEnergyStats
    {
        public sealed class Result
        {
            public double Hours;  // время от батареи со сбором данных
            public readonly Dictionary<string, double> AvgW = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        }

        public static Result Load(string dir, int days, DateTime now)
        {
            var r = new Result();
            if (!Directory.Exists(dir)) return r;
            var inv = CultureInfo.InvariantCulture;
            DateTime from = now.AddDays(-days);
            var wh = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            double seconds = 0;
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
                            if (c.Length < 6 || !DateTime.TryParseExact(c[0], "yyyy-MM-ddTHH:mm", inv, DateTimeStyles.None, out m) || m < from) continue;
                            double v;
                            if (c[1] == ProcessEnergyLog.BatteryRow)
                            {
                                if (double.TryParse(c[5], NumberStyles.Float, inv, out v)) seconds += v;
                                continue;
                            }
                            if (!double.TryParse(c[2], NumberStyles.Float, inv, out v)) continue;
                            double old;
                            wh.TryGetValue(c[1], out old);
                            wh[c[1]] = old + v;
                        }
                    }
                }
                catch (IOException) { }
            }
            r.Hours = seconds / 3600;
            if (r.Hours > 0)
                foreach (var kv in wh) r.AvgW[kv.Key] = kv.Value / r.Hours;
            return r;
        }
    }
}
