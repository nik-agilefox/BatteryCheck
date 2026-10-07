using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Web.Script.Serialization;
using Microsoft.Win32;

namespace BatteryCheck
{
    /// <summary>Текст на двух языках из rules.json: {"en": "...", "uk": "..."}; нет нужного — английский.</summary>
    sealed class RuleText
    {
        public string En, Uk;
        public override string ToString() { return L.Ua && !string.IsNullOrEmpty(Uk) ? Uk : En; }
    }

    sealed class RuleCategory
    {
        public string Id;
        public RuleText Name, Advice;
        public double TypicalBgW = 0.5;
        public readonly HashSet<string> Processes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public readonly List<string> Prefixes = new List<string>();
        public bool VendorUtilities;   // сюда же — утилиты всех производителей из справочника
    }

    sealed class RuleVendor
    {
        public string Id, Name;
        public readonly List<string> Match = new List<string>();      // начало строки производителя (BIOS), без регистра
        public readonly HashSet<string> Utilities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public readonly List<string> Prefixes = new List<string>();
        public RuleText ModeHint;
    }

    /// <summary>
    /// Правила советов: категории программ, справочник производителей, программы, которые опрашивают видеокарту.
    /// Встроенные — rules.json внутри программы; поверх — rules.user.json пользователя (рядом с папкой logs):
    /// записи с тем же id заменяются, новые добавляются, gpu_pollers объединяются.
    /// </summary>
    sealed class Rules
    {
        public readonly List<RuleCategory> Categories = new List<RuleCategory>();
        public readonly List<RuleVendor> Vendors = new List<RuleVendor>();
        public readonly HashSet<string> GpuPollers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public readonly HashSet<string> EfficiencyExclude = new HashSet<string>(StringComparer.OrdinalIgnoreCase);  // «Макс. экономия» не трогает
        public readonly List<string> SubzeroServices = new List<string>();  // режим Subzero останавливает (имена служб)
        public string UserFile;    // путь к rules.user.json, если он был прочитан
        public string UserError;   // ошибка в нём (встроенные правила при этом работают)

        public const string UserFileName = "rules.user.json";

        static Rules builtIn;

        /// <summary>Встроенные правила + пользовательский файл из dir (если есть).</summary>
        public static Rules Load(string dir)
        {
            var r = new Rules();
            r.Merge(BuiltInJson());
            string user = dir != null ? Path.Combine(dir, UserFileName) : null;
            if (user != null && File.Exists(user))
            {
                try
                {
                    r.Merge(File.ReadAllText(user));
                    r.UserFile = user;
                }
                catch (Exception e)
                {
                    r.UserError = user + ": " + e.Message;
                }
            }
            return r;
        }

        /// <summary>Только встроенные правила (кэшируются).</summary>
        public static Rules BuiltIn
        {
            get { return builtIn ?? (builtIn = Parse(BuiltInJson())); }
        }

        public static Rules Parse(string json)
        {
            var r = new Rules();
            r.Merge(json);
            return r;
        }

        static string BuiltInJson()
        {
            using (var s = typeof(Rules).Assembly.GetManifestResourceStream("BatteryCheck.rules.json"))
            {
                if (s == null) throw new InvalidOperationException("rules.json is not embedded");
                using (var reader = new StreamReader(s)) return reader.ReadToEnd();
            }
        }

