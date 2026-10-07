using System;
using System.Runtime.InteropServices;

namespace BatteryCheck
{
    /// <summary>Настоящая система для профиля «На батарее»: яркость (WMI), частота встроенного экрана, журнал.</summary>
    sealed class SystemProfileHost : IProfileHost
    {
        readonly Func<string> logDir;
        readonly Action beforeBrightness;

        /// <param name="beforeBrightness">Перед сменой яркости — подсказка журналу пробуждений: смена яркости будит NVIDIA.</param>
        public SystemProfileHost(Func<string> logDir, Action beforeBrightness)
        {
            this.logDir = logDir;
            this.beforeBrightness = beforeBrightness;
        }

        public int GetBrightness() { return Brightness.Get(); }

        public bool SetBrightness(int pct)
        {
            if (beforeBrightness != null) beforeBrightness();
            return Brightness.Set(pct);
        }

        static string Screen { get { return ScreenLuma.InternalScreen().DeviceName; } }

        public int GetRefresh() { return Advisor.RefreshRate(Screen); }
        public int MinRefresh() { return Advisor.MinRefreshRate(Screen); }

        public bool SetRefresh(int hz)
        {
            var dm = new Advisor.DEVMODE { dmSize = (short)Marshal.SizeOf(typeof(Advisor.DEVMODE)) };
            if (!Advisor.EnumDisplaySettings(Screen, -1, ref dm)) return false;
            dm.dmDisplayFrequency = hz;
            dm.dmFields = DM_DISPLAYFREQUENCY;
            // Сначала проверка, потом смена без записи в реестр (флаг 0): после сбоя перезагрузка вернёт прежний режим.
            if (ChangeDisplaySettingsEx(Screen, ref dm, IntPtr.Zero, CDS_TEST, IntPtr.Zero) != 0) return false;
            return ChangeDisplaySettingsEx(Screen, ref dm, IntPtr.Zero, 0, IntPtr.Zero) == 0;
        }

        public bool ResetRefresh()
        {
            return ChangeDisplaySettingsEx(Screen, IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero) == 0;  // режим из реестра
        }

        public void SetEfficiency(string name, bool on, out int changed, out int denied)
        {
            Efficiency.Apply(name, on, out changed, out denied);
        }

        public void Log(string action, string from, string to, string by)
        {
            try { ActionLog.Append(logDir(), action, from, to, by); }
            catch (Exception) { }  // журнал не записался — само действие уже сделано
        }

        const int DM_DISPLAYFREQUENCY = 0x400000;
        const uint CDS_TEST = 0x2;

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern int ChangeDisplaySettingsEx(string device, ref Advisor.DEVMODE dm, IntPtr hwnd, uint flags, IntPtr param);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern int ChangeDisplaySettingsEx(string device, IntPtr dm, IntPtr hwnd, uint flags, IntPtr param);
    }

    /// <summary>Режим питания Windows для работы от батареи — тот же, что ползунок в Параметрах (постоянная настройка).</summary>
    static class PowerModeControl
    {
        public static readonly string[] Codes = { "efficiency", "balanced", "performance", "best" };

        static Guid GuidOf(string code)
        {
            switch (code)
            {
                case "efficiency": return new Guid("961cc777-2547-4f9d-8174-7d86181b8a7a");
                case "performance": return new Guid("3af9b8d9-7c97-431d-ad78-34a8bfea439f");
                case "best": return new Guid("ded574b5-45a0-4f42-8737-46345c09c238");
                default: return Guid.Empty;  // «Сбалансированный»
            }
        }

        /// <summary>Код режима для батареи или null, если Windows не умеет отдельной настройки (до Windows 11).</summary>
        public static string GetDc()
        {
            try
            {
                Guid g;
                if (PowerGetUserConfiguredDCPowerMode(out g) != 0) return null;
                foreach (var c in Codes) if (GuidOf(c) == g) return c;
                return null;
            }
            catch (EntryPointNotFoundException) { return null; }
        }

        public static bool SetDc(string code)
        {
            try
            {
                var g = GuidOf(code);
                return PowerSetUserConfiguredDCPowerMode(ref g) == 0;
            }
            catch (EntryPointNotFoundException) { return false; }
        }

        [DllImport("powrprof.dll")] static extern uint PowerGetUserConfiguredDCPowerMode(out Guid mode);
        [DllImport("powrprof.dll")] static extern uint PowerSetUserConfiguredDCPowerMode(ref Guid mode);
    }

    /// <summary>Настройки и состояние профиля в HKCU\Software\BatteryCheck (состояние — чтобы откатить и после сбоя).</summary>
    static class ProfileStore
    {
        public static ProfileSettings LoadSettings()
        {
            var s = new ProfileSettings
            {
                Brightness = (int)Settings.GetDouble("ProfileBrightness", 0),
                LowerRefresh = Settings.GetDouble("ProfileLowerRefresh", 0) != 0,
            };
            s.EfficiencyApps.AddRange(Split(Settings.GetString("ProfileEfficiencyApps", "")));
            return s;
        }

        public static void SaveSettings(ProfileSettings s)
        {
            Settings.SetDouble("ProfileBrightness", s.Brightness);
            Settings.SetDouble("ProfileLowerRefresh", s.LowerRefresh ? 1 : 0);
            Settings.SetString("ProfileEfficiencyApps", string.Join("|", s.EfficiencyApps));
        }

        public static ProfileState LoadState()
        {
            var st = new ProfileState
            {
                Applied = Settings.GetDouble("ProfileApplied", 0) != 0,
                PrevBrightness = (int)Settings.GetDouble("ProfilePrevBrightness", -1),
                SetBrightness = (int)Settings.GetDouble("ProfileSetBrightness", -1),
                PrevHz = (int)Settings.GetDouble("ProfilePrevHz", -1),
                SetHz = (int)Settings.GetDouble("ProfileSetHz", -1),
            };
            st.EffApplied.AddRange(Split(Settings.GetString("ProfileEffApplied", "")));
            return st;
        }

        public static void SaveState(ProfileState st)
        {
            Settings.SetDouble("ProfileApplied", st.Applied ? 1 : 0);
            Settings.SetDouble("ProfilePrevBrightness", st.PrevBrightness);
            Settings.SetDouble("ProfileSetBrightness", st.SetBrightness);
            Settings.SetDouble("ProfilePrevHz", st.PrevHz);
            Settings.SetDouble("ProfileSetHz", st.SetHz);
            Settings.SetString("ProfileEffApplied", string.Join("|", st.EffApplied));
        }

        static string[] Split(string s)
        {
            return s.Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries);
        }
    }
}
