using System;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace BatteryCheck
{
    /// <summary>Ключи кистей в ресурсах приложения: элементы ссылаются на них и перекрашиваются при смене темы.</summary>
    static class Keys
    {
        public const string Page = "Page", Surface = "Surface", Hover = "Hover", Border = "Border";
        public const string Text = "Text", Text2 = "Text2", Muted = "Muted";
        public const string Battery = "SeriesBattery", Cpu = "SeriesCpu", Gpu = "SeriesGpu", Igpu = "SeriesIgpu";
        public const string Good = "Good", Warning = "Warning", Critical = "Critical";
    }

    /// <summary>
    /// Цвета интерфейса. Серии графика — первые три слота эталонной палитры (синий, оранжевый, бирюзовый):
    /// они различимы попарно и при нарушениях цветового зрения, в каждой из тем отдельно подобраны шаги.
    /// </summary>
    sealed class Theme
    {
        /// <summary>false, если тема задана параметром --theme и не должна следовать за Windows.</summary>
        public static bool FollowSystem = true;

        public bool IsDark;
        public Color Page, Surface, Hover, BorderColor, TextPrimary, TextSecondary, Muted, GridLine, AxisLine;
        /// <summary>Восемь категориальных цветов в проверенном порядке — для процессов на графике.</summary>
        public Color[] Slots;
        public Color Battery, Cpu, Gpu, Igpu;  // слоты палитры 1–4: батарея, процессор, NVIDIA, встроенная графика
        public Color Good, Warning, Critical;

        public static Theme Create(bool dark)
        {
            var t = new Theme();
            t.IsDark = dark;
            if (dark)
            {
                t.Page = C("#0d0d0d");
                t.Surface = C("#1a1a19");
                t.Hover = C("#2c2c2a");
                t.BorderColor = Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF);
                t.TextPrimary = C("#ffffff");
                t.TextSecondary = C("#c3c2b7");
                t.Muted = C("#898781");
                t.GridLine = C("#2c2c2a");
                t.AxisLine = C("#383835");
                t.Battery = C("#3987e5");
                t.Cpu = C("#d95926");
                t.Gpu = C("#199e70");
                t.Slots = new[] { C("#3987e5"), C("#d95926"), C("#199e70"), C("#c98500"), C("#d55181"), C("#008300"), C("#9085e9"), C("#e66767") };
            }
            else
            {
                t.Page = C("#f9f9f7");
                t.Surface = C("#fcfcfb");
                t.Hover = C("#f0efec");
                t.BorderColor = Color.FromArgb(0x1A, 0x0B, 0x0B, 0x0B);
                t.TextPrimary = C("#0b0b0b");
                t.TextSecondary = C("#52514e");
                t.Muted = C("#898781");
                t.GridLine = C("#e1e0d9");
                t.AxisLine = C("#c3c2b7");
                t.Battery = C("#2a78d6");
                t.Cpu = C("#eb6834");
                t.Gpu = C("#1baf7a");
                t.Slots = new[] { C("#2a78d6"), C("#eb6834"), C("#1baf7a"), C("#eda100"), C("#e87ba4"), C("#008300"), C("#4a3aa7"), C("#e34948") };
            }
            t.Igpu = t.Slots[3];
            // Статусные цвета одинаковы в обеих темах и всегда идут вместе со значком и подписью.
            t.Good = C("#0ca30c");
            t.Warning = C("#fab219");
            t.Critical = C("#d03b3b");
            return t;
        }

        public static bool SystemPrefersDark()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    object v = k == null ? null : k.GetValue("AppsUseLightTheme");
                    return v is int && (int)v == 0;
                }
            }
            catch (Exception)
            {
                return false;
            }
        }

        public void Apply(ResourceDictionary r)
        {
            r[Keys.Page] = Brush(Page);
            r[Keys.Surface] = Brush(Surface);
            r[Keys.Hover] = Brush(Hover);
            r[Keys.Border] = Brush(BorderColor);
            r[Keys.Text] = Brush(TextPrimary);
            r[Keys.Text2] = Brush(TextSecondary);
            r[Keys.Muted] = Brush(Muted);
            r[Keys.Battery] = Brush(Battery);
            r[Keys.Cpu] = Brush(Cpu);
            r[Keys.Gpu] = Brush(Gpu);
            r[Keys.Igpu] = Brush(Igpu);
            r[Keys.Good] = Brush(Good);
            r[Keys.Warning] = Brush(Warning);
            r[Keys.Critical] = Brush(Critical);
        }

        public static SolidColorBrush Brush(Color c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }

        static Color C(string hex)
        {
            return (Color)ColorConverter.ConvertFromString(hex);
        }
    }
}
