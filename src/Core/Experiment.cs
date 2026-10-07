using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace BatteryCheck
{
    /// <summary>Одна минута для сравнения «до / после».</summary>
    sealed class ExpMinute
    {
        public DateTime T;
        public int N;
        public bool Ok = true;     // сопоставимая: от батареи, без ввода, та же яркость, окно на экране
        public double SumW, SumCpu;
        public int CpuN, AwakeN, Wakes;
        public int Brightness = -1;

        public double W { get { return SumW / N; } }
        public double Cpu { get { return CpuN > 0 ? SumCpu / CpuN : double.NaN; } }
        public double AwakeShare { get { return (double)AwakeN / N; } }
    }

    /// <summary>
    /// Поминутный учёт для проверки «до / после»: держит последние KeepMinutes минут, чтобы «до» можно было взять
    /// из только что прошедшего времени. Минута сопоставима, если все её замеры — разряд с известной мощностью,
    /// ввода не было ≥ 60 с, яркость одна, окно программы на экране (иначе меняются и опрос, и отрисовка).
    /// </summary>
    sealed class MinuteRecorder
    {
        public const int KeepMinutes = 30;
        const double QuietInputS = 60;
        const int MinSamples = 3;

        readonly List<ExpMinute> minutes = new List<ExpMinute>();
        int prevDState = -1;

        public void Add(Sample s, int brightness, bool onScreen)
        {
            var t = new DateTime(s.Time.Year, s.Time.Month, s.Time.Day, s.Time.Hour, s.Time.Minute, 0);
            var m = minutes.Count > 0 ? minutes[minutes.Count - 1] : null;
            if (m == null || m.T != t)
            {
                m = new ExpMinute { T = t, Brightness = brightness };
                minutes.Add(m);
                while (minutes.Count > KeepMinutes) minutes.RemoveAt(0);
            }
            m.N++;
            if (prevDState == 3 && s.GpuDState >= 0 && s.GpuDState != 3) m.Wakes++;
            if (s.GpuDState >= 0) prevDState = s.GpuDState;
            if (s.GpuDState >= 0 && s.GpuDState != 3) m.AwakeN++;

            double w = s.DischargeW;
            if (double.IsNaN(w) || !onScreen) { m.Ok = false; return; }
            if (!double.IsNaN(s.IdleS) && s.IdleS < QuietInputS) m.Ok = false;
            if (brightness >= 0 && m.Brightness >= 0 && brightness != m.Brightness) m.Ok = false;
            if (m.Brightness < 0) m.Brightness = brightness;
            m.SumW += w;
            if (!double.IsNaN(s.CpuPkgW)) { m.SumCpu += s.CpuPkgW; m.CpuN++; }
        }

        /// <summary>Отметить минуты с from как несопоставимые (например, шёл замер экрана).</summary>
        public void Spoil(DateTime from)
        {
            foreach (var m in minutes) if (m.T.AddMinutes(1) > from) m.Ok = false;
        }

        /// <summary>Сопоставимые минуты, начавшиеся не раньше from и закончившиеся не позже now (текущая — не готова).</summary>
        public List<ExpMinute> OkMinutes(DateTime from, DateTime now)
        {
            var r = new List<ExpMinute>();
            foreach (var m in minutes)
                if (m.Ok && m.N >= MinSamples && m.T >= from && m.T.AddMinutes(1) <= now) r.Add(m);
            return r;
        }

        /// <summary>Последние count сопоставимых минут подряд по времени (без разрывов больше 2 минут) — для «до» из прошлого.</summary>
        public List<ExpMinute> RecentOk(int count, DateTime now)
        {
            var ok = OkMinutes(DateTime.MinValue, now);
            var r = new List<ExpMinute>();
            for (int i = ok.Count - 1; i >= 0 && r.Count < count; i--)
            {
                if (r.Count > 0 && (r[r.Count - 1].T - ok[i].T).TotalMinutes > 2) break;
                r.Add(ok[i]);
            }
            if (r.Count > 0 && (now - r[0].T).TotalMinutes > 3) r.Clear();  // свежими должны быть: «до» — это сейчас
            r.Reverse();
            return r;
        }
    }

    sealed class PhaseStats
    {
        public int Minutes;
        public double W = double.NaN, WErr = double.NaN, Cpu = double.NaN, AwakePct = double.NaN, WakesPerHour = double.NaN;
        public DateTime From, To;

        public static PhaseStats Of(List<ExpMinute> list)
        {
            var p = new PhaseStats { Minutes = list.Count };
            if (list.Count == 0) return p;
            p.From = list[0].T;
            p.To = list[list.Count - 1].T.AddMinutes(1);
            double sum = 0, sum2 = 0, cpu = 0, awake = 0;
            int cpuN = 0, wakes = 0;
            foreach (var m in list)
            {
                sum += m.W;
                sum2 += m.W * m.W;
                if (!double.IsNaN(m.Cpu)) { cpu += m.Cpu; cpuN++; }
                awake += m.AwakeShare;
                wakes += m.Wakes;
            }
            int n = list.Count;
            p.W = sum / n;
            p.WErr = n > 1 ? Math.Sqrt(Math.Max(0, (sum2 - sum * sum / n) / (n - 1)) / n) : double.NaN;
            p.Cpu = cpuN > 0 ? cpu / cpuN : double.NaN;
            p.AwakePct = awake / n * 100;
            p.WakesPerHour = wakes * 60.0 / n;
            return p;
        }
    }

    /// <summary>Итог сравнения: разница средних по минутам и её погрешность (стандартные ошибки складываются квадратично).</summary>
    sealed class ExperimentResult
    {
        public string Title;
        public DateTime At;
        public PhaseStats Before, After;
        public double DeltaW, DeltaErr, GainMin = double.NaN;

        /// <summary>Эффект найден: разница больше двух погрешностей и хотя бы 0,3 Вт.</summary>
        public bool Found { get { return !double.IsNaN(DeltaErr) && Math.Abs(DeltaW) > 2 * DeltaErr && Math.Abs(DeltaW) >= 0.3; } }

        public static ExperimentResult Compare(string title, PhaseStats before, PhaseStats after, double fullWh)
        {
            var r = new ExperimentResult { Title = title, At = DateTime.Now, Before = before, After = after };
            r.DeltaW = after.W - before.W;
            r.DeltaErr = Math.Sqrt(Sq(before.WErr) + Sq(after.WErr));
            if (fullWh > 0 && before.W > 1 && after.W > 1) r.GainMin = (fullWh / after.W - fullWh / before.W) * 60;
            return r;
        }

        static double Sq(double v) { return double.IsNaN(v) ? double.NaN : v * v; }

        /// <summary>Главная строка: «−2.3 ± 0.6 W · 19.5 → 17.2 W · ≈ +35 min».</summary>
        public string Headline()
        {
            string delta = string.Format("{0:+0.0;−0.0} ± {1:0.0} {2}", DeltaW, DeltaErr, Fmt.WUnit);
            string text = string.Format("{0} · {1:0.0} → {2:0.0} {3}", delta, Before.W, After.W, Fmt.WUnit);
            if (!Found) return text + L.T(" · no difference found", " · різниці не знайдено");
            if (!double.IsNaN(GainMin) && Math.Abs(GainMin) >= 1)
                text += " · ≈ " + (GainMin > 0 ? "+" : "−") + Fmt.Hours(Math.Abs(GainMin) / 60);
            return text;
        }

        /// <summary>Подробности: процессор, видеокарта, число минут.</summary>
        public string Details()
        {
            var parts = new List<string>();
            if (!double.IsNaN(Before.Cpu) && !double.IsNaN(After.Cpu))
                parts.Add(string.Format(L.T("CPU {0:0.0} → {1:0.0} W", "процесор {0:0.0} → {1:0.0} Вт"), Before.Cpu, After.Cpu));
            if (Before.AwakePct > 0.5 || After.AwakePct > 0.5)
                parts.Add(string.Format(L.T("GPU awake {0:0} → {1:0} %", "відеокарта не спить {0:0} → {1:0} %"), Before.AwakePct, After.AwakePct));
            if (Before.WakesPerHour > 0 || After.WakesPerHour > 0)
                parts.Add(string.Format(L.T("wake-ups {0:0} → {1:0} per hour", "пробуджень {0:0} → {1:0} на годину"), Before.WakesPerHour, After.WakesPerHour));
            parts.Add(string.Format(L.T("{0} + {1} quiet min", "{0} + {1} тихих хв"), Before.Minutes, After.Minutes));
            return string.Join(" · ", parts);
        }
    }

    /// <summary>Журнал проверок: logs\experiments.csv.</summary>
    static class ExperimentLog
    {
        const string Header = "time,title,before_w,before_err,after_w,after_err,delta_w,delta_err,found,before_cpu_w,after_cpu_w," +
                              "before_awake_pct,after_awake_pct,before_wakes_h,after_wakes_h,before_min,after_min,gain_min";
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public static void Append(string dir, ExperimentResult r)
        {
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "experiments.csv");
            bool header = !File.Exists(path);
            using (var sw = new StreamWriter(path, true, new UTF8Encoding(false)))
            {
                if (header) sw.WriteLine(Header);
                sw.WriteLine(string.Join(",", new[]
                {
                    r.At.ToString("yyyy-MM-ddTHH:mm:ss", Inv), "\"" + (r.Title ?? "").Replace("\"", "'") + "\"",
                    F(r.Before.W), F(r.Before.WErr), F(r.After.W), F(r.After.WErr), F(r.DeltaW), F(r.DeltaErr), r.Found ? "1" : "0",
                    F(r.Before.Cpu), F(r.After.Cpu), F(r.Before.AwakePct), F(r.After.AwakePct),
                    F(r.Before.WakesPerHour), F(r.After.WakesPerHour),
                    r.Before.Minutes.ToString(Inv), r.After.Minutes.ToString(Inv), F(r.GainMin),
                }));
            }
        }

        /// <summary>Последние count проверок, новые первыми.</summary>
        public static List<ExperimentResult> Load(string dir, int count)
        {
            var list = new List<ExperimentResult>();
            string path = Path.Combine(dir, "experiments.csv");
            if (!File.Exists(path)) return list;
            foreach (var line in File.ReadAllLines(path))
            {
                if (line.StartsWith("time,")) continue;
                int q1 = line.IndexOf('"'), q2 = line.LastIndexOf('"');
                if (q1 < 0 || q2 <= q1) continue;
                DateTime t;
                if (!DateTime.TryParseExact(line.Substring(0, q1).TrimEnd(','), "yyyy-MM-ddTHH:mm:ss", Inv, DateTimeStyles.None, out t)) continue;
                var c = line.Substring(q2 + 1).TrimStart(',').Split(',');
                if (c.Length < 16) continue;
                var r = new ExperimentResult
                {
                    At = t, Title = line.Substring(q1 + 1, q2 - q1 - 1),
                    Before = new PhaseStats { W = N(c[0]), WErr = N(c[1]), Cpu = N(c[7]), AwakePct = N(c[9]), WakesPerHour = N(c[11]), Minutes = (int)N(c[13]) },
                    After = new PhaseStats { W = N(c[2]), WErr = N(c[3]), Cpu = N(c[8]), AwakePct = N(c[10]), WakesPerHour = N(c[12]), Minutes = (int)N(c[14]) },
                    DeltaW = N(c[4]), DeltaErr = N(c[5]), GainMin = N(c[15]),
                };
                list.Add(r);
            }
            list.Reverse();
            if (list.Count > count) list.RemoveRange(count, list.Count - count);
            return list;
        }

        static string F(double v) { return double.IsNaN(v) || double.IsInfinity(v) ? "" : v.ToString("0.###", Inv); }

        static double N(string s)
        {
            double v;
            return double.TryParse(s, NumberStyles.Float, Inv, out v) ? v : double.NaN;
        }
    }
}
