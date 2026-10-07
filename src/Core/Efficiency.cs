using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace BatteryCheck
{
    /// <summary>
    /// «Режим эффективности», как в диспетчере задач: EcoQoS (ProcessPowerThrottling, EXECUTION_SPEED) — Windows держит
    /// процесс на энергоэффективных ядрах при низкой частоте, — и низший приоритет. Без прав администратора — только
    /// для процессов этого пользователя; службы (SYSTEM) и программы, запущенные от администратора, Windows не даст изменить.
    /// Выключение снимает EcoQoS (решает снова Windows) и возвращает обычный приоритет, если он низший.
    /// </summary>
    static class Efficiency
    {
        /// <summary>Все процессы с этим именем (без .exe). changed — сколько изменено, denied — скольким Windows отказала.</summary>
        public static void Apply(string name, bool on, out int changed, out int denied)
        {
            changed = denied = 0;
            Process[] list;
            try { list = Process.GetProcessesByName(name); }
            catch (Exception) { return; }
            foreach (var p in list)
                using (p)
                {
                    bool? state = IsOn(p.Id);
                    if (state == on) continue;  // уже так — например, процесс давно в списке
                    if (Set(p.Id, on)) changed++;
                    else denied++;
                }
        }

        /// <summary>Можно ли менять процессы с этим именем: true — хотя бы один открывается на изменение, false — ни один (служба, администратор), null — не запущена.</summary>
        public static bool? CanSet(string name)
        {
            Process[] list;
            try { list = Process.GetProcessesByName(name); }
            catch (Exception) { return null; }
            if (list.Length == 0) return null;
            bool any = false;
            foreach (var p in list)
                using (p)
                {
                    IntPtr h = OpenProcess(PROCESS_SET_INFORMATION, false, p.Id);
                    if (h != IntPtr.Zero) { any = true; CloseHandle(h); }
                }
            return any;
        }

        /// <summary>EcoQoS включён для процесса; null — не удалось узнать (нет доступа, процесс завершился).</summary>
        public static bool? IsOn(int pid)
        {
            IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero) return null;
            try
            {
                var st = new PROCESS_POWER_THROTTLING_STATE { Version = 1 };  // без версии Windows отказывает
                if (!GetProcessInformation(h, ProcessPowerThrottling, ref st, Marshal.SizeOf(typeof(PROCESS_POWER_THROTTLING_STATE)))) return null;
                return (st.ControlMask & EXECUTION_SPEED) != 0 && (st.StateMask & EXECUTION_SPEED) != 0;
            }
            catch (EntryPointNotFoundException) { return null; }  // до Windows 11
            finally { CloseHandle(h); }
        }

        public static bool Set(int pid, bool on)
        {
            IntPtr h = OpenProcess(PROCESS_SET_INFORMATION | PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero) return false;
            try
            {
                var st = new PROCESS_POWER_THROTTLING_STATE
                {
                    Version = 1,
                    // И запросы частого таймера этой программы Windows игнорирует: иначе чип не уходит в глубокий сон.
                    ControlMask = on ? EXECUTION_SPEED | IGNORE_TIMER_RESOLUTION : 0,  // 0 — снова решает Windows
                    StateMask = on ? EXECUTION_SPEED | IGNORE_TIMER_RESOLUTION : 0,
                };
                if (!SetProcessInformation(h, ProcessPowerThrottling, ref st, Marshal.SizeOf(typeof(PROCESS_POWER_THROTTLING_STATE)))) return false;
                uint cls = GetPriorityClass(h);
                if (on && cls != IDLE_PRIORITY_CLASS) SetPriorityClass(h, IDLE_PRIORITY_CLASS);
                else if (!on && cls == IDLE_PRIORITY_CLASS) SetPriorityClass(h, NORMAL_PRIORITY_CLASS);
                return true;
            }
            catch (EntryPointNotFoundException) { return false; }
            finally { CloseHandle(h); }
        }

        [StructLayout(LayoutKind.Sequential)]
        struct PROCESS_POWER_THROTTLING_STATE
        {
            public uint Version, ControlMask, StateMask;
        }

        const int ProcessPowerThrottling = 4;
        const uint EXECUTION_SPEED = 0x1, IGNORE_TIMER_RESOLUTION = 0x4;
        const uint PROCESS_SET_INFORMATION = 0x0200, PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        const uint IDLE_PRIORITY_CLASS = 0x40, NORMAL_PRIORITY_CLASS = 0x20;

        [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool SetProcessInformation(IntPtr h, int infoClass, ref PROCESS_POWER_THROTTLING_STATE info, int size);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool GetProcessInformation(IntPtr h, int infoClass, ref PROCESS_POWER_THROTTLING_STATE info, int size);
        [DllImport("kernel32.dll")] static extern uint GetPriorityClass(IntPtr h);
        [DllImport("kernel32.dll")] static extern bool SetPriorityClass(IntPtr h, uint cls);
    }
}
