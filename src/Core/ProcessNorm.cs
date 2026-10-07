using System;
using System.Collections.Generic;

namespace BatteryCheck
{
    /// <summary>
    /// Своя норма программ: фоновая энергия по дням (из logs\process_energy_*.csv) и время работы от батареи по дням.
    /// Фон программы за день, Вт = её фоновые Вт·ч ÷ часы от батареи в тот день. Норма — медиана по прошлым дням,
    /// когда программа работала (и от батареи было хотя бы 15 минут); нужно хотя бы MinDays таких дней.
    /// </summary>
    sealed class ProcessHistory
    {
        public const double MinDayHours = 0.25;
        public const int MinDays = 3;

        readonly Dictionary<DateTime, double> batteryHours = new Dictionary<DateTime, double>();
        readonly Dictionary<string, Dictionary<DateTime, double>> bg = new Dictionary<string, Dictionary<DateTime, double>>(StringComparer.OrdinalIgnoreCase);

        public void AddBattery(DateTime minute, double seconds)
        {
            double h;
            batteryHours.TryGetValue(minute.Date, out h);
            batteryHours[minute.Date] = h + seconds / 3600;
        }

        public void AddProcess(DateTime minute, string name, double bgWh)
        {
            Dictionary<DateTime, double> days;
            if (!bg.TryGetValue(name, out days)) bg[name] = days = new Dictionary<DateTime, double>();
            double v;
            days.TryGetValue(minute.Date, out v);
            days[minute.Date] = v + bgWh;
        }

        /// <summary>Своя норма фона, Вт, по дням до before; NaN — мало дней. days — сколько дней в основе.</summary>
        public double Norm(string name, DateTime before, out int days)
        {
            days = 0;
            Dictionary<DateTime, double> map;
            if (!bg.TryGetValue(name, out map)) return double.NaN;
            var values = new List<double>();
            foreach (var kv in map)
            {
                double hours;
                if (kv.Key >= before.Date || !batteryHours.TryGetValue(kv.Key, out hours) || hours < MinDayHours) continue;
                values.Add(kv.Value / hours);
            }
            days = values.Count;
            if (values.Count < MinDays) return double.NaN;
            values.Sort();
            return values.Count % 2 == 1 ? values[values.Count / 2] : (values[values.Count / 2 - 1] + values[values.Count / 2]) / 2;
        }

        /// <summary>Фон с from, Вт: сумма фоновых Вт·ч ÷ часы от батареи — по дням, когда программа работала (как и норма).</summary>
        public double Current(string name, DateTime from)
        {
            Dictionary<DateTime, double> map;
            if (!bg.TryGetValue(name, out map)) return double.NaN;
            double wh = 0, hours = 0;
            foreach (var kv in map)
            {
                double h;
                if (kv.Key < from.Date || !batteryHours.TryGetValue(kv.Key, out h)) continue;
                wh += kv.Value;
                hours += h;
            }
            return hours > 0 ? wh / hours : double.NaN;
        }

        /// <summary>
        /// «Выше своей нормы»: больше 1,5 нормы и больше нормы + 0,3 Вт. Порог 0,3 Вт — чтобы не было тревог
        /// из-за программ с фоном в десятые доли ватта, где полуторный рост ничего не стоит.
        /// </summary>
        public static bool AboveNorm(double current, double norm)
        {
            return !double.IsNaN(current) && !double.IsNaN(norm) && current > Math.Max(norm * 1.5, norm + 0.3);
        }
    }
}
