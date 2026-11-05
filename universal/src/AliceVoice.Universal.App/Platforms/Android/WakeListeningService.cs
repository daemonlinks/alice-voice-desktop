using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;

namespace AliceVoice.AppU;

/// <summary>
/// Foreground microphone service so Android does not suspend wake-word listening
/// the moment the screen locks. The session still owns the audio callback.
/// </summary>
[Service(Exported = false, ForegroundServiceType = ForegroundService.TypeMicrophone)]
public class WakeListeningService : Service
{
    public const int NotificationId = 4101;
    public const string ChannelId = "alice_wake";

    public override IBinder? OnBind(Intent? intent) => null;

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        var notification = BuildNotification();
        if (OperatingSystem.IsAndroidVersionAtLeast(34))
            StartForeground(NotificationId, notification, ForegroundService.TypeMicrophone);
        else
            StartForeground(NotificationId, notification);
        return StartCommandResult.Sticky;
    }

    public override void OnDestroy()
    {
        if (OperatingSystem.IsAndroidVersionAtLeast(24))
            StopForeground(StopForegroundFlags.Remove);
        base.OnDestroy();
    }

    private Notification BuildNotification()
    {
        if (OperatingSystem.IsAndroidVersionAtLeast(26))
        {
            var manager = (NotificationManager?)GetSystemService(NotificationService);
            var channel = new NotificationChannel(ChannelId, "Wake word", NotificationImportance.Low)
            {
                Description = "Keeps the microphone open while Wake mode is listening for hey alice."
            };
            manager?.CreateNotificationChannel(channel);
        }

        var builder = OperatingSystem.IsAndroidVersionAtLeast(26)
            ? new Notification.Builder(this, ChannelId)
            : new Notification.Builder(this);
        return builder
            .SetContentTitle("Alice Voice")
            .SetContentText("Listening for “hey alice”")
            .SetSmallIcon(Android.Resource.Drawable.IcDialogInfo)
            .SetOngoing(true)
            .Build();
    }
}
