using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace BatteryCheck
{
    sealed class SamplerOptions
    {
        public int IntervalMs = 1000;
        public bool Gpu = true;
        public bool Log = true;
        public string LogPath;
    }

    /// <summary>Оценка мощности процесса (все процессы с одним именем вместе).</summary>
    sealed class ProcessPower
    {
        public string Name;
        public double PowerW, CpuPct, GpuPct;
        public double DgpuMemMB;  // видеопамять на дискретной карте: держит её включённой
    }

    /// <summary>Снимок для отображения. Все значения скопированы, его можно передавать в другой поток.</summary>
    sealed class Snapshot
    {
        public Sample Sample;
        public BatteryInfo Info;
        public double SocPct = double.NaN;
        public double BatteryAvgW = double.NaN;  // |мощность батареи|, среднее за 30 с
        public double HoursLeft = double.NaN;    // до разряда (разряд) или до полного заряда (заряд)
        public double Cpu10W = double.NaN, Igpu10W = double.NaN, Gpu10W = double.NaN, RestW = double.NaN;  // Cpu10W — весь чип
        public bool RaplAvailable;
        public string GpuName;                   // null — видеокарта NVIDIA не найдена
        public double DeepSleep10 = double.NaN;  // ядра в самом глубоком сне, % времени, сглажено за 10 с
        public bool GpuPolling;                  // опрос мощности NVIDIA включён
        public double SessionS, SessionByRateWh, SessionByCapacityWh;
        public double SessionMismatchPct = double.NaN, SessionAvgW = double.NaN, SessionMinW = double.NaN, SessionMaxW = double.NaN;
        public string Error;
        public string LogPath;
        public int IntervalMs;                    // интервал, с которым был сделан этот замер

        // Последний сон или гибернация при работе от батареи (NaN — не было)
        public DateTime LastSleepEnd;
        public double LastSleepS = double.NaN, LastSleepWh = double.NaN, LastSleepW = double.NaN;

        // Оценка по процессам: обновляется раз в ProcessIntervalSeconds, пока сбор включён
        public List<GpuWake> GpuWakes;            // журнал пробуждений NVIDIA (последние 50); null — NVIDIA нет

        public bool ProcessesActive;              // сбор включён
        public bool ProcessesUpdated;             // в этом замере пришла новая оценка
        public List<ProcessPower> Processes;      // по убыванию мощности; null — оценки ещё нет
        public double ProcessesAttributedW = double.NaN, ProcessesUnattributedW = double.NaN;
    }

    /// <summary>
    /// Опрос всех источников, сглаживание, учёт сессии разряда и CSV-лог.
    /// Tick() вызывается из одного потока; ResetSession и GpuPolling можно менять из любого.
    /// </summary>
    sealed class Sampler : IDisposable
    {
        const double InfoRefreshSeconds = 60;
        const double DisplaysRefreshSeconds = 10;

        readonly Battery battery;
        readonly Rapl rapl;
        readonly GpuPowerState gpuPower;
        readonly CsvLog log;
        NvidiaGpu gpu;
        bool gpuOpenTried;

        BatteryInfo info;
        DateTime infoTime;
        Session session;
        CpuIdle cpuIdle;
        readonly Ema deep10 = new Ema(10);
        readonly Ema batAvg = new Ema(30), bat10 = new Ema(10), cpu10 = new Ema(10), igpu10 = new Ema(10), gpu10 = new Ema(10);
        Sample prev;
        int displays = -1, displaysOnGpu = -1;
        DateTime displaysTime = DateTime.MinValue;

        volatile bool resetRequested;
        volatile bool gpuPolling;
        volatile int intervalMs;

        DateTime lastSleepEnd;
        double lastSleepS = double.NaN, lastSleepWh = double.NaN;

        /// <summary>Пропуск дольше этого считается сном, а не просто редким опросом.</summary>
        const double SleepMinSeconds = 60;

        /// <summary>
        /// Замер по процессам стоит ~20 мс процессора (≈2 % одного ядра при раз в секунду) и идёт, только пока
        /// режим «Процессы» открыт на экране.
        /// </summary>
        public const double ProcessIntervalSeconds = 1;
        const int ProcessesKept = 30;

        public const double BackgroundProcessIntervalSeconds = 10;
        ProcessMonitor procMon;
        ProcessEnergyLog energyLog;
        volatile bool backgroundProcesses = true;
        GpuWakeTracker wakeTracker;
        bool wakeTrackerTried;
        volatile bool processesEnabled;
        bool procActive;
        DateTime procLast;
        double accCores, accIgpu, accDgpu, accTotal;  // суммы мощности за интервал между замерами процессов
        int accN;
        List<ProcessPower> procLatest;
        double procAttributed = double.NaN, procUnattributed = double.NaN;

        public Sampler(SamplerOptions o)
        {
            battery = Battery.Open(0);
            if (battery == null) throw new InvalidOperationException(L.T("Battery not found.", "Батарею не знайдено."));
            try
            {
                info = battery.QueryInfo();
                infoTime = DateTime.UtcNow;
                rapl = new Rapl();
                cpuIdle = new CpuIdle();
                gpuPower = GpuPowerState.FindNvidia();
                gpuPolling = o.Gpu;
                // Инициализация NVML будит видеокарту, поэтому спящую не трогаем: подключимся, когда проснётся сама.
                if (o.Gpu && (gpuPower == null || gpuPower.Read() != 3))
                {
                    gpu = NvidiaGpu.TryOpen();
                    gpuOpenTried = true;
                }
                if (o.Log)
                {
                    log = new CsvLog(o.LogPath ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "logs",
                        "battery_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".csv"));
                    energyLog = new ProcessEnergyLog(Path.GetDirectoryName(log.FilePath));
                }
            }
            catch
            {
                Dispose();
                throw;
            }
            intervalMs = o.IntervalMs;
            session = new Session(MaxGap());
        }

        readonly LogReplay replay;

        /// <summary>Воспроизведение записанных логов вместо датчиков: без батареи, RAPL, NVML, лога и процессов.</summary>
        public Sampler(LogReplay replay)
        {
            this.replay = replay;
            info = new BatteryInfo { DeviceName = "replay", Manufacturer = Path.GetFileName(replay.Source) };
            intervalMs = 1000;
            session = new Session(MaxGap());
        }

        /// <summary>Интервал между замерами больше этого — разрыв (сон, зависание): его не интегрируем.</summary>
        double MaxGap()
        {
            return Math.Max(5, intervalMs / 1000.0 * 3);
        }

        public string LogPath { get { return log != null ? log.FilePath : null; } }

        public void ResetSession() { resetRequested = true; }

        /// <summary>Событие, которое может разбудить NVIDIA (смена яркости, подключение экрана) — для журнала пробуждений.</summary>
        public void WakeHint(string text)
        {
            var t = wakeTracker;
            if (t != null) t.Hint(text);
        }

        public bool GpuPolling
        {
            get { return gpuPolling; }
            set { gpuPolling = value; }
        }

        /// <summary>Оценивать мощность по процессам раз в секунду. Включать только когда она видна: замер дорогой.</summary>
        public bool ProcessesEnabled
        {
            get { return processesEnabled; }
            set { processesEnabled = value; }
        }

        /// <summary>Собирать энергию процессов для рекомендаций в фоне при работе от батареи (раз в 10 с).</summary>
        public bool BackgroundProcesses
        {
            get { return backgroundProcesses; }
            set { backgroundProcesses = value; }
        }

        static double Nz(double v)
        {
            return double.IsNaN(v) ? 0 : v;
        }

        /// <summary>
        /// Делит мощность между процессами: ядра процессора — по израсходованным тактам, встроенная графика и NVIDIA —
        /// по загрузке их движков. Мощность берётся средней за интервал, за который считались такты и загрузка.
        /// Остаток (экран, плата, общая часть процессора) процессам не принадлежит — он «не распределён».
        /// </summary>
        bool SampleProcesses(Sample cur)
        {
            // Видимый режим «Процессы» — раз в секунду; для рекомендаций — в фоне от батареи раз в 10 с (~0,2 % ядра).
            bool background = backgroundProcesses && cur.Battery.Discharging && energyLog != null;
            if (!processesEnabled && !background)
            {
                procActive = false;
                procLatest = null;
                return false;
            }
            if (!procActive)
            {
                if (procMon == null) procMon = new ProcessMonitor();
                procMon.Reset();
                procMon.Sample();  // база для следующего замера
                procActive = true;
                procLast = cur.TimeUtc;
                accCores = accIgpu = accDgpu = accTotal = 0;
                accN = 0;
                return false;
            }

            accCores += Nz(!double.IsNaN(cur.CpuCoresW) ? cur.CpuCoresW : cur.CpuPkgW);
            accIgpu += Nz(cur.IgpuW);
            accDgpu += Nz(cur.GpuW);
            accTotal += cur.Battery.Discharging ? Nz(cur.DischargeW) : Nz(cur.CpuPkgW) + Nz(cur.GpuW);
            accN++;
            double interval = processesEnabled ? ProcessIntervalSeconds : BackgroundProcessIntervalSeconds;
            double window = (cur.TimeUtc - procLast).TotalSeconds;
            if (window < interval - 0.2) return false;

            var usage = procMon.Sample();
            procLast = cur.TimeUtc;
            bool updated = false;
            // Энергия за окно — в лог для рекомендаций (только от батареи; окно длиннее минуты — был пропуск, не считаем).
            bool account = energyLog != null && cur.Battery.Discharging && window <= 60;
            string fgName = null;
            if (account)
            {
                long fgPid = ForegroundApp.Pid();
                procMon.LastNames.TryGetValue(fgPid, out fgName);
            }
            if (usage != null && accN > 0)
            {
                double cores = accCores / accN, igpu = accIgpu / accN, dgpu = accDgpu / accN, total = accTotal / accN;
                var list = new List<ProcessPower>(usage.Count);
                double attributed = 0;
                foreach (var u in usage)
                {
                    double gpuW = igpu * u.IgpuShare + dgpu * u.DgpuShare;
                    double p = cores * u.CycleShare + gpuW;
                    attributed += p;
                    list.Add(new ProcessPower { Name = u.Name, PowerW = p, CpuPct = u.CpuPct, GpuPct = u.GpuPct, DgpuMemMB = u.DgpuMemMB });
                    if (account && p > 0)
                        energyLog.Add(cur.Time, u.Name, p * window / 3600, fgName != null && string.Equals(fgName, u.Name, StringComparison.OrdinalIgnoreCase), gpuW * window / 3600);
                }
                if (account) energyLog.AddBattery(cur.Time, total * window / 3600, window);
                list.Sort((a, b) => b.PowerW.CompareTo(a.PowerW));
                if (list.Count > ProcessesKept) list.RemoveRange(ProcessesKept, list.Count - ProcessesKept);
                procLatest = list;
                procAttributed = attributed;
                procUnattributed = Math.Max(0, total - attributed);
                updated = true;
            }
            accCores = accIgpu = accDgpu = accTotal = 0;
            accN = 0;
            return updated;
        }

        /// <summary>Очередной замер; при воспроизведении null — логи кончились.</summary>
        public Snapshot Tick()
        {
            LogReplay.Row row = null;
            if (replay != null)
            {
                row = replay.Next();
                if (row == null) return null;
                // Интервал опроса в логе — фактический шаг между строками (1 с на экране, 10 с в фоне); больше 35 с — пропуск.
                if (prev != null)
                {
                    double gap = (row.Sample.TimeUtc - prev.TimeUtc).TotalSeconds;
                    intervalMs = gap > 0 && gap <= 35 ? (int)Math.Round(gap * 1000) : 1000;
                }
            }

            double maxGap = MaxGap();
            if (resetRequested)
            {
                resetRequested = false;
                session = new Session(maxGap);
            }
            session.MaxGapSeconds = maxGap;

            Sample cur;
            string error = null;
            bool polling = gpuPolling;
            if (row != null)
            {
                cur = row.Sample;
                if (row.FullMwh > 0) info.FullChargedCapacity = row.FullMwh;
            }
            else
            {
                cur = new Sample { Time = DateTime.Now, TimeUtc = DateTime.UtcNow };
                ReadSensors(cur, polling, ref error);
            }

            double dt = prev == null ? 0 : (cur.TimeUtc - prev.TimeUtc).TotalSeconds;
            bool modeChanged = prev != null && prev.Battery.Discharging != cur.Battery.Discharging;
            if (modeChanged || dt > maxGap)
            {
                batAvg.Reset();
                bat10.Reset();
            }

            // Долгий пропуск при работе от батареи до и после — это сон или гибернация: считаем их расход.
            if (prev != null && dt > Math.Max(maxGap, SleepMinSeconds) && !prev.Battery.OnLine && !cur.Battery.OnLine
                && prev.Battery.HasCapacity && cur.Battery.HasCapacity)
            {
                lastSleepEnd = cur.Time;
                lastSleepS = dt;
                lastSleepWh = ((double)prev.Battery.Capacity - cur.Battery.Capacity) / 1000.0;
            }
            batAvg.Add(Math.Abs(cur.Battery.RateW), dt);
            bat10.Add(cur.DischargeW, dt);
            cpu10.Add(cur.CpuPkgW, dt);
            igpu10.Add(cur.IgpuW, dt);
            if (double.IsNaN(cur.GpuW)) gpu10.Reset();
            else gpu10.Add(cur.GpuW, dt);
            if (prev != null) session.Add(prev, cur);
            deep10.Add(cur.DeepSleepPct, dt);

            // «Остальное» = батарея − процессор − видеокарта, по сглаженным значениям
            // (мощность батареи и RAPL обновляются не синхронно).
            double restW = double.NaN;
            if (cur.Battery.Discharging && !double.IsNaN(bat10.Value) && !double.IsNaN(cpu10.Value))
                restW = bat10.Value - cpu10.Value - (double.IsNaN(gpu10.Value) ? 0 : gpu10.Value);

            if (log != null) log.Write(cur, info.FullChargedCapacity, restW);
            prev = cur;
            bool procUpdated = replay == null && SampleProcesses(cur);

            return BuildSnapshot(cur, restW, polling, procUpdated, error);
        }

        void ReadSensors(Sample cur, bool polling, ref string error)
        {
            try
            {
                cur.Battery = battery.QueryStatus();
                if ((cur.TimeUtc - infoTime).TotalSeconds >= InfoRefreshSeconds)
                {
                    info = battery.QueryInfo();
                    infoTime = cur.TimeUtc;
                }
            }
            catch (Win32Exception e)
            {
                error = L.T("Battery read error: ", "Помилка читання батареї: ") + e.Message;
                cur.Battery = new BatteryStatus { Capacity = 0xFFFFFFFF, Voltage = 0xFFFFFFFF, Rate = int.MinValue };
            }

            rapl.Sample();
            cur.CpuPkgW = rapl.PkgW;
            cur.CpuCoresW = rapl.CoresW;
            cur.IgpuW = rapl.IgpuW;
            cur.DramW = rapl.DramW;

            // Спящую видеокарту NVML будит, поэтому сначала смотрим её состояние в диспетчере устройств.
            cur.GpuDState = gpuPower != null ? gpuPower.Read() : -1;
            if (gpuPower != null && gpuPower.Disabled)
            {
                cur.GpuDisabled = true;
                cur.GpuDState = -1;  // «D3» отключённой карты — не сон: состояние неизвестно, NVML недоступен
            }
            if (polling && gpu == null && !gpuOpenTried && cur.GpuDState != 3)
            {
                gpu = NvidiaGpu.TryOpen();
                gpuOpenTried = true;
            }
            // Журнал пробуждений NVIDIA: DXGI (поиск адаптера) — один раз, при первом замере.
            if (!wakeTrackerTried && gpuPower != null)
            {
                wakeTrackerTried = true;
                wakeTracker = GpuWakeTracker.Create();
            }
            if (wakeTracker != null) wakeTracker.Tick(cur.GpuDState);

            if (cur.GpuDState == 3)
            {
                cur.GpuW = 0;
                cur.GpuUtil = 0;
            }
            else if (polling && gpu != null && !cur.GpuDisabled)
            {
                gpu.Sample();
                cur.GpuW = gpu.PowerW;
                cur.GpuUtil = gpu.UtilPct;
                cur.GpuPState = gpu.PState;
            }

            // Перечисление экранов стоит ~1,5 мс процессора — в 10 раз больше всех остальных датчиков,
            // а экраны меняются редко, поэтому пересчитываем их раз в 10 с.
            if (displaysTime == DateTime.MinValue || (cur.TimeUtc - displaysTime).TotalSeconds >= DisplaysRefreshSeconds)
            {
                Displays.Count(out displays, out displaysOnGpu);
                displaysTime = cur.TimeUtc;
            }
            cur.Displays = displays;
            cur.DisplaysOnGpu = displaysOnGpu;

            cur.IdleS = UserIdle.Seconds();
            if (cpuIdle != null) { cpuIdle.Sample(); cur.DeepSleepPct = cpuIdle.DeepPct; }
            cur.TimerMs = SystemTimer.CurrentMs();
            cur.DisplayState = DisplayState.Current;
            var screen = screenEstimate;
            if (screen != null && (cur.TimeUtc - screen.At).TotalSeconds <= 15)  // окно обновляет оценку раз в 5 с, пока открыто
            {
                cur.ScreenW = screen.W;
                cur.BrightnessPct = screen.Brightness;
            }
            if (cur.DisplayState == DisplayState.Off) cur.ScreenW = 0;  // снимок экрана показывает картинку и при погасшей панели
        }

        sealed class ScreenEstimate
        {
            public double W;
            public int Brightness;
            public DateTime At;
        }

        volatile ScreenEstimate screenEstimate;

        /// <summary>Оценка мощности экрана от окна (по замеру экрана) — для лога и эталона простоя. Из любого потока.</summary>
        public void SetScreenEstimate(double watts, int brightness)
        {
            screenEstimate = new ScreenEstimate { W = watts, Brightness = brightness, At = DateTime.UtcNow };
        }

        Snapshot BuildSnapshot(Sample cur, double restW, bool polling, bool procUpdated, string error)
        {
            var b = cur.Battery;
            var s = new Snapshot();
            s.Sample = cur;
            s.Info = info;
            s.SocPct = b.HasCapacity && info.FullChargedCapacity > 0 ? 100.0 * b.Capacity / info.FullChargedCapacity : double.NaN;
            s.BatteryAvgW = batAvg.Value;
            if (b.Discharging && b.HasCapacity) s.HoursLeft = b.Capacity / 1000.0 / batAvg.Value;
            else if (b.Charging && b.HasCapacity) s.HoursLeft = (info.FullChargedCapacity - (double)b.Capacity) / 1000.0 / batAvg.Value;
            s.Cpu10W = cpu10.Value;
            s.Igpu10W = igpu10.Value;
            s.Gpu10W = gpu10.Value;
            s.RestW = restW;
            s.DeepSleep10 = deep10.Value;
            s.RaplAvailable = rapl != null ? rapl.Available : replay != null && replay.HasRapl;
            s.GpuName = gpu != null ? gpu.Name : gpuPower != null ? gpuPower.Name : replay != null && replay.HasGpu ? "dGPU" : null;
            s.GpuPolling = polling;
            s.SessionS = session.DurationS;
            s.SessionByRateWh = session.EnergyByRateWh;
            s.SessionByCapacityWh = session.EnergyByCapacityWh;
            s.SessionMismatchPct = session.MismatchPct;
            s.SessionAvgW = session.AvgW;
            s.SessionMinW = session.MinW;
            s.SessionMaxW = session.MaxW;
            s.Error = error;
            s.LogPath = LogPath;
            s.IntervalMs = intervalMs;
            if (!double.IsNaN(lastSleepS))
            {
                s.LastSleepEnd = lastSleepEnd;
                s.LastSleepS = lastSleepS;
                s.LastSleepWh = lastSleepWh;
                s.LastSleepW = lastSleepWh * 3600 / lastSleepS;
            }
            if (wakeTracker != null) s.GpuWakes = wakeTracker.Snapshot();
            s.ProcessesActive = procActive;
            s.ProcessesUpdated = procUpdated;
            if (procLatest != null)
            {
                s.Processes = new List<ProcessPower>(procLatest);
                s.ProcessesAttributedW = procAttributed;
                s.ProcessesUnattributedW = procUnattributed;
            }
            return s;
        }

        /// <summary>
        /// Цикл опроса. После каждого замера intervalFor выбирает паузу до следующего (например, реже в фоне).
        /// Поток спит до следующего тика без активного ожидания. Сигнал poke делает замер сразу
        /// (смена источника питания, открытие окна), stop завершает цикл. count = 0 — работать до сигнала.
        /// </summary>
        public void Run(Func<Snapshot, int> intervalFor, int count, WaitHandle stop, WaitHandle poke, Action<Snapshot> onSnapshot)
        {
            var handles = poke != null ? new[] { stop, poke } : new[] { stop };
            var sw = Stopwatch.StartNew();
            long nextTick = 0;
            int n = 0;
            while (true)
            {
                var snap = Tick();
                onSnapshot(snap);
                if (count > 0 && ++n >= count) return;

                int interval = Math.Max(200, intervalFor(snap));
                intervalMs = interval;
                nextTick += interval;
                long now = sw.ElapsedMilliseconds;
                if (now - nextTick > interval) nextTick = now;  // отстали (например, после сна) — не догоняем
                int r = WaitHandle.WaitAny(handles, (int)Math.Max(0, nextTick - now));
                if (r == 0) return;
                if (r == 1) nextTick = sw.ElapsedMilliseconds;  // внеочередной замер — отсчёт заново
            }
        }

        /// <summary>
        /// Воспроизведение логов: speed — во сколько раз быстрее реального времени (0 — без пауз), stop прерывает.
        /// Паузы ограничены 2 с: сон и пропуски в логе не ждём.
        /// </summary>
        public void RunReplay(double speed, WaitHandle stop, Action<Snapshot> onSnapshot)
        {
            while (true)
            {
                var snap = Tick();
                if (snap == null) return;
                onSnapshot(snap);
                var next = replay.PeekTime;
                if (next == null) return;
                int wait = speed > 0 ? (int)Math.Min(2000, Math.Max(0, (next.Value - snap.Sample.Time).TotalMilliseconds / speed)) : 0;
                if (stop != null && stop.WaitOne(wait)) return;
            }
        }

        public void Dispose()
        {
            if (log != null) log.Dispose();
            if (energyLog != null) energyLog.Dispose();
            if (procMon != null) procMon.Dispose();
            if (wakeTracker != null) wakeTracker.Dispose();
            // nvmlShutdown — обращение к драйверу и может разбудить спящую видеокарту; при выходе процесса
            // Windows и так всё освободит, поэтому спящую не трогаем.
            if (gpu != null && (gpuPower == null || gpuPower.Read() != 3)) gpu.Dispose();
            if (gpuPower != null) gpuPower.Dispose();
            if (rapl != null) rapl.Dispose();
            if (cpuIdle != null) cpuIdle.Dispose();
            if (battery != null) battery.Dispose();
        }
    }

    static class Fmt
    {
        public static string W(double w)
        {
            return double.IsNaN(w) || double.IsInfinity(w) ? "—" : w.ToString("0.0") + " " + WUnit;
        }

        public static string WUnit { get { return L.T("W", "Вт"); } }
        public static string WhUnit { get { return L.T("Wh", "Вт·год"); } }
        public static string SecUnit { get { return L.T("s", "с"); } }
        public static string MinUnit { get { return L.T("min", "хв"); } }
        public static string HourUnit { get { return L.T("h", "год"); } }

        public static string Num(double v, string format)
        {
            return double.IsNaN(v) || double.IsInfinity(v) ? "—" : v.ToString(format);
        }

        public static string Wh(uint mwh, bool relative)
        {
            return relative ? mwh + L.T(" units", " од.") : (mwh / 1000.0).ToString("0.00") + " " + WhUnit;
        }

        public static string Duration(double seconds)
        {
            var t = TimeSpan.FromSeconds(seconds);
            return string.Format("{0:00}:{1:00}:{2:00}", (int)t.TotalHours, t.Minutes, t.Seconds);
        }

        public static string Hours(double hours)
        {
            if (double.IsNaN(hours) || double.IsInfinity(hours) || hours < 0 || hours > 99) return "—";
            int totalMin = (int)Math.Round(hours * 60);
            return totalMin >= 60 ? string.Format("{0} {1} {2:00} {3}", totalMin / 60, HourUnit, totalMin % 60, MinUnit)
                : totalMin + " " + MinUnit;
        }

        public static string Bar(double pct, int width)
        {
            if (double.IsNaN(pct)) return new string('░', width);
            int filled = (int)Math.Round(Math.Max(0, Math.Min(100, pct)) / 100 * width);
            return new string('█', filled) + new string('░', width - filled);
        }

        public static string GpuState(int dstate)
        {
            return dstate == 3 ? L.T("asleep (D3)", "спить (D3)") : dstate == 0 ? L.T("active (D0)", "працює (D0)")
                : dstate > 0 ? "D" + dstate : "—";
        }
    }
}
