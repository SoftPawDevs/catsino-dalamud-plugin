using System.Reflection;
using Catsino.Plugin.Contracts;
using Catsino.Plugin.Ui;
using NVorbis;

namespace Catsino.Plugin.Tests;

// The roulette art and sound ship inside the plugin binary. A missing or unreadable asset would only show
// up as a silent, blank wheel in game, so the shipped resources are checked here instead.
public sealed class RouletteAssetTests
{
    private static readonly Assembly Plugin = typeof(RouletteSounds).Assembly;

    [Theory]
    [InlineData("roulette_base.png")]
    [InlineData("roulette_wheel.png")]
    [InlineData("roulette_pill1.png")]
    [InlineData("roulette_pill2.png")]
    [InlineData("roulette_spin.ogg")]
    [InlineData("roulette_stop.ogg")]
    public void Every_roulette_asset_is_embedded(string file)
    {
        Assert.Contains(Plugin.GetManifestResourceNames(), name => name.EndsWith($".{file}", StringComparison.Ordinal));
    }

    // The spin clip's length IS the length of the spin: the backend's deadline, the browser animation and
    // the plugin animation all run for SpinMilliseconds. Replacing the audio with a clip of a different
    // length without moving that constant would leave the wheel and the sound out of step, so this pins them
    // together (with a small tolerance, since a re-encode shifts the tail by a few milliseconds).
    [Fact]
    public void Spin_clip_is_exactly_as_long_as_the_spin_it_scores()
    {
        Assert.Equal(RouletteBetDefaults.SpinMilliseconds / 1000d, ClipSeconds("roulette_spin.ogg"), 1);
    }

    [Fact]
    public void Stop_clip_is_short_enough_to_finish_inside_the_result_window()
    {
        var seconds = ClipSeconds("roulette_stop.ogg");
        Assert.InRange(seconds, 0.1, RouletteBetDefaults.ResultsVisibleSeconds);
    }

    private static double ClipSeconds(string file)
    {
        var resource = Assert.Single(Plugin.GetManifestResourceNames(), name => name.EndsWith($".{file}", StringComparison.Ordinal));
        using var stream = Plugin.GetManifestResourceStream(resource)!;
        using var reader = new VorbisReader(stream);
        return reader.TotalTime.TotalSeconds;
    }
}
