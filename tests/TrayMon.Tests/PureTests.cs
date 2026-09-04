using TrayMon;
using Xunit;

namespace TrayMon.Tests;

public sealed class GuidPoolTests
{
	private static Guid[] Pool(int n) =>
		Enumerable.Range(1, n).Select(i => new Guid($"6f2a1c40-9d3b-4f7e-a1c2-7c9e5b0000{i:X2}")).ToArray();

	[Fact]
	public void SameKeyKeepsItsGuid()
	{
		var pool = new GuidPool("тест", Pool(4));
		Assert.Equal(pool.For("C:"), pool.For("C:"));
	}

	[Fact]
	public void DifferentKeysNeverShare()
	{
		var pool = new GuidPool("тест", Pool(4));
		var taken = new[] { "C:", "D:", "E:", "F:" }.Select(k => pool.For(k)).ToList();
		Assert.Equal(4, taken.Distinct().Count());
		Assert.All(taken, g => Assert.NotNull(g));
	}

	/// <summary>The failure this pool exists for: a USB stick appears and every letter shifts.</summary>
	[Fact]
	public void ANewLetterInTheMiddleDoesNotMoveAnybody()
	{
		var pool = new GuidPool("тома", Pool(4));
		var c = pool.For("C:");
		var e = pool.For("E:");
		var d = pool.For("D:");   // plugged in afterwards, sorts between the two
		Assert.Equal(c, pool.For("C:"));
		Assert.Equal(e, pool.For("E:"));
		Assert.NotEqual(c, d);
		Assert.NotEqual(e, d);
	}

	[Fact]
	public void OverflowIsRefusedAndCounted()
	{
		var pool = new GuidPool("тома", Pool(2));
		Assert.NotNull(pool.For("C:"));
		Assert.NotNull(pool.For("D:"));
		Assert.Null(pool.For("E:"));
		Assert.Equal(1, pool.Refused);
		Assert.Equal(2, pool.Used);
	}

	[Fact]
	public void ReleasedSlotGoesToTheNextSource()
	{
		var pool = new GuidPool("тома", Pool(1));
		var first = pool.For("C:");
		Assert.Null(pool.For("D:"));
		pool.Release("C:");
		Assert.Equal(first, pool.For("D:"));
	}
}

public sealed class StatsTests
{
	[Fact]
	public void NotReadyUntilThereAreTwoValues()
	{
		var stats = new Stats();
		Assert.False(stats.Ready);
		stats.Add(1);
		Assert.False(stats.Ready);
		stats.Add(2);
		Assert.True(stats.Ready);
	}

	[Fact]
	public void WindowIsMinAverageMax()
	{
		var stats = new Stats();
		foreach (var v in new double[] { 10, 20, 30 }) stats.Add(v);
		var (min, avg, max) = stats.Window();
		Assert.Equal(10, min);
		Assert.Equal(20, avg, 6);
		Assert.Equal(30, max);
	}

	/// <summary>The ring holds five minutes at a two-second tick and nothing older.</summary>
	[Fact]
	public void OldValuesFallOutOfTheRing()
	{
		var stats = new Stats();
		for (var i = 0; i < 150; i++) stats.Add(1000);
		for (var i = 0; i < 150; i++) stats.Add(5);
		var (min, avg, max) = stats.Window();
		Assert.Equal(5, min);
		Assert.Equal(5, max);
		Assert.Equal(5, avg, 6);
	}
}

public sealed class ScaleTests
{
	[Theory]
	[InlineData(70, 85)]    // CPU
	[InlineData(88, 95)]    // RAM
	[InlineData(50, 75)]    // UPS, on the inverted scale
	[InlineData(75, 90)]    // GPU
	[InlineData(55, 65)]    // disk behind RAID
	public void EveryMetricReachesYellowAtTheSamePlace(double warn, double crit)
	{
		// The whole point of the summary icon: its colour must be the worst colour in the tray,
		// whatever the thresholds are. A plain ratio to the critical threshold put these between
		// 67 and 93 instead.
		Assert.Equal(Scale.Yellow, Scale.Score(warn, warn, crit).Value, 6);
		Assert.Equal(Scale.Red, Scale.Score(crit, warn, crit).Value, 6);
	}

	[Fact]
	public void BelowTheWarningItIsProportional()
	{
		Assert.Equal(0, Scale.Score(0, 70, 85).Value, 6);
		Assert.Equal(Scale.Yellow / 2, Scale.Score(35, 70, 85).Value, 6);
	}

	[Fact]
	public void PastRedItKeepsGrowingButIsCapped()
	{
		Assert.True(Scale.Score(90, 70, 85) > Scale.Red);
		Assert.Equal(Scale.Ceiling, Scale.Score(1e9, 55, 65).Value, 6);
	}

	[Fact]
	public void NonsenseThresholdsScoreNothing()
	{
		Assert.Null(Scale.Score(50, 90, 80));
		Assert.Null(Scale.Score(50, 0, 0));
		Assert.Null(Scale.Score(null, 70, 85));
	}
}

public sealed class FormatTests
{
	/// <summary>
	/// The icon and the tooltip must agree. Math.Round rounds to even, ToString("0") rounds away
	/// from zero, and the two together drew 50 on a plate whose tooltip said 51 %.
	/// </summary>
	[Theory]
	[InlineData(50.5, "51")]
	[InlineData(84.5, "85")]
	[InlineData(0.5, "1")]
	[InlineData(1.4, "1")]
	public void WholeRoundsTheSameWayTheTooltipDoes(double value, string expected)
	{
		Assert.Equal(expected, Format.Whole(value));
		Assert.Equal(expected + "%", Format.Pct(Format.Round(value)));
	}

	[Fact]
	public void NoValueDrawsNothing() => Assert.Null(Format.Whole(null));

	[Fact]
	public void FanSpeedIsInThousands()
	{
		Assert.Equal("0.6", Format.Rpm(586));
		Assert.Equal("1.2", Format.Rpm(1240));
	}

	/// <summary>
	/// The megabits argument, checked rather than asserted: background chatter of 30 KB/s is 0 in
	/// both units, and the icon only lifts off at about 62 KB/s. Eight times more sensitive than
	/// megabytes is true; "30 KB/s is visible" was not.
	/// </summary>
	[Fact]
	public void MegabitsLiftOffEightTimesEarlierThanMegabytes()
	{
		Assert.Equal("0", Format.MbitWhole(30 * 1024 / 1e6));
		Assert.Equal("1", Format.MbitWhole(63 * 1024 / 1e6));
		Assert.Equal("0", Format.Mb(63 * 1024 / 1e6));
	}

	[Fact]
	public void OnlyAStandstillAlarms()
	{
		Assert.Equal(100, Format.Stalled(0));
		Assert.Equal(0, Format.Stalled(1));
	}
}
