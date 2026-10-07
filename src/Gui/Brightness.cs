using System;
using System.Collections.Generic;
using System.Globalization;
using System.Management;

namespace BatteryCheck
{
    /// <summary>Яркость встроенного экрана через WMI (root\wmi). Права администратора не нужны.</summary>
    static class Brightness
    {
        /// <summary>Текущая яркость, %, или -1, если экран её не поддерживает.</summary>
        public static int Get()
        {
            try
            {
                using (var s = new ManagementObjectSearcher(@"root\wmi", "SELECT CurrentBrightness, Active FROM WmiMonitorBrightness"))
                    foreach (ManagementObject o in s.Get())
                        using (o)
                            if ((bool)o["Active"]) return Convert.ToInt32(o["CurrentBrightness"]);
            }
            catch (Exception)
            {
            }
            return -1;
        }

        public static bool Set(int percent)
        {
            try
            {
                using (var s = new ManagementObjectSearcher(@"root\wmi", "SELECT * FROM WmiMonitorBrightnessMethods"))
                    foreach (ManagementObject o in s.Get())
                        using (o)
                        {
                            o.InvokeMethod("WmiSetBrightness", new object[] { (uint)1, (byte)Math.Max(0, Math.Min(100, percent)) });
                            return true;
                        }
            }
            catch (Exception)
            {
            }
            return false;
        }
    }

    /// <summary>
    /// Мощность экрана по замеру: точки (яркость, Вт белого изображения сверх чёрного), между ними линейно, и PanelW —
    /// сколько стоит включённая панель с чёрным изображением сверх выключенной (электроника, обновление кадров).
    /// </summary>
    sealed class ScreenModel
    {
        public readonly SortedDictionary<int, double> Points = new SortedDictionary<int, double>();
        public DateTime MeasuredAt;
        public double PanelW = double.NaN;  // NaN — не измерено (замер до 06.10 или Windows не выключила экран)

        public double At(int brightness)
        {
            if (Points.Count < 2 || brightness < 0) return double.NaN;
            int prevB = -1;
            double prevW = 0;
            foreach (var kv in Points)
            {
                if (brightness <= kv.Key)
                {
                    if (prevB < 0) return kv.Value;
                    return prevW + (kv.Value - prevW) * (brightness - prevB) / Math.Max(1, kv.Key - prevB);
                }
                prevB = kv.Key;
                prevW = kv.Value;
            }
            return prevW;
        }

        // В настройках: «2026-10-04T23:40:00|0:0;60:3.1;100:5.6|1.4» (последнее — PanelW, может отсутствовать)
        public static ScreenModel Load()
        {
            try
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\BatteryCheck"))
                {
                    var s = k == null ? null : k.GetValue("ScreenModel") as string;
                    if (string.IsNullOrEmpty(s)) return null;
                    var parts = s.Split('|');
                    var m = new ScreenModel();
                    DateTime.TryParse(parts[0], CultureInfo.InvariantCulture, DateTimeStyles.None, out m.MeasuredAt);
                    foreach (var p in parts[1].Split(';'))
                    {
                        var kv = p.Split(':');
                        m.Points[int.Parse(kv[0], CultureInfo.InvariantCulture)] = double.Parse(kv[1], CultureInfo.InvariantCulture);
                    }
                    double panel;
                    if (parts.Length > 2 && double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out panel)) m.PanelW = panel;
                    return m.Points.Count >= 2 ? m : null;
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        public void Save()
        {
            var pts = new List<string>();
            foreach (var kv in Points) pts.Add(kv.Key.ToString(CultureInfo.InvariantCulture) + ":" + kv.Value.ToString("0.###", CultureInfo.InvariantCulture));
            try
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\BatteryCheck"))
                    k.SetValue("ScreenModel", MeasuredAt.ToString("s", CultureInfo.InvariantCulture) + "|" + string.Join(";", pts) +
                        (double.IsNaN(PanelW) ? "" : "|" + PanelW.ToString("0.###", CultureInfo.InvariantCulture)));
            }
            catch (Exception)
            {
            }
        }
    }
}
