using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace TrayMon;

internal static class Program
{
	[STAThread]
	private static int Main(string[] args)
	{
		if (args.Any(a => a.Equals("--once", StringComparison.OrdinalIgnoreCase)))
			return RunOnce();

		ApplicationConfiguration.Initialize();
		Application.Run(new TrayApp());
		return 0;
	}

	[DllImport("kernel32.dll")]
	private static extern bool AttachConsole(int processId);

	/// <summary>Smoke test: print every value once, with the cost of each source, then exit.</summary>
	private static int RunOnce()
	{
		AttachConsole(-1);   // ATTACH_PARENT_PROCESS — a WinExe has no console of its own
		var ci = CultureInfo.InvariantCulture;
		var r = new Readings();
		var sw = new Stopwatch();

		using var perf = new PerfSensors();
		Thread.Sleep(1000);   // PDH needs two samples for a rate counter
		sw.Restart(); perf.Read(r, true); var tCpu = sw.Elapsed.TotalMilliseconds;
		sw.Restart(); r.TopIo = perf.TopIoProcesses(3); var tIo = sw.Elapsed.TotalMilliseconds;

		sw.Restart(); MemorySensor.Read(r); var tMem = sw.Elapsed.TotalMilliseconds;

		using var gpu = new GpuSensor();
		sw.Restart(); gpu.Read(r); var tGpu = sw.Elapsed.TotalMilliseconds;

		using var lhm = new LhmSensor();
		sw.Restart(); r.CpuTemp = lhm.ReadCpuTemp(); var tCpuTemp = sw.Elapsed.TotalMilliseconds;
		sw.Restart(); r.Fans = lhm.ReadFans(); var tFans = sw.Elapsed.TotalMilliseconds;
		sw.Restart(); lhm.RefreshDiskTemps(); var tDisk = sw.Elapsed.TotalMilliseconds;
		r.Disks = lhm.DiskTemps();
		var hdd = new HddSensor();
		sw.Restart(); r.RaidDisks = hdd.ReadAll(); var tRaid = sw.Elapsed.TotalMilliseconds;

		var config = Config.Load();
		var ups = new UpsSensor(config.Ups);
		sw.Restart(); ups.Read(r); var tUps = sw.Elapsed.TotalMilliseconds;

		Console.WriteLine();
		Console.WriteLine($"CPU      {Fmt(r.CpuLoad)} %      {Fmt(r.CpuTemp)} °C");
		Console.WriteLine($"RAM      {Fmt(r.MemLoad)} %      {r.MemUsedGb.ToString("0.0", ci)} / {r.MemTotalGb.ToString("0.0", ci)} GB");
		Console.WriteLine($"GPU      {Fmt(r.GpuLoad)} %      {Fmt(r.GpuTemp)} °C");
		Console.WriteLine($"GPU mem  {Fmt(r.GpuMemLoad)} %      {r.GpuMemUsedGb.ToString("0.0", ci)} / {r.GpuMemTotalGb.ToString("0.0", ci)} GB");
		foreach (var d in r.Disks)
			Console.WriteLine($"disk     {d.Temp.ToString("0", ci)} °C     {d.Name}");
		if (r.Disks.Count == 0)
			Console.WriteLine("disk     no temperatures (elevated token missing, or disks hidden behind RAID)");
		if (r.RaidDisks.Count == 0)
			Console.WriteLine("raid     no answer over CSMI (smartctl.exe missing, or no RAID controller here)");
		foreach (var d in r.RaidDisks)
			Console.WriteLine($"raid     {d.Temp.ToString("0", ci)} °C     {d.Name} {d.Serial}   (behind RAID controller)");
		foreach (var f in r.Fans)
			Console.WriteLine($"fan      {f.Rpm.ToString("0", ci).PadLeft(4)} rpm   {(f.Duty.HasValue ? f.Duty.Value.ToString("0", ci) + "%" : "—")}   {f.Name}{(f.Rpm == 0 ? "   (header empty)" : "")}");
		Console.WriteLine($"net      ↓ {r.NetInMb.ToString("0.00", ci)} / ↑ {r.NetOutMb.ToString("0.00", ci)} MB/s   link {r.NetLinkMb.ToString("0", ci)} MB/s");
		foreach (var v in r.Volumes)
			Console.WriteLine($"volume   {v.Name,-4} R {v.ReadMb.ToString("0.00", ci).PadLeft(8)} / W {v.WriteMb.ToString("0.00", ci).PadLeft(8)} MB/s");
		Console.WriteLine($"top io   {string.Join(", ", r.TopIo.Select(t => $"{t.Name} {t.Mb.ToString("0.0", ci)}"))}");
		Console.WriteLine($"fan      {(r.GpuFanRpm.HasValue ? r.GpuFanRpm.Value.ToString("0", ci).PadLeft(4) + " rpm" : "   — rpm")}   {(r.GpuFanDuty.HasValue ? r.GpuFanDuty.Value.ToString("0", ci) + "%" : "—")}   GPU Fan");
		if (ups.Present)
			Console.WriteLine($"ups      {Fmt(r.UpsCharge)} %      {(r.UpsOnBattery ? "on battery" : "on line")}, " +
							  $"{Fmt(r.UpsRunTimeMin)} min left, load {Fmt(r.UpsLoad)} %{(r.UpsNeedsNewBattery ? ", REPLACE BATTERY" : "")}");
		else
			Console.WriteLine($"ups      no answer over SNMP at {config.Ups.Host}:{config.Ups.Port} ({ups.LastError})");

		Console.WriteLine();
		Console.WriteLine($"counter in use: {perf.CounterInUse}");
		Console.WriteLine($"sensor driver:  {(lhm.Available ? "loaded" : "UNAVAILABLE — " + (lhm.LastError ?? "run elevated"))}");
		Console.WriteLine($"settings file:  {Config.Path}");
		Console.WriteLine($"cost, ms:       perf(cpu+net+disk) {tCpu:0.0}  topio {tIo:0.0}  mem {tMem:0.0}  gpu {tGpu:0.0}  cputemp {tCpuTemp:0.0}  fans {tFans:0.0}  disktemp {tDisk:0.0}  raid {tRaid:0.0}  ups {tUps:0.0}");
		Console.WriteLine();
		return 0;
	}

