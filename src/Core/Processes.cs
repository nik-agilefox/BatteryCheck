using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace BatteryCheck
{
    /// <summary>Использование ресурсов процессом (все процессы с одним именем суммируются) за интервал между замерами.</summary>
    sealed class ProcessUsage
    {
        public string Name;
        public double CycleShare;           // доля тактов процессора, израсходованных всеми процессами
        public double CpuPct;               // загрузка в % от всех логических процессоров, как в диспетчере задач
        public double IgpuShare, DgpuShare; // доля работы на встроенной и на дискретной видеокарте
        public double DgpuMemMB;            // видеопамять на дискретной видеокарте: держит её включённой, даже без нагрузки
        public double GpuPct;               // наибольшая загрузка одного движка видеокарты, как в диспетчере задач
    }

    /// <summary>
    /// Такты и время процессора по процессам и загрузка движков видеокарт по процессам (счётчики Windows «GPU Engine»).
    /// Права администратора не нужны, спящую видеокарту не будит.
    ///
    /// Замер дорогой — ~20 мс процессора: NtQuerySystemInformation собирает сведения о каждом потоке системы.
    /// Дешёвых способов, которые видят и системные процессы (dwm, csrss, службы), без прав администратора нет:
    /// OpenProcess к ним не пускает, а QueryProcessCycleTime по всем процессам ещё дороже. Поэтому опрашивать редко.
    /// </summary>
    sealed class ProcessMonitor : IDisposable
    {
        const int SystemProcessInformation = 5;
        const uint STATUS_INFO_LENGTH_MISMATCH = 0xC0000004;
        const uint PDH_FMT_DOUBLE = 0x200;
        const int PDH_MORE_DATA = unchecked((int)0x800007D2);

        // Смещения полей SYSTEM_PROCESS_INFORMATION (x64)
        const int OffNext = 0, OffCycles = 24, OffCreate = 32, OffUser = 40, OffKernel = 48, OffNameLen = 56, OffNamePtr = 64, OffPid = 80;

        struct Times
        {
            public long Create, Cycles, Cpu;  // Cpu — user + kernel, в 100 нс
        }

        Dictionary<long, Times> prev = new Dictionary<long, Times>();
        DateTime prevTime = DateTime.MinValue;
        IntPtr buffer;
        int bufferSize = 2 << 20;
        IntPtr gpuItems;
        int gpuItemsSize;
        readonly IntPtr query, gpuCounter, memCounter;

        /// <summary>Служебные процессы с видеопамятью на любой видеокарте — не «держатели» в смысле совета.</summary>
        static readonly HashSet<string> NotHolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "System", "csrss", "BatteryCheckGui", "BatteryCheck" };
        const double HolderMinMB = 16;
        readonly int cpuCount = Environment.ProcessorCount;

        /// <summary>LUID адаптеров в формате счётчиков: «0x00000000_0x00013d43». null — не найден.</summary>
        public readonly string IgpuLuid, DgpuLuid;

        /// <summary>Номер процесса → имя по последнему замеру (например, чтобы узнать процесс активного окна).</summary>
        public Dictionary<long, string> LastNames = new Dictionary<long, string>();

        public ProcessMonitor()
        {
            buffer = Marshal.AllocHGlobal(bufferSize);
            Dxgi.FindAdapters(out IgpuLuid, out DgpuLuid);
            if (PdhOpenQuery(null, IntPtr.Zero, out query) == 0)
            {
                if (PdhAddEnglishCounter(query, @"\GPU Engine(*)\Utilization Percentage", IntPtr.Zero, out gpuCounter) != 0)
                    gpuCounter = IntPtr.Zero;
                if (PdhAddEnglishCounter(query, @"\GPU Process Memory(*)\Dedicated Usage", IntPtr.Zero, out memCounter) != 0)
                    memCounter = IntPtr.Zero;
                PdhCollectQueryData(query);
            }
        }

        /// <summary>Забыть базу: следующий Sample() только запомнит значения (после паузы в опросе).</summary>
        public void Reset()
        {
            prev.Clear();
            prevTime = DateTime.MinValue;
            if (query != IntPtr.Zero) PdhCollectQueryData(query);
        }

        /// <summary>Использование с прошлого вызова; null при первом вызове (нет базы) или ошибке.</summary>
        public List<ProcessUsage> Sample()
        {
            DateTime now = DateTime.UtcNow;
            bool baseline = prevTime != DateTime.MinValue;
            double wall = baseline ? (now - prevTime).TotalSeconds : 0;
            long prevFileTime = baseline ? prevTime.ToFileTimeUtc() : 0;
            var cur = new Dictionary<long, Times>();
            var nameByPid = new Dictionary<long, string>();
            var byName = new Dictionary<string, ProcessUsage>(StringComparer.OrdinalIgnoreCase);
            var cyclesByName = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            long totalCycles = 0;

            if (!QueryProcesses()) return null;
            int offset = 0;
            while (true)
            {
                IntPtr e = buffer + offset;
                int next = Marshal.ReadInt32(e, OffNext);
                long pid = Marshal.ReadIntPtr(e, OffPid).ToInt64();
                if (pid != 0)  // 0 — «Бездействие системы»
                {
                    var t = new Times
                    {
                        Cycles = Marshal.ReadInt64(e, OffCycles),
                        Create = Marshal.ReadInt64(e, OffCreate),
                        Cpu = Marshal.ReadInt64(e, OffUser) + Marshal.ReadInt64(e, OffKernel),
                    };
                    int len = (ushort)Marshal.ReadInt16(e, OffNameLen);
                    IntPtr namePtr = Marshal.ReadIntPtr(e, OffNamePtr);
                    string name = namePtr == IntPtr.Zero ? "System" : Marshal.PtrToStringUni(namePtr, len / 2);
                    if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name.Substring(0, name.Length - 4);
                    nameByPid[pid] = name;
                    cur[pid] = t;

                    long dCycles = 0, dCpu = 0;
                    Times old;
                    if (prev.TryGetValue(pid, out old) && old.Create == t.Create)
                    {
                        dCycles = t.Cycles - old.Cycles;
                        dCpu = t.Cpu - old.Cpu;
                    }
                    else if (baseline && t.Create > prevFileTime)
                    {
                        dCycles = t.Cycles;  // процесс запущен после прошлого замера — всё его время в этом интервале
                        dCpu = t.Cpu;
                    }

                    if (dCycles > 0 || dCpu > 0)
                    {
                        var u = Get(byName, name);
                        u.CpuPct += Math.Max(0, dCpu) / 1e7 / Math.Max(0.001, wall) / cpuCount * 100;
                        long c;
                        cyclesByName.TryGetValue(name, out c);
                        cyclesByName[name] = c + Math.Max(0, dCycles);
                        totalCycles += Math.Max(0, dCycles);
                    }
                }
                if (next == 0) break;
                offset += next;
            }
            prev = cur;
            prevTime = now;
            LastNames = nameByPid;
            if (!baseline)
            {
                if (query != IntPtr.Zero) PdhCollectQueryData(query);  // база и для счётчиков видеокарт
                return null;
            }

            foreach (var kv in cyclesByName)
                byName[kv.Key].CycleShare = totalCycles > 0 ? (double)kv.Value / totalCycles : 0;

            SampleGpu(nameByPid, byName);
            return new List<ProcessUsage>(byName.Values);
        }

        void SampleGpu(Dictionary<long, string> nameByPid, Dictionary<string, ProcessUsage> byName)
        {
            if (gpuCounter == IntPtr.Zero || (IgpuLuid == null && DgpuLuid == null)) return;
            if (PdhCollectQueryData(query) != 0) return;

            int size = gpuItemsSize, count;
            int st = PdhGetFormattedCounterArray(gpuCounter, PDH_FMT_DOUBLE, ref size, out count, gpuItems);
            if (st == PDH_MORE_DATA)
            {
                if (gpuItems != IntPtr.Zero) Marshal.FreeHGlobal(gpuItems);
                gpuItemsSize = size + 16384;
                gpuItems = Marshal.AllocHGlobal(gpuItemsSize);
                size = gpuItemsSize;
                st = PdhGetFormattedCounterArray(gpuCounter, PDH_FMT_DOUBLE, ref size, out count, gpuItems);
            }
            if (st != 0) return;

            var igpu = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            var dgpu = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            double igpuTotal = 0, dgpuTotal = 0;
            int itemSize = IntPtr.Size + 16;  // PDH_FMT_COUNTERVALUE_ITEM_W: указатель на имя + значение
            for (int i = 0; i < count; i++)
            {
                IntPtr item = gpuItems + i * itemSize;
                double v = BitConverter.Int64BitsToDouble(Marshal.ReadInt64(item, IntPtr.Size + 8));
                if (Marshal.ReadInt32(item, IntPtr.Size) > 1 || v <= 0) continue;  // CStatus: 0/1 — данные верны

                // «pid_11436_luid_0x00000000_0x00013d43_phys_0_eng_0_engtype_3D»
                string[] tok = Marshal.PtrToStringUni(Marshal.ReadIntPtr(item, 0)).Split('_');
                long pid;
                if (tok.Length < 5 || tok[0] != "pid" || !long.TryParse(tok[1], out pid)) continue;
                string name;
                if (!nameByPid.TryGetValue(pid, out name)) continue;
                string luid = tok[3] + "_" + tok[4];

                Dictionary<string, double> target;
                if (string.Equals(luid, DgpuLuid, StringComparison.OrdinalIgnoreCase)) { target = dgpu; dgpuTotal += v; }
                else if (string.Equals(luid, IgpuLuid, StringComparison.OrdinalIgnoreCase)) { target = igpu; igpuTotal += v; }
                else continue;
                double sum;
                target.TryGetValue(name, out sum);
                target[name] = sum + v;
                var u = Get(byName, name);
                if (v > u.GpuPct) u.GpuPct = Math.Min(100, v);
            }
            foreach (var kv in igpu) Get(byName, kv.Key).IgpuShare = kv.Value / igpuTotal;
            foreach (var kv in dgpu) Get(byName, kv.Key).DgpuShare = kv.Value / dgpuTotal;

            // Видеопамять на дискретной карте. Карта не спит, а нагрузки нет — её держат те, у кого там память:
            // делим её мощность между ними по объёму памяти (иначе она уходила бы в «не распределено»).
            if (memCounter == IntPtr.Zero || DgpuLuid == null) return;
            var mem = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            ReadArray(memCounter, (inst, v) =>
            {
                string[] tok = inst.Split('_');
                long pid;
                string name;
                if (tok.Length < 5 || tok[0] != "pid" || !long.TryParse(tok[1], out pid) || !nameByPid.TryGetValue(pid, out name)) return;
                if (!string.Equals(tok[3] + "_" + tok[4], DgpuLuid, StringComparison.OrdinalIgnoreCase)) return;
                double s;
                mem.TryGetValue(name, out s);
                mem[name] = s + v / (1024 * 1024);
            });
            double holdersMB = 0;
            foreach (var kv in mem)
            {
                Get(byName, kv.Key).DgpuMemMB = kv.Value;
                if (kv.Value >= HolderMinMB && !NotHolders.Contains(kv.Key)) holdersMB += kv.Value;
            }
            if (dgpuTotal <= 0 && holdersMB > 0)
                foreach (var kv in mem)
                    if (kv.Value >= HolderMinMB && !NotHolders.Contains(kv.Key)) Get(byName, kv.Key).DgpuShare = kv.Value / holdersMB;
        }

        /// <summary>Значения счётчика с подстановочным знаком: имя экземпляра → значение.</summary>
        void ReadArray(IntPtr counter, Action<string, double> each)
        {
            int size = gpuItemsSize, count;
            int st = PdhGetFormattedCounterArray(counter, PDH_FMT_DOUBLE, ref size, out count, gpuItems);
            if (st == PDH_MORE_DATA)
            {
                if (gpuItems != IntPtr.Zero) Marshal.FreeHGlobal(gpuItems);
                gpuItemsSize = size + 16384;
                gpuItems = Marshal.AllocHGlobal(gpuItemsSize);
                size = gpuItemsSize;
                st = PdhGetFormattedCounterArray(counter, PDH_FMT_DOUBLE, ref size, out count, gpuItems);
            }
            if (st != 0) return;
            int itemSize = IntPtr.Size + 16;
            for (int i = 0; i < count; i++)
            {
                IntPtr item = gpuItems + i * itemSize;
                if (Marshal.ReadInt32(item, IntPtr.Size) > 1) continue;
                double v = BitConverter.Int64BitsToDouble(Marshal.ReadInt64(item, IntPtr.Size + 8));
                if (v > 0) each(Marshal.PtrToStringUni(Marshal.ReadIntPtr(item, 0)), v);
            }
        }

        static ProcessUsage Get(Dictionary<string, ProcessUsage> map, string name)
        {
            ProcessUsage u;
            if (!map.TryGetValue(name, out u))
            {
                u = new ProcessUsage { Name = name };
                map[name] = u;
            }
            return u;
        }

        bool QueryProcesses()
        {
            for (int attempt = 0; attempt < 4; attempt++)
            {
                int needed;
                uint st = NtQuerySystemInformation(SystemProcessInformation, buffer, bufferSize, out needed);
                if (st == 0) return true;
                if (st != STATUS_INFO_LENGTH_MISMATCH) return false;
                Marshal.FreeHGlobal(buffer);
                bufferSize = Math.Max(needed, bufferSize) + (256 << 10);
                buffer = Marshal.AllocHGlobal(bufferSize);
            }
            return false;
        }

        public void Dispose()
        {
            if (query != IntPtr.Zero) PdhCloseQuery(query);
            if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
            if (gpuItems != IntPtr.Zero) Marshal.FreeHGlobal(gpuItems);
            buffer = gpuItems = IntPtr.Zero;
        }

        [DllImport("ntdll.dll")]
        static extern uint NtQuerySystemInformation(int infoClass, IntPtr info, int length, out int returnLength);

        [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
        static extern int PdhOpenQuery(string dataSource, IntPtr userData, out IntPtr query);

        [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
        static extern int PdhAddEnglishCounter(IntPtr query, string path, IntPtr userData, out IntPtr counter);

        [DllImport("pdh.dll")]
        static extern int PdhCollectQueryData(IntPtr query);

        [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
        static extern int PdhGetFormattedCounterArray(IntPtr counter, uint format, ref int bufferSize, out int itemCount, IntPtr items);

        [DllImport("pdh.dll")]
        static extern int PdhCloseQuery(IntPtr query);
    }

    /// <summary>Поиск видеоадаптеров через DXGI: какой LUID у встроенной графики и какой у NVIDIA. Видеокарту не будит.</summary>
    static class Dxgi
    {
        public static void FindAdapters(out string igpu, out string dgpu)
        {
            igpu = dgpu = null;
            try
            {
                Guid iid = typeof(IDXGIFactory1).GUID;
                IDXGIFactory1 factory;
                if (CreateDXGIFactory1(ref iid, out factory) != 0) return;
                try
                {
                    for (uint i = 0; ; i++)
                    {
                        IDXGIAdapter1 adapter;
                        if (factory.EnumAdapters1(i, out adapter) != 0) break;
                        try
                        {
                            DXGI_ADAPTER_DESC1 d;
                            if (adapter.GetDesc1(out d) != 0 || (d.Flags & 2) != 0) continue;  // 2 — программный адаптер
                            string luid = string.Format("0x{0:x8}_0x{1:x8}", d.LuidHigh, d.LuidLow);
                            if (d.VendorId == 0x10DE) { if (dgpu == null) dgpu = luid; }                               // NVIDIA — мощность из NVML
                            else if (d.VendorId == 0x8086 || d.VendorId == 0x1002) { if (igpu == null) igpu = luid; }  // Intel/AMD — RAPL PP1
                        }
                        finally
                        {
                            Marshal.ReleaseComObject(adapter);
                        }
                    }
                }
                finally
                {
                    Marshal.ReleaseComObject(factory);
                }
            }
            catch (Exception)
            {
                // DXGI недоступен — без распределения мощности видеокарт.
            }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct DXGI_ADAPTER_DESC1
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
            public uint VendorId, DeviceId, SubSysId, Revision;
            public UIntPtr DedicatedVideoMemory, DedicatedSystemMemory, SharedSystemMemory;
            public uint LuidLow;
            public int LuidHigh;
            public uint Flags;
        }

        // Методы-заглушки держат места в таблице виртуальных методов COM; вызываются только EnumAdapters1 и GetDesc1.
        [ComImport, Guid("770aae78-f26f-4dba-a829-253c83d1b387"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IDXGIFactory1
        {
            void SetPrivateData(); void SetPrivateDataInterface(); void GetPrivateData(); void GetParent();
            void EnumAdapters(); void MakeWindowAssociation(); void GetWindowAssociation(); void CreateSwapChain(); void CreateSoftwareAdapter();
            [PreserveSig] int EnumAdapters1(uint index, out IDXGIAdapter1 adapter);
        }

        [ComImport, Guid("29038f61-3839-4626-91fd-086879011a05"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IDXGIAdapter1
        {
            void SetPrivateData(); void SetPrivateDataInterface(); void GetPrivateData(); void GetParent();
            void EnumOutputs(); void GetDesc(); void CheckInterfaceSupport();
            [PreserveSig] int GetDesc1(out DXGI_ADAPTER_DESC1 desc);
        }

        [DllImport("dxgi.dll")]
        static extern int CreateDXGIFactory1(ref Guid riid, out IDXGIFactory1 factory);
    }
}
