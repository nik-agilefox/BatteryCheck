using System;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Drawing = System.Drawing;
using WinForms = System.Windows.Forms;

namespace BatteryCheck
{
    /// <summary>
    /// Значок в области уведомлений: только цветные цифры, без плашки, — текущее потребление в ваттах
    /// (при работе от батареи) или «AC» (от сети). Цвет показывает состояние, подсказка — подробности.
    /// Цифры лежат прямо на панели задач, поэтому оттенки подбираются под её тему (она отдельна от темы приложений).
    /// </summary>
    sealed class TrayIcon : IDisposable
    {
        readonly WinForms.NotifyIcon icon = new WinForms.NotifyIcon();
        readonly WinForms.ContextMenuStrip menu = new WinForms.ContextMenuStrip();
        readonly WinForms.ToolStripMenuItem openItem, resetItem, gpuItem, notifyItem, autostartItem, exitItem;
        string shownKey;
        Drawing.Icon current;

        public event Action OpenRequested, ResetRequested, ExitRequested;
        public event Action<bool> GpuPollingChanged;
        public event Action<bool> NotificationsChanged;
        public Func<bool> GetNotifications;
        public Func<Theme> GetTheme;
        public Func<bool> GetGpuPolling;

        public TrayIcon()
        {
            openItem = new WinForms.ToolStripMenuItem();
            openItem.Font = new Drawing.Font(openItem.Font, Drawing.FontStyle.Bold);
            openItem.Click += (s, e) => Raise(OpenRequested);
            resetItem = new WinForms.ToolStripMenuItem();
            resetItem.Click += (s, e) => Raise(ResetRequested);
            gpuItem = new WinForms.ToolStripMenuItem();
            gpuItem.Click += (s, e) =>
            {
                gpuItem.Checked = !gpuItem.Checked;
                var h = GpuPollingChanged;
                if (h != null) h(gpuItem.Checked);
            };
            notifyItem = new WinForms.ToolStripMenuItem();
            notifyItem.Click += (s, e) =>
            {
                notifyItem.Checked = !notifyItem.Checked;
                var h = NotificationsChanged;
                if (h != null) h(notifyItem.Checked);
            };
            autostartItem = new WinForms.ToolStripMenuItem();
            autostartItem.Click += (s, e) =>
            {
                Autostart.Enabled = !Autostart.Enabled;
                autostartItem.Checked = Autostart.Enabled;
            };
            exitItem = new WinForms.ToolStripMenuItem();
            exitItem.Click += (s, e) => Raise(ExitRequested);
            Relabel();

            menu.Items.AddRange(new WinForms.ToolStripItem[]
            {
                openItem, new WinForms.ToolStripSeparator(), resetItem, gpuItem, notifyItem, autostartItem, new WinForms.ToolStripSeparator(), exitItem
            });
            menu.Opening += (s, e) =>
            {
                autostartItem.Checked = Autostart.Enabled;
                if (GetGpuPolling != null) gpuItem.Checked = GetGpuPolling();
                if (GetNotifications != null) notifyItem.Checked = GetNotifications();
                var t = GetTheme != null ? GetTheme() : null;
                if (t != null) menu.Renderer = new WinForms.ToolStripProfessionalRenderer(new MenuColors(t)) { RoundedEdges = false };
                foreach (WinForms.ToolStripItem item in menu.Items)
                    if (t != null) item.ForeColor = ToGdi(t.TextPrimary);
            };

            icon.ContextMenuStrip = menu;
            icon.Text = "Battery Check";
            icon.MouseClick += (s, e) => { if (e.Button == WinForms.MouseButtons.Left) Raise(OpenRequested); };
            icon.BalloonTipClicked += (s, e) => Raise(OpenRequested);
            SetIcon("…", Palette(TaskbarIsLight()).Idle);
            icon.Visible = true;
        }

        /// <summary>Цвета цифр под фон панели задач: на тёмной — светлые шаги, на светлой — тёмные.</summary>
        internal sealed class IconColors
        {
            public Drawing.Color Battery, Low, Critical, Charging, Idle;
        }

