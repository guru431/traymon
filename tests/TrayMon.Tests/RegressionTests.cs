using System.Collections;
using System.Drawing;
using System.Reflection;
using System.Runtime.CompilerServices;
using TrayMon;
using Xunit;

namespace TrayMon.Tests;

/// <summary>Real tray state transitions without opening sensors, windows or the Windows log.</summary>
internal sealed class TrayState
{
	private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
	internal readonly TrayApp App = (TrayApp)RuntimeHelpers.GetUninitializedObject(typeof(TrayApp));
	internal readonly Readings Readings = new();
	internal readonly Config Config;
	internal readonly List<(string Text, bool Critical)> Events = new();

	internal TrayState(Config config = null)
	{
		Config = config ?? new Config();
		Set(App, "_config", Config);
		Set(App, "_r", Readings);
		Set(App, "_dry", true);
		Set(App, "_intervalMs", 2000);
		foreach (var name in new[] { "_slots", "_order", "_journal", "_neverExpected" })
		{
			var field = typeof(TrayApp).GetField(name, Instance);
			field.SetValue(App, Activator.CreateInstance(field.FieldType));
		}
		App.NotificationSink = (text, critical) => Events.Add((text, critical));
	}

	internal static object Get(object owner, string name) => owner.GetType().GetField(name, Instance).GetValue(owner);
	internal static void Set(object owner, string name, object value) => owner.GetType().GetField(name, Instance).SetValue(owner, value);
	internal object Call(string method, params object[] args) => typeof(TrayApp).GetMethod(method, Instance).Invoke(App, args);
	internal object Slot(string id, string metric = "CpuMetric")
	{
		var definition = typeof(TrayApp).GetField(metric, BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
		return Call("Slot", id, definition, Guid.NewGuid(), null);
	}
	internal object Find(string id) => ((IDictionary)Get(App, "_slots"))[id];
}

public sealed class RegressionTests
{
	[Theory]
	[InlineData(Alarm.Normal)]
	[InlineData(Alarm.Warning)]
	[InlineData(Alarm.Critical)]
	public void RequiredRecoveryEmitsExactlyOneEventEvenWhenCriticalNotificationsAreOff(int level)
	{
		var state = new TrayState();
		var slot = state.Slot("cpu");
		TrayState.Set(slot, "Settings", new IconSettings { Required = true, NotifyOnCritical = false });
		TrayState.Set(slot, "Level", Alarm.Dead);
		TrayState.Set(state.App, "_dry", false);
		state.Call("NoteLevelChange", slot, level);
		state.Call("NoteLevelChange", slot, level);
		var notification = Assert.Single(state.Events);
		Assert.Contains("данные снова есть", notification.Text);
		Assert.Equal(level == Alarm.Critical, notification.Critical);
	}

	[Fact]
	public void SummaryRetainsTheSourceLevelThroughRepaintAndReload()
	{
		var state = new TrayState();
		var cpu = state.Slot("cpu");
		TrayState.Set(cpu, "Severity", (double?)82.5);
		TrayState.Set(cpu, "Level", Alarm.Critical);
		TrayState.Set(cpu, "SeenTick", 0L);
		state.Call("ShowWorst");
		var worst = state.Find("worst");
		Assert.Equal(Alarm.Critical, TrayState.Get(worst, "Level"));
		void Repaint() => state.Call("Push", worst, TrayState.Get(worst, "LastText"), TrayState.Get(worst, "LastSeverity"), null);
		Repaint();
		state.Call("ApplySettings");
		Assert.Equal(Alarm.Critical, TrayState.Get(worst, "Level"));
		var settings = (IconSettings)TrayState.Get(worst, "Settings");
		// Do not mutate the shared default settings.
		settings = new IconSettings { Alerts = false };
		TrayState.Set(worst, "Settings", settings);
		Repaint();
		Assert.Equal(Alarm.Normal, TrayState.Get(worst, "Level"));
		settings.Alerts = true;
		settings.Enabled = false;
		Repaint();
		settings.Enabled = true;
		Repaint();
		Assert.Equal(Alarm.Critical, TrayState.Get(worst, "Level"));
	}

