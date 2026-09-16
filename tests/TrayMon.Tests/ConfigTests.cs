using TrayMon;
using Xunit;

namespace TrayMon.Tests;

/// <summary>
/// TrayMon.json is a file the README invites people to edit by hand, and the program overwrites
/// it whole on the next menu click. Every one of these is a way settings were lost.
///
/// All of them touch the same real path (Config.Path is next to the assembly), so they live in
/// one class: xUnit runs tests within a class one at a time.
/// </summary>
public sealed class ConfigTests : IDisposable
{
	private readonly string _saved;
	private readonly string _savedBad;

	public ConfigTests()
	{
		// Anything already there belongs to somebody else; put it back afterwards.
		_saved = File.Exists(Config.Path) ? File.ReadAllText(Config.Path) : null;
		_savedBad = File.Exists(Config.Path + ".bad") ? File.ReadAllText(Config.Path + ".bad") : null;
		Clean();
	}

	public void Dispose()
	{
		Clean();
		if (_saved is not null) File.WriteAllText(Config.Path, _saved);
		if (_savedBad is not null) File.WriteAllText(Config.Path + ".bad", _savedBad);
	}

	private static void Clean()
	{
		foreach (var path in new[] { Config.Path, Config.Path + ".bad", Config.Path + ".tmp" })
			if (File.Exists(path)) File.Delete(path);
	}

	[Fact]
	public void RoundTripsWhatTheMenuWrites()
	{
		var config = new Config();
		var cpu = config.For("cpu");
		cpu.Color = "#1C5CA8";
		cpu.Label = "Процессор";
		cpu.Warn = 60;
		cpu.Crit = 80;
		cpu.Alerts = false;
		config.Ups.Host = "192.0.2.10";
		config.Net.NotPhysical = new List<string> { "vEthernet" };
		Assert.True(config.Save(out var error), error);

		var read = Config.Load();
		Assert.Null(read.LoadError);
		Assert.Equal("#1C5CA8", read.Get("cpu").Color);
		Assert.Equal("Процессор", read.Get("cpu").Label);
		Assert.Equal(60, read.Get("cpu").Warn);
		Assert.False(read.Get("cpu").Alerts);
		Assert.Equal("192.0.2.10", read.Ups.Host);
		Assert.Equal(new[] { "vEthernet" }, read.Net.Filters);
	}

	/// <summary>An entry holding nothing but defaults is dropped, so the file stays readable.</summary>
	[Fact]
	public void TidyDropsAnEntryThatSaysNothing()
	{
		var config = new Config();
		config.For("ram").Color = "#408080";
		config.Tidy("ram");
		Assert.NotNull(config.Get("ram").Color);

		config.For("gpu.0");
		config.Tidy("gpu.0");
		Assert.Same(config.Get("gpu.0"), config.Get("never.seen"));   // both are the shared default
	}

	/// <summary>Switching the highlight off must not be forgotten by Tidy.</summary>
	[Fact]
	public void TidyKeepsAnEntryThatOnlyTurnsAlertsOff()
	{
		var config = new Config();
		config.For("cpu").Alerts = false;
		config.Tidy("cpu");
		Assert.False(config.Get("cpu").Alerts);
	}

	[Fact]
	public void ReadsAHandEditedFileThatIsNotPerfectJson()
	{
		// A lower-case key, a // comment and a trailing comma: three things a person writes and
		// three things the strict reader threw the whole file out for.
		File.WriteAllText(Config.Path, """
			{
			  // мои пороги
			  "icons": {
			    "cpu": { "enabled": true, "warn": 60, "crit": 80, },
			  },
			}
			""");
		var config = Config.Load();
		Assert.Null(config.LoadError);
		Assert.Equal(60, config.Get("cpu").Warn);
		Assert.Equal(80, config.Get("cpu").Crit);
	}

	[Fact]
	public void BrokenJsonIsKeptAsideAndReported()
	{
		File.WriteAllText(Config.Path, "{ this is not json at all");
		var config = Config.Load();
		Assert.NotNull(config.LoadError);
		Assert.True(File.Exists(Config.Path + ".bad"));
	}

	[Fact]
	public void AnExplicitNullSectionDoesNotTakeTheProgramDown()
	{
		File.WriteAllText(Config.Path, """{ "Icons": null, "Ups": null, "Net": null, "Log": null, "Tools": null }""");
		var config = Config.Load();
		Assert.Null(config.LoadError);
		Assert.NotNull(config.Icons);
		Assert.Equal("127.0.0.1", config.Ups.Host);
		Assert.NotEmpty(config.Net.Filters);
	}

	[Fact]
	public void AnExplicitNullIconEntryBecomesDefaults()
	{
		File.WriteAllText(Config.Path, """{ "Icons": { "cpu": null } }""");
		var config = Config.Load();
		Assert.Null(config.LoadError);
		Assert.True(config.Get("cpu").Enabled);
	}