	private static string Fmt(double? v) => v.HasValue ? v.Value.ToString("0", CultureInfo.InvariantCulture).PadLeft(3) : "  —";
}

/// <summary>
/// Tray icons driven by one timer: CPU %, RAM GB, GPU %, VRAM GB, a temperature per sensor
/// (CPU package, GPU, every NVMe) and a speed per running fan. Sources are polled at different
/// rates because they cost different amounts: CPU/RAM are syscall-cheap, a SMART query is not.
///
/// Every icon is a slot with a stable id; which slots are shown, what colour their plate is
/// and how they are labelled comes from <see cref="Config"/> and is edited from the tray menu.
/// </summary>
internal sealed class TrayApp : ApplicationContext
{
	private const int TickMs = 2000;
	private const int GpuEveryTicks = 2;     // 4 s
	private const int SlowEveryTicks = 3;    // 6 s — CPU temperature and fans
	private const int IoEveryTicks = 3;      // 6 s — network and volume throughput
	private const int TopIoEveryTicks = 6;   // 12 s — the ~250-instance process query
	private const int DiskEveryTicks = 30;   // 60 s
	private const int RaidDiskEveryTicks = 300;  // 600 s — spawns smartctl.exe (~300 ms); HDD temperature drifts slowly
	private const int UpsEveryTicks = 15;    // 30 s — a UDP round trip to the SNMP agent

	// Built-in plate colours: one per metric family, so icons are told apart without reading them.
	private static readonly Color CpuPlate = Color.FromArgb(28, 92, 168);      // blue
	private static readonly Color RamPlate = Color.FromArgb(34, 120, 62);      // green
	private static readonly Color GpuPlate = Color.FromArgb(0, 116, 122);      // teal
	private static readonly Color VramPlate = Color.FromArgb(104, 58, 154);    // violet
	private static readonly Color CpuTempPlate = Color.FromArgb(80, 80, 92);   // steel
	private static readonly Color GpuTempPlate = Color.FromArgb(86, 62, 120);  // dark violet
	private static readonly Color DiskTempPlate = Color.FromArgb(122, 76, 30); // brown
	private static readonly Color FanPlate = Color.FromArgb(58, 74, 104);      // slate
	private static readonly Color NetPlate = Color.FromArgb(140, 60, 110);     // magenta
	private static readonly Color VolumePlate = Color.FromArgb(92, 100, 40);   // olive
	private static readonly Color RaidTempPlate = Color.FromArgb(150, 90, 30);  // amber
	private static readonly Color UpsPlate = Color.FromArgb(128, 34, 60);      // maroon