	[Fact]
	public void UpsUnknownConditionDoesNotAnnounceRecoveryOrEraseTheWarning()
	{
		var state = new TrayState();
		var ups = new UpsSensor(new UpsSettings());
		TrayState.Set(ups, "<Present>k__BackingField", true);
		TrayState.Set(state.App, "_ups", ups);
		var slot = state.Slot("ups", "UpsMetric");
		TrayState.Set(slot, "Settings", new IconSettings { Enabled = false, NotifyOnCritical = true });
		TrayState.Set(state.App, "_dry", false);
		void Read(int tick, UpsReading reading)
		{
			TrayState.Set(state.App, "_tick", (long)tick);
			TrayState.Set(state.App, "_upsVersion", tick);
			state.Readings.Ups = reading;
			state.Readings.UpsAt = Environment.TickCount64;
			state.Call("ShowUps");
		}
		Read(1, new UpsReading { Answered = true, Charge = 100, Status = 6, NeedsNewBattery = true });
		Assert.Single(state.Events);
		Read(2, new UpsReading { Answered = true, Charge = 100, Status = 1 });
		Assert.True((bool)TrayState.Get(state.App, "_lastBypass"));
		Assert.True((bool)TrayState.Get(state.App, "_lastReplace"));
		Assert.Equal(Alarm.Warning, TrayState.Get(slot, "Level"));
		Assert.Contains("неизвестно", (string)TrayState.Get(slot, "LastDetail"));
		Assert.Single(state.Events);
		Read(3, new UpsReading { Answered = true, Charge = 100, Status = 2, NeedsNewBattery = false });
		Assert.Equal(2, state.Events.Count);
		Assert.Contains("вышел из байпаса", state.Events[1].Text);
		Assert.Contains("больше не просит", state.Events[1].Text);
	}

	[Theory]
	[InlineData(null, null)]
	[InlineData(0d, null)]
	[InlineData(1d, false)]
	[InlineData(2d, true)]
	[InlineData(3d, null)]
	public void OnlyKnownBatteryReplacementCodesProveACondition(double? code, bool? expected) =>
		Assert.Equal(expected, UpsSensor.ReplacementState(code));

	[Fact]
	public void RediscoveryDoesNotMutateAnInflightRequestOrRaidList()
	{
		var ups = new UpsSensor(new UpsSettings());
		var requested = (List<string>)TrayState.Get(ups, "_oids");
		requested.RemoveAt(1);
		var sent = requested.ToArray();
		ups.Rediscover();
		Assert.Equal(sent, requested);
		Assert.Equal(1, TrayState.Get(ups, "_rediscover"));
		var hdd = new HddSensor();
		var devices = new List<(string, string)> { ("fake", "csmi") };
		TrayState.Set(hdd, "_devices", devices);
		hdd.Rediscover();
		Assert.Same(devices, TrayState.Get(hdd, "_devices"));
	}

	[Fact]
	public void RediscoveryDuringASnmpReplyDropsTheOidOfTheActualRequest()
	{
		UpsSensor ups = null;
		var seen = new List<string[]>();
		ups = new UpsSensor(new UpsSettings(), requested =>
		{
			seen.Add(requested.ToArray());
			if (seen.Count == 1)
			{
				ups.Rediscover();
				return new UpsSensor.SnmpReply { Error = 2, ErrorIndex = 2 };
			}
			return new UpsSensor.SnmpReply { Vars = new() };
		});
		var oids = (List<string>)TrayState.Get(ups, "_oids");
		var capacity = oids[0];
		var status = oids[2];
		oids.Clear();
		oids.AddRange(new[] { capacity, status });
		ups.Read(new Readings());
		Assert.Equal(new[] { capacity, status }, seen[0]);
		Assert.Equal(new[] { capacity }, seen[1]);
		ups.Read(new Readings());
		Assert.Equal(5, seen[2].Length);
	}

