using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace BatteryCheck
{
    /// <summary>Что делать при работе от батареи.</summary>
    sealed class ProfileSettings
    {
        public int Brightness;        // яркость на батарее, %; 0 — не трогать
        public bool LowerRefresh;     // наименьшая частота экрана в текущем разрешении
        public readonly List<string> EfficiencyApps = new List<string>();  // программы в режиме эффективности на батарее

        public bool Enabled { get { return Brightness > 0 || LowerRefresh || EfficiencyApps.Count > 0; } }

        public bool HasApp(string name) { return EfficiencyApps.Exists(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase)); }
    }

    /// <summary>Что профиль изменил (хранится между запусками — чтобы откатить и после сбоя).</summary>
    sealed class ProfileState
    {
        public bool Applied;
        public int PrevBrightness = -1, SetBrightness = -1;
        public int PrevHz = -1, SetHz = -1;
        public readonly List<string> EffApplied = new List<string>();  // кому профиль включил режим эффективности

        public bool ChangedSomething { get { return SetBrightness >= 0 || SetHz > 0 || EffApplied.Count > 0; } }
    }

    /// <summary>Доступ к системе для профиля; в тестах — подделка.</summary>
    interface IProfileHost
    {
        int GetBrightness();               // -1 — не управляется
        bool SetBrightness(int pct);
        int GetRefresh();                  // текущая частота встроенного экрана, Гц; -1 — неизвестна
        int MinRefresh();                  // наименьшая в текущем разрешении; -1 — неизвестна
        bool SetRefresh(int hz);           // без записи в реестр: перезагрузка вернёт прежний режим
        bool ResetRefresh();               // вернуть режим из реестра
        void Log(string action, string from, string to, string by);
        /// <summary>Режим эффективности всем процессам с этим именем; changed — изменено, denied — Windows отказала (нужны права).</summary>
        void SetEfficiency(string name, bool on, out int changed, out int denied);
    }

    /// <summary>
    /// Профиль «На батарее»: применить — снизить яркость (если сейчас выше) и частоту экрана (если есть ниже);
    /// откатить — вернуть прежние значения, но только если пользователь не менял их сам (сравнение с тем, что
    /// поставил профиль). Всё, что сделано, — в журнал.
    /// </summary>
    static class ProfileLogic
    {
        public const string ByProfile = "profile", ByUser = "user";

        public static void Apply(ProfileSettings s, ProfileState st, IProfileHost host)
        {
            if (s.Brightness > 0)
            {
                int cur = host.GetBrightness();
                if (cur > s.Brightness && host.SetBrightness(s.Brightness))
                {
                    st.PrevBrightness = cur;
                    st.SetBrightness = s.Brightness;
                    host.Log("brightness", cur + " %", s.Brightness + " %", ByProfile);
                }
            }
            if (s.LowerRefresh)
            {
                int cur = host.GetRefresh(), min = host.MinRefresh();
                if (cur > 0 && min > 0 && min < cur && host.SetRefresh(min))
                {
                    st.PrevHz = cur;
                    st.SetHz = min;
                    host.Log("refresh", cur + " Hz", min + " Hz", ByProfile);
                }
            }
            foreach (var app in s.EfficiencyApps) EfficiencyOn(app, st, host);
            st.Applied = true;  // и если менять было нечего: иначе профиль применялся бы на каждом замере
        }

        static void EfficiencyOn(string app, ProfileState st, IProfileHost host)
        {
            int changed, denied;
            host.SetEfficiency(app, true, out changed, out denied);
            // В список — и если программа сейчас не запущена: Refresh включит режим, когда она появится.
            if (!st.EffApplied.Exists(a => string.Equals(a, app, StringComparison.OrdinalIgnoreCase))) st.EffApplied.Add(app);
            if (changed > 0) host.Log("efficiency", app + ": " + L.T("off", "вимк."), string.Format(L.T("on ({0} proc.)", "увімк. ({0} проц.)"), changed), ByProfile);
            else if (denied > 0) host.Log("efficiency", app, L.T("needs administrator rights", "потрібні права адміністратора"), ByProfile);
        }

        static void EfficiencyOff(string app, IProfileHost host)
        {
            int changed, denied;
            host.SetEfficiency(app, false, out changed, out denied);
            if (changed > 0) host.Log("efficiency", app + ": " + L.T("on", "увімк."), string.Format(L.T("off ({0} proc.)", "вимк. ({0} проц.)"), changed), ByProfile);
        }

        /// <summary>Пока профиль действует — новым процессам тех же программ (вкладки, перезапуски); без записей в журнал.</summary>
        public static void Refresh(ProfileState st, IProfileHost host)
        {
            if (!st.Applied) return;
            foreach (var app in st.EffApplied)
            {
                int changed, denied;
                host.SetEfficiency(app, true, out changed, out denied);
            }
        }

        /// <summary>
        /// Настройки сменились, пока профиль действует: перейти к новым значениям сразу, без возврата к прежним
        /// и повторного применения (иначе экран мигает). Прежние значения остаются для отката при подключении зарядки.
        /// </summary>
        public static void Update(ProfileSettings s, ProfileState st, IProfileHost host)
        {
            if (!st.Applied) return;
            if (st.SetBrightness >= 0)
            {
                int cur = host.GetBrightness();
                bool ours = cur >= 0 && Math.Abs(cur - st.SetBrightness) <= 1;  // пользователь не менял яркость сам
                if (ours)
                {
                    int target = s.Brightness > 0 && s.Brightness < st.PrevBrightness ? s.Brightness : st.PrevBrightness;
                    if (target != cur && host.SetBrightness(target))
                        host.Log("brightness", cur + " %", target + " %", ByProfile);
                    if (target == st.PrevBrightness) st.SetBrightness = st.PrevBrightness = -1;  // профиль больше ничего не держит
                    else st.SetBrightness = target;
                }
                else st.SetBrightness = st.PrevBrightness = -1;
            }
            else if (s.Brightness > 0)
            {
                int cur = host.GetBrightness();
                if (cur > s.Brightness && host.SetBrightness(s.Brightness))
                {
                    st.PrevBrightness = cur;
                    st.SetBrightness = s.Brightness;
                    host.Log("brightness", cur + " %", s.Brightness + " %", ByProfile);
                }
            }

            foreach (var app in s.EfficiencyApps)
                if (!st.EffApplied.Exists(a => string.Equals(a, app, StringComparison.OrdinalIgnoreCase))) EfficiencyOn(app, st, host);
            foreach (var app in st.EffApplied.ToArray())
                if (!s.HasApp(app))
                {
                    EfficiencyOff(app, host);
                    st.EffApplied.Remove(app);
                }

            if (st.SetHz > 0 && !s.LowerRefresh)
            {
                int cur = host.GetRefresh();
                if (cur == st.SetHz && host.ResetRefresh()) host.Log("refresh", cur + " Hz", st.PrevHz + " Hz", ByProfile);
                st.SetHz = st.PrevHz = -1;
            }
            else if (st.SetHz <= 0 && s.LowerRefresh)
            {
                int cur = host.GetRefresh(), min = host.MinRefresh();
                if (cur > 0 && min > 0 && min < cur && host.SetRefresh(min))
                {
                    st.PrevHz = cur;
                    st.SetHz = min;
                    host.Log("refresh", cur + " Hz", min + " Hz", ByProfile);
                }
            }
        }

        public static void Revert(ProfileState st, IProfileHost host)
        {
            if (st.SetBrightness >= 0)
            {
                int cur = host.GetBrightness();
                if (cur >= 0 && Math.Abs(cur - st.SetBrightness) <= 1)
                {
                    if (host.SetBrightness(st.PrevBrightness))
                        host.Log("brightness", cur + " %", st.PrevBrightness + " %", ByProfile);
                }
                else host.Log("brightness", cur + " %", cur + " % (" + L.T("changed by you — kept", "змінено вами — залишено") + ")", ByProfile);
            }
            if (st.SetHz > 0)
            {
                int cur = host.GetRefresh();
                if (cur == st.SetHz)
                {
                    if (host.ResetRefresh()) host.Log("refresh", cur + " Hz", st.PrevHz + " Hz", ByProfile);
                }
                else host.Log("refresh", cur + " Hz", cur + " Hz (" + L.T("changed by you — kept", "змінено вами — залишено") + ")", ByProfile);
            }
            foreach (var app in st.EffApplied) EfficiencyOff(app, host);
            st.EffApplied.Clear();
            st.Applied = false;
            st.PrevBrightness = st.SetBrightness = st.PrevHz = st.SetHz = -1;
        }

        /// <summary>Описание изменений для строки состояния.</summary>
        public static string Describe(ProfileState st)
        {
            var parts = new List<string>();
            if (st.SetBrightness >= 0) parts.Add(string.Format(L.T("brightness {0} → {1} %", "яскравість {0} → {1} %"), st.PrevBrightness, st.SetBrightness));
            if (st.SetHz > 0) parts.Add(string.Format(L.T("refresh {0} → {1} Hz", "частота {0} → {1} Гц"), st.PrevHz, st.SetHz));
            if (st.EffApplied.Count > 0) parts.Add(L.T("efficiency mode: ", "режим ефективності: ") + string.Join(", ", st.EffApplied));
            return string.Join(", ", parts);
        }
    }

    sealed class ActionEntry
    {
        public DateTime At;
        public string Action, From, To, By;
    }

    /// <summary>Журнал изменений системы: logs\actions.csv.</summary>
    static class ActionLog
    {
        const string Header = "time,action,from,to,by";
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public static void Append(string dir, string action, string from, string to, string by)
        {
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "actions.csv");
            bool header = !File.Exists(path);
            using (var sw = new StreamWriter(path, true, new UTF8Encoding(false)))
            {
                if (header) sw.WriteLine(Header);
                sw.WriteLine(string.Join(",", DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss", Inv), Clean(action), Clean(from), Clean(to), Clean(by)));
            }
        }

        static string Clean(string s) { return (s ?? "").Replace(",", ";").Replace("\n", " "); }

        public static List<ActionEntry> Load(string dir, int count)
        {
            var list = new List<ActionEntry>();
            string path = Path.Combine(dir, "actions.csv");
            if (!File.Exists(path)) return list;
            foreach (var line in File.ReadAllLines(path))
            {
                var c = line.Split(',');
                DateTime t;
                if (c.Length < 5 || !DateTime.TryParseExact(c[0], "yyyy-MM-ddTHH:mm:ss", Inv, DateTimeStyles.None, out t)) continue;
                list.Add(new ActionEntry { At = t, Action = c[1], From = c[2], To = c[3], By = c[4] });
            }
            list.Reverse();
            if (list.Count > count) list.RemoveRange(count, list.Count - count);
            return list;
        }

        /// <summary>Название действия на языке интерфейса.</summary>
        public static string ActionName(string code)
        {
            switch (code)
            {
                case "brightness": return L.T("brightness", "яскравість");
                case "refresh": return L.T("refresh rate", "частота екрана");
                case "power_mode_dc": return L.T("power mode on battery", "режим живлення від батареї");
                case "efficiency": return L.T("efficiency mode", "режим ефективності");
                case "gpu_pref": return L.T("graphics for the app", "графіка для програми");
                default:
                    if (code != null && code.StartsWith("economy:"))
                        return "Eco: " + MaxEconomy.NameOf(code.Substring("economy:".Length));
                    return code;
            }
        }
    }
}
