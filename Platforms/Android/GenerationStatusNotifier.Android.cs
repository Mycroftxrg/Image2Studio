using Android;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using AndroidX.Core.App;
using AndroidX.Core.Content;
using Image2Studio.Services;

namespace Image2Studio.Services;

public static partial class GenerationStatusNotifier
{
    private const string ChannelId = "image2_generation_status";
    private const int NotificationId = 20260523;
    private const int RequestCode = 2605;

    public static partial void ShowGenerating()
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (!CanShowNotification())
            {
                return;
            }

            var notification = CreateBuilder("正在生成", "Image2 Studio 正在等待图片生成")
                .SetOngoing(true)
                .SetOnlyAlertOnce(true)
                .SetProgress(100, 30, true);

            TryApplyProgressStyle(notification, completed: false);
            Notify(notification.Build());
        });
    }

    public static partial void ShowCompleted(bool hasImage)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (!CanShowNotification())
            {
                return;
            }

            var text = hasImage ? "生成完成，图片已返回" : "请求完成，但未识别到图片";
            var notification = CreateBuilder("生成完成", text)
                .SetOngoing(false)
                .SetAutoCancel(true)
                .SetProgress(0, 0, false);

            TryApplyProgressStyle(notification, completed: true);
            Notify(notification.Build());
        });
    }

    private static Notification.Builder CreateBuilder(string title, string text)
    {
        var context = Platform.AppContext;
        EnsureChannel(context);

        var intent = context.PackageManager?.GetLaunchIntentForPackage(context.PackageName ?? string.Empty);
        intent?.SetFlags(ActivityFlags.SingleTop | ActivityFlags.ClearTop);
        var pendingIntent = PendingIntent.GetActivity(
            context,
            RequestCode,
            intent,
            PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);

        var builder = Build.VERSION.SdkInt >= BuildVersionCodes.O
            ? new Notification.Builder(context, ChannelId)
            : new Notification.Builder(context);

        return builder
            .SetSmallIcon(Resource.Drawable.ic_stat_image2)
            .SetContentTitle(title)
            .SetContentText(text)
            .SetSubText("Image2 Studio")
            .SetContentIntent(pendingIntent)
            .SetShowWhen(true)
            .SetColor(Android.Graphics.Color.ParseColor("#D8B36A"))
            .SetCategory(Notification.CategoryProgress)
            .SetVisibility(NotificationVisibility.Public);
    }

    private static void TryApplyProgressStyle(Notification.Builder builder, bool completed)
    {
        if (Build.VERSION.SdkInt < (BuildVersionCodes)36)
        {
            return;
        }

        var style = new Notification.ProgressStyle()
            .SetStyledByProgress(true)
            .SetProgressIndeterminate(!completed)
            .SetProgress(completed ? 100 : 45);

        builder.SetStyle(style);
    }

    private static bool CanShowNotification()
    {
        var context = Platform.AppContext;
        if (Build.VERSION.SdkInt < BuildVersionCodes.Tiramisu)
        {
            return true;
        }

        if (ContextCompat.CheckSelfPermission(context, Manifest.Permission.PostNotifications) == Permission.Granted)
        {
            return true;
        }

        if (Platform.CurrentActivity is Activity activity)
        {
            ActivityCompat.RequestPermissions(
                activity,
                new[] { Manifest.Permission.PostNotifications },
                RequestCode);
        }

        return false;
    }

    private static void EnsureChannel(Context context)
    {
        if (Build.VERSION.SdkInt < BuildVersionCodes.O)
        {
            return;
        }

        var manager = (NotificationManager?)context.GetSystemService(Context.NotificationService);
        if (manager?.GetNotificationChannel(ChannelId) is not null)
        {
            return;
        }

        var channel = new NotificationChannel(
            ChannelId,
            "生成状态",
            NotificationImportance.Default)
        {
            Description = "显示 Image2 Studio 的图片生成状态"
        };
        channel.EnableVibration(false);
        channel.SetShowBadge(false);
        manager?.CreateNotificationChannel(channel);
    }

    private static void Notify(Notification notification)
    {
        var context = Platform.AppContext;
        var manager = (NotificationManager?)context.GetSystemService(Context.NotificationService);
        manager?.Notify(NotificationId, notification);
    }
}
