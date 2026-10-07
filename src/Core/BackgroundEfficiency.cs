using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace BatteryCheck
{
    sealed class ProcEntry
    {
        public int Pid, Parent;
        public string Name;  // без .exe
    }

    /// <summary>Процессы и режим эффективности; в тестах — подделка.</summary>
    interface IProcessSource
    {
        List<ProcEntry> Snapshot();          // процессы сеанса пользователя
        int ForegroundPid();
        bool? IsOn(int pid);
        bool Set(int pid, bool on);
    }

    /// <summary>
    /// Режим эффективности для всех фоновых программ (часть «Максимальной экономии», только от батареи).
    /// Раз в SweepSeconds — всем процессам сеанса, кроме активного окна (все процессы той же программы и их потомки),
    /// своего процесса и исключений (оболочка Windows, плееры, звонки — список в rules.json). Активное окно проверяется
    /// каждый Tick: переключились на программу — режим с неё снимается сразу. Процессы, которые уже были в режиме
    /// эффективности (Windows сама притормаживает, например, фоновые вкладки), не трогаются и потом не снимаются.
    /// </summary>
    sealed class BackgroundEfficiency
    {
        public const double SweepSeconds = 10;

        readonly IProcessSource source;
        readonly HashSet<string> exclude;
        readonly int self;
        readonly Dictionary<int, string> ours = new Dictionary<int, string>();  // pid → имя: кому режим включили мы
        DateTime lastSweep = DateTime.MinValue;
        int lastForeground = -1;

        public BackgroundEfficiency(IProcessSource source, IEnumerable<string> exclude, int selfPid)
        {
            this.source = source;
            this.exclude = new HashSet<string>(exclude, StringComparer.OrdinalIgnoreCase);
            self = selfPid;
        }

        public int Count { get { return ours.Count; } }
        public bool Active { get; private set; }

        /// <summary>Каждую секунду. active — экономия включена и ноутбук от батареи. Возвращает true, если состояние сменилось (для журнала).</summary>
        public bool Tick(bool active, DateTime now)
        {
            if (!active)
            {
                if (!Active) return false;
                RestoreAll();
                Active = false;
                return true;
            }
            bool started = !Active;
            Active = true;
            int fg = source.ForegroundPid();
            if (started || fg != lastForeground || (now - lastSweep).TotalSeconds >= SweepSeconds)
            {
                lastForeground = fg;
                lastSweep = now;
                Sweep(fg);
            }
            return started;
        }

        void Sweep(int fg)
        {
            var list = source.Snapshot();
            var byPid = new Dictionary<int, ProcEntry>();
            foreach (var p in list) byPid[p.Pid] = p;

            // Освободить: активное окно — вся программа (все процессы с тем же именем) и их потомки.
            var exempt = new HashSet<int> { self };
            ProcEntry fgp;
            if (byPid.TryGetValue(fg, out fgp))
            {
                foreach (var p in list) if (string.Equals(p.Name, fgp.Name, StringComparison.OrdinalIgnoreCase)) exempt.Add(p.Pid);
                bool grew = true;
                while (grew)  // потомки — до неподвижной точки (деревья неглубокие)
                {
                    grew = false;
                    foreach (var p in list) if (!exempt.Contains(p.Pid) && exempt.Contains(p.Parent) && p.Parent != 0) { exempt.Add(p.Pid); grew = true; }
                }
            }

            // Свои, которых больше нет (завершились) или pid занят другим процессом, — забыть.
            foreach (var pid in new List<int>(ours.Keys))
            {
                ProcEntry p;
                if (!byPid.TryGetValue(pid, out p) || !string.Equals(p.Name, ours[pid], StringComparison.OrdinalIgnoreCase)) ours.Remove(pid);
            }

            foreach (var p in list)
            {
                bool free = exempt.Contains(p.Pid) || exclude.Contains(p.Name);
                if (free)
                {
                    if (ours.ContainsKey(p.Pid) && source.Set(p.Pid, false)) ours.Remove(p.Pid);
                    continue;
                }
                if (ours.ContainsKey(p.Pid)) continue;
                if (source.IsOn(p.Pid) != false) continue;  // уже в режиме (сам или Windows) или нет доступа
                if (source.Set(p.Pid, true)) ours[p.Pid] = p.Name;
            }
        }

        public void RestoreAll()
        {
            if (ours.Count == 0) return;
            var byPid = new Dictionary<int, ProcEntry>();
            foreach (var p in source.Snapshot()) byPid[p.Pid] = p;
            foreach (var kv in ours)
            {
                ProcEntry p;
                if (byPid.TryGetValue(kv.Key, out p) && string.Equals(p.Name, kv.Value, StringComparison.OrdinalIgnoreCase)) source.Set(kv.Key, false);
            }
            ours.Clear();
        }
    }

    /// <summary>Настоящие процессы: снимок Toolhelp (имя и родитель, ~1–2 мс), только сеанс пользователя.</summary>
    sealed class SystemProcessSource : IProcessSource
    {
        readonly uint session;

        public SystemProcessSource()
        {
            ProcessIdToSessionId((uint)Process.GetCurrentProcess().Id, out session);
        }

        public List<ProcEntry> Snapshot()
        {
            var r = new List<ProcEntry>();
            IntPtr snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
            if (snap == new IntPtr(-1)) return r;
            try
            {
                var e = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf(typeof(PROCESSENTRY32W)) };
                for (bool ok = Process32FirstW(snap, ref e); ok; ok = Process32NextW(snap, ref e))
                {
                    uint s;
                    if (e.th32ProcessID == 0 || !ProcessIdToSessionId(e.th32ProcessID, out s) || s != session) continue;
                    string name = e.szExeFile ?? "";
                    if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name.Substring(0, name.Length - 4);
                    r.Add(new ProcEntry { Pid = (int)e.th32ProcessID, Parent = (int)e.th32ParentProcessID, Name = name });
                }
            }
            finally { CloseHandle(snap); }
            return r;
        }

        public int ForegroundPid() { return (int)ForegroundApp.Pid(); }
        public bool? IsOn(int pid) { return Efficiency.IsOn(pid); }
        public bool Set(int pid, bool on) { return Efficiency.Set(pid, on); }

        const uint TH32CS_SNAPPROCESS = 0x2;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct PROCESSENTRY32W
        {
            public uint dwSize, cntUsage, th32ProcessID;
            public IntPtr th32DefaultHeapID;
            public uint th32ModuleID, cntThreads, th32ParentProcessID;
            public int pcPriClassBase;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
        }

        [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool Process32FirstW(IntPtr snap, ref PROCESSENTRY32W e);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool Process32NextW(IntPtr snap, ref PROCESSENTRY32W e);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll")] static extern bool ProcessIdToSessionId(uint pid, out uint session);
    }
}
