// Поиск «будильника» видеокарты без прав администратора: совпадение по времени.
// Состояние сна NVIDIA (диспетчер устройств, карту не будит) — 5 раз в секунду, активность процессора всех процессов
// (NtQuerySystemInformation, видит и службы SYSTEM) — раз в секунду. Для каждого процесса сравнивается его активность
// в окне вокруг пробуждений с активностью в остальное время: «подъём» = во сколько раз больше перед пробуждением.
// Запуск: tools\wake-probe.cmd [секунд] — итог в консоль, сырые данные — в %TEMP%\bc_wakeprobe.csv.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;

namespace BatteryCheck
{
    static class WakeProbe
    {
        const int WindowBefore = 3, WindowAfter = 1;  // секунды вокруг начала пробуждения

        static int Main(string[] args)
        {
            int seconds = args.Length > 0 ? int.Parse(args[0], CultureInfo.InvariantCulture) : 1200;
            var gpu = GpuPowerState.FindNvidia();
            if (gpu == null) { Console.WriteLine("NVIDIA GPU not found"); return 1; }
            var mon = new ProcessMonitor();
            mon.Sample();

            var perSecond = new List<Dictionary<string, double>>();  // секунда → процесс → % ЦП
            var wakes = new List<int>();                                // номер секунды начала пробуждения
            var awakeSeconds = new List<bool>();
            int prev = gpu.Read();
            var sw = Stopwatch.StartNew();
            string self = Process.GetCurrentProcess().ProcessName;
            Console.WriteLine("Probing for {0} s… (D-state {1})", seconds, prev);

            for (int sec = 0; sec < seconds; sec++)
            {
                bool awake = false;
                for (int k = 0; k < 5; k++)
                {
                    int d = gpu.Read();
                    if (d == 0) awake = true;
                    if (prev == 3 && d == 0)
                    {
                        wakes.Add(sec);
                        Console.WriteLine("  wake at {0:HH:mm:ss} (s {1})", DateTime.Now, sec);
                    }
                    if (d >= 0) prev = d;
                    long next = (long)((sec + (k + 1) / 5.0) * 1000);
                    int wait = (int)(next - sw.ElapsedMilliseconds);
                    if (wait > 0) Thread.Sleep(wait);
                }
                var usage = mon.Sample();
                var map = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                if (usage != null)
                    foreach (var u in usage)
                        if (u.CpuPct > 0 && u.Name != self && u.Name != "BatteryCheckGui") map[u.Name] = u.CpuPct;
                perSecond.Add(map);
                awakeSeconds.Add(awake);
            }

            using (var w = new StreamWriter(Path.Combine(Path.GetTempPath(), "bc_wakeprobe.csv")))
            {
                w.WriteLine("second,awake,wake,process,cpu_pct");
                for (int i = 0; i < perSecond.Count; i++)
                    foreach (var kv in perSecond[i])
                        w.WriteLine(string.Join(",", i, awakeSeconds[i] ? 1 : 0, wakes.Contains(i) ? 1 : 0, kv.Key.Replace(",", ";"), kv.Value.ToString("0.0000", CultureInfo.InvariantCulture)));
            }

            Console.WriteLine();
            Console.WriteLine("Wake-ups: {0} in {1:0} min ({2:0} per hour)", wakes.Count, seconds / 60.0, wakes.Count * 3600.0 / seconds);
            if (wakes.Count < 3) { Console.WriteLine("Too few wake-ups for a conclusion."); return 0; }

            // Окна вокруг пробуждений и «спокойные» секунды (видеокарта спит, не рядом с пробуждением) — для сравнения.
            var inWindow = new bool[perSecond.Count];
            foreach (int s in wakes)
                for (int i = Math.Max(0, s - WindowBefore); i <= Math.Min(perSecond.Count - 1, s + WindowAfter); i++) inWindow[i] = true;
            var names = new HashSet<string>(perSecond.SelectMany(m => m.Keys), StringComparer.OrdinalIgnoreCase);
            var rows = new List<Tuple<string, double, double, double>>();
            foreach (var n in names)
            {
                int hits = 0;
                double windowSum = 0;
                foreach (int s in wakes)
                {
                    double sum = 0;
                    for (int i = Math.Max(0, s - WindowBefore); i <= Math.Min(perSecond.Count - 1, s + WindowAfter); i++)
                    {
                        double v;
                        if (perSecond[i].TryGetValue(n, out v)) sum += v;
                    }
                    if (sum > 0) hits++;
                    windowSum += sum;
                }
                double restSum = 0;
                int restN = 0, restActive = 0;
                for (int i = 0; i < perSecond.Count; i++)
                {
                    if (inWindow[i] || awakeSeconds[i]) continue;
                    double v;
                    perSecond[i].TryGetValue(n, out v);
                    restSum += v;
                    restN++;
                    if (v > 0) restActive++;
                }
                int windowLen = WindowBefore + WindowAfter + 1;
                double perWindowWake = windowSum / wakes.Count;
                double perWindowRest = restN > 0 ? restSum / restN * windowLen : 0;
                double hitRate = (double)hits / wakes.Count;
                double restRate = restN > 0 ? 1 - Math.Pow(1 - (double)restActive / restN, windowLen) : 0;  // шанс активности в случайном окне
                double lift = perWindowRest > 0 ? perWindowWake / perWindowRest : (perWindowWake > 0 ? 999 : 0);
                rows.Add(Tuple.Create(n, hitRate, restRate, lift));
            }
            Console.WriteLine();
            Console.WriteLine("Active before wake-ups (≥ 70 % of them) and much more than at other times:");
            Console.WriteLine("  {0,-34} {1,10} {2,12} {3,8}", "process", "wake hits", "random hits", "lift");
            foreach (var r in rows.Where(r => r.Item2 >= 0.7).OrderByDescending(r => r.Item2 - r.Item3).ThenByDescending(r => r.Item4).Take(15))
                Console.WriteLine("  {0,-34} {1,9:0} % {2,11:0} % {3,7:0.0}×", r.Item1, r.Item2 * 100, r.Item3 * 100, r.Item4);
            return 0;
        }
    }
}
