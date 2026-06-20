using System.Security;
using Microsoft.Windows.AppNotifications;

namespace JTS_App.Services;

public sealed class NotificationService
{
    private bool _registered;

    public void Initialize()
    {
        try
        {
            if (_registered || !AppNotificationManager.IsSupported()) return;
            AppNotificationManager.Default.Register();
            _registered = true;
        }
        catch
        {
            _registered = false;
        }
    }

    public void Show(string title, string body)
    {
        try
        {
            if (!AppNotificationManager.IsSupported()) return;
            if (!_registered) Initialize();

            var xml =
                $"""
                <toast>
                  <visual>
                    <binding template="ToastGeneric">
                      <text>{SecurityElement.Escape(title)}</text>
                      <text>{SecurityElement.Escape(body)}</text>
                    </binding>
                  </visual>
                </toast>
                """;
            AppNotificationManager.Default.Show(new AppNotification(xml));
        }
        catch
        {
            // Notifications are non-critical; never let a toast close the app.
        }
    }
}