	// Per-icon identity for the tray. Windows keeps the position the user dragged an icon to
	// against these values — never renumber them, or every icon jumps back to the end.
	private const string GuidPrefix = "6f2a1c40-9d3b-4f7e-a1c2-7c9e5b0000";
	private static readonly Guid CpuGuid = new(GuidPrefix + "01");
	private static readonly Guid RamGuid = new(GuidPrefix + "02");
	private static readonly Guid GpuGuid = new(GuidPrefix + "03");
	private static readonly Guid VramGuid = new(GuidPrefix + "04");
	private static readonly Guid CpuTempGuid = new(GuidPrefix + "05");
	private static readonly Guid GpuTempGuid = new(GuidPrefix + "06");
	private static readonly Guid GpuFanGuid = new(GuidPrefix + "07");
	private static readonly Guid NetGuid = new(GuidPrefix + "08");
	private static readonly Guid UpsGuid = new(GuidPrefix + "09");
	private static readonly Guid[] RaidTempGuids =
	{
		new(GuidPrefix + "41"), new(GuidPrefix + "42"), new(GuidPrefix + "43"), new(GuidPrefix + "44"),
	};
	private static readonly Guid[] DiskGuids =
	{
		new(GuidPrefix + "11"), new(GuidPrefix + "12"), new(GuidPrefix + "13"), new(GuidPrefix + "14"),
	};
	private static readonly Guid[] VolumeGuids =
	{
		new(GuidPrefix + "31"), new(GuidPrefix + "32"), new(GuidPrefix + "33"), new(GuidPrefix + "34"),
		new(GuidPrefix + "35"), new(GuidPrefix + "36"), new(GuidPrefix + "37"), new(GuidPrefix + "38"),
	};
	private static readonly Guid[] FanGuids =
	{
		new(GuidPrefix + "21"), new(GuidPrefix + "22"), new(GuidPrefix + "23"), new(GuidPrefix + "24"),
		new(GuidPrefix + "25"), new(GuidPrefix + "26"), new(GuidPrefix + "27"), new(GuidPrefix + "28"),
	};

	/// <summary>
	/// Which SuperIO header carries the CPU cooler. Chips rarely say so: most report plain
	/// "Fan #N". If a sensor names itself (some boards do), that name is used as is; otherwise
	/// the first header gets the "CPU fan" hint, which holds on most desktop boards — and the
	/// user can rename any icon from the menu.
	/// </summary>
	private const string CpuFanSensorName = "Fan #1";

	private sealed class IconSlot
	{
		public string Id;
		public string DefaultLabel;
		public Guid Guid;
		public Color DefaultPlate;
		public double Warn, Crit;
		public TrayValueIcon Icon;
		public string LastText;
		public double? LastSeverity;
		public string LastDetail = "";
	}

	private readonly Config _config = Config.Load();
	private readonly Dictionary<string, IconSlot> _slots = new();
	private readonly List<IconSlot> _order = new();

	private readonly PerfSensors _perf = new();
	private readonly GpuSensor _gpu = new();
	private readonly LhmSensor _lhm = new();
	private readonly HddSensor _hdd = new();
	private readonly Readings _r = new();
	private readonly UpsSensor _ups;

	private readonly ContextMenuStrip _menu = new();
	private readonly System.Windows.Forms.Timer _timer;

	/// <summary>Fans that were spinning at startup. Headers with nothing plugged in stay hidden,
	/// but a fan that stops later keeps its icon and turns red.</summary>
	private List<string> _fanNames;

	private long _tick;
	private bool _diskRefreshRunning;
	private bool _raidRefreshRunning;
	private bool _upsRefreshRunning;

	public TrayApp()
	{
		_ups = new UpsSensor(_config.Ups);
		Task.Run(RefreshDisks);   // first SMART query off the UI thread
		Task.Run(RefreshUps);

		_timer = new System.Windows.Forms.Timer { Interval = TickMs };
		_timer.Tick += OnTick;
		_timer.Start();
		OnTick(null, EventArgs.Empty);
	}