        internal static IconColors Palette(bool lightTaskbar)
        {
            return lightTaskbar
                ? new IconColors
                {
                    Battery = Drawing.Color.FromArgb(0x1c, 0x5c, 0xab),   // синий, шаг 550
                    Low = Drawing.Color.FromArgb(0xa8, 0x6d, 0x00),       // тёмный янтарный: жёлтый на светлом не читается
                    Critical = Drawing.Color.FromArgb(0xd0, 0x3b, 0x3b),
                    Charging = Drawing.Color.FromArgb(0x00, 0x63, 0x00),
                    Idle = Drawing.Color.FromArgb(0x52, 0x51, 0x4e),
                }
                : new IconColors
                {
                    Battery = Drawing.Color.FromArgb(0x6d, 0xa7, 0xec),   // синий, шаг 300
                    Low = Drawing.Color.FromArgb(0xfa, 0xb2, 0x19),
                    Critical = Drawing.Color.FromArgb(0xff, 0x6b, 0x6b),
                    Charging = Drawing.Color.FromArgb(0x3c, 0xc8, 0x3c),
                    Idle = Drawing.Color.FromArgb(0xc3, 0xc2, 0xb7),
                };
        }

        /// <summary>Тема панели задач («системная» в настройках Windows), а не тема приложений.</summary>
        static bool TaskbarIsLight()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    object v = k == null ? null : k.GetValue("SystemUsesLightTheme");
                    return v is int && (int)v == 1;
                }
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>Подписи меню на текущем языке; подсказка значка обновится со следующим замером.</summary>
        public void Relabel()
        {
            openItem.Text = L.T("Open Battery Check", "Відкрити Battery Check");
            resetItem.Text = L.T("Reset discharge session", "Скинути сесію розряду");
            gpuItem.Text = L.T("Poll NVIDIA power", "Опитувати потужність NVIDIA");
            notifyItem.Text = L.T("Battery notifications", "Сповіщення про батарею");
            autostartItem.Text = L.T("Start with Windows", "Запускати разом з Windows");
            exitItem.Text = L.T("Exit", "Вихід");
            shownTip = null;
        }

        public void ShowHint(string title, string text)
        {
            // Пределы Windows для всплывающей подсказки: заголовок — 63 символа, текст — 255.
            if (title.Length > 63) title = title.Substring(0, 62) + "…";
            if (text.Length > 255) text = text.Substring(0, 254) + "…";
            icon.ShowBalloonTip(5000, title, text, WinForms.ToolTipIcon.Info);
        }

        /// <summary>
        /// WinForms при любом изменении значка заново отправляет в Windows и подсказку, и открытая подсказка мерцает.
        /// Поэтому, пока курсор над значком, ничего не обновляем.
        /// </summary>
        bool IsHovered()
        {
            try
            {
                if (iconId == null)
                {
                    var t = typeof(WinForms.NotifyIcon);
                    var f = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
                    var window = (WinForms.NativeWindow)t.GetField("window", f).GetValue(icon);
                    iconId = new NOTIFYICONIDENTIFIER { cbSize = Marshal.SizeOf(typeof(NOTIFYICONIDENTIFIER)), hWnd = window.Handle, uID = (int)t.GetField("id", f).GetValue(icon) };
                }
                var id = iconId.Value;
                RECT r;
                if (Shell_NotifyIconGetRect(ref id, out r) != 0) return false;
                var p = WinForms.Cursor.Position;
                return p.X >= r.Left && p.X < r.Right && p.Y >= r.Top && p.Y < r.Bottom;
            }
            catch (Exception)
            {
                return false;  // устройство WinForms изменилось — просто обновляем как раньше
            }
        }

        NOTIFYICONIDENTIFIER? iconId;
        string shownTip;

