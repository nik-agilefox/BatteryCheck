using System;
using System.Collections.Generic;
using System.IO;

namespace BatteryCheck
{
    /// <summary>Простой этой машины: расход в самые тихие минуты работы от батареи.</summary>
    sealed class Baseline
    {
        public const int NeededMinutes = 20;

        public int QuietMinutes;           // тихих минут найдено
        public double TotalW = double.NaN, CpuPkgW = double.NaN, RestW = double.NaN, ScreenW = double.NaN;
        public DateTime From, To;          // период логов
        public bool InputKnown;            // в логах есть idle_s — тихие минуты отобраны и по отсутствию ввода

        public bool Valid { get { return QuietMinutes >= NeededMinutes && !double.IsNaN(TotalW); } }
    }

    /// <summary>«Лишние ватты»: на сколько текущий расход выше простоя и из чего это складывается.</summary>
    sealed class Excess
    {
        /// <summary>
        /// Каждый ватт процессора добавляет к «остальному» ещё ~0,75 Вт (преобразователи питания, память) —
        /// найдено при замерах экрана. Поэтому процессор выше простоя считается с этим множителем.
        /// </summary>
        public const double CpuOverhead = 1.75;

        public double NowW, BaseW;
        public double TotalW, GpuW, CpuW = double.NaN, ScreenW = double.NaN, OtherW;

        public bool AtIdle { get { return TotalW < 0.5; } }

        public static Excess Compute(Baseline b, double nowW, double cpuPkgW, double gpuW, double screenW)
        {
            var e = new Excess { NowW = nowW, BaseW = b.TotalW, TotalW = nowW - b.TotalW };
            e.GpuW = double.IsNaN(gpuW) ? 0 : Math.Max(0, gpuW);  // в тихие минуты видеокарта спит: её простой — 0
            if (!double.IsNaN(cpuPkgW) && !double.IsNaN(b.CpuPkgW)) e.CpuW = Math.Max(0, cpuPkgW - b.CpuPkgW) * CpuOverhead;
            if (!double.IsNaN(screenW) && !double.IsNaN(b.ScreenW)) e.ScreenW = Math.Max(0, screenW - b.ScreenW);
            e.OtherW = e.TotalW - e.GpuW - Nz(e.CpuW) - Nz(e.ScreenW);
            return e;
        }

        static double Nz(double v) { return double.IsNaN(v) ? 0 : v; }

        /// <summary>Короткая строка для плитки мощности.</summary>
        public string Short()
        {
            return AtIdle
                ? string.Format(L.T("at idle level ({0})", "на рівні простою ({0})"), Fmt.W(BaseW))
                : string.Format(L.T("idle {0} · +{1:0.0} {2} extra", "простій {0} · +{1:0.0} {2} зайвих"), Fmt.W(BaseW), TotalW, Fmt.WUnit);
        }

        /// <summary>Разбор для подсказки.</summary>
        public string Details(Baseline b)
        {
            var lines = new List<string> { BaselineText.Describe(b), "" };
            lines.Add(string.Format(L.T("Now {0} (30 s average), {1:+0.0;−0.0} {2} vs idle:", "Зараз {0} (середнє за 30 с), {1:+0.0;−0.0} {2} до простою:"),
                Fmt.W(NowW), TotalW, Fmt.WUnit));
            Add(lines, L.T("discrete GPU awake", "дискретна відеокарта не спить"), GpuW);
            Add(lines, L.T("CPU above idle (incl. ~0.75 W per watt in power delivery and memory)",
                           "процесор понад простій (з ~0,75 Вт на ват у живленні та пам'яті)"), CpuW);
            Add(lines, L.T("screen above idle", "екран понад простій"), ScreenW);
            Add(lines, L.T("other (network, storage, peripherals, rounding)", "інше (мережа, накопичувач, периферія, похибка)"), OtherW);
            return string.Join("\n", lines);
        }

        static void Add(List<string> lines, string name, double w)
        {
            if (double.IsNaN(w) || Math.Abs(w) < 0.1) return;
            lines.Add(string.Format("  {0:+0.0;−0.0} {1} — {2}", w, Fmt.WUnit, name));
        }
    }

