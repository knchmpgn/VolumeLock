namespace VolumeLock.Services;

/// <summary>
/// Resolves the directory containing the portable executable. With
/// IncludeAllContentForSelfExtract the app is extracted to a temp cache folder
/// and AppContext.BaseDirectory points there, so user data (settings, logs) must
/// be anchored to the real exe location instead.
/// </summary>
public static class AppPaths
{
    public static string ExecutableDirectory { get; } =
        Path.GetDirectoryName(Environment.ProcessPath)
        ?? AppContext.BaseDirectory;
}
