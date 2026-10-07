using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows;
using System.Windows.Controls;

namespace BatteryCheck
{
    /// <summary>
    /// Экран OLED: мощность зависит не от «подсветки» (её нет), а от того, что показано, — чёрный пиксель почти
    /// ничего не стоит. Поэтому замер по кнопке (при работе от батареи) показывает на весь встроенный экран
    /// чёрное изображение, затем выключает экран, затем белое при текущей яркости и белое при 100 %. Результат —
    /// сколько стоит включённая панель с чёрным изображением (сверх выключенной) и белый экран сверх чёрного при данной
    /// яркости. Текущая оценка = панель + белый × «доля белого» на экране (по снимку раз в 5 с).
    ///
    /// Помехи, найденные на практике: смена яркости будит NVIDIA, а её засыпание ещё ~6 с стоит +15 Вт
    /// (сохранение видеопамяти). Поэтому фаза ждёт, пока видеокарта проспит QuietSeconds, и ещё SettleSeconds
    /// после смены изображения. Каждый ватт процессора добавляет к «остальному» ~0,75 Вт — замеры с отклонением
    /// процессора больше CpuTolerance отбрасываются. Свой опрос NVIDIA и сбор по процессам на время замера выключены.
    /// Прежняя яркость записывается заранее и восстанавливается даже после сбоя.
    /// </summary>
    sealed partial class MainWindow
    {
        const double QuietSeconds = 10, SettleSeconds = 4, PhaseTimeoutSeconds = 120, CpuTolerance = 1.5;
        const double OffWaitSeconds = 8;  // Windows не выключила экран за это время — фаза пропускается
        const int SamplesPerPhase = 30, MinKeptSamples = 22;  // 30 с на фазу: медиана отсеивает всплески фоновых процессов
        const int PhaseBlack = 0, PhaseOff = 1, PhaseWhite = 2, PhaseWhite100 = 3;
        static string[] PhaseNames
        {
            get
            {
                return new[]
                {
                    L.T("black screen", "чорний екран"), L.T("screen off", "екран вимкнено"),
                    L.T("white screen", "білий екран"), L.T("white, 100 % brightness", "білий, яскравість 100 %")
                };
            }
        }

        static string MeasureText { get { return L.T("Measure screen", "Виміряти екран"); } }
        static string CancelledText { get { return L.T("cancelled", "скасовано"); } }

        FlatButton screenButton;
        TextBlock[] screenRow;
        ScreenModel screenModel = ScreenModel.Load();
        volatile int brightness = -1;
        double luma = double.NaN;  // доля белого на встроенном экране, 0…1
        DateTime brightnessReadAt = DateTime.MinValue;
        int brightnessReading;

        struct CalSample
        {
            public double Rest, Cpu;
        }

        int calPhase = -1;  // -1 — замера нет; иначе Phase*
        int calB0;
        bool calGpuPollingWas;
        bool calGpuHeld;  // NVIDIA держит внешний экран: ждать её сна бессмысленно, её мощность измеряется и вычитается
        DateTime calPhaseStart;
        DateTime calOffSince;  // с какого момента Windows сообщает, что экран выключен (фаза PhaseOff)
        bool calSkipOff;       // Modern Standby: выключение экрана усыпит всю систему — фаза PhaseOff пропускается
        DateTime gpuAwakeAt = DateTime.UtcNow;  // когда NVIDIA последний раз была не в D3 (обновляется в UpdateWakes)
        List<CalSample> calSamples;
        readonly double[] calResult = new double[4], calNoise = new double[4];
        TestPatternForm pattern;

        /// <summary>Если прошлый запуск прервался посреди замера, вернуть яркость, которая была до него.</summary>
        static void RestorePendingBrightness()
        {
            double pending = Settings.GetDouble("PendingBrightness", double.NaN);
            if (double.IsNaN(pending)) return;
            Brightness.Set((int)pending);
            Settings.SetDouble("PendingBrightness", double.NaN);
        }

