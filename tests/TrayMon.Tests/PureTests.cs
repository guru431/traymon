using TrayMon;
using Xunit;

namespace TrayMon.Tests;

/// <summary>A settings file in memory, so the pool's persistence can be checked without one.</summary>
internal sealed class FakeGuidStore : IGuidStore
{
	private readonly Dictionary<string, Dictionary<string, Guid>> _pools = new(StringComparer.Ordinal);

	public bool TryGet(string pool, string key, out Guid guid)
	{
		guid = Guid.Empty;
		return _pools.TryGetValue(pool, out var table) && table.TryGetValue(key, out guid);
	}

	public string OwnerOf(string pool, Guid guid)
	{
		if (!_pools.TryGetValue(pool, out var table)) return null;
		foreach (var (key, value) in table)
			if (value == guid) return key;
		return null;
	}

	public void Set(string pool, string key, Guid guid)
	{
		if (!_pools.TryGetValue(pool, out var table)) _pools[pool] = table = new Dictionary<string, Guid>(StringComparer.Ordinal);
		table[key] = guid;
	}

	public void Remove(string pool, string key)
	{
		if (_pools.TryGetValue(pool, out var table)) table.Remove(key);
	}
}

public sealed class GuidPoolTests
{
	private static Guid[] Pool(int n) =>
		Enumerable.Range(1, n).Select(i => new Guid($"6f2a1c40-9d3b-4f7e-a1c2-7c9e5b0000{i:X2}")).ToArray();

	[Fact]
	public void SameKeyKeepsItsGuid()
	{
		var pool = new GuidPool("тест", "test", Pool(4));
		Assert.Equal(pool.For("C:"), pool.For("C:"));
	}

	[Fact]
	public void DifferentKeysNeverShare()
	{
		var pool = new GuidPool("тест", "test", Pool(4));
		var taken = new[] { "C:", "D:", "E:", "F:" }.Select(k => pool.For(k)).ToList();
		Assert.Equal(4, taken.Distinct().Count());
		Assert.All(taken, g => Assert.NotNull(g));
	}

	/// <summary>The failure this pool exists for: a USB stick appears and every letter shifts.</summary>
	[Fact]
	public void ANewLetterInTheMiddleDoesNotMoveAnybody()
	{
		var pool = new GuidPool("тома", "vol", Pool(4));
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
		var pool = new GuidPool("тома", "vol", Pool(2));
		Assert.NotNull(pool.For("C:"));
		Assert.NotNull(pool.For("D:"));
		Assert.Null(pool.For("E:"));
		Assert.Equal(1, pool.Refused);
		Assert.Equal(2, pool.Used);
	}

	[Fact]
	public void ReleasedSlotGoesToTheNextSource()
	{
		var pool = new GuidPool("тома", "vol", Pool(1));
		var first = pool.For("C:");
		Assert.Null(pool.For("D:"));
		pool.Release("C:");
		Assert.Equal(first, pool.For("D:"));
	}

	/// <summary>
	/// The failure a pool alone cannot prevent: the table was rebuilt from scratch at every start
	/// and filled in the order sources first answered, so a restart without A gave A's GUID — and
	/// with it A's tray position and visibility — to B.
	/// </summary>
	[Fact]
	public void AssignmentSurvivesARestartWithADeviceMissing()
	{
		var store = new FakeGuidStore();
		var first = new GuidPool("тома", "vol", Pool(4), store);
		var a = first.For("A-adapter");
		var b = first.For("B-adapter");
		Assert.NotEqual(a, b);

		// New process, same settings file, A is not there any more.
		var second = new GuidPool("тома", "vol", Pool(4), store);
		Assert.Equal(b, second.For("B-adapter"));
		// And A keeps its own when it comes back.
		Assert.Equal(a, second.For("A-adapter"));
	}

	/// <summary>A remembered assignment must not starve a device that is here now.</summary>
	[Fact]
	public void RememberedSlotsDoNotOutrankLiveSources()
	{
		var store = new FakeGuidStore();
		var first = new GuidPool("тома", "vol", Pool(2), store);
		first.For("gone-1");
		first.For("gone-2");

		var second = new GuidPool("тома", "vol", Pool(2), store);
		Assert.NotNull(second.For("here-1"));
		Assert.NotNull(second.For("here-2"));
	}

	/// <summary>Releasing a slot forgets it, or the pool would fill up with names from March.</summary>
	[Fact]
	public void ReleaseAlsoForgetsTheRememberedAssignment()
	{
		var store = new FakeGuidStore();
		var pool = new GuidPool("тома", "vol", Pool(1), store);
		var only = pool.For("C:");
		pool.Release("C:");
		Assert.Null(store.OwnerOf("vol", only.Value));
		Assert.Equal(only, pool.For("D:"));
	}
}

