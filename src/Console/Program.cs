using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace BatteryCheck
{
    sealed class Options
    {
        public SamplerOptions Sampler = new SamplerOptions();
        public bool Plain;
        public int Count;  // 0 — работать до выхода
        public string ReplayPath;  // --replay: воспроизвести логи вместо датчиков
        public double Speed;       // во сколько раз быстрее реального времени; 0 — без пауз, только сводка
    }

    static class Program
    {
        static readonly ManualResetEvent stop = new ManualResetEvent(false);

        static int Main(string[] args)
        {
            L.Apply(L.LoadSaved());  // язык — тот же, что выбран в оконной версии; --lang en|ua меняет его на этот запуск

            Options opt;
            try
            {
                opt = ParseArgs(args);
            }
            catch (ArgumentException e)
            {
                Console.Error.WriteLine(e.Message);
                PrintUsage();
                return 2;
            }
            if (opt == null)
            {
                PrintUsage();
                return 0;
            }

            Console.OutputEncoding = Encoding.UTF8;
            if (Console.IsOutputRedirected) opt.Plain = true;
            if (opt.ReplayPath != null) return Replay(opt);
            if (!opt.Plain) Console.WriteLine(L.T("Initializing sensors…", "Ініціалізація датчиків…"));

            Sampler sampler;
            try
            {
                sampler = new Sampler(opt.Sampler);
            }
            catch (InvalidOperationException e)
            {
                Console.Error.WriteLine(e.Message);
                return 1;
            }

            Console.CancelKeyPress += (s, e) => { e.Cancel = true; stop.Set(); };
            if (!Console.IsInputRedirected) StartKeyReader(sampler);

            Snapshot last = null;
            try
            {
                if (!opt.Plain)
                {
                    Console.CursorVisible = false;
                    Console.Clear();
                }
                sampler.Run(snap => opt.Sampler.IntervalMs, opt.Count, stop, null, snap =>
                {
                    last = snap;
                    if (opt.Plain) Console.WriteLine(PlainLine(snap.Sample));
                    else Screen.Draw(BuildScreen(snap));
                });
            }
            finally
            {
                sampler.Dispose();
                if (!opt.Plain) Console.CursorVisible = true;
            }

            if (!opt.Plain)
            {
                Console.WriteLine();
                Console.WriteLine();
            }
            if (last != null && last.SessionS > 0)
                Console.WriteLine(L.T("Discharge session: {0}, {1:0.00} Wh, average power {2:0.0} W",
                                      "Сесія розряду: {0}, {1:0.00} Вт·год, середня потужність {2:0.0} Вт"),
                    Fmt.Duration(last.SessionS), last.SessionByRateWh, last.SessionAvgW);
            if (sampler.LogPath != null)
                Console.WriteLine(L.T("Log saved: ", "Лог збережено: ") + sampler.LogPath);
            return 0;
        }

        /// <summary>
        /// Воспроизведение записанных логов через ту же логику, что и живой опрос. С --speed N — экран в N раз быстрее
        /// реального времени, без него — только итог: сессия разряда и честность индикатора заряда по каждому разряду.
        /// </summary>
        static int Replay(Options opt)
        {
            LogReplay replay;
            try
            {
                replay = LogReplay.Load(opt.ReplayPath);
            }
            catch (IOException e)
            {
                Console.Error.WriteLine(e.Message);
                return 1;
            }
            if (replay.Count == 0)
            {
                Console.Error.WriteLine(L.T("No samples found in ", "Не знайдено замірів у ") + opt.ReplayPath);
                return 1;
            }

            Console.CancelKeyPress += (s, e) => { e.Cancel = true; stop.Set(); };
            bool screen = opt.Speed > 0 && !opt.Plain;
            if (screen) { Console.CursorVisible = false; Console.Clear(); }
            Snapshot first = null, last = null;
            int n = 0;
            using (var sampler = new Sampler(replay))
            {
                sampler.RunReplay(opt.Speed, stop, snap =>
                {
                    if (first == null) first = snap;
                    last = snap;
                    n++;
                    if (screen) Screen.Draw(BuildScreen(snap));
                    else if (opt.Plain && opt.Speed > 0) Console.WriteLine(PlainLine(snap.Sample));
                });
            }
            if (screen) { Console.CursorVisible = true; Console.WriteLine(); Console.WriteLine(); }

            Console.WriteLine(L.T("Replay: ", "Відтворення: ") + replay.Source);
            Console.WriteLine(L.T("  samples {0} · {1:yyyy-MM-dd HH:mm} – {2:yyyy-MM-dd HH:mm}", "  замірів {0} · {1:yyyy-MM-dd HH:mm} – {2:yyyy-MM-dd HH:mm}"),
                n, first.Sample.Time, last.Sample.Time);
            if (last.SessionS > 0)
                Console.WriteLine(L.T("  on battery {0} · by power {1:0.00} Wh · by remaining charge {2:0.00} Wh · average {3}",
                                      "  від батареї {0} · за потужністю {1:0.00} Вт·год · за залишком {2:0.00} Вт·год · середня {3}"),
                    Fmt.Hours(last.SessionS / 3600), last.SessionByRateWh, last.SessionByCapacityWh, Fmt.W(last.SessionAvgW));

            var sessions = DischargeHistory.Load(opt.ReplayPath);
            Console.WriteLine();
            Console.WriteLine(L.T("Discharges ({0}):", "Розряди ({0}):"), sessions.Count);
            for (int i = sessions.Count - 1; i >= 0; i--)
            {
                var s = sessions[i];
                double r = s.GaugeRatio;
                Console.WriteLine("  {0:yyyy-MM-dd HH:mm}  {1,12}  {2,3:0}→{3,3:0} %  {4,9}  {5}  {6}{7}",
                    s.Start, Fmt.Hours(s.ActiveS / 3600), s.SocStart, s.SocEnd, Fmt.W(s.AvgW),
                    L.T("indicator ", "індикатор ") + (double.IsNaN(r) ? "—" : r.ToString("0.000")),
                    r < DischargeHistory.FasterThreshold ? "⚠" : "",
                    s.Jumps > 0 ? string.Format(L.T(" jumps {0}", " стрибків {0}"), s.Jumps) : "");
            }
            Console.WriteLine();
            Console.WriteLine(DischargeHistory.Evaluate(sessions).Describe());

            var bl = IdleBaseline.Compute(LogReplay.Load(opt.ReplayPath), DateTime.MinValue);  // за весь период логов
            Console.WriteLine();
            Console.WriteLine(BaselineText.Describe(bl));
            if (bl.Valid)
                Console.WriteLine(L.T("  CPU package {0} · rest {1} · screen {2}", "  процесор {0} · решта {1} · екран {2}"),
                    Fmt.W(bl.CpuPkgW), Fmt.W(bl.RestW), Fmt.W(bl.ScreenW));
            return 0;
        }

        static List<string> BuildScreen(Snapshot snap)
        {
            var s = snap.Sample;
            var b = s.Battery;
            var info = snap.Info;
            bool rel = info.IsRelative;
            var lines = new List<string>();

            lines.Add(string.Format(" BATTERY CHECK{0,48}", s.Time.ToString("HH:mm:ss")));
            lines.Add(" " + new string('─', 60));

            var name = new List<string>();
            foreach (var part in new[] { info.DeviceName, info.Manufacturer, info.Chemistry })
                if (!string.IsNullOrEmpty(part)) name.Add(part);
            lines.Add(Row(L.T("Battery", "Батарея"), string.Join(" · ", name)));
            lines.Add(Row(L.T("Capacity", "Ємність"), string.Format(L.T("{0} design · {1} full", "{0} паспортна · {1} повна"),
                Fmt.Wh(info.DesignedCapacity, rel), Fmt.Wh(info.FullChargedCapacity, rel))));
            lines.Add(Row(L.T("Wear", "Знос"), string.Format(L.T("{0} · cycles {1}", "{0} · циклів {1}"),
                double.IsNaN(info.WearPercent) ? "—" : info.WearPercent.ToString("0.0") + " %",
                info.CycleCount > 0 ? info.CycleCount.ToString() : L.T("n/a", "н/д"))));
            if (!double.IsNaN(info.TemperatureC))
                lines.Add(Row(L.T("Temperature", "Температура"), info.TemperatureC.ToString("0.0") + " °C"));
            lines.Add("");

            string state = b.Discharging ? L.T("on battery, discharging", "від батареї, розряд")
                         : b.Charging ? L.T("plugged in, charging", "від мережі, заряд")
                         : b.OnLine ? L.T("plugged in, battery idle", "від мережі, батарея в спокої")
                         : L.T("unknown", "невідомо");
            if (b.Critical) state += L.T(" · CRITICAL", " · КРИТИЧНИЙ");
            lines.Add(Row(L.T("Power", "Живлення"), state));
            if (s.Displays >= 0)
                lines.Add(Row(L.T("Displays", "Екрани"), s.DisplaysOnGpu > 0
                    ? string.Format(L.T("{0} · via NVIDIA: {1} (keeps it awake)", "{0} · через NVIDIA: {1} (не дає їй заснути)"),
                        s.Displays, s.DisplaysOnGpu)
                    : s.Displays.ToString()));

            lines.Add(Row(L.T("Charge", "Заряд"), string.Format("{0} {1}  {2}", Fmt.Bar(snap.SocPct, 16),
                double.IsNaN(snap.SocPct) ? "—" : snap.SocPct.ToString("0.0") + " %",
                b.HasCapacity ? Fmt.Wh(b.Capacity, rel) : "—")));
            lines.Add(Row(L.T("Voltage", "Напруга"), string.Format(L.T("{0,-10} current {1}", "{0,-10} струм {1}"),
                b.HasVoltage ? (b.Voltage / 1000.0).ToString("0.000") + L.T(" V", " В") : "—",
                double.IsNaN(b.CurrentA) ? "—" : b.CurrentA.ToString("0.00") + L.T(" A", " А"))));

            if (b.Discharging || b.Charging)
            {
                lines.Add(Row(L.T("Power draw", "Потужність"), string.Format(L.T("{0,-10} avg 30 s {1}", "{0,-10} сер. 30 с {1}"),
                    Fmt.W(Math.Abs(b.RateW)), Fmt.W(snap.BatteryAvgW))));
                lines.Add(Row(b.Discharging ? L.T("Remaining", "Залишилось") : L.T("To full", "До повного"),
                    "≈ " + Fmt.Hours(snap.HoursLeft)));
            }
            else
            {
                lines.Add(Row(L.T("Power draw", "Потужність"), L.T("— (not measured on AC)", "— (від мережі не вимірюється)")));
            }
            lines.Add("");

            lines.Add(string.Format(" {0,-24}{1,9}{2,10}", L.T("COMPONENTS", "КОМПОНЕНТИ"), L.T("now", "зараз"), L.T("avg 10 s", "сер. 10 с")));
            if (snap.RaplAvailable)
            {
                lines.Add(Comp(L.T("CPU", "Процесор"), s.CpuPkgW, snap.Cpu10W, null));
                lines.Add(Comp(L.T("  cores", "  ядра"), s.CpuCoresW, double.NaN, null));
                lines.Add(Comp(L.T("  integrated graphics", "  вбуд. графіка"), s.IgpuW, double.NaN, null));
                if (s.DramW > 0) lines.Add(Comp(L.T("  memory", "  пам'ять"), s.DramW, double.NaN, null));
            }
            else
            {
                lines.Add(L.T(" CPU: RAPL counters unavailable", " Процесор: лічильники RAPL недоступні"));
            }

            if (snap.GpuName == null)
                lines.Add(L.T(" NVIDIA GPU not found", " Відеокарту NVIDIA не знайдено"));
            else if (s.GpuDState == 3)
                lines.Add(Comp(snap.GpuName, 0, snap.Gpu10W, Fmt.GpuState(3)));
            else if (!double.IsNaN(s.GpuW))
                lines.Add(Comp(snap.GpuName, s.GpuW, snap.Gpu10W, string.Format("P{0} · {1}",
                    s.GpuPState >= 0 ? s.GpuPState.ToString() : "?", s.GpuUtil >= 0 ? s.GpuUtil + " %" : "—")));
            else
                lines.Add(Comp(snap.GpuName, double.NaN, double.NaN,
                    snap.GpuPolling ? Fmt.GpuState(0) : L.T("active, not polled", "працює, не опитується")));

            string restLabel = !double.IsNaN(s.GpuW) ? L.T("Rest", "Решта") : L.T("Rest + GPU", "Решта + відеокарта");
            lines.Add(b.Discharging
                ? Comp(restLabel, double.NaN, snap.RestW, null)
                : string.Format(" {0,-24}{1,9}{2,10}", restLabel, "—", "—"));
            lines.Add(b.Discharging
                ? L.T("   screen, SSD, network, board = battery − CPU − GPU", "   екран, SSD, мережа, плата = батарея − CPU − GPU")
                : L.T("   measured only on battery", "   рахується лише від батареї"));
            lines.Add("");

            lines.Add(string.Format(" {0,-24}{1,9}", L.T("DISCHARGE SESSION", "СЕСІЯ РОЗРЯДУ"), Fmt.Duration(snap.SessionS)));
            if (snap.SessionS > 0)
            {
                lines.Add(string.Format(" {0,-24}{1,12}", L.T("Energy by power", "Енергія за потужністю"),
                    snap.SessionByRateWh.ToString("0.00") + " " + Fmt.WhUnit));
                lines.Add(string.Format(" {0,-24}{1,12}", L.T("by remaining charge", "за залишком заряду"),
                    snap.SessionByCapacityWh.ToString("0.00") + " " + Fmt.WhUnit));
                if (!double.IsNaN(snap.SessionMismatchPct))
                    lines.Add(string.Format(" {0,-24}{1,12}", L.T("mismatch", "розбіжність"),
                        snap.SessionMismatchPct.ToString("+0.0;-0.0;0.0") + " %"));
                lines.Add(string.Format(" {0,-24}{1}", L.T("Power avg/min/max", "Потужність сер/мін/макс"),
                    string.Format("{0:0.0} / {1:0.0} / {2:0.0} {3}", snap.SessionAvgW, snap.SessionMinW, snap.SessionMaxW, Fmt.WUnit)));
            }
            else
            {
                lines.Add(L.T(" Unplug the charger to start tracking the discharge.", " Відключіть зарядний пристрій, щоб почати облік розряду."));
            }
            lines.Add("");

            if (snap.Error != null) lines.Add(" ! " + snap.Error);
            lines.Add(L.T(" Log: ", " Лог: ") + (snap.LogPath != null ? @"logs\" + Path.GetFileName(snap.LogPath) : L.T("off", "вимкнено")));
            lines.Add(L.T(" Q — quit · R — reset session", " Q — вихід · R — скинути сесію"));
            return lines;
        }

        static string Row(string label, string value)
        {
            return string.Format(" {0,-12}{1}", label, value);
        }

        static string Comp(string label, double now, double avg, string extra)
        {
            if (label.Length > 23) label = label.Substring(0, 23);
            return string.Format(" {0,-24}{1,9}{2,10}{3}", label, Fmt.W(now), Fmt.W(avg),
                string.IsNullOrEmpty(extra) ? "" : "  " + extra);
        }

        static string PlainLine(Sample s)
        {
            var b = s.Battery;
            var inv = CultureInfo.InvariantCulture;
            return string.Format(inv, "{0:HH:mm:ss} {1} cap={2}mWh volt={3}mV rate={4}mW cpu={5:0.0}W gpu={6:0.0}W gpu_d={7} displays={8}/{9}",
                s.Time, b.Discharging ? "DISCHARGE" : b.Charging ? "CHARGE" : b.OnLine ? "AC" : "?",
                b.Capacity, b.Voltage, b.Rate, s.CpuPkgW, s.GpuW,
                s.GpuDState >= 0 ? "D" + s.GpuDState : "?", s.Displays, s.DisplaysOnGpu);
        }

        static void StartKeyReader(Sampler sampler)
        {
            var t = new Thread(() =>
            {
                while (true)
                {
                    ConsoleKeyInfo k;
                    try { k = Console.ReadKey(true); }
                    catch (InvalidOperationException) { return; }
                    if (k.Key == ConsoleKey.Q || k.Key == ConsoleKey.Escape)
                    {
                        stop.Set();
                        return;
                    }
                    if (k.Key == ConsoleKey.R) sampler.ResetSession();
                }
            });
            t.IsBackground = true;
            t.Start();
        }

        static Options ParseArgs(string[] args)
        {
            var o = new Options();
            bool help = false;  // справку — после разбора, чтобы --lang действовал и на неё
            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i].ToLowerInvariant())
                {
                    case "-h": case "--help": case "/?":
                        help = true;
                        break;
                    case "-i": case "--interval":
                        double sec;
                        if (i + 1 >= args.Length || !double.TryParse(args[++i].Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out sec) || sec < 0.2)
                            throw new ArgumentException(L.T("Interval must be a number of seconds, at least 0.2",
                                                            "Інтервал має бути числом секунд, не меншим за 0.2"));
                        o.Sampler.IntervalMs = (int)Math.Round(sec * 1000);
                        break;
                    case "-n": case "--count":
                        int n;
                        if (i + 1 >= args.Length || !int.TryParse(args[++i], out n) || n < 1)
                            throw new ArgumentException(L.T("--count expects a positive number", "--count очікує додатне число"));
                        o.Count = n;
                        break;
                    case "--log":
                        if (i + 1 >= args.Length) throw new ArgumentException(L.T("--log expects a file path", "--log очікує шлях до файлу"));
                        o.Sampler.LogPath = args[++i];
                        break;
                    case "--lang":
                        string lang = i + 1 < args.Length ? args[++i].ToLowerInvariant() : "";
                        if (lang != "en" && lang != "ua") throw new ArgumentException(L.T("--lang expects en or ua", "--lang очікує en або ua"));
                        L.Apply(lang == "ua");
                        break;
                    case "--replay":
                        if (i + 1 >= args.Length) throw new ArgumentException(L.T("--replay expects a log file or folder", "--replay очікує файл логу або теку"));
                        o.ReplayPath = args[++i];
                        if (!File.Exists(o.ReplayPath) && !Directory.Exists(o.ReplayPath))
                            throw new ArgumentException(L.T("Not found: ", "Не знайдено: ") + o.ReplayPath);
                        break;
                    case "--speed":
                        double speed;
                        if (i + 1 >= args.Length || !double.TryParse(args[++i].Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out speed) || speed <= 0)
                            throw new ArgumentException(L.T("--speed expects a positive number", "--speed очікує додатне число"));
                        o.Speed = speed;
                        break;
                    case "--no-log": o.Sampler.Log = false; break;
                    case "--no-gpu": o.Sampler.Gpu = false; break;
                    case "--plain": o.Plain = true; break;
                    default:
                        throw new ArgumentException(L.T("Unknown option: ", "Невідомий параметр: ") + args[i]);
                }
            }
            return help ? null : o;
        }

        static void PrintUsage()
        {
            Console.WriteLine(L.T("BatteryCheck — real-time laptop power consumption", "BatteryCheck — енергоспоживання ноутбука в реальному часі"));
            Console.WriteLine();
            Console.WriteLine(L.T("  -i, --interval <sec>  polling period (default 1; the battery driver updates once per second)",
                                  "  -i, --interval <сек>  період опитування (типово 1; драйвер батареї оновлюється раз на 1 с)"));
            Console.WriteLine(L.T("      --no-gpu          do not poll NVIDIA power (a sleeping GPU is never polled anyway)",
                                  "      --no-gpu          не опитувати потужність NVIDIA (сплячу відеокарту й так не опитує)"));
            Console.WriteLine(L.T("      --log <file>      CSV log path (default logs\\battery_<date>.csv)",
                                  "      --log <файл>      шлях до CSV-логу (типово logs\\battery_<дата>.csv)"));
            Console.WriteLine(L.T("      --no-log          do not write a log", "      --no-log          не писати лог"));
            Console.WriteLine(L.T("      --plain           line-by-line output instead of a screen", "      --plain           порядковий вивід замість екрана"));
            Console.WriteLine(L.T("  -n, --count <N>       take N samples and exit", "  -n, --count <N>       зробити N вимірів і вийти"));
            Console.WriteLine(L.T("      --lang en|ua      interface language for this run", "      --lang en|ua      мова інтерфейсу для цього запуску"));
            Console.WriteLine(L.T("      --replay <path>   replay recorded logs (a battery_*.csv file or a folder) instead of sensors",
                                  "      --replay <шлях>   відтворити записані логи (файл battery_*.csv або теку) замість датчиків"));
            Console.WriteLine(L.T("      --speed <N>       with --replay: show the screen N times faster than real time",
                                  "      --speed <N>       з --replay: показувати екран у N разів швидше за реальний час"));
        }
    }

    static class Screen
    {
        static int lastLines;

        /// <summary>Перерисовывает экран целиком одной записью, без мерцания.</summary>
        public static void Draw(List<string> lines)
        {
            int width, height;
            try
            {
                width = Math.Max(20, Console.WindowWidth - 1);
                height = Console.WindowHeight;
            }
            catch (IOException)
            {
                return;
            }

            int n = Math.Min(Math.Max(lines.Count, lastLines), Math.Max(1, height - 1));
            var sb = new StringBuilder();
            for (int i = 0; i < n; i++)
            {
                string s = i < lines.Count ? lines[i] : "";
                if (s.Length > width) s = s.Substring(0, width);
                sb.Append(s.PadRight(width));
                if (i < n - 1) sb.Append(Environment.NewLine);
            }
            try
            {
                Console.SetCursorPosition(0, 0);
                Console.Write(sb.ToString());
            }
            catch (IOException) { }
            catch (ArgumentOutOfRangeException) { }
            lastLines = lines.Count;
        }
    }
}