	private void OnTick(object sender, EventArgs e)
	{
		_tick++;
		_perf.Read(_r, _tick % IoEveryTicks == 0);   // CPU every tick, network and volumes every other
		MemorySensor.Read(_r);
		if (_tick % GpuEveryTicks == 0) _gpu.Read(_r);
		if (_tick % SlowEveryTicks == 0)
		{
			_r.CpuTemp = _lhm.ReadCpuTemp();
			_r.Fans = _lhm.ReadFans();
			// Only worth ~250 counter instances when something is actually moving.
			if (_tick % TopIoEveryTicks == 0)
				_r.TopIo = _r.Volumes.Sum(v => v.ReadMb + v.WriteMb) > 0.5 ? _perf.TopIoProcesses(3) : new();
		}
		if (_tick % DiskEveryTicks == 0) Task.Run(RefreshDisks);
		if (_tick % RaidDiskEveryTicks == 1) Task.Run(RefreshRaidDisk);
		if (_tick % UpsEveryTicks == 2) Task.Run(RefreshUps);

		var ci = CultureInfo.InvariantCulture;

		Show("cpu", "CPU", CpuGuid, CpuPlate, 70, 85,
			Whole(_r.CpuLoad), _r.CpuLoad, $"{Pct(_r.CpuLoad)}   temp {Deg(_r.CpuTemp)}");

		// 88/95 rather than 80/90: on this hypervisor 156 of 192 GB in use is the normal
		// resting state, and a plate that is permanently yellow says nothing.
		Show("ram", "RAM", RamGuid, RamPlate, 88, 95,
			Whole(_r.MemLoad.HasValue ? _r.MemUsedGb : null), _r.MemLoad,
			$"{_r.MemUsedGb.ToString("0.0", ci)} / {_r.MemTotalGb.ToString("0.0", ci)} GB   {Pct(_r.MemLoad)}");

		Show("gpu", "GPU", GpuGuid, GpuPlate, 75, 90,
			Whole(_r.GpuLoad), _r.GpuLoad, $"core {Pct(_r.GpuLoad)}   temp {Deg(_r.GpuTemp)}");

		Show("vram", "VRAM", VramGuid, VramPlate, 75, 90,
			Whole(_r.GpuMemLoad.HasValue ? _r.GpuMemUsedGb : null), _r.GpuMemLoad,
			$"{_r.GpuMemUsedGb.ToString("0.0", ci)} / {_r.GpuMemTotalGb.ToString("0.0", ci)} GB   {Pct(_r.GpuMemLoad)}");

		Show("cpu.temp", "CPU temp", CpuTempGuid, CpuTempPlate, 75, 85,
			Whole(_r.CpuTemp), _r.CpuTemp, $"package {Deg(_r.CpuTemp)}");

		Show("gpu.temp", "GPU temp", GpuTempGuid, GpuTempPlate, 80, 90,
			Whole(_r.GpuTemp), _r.GpuTemp, $"{Deg(_r.GpuTemp)}   load {Pct(_r.GpuLoad)}");

		ShowDisks();
		ShowRaidDisk();
		ShowFans();
		ShowNetwork();
		ShowVolumes();
		ShowUps();
	}

	/// <summary>
	/// Charge of the UPS, once its SNMP agent has answered. Severity is inverted — a low charge
	/// is what matters — and running on battery goes straight to red whatever the charge is,
	/// because that is the state worth walking over to the rack for.
	/// </summary>
	private void ShowUps()
	{
		if (!_ups.Present) return;

		var charge = _r.UpsCharge;
		double? severity = charge.HasValue ? (_r.UpsOnBattery ? 100 : 100 - charge.Value) : null;
		var power = _r.UpsOnBattery ? "от батареи" : "от сети";
		var left = _r.UpsRunTimeMin.HasValue
			? $"   ещё {_r.UpsRunTimeMin.Value.ToString("0", CultureInfo.InvariantCulture)} мин"
			: "";
		var load = _r.UpsLoad.HasValue ? $"   нагрузка {Pct(_r.UpsLoad)}" : "";
		var replace = _r.UpsNeedsNewBattery ? "   ТРЕБУЕТ ЗАМЕНЫ" : "";

		Show("ups", "UPS", UpsGuid, UpsPlate, 50, 75,
			Whole(charge), severity, $"{Pct(charge)} {power}{left}{load}{replace}");
	}

	/// <summary>Traffic over physical adapters; virtual switch chatter between VMs is not counted.</summary>
	private void ShowNetwork()
	{
		var total = _r.NetInMb + _r.NetOutMb;
		var link = _r.NetLinkMb;
		var utilisation = link > 0 ? 100 * total / link : 0;
		var of = link > 0 ? $"   {utilisation:0}% of {link:0} MB/s" : "";
		Show("net", "NET", NetGuid, NetPlate, 70, 90, Mb(total), utilisation,
			$"↓ {Mb2(_r.NetInMb)} / ↑ {Mb2(_r.NetOutMb)} MB/s{of}");
	}