public sealed class StatsTests
{
	private const long Now = 1_000_000;

	[Fact]
	public void NoRangeUntilThereAreTwoValues()
	{
		var stats = new Stats();
		Assert.Null(stats.Window(Now));
		stats.Add(1, Now);
		Assert.Null(stats.Window(Now));
		stats.Add(2, Now);
		Assert.NotNull(stats.Window(Now));
	}

	[Fact]
	public void WindowIsMinAverageMax()
	{
		var stats = new Stats();
		foreach (var v in new double[] { 10, 20, 30 }) stats.Add(v, Now);
		var (min, avg, max) = stats.Window(Now).Value;
		Assert.Equal(10, min);
		Assert.Equal(20, avg, 6);
		Assert.Equal(30, max);
	}

	/// <summary>
	/// The window is five minutes of wall clock, not 150 readings. The ring alone meant "5 мин"
	/// covered 25 minutes at TickMs = 10000 and an hour and a quarter through a locked session, and
	/// an extreme from before a long gap stayed in the line.
	/// </summary>
	[Fact]
	public void OldValuesFallOutByTime()
	{
		var stats = new Stats();
		stats.Add(1000, Now);
		stats.Add(1000, Now + 1000);
		stats.Add(5, Now + Stats.WindowMs + 2000);
		stats.Add(6, Now + Stats.WindowMs + 3000);
		var (min, avg, max) = stats.Window(Now + Stats.WindowMs + 3000).Value;
		Assert.Equal(5, min);
		Assert.Equal(6, max);
		Assert.Equal(5.5, avg, 6);
	}

	/// <summary>And the ring is still a ring: 150 readings inside the window keep the newest.</summary>
	[Fact]
	public void TheRingStillWrapsWhenEverythingIsFresh()
	{
		var stats = new Stats();
		for (var i = 0; i < 150; i++) stats.Add(1000, Now + i);
		for (var i = 0; i < 150; i++) stats.Add(5, Now + 150 + i);
		var (min, avg, max) = stats.Window(Now + 300).Value;
		Assert.Equal(5, min);
		Assert.Equal(5, max);
		Assert.Equal(5, avg, 6);
	}
}

/// <summary>
/// The level machine. There used to be three of these — the renderer with hysteresis, the journal
/// with bare thresholds, the summary icon with a second hysteresis over a non-linear score — and
/// they disagreed in exactly the cases below.
/// </summary>
public sealed class AlarmTests
{
	[Fact]
	public void RisesAtOnceAndFallsWithAMargin()
	{
		// CPU against 70/85, the built-in pair from the README.
		Assert.Equal(Alarm.Normal, Alarm.LevelOf(Alarm.Normal, 60, 70, 85, true));
		Assert.Equal(Alarm.Warning, Alarm.LevelOf(Alarm.Normal, 70, 70, 85, true));
		Assert.Equal(Alarm.Critical, Alarm.LevelOf(Alarm.Warning, 85, 70, 85, true));
		// 82.9 is below the red threshold but inside its margin: still red, and that is the whole
		// point — the plate and the journal used to disagree about precisely this value.
		Assert.Equal(Alarm.Critical, Alarm.LevelOf(Alarm.Critical, 82.9, 70, 85, true));
		Assert.Equal(Alarm.Warning, Alarm.LevelOf(Alarm.Critical, 80, 70, 85, true));
		Assert.Equal(Alarm.Warning, Alarm.LevelOf(Alarm.Warning, 69, 70, 85, true));
		Assert.Equal(Alarm.Normal, Alarm.LevelOf(Alarm.Warning, 60, 70, 85, true));
	}

	[Fact]
	public void NoValueIsNoData()
	{
		Assert.Equal(Alarm.Dead, Alarm.LevelOf(Alarm.Normal, null, 70, 85, true));
		Assert.Equal(Alarm.Dead, Alarm.LevelOf(Alarm.Critical, null, 70, 85, true));
	}

	/// <summary>
	/// The one the sentinel broke: a failing disk carries a severity of 1e9, and with the highlight
	/// switched off the thresholds were set to 1e9 as well — so <c>s >= crit</c> painted it red
	/// while the journal and the summary icon both called it normal.
	/// </summary>
	[Fact]
	public void HighlightOffIsNormalEvenForAFailingDisk()
	{
		Assert.Equal(Alarm.Normal, Alarm.LevelOf(Alarm.Critical, 1e9, 1e9, 1e9, false));
		Assert.Equal(Alarm.Normal, Alarm.LevelOf(Alarm.Critical, 1e9, 55, 65, false));
		Assert.Equal(Alarm.Critical, Alarm.LevelOf(Alarm.Normal, 1e9, 55, 65, true));
	}

