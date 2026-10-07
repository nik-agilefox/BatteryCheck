using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace BatteryCheck
{
    /// <summary>Один замер всех источников.</summary>
    sealed class Sample
    {
        public DateTime Time;      // локальное время
        public DateTime TimeUtc;   // для расчёта интервалов
        public BatteryStatus Battery;
        public double CpuPkgW = double.NaN, CpuCoresW = double.NaN, IgpuW = double.NaN, DramW = double.NaN;
        public double GpuW = double.NaN;
        public int GpuUtil = -1, GpuPState = -1;
        public int GpuDState = -1;                    // 0 — D0 (работает), 3 — D3 (спит)
        public bool GpuDisabled;                      // отключена в диспетчере устройств: без драйвера не спит (GpuDState = -1)
        public int Displays = -1, DisplaysOnGpu = -1;  // подключённые экраны, из них через NVIDIA
        public double IdleS = double.NaN;             // сколько секунд не было ввода с клавиатуры и мыши
        public double ScreenW = double.NaN;           // оценка мощности экрана (из окна, по замеру экрана)
        public int BrightnessPct = -1;
        public double DeepSleepPct = double.NaN;      // ядра в самом глубоком сне, % времени
        public double TimerMs = double.NaN;           // разрешение системного таймера
        public int DisplayState = -1;                 // экраны по Windows: 0 — выключены, 1 — включены, 2 — затемнены; -1 — неизвестно

        /// <summary>
        /// Мощность разряда в ваттах (положительная), NaN если батарея не разряжается.
        /// Ноль при разряде — ещё не обновившееся значение (бывает в момент отключения зарядки), а не настоящий ноль.
        /// </summary>
        public double DischargeW
        {
            get { return Battery.Discharging && Battery.HasRate && Battery.Rate < 0 ? -Battery.Rate / 1000.0 : double.NaN; }
        }
    }

    /// <summary>Экспоненциальное скользящее среднее с постоянной времени tau (с).</summary>
    sealed class Ema
    {
        readonly double tau;
        double value = double.NaN;

        public Ema(double tauSeconds) { tau = tauSeconds; }

        public double Value { get { return value; } }

        public void Add(double x, double dt)
        {
            if (double.IsNaN(x)) return;
            if (double.IsNaN(value) || dt <= 0) { value = x; return; }
            value += (1 - Math.Exp(-dt / tau)) * (x - value);
        }

        public void Reset() { value = double.NaN; }
    }

    /// <summary>
    /// Статистика разряда. Энергия считается двумя способами:
    /// суммой P·Δt по мощности и по убыванию остатка заряда, который считает контроллер батареи.
    /// </summary>
    sealed class Session
    {
        public double MaxGapSeconds;  // меняется вместе с интервалом опроса

        public double DurationS;
        public double EnergyByRateWh;
        public double EnergyByCapacityWh;
        public double MinW = double.NaN, MaxW = double.NaN;

        public Session(double maxGapSeconds) { MaxGapSeconds = maxGapSeconds; }

        public double AvgW { get { return DurationS > 0 ? EnergyByRateWh * 3600 / DurationS : double.NaN; } }

        /// <summary>Расхождение двух способов в процентах (относительно остатка заряда).</summary>
        public double MismatchPct
        {
            get { return EnergyByCapacityWh > 0.05 ? (EnergyByRateWh - EnergyByCapacityWh) / EnergyByCapacityWh * 100 : double.NaN; }
        }

        public void Add(Sample prev, Sample cur)
        {
            double dt = (cur.TimeUtc - prev.TimeUtc).TotalSeconds;
            if (dt <= 0 || dt > MaxGapSeconds) return;  // разрыв: сон, гибернация, смена часов
            double p = cur.DischargeW, pPrev = prev.DischargeW;
            if (double.IsNaN(p) || double.IsNaN(pPrev)) return;

            EnergyByRateWh += (p + pPrev) / 2 * dt / 3600;
            DurationS += dt;
            if (prev.Battery.HasCapacity && cur.Battery.HasCapacity)
                EnergyByCapacityWh += ((double)prev.Battery.Capacity - cur.Battery.Capacity) / 1000.0;

            if (double.IsNaN(MinW) || p < MinW) MinW = p;
            if (double.IsNaN(MaxW) || p > MaxW) MaxW = p;
        }
    }

    /// <summary>CSV-журнал замеров. Пишет в буфер и сбрасывает на диск раз в минуту.</summary>
    sealed class CsvLog : IDisposable
    {
        const double FlushSeconds = 60;
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        readonly StreamWriter writer;
        DateTime lastFlush = DateTime.UtcNow;

        public readonly string FilePath;

        public CsvLog(string path)
        {
            FilePath = Path.GetFullPath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
            writer = new StreamWriter(FilePath, false, new UTF8Encoding(false), 64 * 1024);
            writer.WriteLine("time,power_state,on_ac,charging,discharging,capacity_mwh,full_mwh,soc_pct,voltage_mv,rate_mw,current_a," +
                             "cpu_pkg_w,cpu_cores_w,igpu_w,dram_w,dgpu_w,dgpu_util_pct,dgpu_pstate,dgpu_dstate," +
                             "displays,displays_on_dgpu,rest_w,idle_s,brightness_pct,screen_w,dgpu_disabled,cpu_c3_pct,timer_ms,display_state");
        }

        public void Write(Sample s, uint fullMwh, double restW)
        {
            var b = s.Battery;
            double soc = b.HasCapacity && fullMwh > 0 ? 100.0 * b.Capacity / fullMwh : double.NaN;
            writer.WriteLine(string.Join(",", new[]
            {
                s.Time.ToString("yyyy-MM-ddTHH:mm:ss.fff", Inv),
                b.PowerState.ToString(Inv),
                b.OnLine ? "1" : "0",
                b.Charging ? "1" : "0",
                b.Discharging ? "1" : "0",
                b.HasCapacity ? b.Capacity.ToString(Inv) : "",
                fullMwh.ToString(Inv),
                F(soc, "0.00"),
                b.HasVoltage ? b.Voltage.ToString(Inv) : "",
                b.HasRate ? b.Rate.ToString(Inv) : "",
                F(b.CurrentA, "0.000"),
                F(s.CpuPkgW, "0.000"),
                F(s.CpuCoresW, "0.000"),
                F(s.IgpuW, "0.000"),
                F(s.DramW, "0.000"),
                F(s.GpuW, "0.000"),
                s.GpuUtil >= 0 ? s.GpuUtil.ToString(Inv) : "",
                s.GpuPState >= 0 ? s.GpuPState.ToString(Inv) : "",
                s.GpuDState >= 0 ? s.GpuDState.ToString(Inv) : "",
                s.Displays >= 0 ? s.Displays.ToString(Inv) : "",
                s.DisplaysOnGpu >= 0 ? s.DisplaysOnGpu.ToString(Inv) : "",
                F(restW, "0.000"),
                F(s.IdleS, "0"),
                s.BrightnessPct >= 0 ? s.BrightnessPct.ToString(Inv) : "",
                F(s.ScreenW, "0.000"),
                s.GpuDisabled ? "1" : "0",
                F(s.DeepSleepPct, "0.0"),
                F(s.TimerMs, "0.000"),
                s.DisplayState >= 0 ? s.DisplayState.ToString(Inv) : "",
            }));

            if ((DateTime.UtcNow - lastFlush).TotalSeconds >= FlushSeconds)
            {
                writer.Flush();
                lastFlush = DateTime.UtcNow;
            }
        }

        static string F(double v, string format)
        {
            return double.IsNaN(v) || double.IsInfinity(v) ? "" : v.ToString(format, Inv);
        }

        public void Dispose()
        {
            writer.Flush();
            writer.Dispose();
        }
    }
}
