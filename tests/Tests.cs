// Тесты ядра на синтетических логах: воспроизведение через Sampler, сессия разряда, честность индикатора заряда,
// миграция журнала пробуждений, языки. Сборка и запуск: test.cmd. Код выхода — число упавших тестов.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace BatteryCheck
{
    static class Tests
    {
        static int failed, passed;
        static string current;

        static int Main()
        {
            Console.OutputEncoding = Encoding.UTF8;
            var tests = new List<KeyValuePair<string, Action>>
            {
                T("Plural: Ukrainian and English forms", PluralForms),
                T("Fmt.Hours in both languages", HoursFormat),
                T("Replay: session energy at 1 s steps", () => ReplayEnergy(1)),
                T("Replay: session energy at 10 s steps (background polling)", () => ReplayEnergy(10)),
                T("Replay: «rest» = battery − CPU − GPU", ReplayRest),
                T("Replay: sleep gap is not integrated and is reported", ReplaySleep),
                T("Replay: old log without power_state and dgpu columns", ReplayOldLog),
                T("Gauge: honest indicator → ratio 1.00, Ok", GaugeHonest),
                T("Gauge: percentages drop 25 % faster → ratio 0.80, Faster", GaugeFaster),
                T("Gauge: percentage jump is detected", GaugeJump),
                T("Gauge: one band drifts in the last 3 discharges → Drifting", GaugeDrifting),
                T("Gauge: too little data → Unknown", GaugeUnknown),
                T("Wake log: old format is migrated once", WakeLogMigration),
                T("Update: release JSON → version, package, sha256", ReleaseParse),
                T("Update: no package / draft / prerelease → no update", ReleaseRejected),
                T("Update: version comparison", ReleaseVersions),
                T("Baseline: quietest minutes, busy and GPU-awake minutes excluded", BaselineQuiet),
                T("Baseline: user input excludes a minute", BaselineInput),
                T("Baseline: not enough quiet minutes → not valid", BaselineTooFew),
                T("Excess: breakdown adds up to the total", ExcessBreakdown),
                T("Experiment: minutes with input, other brightness or hidden window are not comparable", RecorderFilters),
                T("Experiment: GPU wake-ups and awake share per minute", RecorderWakes),
                T("Experiment: «before» from recent minutes only if fresh and continuous", RecorderRecent),
                T("Experiment: clear effect is found, gain in minutes", ExperimentFound),
                T("Experiment: noise only → no difference", ExperimentNoise),
                T("Experiment: log round trip", ExperimentLogRoundTrip),
                T("Profile: lowers brightness and refresh, restores on revert", ProfileApplyRevert),
                T("Profile: does not raise brightness that is already lower", ProfileNoRaise),
                T("Profile: value changed by the user is kept on revert", ProfileUserChanged),
                T("Profile: single-rate screen → refresh untouched", ProfileSingleRate),
                T("Action log: round trip, newest first", ActionLogRoundTrip),
                T("Max economy: applies, restores, keeps what the user changed", MaxEconomyApplyRevert),
                T("GPU preference: apps on the discrete GPU go to power saving; other fields kept; restored", GpuPreferenceFlow),
                T("Background efficiency: all but the active app, its children, exclusions; switch and restore", BackgroundEfficiencyFlow),
                T("Rules: efficiency exclusions are loaded", RulesEfficiencyExclude),
                T("Process snapshot (Toolhelp) sees our own process with its name and parent", SystemSnapshot),
                T("Efficiency: real EcoQoS and priority on our own process, on and off", EfficiencyOwnProcess),
                T("Efficiency: own process can be changed, a SYSTEM process cannot", EfficiencyCanSet),
                T("Profile: efficiency mode on unplug, refresh, remove from list, revert", ProfileEfficiency),
                T("Profile: efficiency for a service is reported as needing admin", ProfileEfficiencyDenied),
                T("Profile: changing the target while active — straight to it, no flicker", ProfileUpdateTarget),
                T("Profile: changing to «don't change» or above the old value restores it", ProfileUpdateRestore),
                T("Profile: change while active after the user set brightness — not touched", ProfileUpdateUser),
                T("Rules: built-in categories, vendor utilities and prefixes", RulesBuiltIn),
                T("Rules: vendor from the BIOS manufacturer string", RulesVendor),
                T("Rules: user file replaces and adds by id; a broken file is reported", RulesUserFile),
                T("Own norm: median of past days, above-norm check", OwnNorm),
                T("Own norm: too few days → none", OwnNormFewDays),
                T("Quiet minutes: apps while the user is away, minutes with input excluded", QuietProcessesFlow),
                T("Notify: low battery once per threshold per discharge", NotifyLow),
                T("Notify: drain while away, not with input, not twice in 30 min", NotifyDrain),
                T("Notify: GPU awake 5 min without load; busy GPU is not reported", NotifyGpu),
                T("Notify: external monitor once per discharge", NotifyMonitor),
                T("Notify: disabled discrete GPU on battery", NotifyGpuDisabled),
                T("Notify: GPU awake — names the app holding video memory on it", NotifyGpuHolder),
            };
            L.Apply(false);
            foreach (var t in tests)
            {
                current = t.Key;
                int before = failed;
                try { t.Value(); }
                catch (Exception e) { Fail("exception: " + e); }
                if (failed == before) { passed++; Console.WriteLine("PASS  " + t.Key); }
                L.Apply(false);
            }
            Console.WriteLine();
            Console.WriteLine("{0} passed, {1} failed", passed, failed);
            return failed;
        }

        static KeyValuePair<string, Action> T(string name, Action a) { return new KeyValuePair<string, Action>(name, a); }

        static void Fail(string message)
        {
            failed++;
            Console.WriteLine("FAIL  " + current + ": " + message);
        }

        static void Check(bool ok, string message) { if (!ok) Fail(message); }

        static void Near(double actual, double expected, double tolerance, string what)
        {
            if (double.IsNaN(actual) || Math.Abs(actual - expected) > tolerance)
                Fail(string.Format(CultureInfo.InvariantCulture, "{0}: expected {1} ± {2}, got {3}", what, expected, tolerance, actual));
        }

        static void Equal<T2>(T2 actual, T2 expected, string what)
        {
            if (!Equals(actual, expected)) Fail(string.Format("{0}: expected «{1}», got «{2}»", what, expected, actual));
        }

        // ---------------- Обновления ----------------

        static string ReleaseJson(string tag, string asset, bool draft, bool pre)
        {
            return "{\"tag_name\":\"" + tag + "\",\"html_url\":\"https://github.com/x/y/releases/tag/" + tag + "\",\"body\":\"fixes\"," +
                   "\"draft\":" + (draft ? "true" : "false") + ",\"prerelease\":" + (pre ? "true" : "false") + ",\"assets\":[" +
                   "{\"name\":\"BatteryCheckSetup-1.2.0.exe\",\"size\":10,\"browser_download_url\":\"https://github.com/x/y/releases/download/v1.2.0/BatteryCheckSetup-1.2.0.exe\"}," +
                   "{\"name\":\"" + asset + "\",\"size\":12345,\"digest\":\"sha256:ABCDEF\",\"browser_download_url\":\"https://github.com/x/y/releases/download/" + tag + "/" + asset + "\"}]}";
        }

        static void ReleaseParse()
        {
            var r = ReleaseInfo.Parse(ReleaseJson("v1.2.0", "BatteryCheck-1.2.0.zip", false, false));
            Check(r != null, "parsed");
            if (r == null) return;
            Equal(r.Version, "1.2.0", "version without v");
            Check(r.PackageUrl.EndsWith("/BatteryCheck-1.2.0.zip"), "package url, not the setup");
            Equal(r.PackageSize, 12345L, "size");
            Equal(r.PackageSha256, "abcdef", "sha256 lower-case, without prefix");
            Equal(r.Notes, "fixes", "notes");
        }

        static void ReleaseRejected()
        {
            Check(ReleaseInfo.Parse(ReleaseJson("v1.2.0", "other.zip", false, false)) == null, "no package");
            Check(ReleaseInfo.Parse(ReleaseJson("v1.2.0", "BatteryCheck-1.2.0.zip", true, false)) == null, "draft");
            Check(ReleaseInfo.Parse(ReleaseJson("v1.2.0", "BatteryCheck-1.2.0.zip", false, true)) == null, "prerelease");
            Check(ReleaseInfo.Parse("{\"message\":\"Not Found\"}") == null, "API error");
            Check(ReleaseInfo.Parse("not json") == null, "garbage");
        }

        static void ReleaseVersions()
        {
            Check(ReleaseInfo.IsNewer("1.10.0", "1.9.3"), "1.10 > 1.9");
            Check(!ReleaseInfo.IsNewer("1.0.0", "1.0.0"), "same");
            Check(!ReleaseInfo.IsNewer("0.9", "1.0.0"), "older");
            Check(!ReleaseInfo.IsNewer("abc", "1.0.0"), "garbage is not newer");
            Check(!ReleaseInfo.IsNewer(AppInfo.Version, AppInfo.Version), "current");
        }

        // ---------------- Синтетические логи ----------------

        const uint FullMwh = 76320;

        /// <summary>Пишет battery_*.csv в формате CsvLog (часть колонок — разбор идёт по заголовку).</summary>
        sealed class LogBuilder
        {
            readonly StringBuilder sb = new StringBuilder();
            public DateTime T = new DateTime(2026, 9, 1, 10, 0, 0);
            public double CapMwh = FullMwh * 0.9;
            public int DState = 3;                    // видеокарта: 3 — спит, 0 — работает
            public double GpuW = 0;                   // её мощность, когда работает
            public double Idle = 600;                 // секунд без ввода; NaN — колонка пустая
            public double Screen = double.NaN;        // оценка экрана, Вт
            readonly bool oldFormat;

            public LogBuilder(bool oldFormat)
            {
                this.oldFormat = oldFormat;
                sb.AppendLine(oldFormat
                    ? "time,on_ac,charging,discharging,capacity_mwh,full_mwh,soc_pct,voltage_mv,rate_mw,cpu_pkg_w"
                    : "time,power_state,on_ac,charging,discharging,capacity_mwh,full_mwh,soc_pct,voltage_mv,rate_mw,cpu_pkg_w,dgpu_w,dgpu_dstate,idle_s,screen_w");
            }

            /// <summary>
            /// Разряд: seconds секунд с шагом step при мощности watts. capFactor — во сколько раз быстрее мощности
            /// убывает остаток (1 — индикатор честный). capFactorBand — свой множитель внутри полосы заряда [lo, hi).
            /// </summary>
            public LogBuilder Discharge(int seconds, int step, double watts, double capFactor, double cpuW = 8,
                                        double bandLo = -1, double bandHi = -1, double capFactorBand = 1)
            {
                for (int s = 0; s <= seconds; s += step)
                {
                    Row(false, -watts * 1000, cpuW);
                    double soc = CapMwh / FullMwh * 100;
                    double f = soc >= bandLo && soc < bandHi ? capFactorBand : capFactor;
                    CapMwh -= watts * step / 3.6 * f;
                    T = T.AddSeconds(step);
                }
                return this;
            }

            public LogBuilder OnAc(int seconds)
            {
                for (int s = 0; s <= seconds; s += 10)
                {
                    Row(true, 0, 8);
                    T = T.AddSeconds(10);
                }
                return this;
            }

            public LogBuilder Gap(int seconds) { T = T.AddSeconds(seconds); return this; }

            public LogBuilder Drop(double pct) { CapMwh -= FullMwh * pct / 100; return this; }

            void Row(bool ac, double rateMw, double cpuW)
            {
                var inv = CultureInfo.InvariantCulture;
                string time = T.ToString("yyyy-MM-ddTHH:mm:ss.fff", inv);
                string cap = ((uint)Math.Round(CapMwh)).ToString(inv);
                string soc = (CapMwh / FullMwh * 100).ToString("0.00", inv);
                string rate = ((int)Math.Round(rateMw)).ToString(inv);
                string cpu = cpuW.ToString("0.000", inv);
                if (oldFormat)
                    sb.AppendLine(string.Join(",", time, ac ? "1" : "0", "0", ac ? "0" : "1", cap, FullMwh.ToString(inv), soc, "16000", rate, cpu));
                else
                    sb.AppendLine(string.Join(",", time, ac ? "1" : "2", ac ? "1" : "0", "0", ac ? "0" : "1", cap, FullMwh.ToString(inv), soc,
                        "16000", rate, cpu, (DState == 3 ? 0 : GpuW).ToString("0.000", inv), DState.ToString(inv),
                        double.IsNaN(Idle) ? "" : Idle.ToString("0", inv), double.IsNaN(Screen) ? "" : Screen.ToString("0.000", inv)));
            }

            public string Save(string name)
            {
                string dir = Path.Combine(Path.GetTempPath(), "bc_tests", name);
                if (Directory.Exists(dir)) foreach (var f in Directory.GetFiles(dir)) File.Delete(f);
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, "battery_20260901_100000.csv"), sb.ToString(), new UTF8Encoding(false));
                return dir;
            }
        }

        static Snapshot RunReplay(string dir)
        {
            Snapshot last = null;
            using (var s = new Sampler(LogReplay.Load(dir)))
                s.RunReplay(0, null, snap => last = snap);
            return last;
        }

        // ---------------- Тесты ----------------

        static void PluralForms()
        {
            L.Apply(true);
            Func<long, string> times = n => L.Plural(n, "time", "times", "раз", "рази", "разів");
            Equal(times(1), "раз", "1");
            Equal(times(2), "рази", "2");
            Equal(times(4), "рази", "4");
            Equal(times(5), "разів", "5");
            Equal(times(11), "разів", "11");
            Equal(times(12), "разів", "12");
            Equal(times(21), "раз", "21");
            Equal(times(22), "рази", "22");
            Equal(times(111), "разів", "111");
            L.Apply(false);
            Equal(times(1), "time", "en 1");
            Equal(times(2), "times", "en 2");
            Equal(times(0), "times", "en 0");
        }

        static void HoursFormat()
        {
            Equal(Fmt.Hours(65 / 60.0), "1 h 05 min", "en 65 min");
            Equal(Fmt.Hours(0.75), "45 min", "en 45 min");
            Equal(Fmt.Hours(double.NaN), "—", "NaN");
            L.Apply(true);
            Equal(Fmt.Hours(65 / 60.0), "1 год 05 хв", "ua 65 min");
            Equal(Fmt.W(23.4), "23,4 Вт", "ua W");
        }

        static void ReplayEnergy(int step)
        {
            string dir = new LogBuilder(false).Discharge(1800, step, 20, 1).Save("energy" + step);
            var last = RunReplay(dir);
            Near(last.SessionByRateWh, 10, 0.05, "energy by power");
            Near(last.SessionByCapacityWh, 10, 0.05, "energy by capacity");
            Near(last.SessionS, 1800, 1, "session duration");
            Near(last.SessionAvgW, 20, 0.01, "average power");
            Near(last.HoursLeft, last.Sample.Battery.Capacity / 1000.0 / 20, 0.01, "hours left");
        }

        static void ReplayRest()
        {
            string dir = new LogBuilder(false).Discharge(120, 1, 20, 1, 8).Save("rest");
            var last = RunReplay(dir);
            Near(last.RestW, 12, 0.05, "rest");
            Check(last.RaplAvailable, "RAPL should be available from cpu_pkg_w");
            Check(last.GpuName != null, "GPU should be detected from dgpu columns");
        }

        static void ReplaySleep()
        {
            string dir = new LogBuilder(false).Discharge(600, 1, 20, 1).Gap(7200).Drop(2).Discharge(600, 1, 20, 1).Save("sleep");
            var last = RunReplay(dir);
            Near(last.SessionS, 1200, 2, "session excludes the gap");
            Near(last.SessionByRateWh, 20.0 * 1200 / 3600, 0.05, "energy excludes the gap");
            Near(last.LastSleepS, 7201, 2, "sleep duration");
            Near(last.LastSleepWh, FullMwh * 0.02 / 1000 + 20.0 / 3600, 0.05, "sleep energy");
        }

        static void ReplayOldLog()
        {
            string dir = new LogBuilder(true).Discharge(600, 1, 15, 1).Save("oldlog");
            var last = RunReplay(dir);
            Check(last.Sample.Battery.Discharging, "discharging flag rebuilt from columns");
            Near(last.SessionByRateWh, 2.5, 0.02, "energy");
            Check(last.GpuName == null, "no GPU in old log");
            Near(DischargeHistory.Load(dir)[0].GaugeRatio, 1, 0.01, "gauge ratio");
        }

        static void GaugeHonest()
        {
            string dir = new LogBuilder(false).Discharge(3600, 1, 20, 1).Save("honest");
            var sessions = DischargeHistory.Load(dir);
            Equal(sessions.Count, 1, "sessions");
            Near(sessions[0].GaugeRatio, 1, 0.005, "ratio");
            Equal(sessions[0].Jumps, 0, "jumps");
            var v = DischargeHistory.Evaluate(sessions);
            Equal(v.Status, GaugeVerdict.Kind.Ok, "status");
            Check(!v.Warning, "no warning");
        }

        static void GaugeFaster()
        {
            string dir = new LogBuilder(false).Discharge(3600, 10, 20, 1.25).Save("faster");
            var sessions = DischargeHistory.Load(dir);
            Near(sessions[0].GaugeRatio, 0.8, 0.005, "ratio");
            var v = DischargeHistory.Evaluate(sessions);
            Equal(v.Status, GaugeVerdict.Kind.Faster, "status");
            Check(v.Describe().StartsWith("⚠"), "warning text");
        }

        static void GaugeJump()
        {
            string dir = new LogBuilder(false).Discharge(600, 1, 20, 1).Drop(4).Discharge(600, 1, 20, 1).Save("jump");
            var s = DischargeHistory.Load(dir)[0];
            Equal(s.Jumps, 1, "jumps");
        }

        static void GaugeDrifting()
        {
            // Шесть разрядов 85 → 25 %; в последних трёх полоса 40–60 % убывает в 1,25 раза быстрее.
            var b = new LogBuilder(false);
            for (int i = 0; i < 6; i++)
            {
                b.CapMwh = FullMwh * 0.85;
                // 60 % ёмкости при 20 Вт: 45,8 Вт·ч ≈ 8240 с (с поправкой на быструю полосу — немного меньше, неважно)
                b.Discharge(8000, 10, 20, 1, 8, 40, 60, i >= 3 ? 1.25 : 1).OnAc(600).Gap(3600);
            }
            var sessions = DischargeHistory.Load(b.Save("drift"));
            Equal(sessions.Count, 6, "sessions");
            var v = DischargeHistory.Evaluate(sessions);
            Equal(v.Status, GaugeVerdict.Kind.Drifting, "status");
            Equal(v.Band, 2, "band");
            Near(v.BandNow, 0.8, 0.02, "band ratio now");
            Near(v.BandUsual, 1, 0.02, "band ratio usual");
        }

        static void GaugeUnknown()
        {
            string dir = new LogBuilder(false).Discharge(300, 1, 20, 1).Save("unknown");
            var sessions = DischargeHistory.Load(dir);
            Equal(DischargeHistory.Evaluate(sessions).Status, GaugeVerdict.Kind.Unknown, "status");
        }

        static Baseline BaselineOf(string dir)
        {
            return IdleBaseline.Compute(LogReplay.Load(dir), DateTime.MinValue);
        }

        static void BaselineQuiet()
        {
            var b = new LogBuilder(false);
            b.Screen = 1.5;
            b.Discharge(1800, 1, 11, 1, 3);                     // 30 тихих минут: 11 Вт, процессор 3 Вт
            b.DState = 0; b.GpuW = 6;
            b.Discharge(600, 1, 25, 1, 8);                      // видеокарта не спит — не тихие
            b.DState = 3;
            b.Discharge(1200, 10, 30, 1, 18);                   // тяжёлая нагрузка в фоне: «тихие», но не из самых тихих 20 %
            var bl = BaselineOf(b.Save("baseline"));
            Check(bl.Valid, "valid");
            Check(bl.InputKnown, "input known");
            Near(bl.QuietMinutes, 30 + 20, 2, "quiet minutes");
            Near(bl.TotalW, 11, 0.01, "total");
            Near(bl.CpuPkgW, 3, 0.01, "cpu");
            Near(bl.RestW, 8, 0.01, "rest");
            Near(bl.ScreenW, 1.5, 0.01, "screen");
        }

        static void BaselineInput()
        {
            var b = new LogBuilder(false);
            b.Idle = 5;
            b.Discharge(1800, 1, 9, 1, 3);                      // при работе пользователя расход ниже — но это не простой
            b.Idle = 600;
            b.Discharge(1800, 1, 12, 1, 3);
            var bl = BaselineOf(b.Save("baseline_input"));
            Near(bl.TotalW, 12, 0.01, "total ignores minutes with input");
            var old = new LogBuilder(false);
            old.Idle = double.NaN;
            old.Discharge(1800, 1, 9, 1, 3);
            var blOld = BaselineOf(old.Save("baseline_noinput"));
            Check(!blOld.InputKnown && blOld.Valid, "old logs without idle_s still give a baseline");
        }

        static void BaselineTooFew()
        {
            var bl = BaselineOf(new LogBuilder(false).Discharge(600, 1, 11, 1).Save("baseline_few"));
            Check(!bl.Valid, "not valid");
            Near(bl.QuietMinutes, 10, 1, "quiet minutes");
            Check(BaselineText.Describe(bl).Contains("/") || BaselineText.Describe(bl).Contains("of"), "collecting text");
        }

        static void ExcessBreakdown()
        {
            var bl = new Baseline { QuietMinutes = 30, TotalW = 11, CpuPkgW = 3, RestW = 7, ScreenW = 1 };
            var e = Excess.Compute(bl, 25, 5, 4, 2);
            Near(e.TotalW, 14, 1e-9, "total");
            Near(e.GpuW, 4, 1e-9, "gpu");
            Near(e.CpuW, 2 * Excess.CpuOverhead, 1e-9, "cpu");
            Near(e.ScreenW, 1, 1e-9, "screen");
            Near(e.GpuW + e.CpuW + e.ScreenW + e.OtherW, e.TotalW, 1e-9, "sum");
            Check(!e.AtIdle && e.Short().Contains("+14.0"), "short text: " + e.Short());
            Check(Excess.Compute(bl, 11.2, 3, 0, 1).AtIdle, "at idle");
        }

        static readonly DateTime T0 = new DateTime(2026, 9, 2, 12, 0, 0);

        static Sample S(DateTime t, double w, double cpu = 5, int dstate = 3, double idle = 600)
        {
            return new Sample
            {
                Time = t, TimeUtc = t.ToUniversalTime(), CpuPkgW = cpu, GpuDState = dstate, IdleS = idle,
                Battery = new BatteryStatus { PowerState = 2, Capacity = 50000, Voltage = 16000, Rate = -(int)Math.Round(w * 1000) },
            };
        }

        /// <summary>Секунда за секундой: minutes минут с мощностью w(i) (i — номер секунды).</summary>
        static void Feed(MinuteRecorder r, DateTime from, int minutes, Func<int, double> w, int brightness = 50, bool onScreen = true,
                         Func<int, int> dstate = null, Func<int, double> idle = null)
        {
            for (int i = 0; i < minutes * 60; i++)
                r.Add(S(from.AddSeconds(i), w(i), 5, dstate != null ? dstate(i) : 3, idle != null ? idle(i) : 600), brightness, onScreen);
        }

        static void RecorderFilters()
        {
            var r = new MinuteRecorder();
            Feed(r, T0, 3, i => 20);                                            // 12:00–12:03 тихие
            Feed(r, T0.AddMinutes(3), 1, i => 20, 50, true, null, i => 5);      // 12:03 — ввод
            Feed(r, T0.AddMinutes(4), 1, i => 20, 80);                          // 12:04 — другая яркость (своя в начале минуты — сопоставима сама с собой)
            Feed(r, T0.AddMinutes(5), 1, i => i == 30 ? 20 : 20, 50, false);    // 12:05 — окно скрыто
            r.Add(S(T0.AddMinutes(6), 20), 50, true);
            r.Add(S(T0.AddMinutes(6).AddSeconds(20), 20), 80, true);           // 12:06 — яркость сменилась внутри минуты
            r.Add(S(T0.AddMinutes(6).AddSeconds(40), 20), 80, true);
            var ok = r.OkMinutes(T0, T0.AddMinutes(10));
            Equal(ok.Count, 4, "comparable minutes (12:00–12:02 and 12:04)");
            Check(ok.TrueForAll(m => m.T != T0.AddMinutes(3) && m.T != T0.AddMinutes(5) && m.T != T0.AddMinutes(6)), "excluded minutes");
            Equal(r.OkMinutes(T0, T0.AddMinutes(2).AddSeconds(30)).Count, 2, "current minute is not finished yet");
        }

        static void RecorderWakes()
        {
            var r = new MinuteRecorder();
            // Минута: 2 пробуждения по 10 с (секунды 20–29 и 40–49).
            Feed(r, T0, 1, i => 20, 50, true, i => i % 20 < 10 && i >= 10 ? 0 : 3);
            var m = r.OkMinutes(T0, T0.AddMinutes(1))[0];
            Equal(m.Wakes, 2, "wakes");
            Near(m.AwakeShare, 20.0 / 60, 1e-9, "awake share");
            var p = PhaseStats.Of(new List<ExpMinute> { m });
            Near(p.WakesPerHour, 120, 1e-9, "wakes per hour");
        }

        static void RecorderRecent()
        {
            var r = new MinuteRecorder();
            Feed(r, T0, 10, i => 20);
            Equal(r.RecentOk(8, T0.AddMinutes(10).AddSeconds(5)).Count, 8, "fresh");
            Equal(r.RecentOk(8, T0.AddMinutes(20)).Count, 0, "stale");
            var g = new MinuteRecorder();
            Feed(g, T0, 4, i => 20);
            Feed(g, T0.AddMinutes(4), 4, i => 20, 50, true, null, i => 1);      // 4 минуты работы пользователя
            Feed(g, T0.AddMinutes(8), 3, i => 20);
            Equal(g.RecentOk(8, T0.AddMinutes(11)).Count, 3, "stops at a gap");
        }

        static PhaseStats Phase(DateTime from, int minutes, Func<int, double> w)
        {
            var r = new MinuteRecorder();
            Feed(r, from, minutes, w);
            return PhaseStats.Of(r.OkMinutes(from, from.AddMinutes(minutes)));
        }

        static void ExperimentFound()
        {
            var rnd = new Random(1);
            var before = Phase(T0, 8, i => 20 + rnd.NextDouble() - 0.5);
            var after = Phase(T0.AddMinutes(10), 8, i => 17 + rnd.NextDouble() - 0.5);
            var e = ExperimentResult.Compare("closed X", before, after, 76.32);
            Near(e.DeltaW, -3, 0.05, "delta");
            Check(e.Found, "found");
            Near(e.GainMin, (76.32 / after.W - 76.32 / before.W) * 60, 1e-6, "gain");
            Check(e.Headline().StartsWith("−3.0 ±"), "headline: " + e.Headline());
        }

        static void ExperimentNoise()
        {
            var rnd = new Random(2);
            // Шум по минутам ±2 Вт, разницы нет.
            var before = Phase(T0, 8, i => 20 + 4 * (rnd.NextDouble() - 0.5) * (i % 60 == 0 ? 15 : 0));
            var after = Phase(T0.AddMinutes(10), 8, i => 20 + 4 * (rnd.NextDouble() - 0.5) * (i % 60 == 0 ? 15 : 0));
            var e = ExperimentResult.Compare("nothing", before, after, 76.32);
            Check(!e.Found, string.Format("no effect expected: {0}", e.Headline()));
            Check(e.Headline().Contains("no difference"), "headline says so");
        }

        static void ExperimentLogRoundTrip()
        {
            string dir = Path.Combine(Path.GetTempPath(), "bc_tests", "experiments");
            if (Directory.Exists(dir)) foreach (var f in Directory.GetFiles(dir)) File.Delete(f);
            var before = Phase(T0, 8, i => 20);
            var after = Phase(T0.AddMinutes(10), 8, i => 18);
            ExperimentLog.Append(dir, ExperimentResult.Compare("first, with \"quotes\"", before, after, 76.32));
            ExperimentLog.Append(dir, ExperimentResult.Compare("second", after, before, 76.32));
            var list = ExperimentLog.Load(dir, 5);
            Equal(list.Count, 2, "count");
            Equal(list[0].Title, "second", "newest first");
            Equal(list[1].Title, "first, with 'quotes'", "title with comma and quotes");
            Near(list[1].DeltaW, -2, 1e-3, "delta");
            Equal(list[1].Before.Minutes, 8, "minutes");
        }

        sealed class FakeHost : IProfileHost
        {
            public int Brightness = 80, Hz = 240, MinHz = 60, RegistryHz = 240;
            public readonly List<string> Log = new List<string>();
            public int GetBrightness() { return Brightness; }
            public bool SetBrightness(int pct) { Brightness = pct; return true; }
            public int GetRefresh() { return Hz; }
            public int MinRefresh() { return MinHz; }
            public bool SetRefresh(int hz) { Hz = hz; return true; }
            public bool ResetRefresh() { Hz = RegistryHz; return true; }
            public readonly Dictionary<string, bool> Eff = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            public readonly HashSet<string> Denied = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public int EffCalls;
            public void SetEfficiency(string name, bool on, out int changed, out int denied)
            {
                EffCalls++;
                changed = denied = 0;
                if (Denied.Contains(name)) { denied = 1; return; }
                bool cur;
                Eff.TryGetValue(name, out cur);
                if (cur != on) { Eff[name] = on; changed = 1; }
            }
            void IProfileHost.Log(string action, string from, string to, string by) { Log.Add(action + ":" + from + ">" + to + ":" + by); }
        }

        static void ProfileApplyRevert()
        {
            var h = new FakeHost();
            var s = new ProfileSettings { Brightness = 50, LowerRefresh = true };
            var st = new ProfileState();
            ProfileLogic.Apply(s, st, h);
            Equal(h.Brightness, 50, "brightness lowered");
            Equal(h.Hz, 60, "refresh lowered");
            Check(st.Applied && st.ChangedSomething, "state");
            Equal(h.Log.Count, 2, "logged");
            ProfileLogic.Revert(st, h);
            Equal(h.Brightness, 80, "brightness restored");
            Equal(h.Hz, 240, "refresh restored");
            Check(!st.Applied && !st.ChangedSomething, "state cleared");
            Equal(h.Log.Count, 4, "revert logged");
        }

        static void ProfileNoRaise()
        {
            var h = new FakeHost { Brightness = 30 };
            var st = new ProfileState();
            ProfileLogic.Apply(new ProfileSettings { Brightness = 50 }, st, h);
            Equal(h.Brightness, 30, "kept lower brightness");
            Check(st.Applied && !st.ChangedSomething, "applied, nothing changed");
            Equal(h.Log.Count, 0, "nothing logged");
        }

        static void ProfileUserChanged()
        {
            var h = new FakeHost();
            var st = new ProfileState();
            ProfileLogic.Apply(new ProfileSettings { Brightness = 50, LowerRefresh = true }, st, h);
            h.Brightness = 65;   // пользователь прибавил яркость на батарее
            h.Hz = 120;          // и сам выбрал другую частоту
            ProfileLogic.Revert(st, h);
            Equal(h.Brightness, 65, "user brightness kept");
            Equal(h.Hz, 120, "user refresh kept");
            Check(h.Log[h.Log.Count - 1].StartsWith("refresh:120 Hz>120 Hz"), "kept is logged: " + h.Log[h.Log.Count - 1]);
        }

        static void ProfileSingleRate()
        {
            var h = new FakeHost { MinHz = 240 };
            var st = new ProfileState();
            ProfileLogic.Apply(new ProfileSettings { LowerRefresh = true }, st, h);
            Equal(h.Hz, 240, "refresh untouched");
            Check(!st.ChangedSomething, "nothing changed");
        }

        static void ProfileUpdateTarget()
        {
            var h = new FakeHost { Brightness = 70 };
            var st = new ProfileState();
            ProfileLogic.Apply(new ProfileSettings { Brightness = 30 }, st, h);
            int logged = h.Log.Count;
            ProfileLogic.Update(new ProfileSettings { Brightness = 50 }, st, h);
            Equal(h.Brightness, 50, "straight to the new target");
            Equal(h.Log.Count, logged + 1, "one change, not revert + apply");
            Equal(st.PrevBrightness, 70, "old value kept for the revert");
            ProfileLogic.Revert(st, h);
            Equal(h.Brightness, 70, "revert to the value before the profile");

            var fresh = new FakeHost { Brightness = 40 };
            var st2 = new ProfileState();
            ProfileLogic.Apply(new ProfileSettings { Brightness = 50 }, st2, fresh);   // 40 ≤ 50 — нечего менять
            ProfileLogic.Update(new ProfileSettings { Brightness = 30 }, st2, fresh);
            Equal(fresh.Brightness, 30, "lower target applies now");
            Equal(st2.PrevBrightness, 40, "prev");
        }

        static void ProfileUpdateRestore()
        {
            var h = new FakeHost { Brightness = 70 };
            var st = new ProfileState();
            ProfileLogic.Apply(new ProfileSettings { Brightness = 30 }, st, h);
            ProfileLogic.Update(new ProfileSettings { Brightness = 0 }, st, h);
            Equal(h.Brightness, 70, "«don't change» restores");
            Check(!st.ChangedSomething, "profile holds nothing");
            var st2 = new ProfileState();
            ProfileLogic.Apply(new ProfileSettings { Brightness = 30 }, st2, h);
            ProfileLogic.Update(new ProfileSettings { Brightness = 80 }, st2, h);
            Equal(h.Brightness, 70, "target above the old value — the old value, not 80");
        }

        static void ProfileUpdateUser()
        {
            var h = new FakeHost { Brightness = 70 };
            var st = new ProfileState();
            ProfileLogic.Apply(new ProfileSettings { Brightness = 30 }, st, h);
            h.Brightness = 55;   // пользователь сам
            ProfileLogic.Update(new ProfileSettings { Brightness = 40 }, st, h);
            Equal(h.Brightness, 55, "user value kept");
            Check(!st.ChangedSomething, "profile lets go");
        }

        static void EfficiencyOwnProcess()
        {
            var me = System.Diagnostics.Process.GetCurrentProcess();
            Check(Efficiency.Set(me.Id, true), "set on");
            Equal(Efficiency.IsOn(me.Id), (bool?)true, "is on");
            me.Refresh();
            Equal(me.PriorityClass, System.Diagnostics.ProcessPriorityClass.Idle, "idle priority");
            Check(Efficiency.Set(me.Id, false), "set off");
            Equal(Efficiency.IsOn(me.Id), (bool?)false, "is off");
            me.Refresh();
            Equal(me.PriorityClass, System.Diagnostics.ProcessPriorityClass.Normal, "normal priority back");
        }

        static void EfficiencyCanSet()
        {
            Equal(Efficiency.CanSet(System.Diagnostics.Process.GetCurrentProcess().ProcessName), (bool?)true, "own process");
            Equal(Efficiency.CanSet("csrss"), (bool?)false, "csrss runs as SYSTEM");
            Equal(Efficiency.CanSet("no-such-process-bc"), (bool?)null, "not running");
        }

        static void ProfileEfficiency()
        {
            var h = new FakeHost();
            var s = new ProfileSettings();
            s.EfficiencyApps.Add("chrome");
            s.EfficiencyApps.Add("telegram");
            Check(s.Enabled, "enabled by the list alone");
            var st = new ProfileState();
            ProfileLogic.Apply(s, st, h);
            Check(h.Eff["chrome"] && h.Eff["telegram"], "on for both");
            Equal(st.EffApplied.Count, 2, "remembered");
            Check(ProfileLogic.Describe(st).Contains("chrome, telegram"), "described: " + ProfileLogic.Describe(st));
            int calls = h.EffCalls;
            ProfileLogic.Refresh(st, h);
            Equal(h.EffCalls, calls + 2, "refresh touches both (new processes)");
            s.EfficiencyApps.Remove("telegram");
            ProfileLogic.Update(s, st, h);
            Check(!h.Eff["telegram"], "removed app back to normal");
            Equal(st.EffApplied.Count, 1, "one left");
            ProfileLogic.Revert(st, h);
            Check(!h.Eff["chrome"], "revert");
            Equal(st.EffApplied.Count, 0, "cleared");
        }

        static void ProfileEfficiencyDenied()
        {
            var h = new FakeHost();
            h.Denied.Add("AcerSysMonitorService");
            var s = new ProfileSettings();
            s.EfficiencyApps.Add("AcerSysMonitorService");
            var st = new ProfileState();
            ProfileLogic.Apply(s, st, h);
            Check(h.Log.Count == 1 && h.Log[0].Contains("administrator"), "logged: " + string.Join(" / ", h.Log));
        }

        sealed class FakeScheme : IPowerScheme
        {
            public readonly Dictionary<Guid, uint> V = new Dictionary<Guid, uint>();
            public int Activated;
            public bool ReadDc(Guid sub, Guid setting, out uint value) { return V.TryGetValue(setting, out value); }
            public bool WriteDc(Guid sub, Guid setting, uint value) { V[setting] = value; return true; }
            public void Activate() { Activated++; }
        }

        sealed class FakePrefs : IGpuPreferenceStore
        {
            public readonly Dictionary<string, string> V = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public string Get(string p) { string v; return V.TryGetValue(p, out v) ? v : null; }
            public void Set(string p, string v) { if (v == null) V.Remove(p); else V[p] = v; }
        }

        static void GpuPreferenceFlow()
        {
            var store = new FakePrefs();
            store.V[@"C:\a\Figma.exe"] = "SwapEffectUpgradeEnable=1;GpuPreference=2;";
            store.V[@"C:\a\Already.exe"] = "GpuPreference=1;";
            Func<string, string> exe = n => @"C:\a\" + n + ".exe";
            var m = new GpuPreferenceManager(store, exe, null);
            var log = new List<string>();
            Action<string, string, string> L3 = (a, b, c) => log.Add(a);
            var procs = new List<ProcessPower>
            {
                new ProcessPower { Name = "Figma", DgpuMemMB = 197 },
                new ProcessPower { Name = "Game", DgpuMemMB = 900 },
                new ProcessPower { Name = "dwm", DgpuMemMB = 45 },
                new ProcessPower { Name = "Already", DgpuMemMB = 50 },
                new ProcessPower { Name = "Small", DgpuMemMB = 4 },
            };
            var moved = m.Update(procs, L3);
            Equal(string.Join(",", moved), "Figma,Game", "moved");
            Equal(store.V[@"C:\a\Figma.exe"], "SwapEffectUpgradeEnable=1;GpuPreference=1;", "other field kept");
            Equal(store.V[@"C:\a\Game.exe"], "GpuPreference=1;", "new value");
            Equal(m.Update(procs, L3).Count, 0, "not twice");
            var restored = new GpuPreferenceManager(store, exe, GpuPreferenceManager.Parse(GpuPreferenceManager.Serialize(m.Saved)));
            store.V[@"C:\a\Game.exe"] = "GpuPreference=2;";      // пользователь сам выбрал дискретную
            restored.RestoreAll(L3);
            Equal(store.V[@"C:\a\Figma.exe"], "SwapEffectUpgradeEnable=1;GpuPreference=2;", "Figma restored");
            Equal(store.V[@"C:\a\Game.exe"], "GpuPreference=2;", "user choice kept");
            Equal(store.V[@"C:\a\Already.exe"], "GpuPreference=1;", "not ours — untouched");
            var fresh = new FakePrefs();
            var m2 = new GpuPreferenceManager(fresh, exe, null);
            m2.Update(new[] { new ProcessPower { Name = "New", DgpuMemMB = 100 } }, L3);
            m2.RestoreAll(L3);
            Check(!fresh.V.ContainsKey(@"C:\a\New.exe"), "value that did not exist is removed again");
        }

        static void MaxEconomyApplyRevert()
        {
            var s = new FakeScheme();
            var boost = MaxEconomy.Settings[0].Setting;
            var epp = MaxEconomy.Settings[1].Setting;
            s.V[boost] = 2; s.V[epp] = 33; s.V[MaxEconomy.Settings[2].Setting] = 33; s.V[MaxEconomy.Settings[3].Setting] = 10;
            var log = new List<string>();
            var saved = MaxEconomy.Apply(s, (id, a, b) => log.Add(id + ":" + a + ">" + b));
            Equal(s.V[boost], 0u, "boost off");
            Equal(s.V[epp], 100u, "epp 100");
            Equal(saved.Count, 4, "saved");
            Equal(s.Activated, 1, "activated once");
            var parsed = MaxEconomy.Parse(MaxEconomy.Serialize(saved));
            Equal(parsed["boost"], 2u, "round trip");
            s.V[epp] = 50;                       // пользователь сам поменял
            MaxEconomy.Revert(s, parsed, (id, a, b) => log.Add(id + ":" + a + ">" + b));
            Equal(s.V[boost], 2u, "boost restored");
            Equal(s.V[epp], 50u, "user value kept");
            Equal(s.V[MaxEconomy.Settings[3].Setting], 10u, "energy saver restored");
            Equal(MaxEconomy.Apply(new FakeScheme(), (a, b, c) => { }).Count, 0, "unreadable settings are skipped");
        }

        sealed class FakeProcs : IProcessSource
        {
            public List<ProcEntry> List = new List<ProcEntry>();
            public int Fg;
            public readonly Dictionary<int, bool> On = new Dictionary<int, bool>();
            public List<ProcEntry> Snapshot() { return new List<ProcEntry>(List); }
            public int ForegroundPid() { return Fg; }
            public bool? IsOn(int pid) { bool v; return On.TryGetValue(pid, out v) ? v : false; }
            public bool Set(int pid, bool on) { On[pid] = on; return true; }
            public void Add(int pid, int parent, string name) { List.Add(new ProcEntry { Pid = pid, Parent = parent, Name = name }); }
        }

        static void BackgroundEfficiencyFlow()
        {
            var s = new FakeProcs();
            s.Add(10, 1, "chrome"); s.Add(11, 10, "chrome"); s.Add(12, 11, "node");   // активное окно, его процессы и потомок
            s.Add(20, 1, "Telegram"); s.Add(30, 1, "explorer"); s.Add(40, 1, "Slack"); s.Add(99, 1, "BatteryCheckGui");
            s.On[40] = true;  // Slack уже в режиме эффективности (Windows или пользователь)
            s.Fg = 10;
            var b = new BackgroundEfficiency(s, new[] { "explorer" }, 99);
            Check(b.Tick(true, T0), "started");
            Check(s.On[20], "Telegram throttled");
            Check(!s.On.ContainsKey(10) && !s.On.ContainsKey(11) && !s.On.ContainsKey(12), "active app, its processes and child are free");
            Check(!s.On.ContainsKey(30) && !s.On.ContainsKey(99), "exclusion and self untouched");
            Equal(b.Count, 1, "ours");
            s.Fg = 20;                                    // переключились на Telegram
            b.Tick(true, T0.AddSeconds(1));
            Check(!s.On[20], "new active app released at once");
            Check(s.On[10] && s.On[11] && s.On[12], "previous active app throttled");
            s.List.RemoveAll(p => p.Pid == 12);           // процесс завершился
            b.Tick(true, T0.AddSeconds(15));
            Equal(b.Count, 2, "dead process forgotten");
            Check(b.Tick(false, T0.AddSeconds(16)), "stopped");
            Check(!s.On[10] && !s.On[11], "restored");
            Check(s.On[40], "Slack was not ours — left as is");
            Equal(b.Count, 0, "nothing left");
        }

        static void SystemSnapshot()
        {
            var me = System.Diagnostics.Process.GetCurrentProcess();
            var list = new SystemProcessSource().Snapshot();
            var mine = list.Find(p => p.Pid == me.Id);
            Check(mine != null, "own process found among " + list.Count);
            if (mine != null) { Equal(mine.Name, me.ProcessName, "name without .exe"); Check(mine.Parent > 0, "parent"); }
            Check(list.Count > 20, "many processes in the session");
        }

        static void RulesEfficiencyExclude()
        {
            var r = Rules.BuiltIn;
            Check(r.EfficiencyExclude.Contains("explorer") && r.EfficiencyExclude.Contains("spotify"), "shell and players excluded");
        }

        static void ActionLogRoundTrip()
        {
            string dir = Path.Combine(Path.GetTempPath(), "bc_tests", "actions");
            if (Directory.Exists(dir)) foreach (var f in Directory.GetFiles(dir)) File.Delete(f);
            ActionLog.Append(dir, "brightness", "80 %", "50 %", ProfileLogic.ByProfile);
            ActionLog.Append(dir, "power_mode_dc", "Balanced, x", "Best power efficiency", ProfileLogic.ByUser);
            var list = ActionLog.Load(dir, 5);
            Equal(list.Count, 2, "count");
            Equal(list[0].Action, "power_mode_dc", "newest first");
            Equal(list[0].From, "Balanced; x", "comma replaced");
            Equal(list[1].To, "50 %", "value");
        }

        static void RulesBuiltIn()
        {
            var r = Rules.BuiltIn;
            Check(r.Categories.Count >= 8, "categories");
            Equal(r.CategoryOf("chrome").Id, "browser", "chrome");
            Equal(r.CategoryOf("Telegram").Id, "messenger", "case-insensitive");
            Equal(r.CategoryOf("PredatorSense").Id, "vendor", "vendor utility from the vendor list");
            Equal(r.CategoryOf("AcerSomethingNew").Id, "vendor", "vendor prefix");
            Equal(r.CategoryOf("HWiNFO64").Id, "monitor", "hardware monitor");
            Check(r.CategoryOf("myownapp") == null, "unknown app");
            Check(r.GpuPollers.Contains("predatorsense"), "gpu pollers, case-insensitive");
            Equal(r.CategoryOf("chrome").Name.ToString(), "browser", "en name");
            L.Apply(true);
            Equal(r.CategoryOf("chrome").Name.ToString(), "браузер", "uk name");
            Check(r.CategoryOf("steam").Advice.ToString().StartsWith("при роботі"), "uk advice");
        }

        static void RulesVendor()
        {
            var r = Rules.BuiltIn;
            Equal(r.VendorFor("Acer").Id, "acer", "Acer");
            Equal(r.VendorFor("LENOVO").Id, "lenovo", "LENOVO");
            Equal(r.VendorFor("ASUSTeK COMPUTER INC.").Id, "asus", "ASUSTeK");
            Equal(r.VendorFor("Micro-Star International Co., Ltd.").Id, "msi", "MSI");
            Equal(r.VendorFor("HP").Id, "hp", "HP");
            Check(r.VendorFor("Shenzhen Something") == null, "unknown vendor");
            Check(r.VendorFor(null) == null, "null");
            Check(r.VendorFor("Acer").ModeHint.ToString().Contains("PredatorSense"), "mode hint");
        }

        static void RulesUserFile()
        {
            string dir = Path.Combine(Path.GetTempPath(), "bc_tests", "rules");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, Rules.UserFileName);
            File.WriteAllText(path, @"{
  ""categories"": [
    { ""id"": ""browser"", ""name"": { ""en"": ""web browser"" }, ""typical_bg_w"": 2.0, ""processes"": [ ""chrome"" ] },
    { ""id"": ""ide"", ""name"": ""IDE"", ""typical_bg_w"": 1.2, ""processes"": [ ""devenv"", ""rider64"" ] }
  ],
  ""gpu_pollers"": [ ""MyPoller"" ]
}", new UTF8Encoding(false));
            var r = Rules.Load(dir);
            Equal(r.UserFile, path, "user file read");
            Near(r.CategoryOf("chrome").TypicalBgW, 2.0, 1e-9, "replaced typical");
            Check(r.CategoryOf("firefox") == null, "replaced category keeps only its own processes");
            Equal(r.CategoryOf("devenv").Name.ToString(), "IDE", "added category, plain-string name");
            Check(r.GpuPollers.Contains("MyPoller") && r.GpuPollers.Contains("PredatorSense"), "pollers merged");
            Equal(r.CategoryOf("telegram").Id, "messenger", "untouched built-in");

            File.WriteAllText(path, "{ not json", new UTF8Encoding(false));
            var broken = Rules.Load(dir);
            Check(broken.UserError != null, "error reported");
            Equal(broken.CategoryOf("chrome").Id, "browser", "built-in still works");
            File.Delete(path);
        }

        static void OwnNorm()
        {
            var h = new ProcessHistory();
            var d0 = new DateTime(2026, 9, 1);
            // 5 прошлых дней: по 1 ч от батареи, программа — 0,4 Вт в фоне (один день — 0,6).
            for (int i = 0; i < 5; i++)
            {
                h.AddBattery(d0.AddDays(i).AddHours(10), 3600);
                h.AddProcess(d0.AddDays(i).AddHours(10), "app", i == 2 ? 0.6 : 0.4);
            }
            // Эта неделя: 2 дня по 2 ч, 1,2 Вт.
            var week = d0.AddDays(10);
            for (int i = 0; i < 2; i++)
            {
                h.AddBattery(week.AddDays(i).AddHours(9), 7200);
                h.AddProcess(week.AddDays(i).AddHours(9), "app", 2.4);
            }
            int days;
            Near(h.Norm("app", week, out days), 0.4, 1e-9, "norm");
            Equal(days, 5, "days");
            Near(h.Current("app", week), 1.2, 1e-9, "current");
            Check(ProcessHistory.AboveNorm(1.2, 0.4), "above");
            Check(!ProcessHistory.AboveNorm(0.55, 0.4), "1.4× is not above");
            Check(!ProcessHistory.AboveNorm(0.2, 0.1), "tiny absolute change is not above");
            Check(double.IsNaN(h.Norm("other", week, out days)), "unknown app");
        }

        static void QuietProcessesFlow()
        {
            var b = new LogBuilder(false);
            b.Idle = 600; b.Discharge(600, 1, 20, 1);    // 10 тихих минут, 20 Вт
            b.Idle = 5; b.Discharge(300, 1, 30, 1);      // 5 минут работы пользователя, 30 Вт
            string dir = b.Save("quiet");
            var sb = new StringBuilder("minute,process,wh,fg_wh,gpu_wh,seconds\n");
            var inv = CultureInfo.InvariantCulture;
            for (int m = 0; m < 15; m++)
            {
                string min = new DateTime(2026, 9, 1, 10, m, 0).ToString("yyyy-MM-ddTHH:mm", inv);
                bool away = m < 10;
                sb.AppendLine(string.Join(",", min, ProcessEnergyLog.BatteryRow, ((away ? 20.0 : 30.0) / 60).ToString("0.######", inv), "0", "0", "60"));
                sb.AppendLine(string.Join(",", min, "Telegram", (1.0 / 60).ToString("0.######", inv), "0", "0", "60"));
                if (!away) sb.AppendLine(string.Join(",", min, "Figma", (8.0 / 60).ToString("0.######", inv), (8.0 / 60).ToString("0.######", inv), "0", "60"));
            }
            File.WriteAllText(Path.Combine(dir, "process_energy_20260901.csv"), sb.ToString(), new UTF8Encoding(false));
            var q = QuietProcesses.Compute(dir, 3650, new DateTime(2026, 9, 2));
            Near(q.Minutes, 10, 1, "quiet minutes");
            Near(q.BatteryW, 20, 0.1, "battery in quiet minutes");
            Equal(q.Top.Count > 0 ? q.Top[0].Key : "", "Telegram", "top");
            Near(q.Top[0].Value, 1, 0.05, "Telegram W");
            Check(!q.Top.Exists(kv => kv.Key == "Figma"), "Figma only while the user worked");
        }

        static void OwnNormFewDays()
        {
            var h = new ProcessHistory();
            var d0 = new DateTime(2026, 9, 1);
            for (int i = 0; i < 2; i++)
            {
                h.AddBattery(d0.AddDays(i), 3600);
                h.AddProcess(d0.AddDays(i), "app", 0.4);
            }
            h.AddBattery(d0.AddDays(2), 300);                   // день с 5 минутами от батареи не считается
            h.AddProcess(d0.AddDays(2), "app", 0.1);
            int days;
            Check(double.IsNaN(h.Norm("app", d0.AddDays(10), out days)), "no norm");
            Equal(days, 2, "two valid days");
        }

        static Snapshot Snap(DateTime t, double w, double soc, int dstate = 3, double idle = 600, int displaysOnGpu = 0, bool ac = false)
        {
            var s = S(t, w, 5, dstate, idle);
            s.DisplaysOnGpu = displaysOnGpu;
            if (ac) s.Battery = new BatteryStatus { PowerState = 1, Capacity = 50000, Voltage = 16000, Rate = 0 };
            return new Snapshot { Sample = s, SocPct = soc, BatteryAvgW = w, HoursLeft = soc / 100 * 76 / w, Cpu10W = 9, Gpu10W = dstate == 3 ? 0 : 6 };
        }

        /// <summary>Замеры раз в 10 с; возвращает показанные уведомления.</summary>
        static List<Notice> Run(Notifier n, Baseline b, DateTime from, int seconds, Func<int, Snapshot> snap)
        {
            var shown = new List<Notice>();
            for (int i = 0; i < seconds; i += 10)
            {
                var x = n.Check(snap(i), b, from.AddSeconds(i), false);
                if (x != null) shown.Add(x);
            }
            return shown;
        }

        static void NotifyLow()
        {
            var n = new Notifier();
            var shown = Run(n, null, T0, 600, i => Snap(T0.AddSeconds(i), 15, 25 - i / 60.0));     // 25 → 15 %
            Equal(shown.Count, 1, "one notice at 20 %");
            Equal(shown[0].Kind, "low", "kind");
            shown = Run(n, null, T0.AddMinutes(10), 600, i => Snap(T0.AddMinutes(10).AddSeconds(i), 15, 15 - i / 60.0));  // 15 → 5 %
            Equal(shown.Count, 1, "one notice at 10 %");
            var late = new Notifier();
            Equal(Run(late, null, T0, 120, i => Snap(T0.AddSeconds(i), 15, 8)).Count, 1, "started at 8 % — one notice, not two");
        }

        static void NotifyDrain()
        {
            var b = new Baseline { QuietMinutes = 30, TotalW = 10, CpuPkgW = 3 };
            var n = new Notifier();
            var shown = Run(n, b, T0, 12 * 60, i => Snap(T0.AddSeconds(i), 20, 80));
            Equal(shown.Count, 1, "drain once");
            Equal(shown[0].Kind, "drain", "kind");
            Check(shown[0].Text.Contains("CPU"), "culprit: " + shown[0].Text);
            Equal(Run(n, b, T0.AddMinutes(12), 15 * 60, i => Snap(T0.AddMinutes(12).AddSeconds(i), 20, 79)).Count, 0, "not again within 30 min");

            var busy = new Notifier();
            Equal(Run(busy, b, T0, 12 * 60, i => Snap(T0.AddSeconds(i), 20, 80, 3, i % 300 == 0 ? 5 : 600)).Count, 0, "user input breaks the quiet stretch");
            var normal = new Notifier();
            Equal(Run(normal, b, T0, 12 * 60, i => Snap(T0.AddSeconds(i), 13, 80)).Count, 0, "1.3× idle is fine");
        }

        static void NotifyGpu()
        {
            var wake = new GpuWake { Start = T0 };
            var n = new Notifier();
            var shown = Run(n, null, T0, 6 * 60, i => { var s = Snap(T0.AddSeconds(i), 15, 80, 0); s.GpuWakes = new List<GpuWake> { wake }; return s; });
            Equal(shown.Count, 1, "gpu notice");
            Equal(shown[0].Kind, "gpu", "kind");
            Check(shown[0].Text.Contains("NVIDIA wake-ups"), "unknown cause text: " + shown[0].Text);

            var game = new GpuWake { Start = T0 };
            game.Busy["game"] = 85;
            var g = new Notifier();
            Equal(Run(g, null, T0, 6 * 60, i => { var s = Snap(T0.AddSeconds(i), 40, 80, 0); s.GpuWakes = new List<GpuWake> { game }; return s; }).Count, 0, "busy GPU");
        }

        static void NotifyGpuHolder()
        {
            var n = new Notifier();
            var shown = Run(n, null, T0, 6 * 60, i =>
            {
                var s = Snap(T0.AddSeconds(i), 25, 80, 0);
                s.GpuWakes = new List<GpuWake> { new GpuWake { Start = T0 } };
                s.Processes = new List<ProcessPower>
                {
                    new ProcessPower { Name = "Figma", PowerW = 6, DgpuMemMB = 197 },
                    new ProcessPower { Name = "dwm", PowerW = 1, DgpuMemMB = 45 },
                };
                return s;
            });
            Equal(shown.Count, 1, "gpu notice");
            Check(shown[0].Text.Contains("Figma") && shown[0].Text.Contains("197"), "names the holder: " + shown[0].Text);
        }

        static void NotifyGpuDisabled()
        {
            var n = new Notifier();
            var shown = Run(n, null, T0, 120, i => { var s = Snap(T0.AddSeconds(i), 35, 80, -1); s.Sample.GpuDisabled = true; return s; });
            Equal(shown.Count, 1, "once");
            Equal(shown[0].Kind, "gpu_disabled", "kind");
        }

        static void NotifyMonitor()
        {
            var n = new Notifier();
            Equal(Run(n, null, T0, 120, i => Snap(T0.AddSeconds(i), 25, 80, 0, 600, 1)).Count, 1, "once");
            Run(n, null, T0.AddMinutes(2), 60, i => Snap(T0.AddMinutes(2).AddSeconds(i), 0, 80, 0, 600, 1, true));  // зарядка
            Equal(Run(n, null, T0.AddMinutes(3), 120, i => Snap(T0.AddMinutes(3).AddSeconds(i), 25, 80, 0, 600, 1)).Count, 1, "again after replugging");
        }

        static void WakeLogMigration()
        {
            string dir = Path.Combine(Path.GetTempPath(), "bc_tests", "wakes");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "gpu_wakes.csv");
            File.WriteAllLines(path, new[]
            {
                "start,end,seconds,cost_wh,cause",
                "2026-10-05T00:28:10,2026-10-05T00:28:22,11,0.0525,\"неизвестно (видеокарту опросили, но работы не было)\"",
                "2026-10-05T00:30:00,2026-10-05T00:30:12,12,,\"замер экрана: смена яркости\"",
                "2026-10-05T00:31:00,2026-10-05T00:31:12,12,0.0400,\"работа: steam 12 %, dwm 3 %\"",
            }, new UTF8Encoding(false));
            GpuWakeLog.Migrate(path);
            var lines = File.ReadAllLines(path);
            Equal(lines[0], GpuWakeLog.Header, "header");
            Equal(lines[1].Split(',')[4], "unknown", "unknown flag");
            Equal(lines[2].Split(',')[4], "self", "self flag");
            Equal(lines[3].Split(',')[4], "", "no flag");
            Check(lines[3].EndsWith("\"работа: steam 12 %, dwm 3 %\""), "cause with commas kept");
            GpuWakeLog.Migrate(path);
            Equal(File.ReadAllLines(path).Length, 4, "second migration is a no-op");
        }
    }
}
