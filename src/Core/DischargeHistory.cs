using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace BatteryCheck
{
    /// <summary>Энергия за разряд в одной полосе заряда: по счётчику (мощность × время) и по индикатору (убыль остатка).</summary>
    sealed class GaugeBand
    {
        public double RateWh, CapWh;
        public double Ratio { get { return CapWh >= DischargeHistory.MinBandWh ? RateWh / CapWh : double.NaN; } }
    }

    /// <summary>Один разряд: от отключения зарядки до её подключения (по CSV-логам программы).</summary>
    sealed class DischargeSession
    {
        public DateTime Start, End;
        public double ActiveS;        // время работы от батареи, когда программа писала лог
        public double PausedS;        // пропуски внутри разряда: сон, гибернация, программа не была запущена
        public double SocStart = double.NaN, SocEnd = double.NaN;
        public double CapStartMwh = double.NaN, CapEndMwh = double.NaN, FullMwh = double.NaN;
        public double EnergyByRateWh; // сумма мощности × время за активное время
        public bool Ongoing;
        // Экран: изображение (оценка по замеру экрана, без самой панели) за время, когда оценка была, и время, когда экран
        // горел (до колонки display_state в логе — всё активное время).
        public double ScreenContentWh, ScreenKnownS, DisplayOnS;

        /// <summary>
        /// Честность индикатора заряда: энергия по счётчику и убыль остатка (то, от чего считаются проценты Windows)
        /// на одних и тех же непрерывных участках. Полосы: [0] — 0–20 %, … [4] — 80–100 %.
        /// </summary>
        public double GaugeRateWh, GaugeCapWh;
        public readonly GaugeBand[] Bands = { new GaugeBand(), new GaugeBand(), new GaugeBand(), new GaugeBand(), new GaugeBand() };
        /// <summary>«Прыжки»: остаток упал на 3 % и больше за минуту, а энергии за это время ушло втрое меньше.</summary>
        public int Jumps;

        public double UsedWh { get { return (CapStartMwh - CapEndMwh) / 1000; } }

        /// <summary>Средняя мощность за активное время, Вт.</summary>
        public double AvgW { get { return ActiveS > 60 ? EnergyByRateWh * 3600 / ActiveS : double.NaN; } }

        /// <summary>Сколько ноутбук проработал бы от 100 % до 0 % при этой средней мощности, ч.</summary>
        public double FullRuntimeH { get { return AvgW > 0 && !double.IsNaN(FullMwh) ? FullMwh / 1000 / AvgW : double.NaN; } }

        /// <summary>
        /// Отдано энергии / убыль остатка. 1 — проценты Windows соответствуют отданной энергии; 0,8 — проценты падают
        /// на 25 % быстрее, и 0 % наступит, когда батарея отдаст лишь 80 % обещанного. NaN — мало данных.
        /// </summary>
        public double GaugeRatio { get { return GaugeCapWh >= DischargeHistory.MinGaugeWh ? GaugeRateWh / GaugeCapWh : double.NaN; } }

        /// <summary>Подробности для подсказки: обе энергии, полосы, прыжки.</summary>
        public string GaugeDetails()
        {
            var lines = new List<string>
            {
                string.Format(L.T("By the energy counter {0:0.00} Wh, by the Windows indicator {1:0.00} Wh (continuous stretches only).",
                                  "За лічильником енергії {0:0.00} Вт·год, за індикатором Windows {1:0.00} Вт·год (лише безперервні ділянки)."),
                    GaugeRateWh, GaugeCapWh),
            };
            for (int i = 4; i >= 0; i--)
                if (!double.IsNaN(Bands[i].Ratio))
                    lines.Add(string.Format("  {0}: {1:0.00}", GaugeVerdict.BandName(i), Bands[i].Ratio));
            if (Jumps > 0) lines.Add(string.Format(L.T("Percentage jumps: {0}", "Стрибків відсотків: {0}"), Jumps));
            lines.Add(L.T("1.00 — percentages match the energy delivered; below 0.92 — they drop faster.",
                          "1,00 — відсотки відповідають відданій енергії; нижче 0,92 — падають швидше."));
            return string.Join("\n", lines);
        }
    }

    /// <summary>Вывод по всем разрядам: честен ли индикатор заряда.</summary>
    sealed class GaugeVerdict
    {
        public enum Kind { Unknown, Ok, Faster, Drifting }

        public Kind Status = Kind.Unknown;
        public double Ratio = double.NaN;  // по всем разрядам вместе
        public int Sessions;               // разрядов с достаточными данными
        public int Band = -1;              // для Drifting: полоса, где отношение упало
        public double BandNow = double.NaN, BandUsual = double.NaN;
        public int Jumps;

        public bool Warning { get { return Status == Kind.Faster || Status == Kind.Drifting; } }

        public static string BandName(int band)
        {
            return string.Format("{0}–{1} %", band * 20, band * 20 + 20);
        }

        /// <summary>Вывод одной-двумя фразами на языке интерфейса.</summary>
        public string Describe()
        {
            string text;
            switch (Status)
            {
                case Kind.Ok:
                    text = string.Format(L.T("Windows charge indicator is honest: percentages match the energy delivered (ratio {0:0.00} over {1} {2}).",
                                             "Індикатор заряду Windows чесний: відсотки відповідають відданій енергії (відношення {0:0.00} за {1} {2})."),
                        Ratio, Sessions, L.Plural(Sessions, "discharge", "discharges", "розряд", "розряди", "розрядів"));
                    break;
                case Kind.Faster:
                    text = string.Format(L.T("⚠ Windows percentages drop faster than energy is delivered (ratio {0:0.00}): 0 % will come after only ≈ {1:0} % of the stated capacity. ",
                                             "⚠ Відсотки Windows падають швидше, ніж віддається енергія (відношення {0:0.00}): 0 % настане, коли батарея віддасть лише ≈ {1:0} % заявленої ємності. "),
                        Ratio, Ratio * 100) + CalibrationHint;
                    break;
                case Kind.Drifting:
                    text = string.Format(L.T("⚠ In the {0} range percentages started dropping faster: ratio {1:0.00} in the last 3 discharges vs usual {2:0.00}. ",
                                             "⚠ У діапазоні {0} відсотки почали падати швидше: відношення {1:0.00} в останніх 3 розрядах проти звичайного {2:0.00}. "),
                        BandName(Band), BandNow, BandUsual) + CalibrationHint;
                    break;
                default:
                    text = L.T("Charge indicator check: not enough data yet — it needs a continuous discharge of about 7 % or more.",
                               "Перевірка індикатора заряду: поки замало даних — потрібен безперервний розряд приблизно від 7 %.");
                    break;
            }
            if (Jumps > 0)
                text += string.Format(L.T(" Percentage jumps: {0}.", " Стрибків відсотків: {0}."), Jumps);
            return text;
        }

        static string CalibrationHint
        {
            get
            {
                return L.T("Try calibrating: charge to 100 %, run down until the laptop shuts off, charge to 100 % again. If it persists, the cells are wearing.",
                           "Спробуйте калібрування: зарядити до 100 %, розрядити до вимкнення, знову зарядити до 100 %. Якщо не допоможе — зношуються комірки.");
            }
        }
    }

    /// <summary>
    /// Разбор CSV-логов (battery_*.csv) в список разрядов. Логи разных версий и одновременно работавших копий
    /// сливаются по времени; повторы (замеры чаще 0,5 с) отбрасываются. Пропуск больше MaxActiveGap внутри
    /// разряда — сон или программа не работала: время идёт в PausedS; пропуск больше BreakGap — новый разряд.
    /// </summary>
    static class DischargeHistory
    {
        const double MaxActiveGapS = 60, BreakGapS = 12 * 3600, MinSessionS = 120, MinSocDrop = 1;
        public const double MinGaugeWh = 1.5, MinBandWh = 2, MinVerdictWh = 5;
        public const double FasterThreshold = 0.92, DriftThreshold = 0.93;

        struct Row
        {
            public DateTime T;
            public bool OnAc, Discharging;
            public double Cap, Full, Soc, RateMw, ScreenW;
            public int Display;  // display_state; -1 — нет в логе
        }

        public static List<DischargeSession> Load(string dir)
        {
            var rows = new List<Row>();
            if (Directory.Exists(dir))
                foreach (var f in Directory.GetFiles(dir, "battery_*.csv"))
                    ReadFile(f, rows);
            else if (File.Exists(dir))
                ReadFile(dir, rows);
            rows.Sort((a, b) => a.T.CompareTo(b.T));

            var sessions = new List<DischargeSession>();
            DischargeSession cur = null;
            Row prev = new Row();
            DateTime lastT = DateTime.MinValue;
            foreach (var r in rows)
            {
                if ((r.T - lastT).TotalSeconds < 0.5) continue;  // та же секунда из другого лога
                lastT = r.T;

                if (r.Discharging && !r.OnAc)
                {
                    double dt = cur == null ? 0 : (r.T - prev.T).TotalSeconds;
                    if (cur != null && dt > BreakGapS) { Close(cur, sessions); cur = null; }
                    if (cur == null)
                    {
                        cur = new DischargeSession { Start = r.T, SocStart = r.Soc, CapStartMwh = r.Cap, FullMwh = r.Full };
                    }
                    else if (dt <= MaxActiveGapS)
                    {
                        cur.ActiveS += dt;
                        if (!double.IsNaN(r.RateMw) && r.RateMw < 0) cur.EnergyByRateWh += -r.RateMw / 1000 * dt / 3600;
                        if (!double.IsNaN(r.ScreenW)) { cur.ScreenContentWh += r.ScreenW * dt / 3600; cur.ScreenKnownS += dt; }
                        if (r.Display != 0) cur.DisplayOnS += dt;
                        AddGauge(cur, prev, r, dt);
                    }
                    else
                    {
                        cur.PausedS += dt;
                    }
                    cur.End = r.T;
                    if (!double.IsNaN(r.Soc)) cur.SocEnd = r.Soc;
                    if (!double.IsNaN(r.Cap)) cur.CapEndMwh = r.Cap;
                    if (!double.IsNaN(r.Full)) cur.FullMwh = r.Full;
                    if (double.IsNaN(cur.SocStart)) cur.SocStart = r.Soc;
                    if (double.IsNaN(cur.CapStartMwh)) cur.CapStartMwh = r.Cap;
                    prev = r;
                }
                else if (r.OnAc && cur != null)
                {
                    Close(cur, sessions);
                    cur = null;
                }
            }
            if (cur != null)
            {
                cur.Ongoing = (DateTime.Now - cur.End).TotalMinutes < 3;  // лог сбрасывается на диск раз в минуту
                Close(cur, sessions);
            }
            sessions.Reverse();  // новые сверху
            return sessions;
        }

        /// <summary>
        /// Участок между двумя соседними замерами: энергия по счётчику (трапеция) и убыль остатка — только если известны
        /// мощность и остаток на обоих концах. Полоса — по заряду в начале участка: на низком заряде напряжение ниже,
        /// и на процент приходится меньше энергии, поэтому сравнивать со временем имеет смысл полосу с той же полосой.
        /// </summary>
        static void AddGauge(DischargeSession s, Row a, Row b, double dt)
        {
            if (double.IsNaN(a.RateMw) || double.IsNaN(b.RateMw) || a.RateMw >= 0 || b.RateMw >= 0) return;
            if (double.IsNaN(a.Cap) || double.IsNaN(b.Cap)) return;
            double rateWh = (-a.RateMw - b.RateMw) / 2 / 1000 * dt / 3600;
            double capWh = (a.Cap - b.Cap) / 1000;
            s.GaugeRateWh += rateWh;
            s.GaugeCapWh += capWh;
            if (!double.IsNaN(a.Soc))
            {
                var band = s.Bands[(int)Math.Max(0, Math.Min(4, Math.Floor(a.Soc / 20)))];
                band.RateWh += rateWh;
                band.CapWh += capWh;
            }
            if (!double.IsNaN(a.Soc) && !double.IsNaN(b.Soc) && a.Soc - b.Soc >= 3 && rateWh < capWh / 3) s.Jumps++;
        }

        /// <summary>
        /// Вывод по разрядам (новые первыми). Faster — в 2 из 3 последних разрядов (по ≥ 5 Вт·ч) проценты падают
        /// больше чем на 8 % быстрее отданной энергии. Drifting — в какой-то полосе 3 последних разряда подряд
        /// на 7 % ниже своей обычной величины (медиана более ранних, нужно ≥ 3).
        /// </summary>
        public static GaugeVerdict Evaluate(List<DischargeSession> sessions)
        {
            var v = new GaugeVerdict();
            double rate = 0, cap = 0;
            var recent = new List<double>();
            foreach (var s in sessions)
            {
                v.Jumps += s.Jumps;
                if (s.GaugeCapWh < MinVerdictWh) continue;
                v.Sessions++;
                rate += s.GaugeRateWh;
                cap += s.GaugeCapWh;
                if (recent.Count < 3) recent.Add(s.GaugeRatio);
            }
            if (v.Sessions == 0) return v;
            v.Ratio = rate / cap;
            int low = 0;
            foreach (double r in recent) if (r < FasterThreshold) low++;
            v.Status = low >= 2 || (recent.Count == 1 && low == 1) ? GaugeVerdict.Kind.Faster : GaugeVerdict.Kind.Ok;
            if (v.Status == GaugeVerdict.Kind.Faster) return v;

            for (int band = 0; band < 5; band++)
            {
                var list = new List<double>();  // новые первыми
                foreach (var s in sessions)
                {
                    double r = s.Bands[band].Ratio;
                    if (!double.IsNaN(r)) list.Add(r);
                }
                if (list.Count < 6) continue;
                double usual = Median(list.GetRange(3, list.Count - 3));
                bool drift = true;
                for (int i = 0; i < 3; i++) if (list[i] >= usual * DriftThreshold) drift = false;
                if (!drift) continue;
                v.Status = GaugeVerdict.Kind.Drifting;
                v.Band = band;
                v.BandNow = Median(list.GetRange(0, 3));
                v.BandUsual = usual;
                break;
            }
            return v;
        }

        static double Median(List<double> values)
        {
            var s = new List<double>(values);
            s.Sort();
            return s.Count % 2 == 1 ? s[s.Count / 2] : (s[s.Count / 2 - 1] + s[s.Count / 2]) / 2;
        }

        static void Close(DischargeSession s, List<DischargeSession> into)
        {
            bool longEnough = (s.End - s.Start).TotalSeconds >= MinSessionS && s.SocStart - s.SocEnd >= MinSocDrop;
            if (longEnough || s.Ongoing) into.Add(s);
        }

        static void ReadFile(string path, List<Row> rows)
        {
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var r = new StreamReader(fs))
                {
                    var head = (r.ReadLine() ?? "").Split(',');
                    int iT = Array.IndexOf(head, "time"), iAc = Array.IndexOf(head, "on_ac"), iDis = Array.IndexOf(head, "discharging"),
                        iCap = Array.IndexOf(head, "capacity_mwh"), iFull = Array.IndexOf(head, "full_mwh"),
                        iSoc = Array.IndexOf(head, "soc_pct"), iRate = Array.IndexOf(head, "rate_mw"),
                        iScreen = Array.IndexOf(head, "screen_w"), iDisplay = Array.IndexOf(head, "display_state");
                    if (iT < 0 || iAc < 0 || iDis < 0) return;
                    var inv = CultureInfo.InvariantCulture;
                    string line;
                    while ((line = r.ReadLine()) != null)
                    {
                        var c = line.Split(',');
                        DateTime t;
                        if (c.Length <= iDis || !DateTime.TryParseExact(c[iT], "yyyy-MM-ddTHH:mm:ss.fff", inv, DateTimeStyles.None, out t)) continue;
                        rows.Add(new Row
                        {
                            T = t,
                            OnAc = c[iAc] == "1",
                            Discharging = c[iDis] == "1",
                            Cap = Num(c, iCap),
                            Full = Num(c, iFull),
                            Soc = Num(c, iSoc),
                            RateMw = Num(c, iRate),
                            ScreenW = Num(c, iScreen),
                            Display = double.IsNaN(Num(c, iDisplay)) ? -1 : (int)Num(c, iDisplay),
                        });
                    }
                }
            }
            catch (IOException)
            {
                // Файл занят или повреждён — пропускаем.
            }
        }

        static double Num(string[] c, int i)
        {
            double v;
            return i >= 0 && i < c.Length && double.TryParse(c[i], NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? v : double.NaN;
        }
    }
}