	/// <summary>One icon per lettered volume, showing read plus write.</summary>
	private void ShowVolumes()
	{
		for (var i = 0; i < _r.Volumes.Count && i < VolumeGuids.Length; i++)
		{
			var v = _r.Volumes[i];
			var top = _r.TopIo.Count > 0
				? "\n" + string.Join(" · ", _r.TopIo.Select(t => $"{t.Name} {Mb2(t.Mb)}"))
				: "";
			Show($"vol.{v.Name}", v.Name, VolumeGuids[i], VolumePlate, NeverAlerts, NeverAlerts,
				Mb(v.ReadMb + v.WriteMb), 0,
				$"{Mb2(v.ReadMb + v.WriteMb)} MB/s   R {Mb2(v.ReadMb)} / W {Mb2(v.WriteMb)}{top}");
		}
	}

	/// <summary>One icon per disk; they appear once the first SMART query comes back.</summary>
	private void ShowDisks()
	{
		for (var i = 0; i < _r.Disks.Count && i < DiskGuids.Length; i++)
		{
			var d = _r.Disks[i];
			Show($"disk.{i}", d.Name, DiskGuids[i], DiskTempPlate, 60, 70,
				Whole(d.Temp), d.Temp, Deg(d.Temp));
		}
	}

	/// <summary>
	/// One icon per disk behind a RAID controller. Identified by serial number, so an icon
	/// keeps its place even if the controller renumbers ports between boots.
	/// </summary>
	private void ShowRaidDisk()
	{
		for (var i = 0; i < _r.RaidDisks.Count && i < RaidTempGuids.Length; i++)
		{
			var d = _r.RaidDisks[i];
			var id = string.IsNullOrEmpty(d.Serial) ? $"disk.raid.{i}" : $"disk.raid.{d.Serial}";
			var label = string.IsNullOrEmpty(d.Serial) ? d.Name : $"{d.Name} {d.Serial}";
			Show(id, label, RaidTempGuids[i], RaidTempPlate, 55, 65, Whole(d.Temp), d.Temp, Deg(d.Temp));
		}
	}

	private void RefreshRaidDisk()
	{
		if (_raidRefreshRunning) return;
		_raidRefreshRunning = true;
		try { _r.RaidDisks = _hdd.ReadAll(); }
		finally { _raidRefreshRunning = false; }
	}

	private void RefreshUps()
	{
		if (_upsRefreshRunning) return;
		_upsRefreshRunning = true;
		try { _ups.Read(_r); }
		finally { _upsRefreshRunning = false; }
	}

	/// <summary>
	/// One icon per fan that was turning at startup, plus the GPU fan. A stopped fan reads 0
	/// and goes red — that is the state worth noticing, so severity is inverted here.
	/// </summary>
	private void ShowFans()
	{
		if (_fanNames is null && _r.Fans.Count > 0)
			_fanNames = _r.Fans.Where(f => f.Rpm > 0).Select(f => f.Name).Take(FanGuids.Length).ToList();

		if (_fanNames is not null)
		{
			for (var i = 0; i < _fanNames.Count; i++)
			{
				var name = _fanNames[i];
				if (!_r.Fans.Any(f => f.Name == name)) continue;
				var fan = _r.Fans.First(f => f.Name == name);
				var duty = fan.Duty.HasValue ? $"   duty {Pct(fan.Duty)}" : "";
				var label = name == CpuFanSensorName ? $"CPU fan ({name})" : name;
				Show($"fan.{name}", label, FanGuids[i], FanPlate, 50, 90,
					Rpm(fan.Rpm), Stalled(fan.Rpm),
					$"{fan.Rpm.ToString("0", CultureInfo.InvariantCulture)} rpm{duty}");
			}
		}

		if (_r.GpuFanRpm.HasValue || _r.GpuFanDuty.HasValue)
		{
			var rpm = _r.GpuFanRpm;
			var detail = rpm.HasValue
				? $"{rpm.Value.ToString("0", CultureInfo.InvariantCulture)} rpm   duty {Pct(_r.GpuFanDuty)}"
				: $"duty {Pct(_r.GpuFanDuty)} (driver reports no rpm)";
			Show("fan.gpu", "GPU fan", GpuFanGuid, FanPlate, 50, 90,
				rpm.HasValue ? Rpm(rpm.Value) : Whole(_r.GpuFanDuty),
				Stalled(rpm ?? _r.GpuFanDuty.Value), detail);
		}
	}