	[Fact]
	public void DiskLossAndRestartPreserveEachOwnersSettingsAndGuid()
	{
		var config = new Config();
		config.For("disk.Model@A111").Label = "first";
		config.For("disk.Model@B222").Warn = 55;
		// Ambiguous historical entries remain available, but cannot configure a new owner.
		config.For("disk.Model").Label = "legacy";
		config.For("disk.Model").Required = true;
		var state = new TrayState(config);
		Assert.False((bool)state.Call("Expect", "disk.Model"));
		void Setup(TrayState current) => TrayState.Set(current.App, "_diskPool",
			new GuidPool("test", "disk", Enumerable.Range(1, 4).Select(i => new Guid(i, 0, 0, new byte[8])).ToArray(), (IGuidStore)current.App));
		Setup(state);
		DiskReading Disk(string serial) => new("Model", 40, null, null) { Serial = serial };
		void Publish(TrayState current, params DiskReading[] disks)
		{
			current.Readings.Disks = disks.ToList();
			current.Readings.DisksAt = Environment.TickCount64;
			TrayState.Set(current.App, "_disksVersion", (int)TrayState.Get(current.App, "_disksVersion") + 1);
			current.Call("ShowDisks");
		}
		Publish(state, Disk("A111"), Disk("B222"));
		var a = state.Find("disk.Model@A111");
		var b = state.Find("disk.Model@B222");
		var aGuid = TrayState.Get(a, "Guid");
		var bGuid = TrayState.Get(b, "Guid");
		Publish(state, Disk("B222"));
		Assert.Equal(bGuid, TrayState.Get(state.Find("disk.Model@B222"), "Guid"));
		Assert.Equal(55, ((IconSettings)TrayState.Get(b, "Settings")).Warn);
		Publish(state, Disk("B222"), Disk("A111"));
		Assert.Equal(aGuid, TrayState.Get(a, "Guid"));
		Assert.Equal("first", ((IconSettings)TrayState.Get(a, "Settings")).Label);
		var restarted = new TrayState(config);
		Setup(restarted);
		Publish(restarted, Disk("B222"));
		Assert.Equal(bGuid, TrayState.Get(restarted.Find("disk.Model@B222"), "Guid"));
		Assert.Equal("legacy", config.Get("disk.Model").Label);
	}

	[Fact]
	public void PublicSnapshotNeverReadsOrCopiesLabelsIdentifiersOrFreeText()
	{
		var state = new TrayState();
		var secrets = new[] { "Private rack customer Q7", "AQ", @"X:\private\customer", "owner\\operator", "198.51.100.73", "SERIAL-ZYX" };
		foreach (var secret in secrets)
		{
			var slot = state.Slot("disk." + secret, "DiskMetric");
			TrayState.Set(slot, "Settings", new IconSettings { Label = secret });
			TrayState.Set(slot, "DefaultLabel", secret);
			TrayState.Set(slot, "LastDetail", secret);
			TrayState.Set(slot, "LastUnit", secret);
			TrayState.Set(slot, "LastText", secret);
			TrayState.Set(slot, "LastValue", (double?)42);
		}
		var text = state.App.PublicDiagnostics();
		Assert.All(secrets, secret => Assert.DoesNotContain(secret, text));
		Assert.Contains("42 °C", text);
		Assert.Contains("отрисовок", text);
	}

	[Fact]
	public void UncheckedExternalPathsRefuseUnlessTheInstallIsProvenWritable()
	{
		var safe = new Autostart.PathCheck(false, null, null);
		var unknown = new Autostart.PathCheck(false, null, "ACL or final-path resolution failed");
		var writable = new Autostart.PathCheck(true, "test-account", null);
		Assert.NotNull(Autostart.ExternalRefusal(safe, unknown));
		Assert.NotNull(Autostart.ExternalRefusal(unknown, safe));
		Assert.NotNull(Autostart.ExternalRefusal(safe, writable));
		Assert.Null(Autostart.ExternalRefusal(safe, safe));
		Assert.Null(Autostart.ExternalRefusal(writable, unknown));
	}

