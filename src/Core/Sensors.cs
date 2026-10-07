using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace BatteryCheck
{
    /// <summary>
    /// Мощность процессора из счётчиков Windows «Energy Meter» (Intel/AMD RAPL), в мВт.
    /// Читается напрямую через PDH: System.Diagnostics.PerformanceCounter при запуске
    /// загружает весь каталог счётчиков и тратит на это несколько секунд.
    /// </summary>
    sealed class Rapl : IDisposable
    {
        const uint PDH_FMT_DOUBLE = 0x200;

        readonly IntPtr query;
        readonly IntPtr pkg, cores, igpu, dram;

        public double PkgW = double.NaN;    // весь процессор
        public double CoresW = double.NaN;  // вычислительные ядра (PP0)
        public double IgpuW = double.NaN;   // встроенная графика (PP1)
        public double DramW = double.NaN;   // память (часто не поддерживается — 0)

        public bool Available { get { return pkg != IntPtr.Zero; } }

        public Rapl()
        {
            if (PdhOpenQuery(null, IntPtr.Zero, out query) != 0) return;
            pkg = Add("RAPL_Package0_PKG");
            cores = Add("RAPL_Package0_PP0");
            igpu = Add("RAPL_Package0_PP1");
            dram = Add("RAPL_Package0_DRAM");
            PdhCollectQueryData(query);  // PDH отдаёт значение только со второго сбора
        }

        IntPtr Add(string instance)
        {
            IntPtr counter;
            return PdhAddEnglishCounter(query, @"\Energy Meter(" + instance + @")\Power", IntPtr.Zero, out counter) == 0
                ? counter : IntPtr.Zero;
        }

        readonly Stopwatch sinceCollect = Stopwatch.StartNew();

        public void Sample()
        {
            // Мощность считается между двумя сборами; на интервале в миллисекунды (первый замер сразу после
            // инициализации) она случайна — бывали «всплески» до 150 Вт. Такой замер отбрасываем.
            bool tooSoon = sinceCollect.Elapsed.TotalSeconds < 0.2;
            if (query == IntPtr.Zero || PdhCollectQueryData(query) != 0 || tooSoon)
            {
                sinceCollect.Restart();
                PkgW = CoresW = IgpuW = DramW = double.NaN;
                return;
            }
            sinceCollect.Restart();
            PkgW = Read(pkg);
            CoresW = Read(cores);
            IgpuW = Read(igpu);
            DramW = Read(dram);
        }

        static double Read(IntPtr counter)
        {
            if (counter == IntPtr.Zero) return double.NaN;
            uint type;
            PDH_FMT_COUNTERVALUE v;
            if (PdhGetFormattedCounterValue(counter, PDH_FMT_DOUBLE, out type, out v) != 0 || v.CStatus > 1)
                return double.NaN;
            return v.DoubleValue / 1000.0;
        }

        public void Dispose()
        {
            if (query != IntPtr.Zero) PdhCloseQuery(query);
        }

        [StructLayout(LayoutKind.Explicit)]
        struct PDH_FMT_COUNTERVALUE
        {
            [FieldOffset(0)] public uint CStatus;
            [FieldOffset(8)] public double DoubleValue;
        }

        [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
        static extern int PdhOpenQuery(string dataSource, IntPtr userData, out IntPtr query);

        [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
        static extern int PdhAddEnglishCounter(IntPtr query, string path, IntPtr userData, out IntPtr counter);

        [DllImport("pdh.dll")]
        static extern int PdhCollectQueryData(IntPtr query);

        [DllImport("pdh.dll")]
        static extern int PdhGetFormattedCounterValue(IntPtr counter, uint format, out uint type, out PDH_FMT_COUNTERVALUE value);

        [DllImport("pdh.dll")]
        static extern int PdhCloseQuery(IntPtr query);
    }

    /// <summary>
    /// Состояние питания дискретной видеокарты NVIDIA из диспетчера устройств: D0 — работает, D3 — спит.
    /// В отличие от NVML, этот запрос видеокарту не будит.
    /// </summary>
    sealed class GpuPowerState : IDisposable
    {
        static readonly Guid GUID_DEVCLASS_DISPLAY = new Guid("4d36e968-e325-11ce-bfc1-08002be10318");
        const int DIGCF_PRESENT = 0x2;
        const int SPDRP_DEVICEDESC = 0x0, SPDRP_HARDWAREID = 0x1, SPDRP_DEVICE_POWER_DATA = 0x1E;

        readonly IntPtr set;
        SP_DEVINFO_DATA device;
        readonly byte[] buffer = new byte[64];

        public readonly string Name;

        GpuPowerState(IntPtr set, SP_DEVINFO_DATA device, string name)
        {
            this.set = set;
            this.device = device;
            Name = name;
        }

        public static GpuPowerState FindNvidia()
        {
            Guid g = GUID_DEVCLASS_DISPLAY;
            IntPtr set = SetupDiGetClassDevs(ref g, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT);
            if (set == new IntPtr(-1)) return null;

            var d = new SP_DEVINFO_DATA();
            d.cbSize = Marshal.SizeOf(d);
            for (int i = 0; SetupDiEnumDeviceInfo(set, i, ref d); i++)
            {
                string hw = GetString(set, ref d, SPDRP_HARDWAREID);
                if (hw != null && hw.ToUpperInvariant().StartsWith(@"PCI\VEN_10DE"))
                {
                    string name = GetString(set, ref d, SPDRP_DEVICEDESC) ?? "NVIDIA";
                    return new GpuPowerState(set, d, name.Replace("NVIDIA GeForce ", "").Replace("NVIDIA ", ""));
                }
            }
            SetupDiDestroyDeviceInfoList(set);
            return null;
        }

        /// <summary>
        /// Устройство отключено в диспетчере устройств или его драйвер не запущен. Тогда Windows по-прежнему сообщает
        /// D3, но перевести карту в сон некому: на Predator PHN16S-71 отключённая карта тянула +17 Вт — больше, чем спящая.
        /// Обновляется при каждом Read().
        /// </summary>
        public bool Disabled { get; private set; }

        /// <summary>0…3 — D0…D3, -1 — неизвестно.</summary>
        public int Read()
        {
            uint status, problem;
            Disabled = CM_Get_DevNode_Status(out status, out problem, device.DevInst, 0) == 0
                       && (problem == CM_PROB_DISABLED || (status & DN_STARTED) == 0);
            int type, required;
            if (!SetupDiGetDeviceRegistryProperty(set, ref device, SPDRP_DEVICE_POWER_DATA, out type, buffer, buffer.Length, out required))
                return -1;
            uint s = BitConverter.ToUInt32(buffer, 4);  // CM_POWER_DATA.PD_MostRecentPowerState: 1 = D0 … 4 = D3
            return s >= 1 && s <= 4 ? (int)s - 1 : -1;
        }

        static string GetString(IntPtr set, ref SP_DEVINFO_DATA d, int property)
        {
            var buf = new byte[1024];
            int type, required;
            if (!SetupDiGetDeviceRegistryProperty(set, ref d, property, out type, buf, buf.Length, out required))
                return null;
            string s = Encoding.Unicode.GetString(buf, 0, Math.Min(required, buf.Length));
            int z = s.IndexOf('\0');  // для REG_MULTI_SZ берём первую строку
            return z >= 0 ? s.Substring(0, z) : s;
        }

        public void Dispose()
        {
            SetupDiDestroyDeviceInfoList(set);
        }

        [StructLayout(LayoutKind.Sequential)]
        struct SP_DEVINFO_DATA
        {
            public int cbSize;
            public Guid ClassGuid;
            public uint DevInst;
            public IntPtr Reserved;
        }

        [DllImport("setupapi.dll", SetLastError = true)]
        static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, IntPtr enumerator, IntPtr hwndParent, int flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        static extern bool SetupDiEnumDeviceInfo(IntPtr set, int index, ref SP_DEVINFO_DATA data);

        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool SetupDiGetDeviceRegistryProperty(IntPtr set, ref SP_DEVINFO_DATA data, int property,
                                                            out int regType, [Out] byte[] buffer, int size, out int required);

        [DllImport("setupapi.dll", SetLastError = true)]
        static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

        const uint DN_STARTED = 0x8, CM_PROB_DISABLED = 22;

        [DllImport("cfgmgr32.dll")]
        static extern int CM_Get_DevNode_Status(out uint status, out uint problem, uint devInst, int flags);
    }

    /// <summary>
    /// Сон ядер процессора: доля времени в самом глубоком состоянии, которое Windows отдаёт счётчиком «% C3 Time»
    /// (глубже Windows не различает). Чем больше — тем меньше тратит процессор в простое. PDH, без прав администратора.
    /// </summary>
    sealed class CpuIdle : IDisposable
    {
        IntPtr query, c3;
        public double DeepPct = double.NaN;

        public CpuIdle()
        {
            if (PdhOpenQuery(null, IntPtr.Zero, out query) != 0) { query = IntPtr.Zero; return; }
            if (PdhAddEnglishCounter(query, @"\Processor Information(_Total)\% C3 Time", IntPtr.Zero, out c3) != 0) c3 = IntPtr.Zero;
            PdhCollectQueryData(query);
        }

        public void Sample()
        {
            if (query == IntPtr.Zero || c3 == IntPtr.Zero || PdhCollectQueryData(query) != 0) return;
            PDH_FMT_COUNTERVALUE v;
            int type;
            DeepPct = PdhGetFormattedCounterValue(c3, PDH_FMT_DOUBLE, out type, out v) == 0 && v.CStatus <= 1 ? Math.Max(0, Math.Min(100, v.Value)) : double.NaN;
        }

        public void Dispose() { if (query != IntPtr.Zero) PdhCloseQuery(query); query = IntPtr.Zero; }

        const uint PDH_FMT_DOUBLE = 0x200;

        [StructLayout(LayoutKind.Sequential)]
        struct PDH_FMT_COUNTERVALUE { public uint CStatus; public double Value; }

        [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern int PdhOpenQuery(string src, IntPtr user, out IntPtr q);
        [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern int PdhAddEnglishCounter(IntPtr q, string path, IntPtr user, out IntPtr c);
        [DllImport("pdh.dll")] static extern int PdhCollectQueryData(IntPtr q);
        [DllImport("pdh.dll")] static extern int PdhGetFormattedCounterValue(IntPtr c, uint fmt, out int type, out PDH_FMT_COUNTERVALUE v);
        [DllImport("pdh.dll")] static extern int PdhCloseQuery(IntPtr q);
    }

    /// <summary>
    /// Разрешение системного таймера, мс: 15,6 — обычное; 1 и меньше — какая-то программа попросила Windows будить систему
    /// тысячу раз в секунду, и чип не может надолго уходить в глубокий сон. Без прав администратора.
    /// </summary>
    static class SystemTimer
    {
        public static double CurrentMs()
        {
            uint max, min, cur;
            try { return NtQueryTimerResolution(out max, out min, out cur) == 0 ? cur / 10000.0 : double.NaN; }
            catch (EntryPointNotFoundException) { return double.NaN; }
        }

        [DllImport("ntdll.dll")] static extern int NtQueryTimerResolution(out uint maximum, out uint minimum, out uint current);
    }

    /// <summary>
    /// Горят ли экраны на самом деле — по уведомлению Windows GUID_CONSOLE_DISPLAY_STATE (без окна и прав администратора).
    /// Тайм-аут экрана в настройках ничего не гарантирует: программа с запросом «экран нужен» (плеер, браузер с видео) его отменяет.
    /// </summary>
    static class DisplayState
    {
        public const int Off = 0, On = 1, Dimmed = 2;

        static readonly Guid ConsoleDisplayState = new Guid("6fe69556-704a-47a0-8f24-c28d936fda47");
        const int PBT_POWERSETTINGCHANGE = 0x8013, DEVICE_NOTIFY_CALLBACK = 2;

        static volatile int state = -1;
        static NotifyCallback callback;  // держим делегат, иначе сборщик мусора уберёт его из-под Windows
        static IntPtr handle, recipient;

        /// <summary>
        /// Modern Standby (S0 low power idle): выключение экрана — это вход в сон всей системы (и по тайм-ауту, и командой
        /// SC_MONITORPOWER — проверено 05.10: «entering Modern Standby, Reason: SC_MONITORPOWER»). Состояния «экран погас,
        /// система работает» здесь нет, поэтому и панель отдельно от остального не измерить.
        /// </summary>
        public static bool ModernStandby
        {
            get
            {
                if (modernStandby < 0)
                {
                    // SYSTEM_POWER_CAPABILITIES: 15 BOOLEAN (SystemS3 — смещение 5), 2 BYTE, FastSystemS4, Hiberboot,
                    // WakeAlarmPresent, AoAc (смещение 20; проверено на PHN16S-71). Без классического сна S3 — тоже не
                    // выключать экран: ошибка здесь усыпляет ноутбук посреди замера.
                    var buf = new byte[128];
                    try { modernStandby = !GetPwrCapabilities(buf) || buf[20] != 0 || buf[5] == 0 ? 1 : 0; }
                    catch (EntryPointNotFoundException) { modernStandby = 1; }
                }
                return modernStandby == 1;
            }
        }

        static int modernStandby = -1;

        [DllImport("powrprof.dll")] static extern bool GetPwrCapabilities(byte[] caps);

        /// <summary>0 — выключены, 1 — включены, 2 — затемнены; -1 — неизвестно (до первого уведомления или не поддерживается).</summary>
        public static int Current
        {
            get
            {
                if (handle == IntPtr.Zero) Start();
                return state;
            }
        }

        static void Start()
        {
            if (callback != null) return;
            callback = OnNotify;
            try
            {
                var p = new SubscribeParameters { Callback = Marshal.GetFunctionPointerForDelegate(callback), Context = IntPtr.Zero };
                recipient = Marshal.AllocHGlobal(Marshal.SizeOf(p));  // не освобождается: подписка живёт до конца процесса
                Marshal.StructureToPtr(p, recipient, false);
                var g = ConsoleDisplayState;
                IntPtr h;
                if (PowerSettingRegisterNotification(ref g, DEVICE_NOTIFY_CALLBACK, recipient, out h) == 0) handle = h;  // Windows сразу присылает текущее состояние
            }
            catch (EntryPointNotFoundException) { }
            catch (DllNotFoundException) { }
        }

        static uint OnNotify(IntPtr context, uint type, IntPtr setting)
        {
            // POWERBROADCAST_SETTING: GUID (16 байт), DataLength (4), Data
            if (type == PBT_POWERSETTINGCHANGE && setting != IntPtr.Zero && Marshal.ReadInt32(setting, 16) >= 4)
                state = Marshal.ReadInt32(setting, 20);
            return 0;
        }

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate uint NotifyCallback(IntPtr context, uint type, IntPtr setting);

        [StructLayout(LayoutKind.Sequential)]
        struct SubscribeParameters
        {
            public IntPtr Callback;
            public IntPtr Context;
        }

        [DllImport("powrprof.dll")]
        static extern uint PowerSettingRegisterNotification(ref Guid setting, uint flags, IntPtr recipient, out IntPtr handle);
    }

    /// <summary>Подключённые экраны: всего и сколько из них выводится через видеокарту NVIDIA.</summary>
    static class Displays
    {
        const int DISPLAY_DEVICE_ATTACHED_TO_DESKTOP = 0x1;

        public static void Count(out int total, out int onNvidia)
        {
            total = 0;
            onNvidia = 0;
            var dd = new DISPLAY_DEVICE();
            dd.cb = Marshal.SizeOf(dd);
            for (uint i = 0; EnumDisplayDevices(null, i, ref dd, 0); i++)
            {
                if ((dd.StateFlags & DISPLAY_DEVICE_ATTACHED_TO_DESKTOP) != 0)
                {
                    total++;
                    if (dd.DeviceString != null && dd.DeviceString.IndexOf("NVIDIA", StringComparison.OrdinalIgnoreCase) >= 0)
                        onNvidia++;
                }
                dd.cb = Marshal.SizeOf(dd);
            }
        }

        /// <summary>Имя («\\.\DISPLAY1») экрана, подключённого не к NVIDIA, — встроенного экрана ноутбука; null — не найден.</summary>
        public static string InternalDeviceName()
        {
            var dd = new DISPLAY_DEVICE();
            dd.cb = Marshal.SizeOf(dd);
            for (uint i = 0; EnumDisplayDevices(null, i, ref dd, 0); i++)
            {
                if ((dd.StateFlags & DISPLAY_DEVICE_ATTACHED_TO_DESKTOP) != 0 && dd.DeviceString != null
                    && dd.DeviceString.IndexOf("NVIDIA", StringComparison.OrdinalIgnoreCase) < 0)
                    return dd.DeviceName;
                dd.cb = Marshal.SizeOf(dd);
            }
            return null;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct DISPLAY_DEVICE
        {
            public int cb;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
            public int StateFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern bool EnumDisplayDevices(string device, uint index, ref DISPLAY_DEVICE dd, uint flags);
    }

    /// <summary>
    /// Дискретная видеокарта NVIDIA через NVML (nvml.dll ставится вместе с драйвером).
    /// Запрос будит спящую видеокарту, поэтому её опрашивают, только когда она уже в D0 (см. GpuPowerState).
    /// </summary>
    sealed class NvidiaGpu : IDisposable
    {
        IntPtr device;

        public string Name;
        public double PowerW = double.NaN;
        public int UtilPct = -1;
        public int PState = -1;

        NvidiaGpu() { }

        public static NvidiaGpu TryOpen()
        {
            try
            {
                if (nvmlInit_v2() != 0) return null;
                IntPtr d;
                if (nvmlDeviceGetHandleByIndex_v2(0, out d) != 0)
                {
                    nvmlShutdown();
                    return null;
                }
                var sb = new StringBuilder(96);
                nvmlDeviceGetName(d, sb, (uint)sb.Capacity);
                var gpu = new NvidiaGpu();
                gpu.device = d;
                gpu.Name = sb.ToString().Replace("NVIDIA GeForce ", "").Replace("NVIDIA ", "");
                return gpu;
            }
            catch (DllNotFoundException) { return null; }
            catch (EntryPointNotFoundException) { return null; }
            catch (BadImageFormatException) { return null; }
        }

        public void Sample()
        {
            uint mw;
            PowerW = nvmlDeviceGetPowerUsage(device, out mw) == 0 ? mw / 1000.0 : double.NaN;
            NvmlUtilization u;
            UtilPct = nvmlDeviceGetUtilizationRates(device, out u) == 0 ? (int)u.Gpu : -1;
            int p;
            PState = nvmlDeviceGetPerformanceState(device, out p) == 0 ? p : -1;
        }

        public void Dispose()
        {
            nvmlShutdown();
        }

        [StructLayout(LayoutKind.Sequential)]
        struct NvmlUtilization
        {
            public uint Gpu;
            public uint Memory;
        }

        const string Nvml = "nvml.dll";

        [DllImport(Nvml, CallingConvention = CallingConvention.Cdecl)]
        static extern int nvmlInit_v2();

        [DllImport(Nvml, CallingConvention = CallingConvention.Cdecl)]
        static extern int nvmlShutdown();

        [DllImport(Nvml, CallingConvention = CallingConvention.Cdecl)]
        static extern int nvmlDeviceGetHandleByIndex_v2(uint index, out IntPtr device);

        [DllImport(Nvml, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        static extern int nvmlDeviceGetName(IntPtr device, StringBuilder name, uint length);

        [DllImport(Nvml, CallingConvention = CallingConvention.Cdecl)]
        static extern int nvmlDeviceGetPowerUsage(IntPtr device, out uint milliwatts);

        [DllImport(Nvml, CallingConvention = CallingConvention.Cdecl)]
        static extern int nvmlDeviceGetUtilizationRates(IntPtr device, out NvmlUtilization utilization);

        [DllImport(Nvml, CallingConvention = CallingConvention.Cdecl)]
        static extern int nvmlDeviceGetPerformanceState(IntPtr device, out int pstate);
    }
}
