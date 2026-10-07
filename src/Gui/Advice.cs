using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;

namespace BatteryCheck
{
    /// <summary>Потребитель за период анализа.</summary>
    sealed class Consumer
    {
        public string Name, Category;
        public double Wh, FgWh, GpuWh;
        public double BgAvgW;        // фоновое потребление в среднем за всё время работы от батареи
        public double TypicalBgW;    // ориентир для категории
        public double OwnNormW = double.NaN;  // своя норма фона по прошлым дням (NaN — мало дней)
        public double OwnNowW = double.NaN;   // фон за период — по дням, когда программа работала (сравнимо с нормой)
        public int OwnDays;
        public bool AboveCategory, AboveOwn;
        public bool Atypical { get { return AboveCategory || AboveOwn; } }
        public double BgWh { get { return Wh - FgWh; } }
    }

    sealed class Recommendation
    {
        public string Title, Detail;
        public double SavingW = double.NaN;  // оценка экономии, Вт
        public double GainMin = double.NaN;  // прибавка автономности, мин
        public string Basis;                 // «замер», «оценка по логам», «типичное значение»
        public string Process;               // совет про программу — её имя (для режима эффективности)
    }

    sealed class AdviceReport
    {
        public double Hours, BatteryWh, AvgW = double.NaN, FullWh = double.NaN;
        public DateTime From, To;
        public readonly List<Consumer> Consumers = new List<Consumer>();
        public readonly List<Recommendation> Items = new List<Recommendation>();
        public string Vendor;        // производитель из справочника или null
        public string RulesNote;     // пользовательские правила: прочитаны или ошибка
        public QuietProcesses.Result Quiet;  // кто тратит, пока пользователь не работает
    }

    /// <summary>Текущее состояние системы для правил (передаёт окно).</summary>
    sealed class AdviceContext
    {
        public int RefreshHz = -1, MinRefreshHz = -1, Brightness = -1, DisplaysOnGpu;
        public double Luma = double.NaN;
        public ScreenModel Screen;
        public string PowerMode;           // код режима: efficiency, balanced, performance, best — или null
        public double FullWh = double.NaN;
        public string Manufacturer;        // из BIOS
        public bool GpuDisabled;           // дискретная видеокарта отключена в диспетчере устройств
        public double TimerMs = double.NaN; // разрешение системного таймера сейчас
        public bool HasBattery = true;     // ПК без аккумулятора: советы про экран ноутбука, батарею и сон видеокарты не к месту
        public HashSet<string> Running = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Рекомендации по правилам rules.json (категории программ, производители, программы-«будильники» видеокарты)
    /// и данным: logs\process_energy_*.csv (энергия процессов от батареи по минутам), logs\gpu_wakes.csv и текущее
    /// состояние системы. «Выше обычного» — выше своей нормы этой программы на этой машине (по прошлым дням),
    /// а если её ещё нет — выше ориентира категории; ориентир категории — эвристика, а не измерение.
    /// </summary>
    static class Advisor
    {
        public const int Days = 7;
        public const int NormDays = 30;  // своя норма — по 30 дням до периода анализа
        const double MinHours = 0.25;    // меньше — мало данных для выводов о программах

