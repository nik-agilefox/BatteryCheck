using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace BatteryCheck
{
    /// <summary>
    /// Записанные CSV-логи (battery_*.csv) как источник замеров вместо датчиков: Sampler прогоняет их через ту же
    /// логику — сглаживание, сессию разряда, «остальное», прогноз. Нужен, чтобы проверять расчёты на настоящих данных
    /// без железа, в том числе на логах с чужих ноутбуков. Логи разных версий читаются по заголовку: нет колонки —
    /// нет значения. Несколько файлов (и одновременно работавших копий) сливаются по времени, повторы отбрасываются.
    /// </summary>
    sealed class LogReplay
    {
        public sealed class Row
        {
            public Sample Sample;
            public uint FullMwh;
        }

        readonly List<Row> rows = new List<Row>();
        int pos;

        public int Count { get { return rows.Count; } }
        public bool HasRapl { get; private set; }
        public bool HasGpu { get; private set; }
        public string Source { get; private set; }

        /// <summary>Файл или папка с battery_*.csv.</summary>
        public static LogReplay Load(string path)
        {
            var files = Directory.Exists(path) ? Directory.GetFiles(path, "battery_*.csv") : new[] { path };
            var r = Load(files);
            r.Source = Path.GetFullPath(path);
            return r;
        }

        public static LogReplay Load(IEnumerable<string> paths)
        {
            var r = new LogReplay { Source = "" };
            var files = new List<string>(paths);
            files.Sort(StringComparer.OrdinalIgnoreCase);
            foreach (var f in files) r.ReadFile(f);
            r.rows.Sort((a, b) => a.Sample.Time.CompareTo(b.Sample.Time));
            var unique = new List<Row>(r.rows.Count);
            foreach (var row in r.rows)
                if (unique.Count == 0 || (row.Sample.Time - unique[unique.Count - 1].Sample.Time).TotalSeconds >= 0.5)
                    unique.Add(row);
            r.rows.Clear();
            r.rows.AddRange(unique);
            return r;
        }

        /// <summary>Следующий замер или null, если логи кончились.</summary>
        public Row Next()
        {
            return pos < rows.Count ? rows[pos++] : null;
        }

        /// <summary>Время следующего замера (для паузы при воспроизведении в реальном темпе); null — конец.</summary>
        public DateTime? PeekTime
        {
            get { return pos < rows.Count ? rows[pos].Sample.Time : (DateTime?)null; }
        }

        void ReadFile(string path)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var reader = new StreamReader(fs))
            {
                var head = (reader.ReadLine() ?? "").Split(',');
                Func<string, int> col = name => Array.IndexOf(head, name);
                int iT = col("time"), iState = col("power_state"), iAc = col("on_ac"), iChg = col("charging"), iDis = col("discharging"),
                    iCap = col("capacity_mwh"), iFull = col("full_mwh"), iVolt = col("voltage_mv"), iRate = col("rate_mw"),
                    iPkg = col("cpu_pkg_w"), iCores = col("cpu_cores_w"), iIgpu = col("igpu_w"), iDram = col("dram_w"),
                    iGpu = col("dgpu_w"), iUtil = col("dgpu_util_pct"), iP = col("dgpu_pstate"), iD = col("dgpu_dstate"),
                    iDisp = col("displays"), iDispGpu = col("displays_on_dgpu"),
                    iIdle = col("idle_s"), iBright = col("brightness_pct"), iScreen = col("screen_w"), iDisabled = col("dgpu_disabled"), iC3 = col("cpu_c3_pct"), iTimer = col("timer_ms"),
                    iDispState = col("display_state");
                if (iT < 0) return;
                var inv = CultureInfo.InvariantCulture;
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    var c = line.Split(',');
                    DateTime t;
                    if (c.Length <= iT || !DateTime.TryParseExact(c[iT], "yyyy-MM-ddTHH:mm:ss.fff", inv, DateTimeStyles.None, out t)) continue;

                    uint state;
                    if (iState < 0 || !uint.TryParse(Str(c, iState), NumberStyles.Integer, inv, out state))
                        state = (Str(c, iAc) == "1" ? 1u : 0) | (Str(c, iDis) == "1" ? 2u : 0) | (Str(c, iChg) == "1" ? 4u : 0);
                    var s = new Sample
                    {
                        Time = t,
                        TimeUtc = t.ToUniversalTime(),
                        Battery = new BatteryStatus
                        {
                            PowerState = state,
                            Capacity = UInt(c, iCap, 0xFFFFFFFF),
                            Voltage = UInt(c, iVolt, 0xFFFFFFFF),
                            Rate = Int(c, iRate, int.MinValue),
                        },
                        CpuPkgW = Num(c, iPkg), CpuCoresW = Num(c, iCores), IgpuW = Num(c, iIgpu), DramW = Num(c, iDram),
                        GpuW = Num(c, iGpu),
                        GpuUtil = Int(c, iUtil, -1), GpuPState = Int(c, iP, -1), GpuDState = Int(c, iD, -1),
                        Displays = Int(c, iDisp, -1), DisplaysOnGpu = Int(c, iDispGpu, -1),
                        IdleS = Num(c, iIdle), BrightnessPct = Int(c, iBright, -1), ScreenW = Num(c, iScreen), GpuDisabled = Str(c, iDisabled) == "1", DeepSleepPct = Num(c, iC3), TimerMs = Num(c, iTimer),
                        DisplayState = Int(c, iDispState, -1),
                    };
                    if (!double.IsNaN(s.CpuPkgW)) HasRapl = true;
                    if (!double.IsNaN(s.GpuW) || s.GpuDState >= 0) HasGpu = true;
                    rows.Add(new Row { Sample = s, FullMwh = UInt(c, iFull, 0) });
                }
            }
        }

        static string Str(string[] c, int i)
        {
            return i >= 0 && i < c.Length ? c[i] : "";
        }

        static double Num(string[] c, int i)
        {
            double v;
            return double.TryParse(Str(c, i), NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? v : double.NaN;
        }

        static uint UInt(string[] c, int i, uint missing)
        {
            uint v;
            return uint.TryParse(Str(c, i), NumberStyles.Integer, CultureInfo.InvariantCulture, out v) ? v : missing;
        }

        static int Int(string[] c, int i, int missing)
        {
            int v;
            return int.TryParse(Str(c, i), NumberStyles.Integer, CultureInfo.InvariantCulture, out v) ? v : missing;
        }
    }
}