	/// <summary>Creates the slot on first sight, then shows or hides it according to settings.</summary>
	private void Show(string id, string defaultLabel, Guid guid, Color plate, double warn, double crit,
					  string text, double? severity, string detail)
	{
		if (!_slots.TryGetValue(id, out var slot))
		{
			slot = new IconSlot { Id = id, DefaultLabel = defaultLabel, Guid = guid, DefaultPlate = plate, Warn = warn, Crit = crit };
			_slots[id] = slot;
			_order.Add(slot);
		}

		slot.LastText = text;
		slot.LastSeverity = severity;
		slot.LastDetail = detail;

		if (!_config.For(id).Enabled)
		{
			if (slot.Icon is not null) { slot.Icon.Dispose(); slot.Icon = null; }
			return;
		}

		slot.Icon ??= new TrayValueIcon(OnIconRightClick, PlateOf(slot), slot.Guid, WarnOf(slot), CritOf(slot));
		slot.Icon.SetThresholds(WarnOf(slot), CritOf(slot));
		slot.Icon.SetInk(InkOf(slot));
		slot.Icon.Update(text, severity, $"{LabelOf(slot)}   {detail}");
	}

	/// <summary>Thresholds high enough that no reading reaches them — the "no alert colouring" state.</summary>
	private const double NeverAlerts = 1e9;

	private double WarnOf(IconSlot slot) => _config.For(slot.Id).Warn ?? slot.Warn;
	private double CritOf(IconSlot slot) => _config.For(slot.Id).Crit ?? slot.Crit;
	private bool AlertsOn(IconSlot slot) => WarnOf(slot) < NeverAlerts;

	private Color PlateOf(IconSlot slot)
	{
		var hex = _config.For(slot.Id).Color;
		if (!string.IsNullOrWhiteSpace(hex))
		{
			try { return ColorTranslator.FromHtml(hex); }
			catch (Exception) { /* hand-edited nonsense in the file — fall back to the default */ }
		}
		return slot.DefaultPlate;
	}

	private string LabelOf(IconSlot slot) => _config.For(slot.Id).Label ?? slot.DefaultLabel;

	/// <summary>Digit colour chosen by the user; null lets the icon pick it by the plate colour.</summary>
	private Color? InkOf(IconSlot slot) => _config.For(slot.Id).Ink switch
	{
		"light" => Color.White,
		"dark" => Color.Black,
		_ => null,
	};

	// ---- tray menu ----

	private void OnIconRightClick(TrayValueIcon icon)
	{
		var clicked = _order.FirstOrDefault(s => ReferenceEquals(s.Icon, icon));

		_menu.Items.Clear();
		if (clicked is not null)
		{
			_menu.Items.Add(new ToolStripMenuItem(LabelOf(clicked)) { Enabled = false });
			_menu.Items.Add(new ToolStripSeparator());
			_menu.Items.Add("Цвет фона…", null, (_, _) => PickColor(clicked));
			_menu.Items.Add(InkMenu(clicked));
			_menu.Items.Add("Переименовать…", null, (_, _) => Rename(clicked));
			var alerts = new ToolStripMenuItem("Подсвечивать при перегрузке")
			{
				Checked = AlertsOn(clicked),
				CheckOnClick = true,
				ToolTipText = $"Жёлтый при {clicked.Warn:0}, красный при {clicked.Crit:0}",
			};
			alerts.Click += (_, _) => SetAlerts(clicked, alerts.Checked);
			_menu.Items.Add(alerts);
			_menu.Items.Add("Скрыть этот значок", null, (_, _) => SetEnabled(clicked, false));
			_menu.Items.Add(new ToolStripSeparator());
		}

		var all = new ToolStripMenuItem("Показывать значки");
		foreach (var slot in _order)
		{
			var item = new ToolStripMenuItem(LabelOf(slot)) { Checked = _config.For(slot.Id).Enabled, CheckOnClick = true };
			var captured = slot;
			item.Click += (_, _) => SetEnabled(captured, item.Checked);
			all.DropDownItems.Add(item);
		}
		_menu.Items.Add(all);

		var autostart = new ToolStripMenuItem("Запускать при входе в Windows")
		{
			Checked = Autostart.IsEnabled,
			CheckOnClick = true,
			ToolTipText = "Задача планировщика с правами администратора — без неё не читается температура CPU",
		};
		autostart.Click += (_, _) => ToggleAutostart(autostart.Checked);
		_menu.Items.Add(autostart);

		_menu.Items.Add("Создать ярлык на рабочем столе", null, (_, _) => CreateShortcut());
		_menu.Items.Add("Настройки в файле…", null, (_, _) => OpenConfigFile());
		_menu.Items.Add(new ToolStripSeparator());
		_menu.Items.Add("Выход", null, (_, _) => ExitThread());

		icon.PrepareMenu();
		_menu.Show(Cursor.Position);
	}