        /// <param name="totalW">Полная мощность: от батареи — разряд, от сети — оценка входа от блока питания (NaN — неизвестна).</param>
        public void Update(Snapshot snap, Theme theme, double totalW)
        {
            if (IsHovered()) return;
            var b = snap.Sample.Battery;
            var colors = Palette(TaskbarIsLight());
            string text, tip;
            Drawing.Color color;
            // От сети — тоже число: оценка полной мощности от блока; «AC» — только если оценки нет.
            string watts = double.IsNaN(totalW) ? "AC" : Math.Min(999, Math.Round(totalW)).ToString("0");

            if (!snap.HasBattery)
            {
                text = watts;
                tip = double.IsNaN(totalW) ? L.T("no battery", "без батареї")
                                           : string.Format(L.T("CPU + graphics {0} W", "процесор + графіка {0} Вт"), watts);
                color = colors.Idle;
            }
            else if (b.Discharging && b.HasRate)
            {
                double w = Math.Abs(b.RateW);
                text = Math.Min(999, Math.Round(w)).ToString("0");
                tip = string.Format("{0:0} {1} · {2} · ≈ {3}", w, Fmt.WUnit, Pct(snap.SocPct), Fmt.Hours(snap.HoursLeft));
                color = b.Critical || snap.SocPct < 10 ? colors.Critical : snap.SocPct < 20 ? colors.Low : colors.Battery;
            }
            else if (b.Charging)
            {
                text = watts;
                tip = string.Format(L.T("≈{0} W from AC · {1} · full in {2}", "≈{0} Вт з мережі · {1} · до 100 % {2}"),
                    watts, Pct(snap.SocPct), Fmt.Hours(snap.HoursLeft));
                if (double.IsNaN(totalW))
                    tip = string.Format(L.T("Charging {0} · full in ≈ {1}", "Заряд {0} · до 100 % ≈ {1}"), Pct(snap.SocPct), Fmt.Hours(snap.HoursLeft));
                color = colors.Charging;
            }
            else
            {
                text = watts;
                tip = double.IsNaN(totalW) ? L.T("On AC · charge ", "Від мережі · заряд ") + Pct(snap.SocPct)
                                           : string.Format(L.T("≈{0} W from AC (estimate) · {1}", "≈{0} Вт з мережі (оцінка) · {1}"), watts, Pct(snap.SocPct));
                color = colors.Idle;
            }

            tip = "Battery Check · " + tip;
            if (tip.Length > 63) tip = tip.Substring(0, 63);  // предел NotifyIcon.Text в .NET Framework
            if (tip != shownTip)
            {
                icon.Text = tip;
                shownTip = tip;
            }
            SetIcon(text, color);
        }

        static string Pct(double v)
        {
            return double.IsNaN(v) ? "—" : Math.Round(v).ToString("0") + " %";
        }

        /// <summary>Перерисовывает значок, только если изменились текст или цвет (в том числе из-за темы панели задач).</summary>
        void SetIcon(string text, Drawing.Color color)
        {
            string key = text + "|" + color.ToArgb();
            if (key == shownKey) return;
            shownKey = key;

            int size = Math.Max(16, WinForms.SystemInformation.SmallIconSize.Width);
            var old = current;
            current = Render(text, color, size);
            icon.Icon = current;
            if (old != null) old.Dispose();
        }

        // «AC» — почти на всю высоту; число — на 60 % высоты, под ним подпись WATT на 24 %.
        // По ширине текст можно сузить не больше чем на 20 %, дальше — уменьшать целиком.
        const float TextHeight = 0.88f, DigitsHeight = 0.60f, WattHeight = 0.24f, MaxSqueeze = 0.8f;

        /// <summary>
        /// Текст контуром, вписанный в прямоугольник по реальным границам глифов. stretch — растянуть на всю ширину
        /// (широкое начертание для подписи), иначе — по высоте с сужением не больше MaxSqueeze.
        /// </summary>
        static void DrawFit(Drawing.Graphics g, Drawing.Brush brush, string text, Drawing.RectangleF box, bool stretch)
        {
            using (var path = new GraphicsPath())
            using (var family = new Drawing.FontFamily("Segoe UI"))
            {
                path.AddString(text, family, (int)Drawing.FontStyle.Bold, 100f, Drawing.PointF.Empty, Drawing.StringFormat.GenericTypographic);
                var b = path.GetBounds();
                float sy = box.Height / b.Height, sx = sy;
                if (stretch) sx = box.Width / b.Width;
                else if (b.Width * sx > box.Width)
                {
                    sx = box.Width / b.Width;
                    if (sx < sy * MaxSqueeze) sy = sx / MaxSqueeze;
                }
                using (var m = new Matrix())
                {
                    m.Translate(box.X + box.Width / 2, box.Y + box.Height / 2);
                    m.Scale(sx, sy);
                    m.Translate(-(b.X + b.Width / 2), -(b.Y + b.Height / 2));
                    path.Transform(m);
                }
                g.FillPath(brush, path);
            }
        }

