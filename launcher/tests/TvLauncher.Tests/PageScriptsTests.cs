using System.Globalization;
using TvLauncher.Apps;

namespace TvLauncher.Tests;

public sealed class PageScriptsTests
{
    [Fact]
    public void IsPlaying_NeverPausesOrPlays()
    {
        // Reading must never change the player: Disney+'s player wedges on a direct pause, and play starts a video.
        Assert.DoesNotContain("pause(", PageScripts.IsPlaying);
        Assert.DoesNotContain("play(", PageScripts.IsPlaying);
        Assert.Contains("shadowRoot", PageScripts.IsPlaying);
    }

    [Fact]
    public void DirectPause_OnlyPauses()
    {
        Assert.Contains("v.pause()", PageScripts.DirectPause);
        Assert.DoesNotContain("play(", PageScripts.DirectPause);
        Assert.Contains("shadowRoot", PageScripts.DirectPause);
    }

    [Fact]
    public void HideScrollbars_HasTheThreeRulesAndTouchesNoMedia()
    {
        var script = PageScripts.HideScrollbars;

        Assert.Contains("scrollbar-width: none", script);
        Assert.Contains("::-webkit-scrollbar", script);
        Assert.Contains("display: none; width: 0; height: 0", script);
        // No root element exists yet when this runs (pagescripts.test.cjs runs the script against such a document).
        Assert.Contains("document.documentElement", script);
        Assert.Contains("MutationObserver", script);
        Assert.Contains("try", script);
        Assert.DoesNotContain("pause(", script);
        Assert.DoesNotContain("play(", script);
        Assert.DoesNotContain("video", script);
    }

    [Fact]
    public void ArrowNav_TakesTheArrowsFirstAndLeavesThePlayerAlone()
    {
        var script = PageScripts.ArrowNav;

        // Its own listener runs first (capture, added before the page's scripts) and the page never sees the key.
        Assert.Contains("window.addEventListener('keydown'", script);
        Assert.Contains("}, true);", script);
        Assert.Contains("ev.stopImmediatePropagation()", script);
        Assert.Contains("ev.preventDefault()", script);
        // The player's URL (seen in Disney+'s own links, 2026-10-30) keeps its keys. arrownav.test.cjs runs the rest.
        Assert.Contains("'/play/'", script);
        Assert.DoesNotContain("pause(", script);
        Assert.DoesNotContain("play(", script);
        Assert.DoesNotContain("video", script);
    }

    [Fact]
    public void PressButton_LabelIsAJsonString_AndTheVideoIsNeverTouched()
    {
        var script = PageScripts.PressButton("Pause \"now\" </script>");

        // pagescripts.test.cjs runs it against a fake page.
        Assert.Contains("const want = \"Pause \\u0022now\\u0022 \\u003C/script\\u003E\";", script);
        Assert.DoesNotContain("__LABEL__", script);
        Assert.Contains("shadowRoot", script);
        Assert.Contains("hit.click()", script);
        Assert.DoesNotContain("pause(", script);
        Assert.DoesNotContain("video", script);
    }

    [Fact]
    public void ExitWhenEmpty_IdIsAJsonString_AndThePageIsClosedOnce()
    {
        var script = PageScripts.ExitWhenEmpty("app\"Root</script>");

        // pagescripts.test.cjs runs it against a fake page.
        Assert.Contains("const id = \"app\\u0022Root\\u003C/script\\u003E\";", script);
        Assert.DoesNotContain("__ID__", script);
        Assert.Contains("MutationObserver", script);
        Assert.Contains("postMessage({ tvExit: 'empty' })", script);
        Assert.DoesNotContain("window.close()", script);
        Assert.DoesNotContain("pause(", script);
        Assert.DoesNotContain("video", script);
    }

    [Fact]
    public void HidePlayReady_RefusesOnlyPlayReadyAndTouchesNoMedia()
    {
        var script = PageScripts.HidePlayReady;

        // pagescripts.test.cjs runs it against a fake navigator.
        Assert.Contains("navigator.requestMediaKeySystemAccess", script);
        Assert.Contains("/playready/i", script);
        Assert.Contains("'NotSupportedError'", script);
        Assert.Contains("original.call(navigator,", script);
        Assert.Contains("try", script);
        Assert.DoesNotContain("pause(", script);
        Assert.DoesNotContain("play(", script);
        Assert.DoesNotContain("video", script);
    }

    [Fact]
    public void Boost_InvariantCulture()
    {
        Assert.Contains("gain: 1.5", PageScripts.Boost(1.5));

        var before = CultureInfo.CurrentCulture;
        try
        {
            // A German Windows writes 1.5 as "1,5", which would make the script a syntax error.
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            Assert.Contains("gain: 1.5", PageScripts.Boost(1.5));
        }
        finally
        {
            CultureInfo.CurrentCulture = before;
        }
    }

    [Fact]
    public void Boost_RoutesThroughLimiter()
    {
        var script = PageScripts.Boost(4);

        Assert.Contains("createMediaElementSource", script);
        Assert.Contains("threshold.value = -3", script);
        Assert.Contains("play.apply(this, arguments)", script);
        Assert.Contains("gain: 4,", script);
        Assert.DoesNotContain("__GAIN__", script);
    }

    [Fact]
    public void SetBoostGain_Text()
    {
        Assert.Equal("window.__tvBoost ? window.__tvBoost.set(4) : null", PageScripts.SetBoostGain(4));

        var before = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            Assert.Equal("window.__tvBoost ? window.__tvBoost.set(1.5) : null", PageScripts.SetBoostGain(1.5));
        }
        finally
        {
            CultureInfo.CurrentCulture = before;
        }
    }
}