	/// <summary>
	/// Light or dark digits for this icon. "Авто" is the built-in rule — dark on the yellow
	/// warning plate, light everywhere else — which only fits plates that are dark themselves.
	/// </summary>
	private ToolStripMenuItem InkMenu(IconSlot slot)
	{
		var menu = new ToolStripMenuItem("Цвет цифр");
		var current = _config.For(slot.Id).Ink;
		foreach (var (label, value) in new[] { ("Авто", (string)null), ("Светлые", "light"), ("Тёмные", "dark") })
		{
			var choice = value;
			var item = new ToolStripMenuItem(label) { Checked = current == choice };
			item.Click += (_, _) => SetInk(slot, choice);
			menu.DropDownItems.Add(item);
		}
		return menu;
	}

	private void SetInk(IconSlot slot, string ink)
	{
		_config.For(slot.Id).Ink = ink;
		_config.Save();
		slot.Icon?.SetInk(InkOf(slot));
	}

	private void PickColor(IconSlot slot)
	{
		using var dialog = new ColorDialog { Color = PlateOf(slot), FullOpen = true, AnyColor = true };
		if (dialog.ShowDialog() != DialogResult.OK) return;
		_config.For(slot.Id).Color = $"#{dialog.Color.R:X2}{dialog.Color.G:X2}{dialog.Color.B:X2}";
		_config.Save();
		slot.Icon?.SetPlate(dialog.Color);

		// A plate held at its warning colour ignores the chosen one, which looks like the
		// setting did nothing.
		if (slot.Icon?.IsAlerting == true)
			MessageBox.Show(
				$"Цвет сохранён, но сейчас значок подсвечен порогом ({slot.LastSeverity:0} ≥ {WarnOf(slot):0}).\n" +
				"Выбранный цвет появится, когда значение опустится ниже порога, либо снимите\n" +
				"галочку «Подсвечивать при перегрузке».",
				"TrayMon", MessageBoxButtons.OK, MessageBoxIcon.Information);
	}

	private void SetAlerts(IconSlot slot, bool on)
	{
		var settings = _config.For(slot.Id);
		settings.Warn = on ? null : NeverAlerts;
		settings.Crit = on ? null : NeverAlerts;
		_config.Save();
		slot.Icon?.SetThresholds(WarnOf(slot), CritOf(slot));
	}

	private void Rename(IconSlot slot)
	{
		var name = Prompt("Подпись значка", LabelOf(slot));
		if (name is null) return;
		_config.For(slot.Id).Label = string.IsNullOrWhiteSpace(name) ? null : name;   // empty restores the default
		_config.Save();
		slot.Icon?.Update(slot.LastText, slot.LastSeverity, $"{LabelOf(slot)}   {slot.LastDetail}");
	}

	private void SetEnabled(IconSlot slot, bool enabled)
	{
		// The menu lives on the icons; hiding the last one would leave no way back in — not
		// even to quit.
		if (!enabled && _order.Count(s => _config.For(s.Id).Enabled) <= 1)
		{
			MessageBox.Show("Последний значок скрыть нельзя — иначе не останется меню.",
				"TrayMon", MessageBoxButtons.OK, MessageBoxIcon.Information);
			return;
		}

		_config.For(slot.Id).Enabled = enabled;
		_config.Save();
		if (!enabled)
		{
			if (slot.Icon is not null) { slot.Icon.Dispose(); slot.Icon = null; }
			return;
		}
		slot.Icon ??= new TrayValueIcon(OnIconRightClick, PlateOf(slot), slot.Guid, slot.Warn, slot.Crit);
		slot.Icon.Update(slot.LastText, slot.LastSeverity, $"{LabelOf(slot)}   {slot.LastDetail}");
	}

