using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;

namespace BatteryCheck
{
    /// <summary>Параметр схемы питания для работы от батареи и его значение в режиме «Максимальная экономия».</summary>
    sealed class PowerSetting
    {
        public string Id, NameEn, NameUa;
        public Guid Sub, Setting;
        public uint Economy;
        public string Name { get { return L.T(NameEn, NameUa); } }
    }

    /// <summary>Доступ к активной схеме питания; в тестах — подделка.</summary>
    interface IPowerScheme
    {
        bool ReadDc(Guid sub, Guid setting, out uint value);
        bool WriteDc(Guid sub, Guid setting, uint value);
        void Activate();  // применить изменения сразу
    }

    /// <summary>
    /// «Максимальная экономия»: значения активной схемы питания для батареи — ускорение процессора выключено
    /// (главное против нагрева: нет всплесков частоты), EPP = 100 (Windows держит ядра на низкой частоте, пока можно),
    /// экономия энергии Windows включена при любом заряде. От сети ничего не меняется, поэтому при подключении зарядки
    /// откатывать нечего. Выключение возвращает прежние значения — только те, что пользователь не менял сам.
    /// Без прав администратора, если схема питания пользовательская (у Predator PHN16S-71 — да).
    /// </summary>
    static class MaxEconomy
    {
        static readonly Guid Processor = new Guid("54533251-82be-4824-96c1-47b60b740d00");
        static readonly Guid EnergySaver = new Guid("de830923-a562-41af-a086-e3a2c6bad2da");

        public static readonly PowerSetting[] Settings =
        {
            new PowerSetting { Id = "boost", NameEn = "CPU boost", NameUa = "прискорення процесора", Sub = Processor, Setting = new Guid("be337238-0d82-4146-a960-4f3749d470c7"), Economy = 0 },
            new PowerSetting { Id = "epp", NameEn = "energy preference (P-cores)", NameUa = "пріоритет енергії (P-ядра)", Sub = Processor, Setting = new Guid("36687f9e-e3a5-4dbf-b1dc-15eb381c6863"), Economy = 100 },
            new PowerSetting { Id = "epp1", NameEn = "energy preference (E-cores)", NameUa = "пріоритет енергії (E-ядра)", Sub = Processor, Setting = new Guid("36687f9e-e3a5-4dbf-b1dc-15eb381c6864"), Economy = 100 },
            new PowerSetting { Id = "saver", NameEn = "Energy saver from", NameUa = "Економія енергії від", Sub = EnergySaver, Setting = new Guid("e69653ca-cf7f-4f05-aa73-cb833fa90ad4"), Economy = 100 },
        };

        /// <summary>
        /// Subzero — поверх Eco: потолок частоты процессора от батареи (максимальное состояние 60 % для P- и E-ядер).
        /// Жёсткое ограничение мощности процессора и в работе, и во всплесках; без прав администратора.
        /// </summary>
        public static readonly PowerSetting[] SubzeroSettings =
        {
            new PowerSetting { Id = "maxstate", NameEn = "max CPU state (P-cores), %", NameUa = "макс. стан процесора (P-ядра), %", Sub = Processor, Setting = new Guid("bc5038f7-23e0-4960-96da-33abaf5935ec"), Economy = 60 },
            new PowerSetting { Id = "maxstate1", NameEn = "max CPU state (E-cores), %", NameUa = "макс. стан процесора (E-ядра), %", Sub = Processor, Setting = new Guid("bc5038f7-23e0-4960-96da-33abaf5935ed"), Economy = 60 },
        };

        /// <summary>Включить: вернуть прежние значения тех параметров, что изменены (для отката). log(действие, было, стало).</summary>
        public static Dictionary<string, uint> Apply(IPowerScheme scheme, Action<string, string, string> log)
        {
            return Apply(scheme, log, Settings);
        }

        public static Dictionary<string, uint> Apply(IPowerScheme scheme, Action<string, string, string> log, PowerSetting[] settings)
        {
            var saved = new Dictionary<string, uint>();
            foreach (var s in settings)
            {
                uint cur;
                if (!scheme.ReadDc(s.Sub, s.Setting, out cur) || cur == s.Economy) continue;
                if (!scheme.WriteDc(s.Sub, s.Setting, s.Economy)) continue;
                saved[s.Id] = cur;
                log(s.Id, cur.ToString(CultureInfo.InvariantCulture), s.Economy.ToString(CultureInfo.InvariantCulture));
            }
            if (saved.Count > 0) scheme.Activate();
            return saved;
        }