	[Fact]
	public void OneLostGpuRecoversWhileItsHealthyNeighbourKeepsAnswering()
	{
		long now = 0;
		var restarts = 0;
		var restored = false;
		GpuReading Card(string key, bool lost) => new(0, key, lost ? null : 20, lost ? null : 40, null, null, null, 0, 0)
			{ Key = key, LoadCode = lost ? 15 : 0, MemCode = lost ? 15 : 0, TempCode = lost ? 15 : 0 };
		using var gpu = new GpuSensor(() => new() { Card("A", !restored), Card("B", false) },
			() => { restarts++; }, () => now);
		var r = new Readings();
		for (var i = 0; i < 20; i++) { gpu.Read(r); now += 4000; }
		Assert.Equal(2, restarts);
		Assert.Equal("B", r.Gpus[1].Key);
		restored = true;
		gpu.Read(r);
		Assert.Equal(20, r.Gpus[0].Load);
		for (var i = 0; i < 20; i++) { now += 4000; gpu.Read(r); }
		Assert.Equal(2, restarts);
	}

	[Fact]
	public void ZeroRpmPolicyPairsEachFansDutyWithItsOwnTachometer()
	{
		Assert.False(GpuSensor.DrivenStop(0, 0) || GpuSensor.DrivenStop(1200, 40));
		Assert.True(GpuSensor.DrivenStop(0, 40) || GpuSensor.DrivenStop(1200, 0));
		Assert.False(GpuSensor.DrivenStop(0, null));
		Assert.False(GpuSensor.DrivenStop(null, 40));
	}

	[Theory]
	[InlineData(214, 170, 0)]
	[InlineData(196, 48, 32)]
	[InlineData(70, 70, 70)]
	[InlineData(255, 0, 255)]
	[InlineData(255, 0, 0)]
	public void AutoInkChoosesTheStrongerContrast(int red, int green, int blue)
	{
		var plate = Color.FromArgb(red, green, blue);
		var ink = TrayValueIcon.AutoInk(plate);
		var contrast = TrayValueIcon.Contrast(plate, ink);
		Assert.True(contrast >= 4.5);
		Assert.True(contrast >= TrayValueIcon.Contrast(plate, ink == Color.Black ? Color.White : Color.Black));
	}

	[Fact]
	public void EveryBuiltInPlateHasReadableAutomaticDigits()
	{
		var colors = typeof(TrayApp).GetFields(BindingFlags.Static | BindingFlags.NonPublic)
			.Where(field => field.FieldType == typeof(Color)).Select(field => (Color)field.GetValue(null));
		foreach (var color in colors)
			Assert.True(TrayValueIcon.Contrast(color, TrayValueIcon.AutoInk(color)) >= 4.5, color.ToString());
	}

	[Fact]
	public void RightClickSettingsDoesNotToggleTheVisibilityCheckbox()
	{
		var calls = 0;
		using var context = new System.Windows.Forms.ContextMenuStrip();
		using var item = new VisibilityItem("hidden source", () => calls++) { CheckOnClick = true };
		context.Items.Add(item);
		var flags = BindingFlags.Instance | BindingFlags.NonPublic;
		var down = typeof(System.Windows.Forms.ToolStripItem).GetMethod("HandleMouseDown", flags);
		var up = typeof(System.Windows.Forms.ToolStripItem).GetMethod("HandleMouseUp", flags);
		// A prior left press makes WinForms accept right-clicks on context-menu items.
		down.Invoke(item, new object[] { new System.Windows.Forms.MouseEventArgs(System.Windows.Forms.MouseButtons.Left, 1, 0, 0, 0) });
		var right = new System.Windows.Forms.MouseEventArgs(System.Windows.Forms.MouseButtons.Right, 1, 0, 0, 0);
		down.Invoke(item, new object[] { right });
		up.Invoke(item, new object[] { right });
		Assert.False(item.Checked);
		Assert.Equal(1, calls);
	}

	[Fact]
	public void CallbackCoordinatesPreserveNegativeMonitorPositions() =>
		Assert.Equal(new Point(-1200, -80), TrayValueIcon.CallbackPoint(new IntPtr(unchecked((int)0xffb0fb50))));
}
