using TvLauncher.Apps;

namespace TvLauncher.Tests;

public sealed class FrontAtStartTests
{
    private static readonly TimeSpan Tick = FrontAtStart.AskEvery;

    [Fact]
    public void InFrontAtOnce_HoldsWithoutAsking()
    {
        var time = new ManualTime();
        var front = new FrontAtStart(time);

        Assert.Equal(FrontStep.Wait, front.Next(inFront: true, visible: true));
        time.Advance(FrontAtStart.HoldFor);
        Assert.Equal(FrontStep.Held, front.Next(inFront: true, visible: true));
        Assert.Equal(0, front.Asks);
        Assert.Equal(TimeSpan.Zero, front.FirstInFrontAfter);
    }

    [Fact]
    public void Refused_AsksAgainUntilInFront()
    {
        var time = new ManualTime();
        var front = new FrontAtStart(time);

        Assert.Equal(FrontStep.Ask, front.Next(inFront: false, visible: true));
        time.Advance(Tick);
        Assert.Equal(FrontStep.Ask, front.Next(inFront: false, visible: true));
        time.Advance(Tick);
        Assert.Equal(FrontStep.Wait, front.Next(inFront: true, visible: true));

        Assert.Equal(2, front.Asks);
        Assert.Equal(Tick * 2, front.FirstInFrontAfter);
    }

    [Fact]
    public void LosingTheFront_StartsTheHoldAgain()
    {
        var time = new ManualTime();
        var front = new FrontAtStart(time);

        front.Next(inFront: true, visible: true);
        time.Advance(TimeSpan.FromSeconds(3));
        Assert.Equal(FrontStep.Ask, front.Next(inFront: false, visible: true));
        time.Advance(Tick);
        Assert.Equal(FrontStep.Wait, front.Next(inFront: true, visible: true));
        time.Advance(FrontAtStart.HoldFor - TimeSpan.FromMilliseconds(1));
        Assert.Equal(FrontStep.Wait, front.Next(inFront: true, visible: true));
        time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal(FrontStep.Held, front.Next(inFront: true, visible: true));
        Assert.Equal(TimeSpan.Zero, front.FirstInFrontAfter);
    }

    [Fact]
    public void NeverInFront_GivesUpAfterAMinute()
    {
        var time = new ManualTime();
        var front = new FrontAtStart(time);

        Assert.Equal(FrontStep.Ask, front.Next(inFront: false, visible: true));
        time.Advance(FrontAtStart.GiveUpAfter - Tick);
        Assert.Equal(FrontStep.Ask, front.Next(inFront: false, visible: true));
        time.Advance(Tick);
        Assert.Equal(FrontStep.GiveUp, front.Next(inFront: false, visible: true));
        Assert.Equal(2, front.Asks);
        Assert.Null(front.FirstInFrontAfter);
    }

    [Fact]
    public void Hidden_StopsWithoutAsking()
    {
        var front = new FrontAtStart(new ManualTime());

        Assert.Equal(FrontStep.SteppedAside, front.Next(inFront: false, visible: false));
        Assert.Equal(0, front.Asks);
    }
}