	[Fact]
	public void NonsenseThresholdsNeverAlarm()
	{
		Assert.Equal(Alarm.Normal, Alarm.LevelOf(Alarm.Normal, 100, 90, 80, true));
		Assert.Equal(Alarm.Normal, Alarm.LevelOf(Alarm.Normal, 100, double.NaN, 80, true));
		Assert.Equal(Alarm.Normal, Alarm.LevelOf(Alarm.Normal, 100, 70, double.PositiveInfinity, true));
	}
}

public sealed class StorageTemperatureTests
{
	/// <summary>A descriptor as the storage driver fills it: header 24, one 16-byte entry.</summary>
	private static byte[] Answer(short celsius, ushort count = 1, uint? size = null)
	{
		var buffer = new byte[StorageTemperature.HeaderSize + StorageTemperature.EntrySize];
		BitConverter.GetBytes(1u).CopyTo(buffer, 0);                                  // Version
		BitConverter.GetBytes(size ?? (uint)buffer.Length).CopyTo(buffer, 4);          // Size
		BitConverter.GetBytes((short)80).CopyTo(buffer, 8);                            // Critical
		BitConverter.GetBytes((short)70).CopyTo(buffer, 10);                           // Warning
		BitConverter.GetBytes(count).CopyTo(buffer, 12);                               // InfoCount
		BitConverter.GetBytes((ushort)0).CopyTo(buffer, StorageTemperature.HeaderSize); // Index
		BitConverter.GetBytes(celsius).CopyTo(buffer, StorageTemperature.HeaderSize + 2);
		return buffer;
	}

	/// <summary>
	/// The defect this exists for: the header was taken as 16 and the temperature read from
	/// offset 18, which is inside the reserved area — a buffer carrying a real 47 °C decoded as 0.
	/// </summary>
	[Fact]
	public void ReadsTheCompositeTemperature()
	{
		var buffer = Answer(47);
		Assert.Equal(47, StorageTemperature.Parse(buffer, buffer.Length));
	}

	[Fact]
	public void NoEntriesMeansNoTemperature()
	{
		var buffer = Answer(47, count: 0);
		Assert.Null(StorageTemperature.Parse(buffer, buffer.Length));
	}

	[Fact]
	public void ShortOrLyingAnswersAreRefused()
	{
		var buffer = Answer(47);
		Assert.Null(StorageTemperature.Parse(buffer, 20));                     // truncated
		Assert.Null(StorageTemperature.Parse(Answer(47, size: 8), buffer.Length));   // Size too small
		Assert.Null(StorageTemperature.Parse(Answer(47, size: 4096), buffer.Length)); // Size too big
		Assert.Null(StorageTemperature.Parse(null, 40));
	}

	[Fact]
	public void ImpossibleTemperaturesAreNotReadings()
	{
		Assert.Null(StorageTemperature.Parse(Answer(short.MinValue), StorageTemperature.HeaderSize + StorageTemperature.EntrySize));
		Assert.Null(StorageTemperature.Parse(Answer(250), StorageTemperature.HeaderSize + StorageTemperature.EntrySize));
	}
}

public sealed class RedactorTests
{
	[Fact]
	public void HidesEveryOccurrenceWhateverTheCase()
	{
		var hide = new Redactor();
		hide.Hide("SYNTHETICSERIAL123", "<serial>");
		var text = hide.Clean("disk.raid.syntheticserial123 — SYNTHETICSERIAL123");
		Assert.Equal("disk.raid.<serial> — <serial>", text);
	}

	/// <summary>A profile path contains the user name; replacing the name first leaves half a path.</summary>
	[Fact]
	public void LongerValuesGoFirst()
	{
		var hide = new Redactor();
		hide.Hide("alice", "<user>");
		hide.Hide(@"C:\Users\alice", "<profile>");
		Assert.Equal(@"<profile>\Desktop", hide.Clean(@"C:\Users\alice\Desktop"));
	}

	[Fact]
	public void ShortAndEmptyValuesAreIgnored()
	{
		var hide = new Redactor();
		hide.Hide("ab", "<x>");
		hide.Hide("", "<x>");
		hide.Hide(null, "<x>");
		Assert.Equal("ab cab", hide.Clean("ab cab"));
	}
}