    static class BaselineText
    {
        public static string Describe(Baseline b)
        {
            if (!b.Valid)
                return string.Format(L.T(
                    "Idle of this machine: collecting quiet minutes on battery ({0} of {1}). A quiet minute: on battery, the discrete GPU asleep, no keyboard or mouse input.",
                    "Простій цієї машини: збираються тихі хвилини від батареї ({0} з {1}). Тиха хвилина: від батареї, дискретна відеокарта спить, немає вводу з клавіатури й миші."),
                    b.QuietMinutes, Baseline.NeededMinutes);
            return string.Format(L.T(
                "Idle of this machine: {0} — median of the quietest 20 % of {1} quiet minutes on battery over {2:dd.MM}–{3:dd.MM}{4}.",
                "Простій цієї машини: {0} — медіана найтихіших 20 % з {1} тихих хвилин від батареї за {2:dd.MM}–{3:dd.MM}{4}."),
                Fmt.W(b.TotalW), b.QuietMinutes, b.From, b.To,
                b.InputKnown ? "" : L.T(" (older logs have no input data: minutes picked by a sleeping GPU only)",
                                        " (у старих логах немає даних про ввід: хвилини відібрано лише за сплячою відеокартою)"));
        }
    }

    /// <summary>
    /// Эталон простоя по CSV-логам за последние дни. Тихая минута: все замеры минуты — разряд, видеокарта в D3 (если она есть),
    /// ввода не было ≥ 60 с (если это записано), замеров хотя бы 3. Эталон — медианы по самым тихим 20 % таких минут
    /// (не меньше 5): общий расход, процессор, «остальное», экран. Медианы берутся по одному набору минут, поэтому
    /// слагаемые сопоставимы.
    /// </summary>
    static class IdleBaseline
    {
        public const int Days = 14;
        const double QuietInputS = 60;
        const int MinSamplesPerMinute = 3;

        sealed class Minute
        {
            public DateTime T;
            public int N;
            public bool Quiet = true;
            public double Total, Pkg, Gpu, Screen;
            public int PkgN, ScreenN;
        }

        sealed class FileMinutes
        {
            public long Length;
            public DateTime Written;
            public List<Minute> Minutes;
            public bool InputKnown;
        }

        // Поминутные итоги по файлам: законченные логи не меняются, при пересчёте заново разбирается только текущий.
        static readonly Dictionary<string, FileMinutes> cache = new Dictionary<string, FileMinutes>(StringComparer.OrdinalIgnoreCase);
        static readonly object sync = new object();

        public static Baseline Compute(string dirOrFile, DateTime now)
        {
            DateTime from = now.AddDays(-Days);
            var files = new List<string>();
            if (Directory.Exists(dirOrFile))
            {
                foreach (var f in Directory.GetFiles(dirOrFile, "battery_*.csv"))
                    if (File.GetLastWriteTime(f) >= from) files.Add(f);
            }
            else if (File.Exists(dirOrFile)) files.Add(dirOrFile);

            var all = new List<Minute>();
            bool input = false;
            lock (sync)
            {
                foreach (var f in files)
                {
                    var fi = new FileInfo(f);
                    FileMinutes fm;
                    if (!cache.TryGetValue(f, out fm) || fm.Length != fi.Length || fm.Written != fi.LastWriteTimeUtc)
                    {
                        fm = Aggregate(LogReplay.Load(new[] { f }));
                        fm.Length = fi.Length;
                        fm.Written = fi.LastWriteTimeUtc;
                        cache[f] = fm;
                    }
                    all.AddRange(fm.Minutes);
                    input |= fm.InputKnown;
                }
            }
            return Build(all, from, input);
        }

        public static Baseline Compute(LogReplay log, DateTime from)
        {
            var fm = Aggregate(log);
            return Build(fm.Minutes, from, fm.InputKnown);
        }