	private static void ToggleAutostart(bool on)
	{
		var ok = on ? Autostart.Enable(out var error) : Autostart.Disable(out error);
		if (ok)
		{
			MessageBox.Show(
				on ? "TrayMon будет запускаться при входе в Windows." : "Автозапуск отключён.",
				"TrayMon", MessageBoxButtons.OK, MessageBoxIcon.Information);
			return;
		}
		MessageBox.Show($"Не получилось: {error}", "TrayMon", MessageBoxButtons.OK, MessageBoxIcon.Warning);
	}

	private static void CreateShortcut()
	{
		var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
		if (Autostart.CreateShortcut(desktop, out var path, out var error))
			MessageBox.Show($"Ярлык создан:\n{path}", "TrayMon", MessageBoxButtons.OK, MessageBoxIcon.Information);
		else
			MessageBox.Show($"Не получилось: {error}", "TrayMon", MessageBoxButtons.OK, MessageBoxIcon.Warning);
	}

	private static void OpenConfigFile()
	{
		try { Process.Start(new ProcessStartInfo("notepad.exe", $"\"{Config.Path}\"") { UseShellExecute = true }); }
		catch (Exception) { /* no editor, nothing to do about it */ }
	}

	private static string Prompt(string title, string current)
	{
		using var form = new Form
		{
			Text = title,
			Width = 340,
			Height = 150,
			FormBorderStyle = FormBorderStyle.FixedDialog,
			StartPosition = FormStartPosition.CenterScreen,
			MinimizeBox = false,
			MaximizeBox = false,
			ShowInTaskbar = false,
		};
		var box = new TextBox { Left = 12, Top = 18, Width = 300, Text = current };
		var ok = new Button { Text = "OK", Left = 156, Top = 60, Width = 75, DialogResult = DialogResult.OK };
		var cancel = new Button { Text = "Отмена", Left = 237, Top = 60, Width = 75, DialogResult = DialogResult.Cancel };
		form.Controls.AddRange(new Control[] { box, ok, cancel });
		form.AcceptButton = ok;
		form.CancelButton = cancel;
		form.TopMost = true;
		return form.ShowDialog() == DialogResult.OK ? box.Text.Trim() : null;
	}

	// ---- helpers ----

	private void RefreshDisks()
	{
		if (_diskRefreshRunning) return;
		_diskRefreshRunning = true;
		try
		{
			_lhm.RefreshDiskTemps();
			_r.Disks = _lhm.DiskTemps();
		}
		finally { _diskRefreshRunning = false; }
	}

	/// <summary>
	/// MB/s for an icon — always whole. Tenths would be unreadable at 16 pixels anyway, and
	/// every changed digit costs a repaint plus a call into the shell: five icons flickering
	/// through decimals measured at +0.66 % of a core, three times the price of the data itself.
	/// </summary>
	private static string Mb(double v) => Math.Round(v, 0).ToString("0", CultureInfo.InvariantCulture);

	/// <summary>MB/s for a tooltip, always with one decimal.</summary>
	private static string Mb2(double v) => v.ToString("0.0", CultureInfo.InvariantCulture);

	/// <summary>
	/// Fan speed in thousands: 586 rpm shows as 0.6, 1240 as 1.2. Four digits do not fit into
	/// 16 pixels, and a coarser number also stops the icon repainting on every small drift.
	/// </summary>
	private static string Rpm(double v) => (v / 1000).ToString("0.0", CultureInfo.InvariantCulture);

	/// <summary>Severity for a fan: only a standstill is alarming, so it is 100 or 0.</summary>
	private static double Stalled(double rpm) => rpm > 0 ? 0 : 100;

	private static string Whole(double? v) => v.HasValue ? Math.Round(v.Value, 0).ToString("0", CultureInfo.InvariantCulture) : null;
	private static string Pct(double? v) => v.HasValue ? v.Value.ToString("0", CultureInfo.InvariantCulture) + "%" : "—";
	private static string Deg(double? v) => v.HasValue ? v.Value.ToString("0", CultureInfo.InvariantCulture) + "°C" : "—";

	protected override void Dispose(bool disposing)
	{
		if (disposing)
		{
			_timer?.Stop();
			_timer?.Dispose();
			foreach (var slot in _order) slot.Icon?.Dispose();
			_menu?.Dispose();
			_perf?.Dispose();
			_gpu?.Dispose();
			_lhm?.Dispose();
		}
		base.Dispose(disposing);
	}
}
