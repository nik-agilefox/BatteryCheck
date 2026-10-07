using System;

namespace BatteryCheck
{
    /// <summary>Уведомления (см. Notifier): проверка на каждом замере, показ — значком в трее (событие Notify).</summary>
    sealed partial class MainWindow
    {
        readonly Notifier notifier = new Notifier();
        bool notificationsEnabled = Settings.GetDouble("Notifications", 1) != 0;

        /// <summary>Уведомление готово — контроллер показывает его у значка в трее.</summary>
        public event Action<Notice> Notify;

        public bool NotificationsEnabled
        {
            get { return notificationsEnabled; }
            set
            {
                notificationsEnabled = value;
                Settings.SetDouble("Notifications", value ? 1 : 0);
            }
        }

        void CheckNotifications(Snapshot snap)
        {
            // Учёт идёт и при выключенных уведомлениях: включили — пороги и «уже показано» верны сразу.
            var n = notifier.Check(snap, baseline, DateTime.Now, calPhase >= 0);
            var h = Notify;
            if (n != null && notificationsEnabled && h != null) h(n);
        }
    }
}
