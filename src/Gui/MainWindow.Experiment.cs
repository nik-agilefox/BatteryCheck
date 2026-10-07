using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;

namespace BatteryCheck
{
    /// <summary>
    /// Проверка «до / после» (см. MinuteRecorder, ExperimentResult): «до» — 8 тихих минут (если они только что были —
    /// сразу из учёта), затем пользователь делает изменение и нажимает «Готово», «после» — ещё 8 тихих минут.
    /// На время проверки экран не гаснет (иначе выключение экрана по таймеру исказило бы сравнение).
    /// Карточка — вверху вкладки «Советы», итоги — в logs\experiments.csv.
    /// </summary>
    sealed partial class MainWindow
    {
        enum ExpState { Idle, Before, Change, After, Done }

        const int ExpMinutes = 8, ExpMinMinutes = 5;
        const double ExpTimeoutMinutes = 30;

        readonly MinuteRecorder recorder = new MinuteRecorder();
        ExpState expState;
        string expTitle;
        List<ExpMinute> expBefore;
        DateTime expPhaseStart;
        ExperimentResult expResult;
        string expError;

        TextBox expTitleBox;
        TextBlock expStatus;
        FlatButton expStart, expDone, expCancel;
        StackPanel expHistory;
        FrameworkElement expCard;

        FrameworkElement BuildExperimentCard()
        {
            var b = new Border { CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1), Padding = new Thickness(12, 10, 12, 10), Margin = new Thickness(0, 12, 0, 0) };
            b.SetResourceReference(Border.BorderBrushProperty, Keys.Border);
            b.SetResourceReference(Border.BackgroundProperty, Keys.Page);
            var panel = new StackPanel();
            panel.Children.Add(Label(L.T("Check a change: before / after", "Перевірити зміну: до / після"), 13, FontWeights.SemiBold, Keys.Text));
            var hint = Label(L.T("Compares quiet minutes on battery (no keyboard or mouse input, this window open) before and after your change: total power, CPU, discrete GPU. About 20 minutes; the screen is kept on meanwhile.",
                                 "Порівнює тихі хвилини від батареї (без вводу з клавіатури й миші, це вікно відкрите) до і після вашої зміни: загальну потужність, процесор, дискретну відеокарту. Близько 20 хвилин; екран увесь цей час не гасне."),
                             11, FontWeights.Normal, Keys.Muted);
            hint.TextWrapping = TextWrapping.Wrap;
            hint.Margin = new Thickness(0, 2, 0, 8);
            panel.Children.Add(hint);

            var row = new DockPanel();
            expStart = new FlatButton(L.T("Start", "Почати"));
            expStart.Margin = new Thickness(8, 0, 0, 0);
            expStart.Click += () => StartExperiment(expTitleBox.Text);
            expDone = new FlatButton(L.T("Done", "Готово"));
            expDone.Margin = new Thickness(8, 0, 0, 0);
            expDone.Click += ChangeDone;
            expCancel = new FlatButton(L.T("Cancel", "Скасувати"));
            expCancel.Margin = new Thickness(8, 0, 0, 0);
            expCancel.Click += () => StopExperiment(ExpState.Idle);
            foreach (var btn in new[] { expCancel, expDone, expStart })
            {
                DockPanel.SetDock(btn, Dock.Right);
                row.Children.Add(btn);
            }
            expTitleBox = new TextBox { FontSize = 13, Padding = new Thickness(6, 3, 6, 3), VerticalContentAlignment = VerticalAlignment.Center, BorderThickness = new Thickness(1) };
            expTitleBox.SetResourceReference(Control.BackgroundProperty, Keys.Surface);
            expTitleBox.SetResourceReference(Control.ForegroundProperty, Keys.Text);
            expTitleBox.SetResourceReference(Control.BorderBrushProperty, Keys.Border);
            expTitleBox.SetResourceReference(TextBoxBase.CaretBrushProperty, Keys.Text);
            expTitleBox.ToolTip = L.T("What will you change? For example: closed PredatorSense", "Що ви зміните? Наприклад: закрив PredatorSense");
            expTitleBox.Text = expTitle ?? "";
            expTitleBox.KeyDown += (s, e) => { if (e.Key == Key.Enter && expStart.Visibility == Visibility.Visible) StartExperiment(expTitleBox.Text); };
            row.Children.Add(expTitleBox);
            panel.Children.Add(row);