        /// <summary>
        /// Текст как контур (а не DrawString): на прозрачном фоне нет цветной бахромы ClearType, а размер
        /// подгоняется по реальным границам глифов, без пустых полей шрифта.
        /// </summary>
        internal static Drawing.Icon Render(string text, Drawing.Color color, int size)
        {
            using (var bmp = new Drawing.Bitmap(size, size, Drawing.Imaging.PixelFormat.Format32bppArgb))
            {
                using (var g = Drawing.Graphics.FromImage(bmp))
                using (var brush = new Drawing.SolidBrush(color))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    if (text.Length > 0 && char.IsDigit(text[0]))
                    {
                        // Число сверху, под ним мелкая подпись WATT, растянутая на всю ширину (широкое начертание).
                        DrawFit(g, brush, text, new Drawing.RectangleF(0, size * 0.04f, size, size * DigitsHeight), false);
                        DrawFit(g, brush, "WATT", new Drawing.RectangleF(0, size * (1 - WattHeight - 0.03f), size, size * WattHeight), true);
                    }
                    else
                    {
                        DrawFit(g, brush, text, new Drawing.RectangleF(0, size * (1 - TextHeight) / 2, size, size * TextHeight), false);
                    }
                }
                IntPtr hicon = bmp.GetHicon();
                try
                {
                    using (var tmp = Drawing.Icon.FromHandle(hicon))
                        return (Drawing.Icon)tmp.Clone();
                }
                finally
                {
                    DestroyIcon(hicon);
                }
            }
        }

        internal static Drawing.Color ToGdi(System.Windows.Media.Color c)
        {
            return Drawing.Color.FromArgb(c.A, c.R, c.G, c.B);
        }

        static void Raise(Action a)
        {
            if (a != null) a();
        }

        public void Dispose()
        {
            icon.Visible = false;
            icon.Dispose();
            menu.Dispose();
            if (current != null) current.Dispose();
        }

        [DllImport("user32.dll")]
        static extern bool DestroyIcon(IntPtr handle);

        [StructLayout(LayoutKind.Sequential)]
        struct NOTIFYICONIDENTIFIER
        {
            public int cbSize;
            public IntPtr hWnd;
            public int uID;
            public Guid guidItem;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct RECT
        {
            public int Left, Top, Right, Bottom;
        }

        [DllImport("shell32.dll")]
        static extern int Shell_NotifyIconGetRect(ref NOTIFYICONIDENTIFIER identifier, out RECT iconLocation);

        /// <summary>Цвета меню в тон теме приложения: стандартное меню WinForms всегда светлое.</summary>
        internal sealed class MenuColors : WinForms.ProfessionalColorTable
        {
            readonly Drawing.Color bg, hover, border, separator;

            public MenuColors(Theme t)
            {
                bg = ToGdi(t.Surface);
                hover = ToGdi(t.Hover);
                border = t.IsDark ? Drawing.Color.FromArgb(0x38, 0x38, 0x35) : Drawing.Color.FromArgb(0xe1, 0xe0, 0xd9);
                separator = ToGdi(t.GridLine);
            }

            public override Drawing.Color ToolStripDropDownBackground { get { return bg; } }
            public override Drawing.Color ImageMarginGradientBegin { get { return bg; } }
            public override Drawing.Color ImageMarginGradientMiddle { get { return bg; } }
            public override Drawing.Color ImageMarginGradientEnd { get { return bg; } }
            public override Drawing.Color MenuBorder { get { return border; } }
            public override Drawing.Color MenuItemBorder { get { return hover; } }
            public override Drawing.Color MenuItemSelected { get { return hover; } }
            public override Drawing.Color MenuItemSelectedGradientBegin { get { return hover; } }
            public override Drawing.Color MenuItemSelectedGradientEnd { get { return hover; } }
            public override Drawing.Color SeparatorDark { get { return separator; } }
            public override Drawing.Color SeparatorLight { get { return separator; } }
            public override Drawing.Color CheckBackground { get { return hover; } }
            public override Drawing.Color CheckSelectedBackground { get { return hover; } }
            public override Drawing.Color CheckPressedBackground { get { return hover; } }
        }
    }

    /// <summary>Автозапуск при входе в Windows: запись в HKCU\...\Run, без прав администратора.</summary>
    static class Autostart
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string Name = "BatteryCheck";

        static string Command
        {
            get { return "\"" + System.Reflection.Assembly.GetExecutingAssembly().Location + "\" --background"; }
        }

        public static bool Enabled
        {
            get
            {
                using (var k = Registry.CurrentUser.OpenSubKey(RunKey))
                {
                    var v = k == null ? null : k.GetValue(Name) as string;
                    return v != null && string.Equals(v, Command, StringComparison.OrdinalIgnoreCase);
                }
            }
            set
            {
                using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (value) k.SetValue(Name, Command);
                    else if (k.GetValue(Name) != null) k.DeleteValue(Name);
                }
            }
        }
    }
}
