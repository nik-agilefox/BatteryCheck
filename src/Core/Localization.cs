using System;
using System.Globalization;
using System.Threading;
using Microsoft.Win32;

namespace BatteryCheck
{
    /// <summary>
    /// Языки интерфейса: английский (по умолчанию) и украинский. Строки — парами прямо в коде: L.T("Battery", "Батарея"),
    /// поэтому перевод виден рядом с местом использования. Выбор хранится в HKCU\Software\BatteryCheck\Language.
    /// Числа форматируются по языку: en-US (23.4 W) или uk-UA (23,4 Вт).
    /// </summary>
    static class L
    {
        public static bool Ua { get; private set; }

        public static string T(string en, string ua)
        {
            return Ua ? ua : en;
        }

        /// <summary>Множественное число: англ. 1 / много; укр. 1, 21… / 2–4, 22–24… / 5–20, 25… (как «1 раз, 2 рази, 5 разів»).</summary>
        public static string Plural(long n, string enOne, string enMany, string uaOne, string uaFew, string uaMany)
        {
            if (!Ua) return n == 1 ? enOne : enMany;
            long m10 = Math.Abs(n) % 10, m100 = Math.Abs(n) % 100;
            if (m10 == 1 && m100 != 11) return uaOne;
            if (m10 >= 2 && m10 <= 4 && (m100 < 12 || m100 > 14)) return uaFew;
            return uaMany;
        }

        public static void Apply(bool ua)
        {
            Ua = ua;
            var c = CultureInfo.GetCultureInfo(ua ? "uk-UA" : "en-US");
            CultureInfo.DefaultThreadCurrentCulture = c;
            CultureInfo.DefaultThreadCurrentUICulture = c;
            Thread.CurrentThread.CurrentCulture = c;
            Thread.CurrentThread.CurrentUICulture = c;
        }

        /// <summary>Сохранённый язык; по умолчанию — английский.</summary>
        public static bool LoadSaved()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\BatteryCheck"))
                    return k != null && (k.GetValue("Language") as string) == "ua";
            }
            catch (Exception)
            {
                return false;
            }
        }

        public static void Save()
        {
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(@"Software\BatteryCheck"))
                    k.SetValue("Language", Ua ? "ua" : "en");
            }
            catch (Exception)
            {
            }
        }
    }
}