            expStatus = Label("", 12, FontWeights.Normal, Keys.Text2);
            expStatus.TextWrapping = TextWrapping.Wrap;
            expStatus.Margin = new Thickness(0, 8, 0, 0);
            panel.Children.Add(expStatus);
            expHistory = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
            panel.Children.Add(expHistory);
            b.Child = panel;
            expCard = b;
            UpdateExperimentUi();
            FillExperimentHistory();
            return b;
        }

        /// <summary>Каждый замер — и когда окно скрыто: учёт минут и ход проверки.</summary>
        void RecordExperimentSample(Snapshot snap)
        {
            recorder.Add(snap.Sample, brightness, IsOnScreen && calPhase < 0);
            if (expState == ExpState.Idle || expState == ExpState.Done || expState == ExpState.Change) return;

            DateTime now = snap.Sample.Time;
            var ok = recorder.OkMinutes(expPhaseStart, now);
            double elapsed = (DateTime.Now - expPhaseStart).TotalMinutes;
            bool enough = ok.Count >= ExpMinutes || (elapsed >= ExpTimeoutMinutes && ok.Count >= ExpMinMinutes);
            if (!enough && elapsed >= ExpTimeoutMinutes)
            {
                expError = L.T("Not enough quiet minutes in 30 minutes: the laptop must stay on battery, untouched, with this window open.",
                               "Замало тихих хвилин за 30 хвилин: ноутбук має бути від батареї, без дотиків, із відкритим цим вікном.");
                StopExperiment(ExpState.Idle);
                return;
            }
            if (enough && expState == ExpState.Before)
            {
                expBefore = ok;
                expState = ExpState.Change;
            }
            else if (enough && expState == ExpState.After)
            {
                double fullWh = snap.Info.FullChargedCapacity / 1000.0;
                expResult = ExperimentResult.Compare(expTitle, PhaseStats.Of(expBefore), PhaseStats.Of(ok), fullWh);
                try { ExperimentLog.Append(LogDirectory, expResult); }
                catch (Exception) { }  // журнал не записался — итог всё равно на экране
                StopExperiment(ExpState.Done);
                FillExperimentHistory();
                return;
            }
            UpdateExperimentUi();
        }

        void StartExperiment(string title)
        {
            if (expState == ExpState.Before || expState == ExpState.Change || expState == ExpState.After) return;
            expTitle = string.IsNullOrWhiteSpace(title) ? L.T("change", "зміна") : title.Trim();
            if (expTitleBox != null) expTitleBox.Text = expTitle;
            expError = null;
            expResult = null;
            SetThreadExecutionState(EsContinuous | EsDisplayRequired | EsSystemRequired);
            // «До» — из только что прошедших минут, если они уже тихие; иначе ждём новых.
            var recent = recorder.RecentOk(ExpMinutes, DateTime.Now);
            if (recent.Count >= ExpMinutes)
            {
                expBefore = recent;
                expState = ExpState.Change;
            }
            else
            {
                expPhaseStart = DateTime.Now;
                expState = ExpState.Before;
            }
            UpdateExperimentUi();
        }

        void ChangeDone()
        {
            if (expState != ExpState.Change) return;
            expPhaseStart = DateTime.Now;
            expState = ExpState.After;
            Keyboard.ClearFocus();
            UpdateExperimentUi();
        }

        void StopExperiment(ExpState next)
        {
            expState = next;
            SetThreadExecutionState(EsContinuous);  // экран снова гаснет по настройкам Windows
            UpdateExperimentUi();
        }