        FlatButton BuildScreenButton()
        {
            screenButton = new FlatButton(MeasureText);
            screenButton.Click += () =>
            {
                if (calPhase >= 0) AbortScreenCalibration(CancelledText);
                else StartScreenCalibration();
            };
            UpdateScreenButton();
            return screenButton;
        }

        void UpdateScreenButton()
        {
            if (calPhase >= 0) return;
            screenButton.Text = screenModel == null ? MeasureText : L.T("Re-measure screen", "Переміряти екран");
            screenButton.ToolTip = L.T(
                DisplayState.ModernStandby
                    ? "On battery only, 2–3 minutes. Fullscreen: a black image, a white one,\n" +
                      "white at 100 % brightness — then everything is restored. Best not to touch the laptop meanwhile.\n" +
                      "Esc or a click on the screen cancels the measurement."
                    : "On battery only, 2–3 minutes. Fullscreen: a black image, the screen switched off for ~20 s, a white image,\n" +
                      "white at 100 % brightness — then everything is restored. Do not touch the laptop meanwhile:\n" +
                      "moving the mouse turns the screen back on. Esc or a click on the screen cancels the measurement.",
                DisplayState.ModernStandby
                    ? "Лише від батареї, 2–3 хвилини. На весь екран: чорне зображення, біле,\n" +
                      "біле при яскравості 100 % — потім усе повертається. Краще нічого не робити за ноутбуком.\n" +
                      "Esc або клік по екрану скасовують вимірювання."
                    : "Лише від батареї, 2–3 хвилини. На весь екран: чорне зображення, екран вимкнено на ~20 с, біле зображення,\n" +
                      "біле при яскравості 100 % — потім усе повертається. Нічого не робіть за ноутбуком:\n" +
                      "рух миші знову вмикає екран. Esc або клік по екрану скасовують вимірювання.");
        }