	/// <summary>An empty pattern matches every adapter description; a null one used to throw.</summary>
	[Fact]
	public void EmptyAdapterPatternsAreDropped()
	{
		var net = new NetSettings { NotPhysical = new List<string> { "", "  ", null } };
		Assert.Equal(NetSettings.BuiltInNotPhysical, net.Filters);

		net.NotPhysical = new List<string> { "", "vEthernet" };
		Assert.Equal(new[] { "vEthernet" }, net.Filters);
	}

	[Fact]
	public void PollIntervalOnlyMovesUpwards()
	{
		var config = new Config { TickMs = 500 };
		Assert.Equal(2000, config.TickMs);
		config.TickMs = 10000;
		Assert.Equal(10000, config.TickMs);
		config.TickMs = 999999;
		Assert.Equal(10000, config.TickMs);
	}

	[Fact]
	public void LogIntervalHasAFloorNotACeiling()
	{
		var log = new LogSettings { EverySeconds = 5 };
		Assert.Equal(60, log.EverySeconds);   // refused, default kept
		log.EverySeconds = 3600;
		Assert.Equal(3600, log.EverySeconds);
	}

	[Fact]
	public void UpsSettingsKeepDefaultsAgainstBlanks()
	{
		var ups = new UpsSettings { Host = "  ", Community = "", Port = 0, TimeoutMs = 1 };
		Assert.Equal("127.0.0.1", ups.Host);
		Assert.Equal("public", ups.Community);
		Assert.Equal(161, ups.Port);
		Assert.Equal(1500, ups.TimeoutMs);
	}

	[Fact]
	public void SaveLeavesNoTemporaryFileBehind()
	{
		Assert.True(new Config().Save(out _));
		Assert.False(File.Exists(Config.Path + ".tmp"));
	}

	/// <summary>
	/// A pair with the red threshold below the yellow one used to load happily: the plate could
	/// turn red while Score() refused to rank it at all, so the summary icon left it out.
	/// </summary>
	[Fact]
	public void AnInvertedThresholdPairIsDropped()
	{
		File.WriteAllText(Config.Path, """{ "Icons": { "cpu": { "Color": "#112233", "Warn": 90, "Crit": 70 } } }""");
		var config = Config.Load();
		// A note, not an error: the file *was* read, and saying otherwise made the startup dialog
		// announce that every colour and threshold had been lost — and made a reload refuse to
		// apply a file whose only sin was one impossible pair.
		Assert.Null(config.LoadError);
		Assert.NotNull(config.LoadNote);
		Assert.Null(config.Get("cpu").Warn);
		Assert.Null(config.Get("cpu").Crit);
		Assert.Equal("#112233", config.Get("cpu").Color);
	}

	/// <summary>
	/// The regression that hit a live machine: before <c>Alerts</c> existed, switching the
	/// highlight off was written as <c>Warn = Crit = 1e9</c>, and files from those versions still
	/// carry it. Treating it as a broken value dropped it — which switched the highlight back *on*
	/// for four icons somebody had deliberately silenced, and threw the setting out of their file.
	/// </summary>
	[Fact]
	public void TheOldWayOfSwitchingTheHighlightOffIsMigratedNotDropped()
	{
		File.WriteAllText(Config.Path, """
			{ "Icons": {
			    "cpu":  { "Enabled": true, "Color": "#8000FF", "Warn": 1000000000, "Crit": 1000000000 },
			    "gpu.0": { "Enabled": true, "Warn": 1e9, "Crit": 1e9 } } }
			""");
		var config = Config.Load();

		Assert.Null(config.LoadError);
		Assert.Null(config.LoadNote);          // nothing to complain about — it was an instruction
		foreach (var id in new[] { "cpu", "gpu.0" })
		{
			Assert.False(config.Get(id).Alerts);   // the same meaning, in the current spelling
			Assert.Null(config.Get(id).Warn);
			Assert.Null(config.Get(id).Crit);
		}
		Assert.Equal("#8000FF", config.Get("cpu").Color);
		// And the file is rewritten in the new spelling when it is next saved.
		Assert.True(config.TakeDirty());
	}

	/// <summary>An explicit Alerts already in the file wins: migration must not overwrite it.</summary>
	[Fact]
	public void MigrationDoesNotOverrideAnExplicitAlertsFlag()
	{
		File.WriteAllText(Config.Path,
			"""{ "Icons": { "cpu": { "Alerts": true, "Warn": 1000000000, "Crit": 1000000000 } } }""");
		var config = Config.Load();
		Assert.True(config.Get("cpu").Alerts);
		Assert.Null(config.Get("cpu").Warn);
	}

	/// <summary>NaN and Infinity parse out of JSON and then compare false against everything.</summary>
	[Fact]
	public void NonFiniteThresholdsAreDropped()
	{
		File.WriteAllText(Config.Path, """{ "Icons": { "cpu": { "Warn": 1e400, "Crit": 1e401 } } }""");
		var config = Config.Load();
		Assert.Null(config.Get("cpu").Warn);
		Assert.Null(config.Get("cpu").Crit);
		Assert.False(Config.Sane(double.NaN));
		Assert.False(Config.Sane(double.PositiveInfinity));
		Assert.True(Config.Sane(null));
		Assert.True(Config.Sane(70));
	}

