using System;
using System.Collections.Generic;

namespace BatteryCheck
{
    sealed class Notice
    {
        public string Kind, Title, Text;
    }

    /// <summary>
    /// Уведомления по делу, только от батареи; каждый тип — не чаще раза в RepeatMinutes:
    /// <list type="bullet">
    /// <item>drain — 10 минут без ввода, а расход больше 1,5 простоя этой машины; виновник — видеокарта, программа или процессор;</item>
    /// <item>gpu — дискретная видеокарта не спит 5 минут без серьёзной нагрузки (игра — не сюрприз);</item>
    /// <item>monitor — внешний монитор на дискретной видеокарте (раз за разряд);</item>
    /// <item>low — заряд 20 % и 10 % с оставшимся временем (по разу за разряд).</item>
    /// </list>
    /// </summary>
    sealed class Notifier
    {
        public const double RepeatMinutes = 30, DrainMinutes = 10, DrainFactor = 1.5, GpuAwakeMinutes = 5, BusyGpuPct = 30;
        static readonly int[] LowThresholds = { 20, 10 };

        sealed class Minute
        {
            public DateTime T;
            public int N, AwakeN;
            public double SumW;
            public bool AllDischarging = true, Input;
        }

        readonly List<Minute> minutes = new List<Minute>();
        readonly Dictionary<string, DateTime> lastShown = new Dictionary<string, DateTime>();
        readonly HashSet<int> lowShown = new HashSet<int>();
        DateTime? gpuAwakeSince;
        bool monitorShown;

        /// <param name="suppressed">Не уведомлять сейчас (идёт замер экрана); учёт продолжается.</param>
        public Notice Check(Snapshot snap, Baseline baseline, DateTime now, bool suppressed)
        {
            var s = snap.Sample;
            var b = s.Battery;
            Record(s);
            if (!b.Discharging)
            {
                if (b.OnLine) { lowShown.Clear(); monitorShown = false; }
                gpuAwakeSince = null;
                return null;
            }
            bool awake = s.GpuDState >= 0 && s.GpuDState != 3;
            if (!awake) gpuAwakeSince = null;
            else if (gpuAwakeSince == null) gpuAwakeSince = now;
            if (suppressed) return null;

            // Заряд: порог пересечён — по разу за разряд.
            foreach (int p in LowThresholds)
                if (snap.SocPct <= p && !lowShown.Contains(p))
                {
                    foreach (int q in LowThresholds) if (snap.SocPct <= q) lowShown.Add(q);  // запустились уже на 8 % — одно уведомление, не два
                    return new Notice
                    {
                        Kind = "low",
                        Title = string.Format(L.T("Battery {0:0} %", "Батарея {0:0} %"), snap.SocPct),
                        Text = string.Format(L.T("At the current draw ({0}) ≈ {1} left.", "За поточної витрати ({0}) залишилося ≈ {1}."),
                            Fmt.W(snap.BatteryAvgW), Fmt.Hours(snap.HoursLeft)),
                    };
                }

            if (s.GpuDisabled && CanShow("gpu_disabled", now))
                return Show("gpu_disabled", now, L.T("Discrete GPU is disabled", "Дискретну відеокарту вимкнено"),
                    L.T("Without its driver nothing puts it to sleep, and it may draw more than a sleeping one. Enable it in Device Manager → Display adapters.",
                        "Без драйвера її нікому приспати, і вона може споживати більше, ніж спляча. Увімкніть її в диспетчері пристроїв → Відеоадаптери."));

            if (s.DisplaysOnGpu > 0 && !monitorShown)
            {
                monitorShown = true;
                return Show("monitor", now, L.T("External monitor keeps the GPU awake", "Зовнішній монітор не дає відеокарті заснути"),
                    L.T("It is connected to the discrete GPU, which costs about 5–10 W on battery. Disconnect it or use a port on the integrated graphics.",
                        "Його підключено до дискретної відеокарти, від батареї це близько 5–10 Вт. Відключіть його або підключіть до порту вбудованої графіки."));
            }

            if (gpuAwakeSince != null && (now - gpuAwakeSince.Value).TotalMinutes >= GpuAwakeMinutes && s.DisplaysOnGpu <= 0)
            {
                var wake = ActiveWake(snap);
                if (MaxBusy(wake) < BusyGpuPct && CanShow("gpu", now))
                    return Show("gpu", now, L.T("Discrete GPU is not sleeping", "Дискретна відеокарта не спить"),
                        string.Format(L.T("Awake for {0:0} min on battery ({1} now). {2}", "Не спить {0:0} хв від батареї (зараз {1}). {2}"),
                            (now - gpuAwakeSince.Value).TotalMinutes, Fmt.W(snap.Gpu10W), Holders(snap) ?? Culprit(wake)));
            }

            if (baseline != null && baseline.Valid && CanShow("drain", now))
            {
                double w, awakeShare;
                if (QuietDrain(now, out w, out awakeShare) && w > baseline.TotalW * DrainFactor)
                {
                    string why;
                    if (awakeShare > 0.5) why = L.T("the discrete GPU is awake. ", "дискретна відеокарта не спить. ") + (Holders(snap) ?? Culprit(ActiveWake(snap)));
                    else
                    {
                        var top = TopProcess(snap);
                        why = top != null
                            ? string.Format(L.T("{0} ({1}).", "{0} ({1})."), top.Name, Fmt.W(top.PowerW))
                            : string.Format(L.T("the CPU: {0} vs {1} at idle.", "процесор: {0} проти {1} у простої."), Fmt.W(snap.Cpu10W), Fmt.W(baseline.CpuPkgW));
                    }
                    return Show("drain", now, L.T("Battery is draining faster than usual", "Батарея розряджається швидше, ніж зазвичай"),
                        string.Format(L.T("{0} for {1:0} min while you were away — idle of this laptop is {2}. Likely: {3}",
                                          "{0} протягом {1:0} хв, поки ви не працювали, — простій цього ноутбука {2}. Ймовірно: {3}"),
                            Fmt.W(w), DrainMinutes, Fmt.W(baseline.TotalW), why));
                }
            }
            return null;
        }

