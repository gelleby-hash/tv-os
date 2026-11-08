using System.IO;
using System.Security.Cryptography;
using System.Windows.Media.Imaging;

namespace TvLauncher.Backgrounds;

/// <summary>
/// Copies landscape Windows Spotlight photos out of the Windows cache folders into the launcher's own folder,
/// so the page can show them. Spotlight cache files have no extension, and some are portrait or not images.
/// </summary>
public static class SpotlightLibrary
{
    private const int MinWidth = 1920;
    private const int UhdWidth = 3840;

    /// <summary>
    /// Returns the file names (not paths) in <paramref name="targetFolder"/> for the qualifying photos:
    /// 3840 wide first, then the other landscape images at least 1920 wide, each group ordered by file name.
    /// Each copy is named after the SHA-1 of its content. Copies whose source has gone are deleted.
    /// </summary>
    public static IReadOnlyList<string> Refresh(IEnumerable<string> sourceFolders, string targetFolder)
    {
        Directory.CreateDirectory(targetFolder);

        var found = new Dictionary<string, (byte[] Bytes, int Width)>();
        foreach (var folder in sourceFolders)
        {
            if (!Directory.Exists(folder)) continue;
            foreach (var path in EnumerateFiles(folder))
            {
                var bytes = TryRead(path);
                if (bytes is null) continue;
                var width = QualifyingWidth(bytes);
                if (width is null) continue;
                var name = Convert.ToHexString(SHA1.HashData(bytes)).ToLowerInvariant() + ".jpg";
                found.TryAdd(name, (bytes, width.Value));
            }
        }

        // Temp files are left behind by a crash mid-write; they are never photos.
        foreach (var leftover in EnumerateFiles(targetFolder, "*.tmp")) TryDelete(leftover);

        var copied = new HashSet<string>();
        foreach (var (name, photo) in found)
        {
            if (TryCopy(Path.Combine(targetFolder, name), photo.Bytes)) copied.Add(name);
        }

        // A stale copy that cannot be deleted now (open in the page, antivirus) is left for the next refresh.
        foreach (var existing in EnumerateFiles(targetFolder, "*.jpg").Where(f => f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)))
        {
            if (!found.ContainsKey(Path.GetFileName(existing))) TryDelete(existing);
        }

        return found
            .Where(p => copied.Contains(p.Key))
            .OrderBy(p => p.Value.Width >= UhdWidth ? 0 : 1)
            .ThenBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => p.Key)
            .ToList();
    }

    private static IEnumerable<string> EnumerateFiles(string folder, string pattern = "*")
    {
        try { return Directory.GetFiles(folder, pattern); }
        catch (IOException) { return []; }
        catch (UnauthorizedAccessException) { return []; }
    }

    // Writes to a temp name and moves it into place only when complete, so a failure part-way never leaves a
    // truncated <hash>.jpg that a later refresh would accept. An existing copy of the wrong length is rewritten.
    private static bool TryCopy(string copy, byte[] bytes)
    {
        var temp = copy + ".tmp";
        try
        {
            if (File.Exists(copy) && new FileInfo(copy).Length == bytes.Length) return true;
            File.WriteAllBytes(temp, bytes);
            File.Move(temp, copy, overwrite: true);
            return true;
        }
        catch (IOException) { TryDelete(temp); return false; }
        catch (UnauthorizedAccessException) { TryDelete(temp); return false; }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static byte[]? TryRead(string path)
    {
        try { return File.ReadAllBytes(path); }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    // Header-only decode (BitmapCacheOption.None). Unreadable means skipped: WIC can raise COMException and a
    // hostile header can raise OutOfMemoryException or OverflowException, so any exception here skips the file.
    private static int? QualifyingWidth(byte[] bytes)
    {
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.None).Frames[0];
            var (width, height) = (frame.PixelWidth, frame.PixelHeight);
            return width >= MinWidth && width > height ? width : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
