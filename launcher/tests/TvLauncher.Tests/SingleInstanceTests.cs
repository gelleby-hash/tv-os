using TvLauncher.SystemActions;

namespace TvLauncher.Tests;

public sealed class SingleInstanceTests
{
    private readonly string _name = @"Local\TvLauncherTest-" + Guid.NewGuid().ToString("N");

    [Fact]
    public void TryAcquire_Twice_SecondReturnsNull()
    {
        using var first = SingleInstance.TryAcquire(_name);
        var second = SingleInstance.TryAcquire(_name);

        Assert.NotNull(first);
        Assert.Null(second);
    }

    [Fact]
    public void TryAcquire_AfterFirstIsDisposed_Succeeds()
    {
        SingleInstance.TryAcquire(_name)!.Dispose();

        using var again = SingleInstance.TryAcquire(_name);

        Assert.NotNull(again);
    }
}