        public static AdviceReport Analyze(string dir, AdviceContext ctx)
        {
            var rules = Rules.Load(Path.GetDirectoryName(Path.GetFullPath(dir)));
            var r = new AdviceReport { To = DateTime.Now, From = DateTime.Now.AddDays(-Days), FullWh = ctx.FullWh };
            var vendor = rules.VendorFor(ctx.Manufacturer);
            r.Vendor = vendor != null ? vendor.Name : null;
            if (rules.UserError != null) r.RulesNote = L.T("Error in your rules file, built-in rules are used: ", "Помилка у вашому файлі правил, діють вбудовані: ") + rules.UserError;
            else if (rules.UserFile != null) r.RulesNote = L.T("Your rules are applied: ", "Діють ваші правила: ") + rules.UserFile;

            var byName = new Dictionary<string, Consumer>(StringComparer.OrdinalIgnoreCase);
            var history = new ProcessHistory();
            DateTime historyFrom = r.From.AddDays(-NormDays);
            double seconds = 0;
            DateTime first = DateTime.MaxValue, last = DateTime.MinValue;
            var inv = CultureInfo.InvariantCulture;

            if (Directory.Exists(dir))
                foreach (var f in Directory.GetFiles(dir, "process_energy_*.csv"))
                {
                    DateTime day;
                    string stamp = Path.GetFileNameWithoutExtension(f).Substring("process_energy_".Length);
                    if (DateTime.TryParseExact(stamp, "yyyyMMdd", inv, DateTimeStyles.None, out day) && day < historyFrom.Date) continue;
                    try
                    {
                        using (var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                        using (var sr = new StreamReader(fs))
                        {
                            sr.ReadLine();
                            string line;
                            while ((line = sr.ReadLine()) != null)
                            {
                                var c = line.Split(',');
                                DateTime m;
                                if (c.Length < 6 || !DateTime.TryParseExact(c[0], "yyyy-MM-ddTHH:mm", inv, DateTimeStyles.None, out m) || m < historyFrom) continue;
                                double wh = Num(c[2]);
                                if (c[1] == ProcessEnergyLog.BatteryRow)
                                {
                                    history.AddBattery(m, Num(c[5]));
                                    if (m < r.From) continue;
                                    r.BatteryWh += wh;
                                    seconds += Num(c[5]);
                                    if (m < first) first = m;
                                    if (m > last) last = m;
                                    continue;
                                }
                                double fg = Num(c[3]);
                                history.AddProcess(m, c[1], Math.Max(0, wh - fg));
                                if (m < r.From) continue;
                                Consumer x;
                                if (!byName.TryGetValue(c[1], out x)) byName[c[1]] = x = new Consumer { Name = c[1] };
                                x.Wh += wh;
                                x.FgWh += fg;
                                x.GpuWh += Num(c[4]);
                            }
                        }
                    }
                    catch (IOException) { }
                }

            r.Hours = seconds / 3600;
            if (first != DateTime.MaxValue) { r.From = first; r.To = last.AddMinutes(1); }
            if (r.Hours > 0) r.AvgW = r.BatteryWh / r.Hours;

            DateTime windowStart = DateTime.Now.AddDays(-Days);
            foreach (var x in byName.Values)
            {
                var k = rules.CategoryOf(x.Name);
                x.Category = k != null ? k.Name.ToString() : L.T("app", "програма");
                x.TypicalBgW = k != null ? k.TypicalBgW : 0.5;
                x.BgAvgW = r.Hours > 0 ? x.BgWh / r.Hours : double.NaN;
                x.OwnNormW = history.Norm(x.Name, windowStart, out x.OwnDays);
                x.OwnNowW = history.Current(x.Name, windowStart);
                bool enough = r.Hours >= MinHours && x.BgWh >= 0.2;
                x.AboveOwn = enough && ProcessHistory.AboveNorm(x.OwnNowW, x.OwnNormW);
                x.AboveCategory = enough && x.BgAvgW > Math.Max(x.TypicalBgW * 1.5, x.TypicalBgW + 0.2);
                r.Consumers.Add(x);
            }
            r.Consumers.Sort((a, b) => b.Wh.CompareTo(a.Wh));

            AddProcessAdvice(r, rules);
            r.Quiet = QuietProcesses.Compute(dir, Days, DateTime.Now);
            AddQuietAdvice(r, rules);
            if (ctx.HasBattery) AddWakeAdvice(r, dir, ctx, rules);  // на ПК видеокарта с монитором не спит вовсе
            if (ctx.HasBattery) AddScreenAdvice(r, ctx);            // встроенный экран ноутбука
            AddSystemAdvice(r, ctx, vendor);
            r.Items.Sort((a, b) => (double.IsNaN(b.SavingW) ? -1 : b.SavingW).CompareTo(double.IsNaN(a.SavingW) ? -1 : a.SavingW));
            return r;
        }

        /// <summary>Сколько минут автономности даст экономия saveW при средней мощности и полной ёмкости.</summary>
        static double GainMinutes(AdviceReport r, double saveW)
        {
            if (double.IsNaN(r.AvgW) || double.IsNaN(r.FullWh) || saveW <= 0 || r.AvgW - saveW <= 1) return double.NaN;
            return (r.FullWh / (r.AvgW - saveW) - r.FullWh / r.AvgW) * 60;
        }

        static void AddProcessAdvice(AdviceReport r, Rules rules)
        {
            foreach (var x in r.Consumers)
            {
                if (!x.Atypical || QuietSkip.Contains(x.Name)) continue;  // System, dwm… — части Windows: «закройте программу» к ним не относится
                var k = rules.CategoryOf(x.Name);
                string advice = k != null && k.Advice != null ? k.Advice.ToString() : null;
                bool gpu = x.GpuWh > x.Wh * 0.4;
                string detail = (advice != null ? char.ToUpper(advice[0]) + advice.Substring(1) + "."
                                    : L.T("Close the app if you do not need it, or check its power-saving settings.",
                                          "Закрийте програму, якщо вона не потрібна, або перевірте її налаштування енергозбереження.")) +
                                (gpu ? L.T(" Most of it is the GPU: video, animation or 3D in the background — or the app simply keeps the discrete GPU awake. Quit it fully (it may stay in the tray after the window is closed) or set it to Power saving in Settings → Display → Graphics.",
                                          " Більша частина — відеокарта: відео, анімації чи 3D у фоні — або програма просто не дає дискретній відеокарті заснути. Закрийте її повністю (після закриття вікна вона може лишатися в треї) або виберіть «Енергозбереження» в Параметрах → Дисплей → Графіка.") : "");
                // Своя норма точнее ориентира категории: она про эту программу на этой машине.
                if (x.AboveOwn)
                {
                    double save = x.OwnNowW - x.OwnNormW;
                    r.Items.Add(new Recommendation
                    {
                        Title = string.Format(L.T("{0} uses {1:0.0} W in the background — usually {2:0.0} W on this laptop",
                                                  "{0} у фоні витрачає {1:0.0} Вт — зазвичай на цьому ноутбуці {2:0.0} Вт"),
                            x.Name, x.OwnNowW, x.OwnNormW),
                        Detail = L.T("Something changed in it this week (an update, a setting, a stuck tab or task). ", "Цього тижня в ній щось змінилося (оновлення, налаштування, завислі вкладка чи завдання). ") + detail,
                        SavingW = save,
                        GainMin = GainMinutes(r, save),
                        Basis = string.Format(L.T("vs your usual ({0} days)", "проти вашої норми ({0} дн.)"), x.OwnDays),
                        Process = x.Name,
                    });
                    continue;
                }
                double saveCat = x.BgAvgW - x.TypicalBgW;
                r.Items.Add(new Recommendation
                {
                    Title = string.Format(L.T("{0} uses {1:0.0} W in the background — typical for “{2}” is up to {3:0.0} W",
                                              "{0} у фоні витрачає {1:0.0} Вт — зазвичай для категорії «{2}» до {3:0.0} Вт"),
                        x.Name, x.BgAvgW, x.Category, x.TypicalBgW),
                    Detail = detail,
                    SavingW = saveCat,
                    GainMin = GainMinutes(r, saveCat),
                    Basis = L.T("estimate from logs", "оцінка за логами"),
                    Process = x.Name,
                });
            }
        }
        static readonly HashSet<string> QuietSkip = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "System", "dwm", "csrss", "BatteryCheckGui", "BatteryCheck", "Memory Compression", "Registry" };

