using System;
using System.Runtime.InteropServices;
using Drawing = System.Drawing;
using WinForms = System.Windows.Forms;

namespace BatteryCheck
{
    /// <summary>
    /// «Доля белого» на экране: средняя линейная яркость субпикселей (R, G, B) по уменьшенному снимку 128×80.
    /// На OLED мощность экрана примерно пропорциональна этой доле: чёрный пиксель не светится и почти ничего не стоит.
    /// Снимок нигде не сохраняется. Около 5–10 мс процессора.
    /// </summary>
    static class ScreenLuma
    {
        const int W = 128, H = 80;
        static readonly double[] Linear = BuildLut();

        static double[] BuildLut()
        {
            var lut = new double[256];
            for (int i = 0; i < 256; i++)
            {
                double c = i / 255.0;
                lut[i] = c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);  // sRGB → линейный свет
            }
            return lut;
        }

        /// <summary>Встроенный экран ноутбука (работает от встроенной графики, а не от NVIDIA); иначе — основной.</summary>
        public static WinForms.Screen InternalScreen()
        {
            string name = Displays.InternalDeviceName();
            foreach (var s in WinForms.Screen.AllScreens)
                if (name != null && string.Equals(s.DeviceName, name, StringComparison.OrdinalIgnoreCase)) return s;
            return WinForms.Screen.PrimaryScreen;
        }

        /// <summary>0…1 или NaN, если снимок не удался.</summary>
        public static double Measure(Drawing.Rectangle r)
        {
            IntPtr sdc = GetDC(IntPtr.Zero);
            if (sdc == IntPtr.Zero) return double.NaN;
            IntPtr mdc = CreateCompatibleDC(sdc);
            var bmi = new BITMAPINFOHEADER { biSize = 40, biWidth = W, biHeight = -H, biPlanes = 1, biBitCount = 32 };
            IntPtr bits;
            IntPtr dib = CreateDIBSection(sdc, ref bmi, 0, out bits, IntPtr.Zero, 0);
            IntPtr old = SelectObject(mdc, dib);
            try
            {
                SetStretchBltMode(mdc, 4);  // HALFTONE — усреднение, а не прореживание пикселей
                SetBrushOrgEx(mdc, 0, 0, IntPtr.Zero);
                if (dib == IntPtr.Zero || !StretchBlt(mdc, 0, 0, W, H, sdc, r.X, r.Y, r.Width, r.Height, 0x00CC0020)) return double.NaN;
                var buf = new byte[W * H * 4];
                Marshal.Copy(bits, buf, 0, buf.Length);
                double sum = 0;
                for (int i = 0; i < buf.Length; i += 4) sum += Linear[buf[i]] + Linear[buf[i + 1]] + Linear[buf[i + 2]];
                return sum / (W * H * 3);
            }
            finally
            {
                SelectObject(mdc, old);
                if (dib != IntPtr.Zero) DeleteObject(dib);
                DeleteDC(mdc);
                ReleaseDC(IntPtr.Zero, sdc);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        struct BITMAPINFOHEADER
        {
            public int biSize, biWidth, biHeight;
            public short biPlanes, biBitCount;
            public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
        }

        [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hwnd);
        [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
        [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll")] static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFOHEADER bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);
        [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
        [DllImport("gdi32.dll")] static extern int SetStretchBltMode(IntPtr dc, int mode);
        [DllImport("gdi32.dll")] static extern bool SetBrushOrgEx(IntPtr dc, int x, int y, IntPtr prev);
        [DllImport("gdi32.dll")] static extern bool StretchBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, int sw, int sh, uint rop);
    }

    /// <summary>Выключить и включить экраны, как это делает Windows по тайм-ауту (для фазы замера «экран выключен»).</summary>
    static class ScreenPower
    {
        const int WM_SYSCOMMAND = 0x0112, SC_MONITORPOWER = 0xF170;

        public static void Off(IntPtr hwnd)
        {
            SendMessage(hwnd, WM_SYSCOMMAND, (IntPtr)SC_MONITORPOWER, (IntPtr)2);
        }

        /// <summary>Сообщение «включить» Windows 11 выполняет не всегда — надёжнее сдвиг мыши туда и обратно, как от пользователя.</summary>
        public static void On(IntPtr hwnd)
        {
            SendMessage(hwnd, WM_SYSCOMMAND, (IntPtr)SC_MONITORPOWER, (IntPtr)(-1));
            mouse_event(0x0001, 1, 0, 0, UIntPtr.Zero);   // MOUSEEVENTF_MOVE
            mouse_event(0x0001, -1, 0, 0, UIntPtr.Zero);
        }

        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] static extern void mouse_event(uint flags, int dx, int dy, uint data, UIntPtr extra);
    }

    /// <summary>Тестовое изображение на весь экран для замера: чёрное или белое. Esc или щелчок — отмена.</summary>
    sealed class TestPatternForm : WinForms.Form
    {
        readonly WinForms.Label label;
        public event Action Cancelled;

        public TestPatternForm(Drawing.Rectangle bounds)
        {
            FormBorderStyle = WinForms.FormBorderStyle.None;
            StartPosition = WinForms.FormStartPosition.Manual;
            Bounds = bounds;
            TopMost = true;
            ShowInTaskbar = false;
            KeyPreview = true;
            BackColor = Drawing.Color.Black;
            label = new WinForms.Label
            {
                AutoSize = true,
                Location = new Drawing.Point(28, 24),
                Font = new Drawing.Font("Segoe UI", 11f),
                BackColor = Drawing.Color.Transparent,
            };
            Controls.Add(label);
            KeyDown += (s, e) => { if (e.KeyCode == WinForms.Keys.Escape) Cancel(); };
            MouseClick += (s, e) => Cancel();
            label.MouseClick += (s, e) => Cancel();
        }

        /// <summary>Подпись мелкая и почти в тон фону — чтобы не влиять на замер.</summary>
        public void SetPattern(bool white, string text)
        {
            BackColor = white ? Drawing.Color.White : Drawing.Color.Black;
            label.ForeColor = white ? Drawing.Color.FromArgb(205, 205, 205) : Drawing.Color.FromArgb(50, 50, 50);
            label.Text = text;
        }

        void Cancel()
        {
            var h = Cancelled;
            if (h != null) h();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            WinForms.Cursor.Hide();
            Activate();
        }

        protected override void OnFormClosed(WinForms.FormClosedEventArgs e)
        {
            WinForms.Cursor.Show();
            base.OnFormClosed(e);
        }
    }
}
