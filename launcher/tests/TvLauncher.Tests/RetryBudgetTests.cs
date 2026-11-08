using TvLauncher.SystemActions;

namespace TvLauncher.Tests;

public sealed class RetryBudgetTests
{
    private readonly ManualTime _time = new();

    [Fact]
    public void WebViewRecovery_IsThreeTriesInFiveMinutes()
    {
        Assert.Equal(3, RetryBudget.WebViewRecoveryTries);
        Assert.Equal(TimeSpan.FromMinutes(5), RetryBudget.WebViewRecoveryWindow);
    }

    [Fact]
    public void TryUse_FourthTryWithinTheWindow_IsRefused()
    {
        var budget = new RetryBudget(3, TimeSpan.FromMinutes(5), _time);

        Assert.True(budget.TryUse());
        _time.Advance(TimeSpan.FromMinutes(1));
        Assert.True(budget.TryUse());
        _time.Advance(TimeSpan.FromMinutes(1));
        Assert.True(budget.TryUse());
        Assert.Equal(3, budget.Used);
        _time.Advance(TimeSpan.FromMinutes(2));

        Assert.False(budget.TryUse());
    }

    [Fact]
    public void TryUse_TriesOlderThanTheWindow_NoLongerCount()
    {
        // A browser crash every few hours is recovered every time; only a burst gives up.
        var budget = new RetryBudget(3, TimeSpan.FromMinutes(5), _time);
        for (var i = 0; i < 3; i++)
            Assert.True(budget.TryUse());

        _time.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromTicks(1));

        Assert.True(budget.TryUse());
        Assert.Equal(1, budget.Used);
    }
}
