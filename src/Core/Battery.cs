using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace BatteryCheck
{
    /// <summary>Статические данные батареи (IOCTL_BATTERY_QUERY_INFORMATION).</summary>
    sealed class BatteryInfo
    {
        public string DeviceName;
        public string Manufacturer;
        public string SerialNumber;
        public string Chemistry;
        public uint Capabilities;
        public uint DesignedCapacity;     // мВт·ч (или относительные единицы, см. IsRelative)
        public uint FullChargedCapacity;  // мВт·ч
        public uint CycleCount;           // 0 — не поддерживается
        public double TemperatureC = double.NaN;

        public bool IsRelative { get { return (Capabilities & 0x40000000) != 0; } }

        public double WearPercent
        {
            get
            {
                if (DesignedCapacity == 0) return double.NaN;
                return (1.0 - (double)FullChargedCapacity / DesignedCapacity) * 100.0;
            }
        }
    }

    /// <summary>Текущее состояние батареи (BATTERY_STATUS из IOCTL_BATTERY_QUERY_STATUS).</summary>
    [StructLayout(LayoutKind.Sequential)]
    struct BatteryStatus
    {
        public uint PowerState;
        public uint Capacity;   // мВт·ч
        public uint Voltage;    // мВ
        public int Rate;        // мВт: < 0 — разряд, > 0 — заряд

        public bool OnLine      { get { return (PowerState & 0x1) != 0; } }
        public bool Discharging { get { return (PowerState & 0x2) != 0; } }
        public bool Charging    { get { return (PowerState & 0x4) != 0; } }
        public bool Critical    { get { return (PowerState & 0x8) != 0; } }

        public bool HasCapacity { get { return Capacity != 0xFFFFFFFF; } }
        public bool HasVoltage  { get { return Voltage != 0xFFFFFFFF && Voltage != 0; } }
        public bool HasRate     { get { return Rate != int.MinValue; } }  // BATTERY_UNKNOWN_RATE = 0x80000000

        /// <summary>Мощность батареи в ваттах со знаком, NaN если неизвестна.</summary>
        public double RateW { get { return HasRate ? Rate / 1000.0 : double.NaN; } }

        /// <summary>Ток в амперах (по модулю), NaN если неизвестен.</summary>
        public double CurrentA
        {
            get { return HasRate && HasVoltage ? Math.Abs((double)Rate) / Voltage : double.NaN; }
        }
    }

    /// <summary>Доступ к батарее через драйвер (SetupDi + DeviceIoControl). Права администратора не нужны.</summary>
    sealed class Battery : IDisposable
    {
        const uint IOCTL_BATTERY_QUERY_TAG         = 0x294040;
        const uint IOCTL_BATTERY_QUERY_INFORMATION = 0x294044;
        const uint IOCTL_BATTERY_QUERY_STATUS      = 0x29404C;

        const int BatteryInformation     = 0;
        const int BatteryTemperature     = 2;
        const int BatteryDeviceName      = 4;
        const int BatteryManufactureName = 6;
        const int BatterySerialNumber    = 8;

        static readonly Guid GUID_DEVCLASS_BATTERY = new Guid("72631e54-78a4-11d0-bcf7-00aa00b7b32a");

        readonly SafeFileHandle handle;
        uint tag;  // прошивка может выдать новый тег (05.10: при подключении зарядки) — тогда запросы со старым отвергаются

        Battery(SafeFileHandle handle, uint tag)
        {
            this.handle = handle;
            this.tag = tag;
        }

        /// <summary>Открывает батарею с указанным индексом или возвращает null, если её нет.</summary>
        public static Battery Open(int index)
        {
            Guid g = GUID_DEVCLASS_BATTERY;
            IntPtr set = SetupDiGetClassDevs(ref g, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
            if (set == new IntPtr(-1)) throw new Win32Exception();
            try
            {
                var did = new SP_DEVICE_INTERFACE_DATA();
                did.cbSize = Marshal.SizeOf(did);
                if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref g, index, ref did))
                    return null;

                int size;
                SetupDiGetDeviceInterfaceDetail(set, ref did, IntPtr.Zero, 0, out size, IntPtr.Zero);
                IntPtr buf = Marshal.AllocHGlobal(size);
                string path;
                try
                {
                    // cbSize структуры SP_DEVICE_INTERFACE_DETAIL_DATA_W: 8 на x64, 6 на x86
                    Marshal.WriteInt32(buf, IntPtr.Size == 8 ? 8 : 6);
                    if (!SetupDiGetDeviceInterfaceDetail(set, ref did, buf, size, out size, IntPtr.Zero))
                        throw new Win32Exception();
                    path = Marshal.PtrToStringUni(buf + 4);
                }
                finally
                {
                    Marshal.FreeHGlobal(buf);
                }

                SafeFileHandle h = CreateFile(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE,
                                              IntPtr.Zero, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, IntPtr.Zero);
                if (h.IsInvalid) throw new Win32Exception();

                uint wait = 0, t;
                int ret;
                if (!DeviceIoControl(h, IOCTL_BATTERY_QUERY_TAG, ref wait, 4, out t, 4, out ret, IntPtr.Zero) || t == 0)
                {
                    h.Dispose();
                    return null;
                }
                return new Battery(h, t);
            }
            finally
            {
                SetupDiDestroyDeviceInfoList(set);
            }
        }

        /// <summary>Запросить текущий тег батареи заново; false — батареи нет.</summary>
        bool RefreshTag()
        {
            uint wait = 0, t;
            int ret;
            if (!DeviceIoControl(handle, IOCTL_BATTERY_QUERY_TAG, ref wait, 4, out t, 4, out ret, IntPtr.Zero) || t == 0) return false;
            tag = t;
            return true;
        }

        public BatteryInfo QueryInfo()
        {
            BATTERY_INFORMATION bi;
            int ret;
            var q = new BATTERY_QUERY_INFORMATION { BatteryTag = tag, InformationLevel = BatteryInformation };
            if (!DeviceIoControl(handle, IOCTL_BATTERY_QUERY_INFORMATION, ref q, Marshal.SizeOf(q),
                                 out bi, Marshal.SizeOf(typeof(BATTERY_INFORMATION)), out ret, IntPtr.Zero))
            {
                int err = Marshal.GetLastWin32Error();
                if (!RefreshTag()) throw new Win32Exception(err);  // тег устарел — запросить новый и повторить
                q.BatteryTag = tag;
                if (!DeviceIoControl(handle, IOCTL_BATTERY_QUERY_INFORMATION, ref q, Marshal.SizeOf(q),
                                     out bi, Marshal.SizeOf(typeof(BATTERY_INFORMATION)), out ret, IntPtr.Zero))
                    throw new Win32Exception();
            }

            var info = new BatteryInfo();
            info.Capabilities = bi.Capabilities;
            info.DesignedCapacity = bi.DesignedCapacity;
            info.FullChargedCapacity = bi.FullChargedCapacity;
            info.CycleCount = bi.CycleCount;
            info.Chemistry = Encoding.ASCII.GetString(BitConverter.GetBytes(bi.Chemistry)).TrimEnd('\0', ' ');
            info.DeviceName = QueryString(BatteryDeviceName);
            info.Manufacturer = QueryString(BatteryManufactureName);
            info.SerialNumber = QueryString(BatterySerialNumber);
            info.TemperatureC = QueryTemperature();
            return info;
        }

        public BatteryStatus QueryStatus()
        {
            var w = new BATTERY_WAIT_STATUS { BatteryTag = tag };
            BatteryStatus s;
            int ret;
            if (DeviceIoControl(handle, IOCTL_BATTERY_QUERY_STATUS, ref w, Marshal.SizeOf(w),
                                out s, Marshal.SizeOf(typeof(BatteryStatus)), out ret, IntPtr.Zero))
                return s;
            int err = Marshal.GetLastWin32Error();
            if (!RefreshTag()) throw new Win32Exception(err);  // тег устарел (смена источника питания) — новый и повтор
            w.BatteryTag = tag;
            if (!DeviceIoControl(handle, IOCTL_BATTERY_QUERY_STATUS, ref w, Marshal.SizeOf(w),
                                 out s, Marshal.SizeOf(typeof(BatteryStatus)), out ret, IntPtr.Zero))
                throw new Win32Exception();
            return s;
        }

        string QueryString(int level)
        {
            var q = new BATTERY_QUERY_INFORMATION { BatteryTag = tag, InformationLevel = level };
            var buf = new byte[512];
            int ret;
            if (!DeviceIoControl(handle, IOCTL_BATTERY_QUERY_INFORMATION, ref q, Marshal.SizeOf(q), buf, buf.Length, out ret, IntPtr.Zero))
                return null;
            string s = Encoding.Unicode.GetString(buf, 0, ret);
            int z = s.IndexOf('\0');
            if (z >= 0) s = s.Substring(0, z);
            s = s.Trim();
            return s.Length == 0 ? null : s;
        }

        /// <summary>Температура в °C или NaN, если прошивка её не сообщает.</summary>
        double QueryTemperature()
        {
            var q = new BATTERY_QUERY_INFORMATION { BatteryTag = tag, InformationLevel = BatteryTemperature };
            var buf = new byte[4];
            int ret;
            if (!DeviceIoControl(handle, IOCTL_BATTERY_QUERY_INFORMATION, ref q, Marshal.SizeOf(q), buf, buf.Length, out ret, IntPtr.Zero))
                return double.NaN;
            uint tenthsKelvin = BitConverter.ToUInt32(buf, 0);
            return tenthsKelvin == 0 ? double.NaN : tenthsKelvin / 10.0 - 273.15;
        }

        public void Dispose()
        {
            handle.Dispose();
        }

        // ---- WinAPI ----

        const int DIGCF_PRESENT = 0x2, DIGCF_DEVICEINTERFACE = 0x10;
        const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000;
        const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, OPEN_EXISTING = 3, FILE_ATTRIBUTE_NORMAL = 0x80;

        [StructLayout(LayoutKind.Sequential)]
        struct SP_DEVICE_INTERFACE_DATA
        {
            public int cbSize;
            public Guid InterfaceClassGuid;
            public int Flags;
            public IntPtr Reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct BATTERY_QUERY_INFORMATION
        {
            public uint BatteryTag;
            public int InformationLevel;
            public int AtRate;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct BATTERY_INFORMATION
        {
            public uint Capabilities;
            public byte Technology;
            public byte Reserved1, Reserved2, Reserved3;
            public uint Chemistry;  // 4 ASCII-символа
            public uint DesignedCapacity;
            public uint FullChargedCapacity;
            public uint DefaultAlert1;
            public uint DefaultAlert2;
            public uint CriticalBias;
            public uint CycleCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct BATTERY_WAIT_STATUS
        {
            public uint BatteryTag;
            public uint Timeout;
            public uint PowerState;
            public uint LowCapacity;
            public uint HighCapacity;
        }

        [DllImport("setupapi.dll", SetLastError = true)]
        static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, IntPtr enumerator, IntPtr hwndParent, int flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr devInfo, ref Guid classGuid, int index, ref SP_DEVICE_INTERFACE_DATA data);

        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref SP_DEVICE_INTERFACE_DATA data, IntPtr detail, int detailSize, out int requiredSize, IntPtr devInfo);

        [DllImport("setupapi.dll", SetLastError = true)]
        static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool DeviceIoControl(SafeFileHandle h, uint code, ref uint inBuf, int inSize, out uint outBuf, int outSize, out int returned, IntPtr overlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool DeviceIoControl(SafeFileHandle h, uint code, ref BATTERY_QUERY_INFORMATION inBuf, int inSize, out BATTERY_INFORMATION outBuf, int outSize, out int returned, IntPtr overlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool DeviceIoControl(SafeFileHandle h, uint code, ref BATTERY_QUERY_INFORMATION inBuf, int inSize, [Out] byte[] outBuf, int outSize, out int returned, IntPtr overlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool DeviceIoControl(SafeFileHandle h, uint code, ref BATTERY_WAIT_STATUS inBuf, int inSize, out BatteryStatus outBuf, int outSize, out int returned, IntPtr overlapped);
    }
}