        /// <summary>Программа тратит ≥ 0,5 Вт в тихие минуты (пользователь не работает) — совет, если о ней ещё нет.</summary>
        static void AddQuietAdvice(AdviceReport r, Rules rules)
        {
            if (r.Quiet == null || r.Quiet.Minutes < 10) return;
            foreach (var kv in r.Quiet.Top)
            {
                if (kv.Value < 0.5) break;
                if (QuietSkip.Contains(kv.Key) || r.Items.Exists(i => string.Equals(i.Process, kv.Key, StringComparison.OrdinalIgnoreCase))) continue;
                var k = rules.CategoryOf(kv.Key);
                string advice = k != null && k.Advice != null ? k.Advice.ToString() : null;
                r.Items.Add(new Recommendation
                {
                    Title = string.Format(L.T("{0} uses {1:0.0} W while you are away", "{0} витрачає {1:0.0} Вт, поки ви не працюєте"), kv.Key, kv.Value),
                    Detail = (advice != null ? char.ToUpper(advice[0]) + advice.Substring(1) + ". " : "") +
                             L.T("Nothing is happening on the screen, yet it keeps working: close it when not needed or add it to efficiency mode on battery.",
                                 "На екрані нічого не відбувається, а вона працює: закривайте її, коли не потрібна, або додайте до режиму ефективності від батареї."),
                    SavingW = kv.Value,
                    GainMin = GainMinutes(r, kv.Value),
                    Basis = string.Format(L.T("quiet minutes: {0}", "тихих хвилин: {0}"), r.Quiet.Minutes),
                    Process = kv.Key,
                });
            }
        }

