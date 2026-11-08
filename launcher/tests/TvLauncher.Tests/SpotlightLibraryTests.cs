using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TvLauncher.Backgrounds;

namespace TvLauncher.Tests;

public sealed class SpotlightLibraryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tvlauncher-spotlight-" + Guid.NewGuid().ToString("N"));
    private readonly string _source;
    private readonly string _target;

    public SpotlightLibraryTests()
    {
        _source = Path.Combine(_root, "source");
        _target = Path.Combine(_root, "target");
        Directory.CreateDirectory(_source);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    // Spotlight cache files have no extension. The fill byte makes each fixture's content, and so its hash, unique.
    private string WriteJpeg(string name, int width, int height, byte fill)
    {
        var pixels = new byte[width * height];
        Array.Fill(pixels, fill);
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Gray8, null, pixels, width);
        var encoder = new JpegBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        var path = Path.Combine(_source, name);
        using var stream = File.Create(path);
        encoder.Save(stream);
        return path;
    }

    [Fact]
    public void Refresh_KeepsLandscapeAtLeast1920Wide()
    {
        WriteJpeg("a", 1920, 1080, 10);
        WriteJpeg("b", 1280, 720, 20);

        var names = SpotlightLibrary.Refresh([_source], _target);

        Assert.Single(names);
        Assert.EndsWith(".jpg", names[0]);
        Assert.True(File.Exists(Path.Combine(_target, names[0])));
    }

    [Fact]
    public void Refresh_Puts3840WideFirst()
    {
        WriteJpeg("a", 1920, 1080, 10);
        WriteJpeg("b", 3840, 2160, 20);
        WriteJpeg("c", 2560, 1440, 30);

        var names = SpotlightLibrary.Refresh([_source], _target);

        Assert.Equal(3, names.Count);
        Assert.Equal(3840, WidthOf(names[0]));
        Assert.All(names.Skip(1), n => Assert.True(WidthOf(n) < 3840));
    }

    [Fact]
    public void Refresh_PortraitOnly_ReturnsEmpty()
    {
        WriteJpeg("a", 1080, 1920, 10);
        WriteJpeg("b", 2160, 3840, 20);

        var names = SpotlightLibrary.Refresh([_source], _target);

        Assert.Empty(names);
        Assert.Empty(Directory.GetFiles(_target));
    }

    [Fact]
    public void Refresh_SkipsUnreadableFiles()
    {
        var junk = new byte[200 * 1024];
        new Random(1).NextBytes(junk);
        File.WriteAllBytes(Path.Combine(_source, "junk"), junk);
        WriteJpeg("a", 1920, 1080, 10);

        var names = SpotlightLibrary.Refresh([_source], _target);

        Assert.Single(names);
    }

    [Fact]
    public void Refresh_RemovesStaleCopies()
    {
        var keep = WriteJpeg("keep", 1920, 1080, 10);
        var gone = WriteJpeg("gone", 3840, 2160, 20);
        Assert.Equal(2, SpotlightLibrary.Refresh([_source], _target).Count);

        File.Delete(gone);
        var names = SpotlightLibrary.Refresh([_source], _target);

        Assert.Single(names);
        Assert.Equal(names, Directory.GetFiles(_target).Select(Path.GetFileName));
        Assert.True(File.Exists(keep));
    }

    [Fact]
    public void Refresh_MissingSourceFolder_IsNotAnError()
    {
        WriteJpeg("a", 1920, 1080, 10);

        var names = SpotlightLibrary.Refresh([Path.Combine(_root, "does-not-exist"), _source], _target);

        Assert.Single(names);
    }

    [Fact]
    public void Refresh_CopyFailureForOneFile_StillReturnsTheOthers()
    {
        var blocked = WriteJpeg("blocked", 3840, 2160, 20);
        WriteJpeg("fine", 1920, 1080, 10);
        // A directory squatting on the target name makes the copy throw UnauthorizedAccessException.
        var blockedName = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(File.ReadAllBytes(blocked))).ToLowerInvariant() + ".jpg";
        Directory.CreateDirectory(Path.Combine(_target, blockedName));

        var names = SpotlightLibrary.Refresh([_source], _target);

        Assert.Single(names);
        Assert.NotEqual(blockedName, names[0]);
        Assert.True(File.Exists(Path.Combine(_target, names[0])));
    }

    [Fact]
    public void Refresh_DeleteFailure_DoesNotThrow()
    {
        WriteJpeg("a", 1920, 1080, 10);
        Directory.CreateDirectory(_target);
        var locked = Path.Combine(_target, "locked.jpg");
        var other = Path.Combine(_target, "other.jpg");
        File.WriteAllBytes(other, [1, 2, 3]);
        using var hold = new FileStream(locked, FileMode.Create, FileAccess.ReadWrite, FileShare.None);

        var names = SpotlightLibrary.Refresh([_source], _target);

        Assert.Single(names);
        Assert.True(File.Exists(locked));
        Assert.False(File.Exists(other));
    }

    [Fact]
    public void Refresh_LeftoverTempFile_IsNotReturnedAndIsCleanedUp()
    {
        var source = WriteJpeg("a", 1920, 1080, 10);
        var bytes = File.ReadAllBytes(source);
        var name = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(bytes)).ToLowerInvariant() + ".jpg";
        Directory.CreateDirectory(_target);
        File.WriteAllBytes(Path.Combine(_target, name + ".tmp"), bytes[..100]);
        File.WriteAllBytes(Path.Combine(_target, "orphan.jpg.tmp"), [1, 2, 3]);

        var names = SpotlightLibrary.Refresh([_source], _target);

        Assert.Equal([name], names);
        Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(_target, name)));
        Assert.Empty(Directory.GetFiles(_target, "*.tmp"));
    }

    [Fact]
    public void Refresh_TruncatedExistingCopy_IsRewritten()
    {
        var source = WriteJpeg("a", 1920, 1080, 10);
        var bytes = File.ReadAllBytes(source);
        var name = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(bytes)).ToLowerInvariant() + ".jpg";
        Directory.CreateDirectory(_target);
        File.WriteAllBytes(Path.Combine(_target, name), bytes[..100]);

        var names = SpotlightLibrary.Refresh([_source], _target);

        Assert.Equal([name], names);
        Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(_target, name)));
    }

    private int WidthOf(string name)
    {
        using var stream = File.OpenRead(Path.Combine(_target, name));
        return BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.None).Frames[0].PixelWidth;
    }
}
