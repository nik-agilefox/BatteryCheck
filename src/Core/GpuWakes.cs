using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace BatteryCheck
{
    /// <summary>
    /// Формат журнала logs\gpu_wakes.csv. flags — признаки для правил, не зависящие от языка причины:
    /// self — разбудила сама программа (замер экрана), unknown — причина не найдена.
    /// cause — текст на языке интерфейса в момент записи.
    /// </summary>
    static class GpuWakeLog
    {
        public const string Header = "start,end,seconds,cost_wh,flags,cause";
        const string OldHeader = "start,end,seconds,cost_wh,cause";

        /// <summary>Журнал прежнего формата (без flags, причины по-русски) переписывается в новый один раз.</summary>
        public static void Migrate(string path)
        {
            if (!File.Exists(path)) return;
            string[] lines = File.ReadAllLines(path);
            if (lines.Length == 0 || lines[0] != OldHeader) return;
            var output = new List<string> { Header };
            for (int i = 1; i < lines.Length; i++)
            {
                var c = lines[i].Split(new[] { ',' }, 5);
                if (c.Length < 5) continue;
                string flags = c[4].Contains("замер экрана") ? "self" : c[4].Contains("неизвестно") ? "unknown" : "";
                output.Add(string.Join(",", c[0], c[1], c[2], c[3], flags, c[4]));
            }
            string tmp = path + ".tmp";
            File.WriteAllLines(tmp, output, new UTF8Encoding(false));
            File.Delete(path);
            File.Move(tmp, path);
        }
    }

    /// <summary>Одно пробуждение дискретной видеокарты: от перехода D3→D0 до возврата в D3.</summary>
    sealed class GpuWake
    {
        public DateTime Start, End;   // End = MinValue, пока не уснула
        public bool Active { get { return End == DateTime.MinValue; } }
        public double Seconds { get { return ((Active ? DateTime.Now : End) - Start).TotalSeconds; } }

        /// <summary>Процессы, которые нагружали видеокарту во время пробуждения: имя → наибольшая загрузка, %.</summary>
        public readonly Dictionary<string, double> Busy = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        /// <summary>Процессы, которые открыли видеокарту в момент пробуждения (до этого их на ней не было).</summary>
        public readonly List<string> Opened = new List<string>();
        /// <summary>Совпавшие по времени события: смена яркости, подключение экрана…</summary>
        public readonly List<string> Hints = new List<string>();

        public GpuWake Copy()
        {
            var w = new GpuWake { Start = Start, End = End };
            foreach (var kv in Busy) w.Busy[kv.Key] = kv.Value;
            w.Opened.AddRange(Opened);
            w.Hints.AddRange(Hints);
            return w;
        }

        // Коды событий для Hints: текст подставляется при выводе, на текущем языке.
        public const string HintBrightness = "brightness", HintDisplay = "display", HintCalibration = "calibration";

        /// <summary>Разбудила сама программа (замер экрана меняет яркость).</summary>
        public bool SelfCaused { get { return Hints.Contains(HintCalibration); } }
        /// <summary>Причина не найдена: ни событий, ни процессов на видеокарте.</summary>
        public bool Unknown { get { return Hints.Count == 0 && Opened.Count == 0 && Busy.Count == 0; } }

        public static string HintText(string code)
        {
            switch (code)
            {
                case HintBrightness: return L.T("brightness change", "зміна яскравості");
                case HintDisplay: return L.T("display connected or disconnected", "підключення або відключення екрана");
                case HintCalibration: return L.T("screen measurement: brightness change", "вимірювання екрана: зміна яскравості");
                default: return code;
            }
        }

        /// <summary>Наиболее вероятная причина, одной строкой.</summary>
        public string Cause
        {
            get
            {
                var parts = new List<string>();
                foreach (var h in Hints) parts.Add(HintText(h));
                if (Opened.Count > 0) parts.Add(L.T("opened: ", "відкрили: ") + string.Join(", ", Opened));
                var busy = new List<KeyValuePair<string, double>>(Busy);
                busy.Sort((a, b) => b.Value.CompareTo(a.Value));
                var names = new List<string>();
                foreach (var kv in busy)
                    if (names.Count < 3) names.Add(string.Format("{0} {1:0} %", kv.Key, kv.Value));
                if (names.Count > 0) parts.Add(L.T("work: ", "робота: ") + string.Join(", ", names));
                return parts.Count > 0 ? string.Join(" · ", parts)
                    : L.T("unknown (the GPU was polled but did no work)", "невідомо (відеокарту опитали, але роботи не було)");
            }
        }
    }

    /// <summary>
    /// Отслеживает пробуждения дискретной NVIDIA и ищет виновников по счётчикам Windows «GPU Engine»
    /// (какие процессы и насколько загружают её движки). Счётчики видеокарту не будят; пока она спит,
    /// раз в 10 с запоминается, какие процессы уже держат на ней контекст, — чтобы при пробуждении
    /// заметить новые. Время на тик — единицы миллисекунд и только пока видеокарта не спит (плюс редкий фон).
    /// </summary>
    sealed class GpuWakeTracker : IDisposable
    {
        const uint PDH_FMT_DOUBLE = 0x200;
        const int PDH_MORE_DATA = unchecked((int)0x800007D2);
        const double BaselineEverySeconds = 10, HintWindowBefore = 6, HintWindowAfter = 3;
        const int Keep = 50;

        readonly string dgpuLuid;
        IntPtr query, counter, items;
        int itemsSize;
        HashSet<string> onGpu = new HashSet<string>(StringComparer.OrdinalIgnoreCase);  // процессы с контекстом на NVIDIA
        DateTime baselineAt = DateTime.MinValue;
        readonly Dictionary<long, string> names = new Dictionary<long, string>();
        readonly List<GpuWake> wakes = new List<GpuWake>();
        readonly List<KeyValuePair<DateTime, string>> hints = new List<KeyValuePair<DateTime, string>>();
        readonly object sync = new object();
        readonly long selfPid = Process.GetCurrentProcess().Id;
        int prevState = -1;

        GpuWakeTracker(string dgpuLuid)
        {
            this.dgpuLuid = dgpuLuid;
            if (PdhOpenQuery(null, IntPtr.Zero, out query) != 0) return;
            if (PdhAddEnglishCounter(query, @"\GPU Engine(*)\Utilization Percentage", IntPtr.Zero, out counter) != 0) counter = IntPtr.Zero;
            PdhCollectQueryData(query);
        }

        public static GpuWakeTracker Create()
        {
            string igpu, dgpu;
            Dxgi.FindAdapters(out igpu, out dgpu);
            return dgpu == null ? null : new GpuWakeTracker(dgpu);
        }

        /// <summary>Событие, которое может разбудить видеокарту (смена яркости, подключение экрана); привязывается к ближайшему пробуждению.</summary>
        public void Hint(string text)
        {
            lock (sync)
            {
                var now = DateTime.Now;
                hints.Add(new KeyValuePair<DateTime, string>(now, text));
                hints.RemoveAll(h => (now - h.Key).TotalSeconds > 60);
                foreach (var w in wakes)
                    if (Math.Abs((now - w.Start).TotalSeconds) <= HintWindowBefore && !w.Hints.Contains(text)) w.Hints.Add(text);
            }
        }

        public void Tick(int dstate)
        {
            DateTime now = DateTime.Now;
            bool awake = dstate == 0;
            lock (sync)
            {
                if (awake && prevState != 0)
                {
                    var w = new GpuWake { Start = now };
                    foreach (var h in hints)
                        if ((now - h.Key).TotalSeconds <= HintWindowBefore && (h.Key - now).TotalSeconds <= HintWindowAfter && !w.Hints.Contains(h.Value))
                            w.Hints.Add(h.Value);
                    wakes.Add(w);
                    if (wakes.Count > Keep) wakes.RemoveAt(0);
                }
                else if (!awake && prevState == 0 && wakes.Count > 0 && wakes[wakes.Count - 1].Active)
                {
                    wakes[wakes.Count - 1].End = now;
                }
                prevState = dstate;
            }

            if (awake || (now - baselineAt).TotalSeconds >= BaselineEverySeconds)
            {
                baselineAt = now;
                Sample(awake ? CurrentWake() : null);
            }
        }

        GpuWake CurrentWake()
        {
            lock (sync) return wakes.Count > 0 && wakes[wakes.Count - 1].Active ? wakes[wakes.Count - 1] : null;
        }

        /// <summary>Читает загрузку движков NVIDIA по процессам; во время пробуждения — записывает в него.</summary>
        void Sample(GpuWake wake)
        {
            if (counter == IntPtr.Zero || PdhCollectQueryData(query) != 0) return;
            int size = itemsSize, count;
            int st = PdhGetFormattedCounterArray(counter, PDH_FMT_DOUBLE, ref size, out count, items);
            if (st == PDH_MORE_DATA)
            {
                if (items != IntPtr.Zero) Marshal.FreeHGlobal(items);
                itemsSize = size + 16384;
                items = Marshal.AllocHGlobal(itemsSize);
                size = itemsSize;
                st = PdhGetFormattedCounterArray(counter, PDH_FMT_DOUBLE, ref size, out count, items);
            }
            if (st != 0) return;

            var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var busy = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            int itemSize = IntPtr.Size + 16;
            for (int i = 0; i < count; i++)
            {
                IntPtr item = items + i * itemSize;
                // «pid_11436_luid_0x00000000_0x000142dc_phys_0_eng_0_engtype_3D»
                string[] tok = Marshal.PtrToStringUni(Marshal.ReadIntPtr(item, 0)).Split('_');
                long pid;
                if (tok.Length < 5 || tok[0] != "pid" || !long.TryParse(tok[1], out pid)) continue;
                if (!string.Equals(tok[3] + "_" + tok[4], dgpuLuid, StringComparison.OrdinalIgnoreCase)) continue;
                // Своё приложение — не подозреваемый: к NVIDIA оно обращается (NVML), только когда та уже не спит,
                // и иначе ложно попадало бы в «открыли» при первом подключении.
                if (pid == selfPid) continue;
                string name = NameOf(pid);
                if (name == null) continue;
                present.Add(name);
                bool valid = Marshal.ReadInt32(item, IntPtr.Size) <= 1;
                double v = BitConverter.Int64BitsToDouble(Marshal.ReadInt64(item, IntPtr.Size + 8));
                if (valid && v >= 1)
                {
                    double old;
                    busy.TryGetValue(name, out old);
                    busy[name] = Math.Max(old, Math.Min(100, v));
                }
            }

            lock (sync)
            {
                if (wake != null)
                {
                    foreach (string n in present)
                        if (!onGpu.Contains(n) && !wake.Opened.Contains(n) && (DateTime.Now - wake.Start).TotalSeconds <= 5)
                            wake.Opened.Add(n);
                    foreach (var kv in busy)
                    {
                        double old;
                        wake.Busy.TryGetValue(kv.Key, out old);
                        wake.Busy[kv.Key] = Math.Max(old, kv.Value);
                    }
                }
                onGpu = present;
            }
        }

        string NameOf(long pid)
        {
            string n;
            if (names.TryGetValue(pid, out n)) return n;
            try
            {
                using (var p = Process.GetProcessById((int)pid)) n = p.ProcessName;
            }
            catch (Exception)
            {
                n = null;  // процесс уже завершился
            }
            if (names.Count > 2000) names.Clear();
            names[pid] = n;
            return n;
        }

        /// <summary>Копия журнала пробуждений (последние Keep), по времени.</summary>
        public List<GpuWake> Snapshot()
        {
            lock (sync)
            {
                var list = new List<GpuWake>(wakes.Count);
                foreach (var w in wakes) list.Add(w.Copy());
                return list;
            }
        }

        public void Dispose()
        {
            if (query != IntPtr.Zero) PdhCloseQuery(query);
            if (items != IntPtr.Zero) Marshal.FreeHGlobal(items);
            query = items = IntPtr.Zero;
        }

        [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
        static extern int PdhOpenQuery(string dataSource, IntPtr userData, out IntPtr query);

        [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
        static extern int PdhAddEnglishCounter(IntPtr query, string path, IntPtr userData, out IntPtr counter);

        [DllImport("pdh.dll")]
        static extern int PdhCollectQueryData(IntPtr query);

        [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
        static extern int PdhGetFormattedCounterArray(IntPtr counter, uint format, ref int bufferSize, out int itemCount, IntPtr items);

        [DllImport("pdh.dll")]
        static extern int PdhCloseQuery(IntPtr query);
    }
}