public sealed class CsvTests
{
	[Fact]
	public void QuotesOnlyWhatNeedsIt()
	{
		Assert.Equal("cpu", Csv.Field("cpu"));
		Assert.Equal("", Csv.Field(null));
		Assert.Equal("\"net.Intel; 2.5GbE\"", Csv.Field("net.Intel; 2.5GbE"));
		Assert.Equal("\"say \"\"hi\"\"\"", Csv.Field("say \"hi\""));
		Assert.Equal("\"two\nlines\"", Csv.Field("two\nlines"));
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

	/// <summary>
	/// Five digits do not fit a 16-pixel plate, and "пятизначных не бывает" was only true of free
	/// space: 10 Gbit/s is 10000 Mbit/s, and that was drawn off both edges of the icon.
	/// </summary>
	[Fact]
	public void FourDigitsAreTheLimitAndTheUnitMovesUp()
	{
		Assert.False(Format.TooWide(9999));
		Assert.False(Format.TooWide(9999.4));
		Assert.True(Format.TooWide(9999.5));    // rounds away from zero, like everything drawn
		Assert.True(Format.TooWide(10000));
		Assert.Equal("10.0", Format.PerThousand(10000));
		Assert.Equal("12.5", Format.PerThousand(12500));
	}
}

/// <summary>
/// Keys of the directly attached disks. The ordinal used to follow the enumeration order, so two
/// drives of the same model swapped their "#2" — and their settings and tray positions with it —
/// whenever the storage stack listed them the other way round.
/// </summary>
public sealed class DiskKeyTests
{
	private static DiskReading Disk(string model, string serial) =>
		new(model, 40, null, null) { Serial = serial };

	[Fact]
	public void IdenticalModelsAreOrderedBySerial()
	{
		var forwards = TrayApp.DiskKeys(new List<DiskReading>
			{ Disk("WDC WD10", "SN-B"), Disk("WDC WD10", "SN-A") });
		var backwards = TrayApp.DiskKeys(new List<DiskReading>
			{ Disk("WDC WD10", "SN-A"), Disk("WDC WD10", "SN-B") });

		// SN-A is the plain model, SN-B is #2, whichever order they were enumerated in.
		Assert.Equal("WDC WD10#2", forwards[0]);
		Assert.Equal("WDC WD10", forwards[1]);
		Assert.Equal("WDC WD10", backwards[0]);
		Assert.Equal("WDC WD10#2", backwards[1]);
	}

	[Fact]
	public void DifferentModelsKeepTheirNames()
	{
		var keys = TrayApp.DiskKeys(new List<DiskReading>
			{ Disk("Samsung 990", "S1"), Disk("WDC WD10", "S2") });
		Assert.Equal(new[] { "Samsung 990", "WDC WD10" }, keys);
	}
}

/// <summary>
/// Joining the storage driver's temperatures with the sensor library's wear figures. Three
/// separate defects lived here, and all three are about losing or misattributing a disk.
/// </summary>
public sealed class DiskMergeTests
{
	private static DiskReading Driver(string model, double temp) => new(model, temp, null, null);

	private static DiskReading Library(string model, double temp, double wear) =>
		new(model, temp, wear, 100 - wear);

	[Fact]
	public void WearIsTakenFromTheLibraryWhenTheMatchIsUnambiguous()
	{
		var merged = Program.Merge(
			new List<DiskReading> { Driver("Samsung SSD 990 PRO 2TB", 47) },
			new List<DiskReading> { Library("Samsung SSD 990 PRO", 46, 3) });
		var only = Assert.Single(merged);
		Assert.Equal(47, only.Temp);          // the driver's temperature wins
		Assert.Equal(3, only.WearPercent);    // the library's wear figure is carried over
	}

	/// <summary>
	/// Two identical models: one wear figure must not be handed to both, and the dictionary keyed
	/// by model must not throw one of them away.
	/// </summary>
	[Fact]
	public void IdenticalModelsGetTheirOwnWearOrNone()
	{
		var merged = Program.Merge(
			new List<DiskReading> { Driver("WDC WD10", 40), Driver("WDC WD10", 44) },
			new List<DiskReading> { Library("WDC WD10", 40, 90) });
		Assert.Equal(2, merged.Count);
		// Ambiguous, so neither is given the wear figure — and neither disk disappears.
		Assert.All(merged, d => Assert.Null(d.WearPercent));
	}

	/// <summary>
	/// The disappearing act: one drive answering the storage query used to delete every disk that
	/// only the sensor library could see.
	/// </summary>
	[Fact]
	public void LibraryOnlyDisksSurvive()
	{
		var merged = Program.Merge(
			new List<DiskReading> { Driver("Samsung SSD 990 PRO 2TB", 47) },
			new List<DiskReading> { Library("Intel SSDPEKNW010T8", 55, 12) });
		Assert.Equal(2, merged.Count);
		Assert.Contains(merged, d => d.Name == "Intel SSDPEKNW010T8" && d.WearPercent == 12);
	}

	[Fact]
	public void WithNoDriverAnswerTheLibraryListIsUsedAsIs()
	{
		var library = new List<DiskReading> { Library("Intel SSDPEKNW010T8", 55, 12) };
		Assert.Same(library, Program.Merge(new List<DiskReading>(), library));
	}
}