        static void AddWakeAdvice(AdviceReport r, string dir, AdviceContext ctx, Rules rules)
        {
            string path = Path.Combine(dir, "gpu_wakes.csv");
            if (!File.Exists(path) || r.Hours < MinHours) return;
            int n = 0, unknown = 0;
            double wh = 0;
            DateTime firstWake = DateTime.MaxValue;
            try
            {
                foreach (var line in File.ReadAllLines(path))
                {
                    // start,end,seconds,cost_wh,flags,cause; строки прежнего формата (без flags) — по русским меткам в причине
                    var c = line.Split(new[] { ',' }, 6);
                    DateTime t;
                    if (c.Length < 5 || !DateTime.TryParse(c[0], CultureInfo.InvariantCulture, DateTimeStyles.None, out t) || t < r.From) continue;
                    bool old = c[4].StartsWith("\"");
                    string flags = old ? "" : c[4];
                    string cause = old ? string.Join(",", c, 4, c.Length - 4) : "";
                    if (flags == "self" || (old && cause.Contains("замер экрана"))) continue;  // это сама программа
                    if (c[3].Length == 0) continue;               // энергия не посчитана — было от сети, а делим на время от батареи
                    n++;
                    wh += Num(c[3]);
                    if (flags == "unknown" || (old && cause.Contains("неизвестно"))) unknown++;
                    if (t < firstWake) firstWake = t;
                }
            }
            catch (IOException) { return; }
            if (n < 5) return;
            double avgW = wh / Math.Max(r.Hours, 0.1);
            var suspects = new List<string>();
            foreach (var s in rules.GpuPollers)
                if (ctx.Running.Contains(s)) suspects.Add(s);
            suspects.Sort(StringComparer.OrdinalIgnoreCase);
            r.Items.Add(new Recommendation
            {
                Title = string.Format(L.T("The NVIDIA GPU wakes ~{0:0} times an hour — ≈ {1:0.0} W on average", "Відеокарта NVIDIA прокидається ~{0:0} разів на годину — ≈ {1:0.0} Вт у середньому"),
                    n / Math.Max(r.Hours, 0.1), avgW),
                Detail = (unknown * 2 >= n
                        ? L.T("Most wake-ups involve no GPU work: a monitoring app polls it (temperature, clocks). ",
                              "Більшість пробуджень — без роботи на відеокарті: її опитує програма-монітор (температура, частоти). ")
                        : L.T("Wake-ups are caused by apps that run on the GPU (see “NVIDIA wake-ups”). ",
                              "Пробудження викликають програми, що працюють на відеокарті (див. «Пробудження NVIDIA»). ")) +
                    (suspects.Count > 0
                        ? L.T("Likely culprits running now: ", "Зараз запущено ймовірних винуватців: ") + string.Join(", ", suspects) +
                          L.T(" — close them on battery.", " — закривайте їх при роботі від батареї.")
                        : L.T("Open “NVIDIA wake-ups” to see the culprits.", "Відкрийте «Пробудження NVIDIA», щоб побачити винуватців.")),
                SavingW = avgW,
                GainMin = GainMinutes(r, avgW),
                Basis = L.T("measured from the wake-up log", "вимір за журналом пробуджень"),
            });
        }