        void UpdateExperimentUi()
        {
            if (expStatus == null) return;
            bool running = expState == ExpState.Before || expState == ExpState.Change || expState == ExpState.After;
            expStart.Visibility = running ? Visibility.Collapsed : Visibility.Visible;
            expDone.Visibility = expState == ExpState.Change ? Visibility.Visible : Visibility.Collapsed;
            expCancel.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
            expTitleBox.IsEnabled = !running;

            expStatus.Inlines.Clear();
            bool onBattery = lastSnap != null && lastSnap.Sample.Battery.Discharging;
            switch (expState)
            {
                case ExpState.Before:
                case ExpState.After:
                    int n = recorder.OkMinutes(expPhaseStart, DateTime.Now).Count;
                    expStatus.Inlines.Add(new Run(expState == ExpState.Before
                        ? string.Format(L.T("Before: leave the laptop alone. Quiet minutes {0}/{1}.", "До: не чіпайте ноутбук. Тихих хвилин {0}/{1}."), n, ExpMinutes)
                        : string.Format(L.T("After: leave the laptop alone. Quiet minutes {0}/{1}.", "Після: не чіпайте ноутбук. Тихих хвилин {0}/{1}."), n, ExpMinutes))
                        { FontWeight = FontWeights.SemiBold });
                    if (!onBattery)
                        expStatus.Inlines.Add(new Run(L.T(" Paused: unplug the charger — the comparison works only on battery.",
                                                          " Пауза: відключіть зарядку — порівняння працює лише від батареї.")));
                    break;
                case ExpState.Change:
                    expStatus.Inlines.Add(new Run(string.Format(L.T("“Before” is recorded ({0:0.0} W). Now make the change — {1} — then press Done and leave the laptop alone.",
                                                                    "«До» записано ({0:0.0} Вт). Тепер зробіть зміну — {1} — і натисніть «Готово», далі не чіпайте ноутбук."),
                        PhaseStats.Of(expBefore).W, expTitle)) { FontWeight = FontWeights.SemiBold });
                    break;
                case ExpState.Done:
                    if (expResult == null) break;
                    var head = new Run(expResult.Headline()) { FontWeight = FontWeights.SemiBold };
                    if (expResult.Found && expResult.DeltaW < 0) head.SetResourceReference(TextElement.ForegroundProperty, Keys.Good);
                    expStatus.Inlines.Add(new Run(expTitle + ": "));
                    expStatus.Inlines.Add(head);
                    expStatus.Inlines.Add(new LineBreak());
                    expStatus.Inlines.Add(new Run(expResult.Details()) { FontSize = 11 });
                    break;
                default:
                    if (expError != null) expStatus.Inlines.Add(new Run(expError));
                    break;
            }
            expStatus.Visibility = expStatus.Inlines.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        void FillExperimentHistory()
        {
            if (expHistory == null) return;
            expHistory.Children.Clear();
            List<ExperimentResult> list;
            try { list = ExperimentLog.Load(LogDirectory, 5); }
            catch (Exception) { return; }
            if (list.Count == 0) return;
            expHistory.Children.Add(Label(L.T("Previous checks", "Попередні перевірки"), 11, FontWeights.Normal, Keys.Muted));
            foreach (var r in list)
            {
                var t = Label(string.Format("{0:dd.MM HH:mm} · {1} · {2}", r.At, r.Title, r.Headline()), 12, FontWeights.Normal, Keys.Text2);
                t.TextTrimming = TextTrimming.CharacterEllipsis;
                t.ToolTip = r.Details();
                expHistory.Children.Add(t);
            }
        }

        /// <summary>Кнопка «Проверить» у совета: проверка с названием совета.</summary>
        void CheckRecommendation(Recommendation it)
        {
            StartExperiment(it.Title);
            if (expCard != null) expCard.BringIntoView();
        }

        const uint EsContinuous = 0x80000000, EsSystemRequired = 0x1, EsDisplayRequired = 0x2;

        [DllImport("kernel32.dll")]
        static extern uint SetThreadExecutionState(uint flags);
    }
}
