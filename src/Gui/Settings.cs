using System;
using System.Globalization;
using Microsoft.Win32;

namespace BatteryCheck
{
    /// <summary>Настройки и запомненные значения в HKCU\Software\BatteryCheck. Ошибки чтения и записи не критичны.</summary>
    static class Settings
    {
        const string Key = @"Software\BatteryCheck";

        public static double GetDouble(string name, double fallback)
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(Key))
                {
                    var s = k == null ? null : k.GetValue(name) as string;
                    double v;
                    return s != null && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? v : fallback;
                }
            }
            catch (Exception)
            {
                return fallback;
            }
        }

        public static string GetString(string name, string fallback)
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(Key))
                    return (k == null ? null : k.GetValue(name) as string) ?? fallback;
            }
            catch (Exception)
            {
                return fallback;
            }
        }

        public static void SetString(string name, string value)
        {
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(Key)) k.SetValue(name, value ?? "");
            }
            catch (Exception)
            {
            }
        }

        public static void SetDouble(string name, double value)
        {
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(Key))
                {
                    if (double.IsNaN(value)) { if (k.GetValue(name) != null) k.DeleteValue(name); }
                    else k.SetValue(name, value.ToString("R", CultureInfo.InvariantCulture));
                }
            }
            catch (Exception)
            {
            }
        }
    }
}
