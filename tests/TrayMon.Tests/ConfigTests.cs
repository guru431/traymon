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
}