        static FileMinutes Aggregate(LogReplay log)
        {
            var fm = new FileMinutes { Minutes = new List<Minute>() };
            Minute m = null;
            LogReplay.Row row;
            while ((row = log.Next()) != null)
            {
                var s = row.Sample;
                var t = new DateTime(s.Time.Year, s.Time.Month, s.Time.Day, s.Time.Hour, s.Time.Minute, 0);
                if (m == null || m.T != t)
                {
                    m = new Minute { T = t };
                    fm.Minutes.Add(m);
                }
                m.N++;
                double w = s.DischargeW;
                if (double.IsNaN(w)) { m.Quiet = false; continue; }
                if (s.GpuDState >= 0 && s.GpuDState != 3) m.Quiet = false;
                if (!double.IsNaN(s.IdleS))
                {
                    fm.InputKnown = true;
                    if (s.IdleS < QuietInputS) m.Quiet = false;
                }
                m.Total += w;
                m.Gpu += double.IsNaN(s.GpuW) ? 0 : s.GpuW;
                if (!double.IsNaN(s.CpuPkgW)) { m.Pkg += s.CpuPkgW; m.PkgN++; }
                if (!double.IsNaN(s.ScreenW)) { m.Screen += s.ScreenW; m.ScreenN++; }
            }
            return fm;
        }

        static Baseline Build(List<Minute> minutes, DateTime from, bool inputKnown)
        {
            var b = new Baseline { InputKnown = inputKnown };
            // Одна минута может быть в двух логах (две копии программы или стык файлов) — складываем, не меняя кэш.
            var sorted = new List<Minute>(minutes);
            sorted.Sort((x, y) => x.T.CompareTo(y.T));
            var merged = new List<Minute>();
            foreach (var x in sorted)
            {
                if (x.T < from) continue;
                var last = merged.Count > 0 ? merged[merged.Count - 1] : null;
                if (last == null || last.T != x.T)
                {
                    merged.Add(new Minute { T = x.T, N = x.N, Quiet = x.Quiet, Total = x.Total, Pkg = x.Pkg, Gpu = x.Gpu, Screen = x.Screen, PkgN = x.PkgN, ScreenN = x.ScreenN });
                    continue;
                }
                last.N += x.N; last.Quiet &= x.Quiet; last.Total += x.Total; last.Pkg += x.Pkg; last.Gpu += x.Gpu;
                last.Screen += x.Screen; last.PkgN += x.PkgN; last.ScreenN += x.ScreenN;
            }

            var quiet = new List<Minute>();
            foreach (var x in merged)
                if (x.Quiet && x.N >= MinSamplesPerMinute)
                {
                    quiet.Add(x);
                    if (b.From == DateTime.MinValue || x.T < b.From) b.From = x.T;
                    if (x.T > b.To) b.To = x.T;
                }
            b.QuietMinutes = quiet.Count;
            if (quiet.Count < Baseline.NeededMinutes) return b;

            quiet.Sort((x, y) => (x.Total / x.N).CompareTo(y.Total / y.N));
            int k = Math.Max(5, (int)Math.Ceiling(quiet.Count * 0.2));
            var low = quiet.GetRange(0, k);
            b.TotalW = Median(low, x => x.Total / x.N);
            b.CpuPkgW = Median(low, x => x.PkgN == x.N ? x.Pkg / x.PkgN : double.NaN);
            b.RestW = Median(low, x => x.PkgN == x.N ? (x.Total - x.Pkg - x.Gpu) / x.N : double.NaN);
            b.ScreenW = Median(low, x => x.ScreenN > 0 ? x.Screen / x.ScreenN : double.NaN);
            return b;
        }

        static double Median(List<Minute> list, Func<Minute, double> value)
        {
            var v = new List<double>();
            foreach (var x in list)
            {
                double d = value(x);
                if (!double.IsNaN(d)) v.Add(d);
            }
            if (v.Count == 0) return double.NaN;
            v.Sort();
            return v.Count % 2 == 1 ? v[v.Count / 2] : (v[v.Count / 2 - 1] + v[v.Count / 2]) / 2;
        }
    }
}
