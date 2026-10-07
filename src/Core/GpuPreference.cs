using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Web.Script.Serialization;
using Microsoft.Win32;

namespace BatteryCheck
{
    /// <summary>Настройка видеокарты для программ (Параметры → Дисплей → Графика); в тестах — подделка.</summary>
    interface IGpuPreferenceStore
    {
        string Get(string exePath);              // строка вида «GpuPreference=2;SwapEffectUpgradeEnable=1;» или null
        void Set(string exePath, string value);  // null — удалить
    }

    /// <summary>
    /// Перевод программ с дискретной видеокарты на встроенную на время «Максимальной экономии» от батареи:
    /// программам с видеопамятью на дискретной карте ставится GpuPreference=1 («Энергосбережение») в
    /// HKCU\Software\Microsoft\DirectX\UserGpuPreferences — без прав администратора. Действует со следующего запуска
    /// программы (видеокарта выбирается, когда программа запускает графику), поэтому пользователю предлагается её перезапустить.
    /// Прежнее значение запоминается и возвращается при выключении (если пользователь его не менял); прочие поля строки сохраняются.
    /// </summary>
    sealed class GpuPreferenceManager
    {
        public const string PowerSaving = "1";
        static readonly HashSet<string> Skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "dwm", "System", "csrss", "BatteryCheckGui", "BatteryCheck", "explorer",
        };

        readonly IGpuPreferenceStore store;
        readonly Func<string, string> exeOf;   // имя процесса → полный путь к exe (или null)
        readonly Dictionary<string, string> saved;  // путь → прежняя строка (null — значения не было)

        public GpuPreferenceManager(IGpuPreferenceStore store, Func<string, string> exeOf, Dictionary<string, string> saved)
        {
            this.store = store;
            this.exeOf = exeOf;
            this.saved = saved ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        public Dictionary<string, string> Saved { get { return saved; } }

        /// <summary>Программам на дискретной карте — «Энергосбережение». Возвращает имена тех, кого перевели сейчас (им нужен перезапуск).</summary>
        public List<string> Update(IEnumerable<ProcessPower> processes, Action<string, string, string> log)
        {
            var moved = new List<string>();
            if (processes == null) return moved;
            foreach (var p in processes)
            {
                if (p.DgpuMemMB < 16 || Skip.Contains(p.Name)) continue;
                string exe = exeOf(p.Name);
                if (exe == null || saved.ContainsKey(exe)) continue;
                string cur = store.Get(exe);
                if (Field(cur, "GpuPreference") == PowerSaving) continue;  // уже так — не наше, не трогаем и потом
                store.Set(exe, WithField(cur, "GpuPreference", PowerSaving));
                saved[exe] = cur;
                moved.Add(p.Name);
                log(p.Name, Describe(cur), Describe(PowerSaving));
            }
            return moved;
        }

        /// <summary>Вернуть прежние значения тем, у кого всё ещё «Энергосбережение» (иначе пользователь поменял сам — оставить).</summary>
        public void RestoreAll(Action<string, string, string> log)
        {
            foreach (var kv in saved)
            {
                string cur = store.Get(kv.Key);
                string name = System.IO.Path.GetFileNameWithoutExtension(kv.Key);
                if (Field(cur, "GpuPreference") != PowerSaving) { log(name, Describe(Field(cur, "GpuPreference")), L.T("changed by you — kept", "змінено вами — залишено")); continue; }
                store.Set(kv.Key, kv.Value == null ? RemoveField(cur, "GpuPreference") : kv.Value);
                log(name, Describe(PowerSaving), Describe(Field(kv.Value, "GpuPreference")));
            }
            saved.Clear();
        }

        static string Describe(string prefOrString)
        {
            string v = prefOrString != null && prefOrString.Contains("=") ? Field(prefOrString, "GpuPreference") : prefOrString;
            switch (v)
            {
                case "1": return L.T("power saving (integrated)", "енергозбереження (вбудована)");
                case "2": return L.T("high performance (discrete)", "висока продуктивність (дискретна)");
                default: return L.T("let Windows decide", "вирішує Windows");
            }
        }

        // ---- Строка настройки: «Ключ=значение;Ключ=значение;» ----

        public static string Field(string s, string key)
        {
            if (s == null) return null;
            foreach (var part in s.Split(';'))
            {
                int i = part.IndexOf('=');
                if (i > 0 && string.Equals(part.Substring(0, i).Trim(), key, StringComparison.OrdinalIgnoreCase)) return part.Substring(i + 1).Trim();
            }
            return null;
        }

        public static string WithField(string s, string key, string value)
        {
            var sb = new StringBuilder();
            bool done = false;
            foreach (var part in (s ?? "").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int i = part.IndexOf('=');
                if (i > 0 && string.Equals(part.Substring(0, i).Trim(), key, StringComparison.OrdinalIgnoreCase)) { sb.Append(key).Append('=').Append(value).Append(';'); done = true; }
                else sb.Append(part).Append(';');
            }
            if (!done) sb.Append(key).Append('=').Append(value).Append(';');
            return sb.ToString();
        }

        static string RemoveField(string s, string key)
        {
            var sb = new StringBuilder();
            foreach (var part in (s ?? "").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int i = part.IndexOf('=');
                if (!(i > 0 && string.Equals(part.Substring(0, i).Trim(), key, StringComparison.OrdinalIgnoreCase))) sb.Append(part).Append(';');
            }
            return sb.Length > 0 ? sb.ToString() : null;
        }

        public static string Serialize(Dictionary<string, string> saved) { return new JavaScriptSerializer().Serialize(saved); }

        public static Dictionary<string, string> Parse(string json)
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(json)) return d;
            try
            {
                var raw = new JavaScriptSerializer().Deserialize<Dictionary<string, string>>(json);
                if (raw != null) foreach (var kv in raw) d[kv.Key] = kv.Value;
            }
            catch (Exception) { }
            return d;
        }
    }

    /// <summary>Настоящая настройка Windows: HKCU\Software\Microsoft\DirectX\UserGpuPreferences.</summary>
    sealed class RegistryGpuPreferenceStore : IGpuPreferenceStore
    {
        const string Key = @"Software\Microsoft\DirectX\UserGpuPreferences";

        public string Get(string exePath)
        {
            using (var k = Registry.CurrentUser.OpenSubKey(Key)) return k == null ? null : k.GetValue(exePath) as string;
        }

        public void Set(string exePath, string value)
        {
            using (var k = Registry.CurrentUser.CreateSubKey(Key))
            {
                if (value == null) { if (k.GetValue(exePath) != null) k.DeleteValue(exePath); }
                else k.SetValue(exePath, value, RegistryValueKind.String);
            }
        }

        /// <summary>Полный путь к exe первого процесса с этим именем (без прав — для процессов этого пользователя и большинства остальных).</summary>
        public static string ExeOf(string processName)
        {
            System.Diagnostics.Process[] list;
            try { list = System.Diagnostics.Process.GetProcessesByName(processName); }
            catch (Exception) { return null; }
            string path = null;
            foreach (var p in list)
                using (p)
                {
                    if (path != null) continue;
                    IntPtr h = OpenProcess(0x1000, false, p.Id);  // PROCESS_QUERY_LIMITED_INFORMATION
                    if (h == IntPtr.Zero) continue;
                    var sb = new StringBuilder(1024);
                    int size = sb.Capacity;
                    if (QueryFullProcessImageName(h, 0, sb, ref size)) path = sb.ToString();
                    CloseHandle(h);
                }
            return path;
        }

        [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder name, ref int size);
    }
}