        /// <summary>Яркость и доля белого на экране — в фоне не чаще раза в 5 с (WMI и снимок — миллисекунды).</summary>
        void RefreshScreenState()
        {
            if (calPhase >= 0) return;  // во время замера экраном управляем сами
            if ((DateTime.UtcNow - brightnessReadAt).TotalSeconds < 5 || Interlocked.Exchange(ref brightnessReading, 1) == 1) return;
            brightnessReadAt = DateTime.UtcNow;
            // Замер не загрузился при запуске (05.10 после перезагрузки — разовый сбой чтения) — пробовать снова.
            if (screenModel == null)
            {
                screenModel = ScreenModel.Load();
                if (screenModel != null) UpdateScreenButton();
            }
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    int b = Brightness.Get();
                    if (brightness >= 0 && b >= 0 && b != brightness) sampler.WakeHint(GpuWake.HintBrightness);
                    brightness = b;
                    if (screenModel != null)
                    {
                        luma = ScreenLuma.Measure(ScreenLuma.InternalScreen().Bounds);
                        double w = ScreenContentW();
                        if (!double.IsNaN(w)) sampler.SetScreenEstimate(w, b);  // в лог — для эталона простоя
                    }
                }
                finally
                {
                    Interlocked.Exchange(ref brightnessReading, 0);
                }
            });
        }

        /// <summary>Оценка мощности экрана сейчас: белый экран при текущей яркости × доля белого; NaN — не измерено.</summary>
        double ScreenW()
        {
            double content = ScreenContentW();
            return double.IsNaN(content) || double.IsNaN(screenModel.PanelW) ? content : screenModel.PanelW + content;
        }

        /// <summary>
        /// Только изображение (белый × доля белого), без панели. В лог и эталон простоя — так же, как в логах до замера
        /// панели: иначе сравнение с эталоном по старым логам показало бы панель как «лишние» ватты экрана.
        /// </summary>
        double ScreenContentW()
        {
            if (screenModel == null || double.IsNaN(luma)) return double.NaN;
            return screenModel.At(brightness) * luma;
        }

        void UpdateScreenRow(Snapshot snap)
        {
            RefreshScreenState();
            if (calPhase >= 0)
            {
                screenRow[1].Text = "…";
                screenRow[2].Text = "";
                screenRow[3].Text = L.T("measuring", "вимірювання");
            }
            else if (screenModel == null)
            {
                screenRow[1].Text = "—";
                screenRow[2].Text = "";
                screenRow[3].Text = L.T("not measured", "не виміряно");
            }
            else
            {
                screenRow[1].Text = Fmt.W(ScreenW());
                screenRow[2].Text = "";
                // Коротко: колонка примечаний общая для всей таблицы и иначе отнимает место у названий.
                screenRow[3].Text = double.IsNaN(luma) ? "" : string.Format(L.T("{0:0} % white", "{0:0} % білого"), luma * 100);
                screenRow[3].ToolTip = string.Format(L.T("White share on screen {0:0.0} %, brightness {1} %", "Частка білого на екрані {0:0.0} %, яскравість {1} %"), luma * 100, brightness);
            }
            var pts = new List<string>();
            if (screenModel != null)
                foreach (var kv in screenModel.Points)
                    if (kv.Key > 0) pts.Add(string.Format("{0} % — {1:0.0} {2}", kv.Key, kv.Value, Fmt.WUnit));
            screenRow[0].ToolTip = screenModel == null
                ? L.T("OLED screen: power depends on what is shown. Press “Measure screen” while on battery.",
                      "Екран OLED: потужність залежить від того, що показано. Натисніть «Виміряти екран» при роботі від батареї.")
                : string.Format(L.T(
                      "A fully white screen over a black one (measured {0}): {1}.\n" +
                      "Now = panel + that value at the current brightness × white share on screen (a 128×80 snapshot every 5 s, not saved).\n" +
                      "{2}\nOLED limits brightness on large white areas, so the estimate is approximate.",
                      "Білий екран на всю площу понад чорний (вимір {0}): {1}.\n" +
                      "Зараз = панель + це значення при поточній яскравості × частка білого на екрані (знімок 128×80 раз на 5 с, не зберігається).\n" +
                      "{2}\nНа великій білій площі OLED сам обмежує яскравість, тому оцінка приблизна."),
                  screenModel.MeasuredAt.ToString("dd.MM HH:mm"), string.Join(", ", pts),
                  double.IsNaN(screenModel.PanelW)
                      ? (DisplayState.ModernStandby
                          ? L.T("The panel itself (black image vs screen off) cannot be measured on this laptop: switching the screen off puts it to sleep (Modern Standby). It stays in “Other”.",
                                "Саму панель (чорне зображення проти вимкненого екрана) на цьому ноутбуці не виміряти: вимкнення екрана присипляє його (Modern Standby). Вона лишається в «Іншому».")
                          : L.T("The panel itself (black image vs screen off) is not measured — it stays in “Other”. Re-measure the screen to split it out.",
                                "Саму панель (чорне зображення проти вимкненого екрана) не виміряно — вона лишається в «Іншому». Перевиміряйте екран, щоб її виділити."))
                      : string.Format(L.T("The panel itself (black image vs screen off): {0:0.0} W.", "Сама панель (чорне зображення проти вимкненого екрана): {0:0.0} Вт."), screenModel.PanelW));
        }

        void StartScreenCalibration()
        {
            if (lastSnap == null || !lastSnap.Sample.Battery.Discharging)
            {
                ShowNote(L.T("The screen can be measured only on battery: total draw is not measured on AC.", "Вимірювання екрана можливе лише від батареї: від мережі загальне споживання не вимірюється."));
                return;
            }
            int b0 = Brightness.Get();
            if (b0 < 0)
            {
                ShowNote(L.T("This screen's brightness is not controlled through Windows — it cannot be measured.", "Яскравість цього екрана не керується через Windows — вимірювання неможливе."));
                return;
            }
            Settings.SetDouble("PendingBrightness", b0);  // восстановится при следующем запуске, если программа упадёт
            calB0 = b0;
            calSkipOff = DisplayState.ModernStandby;
            calResult[PhaseOff] = calNoise[PhaseOff] = double.NaN;
            calGpuPollingWas = sampler.GpuPolling;
            // Свой опрос NVIDIA не должен держать её включённой. Но если её и так держит внешний экран, она не уснёт —
            // тогда, наоборот, опрашивать и вычитать её мощность (05.10: без этого замер ждал сна видеокарты до тайм-аута).
            calGpuHeld = lastSnap.Sample.DisplaysOnGpu > 0;
            sampler.GpuPolling = calGpuHeld;

            pattern = new TestPatternForm(ScreenLuma.InternalScreen().Bounds);
            pattern.Cancelled += () => AbortScreenCalibration(CancelledText);
            pattern.Show();
            BeginPhase(PhaseBlack);
            SyncProcessCollection();     // сбор по процессам — тоже пауза
        }

        void BeginPhase(int phase)
        {
            if (calPhase == PhaseOff && phase != PhaseOff) ScreenOn();
            calPhase = phase;
            calPhaseStart = DateTime.UtcNow;
            calOffSince = DateTime.MinValue;
            calSamples = new List<CalSample>();
            calResult[phase] = calNoise[phase] = double.NaN;
            UpdatePatternText(0);
            if (phase == PhaseOff)
            {
                sampler.WakeHint(GpuWake.HintDisplay);  // внешний экран на NVIDIA тоже гаснет — её пробуждение не в счёт
                ScreenPower.Off(pattern.Handle);
            }
            if (phase == PhaseWhite100)
            {
                sampler.WakeHint(GpuWake.HintCalibration);
                Brightness.Set(100);
            }
        }

        void ScreenOn()
        {
            sampler.WakeHint(GpuWake.HintDisplay);
            ScreenPower.On(pattern != null ? pattern.Handle : IntPtr.Zero);
        }

        void UpdatePatternText(double waitLeft)
        {
            if (pattern == null) return;
            pattern.SetPattern(calPhase >= PhaseWhite, string.Format(L.T("Screen measurement · {0} · {1} · Esc to cancel", "Вимірювання екрана · {0} · {1} · Esc — скасувати"),
                PhaseNames[calPhase], waitLeft > 0 ? L.T("waiting for the GPU to sleep", "чекаємо, поки засне відеокарта") : calSamples.Count + "/" + SamplesPerPhase));
        }

        /// <summary>Каждый замер, пока идёт калибровка. Вызывается и когда окно не на экране — чтобы прервать.</summary>
        void OnCalibrationSample(Snapshot snap)
        {
            if (calPhase < 0) return;
            var x = snap.Sample;
            if (!IsOnScreen) { AbortScreenCalibration(L.T("window hidden", "вікно приховано")); return; }
            if (!x.Battery.Discharging) { AbortScreenCalibration(L.T("charger connected", "підключено зарядку")); return; }

            // Тишина: видеокарта спит не меньше QuietSeconds (её засыпание ещё ~6 с стоит +15 Вт).
            double quietFor = (DateTime.UtcNow - gpuAwakeAt).TotalSeconds;
            double elapsed = (DateTime.UtcNow - calPhaseStart).TotalSeconds;
            bool gpuOk = quietFor >= QuietSeconds || (calGpuHeld && x.GpuDState == 0 && !double.IsNaN(x.GpuW));
            bool ready = gpuOk && elapsed >= SettleSeconds;

            if (calPhase == PhaseOff)
            {
                // Считать, только пока Windows подтверждает, что экран выключен; включился сам — кто-то тронул мышь.
                if (DisplayState.Current == DisplayState.Off)
                {
                    if (calOffSince == DateTime.MinValue) calOffSince = DateTime.UtcNow;
                }
                else if (calOffSince != DateTime.MinValue)
                {
                    AbortScreenCalibration(L.T("the screen was turned back on — the mouse or keyboard was touched", "екран знову ввімкнувся — торкнулися миші чи клавіатури"));
                    return;
                }
                else if (elapsed > OffWaitSeconds)
                {
                    BeginPhase(PhaseWhite);  // Windows не выключила экран: панель останется неизмеренной
                    return;
                }
                ready = ready && calOffSince != DateTime.MinValue && (DateTime.UtcNow - calOffSince).TotalSeconds >= SettleSeconds;
            }

            if (ready && !double.IsNaN(x.DischargeW) && !double.IsNaN(x.CpuPkgW))
                calSamples.Add(new CalSample { Rest = x.DischargeW - x.CpuPkgW - (double.IsNaN(x.GpuW) ? 0 : x.GpuW), Cpu = x.CpuPkgW });

            double waitLeft = ready ? 0 : Math.Max(gpuOk ? 0 : QuietSeconds - quietFor, SettleSeconds - elapsed);
            UpdatePatternText(waitLeft);
            screenButton.Text = string.Format(L.T("Cancel · {0} · {1}", "Скасувати · {0} · {1}"), PhaseNames[calPhase],
                ready ? calSamples.Count + "/" + SamplesPerPhase : L.T("waiting for quiet", "чекаємо тиші"));

            double result, noise;
            if (calSamples.Count >= SamplesPerPhase && TryPhaseResult(out result, out noise))
            {
                calResult[calPhase] = result;
                calNoise[calPhase] = noise;
                int next = calPhase + 1;
                if (next == PhaseOff && calSkipOff) next++;
                if (calPhase < PhaseWhite || (calPhase == PhaseWhite && calB0 < 95)) BeginPhase(next);
                else FinishScreenCalibration();
            }
            else if (elapsed > PhaseTimeoutSeconds)
            {
                AbortScreenCalibration(calSamples.Count < SamplesPerPhase
                    ? L.T("the NVIDIA GPU never stayed asleep long enough — too much interference", "відеокарта NVIDIA так і не заснула надовго — завади завеликі")
                    : L.T("the CPU load kept changing", "навантаження процесора весь час змінювалося"));
            }
        }

        /// <summary>Медиана «остального» по замерам с обычной нагрузкой процессора и разброс этой медианы.</summary>
        bool TryPhaseResult(out double result, out double noise)
        {
            result = noise = double.NaN;
            var cpu = new List<double>();
            foreach (var s in calSamples) cpu.Add(s.Cpu);
            double cpuMed = Median(cpu);
            var kept = new List<double>();
            foreach (var s in calSamples)
                if (Math.Abs(s.Cpu - cpuMed) <= CpuTolerance) kept.Add(s.Rest);
            if (kept.Count < MinKeptSamples) return false;  // мало спокойных замеров — собираем дальше
            result = Median(kept);
            var dev = new List<double>();
            foreach (double r in kept) dev.Add(Math.Abs(r - result));
            noise = Median(dev) * 1.4826 / Math.Sqrt(kept.Count);
            return true;
        }

        void FinishScreenCalibration()
        {
            bool full = calPhase == PhaseWhite100;
            RestoreAfterCalibration();
            double black = calResult[PhaseBlack];
            double white = calResult[PhaseWhite] - black;
            double noise = Math.Sqrt(calNoise[PhaseBlack] * calNoise[PhaseBlack] + calNoise[PhaseWhite] * calNoise[PhaseWhite]);
            // Панель: чёрный экран сверх выключенного. Меньше шума — не измерена (NaN), а не ноль.
            double panel = black - calResult[PhaseOff];
            double panelNoise = Math.Sqrt(calNoise[PhaseBlack] * calNoise[PhaseBlack] + calNoise[PhaseOff] * calNoise[PhaseOff]);
            if (double.IsNaN(panel) || panel < 2 * panelNoise) panel = double.NaN;
            if (white < 0.3 || white < 3 * noise)
            {
                ShowNote(string.Format(L.T(
                    "Unreliable measurement: white differs from black by {0:0.0} W with noise ±{1:0.0} W. Try again without touching the laptop. The previous result is kept.",
                    "Вимір ненадійний: білий екран відрізняється від чорного на {0:0.0} Вт при шумі ±{1:0.0} Вт. Спробуйте ще раз, нічого не роблячи за ноутбуком. Попередній результат збережено."),
                    white, noise));
                UpdateScreenButton();
                return;
            }
            var m = new ScreenModel { MeasuredAt = DateTime.Now, PanelW = panel };
            m.Points[0] = 0;
            m.Points[Math.Max(1, calB0)] = white;
            if (full) m.Points[100] = Math.Max(white, calResult[PhaseWhite100] - black);
            screenModel = m;
            m.Save();
            string panelText = !double.IsNaN(panel)
                ? string.Format(L.T(" The panel itself (black image vs screen off): {0:0.0} ± {1:0.0} W.", " Сама панель (чорне зображення проти вимкненого екрана): {0:0.0} ± {1:0.0} Вт."), panel, panelNoise)
                : calSkipOff
                    ? L.T(" The panel itself cannot be measured on this laptop: switching the screen off puts it to sleep (Modern Standby). It stays in idle.",
                          " Саму панель на цьому ноутбуці не виміряти: вимкнення екрана присипляє його (Modern Standby). Вона лишається в простої.")
                : double.IsNaN(calResult[PhaseOff])
                    ? L.T(" The panel itself was not measured: Windows did not switch the screen off.", " Саму панель не виміряно: Windows не вимкнула екран.")
                    : string.Format(L.T(" The panel itself: below the noise (±{0:0.0} W) — not counted.", " Сама панель: нижче шуму (±{0:0.0} Вт) — не враховується."), panelNoise);
            ShowNote((full
                ? string.Format(L.T("Screen: a white image costs {0:0.0} ± {1:0.0} W over black at {2} % brightness and {3:0.0} W at 100 %.",
                                     "Екран: біле зображення коштує {0:0.0} ± {1:0.0} Вт понад чорне при яскравості {2} % і {3:0.0} Вт при 100 %."),
                    white, noise, calB0, m.Points[100])
                : string.Format(L.T("Screen: a white image costs {0:0.0} ± {1:0.0} W over black at {2} % brightness.",
                                    "Екран: біле зображення коштує {0:0.0} ± {1:0.0} Вт понад чорне при яскравості {2} %."),
                    white, noise, calB0)) + panelText);
            UpdateScreenButton();
        }

        public void AbortScreenCalibration(string reason)
        {
            if (calPhase < 0) return;
            RestoreAfterCalibration();
            ShowNote(L.T("Screen measurement stopped: ", "Вимірювання екрана перервано: ") + reason + L.T(". Everything was restored.", ". Усе повернуто."));
            UpdateScreenButton();
        }

        void RestoreAfterCalibration()
        {
            int phase = calPhase;
            if (phase == PhaseOff) ScreenOn();
            calPhase = -1;
            if (pattern != null)
            {
                var p = pattern;
                pattern = null;
                p.Close();
                p.Dispose();
            }
            if (phase == PhaseWhite100) sampler.WakeHint(GpuWake.HintCalibration);
            Brightness.Set(calB0);
            Settings.SetDouble("PendingBrightness", double.NaN);
            sampler.GpuPolling = calGpuPollingWas;
            SyncProcessCollection();
        }

        static double Median(List<double> v)
        {
            var s = new List<double>(v);
            s.Sort();
            return s.Count % 2 == 1 ? s[s.Count / 2] : (s[s.Count / 2 - 1] + s[s.Count / 2]) / 2;
        }
    }
}