	/// <summary>Gigabytes left: the red threshold is the lower number, so the check is reversed.</summary>
	[Fact]
	public void GigabyteThresholdsAreValidatedTheOtherWayRound()
	{
		File.WriteAllText(Config.Path, """{ "Icons": { "free.C:": { "WarnGb": 5, "CritGb": 50 } } }""");
		var config = Config.Load();
		Assert.Null(config.LoadError);
		Assert.NotNull(config.LoadNote);
		Assert.Null(config.Get("free.C:").WarnGb);

		File.WriteAllText(Config.Path, """{ "Icons": { "free.C:": { "WarnGb": 50, "CritGb": 5 } } }""");
		config = Config.Load();
		Assert.Null(config.LoadError);
		Assert.Null(config.LoadNote);
		Assert.Equal(50, config.Get("free.C:").WarnGb);
		Assert.Equal(5, config.Get("free.C:").CritGb);
	}

	/// <summary>
	/// Read() is what the watcher and "Перечитать настройки" use. It must not move the file aside:
	/// an editor saves in more than one step, and an intermediate state used to cost the user their
	/// file name and the previous backup with it.
	/// </summary>
	[Fact]
	public void ReadDoesNotTouchABrokenFile()
	{
		File.WriteAllText(Config.Path, "{ half a file");
		var config = Config.Read();
		Assert.NotNull(config.LoadError);
		Assert.True(File.Exists(Config.Path));
		Assert.False(File.Exists(Config.Path + ".bad"));
	}

	/// <summary>The previous backup is not overwritten: two bad saves in a row used to leave only
	/// the second broken file and nothing else.</summary>
	[Fact]
	public void AnExistingBackupIsKept()
	{
		File.WriteAllText(Config.Path + ".bad", "the good old settings");
		File.WriteAllText(Config.Path, "{ broken again");
		var config = Config.Load();
		Assert.NotNull(config.LoadError);
		Assert.Equal("the good old settings", File.ReadAllText(Config.Path + ".bad"));
	}

	/// <summary>Which tray identity each source holds has to survive a restart.</summary>
	[Fact]
	public void SlotAssignmentsRoundTrip()
	{
		var config = new Config();
		var store = (IGuidStore)config;
		var guid = new Guid("6f2a1c40-9d3b-4f7e-a1c2-7c9e5b000051");
		store.Set("net", "Intel 2.5GbE", guid);
		Assert.True(config.TakeDirty());
		Assert.False(config.TakeDirty());
		Assert.True(config.Save(out var error), error);

		var read = (IGuidStore)Config.Load();
		Assert.True(read.TryGet("net", "Intel 2.5GbE", out var back));
		Assert.Equal(guid, back);
		Assert.Equal("Intel 2.5GbE", read.OwnerOf("net", guid));
	}

	/// <summary>Renaming an id carries the settings over — two ids had to change to become stable.</summary>
	[Fact]
	public void RenameMovesTheEntry()
	{
		var config = new Config();
		config.For("gpu.0").Color = "#112233";
		Assert.True(config.Rename("gpu.0", "gpu.a1b2c3d4"));
		Assert.Equal("#112233", config.Get("gpu.a1b2c3d4").Color);
		Assert.Null(config.Get("gpu.0").Color);
		// Nothing to move, and never over an entry that already exists.
		Assert.False(config.Rename("gpu.0", "gpu.a1b2c3d4"));
	}

	[Fact]
	public void TidyKeepsTheNewPerIconSettings()
	{
		var config = new Config();
		config.For("fan.Nuvoton/Fan #2").Stall = "zero-ok";
		config.Tidy("fan.Nuvoton/Fan #2");
		Assert.Equal("zero-ok", config.Get("fan.Nuvoton/Fan #2").Stall);

		config.For("ups").Required = true;
		config.Tidy("ups");
		Assert.True(config.Get("ups").Required);
	}

	[Fact]
	public void SensorProfileAndLogLimitAreReadBack()
	{
		File.WriteAllText(Config.Path, """
			{ "Sensors": { "UseSensorDriver": false },
			  "Log": { "Enabled": true, "MaxMb": 4 },
			  "Net": { "Include": ["vEthernet (LAN)"], "Bandwidth": { "Wi-Fi": 1200 } } }
			""");
		var config = Config.Load();
		Assert.Null(config.LoadError);
		Assert.False(config.Sensors.UseSensorDriver);
		Assert.Equal(4, config.Log.MaxMb);
		Assert.Equal(new[] { "vEthernet (LAN)" }, config.Net.Included);
		Assert.Equal(1200, config.Net.BandwidthFor("Intel Wi-Fi 6E AX211"));
		Assert.Null(config.Net.BandwidthFor("Realtek 2.5GbE"));
	}
}
