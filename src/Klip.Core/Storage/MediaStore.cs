using Klip.Core.Common;

namespace Klip.Core.Storage;

/// <summary>
/// Keeps media (PNG) on disk named by hash, split into yyyy-MM subfolders.
/// Paths come back RELATIVE to the data root so the db stays portable.
/// </summary>
public sealed class MediaStore
{
    /// <summary>Saves PNG bytes; returns a path relative to AppPaths.DataDir.</summary>
    public string SavePng(byte[] pngBytes, string contentHash, DateTimeOffset timestamp)
    {
        var subdir = AppPaths.MediaSubdirFor(timestamp);
        Directory.CreateDirectory(subdir);
        var file = Path.Combine(subdir, $"{contentHash}.png");
        if (!File.Exists(file))
            File.WriteAllBytes(file, pngBytes);
        return Path.GetRelativePath(AppPaths.DataDir, file);
    }

    /// <summary>Saves a JPEG thumbnail; returns a relative path.</summary>
    public string SaveThumb(byte[] jpegBytes, string contentHash, DateTimeOffset timestamp)
    {
        var subdir = AppPaths.ThumbSubdirFor(timestamp);
        Directory.CreateDirectory(subdir);
        var file = Path.Combine(subdir, $"{contentHash}.jpg");
        if (!File.Exists(file))
            File.WriteAllBytes(file, jpegBytes);
        return Path.GetRelativePath(AppPaths.DataDir, file);
    }

    public string ToAbsolute(string relativePath) => Path.Combine(AppPaths.DataDir, relativePath);

    /// <summary>Deletes orphan files (relative paths) from disk.</summary>
    public void DeleteFiles(IEnumerable<string> relativePaths)
    {
        foreach (var rel in relativePaths.Distinct())
        {
            try
            {
                var abs = ToAbsolute(rel);
                if (File.Exists(abs))
                    File.Delete(abs);
            }
            catch (IOException)
            {
                // best effort, o arquivo pode estar em uso por outro processo
            }
        }
    }
}