        static void AddScreenAdvice(AdviceReport r, AdviceContext ctx)
        {
            // Только если у экрана в текущем разрешении есть частота ниже: бывают панели с единственной частотой
            // (у этого ноутбука — только 240 Гц), и совет «переключите на 60 Гц» для них невыполним.
            if (ctx.RefreshHz > 60 && ctx.MinRefreshHz > 0 && ctx.MinRefreshHz < ctx.RefreshHz)
            {
                const double typical = 1.5;  // 240 → 60 Гц: обычно 1–2 Вт; оценка, не замер
                r.Items.Add(new Recommendation
                {
                    Title = string.Format(L.T("The screen runs at {0} Hz", "Екран працює на {0} Гц"), ctx.RefreshHz),
                    Detail = string.Format(L.T("On battery, switch to {0} Hz (Settings → System → Display → Advanced display) or turn on Windows dynamic refresh rate if it is offered there. The saving is typical for such screens; it was not measured on this laptop.",
                                               "При роботі від батареї перемкніть частоту на {0} Гц (Параметри → Система → Дисплей → Додаткові параметри дисплея) або увімкніть у Windows динамічну частоту оновлення, якщо вона там є. Економія — типова для таких екранів, на цьому ноутбуці не вимірювалася."),
                        ctx.MinRefreshHz),
                    SavingW = typical,
                    GainMin = GainMinutes(r, typical),
                    Basis = TypicalText,
                });
            }
            if (ctx.Screen != null && ctx.Brightness >= 0 && !double.IsNaN(ctx.Luma))
            {
                double now = ctx.Screen.At(ctx.Brightness) * ctx.Luma;
                if (ctx.Brightness >= 70)
                {
                    double save = now - ctx.Screen.At(50) * ctx.Luma;
                    if (save >= 0.3)
                        r.Items.Add(new Recommendation
                        {
                            Title = string.Format(L.T("Brightness {0} % with bright content", "Яскравість {0} % при світлому вмісті"), ctx.Brightness),
                            Detail = L.T("On OLED, brightness is expensive exactly on bright areas. Lower brightness to ~50 % on battery.",
                                         "На OLED яскравість дорого коштує саме на світлих ділянках. Знизьте яскравість до ~50 % при роботі від батареї."),
                            SavingW = save, GainMin = GainMinutes(r, save), Basis = ScreenMeasureText,
                        });
                }
                if (ctx.Luma > 0.3)
                {
                    double save = ctx.Screen.At(ctx.Brightness) * (ctx.Luma - 0.1);
                    r.Items.Add(new Recommendation
                    {
                        Title = string.Format(L.T("Lots of white on screen ({0:0} %)", "На екрані багато білого ({0:0} %)"), ctx.Luma * 100),
                        Detail = L.T("On OLED, a dark theme in the system and apps almost eliminates the screen's cost for backgrounds.",
                                     "На OLED темна тема в системі та програмах майже обнуляє витрати екрана на фон."),
                        SavingW = save, GainMin = GainMinutes(r, save), Basis = ScreenMeasureText,
                    });
                }
            }
        }