        void Record(Sample s)
        {
            var t = new DateTime(s.Time.Year, s.Time.Month, s.Time.Day, s.Time.Hour, s.Time.Minute, 0);
            var m = minutes.Count > 0 ? minutes[minutes.Count - 1] : null;
            if (m == null || m.T != t)
            {
                m = new Minute { T = t };
                minutes.Add(m);
                while (minutes.Count > DrainMinutes + 2) minutes.RemoveAt(0);
            }
            double w = s.DischargeW;
            if (double.IsNaN(w)) { m.AllDischarging = false; return; }
            m.N++;
            m.SumW += w;
            if (s.GpuDState >= 0 && s.GpuDState != 3) m.AwakeN++;
            if (!double.IsNaN(s.IdleS) && s.IdleS < 60) m.Input = true;
        }

        /// <summary>Последние DrainMinutes законченных минут подряд — от батареи и без ввода; средний расход и доля времени без сна видеокарты.</summary>
        bool QuietDrain(DateTime now, out double w, out double awakeShare)
        {
            w = awakeShare = 0;
            int need = (int)DrainMinutes, n = 0, samples = 0, awake = 0;
            double sum = 0;
            DateTime expect = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0).AddMinutes(-1);
            for (int i = minutes.Count - 1; i >= 0 && n < need; i--)
            {
                var m = minutes[i];
                if (m.T > expect) continue;            // текущая, незаконченная минута
                if (m.T != expect || !m.AllDischarging || m.Input || m.N < 2) return false;
                sum += m.SumW; samples += m.N; awake += m.AwakeN;
                n++;
                expect = expect.AddMinutes(-1);
            }
            if (n < need) return false;
            w = sum / samples;
            awakeShare = (double)awake / samples;
            return true;
        }

        static GpuWake ActiveWake(Snapshot snap)
        {
            if (snap.GpuWakes == null) return null;
            for (int i = snap.GpuWakes.Count - 1; i >= 0; i--)
                if (snap.GpuWakes[i].Active) return snap.GpuWakes[i];
            return null;
        }

        static double MaxBusy(GpuWake w)
        {
            double max = 0;
            if (w != null) foreach (var kv in w.Busy) max = Math.Max(max, kv.Value);
            return max;
        }

        static string Culprit(GpuWake w)
        {
            if (w == null || w.Unknown)
                return L.T("No app is loading it: probably a monitoring utility polls it — see “NVIDIA wake-ups”.",
                           "Ніщо її не навантажує: ймовірно, її опитує утиліта-монітор — див. «Пробудження NVIDIA».");
            return L.T("Cause: ", "Причина: ") + w.Cause + ".";
        }

        /// <summary>Кто держит видеокарту без нагрузки (видеопамять на ней), или null.</summary>
        static string Holders(Snapshot snap)
        {
            if (snap.Processes == null) return null;
            ProcessPower top = null;
            foreach (var p in snap.Processes)
                if (p.DgpuMemMB >= 16 && p.GpuPct <= 0 && p.Name != "dwm" && (top == null || p.DgpuMemMB > top.DgpuMemMB)) top = p;
            if (top == null) return null;
            return string.Format(L.T("{0} keeps it awake ({1:0} MB of video memory, no load). Quit it fully — closing the window may leave it running in the tray — or set it to Power saving in Settings → Display → Graphics.",
                                     "Її тримає {0} ({1:0} МБ відеопам'яті, без навантаження). Закрийте програму повністю — після закриття вікна вона може лишатися в треї — або виберіть для неї «Енергозбереження» в Параметрах → Дисплей → Графіка."),
                top.Name, top.DgpuMemMB);
        }

        static ProcessPower TopProcess(Snapshot snap)
        {
            if (snap.Processes == null) return null;
            foreach (var p in snap.Processes)
                if (p.PowerW >= 0.5) return p;  // список уже по убыванию
            return null;
        }

        bool CanShow(string kind, DateTime now)
        {
            DateTime t;
            return !lastShown.TryGetValue(kind, out t) || (now - t).TotalMinutes >= RepeatMinutes;
        }

        Notice Show(string kind, DateTime now, string title, string text)
        {
            lastShown[kind] = now;
            return new Notice { Kind = kind, Title = title, Text = text };
        }
    }
}