        void Merge(string json)
        {
            var root = new JavaScriptSerializer().DeserializeObject(json) as Dictionary<string, object>;
            if (root == null) throw new FormatException("rules: the root must be an object");
            foreach (var o in List(root, "categories"))
            {
                var d = o as Dictionary<string, object>;
                if (d == null || Str(d, "id") == null) continue;
                var c = new RuleCategory
                {
                    Id = Str(d, "id"),
                    Name = Text(d, "name") ?? new RuleText { En = Str(d, "id") },
                    Advice = Text(d, "advice"),
                    VendorUtilities = d.ContainsKey("vendor_utilities") && d["vendor_utilities"] is bool && (bool)d["vendor_utilities"],
                };
                double w;
                if (TryNum(d, "typical_bg_w", out w)) c.TypicalBgW = w;
                foreach (var p in List(d, "processes")) if (p is string) c.Processes.Add((string)p);
                foreach (var p in List(d, "prefixes")) if (p is string) c.Prefixes.Add(((string)p).ToLowerInvariant());
                Categories.RemoveAll(x => x.Id == c.Id);
                Categories.Add(c);
            }
            foreach (var o in List(root, "vendors"))
            {
                var d = o as Dictionary<string, object>;
                if (d == null || Str(d, "id") == null) continue;
                var v = new RuleVendor { Id = Str(d, "id"), Name = Str(d, "name") ?? Str(d, "id"), ModeHint = Text(d, "mode_hint") };
                foreach (var p in List(d, "match")) if (p is string) v.Match.Add(((string)p).ToLowerInvariant());
                foreach (var p in List(d, "utilities")) if (p is string) v.Utilities.Add((string)p);
                foreach (var p in List(d, "prefixes")) if (p is string) v.Prefixes.Add(((string)p).ToLowerInvariant());
                Vendors.RemoveAll(x => x.Id == v.Id);
                Vendors.Add(v);
            }
            foreach (var p in List(root, "gpu_pollers")) if (p is string) GpuPollers.Add((string)p);
            foreach (var p in List(root, "efficiency_exclude")) if (p is string) EfficiencyExclude.Add((string)p);
            foreach (var p in List(root, "subzero_services"))
                if (p is string && !SubzeroServices.Exists(s => string.Equals(s, (string)p, StringComparison.OrdinalIgnoreCase))) SubzeroServices.Add((string)p);
        }

        /// <summary>Категория процесса (имя без .exe) или null.</summary>
        public RuleCategory CategoryOf(string process)
        {
            string n = process.ToLowerInvariant();
            foreach (var c in Categories)
                if (c.Processes.Contains(n)) return c;
            var vendorCat = Categories.Find(c => c.VendorUtilities);
            if (vendorCat != null)
                foreach (var v in Vendors)
                {
                    if (v.Utilities.Contains(n)) return vendorCat;
                    foreach (var p in v.Prefixes) if (n.StartsWith(p)) return vendorCat;
                }
            foreach (var c in Categories)
                foreach (var p in c.Prefixes)
                    if (n.StartsWith(p)) return c;
            return null;
        }

        /// <summary>Производитель по строке из BIOS («Acer», «LENOVO», «ASUSTeK COMPUTER INC.»…) или null.</summary>
        public RuleVendor VendorFor(string manufacturer)
        {
            if (string.IsNullOrEmpty(manufacturer)) return null;
            string m = manufacturer.Trim().ToLowerInvariant();
            foreach (var v in Vendors)
                foreach (var prefix in v.Match)
                    if (m.StartsWith(prefix)) return v;
            return null;
        }

        static IEnumerable List(Dictionary<string, object> d, string key)
        {
            object v;
            return d.TryGetValue(key, out v) && v is IEnumerable && !(v is string) ? (IEnumerable)v : new object[0];
        }

        static string Str(Dictionary<string, object> d, string key)
        {
            object v;
            return d.TryGetValue(key, out v) ? v as string : null;
        }

        static RuleText Text(Dictionary<string, object> d, string key)
        {
            object v;
            if (!d.TryGetValue(key, out v)) return null;
            if (v is string) return new RuleText { En = (string)v };
            var t = v as Dictionary<string, object>;
            if (t == null) return null;
            return new RuleText { En = Str(t, "en"), Uk = Str(t, "uk") ?? Str(t, "ua") };
        }

        static bool TryNum(Dictionary<string, object> d, string key, out double value)
        {
            object v;
            value = double.NaN;
            if (!d.TryGetValue(key, out v) || v == null) return false;
            try { value = Convert.ToDouble(v, CultureInfo.InvariantCulture); return true; }
            catch (Exception) { return false; }
        }
    }

    /// <summary>Модель ноутбука из BIOS — без прав администратора.</summary>
    static class Machine
    {
        public static string Manufacturer { get { return Bios("SystemManufacturer"); } }
        public static string Product { get { return Bios("SystemProductName"); } }

        static string Bios(string name)
        {
            try
            {
                using (var k = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS"))
                    return k == null ? null : k.GetValue(name) as string;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