        static void AddSystemAdvice(AdviceReport r, AdviceContext ctx, RuleVendor vendor)
        {
            if (ctx.HasBattery && (ctx.PowerMode == "performance" || ctx.PowerMode == "best"))  // совет про режим «от батареи»
                r.Items.Add(new Recommendation
                {
                    Title = string.Format(L.T("Windows power mode on battery: “{0}”", "Режим живлення Windows від батареї: «{0}»"), PowerModeName(ctx.PowerMode)),
                    Detail = string.Format(L.T("On battery, choose “{0}” or “{1}” (Settings → System → Power, or the profile in the Advice tab).",
                                               "При роботі від батареї виберіть «{0}» або «{1}» (Параметри → Система → Живлення або профіль у вкладці «Поради»)."),
                        PowerModeName("balanced"), PowerModeName("efficiency")) +
                        (vendor != null && vendor.ModeHint != null ? " " + vendor.ModeHint : ""),
                    Basis = TypicalText,
                });
            if (ctx.TimerMs <= 2)
                r.Items.Add(new Recommendation
                {
                    Title = string.Format(L.T("The system timer runs at {0:0.0} ms", "Системний таймер працює з кроком {0:0.0} мс"), ctx.TimerMs),
                    Detail = L.T("Some app asked Windows to wake up the processor about a thousand times a second, so the chip cannot sleep deeply. Usually it is a browser, a messenger, a game launcher or a media app. Switch to Eco — Windows then ignores such requests from background apps — or to Subzero, which also stops the vendor services that do this (on Acer: Quick Access, Care Center, Device Info).",
                                 "Якась програма попросила Windows будити процесор приблизно тисячу разів на секунду, тож чип не може глибоко заснути. Зазвичай це браузер, месенджер, лаунчер ігор чи медіапрограма. Увімкніть Eco — тоді Windows ігнорує такі запити від фонових програм — або Subzero, що ще й зупиняє служби виробника, які це роблять (на Acer: Quick Access, Care Center, Device Info)."),
                    SavingW = 1, GainMin = GainMinutes(r, 1), Basis = L.T("typical: 0.5–2 W", "типово: 0,5–2 Вт"),
                });
            if (ctx.GpuDisabled)
                r.Items.Add(new Recommendation
                {
                    Title = L.T("The discrete GPU is disabled in Device Manager", "Дискретну відеокарту вимкнено в диспетчері пристроїв"),
                    Detail = MainWindow.GpuDisabledText,
                    Basis = L.T("measured on a Predator PHN16S-71: +17 W", "виміряно на Predator PHN16S-71: +17 Вт"),
                });
            if (ctx.HasBattery && ctx.DisplaysOnGpu > 0)  // у ПК монитор на видеокарте — норма
                r.Items.Add(new Recommendation
                {
                    Title = L.T("An external monitor is connected to the NVIDIA GPU", "Зовнішній монітор підключено до відеокарти NVIDIA"),
                    Detail = L.T("While it outputs an image, the GPU cannot sleep. On battery, disconnect the monitor or connect it to a USB-C/Thunderbolt port driven by the integrated graphics.",
                                 "Поки на неї виводиться зображення, вона не може заснути. При роботі від батареї відключіть монітор або підключіть його до порту USB-C/Thunderbolt від вбудованої графіки."),
                    SavingW = 8, GainMin = GainMinutes(r, 8), Basis = L.T("typical: a discrete GPU kept awake, 5–10 W", "типово: дискретна відеокарта, що не спить, 5–10 Вт"),
                });
            if (r.Hours < MinHours)
                r.Items.Add(new Recommendation
                {
                    Title = L.T("Not enough app data", "Замало даних про програми"),
                    Detail = string.Format(L.T("Collected {0} on battery. App statistics are collected in the background on battery; conclusions need at least 15 minutes, better several discharges.",
                                               "Зібрано {0} роботи від батареї. Статистика програм збирається у фоні при роботі від батареї; для висновків потрібно хоча б 15 хвилин, краще — кілька розрядів."),
                        Fmt.Hours(r.Hours)),
                    Basis = "",
                });
        }

