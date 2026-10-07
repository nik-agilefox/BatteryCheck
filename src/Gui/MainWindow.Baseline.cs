using System;
using System.Threading;
using System.Windows.Documents;

namespace BatteryCheck
{
    /// <summary>
    /// Эталон простоя и «лишние ватты» (см. IdleBaseline, Excess): эталон пересчитывается в фоне при запуске и раз в
    /// 30 минут — по логам, с кэшем по файлам; в плитке мощности от батареи — на сколько сейчас расход выше простоя.
    /// </summary>
    sealed partial class MainWindow
    {
        const double BaselineRefreshMinutes = 30;

        Baseline baseline;
        DateTime baselineAt = DateTime.MinValue;
        int baselineLoading;

        void MaybeRefreshBaseline()
        {
            if ((DateTime.UtcNow - baselineAt).TotalMinutes < BaselineRefreshMinutes) return;
            if (Interlocked.Exchange(ref baselineLoading, 1) == 1) return;
            baselineAt = DateTime.UtcNow;
            string dir = LogDirectory;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                Baseline b = null;
                try { b = IdleBaseline.Compute(dir, DateTime.Now); }
                catch (Exception) { b = null; }  // логи не читаются — попробуем в следующий раз
                finally
                {
                    Interlocked.Exchange(ref baselineLoading, 0);
                    if (b != null) Dispatcher.BeginInvoke(new Action(() => baseline = b));
                }
            });
        }

        /// <summary>Вторая строка плитки мощности от батареи: простой и «лишние ватты», разбор — в подсказке.</summary>
        void AddExcessLine(Snapshot snap)
        {
            powerSub.ToolTip = null;
            if (baseline == null) return;
            powerSub.Inlines.Add(new LineBreak());
            if (!baseline.Valid)
            {
                powerSub.Inlines.Add(new Run(string.Format(L.T("idle: collecting {0}/{1} quiet min", "простій: зібрано {0}/{1} тихих хв"),
                    baseline.QuietMinutes, Baseline.NeededMinutes)));
                powerSub.ToolTip = BaselineText.Describe(baseline);
                return;
            }
            var e = Excess.Compute(baseline, snap.BatteryAvgW, snap.Cpu10W, snap.Gpu10W, ScreenContentW());
            powerSub.Inlines.Add(new Run(e.Short()));
            powerSub.ToolTip = e.Details(baseline);
        }
    }
}
