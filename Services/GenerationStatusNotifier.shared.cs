namespace Image2Studio.Services;

public static partial class GenerationStatusNotifier
{
#if !ANDROID
    public static partial void ShowGenerating()
    {
    }

    public static partial void ShowCompleted(bool hasImage)
    {
    }
#endif
}