        static string TypicalText { get { return L.T("typical value", "типове значення"); } }
        static string ScreenMeasureText { get { return L.T("screen measurement", "вимір екрана"); } }

        /// <summary>Название режима питания так, как его показывают Параметры Windows.</summary>
        public static string PowerModeName(string code)
        {
            switch (code)
            {
                case "efficiency": return L.T("Best power efficiency", "Максимальна ефективність");
                case "balanced": return L.T("Balanced", "Збалансований");
                case "performance": return L.T("Better performance", "Краща продуктивність");
                case "best": return L.T("Best performance", "Максимальна продуктивність");
                default: return code;
            }
        }

        static double Num(string s)
        {
            double v;
            return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? v : 0;
        }

        // ---- Состояние системы ----

        /// <summary>Частота обновления экрана, Гц, или -1.</summary>
        public static int RefreshRate(string deviceName)
        {
            var dm = new DEVMODE { dmSize = (short)Marshal.SizeOf(typeof(DEVMODE)) };
            return EnumDisplaySettings(deviceName, -1, ref dm) ? dm.dmDisplayFrequency : -1;  // -1 = ENUM_CURRENT_SETTINGS
        }

        /// <summary>Наименьшая частота, которую драйвер предлагает в текущем разрешении, Гц, или -1.</summary>
        public static int MinRefreshRate(string deviceName)
        {
            var cur = new DEVMODE { dmSize = (short)Marshal.SizeOf(typeof(DEVMODE)) };
            if (!EnumDisplaySettings(deviceName, -1, ref cur)) return -1;
            int min = -1;
            for (int i = 0; ; i++)
            {
                var dm = new DEVMODE { dmSize = (short)Marshal.SizeOf(typeof(DEVMODE)) };
                if (!EnumDisplaySettings(deviceName, i, ref dm)) break;
                if (dm.dmPelsWidth != cur.dmPelsWidth || dm.dmPelsHeight != cur.dmPelsHeight || dm.dmDisplayFrequency <= 1) continue;
                if (min < 0 || dm.dmDisplayFrequency < min) min = dm.dmDisplayFrequency;
            }
            return min;
        }

        /// <summary>
        /// Код режима питания Windows для работы от батареи (ползунок «Режим питания» хранится отдельно для сети и батареи), или null.
        /// Если Windows не умеет отдавать настройку для батареи — текущий режим, но только когда ноутбук и так от батареи.
        /// </summary>
        public static string PowerMode(bool onBattery)
        {
            Guid g;
            try
            {
                if (PowerGetUserConfiguredDCPowerMode(out g) != 0) return null;
            }
            catch (EntryPointNotFoundException)
            {
                try
                {
                    if (!onBattery || PowerGetEffectiveOverlayScheme(out g) != 0) return null;
                }
                catch (Exception)
                {
                    return null;
                }
            }
            {
                string s = g.ToString().ToLowerInvariant();
                if (s == "961cc777-2547-4f9d-8174-7d86181b8a7a") return "efficiency";
                if (s == "3af9b8d9-7c97-431d-ad78-34a8bfea439f") return "performance";
                if (s == "ded574b5-45a0-4f42-8737-46345c09c238") return "best";
                if (g == Guid.Empty || s == "00000000-0000-0000-0000-000000000000") return "balanced";
                return null;
            }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct DEVMODE
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
            public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
            public int dmFields, dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
            public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
            public short dmLogPixels;
            public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
            public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern bool EnumDisplaySettings(string deviceName, int modeNum, ref DEVMODE dm);

        [DllImport("powrprof.dll")]
        static extern uint PowerGetEffectiveOverlayScheme(out Guid overlay);

        [DllImport("powrprof.dll")]
        static extern uint PowerGetUserConfiguredDCPowerMode(out Guid mode);
    }
}
