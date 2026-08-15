using System.IO;
using System.Reflection;

namespace VolumeLock.Services;

/// <summary>
/// Resolves the application icon, working both in a normal folder deployment
/// (loose app.ico) and in a single-file bundle where content is extracted to
/// a temp directory at first launch.
/// </summary>
internal static class IconHelper
{
    private static string? _cachedPath;

    public static string? GetIconPath()
    {
        if (_cachedPath is not null)
            return _cachedPath;

        string baseDir = AppContext.BaseDirectory;

        foreach (string candidate in new[]
        {
            Path.Combine(baseDir, "app.ico"),
        })
        {
            if (File.Exists(candidate))
            {
                _cachedPath = candidate;
                return candidate;
            }
        }

        _cachedPath = ExtractEmbeddedIcon();
        return _cachedPath;
    }

    private static string? ExtractEmbeddedIcon()
    {
        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            string? name = assembly
                .GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith("app.ico", StringComparison.OrdinalIgnoreCase));

            if (name is null)
                return null;

            using var stream = assembly.GetManifestResourceStream(name);
            if (stream is null)
                return null;

            string dir = Path.Combine(Path.GetTempPath(), "VolumeLock");
            Directory.CreateDirectory(dir);

            string path = Path.Combine(dir, "app.ico");
            if (!File.Exists(path))
            {
                using var file = File.Create(path);
                stream.CopyTo(file);
            }

            return path;
        }
        catch
        {
            return null;
        }
    }
}