        /// <summary>Выключить: вернуть сохранённые значения, если параметр всё ещё такой, как поставила экономия.</summary>
        public static void Revert(IPowerScheme scheme, Dictionary<string, uint> saved, Action<string, string, string> log)
        {
            Revert(scheme, saved, log, Settings);
        }

        public static void Revert(IPowerScheme scheme, Dictionary<string, uint> saved, Action<string, string, string> log, PowerSetting[] settings)
        {
            bool changed = false;
            foreach (var s in settings)
            {
                uint prev, cur;
                if (!saved.TryGetValue(s.Id, out prev) || !scheme.ReadDc(s.Sub, s.Setting, out cur)) continue;
                if (cur != s.Economy) { log(s.Id, cur.ToString(CultureInfo.InvariantCulture), cur + " (" + L.T("changed by you — kept", "змінено вами — залишено") + ")"); continue; }
                if (!scheme.WriteDc(s.Sub, s.Setting, prev)) continue;
                changed = true;
                log(s.Id, cur.ToString(CultureInfo.InvariantCulture), prev.ToString(CultureInfo.InvariantCulture));
            }
            if (changed) scheme.Activate();
        }

        /// <summary>«boost=2;epp=33» — для хранения между запусками; пустая строка — режим выключен.</summary>
        public static string Serialize(Dictionary<string, uint> saved)
        {
            var parts = new List<string>();
            foreach (var kv in saved) parts.Add(kv.Key + "=" + kv.Value.ToString(CultureInfo.InvariantCulture));
            return string.Join(";", parts);
        }

        public static Dictionary<string, uint> Parse(string s)
        {
            var d = new Dictionary<string, uint>();
            foreach (var part in (s ?? "").Split(';'))
            {
                int i = part.IndexOf('=');
                uint v;
                if (i > 0 && uint.TryParse(part.Substring(i + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out v)) d[part.Substring(0, i)] = v;
            }
            return d;
        }

        public static string NameOf(string id)
        {
            if (id == "background") return L.T("background apps in efficiency mode", "фонові програми в режимі ефективності");
            foreach (var s in Settings) if (s.Id == id) return s.Name;
            foreach (var s in SubzeroSettings) if (s.Id == id) return s.Name;
            return id;
        }
    }

    /// <summary>
    /// Активная схема питания Windows (powrprof). Значения «от батареи» (DC) — у ноутбука; у ПК без аккумулятора
    /// Windows всегда «от сети», поэтому там те же настройки пишутся в значения AC (ac = true).
    /// </summary>
    sealed class ActivePowerScheme : IPowerScheme
    {
        Guid scheme;
        readonly bool ac;

        public ActivePowerScheme() : this(false) { }

        public ActivePowerScheme(bool ac)
        {
            this.ac = ac;
            IntPtr p;
            if (PowerGetActiveScheme(IntPtr.Zero, out p) != 0) throw new InvalidOperationException("no active power scheme");
            scheme = (Guid)Marshal.PtrToStructure(p, typeof(Guid));
            LocalFree(p);
        }

        public bool ReadDc(Guid sub, Guid setting, out uint value)
        {
            return (ac ? PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, out value)
                       : PowerReadDCValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, out value)) == 0;
        }

        public bool WriteDc(Guid sub, Guid setting, uint value)
        {
            return (ac ? PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, value)
                       : PowerWriteDCValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, value)) == 0;
        }

        public void Activate() { PowerSetActiveScheme(IntPtr.Zero, ref scheme); }

        [DllImport("powrprof.dll")] static extern uint PowerGetActiveScheme(IntPtr root, out IntPtr guid);
        [DllImport("powrprof.dll")] static extern uint PowerReadDCValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, out uint value);
        [DllImport("powrprof.dll")] static extern uint PowerWriteDCValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, uint value);
        [DllImport("powrprof.dll")] static extern uint PowerReadACValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, out uint value);
        [DllImport("powrprof.dll")] static extern uint PowerWriteACValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, uint value);
        [DllImport("powrprof.dll")] static extern uint PowerSetActiveScheme(IntPtr root, ref Guid scheme);
        [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr h);
    }
}
