using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace TrayMon;

internal static class Program
{
	/// <summary>Kept so the crash handlers can pull the icons out of the tray before exiting.</summary>
	private static TrayApp _app;

	[STAThread]
	private static int Main(string[] args)
	{
		if (Has(args, "--once"))
			return RunOnce(Has(args, "--icons"), Number(args, "--ticks", 1));

		// Measurement mode, described in CLAUDE.md and nowhere in the README: the only honest way
		// to compare two builds on a machine under load is to run them side by side, and two
		// copies cannot both own a tray GUID. This drops the single-instance lock and registers
		// icons without GUIDs, so neither copy spends the run taking icons away from the other.
		var measure = Has(args, "--measure");
		if (measure) TrayValueIcon.NoGuids = true;

		// A GUID belongs to an icon, not to a process: a second copy does not duplicate the
		// icons, it takes them away from the first, which then sits there invisible. That reads
		// as "the program will not start", and the menu offers two ways to arrange it (autostart
		// plus a desktop shortcut), so it is stopped here instead of being explained in a README.
		// Local\ scopes this to the logon session, which is also the scope of a tray.
		using var single = new Mutex(true, @"Local\TrayMon.SingleInstance", out var first);
		if (!first && !measure)
		{
			MessageBox.Show(
				"TrayMon уже запущен в этом сеансе.\n\n" +
				"Второй экземпляр забрал бы значки у первого: позиция значка в трее принадлежит\n" +
				"паре «путь к exe + GUID», а не процессу.",
				"TrayMon", MessageBoxButtons.OK, MessageBoxIcon.Information);
			return 2;
		}

		ApplicationConfiguration.Initialize();

		// Without this a single exception on a tick opens a ThreadExceptionDialog every two
		// seconds — invisible on a server with the RDP session disconnected, and each one holding
		// USER handles until they run out.
		Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
		Application.ThreadException += (_, e) => _app?.Trouble(e.Exception);
		AppDomain.CurrentDomain.UnhandledException += (_, e) => Fatal(e.ExceptionObject as Exception);

		_app = new TrayApp();
		// Disposed explicitly rather than left to whoever owns the context: Dispose is what
		// sends NIM_DELETE, and an icon that is not deleted stays in the tray as a ghost until
		// the shell notices the process is gone.
		try { Application.Run(_app); }
		finally { _app.Dispose(); }
		return 0;
	}

	/// <summary>Last chance to take the icons out of the tray, so they do not linger as ghosts.</summary>
	private static void Fatal(Exception ex)
	{
		try { _app?.Panic(ex); } catch (Exception) { /* going down anyway */ }
	}

	private static bool Has(string[] args, string name) =>
		args.Any(a => a.Equals(name, StringComparison.OrdinalIgnoreCase));

	/// <summary>Value of a "--name N" pair, or the fallback when it is absent or not a number.</summary>
	private static int Number(string[] args, string name, int fallback)
	{
		for (var i = 0; i < args.Length - 1; i++)
			if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase) &&
				int.TryParse(args[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
				return Math.Clamp(value, 1, 100);
		return fallback;
	}

	[DllImport("kernel32.dll")]
	private static extern bool AttachConsole(int processId);

	/// <summary>
	/// Smoke test: print every value once, with the cost of each source, then exit.
	/// With --icons it also runs one full tick of the tray layer without registering anything,
	/// and prints which slot got which GUID and what it would draw — the parts of the program
	/// that are actually fragile, and that reading the sensors alone never touches.
	///
	/// Paths and addresses are redacted here: this output goes into public issue trackers.
	/// The unredacted version is the "Диагностика…" window, which stays on the machine.
	/// </summary>
	private static int RunOnce(bool withIcons, int ticks)
	{
		AttachConsole(-1);   // ATTACH_PARENT_PROCESS — a WinExe has no console of its own
		var ci = CultureInfo.InvariantCulture;
		var r = new Readings();
		var sw = new Stopwatch();
		var config = Config.Load();

		// Every source is read twice and both timings are printed. The first call is cold — PDH
		// buffers unallocated, instance lists not warmed, the sensor library still opening its
		// handles — and three documents used to carry a paragraph warning that the numbers were
		// therefore useless for comparing anything. The second pass costs about a tenth of a
		// second in total and removes the need for the warning.
		var cold = new Dictionary<string, double>(StringComparer.Ordinal);
		var warm = new Dictionary<string, double>(StringComparer.Ordinal);
		void Time(string what, bool second, Action work)
		{
			sw.Restart();
			work();
			(second ? warm : cold)[what] = sw.Elapsed.TotalMilliseconds;
		}

		using var perf = new PerfSensors(config.Net);
		using var gpu = new GpuSensor();
		using var lhm = new LhmSensor();
		var disk = new DiskSensor();
		var hdd = new HddSensor(config.Tools);
		var ups = new UpsSensor(config.Ups);

		Thread.Sleep(1000);   // PDH needs two samples for a rate counter
		for (var pass = 0; pass < 2; pass++)
		{
			var second = pass == 1;
			Time("perf", second, () => perf.Read(r, true));
			Time("topio", second, () => r.TopIo = perf.TopIoProcesses(3, int.MaxValue));
			Time("mem", second, () => MemorySensor.Read(r));
			Time("battery", second, () => BatterySensor.Read(r));
			Time("space", second, () => r.Space = SpaceSensor.Read(r.Volumes.Select(v => v.Name)));
			Time("gpu", second, () => gpu.Read(r));
			Time("cputemp+fans", second, () => r.Slow = lhm.ReadSlow());
			Time("disktemp", second, () => r.Disks = DiskTemps(disk, lhm));
			Time("raid", second, () => r.RaidDisks = hdd.ReadAll());
			Time("ups", second, () => ups.Read(r));
		}
		r.UptimeHours = PerfSensors.ReadUptime();

		Console.WriteLine();
		Console.WriteLine($"CPU      {Fmt(r.CpuLoad)} %      {Fmt(r.Slow.CpuTemp)} °C");
		Console.WriteLine($"RAM      {Fmt(r.MemLoad)} %      {r.MemUsedGb.ToString("0.0", ci)} / {r.MemTotalGb.ToString("0.0", ci)} GB");
		if (r.Gpus.Count == 0)
			Console.WriteLine($"GPU      no NVIDIA driver ({gpu.LastError ?? "nvml.dll missing"})");
		foreach (var g in r.Gpus)
		{
			Console.WriteLine($"GPU {g.Index}    {Fmt(g.Load)} %      {Fmt(g.Temp)} °C   {g.Name}");
			Console.WriteLine($"GPU {g.Index} mem {Fmt(g.MemLoad)} %      {g.MemUsedGb.ToString("0.0", ci)} / {g.MemTotalGb.ToString("0.0", ci)} GB");
			Console.WriteLine($"GPU {g.Index} fan {(g.FanRpm.HasValue ? g.FanRpm.Value.ToString("0", ci).PadLeft(4) + " rpm" : "   — rpm")}   " +
							  $"{(g.FanDuty.HasValue ? g.FanDuty.Value.ToString("0", ci) + "%" : "—")}");
		}
		foreach (var d in r.Disks)
			Console.WriteLine($"disk     {d.Temp.ToString("0", ci)} °C     {d.Name}" +
							  $"{(d.WearPercent.HasValue ? "   wear " + d.WearPercent.Value.ToString("0", ci) + "%" : "")}" +
							  $"{(d.SparePercent.HasValue ? "   spare " + d.SparePercent.Value.ToString("0", ci) + "%" : "")}");
		if (r.Disks.Count == 0)
			Console.WriteLine("disk     no temperatures (no drive answered the storage query, or disks hidden behind RAID)");
		if (r.RaidDisks.Count == 0)
			Console.WriteLine("raid     no answer over CSMI (smartctl.exe missing, or no RAID controller here)");
		foreach (var d in r.RaidDisks)
			Console.WriteLine($"raid     {d.Temp.ToString("0", ci)} °C     {d.Name} <serial>   " +
							  $"health {(string.IsNullOrEmpty(d.Health) ? "—" : d.Health)}   (behind RAID controller)");
		foreach (var f in r.Slow.Fans)
			Console.WriteLine($"fan      {f.Rpm.ToString("0", ci).PadLeft(4)} rpm   {(f.Duty.HasValue ? f.Duty.Value.ToString("0", ci) + "%" : "—")}   {f.Chip}/{f.Name}{(f.Rpm == 0 ? "   (header empty)" : "")}");
		foreach (var n in r.Nets)
			Console.WriteLine($"net      ↓ {(n.InMb * 8).ToString("0.00", ci)} / ↑ {(n.OutMb * 8).ToString("0.00", ci)} Mbit/s   " +
							  $"link {(n.LinkMb * 8).ToString("0", ci)} Mbit/s   {n.Name}");
		if (r.Nets.Count == 0)
			Console.WriteLine("net      no physical adapter with a link");
		foreach (var v in r.Volumes)
			Console.WriteLine($"volume   {v.Name,-4} R {v.ReadMb.ToString("0.00", ci).PadLeft(8)} / W {v.WriteMb.ToString("0.00", ci).PadLeft(8)} MB/s");
		foreach (var s in r.Space)
			Console.WriteLine($"free     {s.Name,-4} {s.FreeGb.ToString("0", ci).PadLeft(6)} of {s.TotalGb.ToString("0", ci)} GB");
		Console.WriteLine($"uptime   {(r.UptimeHours.HasValue ? r.UptimeHours.Value.ToString("0.0", ci) + " h" : "—")}");
		Console.WriteLine($"top io   {string.Join(", ", r.TopIo.Select(t => $"{t.Name} {t.Mb.ToString("0.0", ci)}"))}");
		if (r.Battery is not null)
			Console.WriteLine($"battery  {r.Battery.Charge.ToString("0", ci).PadLeft(3)} %      " +
							  $"{(r.Battery.OnBattery ? "on battery" : "on line")}" +
							  $"{(r.Battery.MinutesLeft.HasValue ? ", " + r.Battery.MinutesLeft.Value.ToString("0", ci) + " min left" : "")}");
		if (ups.Present)
			Console.WriteLine($"ups      {Fmt(r.Ups.Charge)} %      {r.Ups.StatusText}, " +
							  $"{Fmt(r.Ups.RunTimeMin)} min left, load {Fmt(r.Ups.Load)} %{(r.Ups.NeedsNewBattery ? ", REPLACE BATTERY" : "")}");
		else
			Console.WriteLine($"ups      no answer over SNMP at <host from TrayMon.json> ({ups.LastError})");

		Console.WriteLine();
		Console.WriteLine($"counter in use: {perf.CounterInUse}");
		Console.WriteLine($"sensor driver:  {(!lhm.Available ? "UNAVAILABLE — " + (lhm.LastError ?? "run elevated") : lhm.DriverBlocked ? "BLOCKED — " + lhm.LastError : "loaded")}");
		if (lhm.Ring0Note is not null) Console.WriteLine($"ring-0 device:  {lhm.Ring0Note}");
		Console.WriteLine($"elevated:       {(TrayApp.IsElevated ? "yes" : "no")}");
		Console.WriteLine($"smartctl:       {(hdd.Available ? "found" : "not found")}");
		// Whether, not who: the identity would be a domain and account name, and this output goes
		// into public issue trackers. The name is in the diagnostics window instead.
		var loose = Autostart.WritableByNonAdmins(AppContext.BaseDirectory, out _);
		Console.WriteLine($"install folder: {(Autostart.LastCheckError is not null ? "permissions not readable — " + Autostart.LastCheckError : loose ? "WRITABLE by a non-administrator — do not enable autostart from here" : "writable by administrators only")}");
		Console.WriteLine($"settings file:  TrayMon.json {(File.Exists(Config.Path) ? "found" : "absent, defaults in use")}" +
						  $"{(config.LoadError is null ? "" : " — " + config.LoadError)}");
		Console.WriteLine("cost, ms (cold / warm):");
		foreach (var source in cold.Keys)
			Console.WriteLine($"                {source,-14} {cold[source].ToString("0.0", ci).PadLeft(7)} / " +
							  $"{warm[source].ToString("0.0", ci).PadLeft(6)}");

		if (withIcons)
		{
			Console.WriteLine();
			// The warmed sensors are handed over rather than built again: a second PerfSensors
			// read its rate counters milliseconds after establishing their baseline (so the
			// numbers were noise), and a second Computer.Open() in the same process opened the
			// ring-0 driver twice for no reason.
			using var dry = new TrayApp(perf, gpu, lhm, disk, hdd, ups);
			Console.WriteLine(dry.DryRun(ticks));
		}
		Console.WriteLine();
		return 0;
	}

	/// <summary>
	/// Disk temperatures from the storage driver, with the sensor library filling in the NVMe
	/// wear figures for the drives it recognises. The storage query needs neither elevation nor
	/// a kernel driver; the wear figures are an NVMe log page and only the library reads those.
	/// </summary>
	internal static List<DiskReading> DiskTemps(DiskSensor disk, LhmSensor lhm)
	{
		lhm.RefreshDiskTemps();
		var fromLibrary = lhm.DiskTemps();
		var fromDriver = disk.Read();
		if (fromDriver.Count == 0) return fromLibrary;

		var health = new Dictionary<string, DiskReading>(StringComparer.OrdinalIgnoreCase);
		foreach (var d in fromLibrary) health[d.Name] = d;

		var merged = new List<DiskReading>(fromDriver.Count);
		foreach (var d in fromDriver)
		{
			// Model names differ in spacing and vendor prefix between the two sources, so the
			// match is loose: one contains the other, or neither and the wear is simply absent.
			var known = health.Values.FirstOrDefault(h =>
				h.Name.Contains(d.Name, StringComparison.OrdinalIgnoreCase) ||
				d.Name.Contains(h.Name, StringComparison.OrdinalIgnoreCase));
			merged.Add(known is null ? d : d with { WearPercent = known.WearPercent, SparePercent = known.SparePercent });
		}
		return merged;
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
	/// <summary>Built-in poll rate. <see cref="Config.TickMs"/> may raise it, never lower it.</summary>
	private const int TickMs = 2000;

	/// <summary>
	/// Poll rate while the session is locked or disconnected. Nobody can see the icons, and an
	/// alarm nobody can see is not an alarm — on a server with the RDP session closed that is
	/// most of the day. Every schedule below is expressed in ticks, so slowing the timer slows
	/// all of them together.
	/// </summary>
	private const int IdleTickMs = 30000;

	// Phases as well as periods. The rule is not "no common divisors" — it cannot be, since
	// 300, 150, 15 and 6 are all multiples of 3 — but "if two periods share a divisor d, their
	// phases must differ modulo d". Two schedules that fail that meet on the same tick *always*,
	// not occasionally: DiskEveryTicks was 30 against SlowEveryTicks 3, so the SMART refresh met
	// the sensor-library read once a minute exactly. Phases below: Slow 1, Io 0, TopIo 4, Disk 5,
	// Raid 1 (period 300, so 1 mod 3 — see the note on RaidDiskEveryTicks), Ups 2, Space 7, Stats 3.
	private const int GpuEveryTicks = 2;         // 4 s
	private const int SlowEveryTicks = 3;        // 6 s — CPU temperature and fans, phase 1
	private const int IoEveryTicks = 3;          // 6 s — network and volume throughput, phase 0
	private const int TopIoEveryTicks = 6;       // 12 s — the ~250-instance process query, phase 4
	private const int DiskEveryTicks = 31;       // 62 s — prime, so it never falls in step with 3
	private const int RaidDiskEveryTicks = 300;  // 600 s — spawns smartctl.exe; HDD temperature drifts slowly
	private const int UpsEveryTicks = 15;        // 30 s — a UDP round trip to the SNMP agent
	private const int SpaceEveryTicks = 150;     // 300 s — free space changes slowly and costs a syscall
	private const int StatsEveryTicks = 15;      // 30 s — recompute the min/avg/max line for tooltips

	/// <summary>
	/// A source silent for this long has its tray slot handed back to the pool. Wall clock, not
	/// ticks: the tick rate is now both configurable and slowed while the session is locked, and
	/// a day has to stay a day either way.
	/// </summary>
	private const long ForgetAfterMs = 24L * 60 * 60 * 1000;

	/// <summary>
	/// A source that has failed this many times running is not there — an SNMP agent that was
	/// never installed, a smartctl.exe nobody copied. Asking every thirty seconds for ever costs
	/// a UDP round trip and a 1.5 s wait on a pool thread on every machine without a UPS, which
	/// is most of them.
	/// </summary>
	private const int GiveUpAfter = 5;

	/// <summary>How long a source that gave up is left alone. "Опросить датчики сейчас" resets it.</summary>
	private const long RetryDeadSourceMs = 5 * 60 * 1000;

	/// <summary>Thresholds high enough that no reading reaches them — the "never alarms" metric state.</summary>
	private const double NeverAlerts = 1e9;

	// Built-in plate colours: one per metric family, so icons are told apart without reading them.
	// No two are alike — GPU and "CPU temperature" used to be the same maroon down to the byte,
	// which made two unrelated metrics indistinguishable. A temperature now carries a lighter
	// shade of the family it belongs to, except the GPU one, which stays blue because a lighter
	// maroon would collide with the critical plate.
	private static readonly Color CpuPlate = Color.FromArgb(128, 0, 255);      // violet
	private static readonly Color RamPlate = Color.FromArgb(64, 128, 128);     // teal
	private static readonly Color GpuPlate = Color.FromArgb(128, 0, 0);        // maroon
	private static readonly Color VramPlate = Color.FromArgb(0, 128, 0);       // green
	private static readonly Color CpuTempPlate = Color.FromArgb(96, 48, 176);  // muted violet — CPU family
	private static readonly Color GpuTempPlate = Color.FromArgb(0, 0, 255);    // blue
	private static readonly Color DiskTempPlate = Color.FromArgb(255, 128, 0); // orange
	private static readonly Color FanPlate = Color.FromArgb(255, 0, 255);      // magenta
	private static readonly Color GpuFanPlate = Color.FromArgb(176, 0, 176);   // dark magenta — fan family
	private static readonly Color NetPlate = Color.FromArgb(0, 128, 192);      // azure
	private static readonly Color VolumePlate = Color.FromArgb(128, 64, 0);    // brown
	private static readonly Color FreePlate = Color.FromArgb(96, 96, 32);      // olive
	private static readonly Color RaidTempPlate = Color.FromArgb(255, 128, 0); // orange
	private static readonly Color UpsPlate = Color.FromArgb(64, 84, 104);      // steel — legible under white digits
	private static readonly Color BatteryPlate = Color.FromArgb(96, 116, 64);  // moss — power family, distinct from steel
	private static readonly Color UptimePlate = Color.FromArgb(72, 72, 88);    // slate
	private static readonly Color WorstPlate = Color.FromArgb(0, 96, 96);      // dark cyan

	// ---- per-icon identity for the tray ----
	//
	// Windows keeps the position the user dragged an icon to against these values — never
	// renumber them, or every icon jumps back to the end of the queue with its visibility reset.
	//
	// The suffixes are laid out in blocks of ten, not consecutively, so a family can grow
	// without stepping on the next one:
	//
	//     01-0C  single icons          0D-0F  free (single icons)
	//     11-14  disk temperature      15-1F  free (disks)
	//     21-28  motherboard fans      29-2F  free (fans)
	//     31-38  volume throughput     39-3F  free (volumes)
	//     41-44  RAID disks            45-4F  free (RAID)
	//     51-57  network adapters      58-5F  free (network)
	//     61-68  free space            69-6F  free (space)
	//     71-7C  second and later GPU  7D+    free
	//
	// Add a new icon by taking the next value inside its block, or a whole free block for a new
	// family — never by inserting into the middle of one.
	private const string GuidPrefix = "6f2a1c40-9d3b-4f7e-a1c2-7c9e5b0000";
	private static readonly Guid CpuGuid = new(GuidPrefix + "01");
	private static readonly Guid RamGuid = new(GuidPrefix + "02");
	private static readonly Guid VramGuid = new(GuidPrefix + "04");
	private static readonly Guid CpuTempGuid = new(GuidPrefix + "05");
	private static readonly Guid UpsGuid = new(GuidPrefix + "09");
	private static readonly Guid WorstGuid = new(GuidPrefix + "0A");
	private static readonly Guid UptimeGuid = new(GuidPrefix + "0B");
	private static readonly Guid BatteryGuid = new(GuidPrefix + "0C");

	// The first slot of each GPU family reuses the GUID of the single-card icon it used to be,
	// so a machine with one card keeps the places its icons were dragged to.
	private static readonly Guid[] GpuGuids = { new(GuidPrefix + "03"), new(GuidPrefix + "71"), new(GuidPrefix + "72"), new(GuidPrefix + "73") };
	private static readonly Guid[] VramGuids = { VramGuid, new(GuidPrefix + "74"), new(GuidPrefix + "75"), new(GuidPrefix + "76") };
	private static readonly Guid[] GpuTempGuids = { new(GuidPrefix + "06"), new(GuidPrefix + "77"), new(GuidPrefix + "78"), new(GuidPrefix + "79") };
	private static readonly Guid[] GpuFanGuids = { new(GuidPrefix + "07"), new(GuidPrefix + "7A"), new(GuidPrefix + "7B"), new(GuidPrefix + "7C") };

	// The first slot reuses the GUID of the single "NET" icon this used to be, for the same reason.
	private static readonly Guid[] NetGuids =
	{
		new(GuidPrefix + "08"), new(GuidPrefix + "51"), new(GuidPrefix + "52"), new(GuidPrefix + "53"),
		new(GuidPrefix + "54"), new(GuidPrefix + "55"), new(GuidPrefix + "56"), new(GuidPrefix + "57"),
	};
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
	private static readonly Guid[] FreeGuids =
	{
		new(GuidPrefix + "61"), new(GuidPrefix + "62"), new(GuidPrefix + "63"), new(GuidPrefix + "64"),
		new(GuidPrefix + "65"), new(GuidPrefix + "66"), new(GuidPrefix + "67"), new(GuidPrefix + "68"),
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

	// ---- metric descriptions ----

	/// <summary>
	/// The constant half of an icon: everything that does not change while the program runs.
	/// Keeping colour, thresholds, unit, menu group and GUID pool together is what stops the
	/// tables that describe one metric from drifting apart across three hundred lines.
	/// </summary>
	private sealed class Metric
	{
		public string Group;      // heading in "Показывать значки"
		public int Order;         // stable position inside that heading
		public string Label;      // default caption
		public string Unit;       // for the threshold hint in the menu
		public Color Plate;
		public double Warn, Crit;
		public Guid[] Pool;       // one entry for a single icon, several for a family

		/// <summary>
		/// The number the user thinks in is <c>100 − severity</c>. Only the UPS: its icon and its
		/// thresholds are about the charge, while severity has to grow as things get worse. This
		/// used to be set on four metrics and read by nothing, which is precisely why the
		/// threshold dialog offered "yellow threshold (% charge): 50" and then stored what was
		/// typed as a severity — enter "red 20" and the plate turned red at 80 % charge.
		/// </summary>
		public bool Inverted;

		/// <summary>
		/// Severity is 0 or 100 by whether the thing is turning at all — fans. There is nothing
		/// to set here, and the threshold dialog used to accept "600 rpm" into a scale that only
		/// ever holds 0 and 100, which silently switched the standstill alarm off.
		/// </summary>
		public bool StallAlarm;

		/// <summary>Whether a transition into the red raises a balloon unless the user says otherwise.</summary>
		public bool NotifyByDefault;
	}

	private static readonly Metric CpuMetric = new() { Group = "Процессор", Order = 0, Label = "CPU", Unit = "%", Plate = CpuPlate, Warn = 70, Crit = 85, Pool = new[] { CpuGuid } };
	private static readonly Metric CpuTempMetric = new() { Group = "Процессор", Order = 1, Label = "Температура CPU", Unit = "°C", Plate = CpuTempPlate, Warn = 75, Crit = 85, Pool = new[] { CpuTempGuid } };
	private static readonly Metric RamMetric = new() { Group = "Память", Order = 0, Label = "RAM", Unit = "% объёма", Plate = RamPlate, Warn = 88, Crit = 95, Pool = new[] { RamGuid } };
	private static readonly Metric GpuMetric = new() { Group = "Видеокарта", Order = 0, Label = "GPU", Unit = "%", Plate = GpuPlate, Warn = 75, Crit = 90, Pool = GpuGuids };
	private static readonly Metric VramMetric = new() { Group = "Видеокарта", Order = 1, Label = "VRAM", Unit = "% объёма", Plate = VramPlate, Warn = 75, Crit = 90, Pool = VramGuids };
	private static readonly Metric GpuTempMetric = new() { Group = "Видеокарта", Order = 2, Label = "Температура GPU", Unit = "°C", Plate = GpuTempPlate, Warn = 80, Crit = 90, Pool = GpuTempGuids };
	private static readonly Metric GpuFanMetric = new() { Group = "Видеокарта", Order = 3, Label = "Вентилятор GPU", Unit = "об/мин", Plate = GpuFanPlate, Warn = 50, Crit = 90, Pool = GpuFanGuids, StallAlarm = true, NotifyByDefault = true };
	private static readonly Metric DiskMetric = new() { Group = "Диски", Order = 0, Label = "Диск", Unit = "°C", Plate = DiskTempPlate, Warn = 60, Crit = 70, Pool = DiskGuids, NotifyByDefault = true };
	private static readonly Metric RaidMetric = new() { Group = "Диски", Order = 1, Label = "Диск за RAID", Unit = "°C", Plate = RaidTempPlate, Warn = 55, Crit = 65, Pool = RaidTempGuids, NotifyByDefault = true };
	private static readonly Metric FanMetric = new() { Group = "Вентиляторы", Order = 0, Label = "Вентилятор", Unit = "об/мин", Plate = FanPlate, Warn = 50, Crit = 90, Pool = FanGuids, StallAlarm = true, NotifyByDefault = true };
	private static readonly Metric NetMetric = new() { Group = "Сеть", Order = 0, Label = "Сеть", Unit = "% полосы", Plate = NetPlate, Warn = 70, Crit = 90, Pool = NetGuids };
	private static readonly Metric VolumeMetric = new() { Group = "Тома", Order = 0, Label = "Том", Unit = "МБ/с", Plate = VolumePlate, Warn = NeverAlerts, Crit = NeverAlerts, Pool = VolumeGuids };
	// Not Inverted: the thresholds and the severity are both "percent used", so the number in the
	// dialog is already the number that colours the plate. What is inverted is only the icon,
	// which draws gigabytes free.
	private static readonly Metric FreeMetric = new() { Group = "Тома", Order = 1, Label = "Свободно", Unit = "% занято", Plate = FreePlate, Warn = 85, Crit = 95, Pool = FreeGuids, NotifyByDefault = true };
	private static readonly Metric UpsMetric = new() { Group = "Питание", Order = 0, Label = "ИБП", Unit = "% заряда", Plate = UpsPlate, Warn = 50, Crit = 75, Pool = new[] { UpsGuid }, Inverted = true };
	private static readonly Metric BatteryMetric = new() { Group = "Питание", Order = 1, Label = "Батарея", Unit = "% заряда", Plate = BatteryPlate, Warn = 60, Crit = 85, Pool = new[] { BatteryGuid }, Inverted = true };
	private static readonly Metric UptimeMetric = new() { Group = "Прочее", Order = 0, Label = "Время работы", Unit = "ч", Plate = UptimePlate, Warn = NeverAlerts, Crit = NeverAlerts, Pool = new[] { UptimeGuid } };
	// 78 and 100 on a scale where every metric's own yellow threshold maps to 78 and its own red
	// to 100 — see Score(). The mapping used to be a plain 100·severity/crit, which put the
	// yellows of the built-in metrics anywhere between 67 (UPS) and 93 (RAM): the summary icon
	// was still green while the UPS icon it was summarising had been yellow for twenty percent.
	private static readonly Metric WorstMetric = new() { Group = "Прочее", Order = 1, Label = "Худшее состояние", Unit = "% от красного порога", Plate = WorstPlate, Warn = 78, Crit = 100, Pool = new[] { WorstGuid } };

	/// <summary>Order the headings appear in the menu, so a tick is where it was yesterday.</summary>
	private static readonly string[] Groups =
		{ "Процессор", "Память", "Видеокарта", "Диски", "Вентиляторы", "Сеть", "Тома", "Питание", "Прочее" };

	// ---- state ----

	private sealed class IconSlot
	{
		public string Id;
		public Metric Metric;
		public string DefaultLabel;
		public Guid Guid;
		public GuidPool Pool;      // null for a single icon that can never be reassigned
		public string PoolKey;
		public IconSettings Settings = IconSettings.Default;
		public TrayValueIcon Icon;
		public string LastText;
		public double? LastSeverity;
		public string LastDetail = "";
		public long SeenTick = -1;
		/// <summary>Wall clock of the last reading, so a slot is forgotten after a day whatever
		/// the tick rate happens to be.</summary>
		public long SeenAtMs = Environment.TickCount64;
		/// <summary>The last number this slot actually had, kept after it goes grey: "— the
		/// adapter is gone, last 12 Mbit/s at 14:20" answers a question a bare dash cannot.</summary>
		public string FarewellText;
		public DateTime FarewellAt;
		public bool Dead;
		/// <summary>Alert level last reported: -1 dead, 0 normal, 1 yellow, 2 red. Transitions
		/// between these are what the journal and the balloons are made of.</summary>
		public int Level = -1;
		/// <summary>Severity of the last reading, kept even for an icon nobody is showing —
		/// the summary icon exists precisely so a hidden metric can still raise the alarm.</summary>
		public double? Severity;
		public readonly Stats Stats = new();
		public string StatsText = "";
	}

	private Config _config = Config.Load();
	private readonly Dictionary<string, IconSlot> _slots = new(StringComparer.Ordinal);
	private readonly List<IconSlot> _order = new();

	private readonly GuidPool _gpuPool, _vramPool, _gpuTempPool, _gpuFanPool;
	private readonly GuidPool _netPool, _volumePool, _freePool, _diskPool, _raidPool, _fanPool;
	private readonly GuidPool[] _pools;

	private readonly PerfSensors _perf;
	private readonly GpuSensor _gpu;
	private readonly LhmSensor _lhm;
	private readonly DiskSensor _disk;
	private readonly HddSensor _hdd;
	private readonly Readings _r = new();
	private readonly UpsSensor _ups;

	/// <summary>False in the dry run, where the sensors are borrowed from <c>--once</c>.</summary>
	private readonly bool _ownsSensors = true;

	private readonly ContextMenuStrip _menu = new();

	/// <summary>True while the "Показывать значки" list is open, which is when a click on an item
	/// must not close the menu. See the handler in the constructor.</summary>
	private bool _pickingIcons;
	private readonly System.Windows.Forms.Timer _timer;
	private readonly bool _dry;

	/// <summary>
	/// Fans that have been seen turning. A header with nothing plugged in reads zero for ever and
	/// gets no icon; a fan that stops later keeps its icon and turns red. The list grows: latching
	/// it at the first poll meant a fan idle at that moment — every case fan with a zero-rpm mode,
	/// which is most of them — never got an icon until the program was restarted.
	/// </summary>
	private readonly HashSet<string> _fanSeen = new(StringComparer.Ordinal);

	// The key of each fan in the list currently published, computed once per published list
	// rather than once per tick: the list is replaced whole every six seconds, the keys cannot
	// change without it, and building a dictionary on every tick to discover that is exactly the
	// kind of per-tick allocation this program does not make.
	private List<FanReading> _fanListSeen;
	private string[] _fanKeysOfList = Array.Empty<string>();

	private List<DiskReading> _diskListSeen;
	private string[] _diskKeysOfList = Array.Empty<string>();

	private long _tick;
	private bool _firstRun;
	private bool _configDirty;
	private bool _greeted;
	private string _topIoLine = "";
	private bool? _lastOnBattery;
	private long _lastLogAt = long.MinValue / 4;

	/// <summary>Guards against re-entering the tick from a modal dialog's own message loop.</summary>
	private bool _inTick;

	/// <summary>Messages raised from inside a tick, shown once the tick has been timed and left.</summary>
	private readonly List<(string Text, MessageBoxIcon Icon)> _notices = new();

	/// <summary>True while the summary window is up: version 4 sends NIN_SELECT for every click,
	/// and the second one arrives inside the first window's modal loop.</summary>
	private bool _summaryOpen;

	/// <summary>Last fifty colour changes, for "why was it red at three in the morning".</summary>
	private readonly Queue<string> _journal = new();

	// Sources that are simply not present on this machine stop being asked so often.
	private int _upsFailures, _raidFailures;
	private long _upsRetryAt, _raidRetryAt;

	// Time spent on the pool threads, so the diagnostics window can say what the program really
	// costs instead of only what its UI thread costs.
	private long _poolTicks;
	private readonly System.Diagnostics.Process _self = System.Diagnostics.Process.GetCurrentProcess();

	private int _intervalMs = TickMs;
	private bool _sessionIdle;
	private long _configTouchedAt;
	private FileSystemWatcher _configWatcher;

	/// <summary>
	/// Something with a window handle on the UI thread, purely to get back onto it. SystemEvents
	/// raises its notifications on a thread of its own, and everything they touch here — the
	/// timer, the icons, the settings — belongs to the thread that owns the message loop.
	/// </summary>
	private readonly Control _sync = new();

	// Interlocked, not plain bools: a check followed by a set is not atomic, and the case the
	// flag exists for — a refresh that outlives its own period — is exactly the case where two
	// threads reach it together.
	private int _diskRefreshRunning;
	private int _raidRefreshRunning;
	private int _upsRefreshRunning;
	private int _slowRefreshRunning;
	private int _topIoRefreshRunning;
	private int _spaceRefreshRunning;

	private readonly List<Task> _background = new();

	/// <summary>Built once in the constructor so the per-tick loop over them allocates nothing.</summary>
	private readonly (string Name, Action Show)[] _families;

	// Source versions, so a family polled once every ten minutes is not re-formatted every two
	// seconds just to be thrown away by the deduplication inside the icon.
	private int _shownDisks = -1, _shownRaid = -1, _shownUps = -1;
	// Bumped by the pool threads that publish these readings and read on the UI thread through
	// Volatile.Read. Not declared volatile, because a volatile field cannot be passed by ref to
	// Interlocked without a warning that the reference is not treated as volatile.
	private int _disksVersion, _raidVersion, _upsVersion;

	private string _lastTickError;
	private int _tickErrors;
	private long _tickTicks;

	public static bool IsElevated
	{
		get
		{
			try
			{
				using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
				return new System.Security.Principal.WindowsPrincipal(identity)
					.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
			}
			catch (Exception) { return false; }
		}
	}

	/// <summary>
	/// The dry run, with the sensors <c>--once</c> has already warmed. Building a second set of
	/// them was measuring noise: the PDH rate counters were read milliseconds after their own
	/// baseline collect, and a second <c>Computer.Open()</c> opened the ring-0 driver twice.
	/// </summary>
	public TrayApp(PerfSensors perf, GpuSensor gpu, LhmSensor lhm, DiskSensor disk,
				   HddSensor hdd, UpsSensor ups)
		: this(dryRun: true, perf, gpu, lhm, disk, hdd, ups)
	{
	}

	public TrayApp() : this(dryRun: false, null, null, null, null, null, null)
	{
	}

	private TrayApp(bool dryRun, PerfSensors perf, GpuSensor gpu, LhmSensor lhm, DiskSensor disk,
					HddSensor hdd, UpsSensor ups)
	{
		_dry = dryRun;
		_firstRun = !File.Exists(Config.Path);
		_ownsSensors = perf is null;
		_perf = perf ?? new PerfSensors(_config.Net);
		_gpu = gpu ?? new GpuSensor();
		_lhm = lhm ?? new LhmSensor();
		_disk = disk ?? new DiskSensor();
		_hdd = hdd ?? new HddSensor(_config.Tools);
		_ups = ups ?? new UpsSensor(_config.Ups);
		_intervalMs = Math.Max(TickMs, _config.TickMs);

		_gpuPool = new GuidPool("видеокарт", GpuGuids);
		_vramPool = new GuidPool("значков видеопамяти", VramGuids);
		_gpuTempPool = new GuidPool("температур GPU", GpuTempGuids);
		_gpuFanPool = new GuidPool("вентиляторов GPU", GpuFanGuids);
		_netPool = new GuidPool("сетевых адаптеров", NetGuids);
		_volumePool = new GuidPool("томов", VolumeGuids);
		_freePool = new GuidPool("значков свободного места", FreeGuids);
		_diskPool = new GuidPool("дисков", DiskGuids);
		_raidPool = new GuidPool("дисков за RAID", RaidTempGuids);
		_fanPool = new GuidPool("вентиляторов", FanGuids);
		_pools = new[] { _gpuPool, _vramPool, _gpuTempPool, _gpuFanPool, _netPool,
						 _volumePool, _freePool, _diskPool, _raidPool, _fanPool };

		_families = new (string, Action)[]
		{
			("ядро", ShowCore), ("GPU", ShowGpus), ("диски", ShowDisks), ("RAID", ShowRaidDisks),
			("вентиляторы", ShowFans), ("сеть", ShowNetwork), ("тома", ShowVolumes),
			("свободное место", ShowFreeSpace), ("ИБП", ShowUps), ("батарея", ShowBattery),
			("время работы", ShowUptime), ("сводный значок", ShowWorst),
		};

		// A click on any menu item closes the whole chain, submenu included, so ticking five icons
		// in the list meant reopening the menu five times. The close is cancelled while that list
		// is the thing being used — and only then: every other item here is a command, and a
		// command that leaves its menu on the screen is worse than the reopening was.
		_menu.Closing += (_, e) =>
		{
			if (_pickingIcons && e.CloseReason == ToolStripDropDownCloseReason.ItemClicked) e.Cancel = true;
		};

		if (_dry) return;

		Spawn(RefreshDisks);   // first SMART query off the UI thread
		Spawn(RefreshUps);
		Spawn(RefreshSlow);

		_ = _sync.Handle;   // created here, on the UI thread, so BeginInvoke has somewhere to land

		// Nobody is looking at a locked or disconnected session, so nothing there is worth two
		// seconds of a core.
		Microsoft.Win32.SystemEvents.SessionSwitch += OnSessionSwitch;
		// The one moment a dirty settings file and a tray full of icons both have to be dealt
		// with at once, and the only notice given.
		Microsoft.Win32.SystemEvents.SessionEnding += OnSessionEnding;
		WatchConfigFile();

		_timer = new System.Windows.Forms.Timer { Interval = _intervalMs };
		_timer.Tick += OnTick;
		_timer.Start();
		OnTick(null, EventArgs.Empty);
	}

	/// <summary>
	/// Notices the settings file being edited, so "Перечитать настройки" is a fallback rather
	/// than a requirement. Debounced: an editor writes a file in more than one step.
	/// </summary>
	private void WatchConfigFile()
	{
		try
		{
			_configWatcher = new FileSystemWatcher(AppContext.BaseDirectory, "TrayMon.json")
			{
				NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName,
				EnableRaisingEvents = true,
			};
			// Only a timestamp is taken here: this runs on a thread-pool thread, and everything
			// the reload touches — icons, the menu, dialogs — belongs to the UI thread. The tick
			// picks it up.
			FileSystemEventHandler touched = (_, _) => Volatile.Write(ref _configTouchedAt, Environment.TickCount64);
			_configWatcher.Changed += touched;
			_configWatcher.Created += touched;
			_configWatcher.Renamed += (_, _) => Volatile.Write(ref _configTouchedAt, Environment.TickCount64);
		}
		catch (Exception ex)
		{
			// A folder that cannot be watched (a network share, a policy) is not a reason to stop.
			Trouble(ex, "наблюдение за файлом настроек");
		}
	}

	private void OnSessionSwitch(object sender, Microsoft.Win32.SessionSwitchEventArgs e)
	{
		// Off the SystemEvents thread first: everything below is UI-thread state.
		if (_sync.InvokeRequired)
		{
			try { _sync.BeginInvoke(new Action(() => OnSessionSwitch(sender, e))); }
			catch (Exception) { /* shutting down; the handle is gone */ }
			return;
		}

		var idle = e.Reason is Microsoft.Win32.SessionSwitchReason.SessionLock
							or Microsoft.Win32.SessionSwitchReason.SessionLogoff
							or Microsoft.Win32.SessionSwitchReason.RemoteDisconnect
							or Microsoft.Win32.SessionSwitchReason.ConsoleDisconnect;
		var busy = e.Reason is Microsoft.Win32.SessionSwitchReason.SessionUnlock
							or Microsoft.Win32.SessionSwitchReason.SessionLogon
							or Microsoft.Win32.SessionSwitchReason.RemoteConnect
							or Microsoft.Win32.SessionSwitchReason.ConsoleConnect;
		if (!idle && !busy) return;
		_sessionIdle = idle;
		if (_timer is null) return;
		_timer.Interval = idle ? Math.Max(IdleTickMs, _intervalMs) : _intervalMs;
		// Coming back: refresh at once rather than up to thirty seconds later, because the first
		// thing somebody does after unlocking is look at the icons.
		if (busy) OnTick(null, EventArgs.Empty);
	}

	private void OnSessionEnding(object sender, Microsoft.Win32.SessionEndingEventArgs e)
	{
		// Blocking, not BeginInvoke: Windows is waiting for this to return before it tears the
		// session down, and an icon left registered comes back as a ghost in the next one.
		if (_sync.InvokeRequired)
		{
			try { _sync.Invoke(new Action(() => OnSessionEnding(sender, e))); }
			catch (Exception) { /* already going */ }
			return;
		}
		try
		{
			if (_configDirty) { _config.Save(); _configDirty = false; }
			foreach (var slot in _order) { slot.Icon?.Dispose(); slot.Icon = null; }
		}
		catch (Exception ex) { Trouble(ex, "завершение сеанса"); }
	}

	/// <summary>
	/// One tick without the tray: builds every slot, assigns every GUID and formats every
	/// number, then reports it. This is the part --once never exercised, and it is the part the
	/// project calls its most fragile — GUID assignment, dead sources, conditional icons.
	/// </summary>
	public string DryRun(int ticks = 1)
	{
		for (var i = 0; i < ticks; i++)
		{
			if (i > 0) Thread.Sleep(TickMs);   // real spacing, so rate counters mean something
			_perf.Read(_r, true);
			MemorySensor.Read(_r);
			BatterySensor.Read(_r);
			_gpu.Read(_r);
			_r.Slow = _lhm.ReadSlow();
			_r.Disks = Program.DiskTemps(_disk, _lhm);
			_r.RaidDisks = _hdd.ReadAll();
			_ups.Read(_r);
			_r.Space = SpaceSensor.Read(_r.Volumes.Select(v => v.Name));
			_r.UptimeHours = PerfSensors.ReadUptime();
			OnTick(null, EventArgs.Empty);   // through OnTick, so the tick times itself here too
		}

		var report = new StringBuilder();
		report.AppendLine($"icons: {_order.Count(s => s.Settings.Enabled)} shown of {_order.Count} known" +
						  $"   ticks: {ticks}");
		foreach (var slot in _order)
			report.AppendLine(
				$"  {(slot.Settings.Enabled ? "on " : "off")} {slot.Guid.ToString()[^2..]}  " +
				$"{slot.Id,-28} {(slot.LastText ?? "—"),5}  {LabelOf(slot)}   {slot.LastDetail}");
		foreach (var line in Overflows()) report.AppendLine("  " + line);
		// Renders and shell calls are zero here by construction, and saying so matters: they are
		// the two numbers a change to the tray path is judged by, and a run that prints "0 / 0"
		// without explanation looks exactly like the broken P/Invoke they exist to catch. The
		// place to read them is "Диагностика…" in a running program.
		report.AppendLine("shell not touched in dry run — renders and shell calls stay 0 here; " +
						  "read them in «Диагностика…»");
		report.AppendLine($"tick {(_tickTicks * 1000.0 / Stopwatch.Frequency / Math.Max(1, _tick)).ToString("0.00", CultureInfo.InvariantCulture)} ms");
		return report.ToString();
	}

	// ---- the tick ----

	private void OnTick(object sender, EventArgs e)
	{
		// One bad tick must cost one tick, not the program. The sources that throw are real —
		// NVML after a driver reset, the sensor library while its driver unloads — and the
		// alternative here is a modal dialog every two seconds on a machine nobody is looking at.
		// The tick times itself. Two QueryPerformanceCounter calls per two seconds cost nothing,
		// and until now the running program could not answer the one question it exists to
		// answer — what does watching this machine cost — outside of a console mode that needs
		// an elevated console to start.
		// Re-entry guard. A modal dialog runs its own message loop, so the timer went on firing
		// underneath it and Tick() ran inside itself; worse, the outer tick was still being timed,
		// so the hours a message box spent in front of an empty chair on a headless server were
		// charged to "average tick time". Dialogs are now raised after the measurement, outside
		// this guard, and a tick that arrives while one is up simply keeps the icons current.
		if (_inTick) return;
		_inTick = true;
		var started = Stopwatch.GetTimestamp();
		try { Tick(); }
		catch (Exception ex) { Trouble(ex); }
		finally
		{
			_tickTicks += Stopwatch.GetTimestamp() - started;
			_inTick = false;
		}
		FlushNotices();
	}

	/// <summary>Shows whatever the tick wanted to say, outside the tick and outside its clock.</summary>
	private void FlushNotices()
	{
		if (_notices.Count == 0 || _dry) return;
		var pending = _notices.ToArray();
		_notices.Clear();
		foreach (var (text, icon) in pending) Info(text, icon);
	}

	/// <summary>Queues a message box to be shown once the tick is over. See <see cref="OnTick"/>.</summary>
	private void Later(string text, MessageBoxIcon icon = MessageBoxIcon.Information) =>
		_notices.Add((text, icon));

	private void Tick()
	{
		_tick++;
		_perf.Read(_r, _tick % IoEveryTicks == 0);   // CPU every tick, network and volumes every third
		MemorySensor.Read(_r);
		BatterySensor.Read(_r);
		// Free: GetTickCount64, not a PDH query over the System object. See PerfSensors.ReadUptime.
		_r.UptimeHours = PerfSensors.ReadUptime();
		if (_tick % GpuEveryTicks == 0) _gpu.Read(_r);

		// The heavy readers are all off the UI thread now, each behind its own flag. The sensor
		// library alone measured 18-45 ms, which is a fifth of a second of a frozen message pump
		// every six seconds when it ran here.
		if (_tick % SlowEveryTicks == 1) Spawn(RefreshSlow);
		// The ~250-instance process query is the most expensive PDH read here and was the one
		// thing left running on the UI thread, against a README that said otherwise.
		if (_tick % TopIoEveryTicks == 4 && _r.Volumes.Sum(v => v.ReadMb + v.WriteMb) > 0.5)
			Spawn(RefreshTopIo);
		if (_tick % DiskEveryTicks == 5) Spawn(RefreshDisks);
		// Offsets 1 and 2: smartctl and the SNMP round trip are the two slowest things here, and
		// starting them on the same tick would put both on the pool at once for no reason.
		if (_tick % RaidDiskEveryTicks == 1 && Environment.TickCount64 >= _raidRetryAt) Spawn(RefreshRaidDisk);
		if (_tick % UpsEveryTicks == 2 && Environment.TickCount64 >= _upsRetryAt) Spawn(RefreshUps);
		if (_tick % SpaceEveryTicks == 7)
		{
			// The names are taken here and not inside the task: the volume list belongs to the UI
			// thread and is cleared and refilled on it every third tick.
			var names = _r.Volumes.Select(v => v.Name).ToArray();
			Spawn(() => RefreshSpace(names));
		}

		// Each family on its own: a source that throws — NVML after a driver reset, the sensor
		// library while its driver unloads — must cost its own icons, not every icon that
		// happens to be displayed after it in this method. The array is built once, so the loop
		// allocates nothing per tick.
		foreach (var family in _families)
		{
			try { family.Show(); }
			catch (Exception ex) { Trouble(ex, family.Name); }
		}

		Fade();
		FirstRunHousekeeping();
		// Once the first tick has registered everything this machine has: a settings file with
		// every icon switched off is a legitimate hand edit, and it used to leave a process with
		// no icons, no menu and no way out but Task Manager.
		// On the second tick, not the first: the first runs inside the constructor, before the
		// message loop exists, and these can put a dialog on the screen.
		if (_tick == 2 && !_dry)
		{
			// Losing every colour, label and threshold is not something to mention only to
			// someone who thinks to open the diagnostics window.
			if (_config.LoadError is not null)
				Later($"Файл настроек не прочитан, взяты значения по умолчанию:\n\n{_config.LoadError}",
					MessageBoxIcon.Warning);
			EnsureSomethingVisible();
		}
		if (_tick % StatsEveryTicks == 3) RefreshStats();
		PickUpConfigEdit();
		WriteLogLine();
	}

	/// <summary>
	/// Applies an edit made to the file while the program runs. One second of quiet after the
	/// last write, because an editor saves in more than one step, and only when the file really
	/// is newer than the one in memory — our own Save touches it too, and reloading after every
	/// menu click would be a loop.
	/// </summary>
	private void PickUpConfigEdit()
	{
		var touched = Volatile.Read(ref _configTouchedAt);
		if (touched == 0 || Environment.TickCount64 - touched < 1000) return;
		Volatile.Write(ref _configTouchedAt, 0);
		if (_dry || !_config.ChangedOnDisk) return;
		ReloadConfig(silent: true);
	}

	/// <summary>CPU, memory and the CPU package temperature — the icons every machine has.</summary>
	private void ShowCore()
	{
		var ci = CultureInfo.InvariantCulture;

		var cpu = Slot("cpu", CpuMetric, CpuGuid);
		Record(cpu, _r.CpuLoad);
		Show(cpu, Whole(_r.CpuLoad), _r.CpuLoad, $"{Pct(_r.CpuLoad)}   температура {Deg(_r.Slow.CpuTemp)}{cpu.StatsText}");

		// 88/95 rather than 80/90: on this hypervisor 156 of 192 GB in use is the normal
		// resting state, and a plate that is permanently yellow says nothing.
		var ram = Slot("ram", RamMetric, RamGuid);
		// Recorded in the unit the icon draws (gigabytes), coloured by the unit the thresholds
		// are in (percent) — otherwise the five-minute line in the tooltip is in a different
		// unit from the number above it.
		Record(ram, _r.MemLoad.HasValue ? _r.MemUsedGb : null, _r.MemLoad);
		// Commit charge next to physical use: it is the number that answers "why is everything
		// paging while RAM sits at 60 %", and it comes out of the same syscall.
		var commit = _r.CommitUsedGb.HasValue
			? $"   выделено {_r.CommitUsedGb.Value.ToString("0.0", ci)} / {_r.CommitTotalGb.ToString("0.0", ci)} ГБ"
			: "";
		Show(ram, Whole(_r.MemLoad.HasValue ? _r.MemUsedGb : null), _r.MemLoad,
			$"{_r.MemUsedGb.ToString("0.0", ci)} / {_r.MemTotalGb.ToString("0.0", ci)} ГБ   {Pct(_r.MemLoad)}{commit}{ram.StatsText}");

		// Only when there is a sensor library to read at all. A machine with no NVIDIA card used
		// to carry three permanent grey dashes and one without an elevated token a fourth, while
		// the README promised the opposite. What is missing now says why, in the tooltip.
		if (!_lhm.Available) return;
		var slow = _r.Slow;
		var cpuTemp = Slot("cpu.temp", CpuTempMetric, CpuTempGuid);
		Record(cpuTemp, slow.CpuTemp);
		Show(cpuTemp, Whole(slow.CpuTemp), slow.CpuTemp, $"пакет {Deg(slow.CpuTemp)}{cpuTemp.StatsText}");
	}

	/// <summary>
	/// Turns a slot that stopped being fed into a grey dash with a reason, and eventually gives
	/// its tray identity back. A monitor that keeps showing the last number of a source that
	/// went away is worse than one that says nothing: "network 0 Mbit/s" and "no network cable"
	/// look identical, and only one of them is true.
	/// </summary>
	private void Fade()
	{
		for (var i = _order.Count - 1; i >= 0; i--)
		{
			var slot = _order[i];
			if (slot.SeenTick == _tick) { slot.Dead = false; continue; }

			if (!slot.Dead)
			{
				slot.Dead = true;
				// What it last said, and when. "— the adapter is gone from the system" answers
				// half the question; the other half is what it was showing before it went.
				if (slot.LastText is not null) { slot.FarewellText = slot.LastText; slot.FarewellAt = DateTime.Now; }
				slot.LastText = null;
				slot.LastSeverity = null;
				slot.LastDetail = Reason(slot) + Farewell(slot);
				slot.Level = -1;
			}

			// A dead slot the user is showing still gets an icon. Without this a machine whose
			// only enabled icon cannot answer — cpu.temp with no elevation, the UPS before its
			// first reply — had no icon at all, and therefore no menu and no way to quit, while
			// both README versions promised a grey icon saying why.
			if (slot.Settings.Enabled) Push(slot, null, null);

			// Gone for a day: hand the identity back so the next adapter or volume can have it.
			// Without this the pool of eight fills up with names last seen in March, and the
			// menu fills with them too.
			if (slot.Pool is null || Environment.TickCount64 - slot.SeenAtMs <= ForgetAfterMs) continue;
			slot.Icon?.Dispose();
			slot.Icon = null;
			slot.Pool.Release(slot.PoolKey);
			_slots.Remove(slot.Id);
			_order.RemoveAt(i);
		}
	}

	private static string Farewell(IconSlot slot) =>
		slot.FarewellText is null
			? ""
			: $", последнее {slot.FarewellText} в {slot.FarewellAt.ToString("HH:mm", CultureInfo.InvariantCulture)}";

	/// <summary>Why an icon went grey — the text that turns a silent dash into an explanation.</summary>
	private string Reason(IconSlot slot)
	{
		if (slot.Id.StartsWith("net.", StringComparison.Ordinal))
			// "The adapter is gone" and "the cable is out" are different answers, and the icon
			// exists precisely to tell them apart. PDH still lists a disconnected adapter.
			return _perf.AdapterStillThere(slot.PoolKey ?? "")
				? "нет линка — кабель отключён или сеть недоступна"
				: "адаптер пропал из системы";
		if (slot.Id.StartsWith("vol.", StringComparison.Ordinal) ||
			slot.Id.StartsWith("free.", StringComparison.Ordinal)) return "том отключён";
		if (slot.Id.StartsWith("disk.raid.", StringComparison.Ordinal))
			return _hdd.LastError ?? "диск за RAID не отвечает";
		if (slot.Id.StartsWith("disk.", StringComparison.Ordinal))
			return _disk.LastError ?? "диск не отдал температуру";
		if (slot.Id.StartsWith("fan.gpu", StringComparison.Ordinal)) return _gpu.LastError ?? "видеокарта не отвечает";
		if (slot.Id.StartsWith("fan.", StringComparison.Ordinal)) return SensorHint("датчик пропал");
		if (slot.Id == "ups")
			return _ups.ChargeUnavailable
				? "агент отвечает, но не отдаёт заряд батареи — похоже, это не APC PowerNet MIB"
				: _ups.LastError ?? "нет ответа от SNMP-агента";
		if (slot.Id == "battery") return "Windows не сообщает о батарее";
		if (slot.Id == "cpu.temp") return SensorHint("датчик не отвечает");
		if (slot.Id.StartsWith("gpu", StringComparison.Ordinal) ||
			slot.Id.StartsWith("vram", StringComparison.Ordinal)) return _gpu.LastError ?? "нет драйвера NVIDIA";
		return "источник молчит";
	}

	/// <summary>
	/// Why a sensor-library reading is missing. The elevation check comes first on purpose: the
	/// library opens perfectly well without an elevated token and then returns null for every
	/// temperature, so "Available" is not the same as "can read", and the user needs to be told
	/// the one thing that would actually fix it.
	/// </summary>
	private string SensorHint(string whenWorking) =>
		!IsElevated ? "нужен запуск от администратора — температуры читаются через драйвер"
		: !_lhm.Available ? "драйвер датчиков не загрузился" + (_lhm.LastError is null ? "" : ": " + _lhm.LastError)
		// The one state nothing in the program could fix and nothing reported: the library opens,
		// says it is fine, and its driver is being refused by HVCI or removed by the antivirus.
		: _lhm.DriverBlocked ? _lhm.LastError
		: whenWorking;

	// ---- the metric families ----

	private void ShowGpus()
	{
		foreach (var g in _r.Gpus)
		{
			var key = g.Index.ToString(CultureInfo.InvariantCulture);
			var suffix = _r.Gpus.Count > 1 ? $" {g.Index + 1}" : "";

			var load = Pooled($"gpu.{key}", GpuMetric, _gpuPool, key, GpuMetric.Label + suffix);
			if (load is not null)
			{
				Record(load, g.Load);
				Show(load, Whole(g.Load), g.Load, $"{g.Name}   ядро {Pct(g.Load)}   температура {Deg(g.Temp)}{load.StatsText}");
			}

			var vram = Pooled($"vram.{key}", VramMetric, _vramPool, key, VramMetric.Label + suffix);
			if (vram is not null)
			{
				Record(vram, g.MemLoad.HasValue ? g.MemUsedGb : null, g.MemLoad);
				Show(vram, Whole(g.MemLoad.HasValue ? g.MemUsedGb : null), g.MemLoad,
					$"{g.MemUsedGb.ToString("0.0", CultureInfo.InvariantCulture)} / " +
					$"{g.MemTotalGb.ToString("0.0", CultureInfo.InvariantCulture)} ГБ   {Pct(g.MemLoad)}{vram.StatsText}");
			}

			var temp = Pooled($"gpu.temp.{key}", GpuTempMetric, _gpuTempPool, key, GpuTempMetric.Label + suffix);
			if (temp is not null)
			{
				Record(temp, g.Temp);
				Show(temp, Whole(g.Temp), g.Temp, $"{Deg(g.Temp)}   загрузка {Pct(g.Load)}{temp.StatsText}");
			}

			if (!g.FanRpm.HasValue && !g.FanDuty.HasValue) continue;
			// A driver without nvmlDeviceGetFanSpeedRPM leaves only the duty cycle, and a bare
			// "35" next to fan icons where "1.2" means twelve hundred revolutions is a different
			// unit with nothing to say so. The caption carries the unit in that case.
			var byDuty = !g.FanRpm.HasValue;
			var fanLabel = GpuFanMetric.Label + suffix + (byDuty ? " (%)" : "");
			var fan = Pooled($"fan.gpu.{key}", GpuFanMetric, _gpuFanPool, key, fanLabel);
			if (fan is null) continue;
			var speed = g.FanRpm ?? g.FanDuty ?? 0;
			Record(fan, speed, Stalled(speed));
			Show(fan,
				byDuty ? Whole(g.FanDuty) : Rpm(g.FanRpm.Value),
				Stalled(speed),
				byDuty
					? $"{Pct(g.FanDuty)} задания (драйвер не сообщает обороты){fan.StatsText}"
					: $"{g.FanRpm.Value.ToString("0", CultureInfo.InvariantCulture)} об/мин   задание {Pct(g.FanDuty)}{fan.StatsText}");
		}
	}

	/// <summary>
	/// One icon per directly attached disk, keyed by model — never by position in the list.
	/// A USB NVMe enclosure, or simply a different enumeration order after a reboot, moved every
	/// index along: "disk.0" then kept the label of one drive while showing the temperature of
	/// another, and carried its colour and thresholds over with it. Identical models are told
	/// apart by an ordinal suffix, which is the best this source offers — the storage property
	/// query returns a model, not a serial.
	/// </summary>
	private void ShowDisks()
	{
		// Version first, list second. The background task writes the list and *then* bumps the
		// version, so reading them the other way round could pair an old list with a new version:
		// the old numbers were shown, the new version recorded, and the fresh list counted as
		// already displayed — up to 62 seconds of showing the previous reading.
		var version = Volatile.Read(ref _disksVersion);
		var disks = _r.Disks;   // one read of the field: it is replaced whole from a pool thread
		var changed = version != _shownDisks;
		// Keys computed once per published list, not once per tick: the list is replaced whole
		// every 62 seconds and the keys cannot change without it.
		if (!ReferenceEquals(disks, _diskListSeen))
		{
			_diskListSeen = disks;
			_diskKeysOfList = DiskKeys(disks);
		}

		for (var i = 0; i < disks.Count && i < _diskKeysOfList.Length; i++)
		{
			var d = disks[i];
			var key = _diskKeysOfList[i];
			var slot = Pooled($"disk.{key}", DiskMetric, _diskPool, key, key);
			if (slot is null) continue;

			// A disk that is wearing out matters more than a disk that is warm — the same
			// argument that already puts a failing RAID disk straight into the red.
			Record(slot, d.Temp, d.Worn ? NeverAlerts : d.Temp);
			if (!changed) continue;
			var wear = d.WearPercent.HasValue
				? $"   износ {d.WearPercent.Value.ToString("0", CultureInfo.InvariantCulture)}%" : "";
			var spare = d.SparePercent.HasValue
				? $"   резерв {d.SparePercent.Value.ToString("0", CultureInfo.InvariantCulture)}%" : "";
			Show(slot, Whole(d.Temp), d.Worn ? NeverAlerts : d.Temp,
				$"{Deg(d.Temp)}{wear}{spare}{(d.Worn ? "   РЕСУРС ИСЧЕРПАН" : "")}{slot.StatsText}");
		}
		_shownDisks = version;
	}

	/// <summary>
	/// A key per disk: the model, plus an ordinal when the machine has more than one of the same
	/// model. Not the index in the list — a USB NVMe enclosure, or a different enumeration order
	/// after a reboot, moved every index along, and "disk.0" then kept one drive's label while
	/// showing another's temperature, colour and thresholds included. The storage property query
	/// returns a model and not a serial number, so this is as stable as this source gets.
	/// </summary>
	private static string[] DiskKeys(List<DiskReading> disks)
	{
		var keys = new string[disks.Count];
		var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
		for (var i = 0; i < disks.Count; i++)
		{
			counts.TryGetValue(disks[i].Name, out var seen);
			counts[disks[i].Name] = seen + 1;
			keys[i] = seen == 0
				? disks[i].Name
				: $"{disks[i].Name}#{(seen + 1).ToString(CultureInfo.InvariantCulture)}";
		}
		return keys;
	}

	/// <summary>
	/// One icon per disk behind a RAID controller, keyed by serial number — so the icon and its
	/// settings follow the disk, and the tray identity follows the icon, even when the controller
	/// renumbers its ports.
	/// </summary>
	private void ShowRaidDisks()
	{
		var version = Volatile.Read(ref _raidVersion);   // version before data — see ShowDisks
		var disks = _r.RaidDisks;
		var changed = version != _shownRaid;
		for (var i = 0; i < disks.Count; i++)
		{
			var d = disks[i];
			var key = string.IsNullOrEmpty(d.Serial) ? i.ToString(CultureInfo.InvariantCulture) : d.Serial;
			var label = string.IsNullOrEmpty(d.Serial) ? d.Name : $"{d.Name} {d.Serial}";
			var slot = Pooled($"disk.raid.{key}", RaidMetric, _raidPool, key, label);
			if (slot is null) continue;

			// A disk that is failing matters more than a disk that is warm, so a bad health
			// verdict takes the plate straight to red whatever the temperature says.
			var failing = HddSensor.Failing(d.Health);
			Record(slot, d.Temp, failing ? NeverAlerts : d.Temp);
			if (!changed) continue;
			var health = string.IsNullOrEmpty(d.Health) ? "" : failing ? $"   ЗДОРОВЬЕ: {d.Health}" : "   здоровье в норме";
			Show(slot, Whole(d.Temp), failing ? NeverAlerts : d.Temp, $"{Deg(d.Temp)}{health}{slot.StatsText}");
		}
		_shownRaid = version;
	}

	/// <summary>
	/// One icon per fan that was turning at startup, plus every GPU fan. A stopped fan reads 0
	/// and goes red — that is the state worth noticing, so severity is inverted here.
	/// </summary>
	private void ShowFans()
	{
		var slow = _r.Slow;
		var fans = slow.Fans;
		if (!ReferenceEquals(fans, _fanListSeen))
		{
			_fanListSeen = fans;
			_fanKeysOfList = FanKeys(fans);
		}

		for (var i = 0; i < fans.Count && i < _fanKeysOfList.Length; i++)
		{
			var fan = fans[i];
			var key = _fanKeysOfList[i];

			// The set of fans grows as they are seen turning, and is never latched. A header with
			// nothing plugged in reads zero for ever and gets no icon, which is the point — but so
			// does a case fan in its zero-rpm mode at the moment of the first poll, and latching
			// the list there meant it never got one until the program was restarted.
			if (fan.Rpm > 0) _fanSeen.Add(key);
			if (!_fanSeen.Contains(key)) continue;

			var label = fan.Name == CpuFanSensorName ? $"Вентилятор CPU ({fan.Name})" : key;
			var slot = Pooled($"fan.{key}", FanMetric, _fanPool, key, label);
			if (slot is null) continue;
			Record(slot, fan.Rpm, Stalled(fan.Rpm));
			var duty = fan.Duty.HasValue ? $"   задание {Pct(fan.Duty)}" : "";
			Show(slot, Rpm(fan.Rpm), Stalled(fan.Rpm),
				$"{fan.Rpm.ToString("0", CultureInfo.InvariantCulture)} об/мин{duty}{slot.StatsText}");
		}
	}

	/// <summary>
	/// A stable key per fan. Names are ambiguous across chips: a board with a Nuvoton and an ITE
	/// reports two sensors both called "Fan #1", and a lookup by name found whichever came first.
	/// A bare name is kept while it is unique on this board — so everybody's existing
	/// <c>fan.Fan #1</c> setting goes on working — and qualified by the chip only where it must be.
	/// </summary>
	private static string[] FanKeys(List<FanReading> fans)
	{
		var keys = new string[fans.Count];
		for (var i = 0; i < fans.Count; i++)
		{
			var duplicate = false;
			for (var j = 0; j < fans.Count && !duplicate; j++)
				duplicate = j != i && fans[j].Name == fans[i].Name;
			keys[i] = duplicate ? $"{fans[i].Chip}/{fans[i].Name}" : fans[i].Name;
		}
		return keys;
	}

	/// <summary>
	/// One icon per physical adapter, received plus sent; virtual switch chatter between VMs is
	/// not counted. An adapter with no link and no traffic — an empty Ethernet socket next to the
	/// Wi-Fi actually in use — gets no icon at all.
	/// </summary>
	private void ShowNetwork()
	{
		foreach (var n in _r.Nets)
		{
			// continue, not break. The list is sorted by name, so one adapter without a free
			// slot in the middle of the alphabet used to silence every adapter after it —
			// including ones whose icons were already registered and working.
			var slot = Pooled($"net.{n.Name}", NetMetric, _netPool, n.Name, n.Name);
			if (slot is null) continue;

			var total = n.InMb + n.OutMb;
			// The heavier direction against the link, not the sum of both. A link is full duplex:
			// 45 % in and 45 % out is a quiet line, and adding them made it yellow — with a
			// maximum of 200 % on the scale the thresholds are expressed in.
			var utilisation = n.LinkMb > 0 ? 100 * Math.Max(n.InMb, n.OutMb) / n.LinkMb : 0;
			Record(slot, total * 8, utilisation);   // drawn in megabits, coloured by % of the link
			var of = n.LinkMb > 0
				? $"   {utilisation.ToString("0", CultureInfo.InvariantCulture)}% от " +
				  $"{(n.LinkMb * 8).ToString("0", CultureInfo.InvariantCulture)} Мбит/с"
				: "";
			// The five-minute line goes before the units, not after: a tooltip is cut at 127
			// characters, an adapter description takes most of them, and what was being cut off
			// was the only part that does not change.
			Show(slot, MbitWhole(total), utilisation,
				$"↓ {Mbit(n.InMb)} / ↑ {Mbit(n.OutMb)} Мбит/с{slot.StatsText}\n" +
				$"{Mb2(n.InMb)} / {Mb2(n.OutMb)} МБ/с{of}");
		}
	}

	/// <summary>One icon per lettered volume, showing read plus write.</summary>
	private void ShowVolumes()
	{
		var built = false;
		foreach (var v in _r.Volumes)
		{
			var slot = Pooled($"vol.{v.Name}", VolumeMetric, _volumePool, v.Name, v.Name);
			if (slot is null) continue;
			var total = v.ReadMb + v.WriteMb;
			Record(slot, total, 0);   // volumes never alarm: there is no "too much I/O"

			// Built at most once per tick: the line does not depend on the volume, and the list
			// behind it is refreshed once every twelve seconds.
			if (!built)
			{
				built = true;
				var top = _r.TopIo;
				_topIoLine = top.Count > 0
					? "\n" + string.Join(" · ", top.Select(t => $"{t.Name} {Mb2(t.Mb)}"))
					: "";
			}
			Show(slot, Mb(total), 0,
				$"{Mb2(total)} МБ/с   чтение {Mb2(v.ReadMb)} / запись {Mb2(v.WriteMb)}" +
				// Per-process figures, for every device at once and for network and pipes as
				// well as disks — the counter is IO Data Bytes/sec, and calling it "I/O across
				// all devices" under a *volume* icon read as if it were this volume's disk
				// traffic. Splitting it by volume needs a kernel trace costing 5-10 % of a core,
				// which is the whole budget of this program.
				$"{(_topIoLine.Length > 0 ? "\nввод-вывод процессов (диск + сеть):" + _topIoLine : "")}");
		}
	}

	/// <summary>Sources that found no free tray slot, so the limits are not silent any more.</summary>
	private List<string> Overflows() => _pools
		.Where(p => p.Refused > 0)
		.Select(p => $"{p.What}: без значка осталось {p.Refused.ToString(CultureInfo.InvariantCulture)} " +
					 $"(заготовлено {p.Size.ToString(CultureInfo.InvariantCulture)})")
		.ToList();

	/// <summary>Free space per volume — whole gigabytes, so the icon repaints once in hours.</summary>
	private void ShowFreeSpace()
	{
		foreach (var s in _r.Space)
		{
			var slot = Pooled($"free.{s.Name}", FreeMetric, _freePool, s.Name, $"Свободно {s.Name}");
			if (slot is null) continue;
			var usedPercent = s.TotalGb > 0 ? 100 * (s.TotalGb - s.FreeGb) / s.TotalGb : 0;
			Record(slot, s.FreeGb, usedPercent);   // drawn in gigabytes free, coloured by % used

			// Terabytes past a thousand gigabytes. A RAID volume on a server has five digits of
			// free space, the drawing code knows steps only up to four characters, and the fifth
			// was drawn straight off the edge of a 16-pixel icon. The unit moves into the caption,
			// the way the GPU fan says "(%)" when the driver only gives a duty cycle.
			var inTb = s.FreeGb >= 1000;
			slot.DefaultLabel = inTb ? $"Свободно {s.Name} (ТБ)" : $"Свободно {s.Name}";
			Show(slot, inTb ? (s.FreeGb / 1024).ToString("0.0", CultureInfo.InvariantCulture) : Whole(s.FreeGb),
				usedPercent,
				$"{s.FreeGb.ToString("0.0", CultureInfo.InvariantCulture)} ГБ свободно из " +
				$"{s.TotalGb.ToString("0.0", CultureInfo.InvariantCulture)}   занято {Pct(usedPercent)}{slot.StatsText}");
		}
	}

	/// <summary>
	/// Charge of the UPS, once its SNMP agent has answered. Severity is inverted — a low charge
	/// is what matters — and running on battery goes straight to red whatever the charge is,
	/// because that is the state worth walking over to the rack for.
	/// </summary>
	private void ShowUps()
	{
		if (!_ups.Present) return;

		var version = Volatile.Read(ref _upsVersion);   // version before data — see ShowDisks
		var ups = _r.Ups;   // one read: charge and the battery flag must come from one answer
		var slot = Slot("ups", UpsMetric, UpsGuid);
		var severity = UpsSeverity(slot, ups);
		Record(slot, ups.Charge, severity);

		// Going onto battery is the event a UPS is bought for, and until now only somebody
		// staring at the tray would see it. Checked before the "nothing changed" shortcut and
		// outside the enabled check — a hidden icon is not a reason to stay quiet about it.
		if (ups.OnBattery.HasValue && _lastOnBattery.HasValue && ups.OnBattery != _lastOnBattery)
		{
			var left = ups.RunTimeMin.HasValue
				? $", ещё {ups.RunTimeMin.Value.ToString("0", CultureInfo.InvariantCulture)} мин"
				: "";
			var what = ups.OnBattery.Value
				? $"ИБП перешёл на батарею. Заряд {Pct(ups.Charge)}{left}."
				: "Питание от сети восстановлено.";
			Balloon("TrayMon — ИБП", what, warning: ups.OnBattery.Value);
			WindowsLog.Write("TrayMon: " + what, critical: ups.OnBattery.Value);
		}
		if (ups.OnBattery.HasValue) _lastOnBattery = ups.OnBattery;

		// Nothing known at all: Fade() greys the icon and puts the reason in the tooltip, which
		// is more honest than the old behaviour of keeping the last "on line" text next to a
		// plate that meant "no answer".
		if (!ups.Answered) return;
		if (version == _shownUps) return;
		_shownUps = version;

		var minutes = ups.RunTimeMin.HasValue
			? $"   ещё {ups.RunTimeMin.Value.ToString("0", CultureInfo.InvariantCulture)} мин"
			: "";
		var load = ups.Load.HasValue ? $"   нагрузка {Pct(ups.Load)}" : "";
		var replace = ups.NeedsNewBattery ? "   ТРЕБУЕТ ЗАМЕНЫ БАТАРЕИ" : "";
		Show(slot, Whole(ups.Charge), severity, $"{Pct(ups.Charge)} {ups.StatusText}{minutes}{load}{replace}");
	}

	/// <summary>
	/// How bad the UPS state is, on the inverted scale the plate is coloured by.
	///
	/// The charge is only one of the answers. A worn battery reporting 100 % and three minutes of
	/// runtime is a UPS that will not survive the next outage; so is one running the load through
	/// a bypass, or one asking to be replaced. All of those were text in a tooltip nobody reads.
	/// </summary>
	private double? UpsSeverity(IconSlot slot, UpsReading ups)
	{
		if (!ups.Answered) return null;
		if (ups.OnBattery == true) return 100;
		if (!ups.Charge.HasValue && !ups.Degraded && !ups.NeedsNewBattery) return null;

		var severity = ups.Charge.HasValue ? 100 - ups.Charge.Value : 0;
		// Under five minutes of runtime is red whatever the charge gauge claims.
		if (ups.RunTimeMin is < 5) severity = Math.Max(severity, CritOf(slot));
		if (ups.Degraded || ups.NeedsNewBattery) severity = Math.Max(severity, WarnOf(slot));
		return severity;
	}

	/// <summary>The battery of a laptop, on the same inverted scale as the UPS.</summary>
	private void ShowBattery()
	{
		var battery = _r.Battery;
		if (battery is null) return;
		var slot = Slot("battery", BatteryMetric, BatteryGuid);
		var severity = battery.OnBattery ? 100 - battery.Charge : 0;
		Record(slot, battery.Charge, severity);
		var left = battery.MinutesLeft.HasValue
			? $"   ещё {battery.MinutesLeft.Value.ToString("0", CultureInfo.InvariantCulture)} мин"
			: "";
		Show(slot, Whole(battery.Charge), severity,
			$"{Pct(battery.Charge)} {(battery.OnBattery ? "от батареи" : "от сети")}{left}{slot.StatsText}");
	}

	private void ShowUptime()
	{
		if (!_r.UptimeHours.HasValue) return;
		var slot = Slot("uptime", UptimeMetric, UptimeGuid);
		Record(slot, _r.UptimeHours.Value, 0);   // a long uptime is not an alarm
		var hours = _r.UptimeHours.Value;
		var days = (int)(hours / 24);
		var detail = days > 0
			? $"{days} сут {(hours - days * 24).ToString("0", CultureInfo.InvariantCulture)} ч без перезагрузки"
			: $"{hours.ToString("0.0", CultureInfo.InvariantCulture)} ч без перезагрузки";
		// Severity 0, not null: null is the code for "this source is dead" and would paint the
		// plate grey for ever. Uptime is never alarming, it just has nothing to warn about.
		Show(slot, Whole(hours < 99 ? hours : Math.Round(hours / 24)), 0,
			detail + (hours >= 99 ? " (на значке — сутки)" : ""));
	}

	/// <summary>
	/// One icon for the worst thing happening anywhere. Seventeen icons do not fit into a
	/// Windows 11 tray, and this is the answer that stays inside "one icon, one number": the
	/// highest severity among everything else, labelled with whichever metric it came from.
	/// </summary>
	private void ShowWorst()
	{
		var slot = Slot("worst", WorstMetric, WorstGuid);
		Alive(slot);   // derived from the others, so it exists whenever anything else does

		IconSlot worst = null;
		double top = double.MinValue;
		foreach (var candidate in _order)
		{
			// Severity comes from Record, not from Show, so an icon the user has hidden still
			// counts here — the whole point is to watch everything with one slot in the tray.
			if (ReferenceEquals(candidate, slot) || candidate.Dead) continue;
			if (candidate.SeenTick != _tick || !candidate.Severity.HasValue) continue;

			var score = Score(candidate);
			if (score is null || score <= top) continue;
			top = score.Value;
			worst = candidate;
		}

		if (worst is null) { Show(slot, "ок", 0, "тревог нет"); return; }
		Show(slot, Whole(top), top,
			$"{LabelOf(worst)} — {Whole(top)} % от своего красного порога" +
			$"{(top >= 100 ? "   ПОРОГ ПРЕВЫШЕН" : "")}");
	}

	/// <summary>
	/// One metric's state on a common scale: 0 is idle, 78 is its own yellow threshold, 100 is
	/// its own red one. Piecewise, not a plain ratio to the critical threshold — that put the
	/// yellows of the built-in metrics anywhere between 67 (the UPS) and 93 (RAM), so the summary
	/// icon and the icon it was summarising disagreed about their colour, and a user threshold
	/// broke the correspondence further. With this the summary plate is always exactly the worst
	/// plate in the tray, whatever anyone sets.
	/// </summary>
	private double? Score(IconSlot slot)
	{
		var crit = CritOf(slot);
		if (crit >= NeverAlerts || !AlertsOn(slot)) return null;   // volumes and uptime never alarm
		return Scale.Score(slot.Severity, WarnOf(slot), crit);
	}

	// ---- slots ----

	/// <summary>
	/// Registers the slot on first sight. Registering does *not* make it alive — that is
	/// <see cref="Record"/>, and only a reading does it, so a source that exists but answers
	/// nothing goes grey with a reason instead of showing a dash for ever.
	/// </summary>
	private IconSlot Slot(string id, Metric metric, Guid guid, string label = null)
	{
		if (!_slots.TryGetValue(id, out var slot))
		{
			slot = new IconSlot
			{
				Id = id,
				Metric = metric,
				DefaultLabel = label ?? metric.Label,
				Guid = guid,
				Settings = _config.Get(id),
			};
			Adopt(slot);
		}
		return slot;
	}

	/// <summary>The same, for a family that draws its tray identity from a pool.</summary>
	private IconSlot Pooled(string id, Metric metric, GuidPool pool, string key, string label)
	{
		if (_slots.TryGetValue(id, out var known)) return known;

		var guid = pool.For(key);
		if (guid is null) return null;   // more sources than prepared slots — reported in the menu

		var slot = new IconSlot
		{
			Id = id,
			Metric = metric,
			DefaultLabel = label ?? metric.Label,
			Guid = guid.Value,
			Pool = pool,
			PoolKey = key,
			Settings = _config.Get(id),
		};
		Adopt(slot);
		return slot;
	}

	private void Adopt(IconSlot slot)
	{
		// Seventeen icons on a first run go straight into the Windows 11 overflow, where nothing
		// is visible — and the menu lives on the icons, so there is then no way to find the
		// program at all. Start with the four that answer "is this machine busy", and let the
		// rest be switched on from the menu. Existing installations are untouched: they have a
		// settings file already, so this only takes effect on a machine with no TrayMon.json.
		if (_firstRun && !DefaultOn(slot.Id))
		{
			_config.For(slot.Id).Enabled = false;
			_configDirty = true;
		}
		slot.Settings = _config.Get(slot.Id);
		_slots[slot.Id] = slot;
		_order.Add(slot);
	}

	/// <summary>The four load meters, and only for the first card: every adapter, every volume and
	/// every second card would put the count back where it was. "gpu.0" is the load of card 0 —
	/// its temperature is "gpu.temp.0" and stays off.</summary>
	private static bool DefaultOn(string id) => id is "cpu" or "ram" or "gpu.0" or "vram.0";

	/// <summary>
	/// Feeds the five-minute window behind the tooltip and marks the slot alive.
	///
	/// A missing value is what "this source is not answering" means, so it does *not* count as
	/// alive: the sensor library opens happily without an elevated token and then returns null
	/// for every temperature, which used to leave a permanent dash on the icon and no hint that
	/// the fix was to run as administrator.
	/// </summary>
	/// <param name="severity">What the thresholds apply to, when that is not the value itself —
	/// a fan is judged on being stopped, the UPS on how little charge is left. Recorded here and
	/// not in <see cref="Show"/> so that a hidden icon still feeds the summary one.</param>
	private void Record(IconSlot slot, double? value, double? severity = null)
	{
		if (!value.HasValue) return;
		slot.SeenTick = _tick;
		slot.SeenAtMs = Environment.TickCount64;
		slot.Stats.Add(value.Value);
		slot.Severity = severity ?? value;
	}

	/// <summary>Marks a slot alive when it is present by construction rather than by a reading.</summary>
	private void Alive(IconSlot slot)
	{
		slot.SeenTick = _tick;
		slot.SeenAtMs = Environment.TickCount64;
	}

	/// <summary>
	/// Formats a reading and, if the icon is shown, puts it in the tray.
	///
	/// Formatting happens whether or not the icon is displayed. It used to be skipped for hidden
	/// icons, which cost two things: the summary window — whose whole purpose is the metrics that
	/// are *not* on the taskbar — showed "—" for every one of them, and an icon switched on from
	/// the menu displayed a grey dash until its family was next re-formatted, up to ten minutes
	/// for a RAID disk. Seventeen strings a tick is microseconds; the expensive part is the call
	/// into the shell, and that is still only made for icons that exist.
	/// </summary>
	private void Show(IconSlot slot, string text, double? severity, string detail)
	{
		// A source that produced nothing this tick belongs to Fade(), which owns the grey plate
		// and the explanation in the tooltip. Letting a caller overwrite that with its own empty
		// formatting is how "—" ends up with no reason next to it.
		if (slot.SeenTick != _tick) return;

		slot.LastText = text;
		slot.LastSeverity = severity;
		slot.LastDetail = detail;
		Push(slot, text, severity);
	}

	/// <summary>Sends the slot's current state to its tray icon, creating the icon if needed.</summary>
	private void Push(IconSlot slot, string text, double? severity)
	{
		NoteLevelChange(slot, severity);
		if (_dry || !slot.Settings.Enabled) return;

		var tooltip = $"{LabelOf(slot)}   {slot.LastDetail}";
		if (slot.Icon is null)
		{
			// Everything the icon needs is known here, so it is born finished: one render and one
			// NIM_ADD instead of a grey placeholder followed by a colour, an ink and a value.
			slot.Icon = new TrayValueIcon(OnIconRightClick, OnIconLeftClick, PlateOf(slot), slot.Guid,
				EffWarn(slot), EffCrit(slot), InkOf(slot), text, severity, tooltip);
			return;
		}
		slot.Icon.SetThresholds(EffWarn(slot), EffCrit(slot));
		slot.Icon.SetInk(InkOf(slot));
		slot.Icon.Update(text, severity, tooltip);
	}

	/// <summary>
	/// Records a change of alert colour, and interrupts for the ones that mean hardware is
	/// failing. Nothing in the program could answer "why was it red at three in the morning" —
	/// the five-minute window in the tooltip is five minutes, and the CSV trail is off by default.
	/// A transition is a rare event, so this costs nothing on a tick.
	/// </summary>
	private void NoteLevelChange(IconSlot slot, double? severity)
	{
		var level = severity is null ? -1
			: !AlertsOn(slot) ? 0
			: severity.Value >= CritOf(slot) ? 2
			: severity.Value >= WarnOf(slot) ? 1
			: 0;
		if (level == slot.Level) return;
		var was = slot.Level;
		slot.Level = level;
		if (was == -1 || _dry) return;   // coming back from silence is Fade's story, not an alarm

		var names = new[] { "серый", "норма", "жёлтый", "красный" };
		var line = $"{DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture)}  {LabelOf(slot)}: " +
				   $"{names[was + 1]} → {names[level + 1]}" +
				   (slot.LastText is null ? "" : $" ({slot.LastText})");
		_journal.Enqueue(line);
		while (_journal.Count > 50) _journal.Dequeue();

		if (level != 2 || was == 2) return;
		if (!(slot.Settings.NotifyOnCritical ?? slot.Metric.NotifyByDefault)) return;
		Balloon("TrayMon", $"{LabelOf(slot)}: {slot.LastDetail}");
		// And into the Application log, where a server's existing monitoring will find it: a
		// balloon needs somebody in front of the screen, and these are exactly the events for
		// which there is nobody.
		WindowsLog.Write($"TrayMon: {LabelOf(slot)} — {slot.LastDetail.Replace("\n", " · ")}", critical: true);
	}

	/// <summary>
	/// A balloon on whichever icon is actually registered. A hidden icon has no tray slot to hang
	/// a notification on, and the event is not about the icon anyway.
	/// </summary>
	private void Balloon(string title, string message, bool warning = true)
	{
		if (_dry) return;
		var carrier = _order.FirstOrDefault(s => s.Icon is not null)?.Icon;
		carrier?.Notify(title, message, warning);
	}

	private void RefreshStats()
	{
		foreach (var slot in _order)
		{
			if (!slot.Stats.Ready) { slot.StatsText = ""; continue; }
			var (min, avg, max) = slot.Stats.Window();
			var ci = CultureInfo.InvariantCulture;
			var digits = max < 10 ? "0.0" : "0";
			// Kept short on purpose: a tray tooltip is 127 characters and no more, and a network
			// icon's own line already takes a hundred of them. A verbose form was simply cut off.
			slot.StatsText = $"\n5 мин: {min.ToString(digits, ci)}…{max.ToString(digits, ci)}, ср {avg.ToString(digits, ci)}";
		}
	}

	private void FirstRunHousekeeping()
	{
		if (!_firstRun) return;

		if (!_greeted && _tick >= 2)
		{
			_greeted = true;
			// Information, not a warning triangle: "TrayMon is running" is not a problem.
			Balloon("TrayMon работает",
				"Значки в области уведомлений. Правой кнопкой по любому из них — меню: " +
				"остальные метрики, цвета, автозапуск.",
				warning: false);
		}
		// Written once, not on every tick: a settings file is the one thing here that touches
		// the disk on a schedule.
		if (_configDirty && _tick % 15 == 0) { _config.Save(); _configDirty = false; }
	}

	/// <summary>
	/// Optional CSV trail. Off by default and never faster than every 30 s — at tick rate this
	/// would cost as much as the sensors it records.
	/// </summary>
	private void WriteLogLine()
	{
		if (_dry || !_config.Log.Enabled) return;
		// Monotonic, not DateTime.Now: a clock stepped backwards — which is what a machine does
		// after an NTP correction or a resume — stopped the log for however long the step was.
		var now = Environment.TickCount64;
		if (now - _lastLogAt < _config.Log.EverySeconds * 1000L) return;
		_lastLogAt = now;

		try
		{
			// A relative path here is resolved against the program folder. Left to the working
			// directory it meant %SystemRoot%\System32 under the logon task, written to by a
			// process with an elevated token.
			var configured = _config.Log.Path;
			var path = string.IsNullOrWhiteSpace(configured)
				? Path.Combine(AppContext.BaseDirectory, "TrayMon.csv")
				: Path.IsPathFullyQualified(configured)
					? configured
					: Path.Combine(AppContext.BaseDirectory, configured);

			var ci = CultureInfo.InvariantCulture;
			var stamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", ci);
			// One row per slot rather than nine fixed columns. The fixed set left out disk
			// temperatures, fans, volumes and top-io — half of what "why was the server slow last
			// night" actually needs — and a long format needs no header change when a machine has
			// a different set of icons from the one the columns were written for.
			var lines = new StringBuilder();
			foreach (var slot in _order)
			{
				if (!slot.Severity.HasValue && slot.LastText is null) continue;
				lines.Append(stamp).Append(';').Append(slot.Id).Append(';')
					 .Append(slot.LastText ?? "").Append(';')
					 .Append(slot.Severity.HasValue ? slot.Severity.Value.ToString("0.0", ci) : "")
					 .Append('\n');
			}
			if (lines.Length == 0) return;
			if (!File.Exists(path)) File.AppendAllText(path, "time;id;value;severity\n");
			File.AppendAllText(path, lines.ToString());
		}
		catch (Exception ex)
		{
			// A full or read-only disk must not take the monitor down with it.
			_lastTickError = "журнал: " + ex.Message;
		}
	}

	// ---- background refreshes ----

	[DllImport("kernel32.dll")]
	private static extern IntPtr GetCurrentThread();

	[DllImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool SetThreadPriority(IntPtr thread, int priority);

	private const int ThreadModeBackgroundBegin = 0x00010000;
	private const int ThreadModeBackgroundEnd = 0x00020000;

	private void Spawn(Action work)
	{
		if (_dry) return;   // the dry run reads everything on its own thread, in order
		var task = Task.Run(() =>
		{
			// Background mode drops both CPU priority and I/O priority for the duration. These
			// threads run smartctl against a spinning disk and wait on a socket; none of it
			// should ever be ahead of whatever the machine is really doing. The mode is per
			// thread and this is a pool thread, so it is always ended again.
			var thread = GetCurrentThread();
			var lowered = SetThreadPriority(thread, ThreadModeBackgroundBegin);
			var started = Stopwatch.GetTimestamp();
			try { work(); }
			finally
			{
				Interlocked.Add(ref _poolTicks, Stopwatch.GetTimestamp() - started);
				if (lowered) SetThreadPriority(thread, ThreadModeBackgroundEnd);
			}
		});
		lock (_background)
		{
			_background.RemoveAll(t => t.IsCompleted);
			_background.Add(task);
		}
	}

	private void RefreshSlow()
	{
		if (Interlocked.Exchange(ref _slowRefreshRunning, 1) == 1) return;
		try { _r.Slow = _lhm.ReadSlow(); }
		catch (Exception ex) { Trouble(ex); }
		finally { Volatile.Write(ref _slowRefreshRunning, 0); }
	}

	private void RefreshDisks()
	{
		if (Interlocked.Exchange(ref _diskRefreshRunning, 1) == 1) return;
		try
		{
			_r.Disks = Program.DiskTemps(_disk, _lhm);
			Interlocked.Increment(ref _disksVersion);
		}
		catch (Exception ex) { Trouble(ex); }
		finally { Volatile.Write(ref _diskRefreshRunning, 0); }
	}

	private void RefreshTopIo()
	{
		if (Interlocked.Exchange(ref _topIoRefreshRunning, 1) == 1) return;
		try
		{
			// A gap longer than two periods makes the sample a baseline instead of a reading.
			_r.TopIo = _perf.TopIoProcesses(3, TopIoEveryTicks * _intervalMs * 2);
		}
		catch (Exception ex) { Trouble(ex); }
		finally { Volatile.Write(ref _topIoRefreshRunning, 0); }
	}

	private void RefreshSpace(string[] volumes)
	{
		if (Interlocked.Exchange(ref _spaceRefreshRunning, 1) == 1) return;
		try { _r.Space = SpaceSensor.Read(volumes); }
		catch (Exception ex) { Trouble(ex); }
		finally { Volatile.Write(ref _spaceRefreshRunning, 0); }
	}

	private void RefreshRaidDisk()
	{
		if (Interlocked.Exchange(ref _raidRefreshRunning, 1) == 1) return;
		try
		{
			_r.RaidDisks = _hdd.ReadAll();
			Interlocked.Increment(ref _raidVersion);
			Backoff(ref _raidFailures, ref _raidRetryAt, _r.RaidDisks.Count > 0);
		}
		catch (Exception ex) { Trouble(ex); }
		finally { Volatile.Write(ref _raidRefreshRunning, 0); }
	}

	private void RefreshUps()
	{
		if (Interlocked.Exchange(ref _upsRefreshRunning, 1) == 1) return;
		try
		{
			_ups.Read(_r);
			Interlocked.Increment(ref _upsVersion);
			Backoff(ref _upsFailures, ref _upsRetryAt, _r.Ups.Answered);
		}
		catch (Exception ex) { Trouble(ex); }
		finally { Volatile.Write(ref _upsRefreshRunning, 0); }
	}

	/// <summary>
	/// Slows down a source that is simply not there. On a machine with no UPS the default address
	/// is the loopback, so the SNMP query sends a datagram and waits a second and a half every
	/// thirty seconds for as long as the machine is up; smartctl.exe that nobody copied is looked
	/// for every ten minutes. Neither is ever going to answer. "Опросить датчики сейчас" clears it.
	/// </summary>
	private static void Backoff(ref int failures, ref long retryAt, bool answered)
	{
		if (answered) { failures = 0; retryAt = 0; return; }
		if (++failures < GiveUpAfter) return;
		retryAt = Environment.TickCount64 + RetryDeadSourceMs;
	}

	/// <summary>
	/// Remembers a failure instead of putting a dialog in front of an empty chair. On a server
	/// with the RDP session disconnected nobody sees a modal box, and the timer keeps firing
	/// underneath it — the old default opened a new one every two seconds until USER handles
	/// ran out. The count and the last message live in "Диагностика…".
	/// </summary>
	public void Trouble(Exception ex, string where = null)
	{
		// Interlocked: this is called from the pool threads as well as from the tick, and a
		// plain increment loses counts exactly when there are the most of them to lose.
		Interlocked.Increment(ref _tickErrors);
		var place = where is null ? "" : $" [{where}]";
		_lastTickError = $"{DateTime.Now:HH:mm:ss}{place} {ex.GetType().Name}: {ex.Message}";
	}

	/// <summary>Called when the process is going down anyway — take the icons with it.</summary>
	public void Panic(Exception ex)
	{
		Trouble(ex);
		foreach (var slot in _order) { try { slot.Icon?.Dispose(); } catch (Exception) { } }
	}

	// ---- settings helpers ----

	private double WarnOf(IconSlot slot) => slot.Settings.Warn ?? slot.Metric.Warn;
	private double CritOf(IconSlot slot) => slot.Settings.Crit ?? slot.Metric.Crit;

	/// <summary>
	/// Whether this icon is coloured by its thresholds at all. Switching the highlight off is a
	/// flag now, not a pair of thresholds set to a billion: the old way overwrote whatever the
	/// user had configured (so ticking the box back on gave them the built-in values, not theirs)
	/// and wrote <c>"Warn": 1000000000</c> into a file the README invites people to read.
	/// </summary>
	private bool AlertsOn(IconSlot slot) =>
		(slot.Settings.Alerts ?? true) && WarnOf(slot) < NeverAlerts;

	// What the icon is told. Identical to the pair above while the highlight is on, and out of
	// reach of any reading while it is off.
	private double EffWarn(IconSlot slot) => AlertsOn(slot) ? WarnOf(slot) : NeverAlerts;
	private double EffCrit(IconSlot slot) => AlertsOn(slot) ? CritOf(slot) : NeverAlerts;

	private Color PlateOf(IconSlot slot)
	{
		var hex = slot.Settings.Color;
		if (!string.IsNullOrWhiteSpace(hex))
		{
			try { return ColorTranslator.FromHtml(hex); }
			catch (Exception) { /* hand-edited nonsense in the file — fall back to the default */ }
		}
		return slot.Metric.Plate;
	}

	private string LabelOf(IconSlot slot) => slot.Settings.Label ?? slot.DefaultLabel;

	/// <summary>Digit colour chosen by the user; null lets the icon pick it by the plate colour.</summary>
	private Color? InkOf(IconSlot slot) => slot.Settings.Ink switch
	{
		"light" => Color.White,
		"dark" => Color.Black,
		_ => null,
	};

	/// <summary>Takes a private settings entry for this slot so it can be written to.</summary>
	private IconSettings Own(IconSlot slot) => slot.Settings = _config.For(slot.Id);

	private void Persist(IconSlot slot)
	{
		_config.Tidy(slot.Id);
		slot.Settings = _config.Get(slot.Id);
		if (_config.Save(out var error)) return;
		Info($"Настройки не сохранены: {error}\n\nИзменение действует до перезапуска программы.",
			MessageBoxIcon.Warning);
	}

	// ---- tray menu ----

	private void OnIconLeftClick(TrayValueIcon icon)
	{
		// A click on the summary icon asks "what exactly is red", so that window is ordered by
		// how bad things are rather than by menu group.
		var clicked = _order.FirstOrDefault(s => ReferenceEquals(s.Icon, icon));
		ShowSummary(byScore: clicked?.Id == "worst");
	}

	private void OnIconRightClick(TrayValueIcon icon)
	{
		var clicked = _order.FirstOrDefault(s => ReferenceEquals(s.Icon, icon));

		// A right-click on another icon does not close the menu that is already up, and the items
		// about to be thrown away are the ones it is showing.
		if (_menu.Visible) _menu.Close();
		ClearMenu();
		if (clicked is not null)
		{
			_menu.Items.Add(new ToolStripMenuItem(LabelOf(clicked)) { Enabled = false });
			_menu.Items.Add(new ToolStripSeparator());
			_menu.Items.Add("Цвет фона…", null, (_, _) => PickColor(clicked));
			_menu.Items.Add(InkMenu(clicked));
			_menu.Items.Add("Переименовать…", null, (_, _) => Rename(clicked));
			// Not offered where there is nothing to set: a fan is judged on standing still, and
			// a metric that never alarms has no thresholds to type into. The dialog used to
			// accept "600 rpm" into a scale that only ever holds 0 and 100, which quietly turned
			// the standstill alarm off while its checkbox still said it was on.
			if (!clicked.Metric.StallAlarm && clicked.Metric.Warn < NeverAlerts)
				_menu.Items.Add("Пороги…", null, (_, _) => EditThresholds(clicked));
			if (clicked.Metric.Warn < NeverAlerts)
			{
				var alerts = new ToolStripMenuItem(clicked.Metric.StallAlarm
					? "Тревога при остановке"
					: "Подсвечивать при перегрузке")
				{
					Checked = AlertsOn(clicked),
					CheckOnClick = true,
					ToolTipText = ThresholdHint(clicked),
				};
				alerts.Click += (_, _) => SetAlerts(clicked, alerts.Checked);
				_menu.Items.Add(alerts);

				var notify = new ToolStripMenuItem("Уведомлять при переходе в красный")
				{
					Checked = clicked.Settings.NotifyOnCritical ?? clicked.Metric.NotifyByDefault,
					CheckOnClick = true,
					ToolTipText = "Всплывающее уведомление один раз на переход, а не на каждый тик",
				};
				notify.Click += (_, _) => SetNotify(clicked, notify.Checked);
				_menu.Items.Add(notify);
			}
			_menu.Items.Add("Скрыть этот значок", null, (_, _) => SetEnabled(clicked, false));
			_menu.Items.Add(new ToolStripSeparator());
		}

		_menu.Items.Add(VisibilityMenu());
		_menu.Items.Add("Опросить датчики сейчас", null, (_, _) => PollNow());
		_menu.Items.Add(new ToolStripSeparator());

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
		_menu.Items.Add("Перечитать настройки", null, (_, _) => ReloadConfig());
		_menu.Items.Add(new ToolStripSeparator());
		_menu.Items.Add("Сводка…", null, (_, _) => ShowSummary());
		_menu.Items.Add("Диагностика…", null, (_, _) => ShowDiagnostics());
		_menu.Items.Add("О программе…", null, (_, _) => ShowAbout());
		_menu.Items.Add("Удалить TrayMon…", null, (_, _) => Uninstall());
		_menu.Items.Add(new ToolStripSeparator());
		_menu.Items.Add("Выход", null, (_, _) => ExitThread());

		icon.PrepareMenu();
		_menu.Show(Cursor.Position);
	}

	/// <summary>Menu items are Components; Clear alone leaves each of them to the finaliser,
	/// and an opened submenu has a window handle no finaliser destroys.</summary>
	private void ClearMenu()
	{
		_pickingIcons = false;   // the item that would have said so is about to be disposed
		var items = _menu.Items.Cast<ToolStripItem>().ToArray();
		_menu.Items.Clear();
		foreach (var item in items) item.Dispose();
	}

	/// <summary>
	/// The list of every icon, grouped and in a fixed order. It used to follow the order slots
	/// happened to be created in — which depends on when asynchronous sensors answer, so it
	/// changed between runs and yesterday's tick was somewhere else today.
	/// </summary>
	private ToolStripMenuItem VisibilityMenu()
	{
		var all = new ToolStripMenuItem("Показывать значки");
		all.DropDownOpened += (_, _) => _pickingIcons = true;
		all.DropDownClosed += (_, _) => _pickingIcons = false;
		// The chain closes from the top down, so the parent has to be held open too — that half
		// is the handler on _menu in the constructor.
		all.DropDown.Closing += (_, e) =>
		{
			if (e.CloseReason == ToolStripDropDownCloseReason.ItemClicked) e.Cancel = true;
		};
		foreach (var group in Groups)
		{
			var members = _order
				.Where(s => s.Metric.Group == group)
				.OrderBy(s => s.Metric.Order)
				.ThenBy(s => LabelOf(s), StringComparer.CurrentCultureIgnoreCase)
				.ToList();
			if (members.Count == 0) continue;

			if (all.DropDownItems.Count > 0) all.DropDownItems.Add(new ToolStripSeparator());
			all.DropDownItems.Add(new ToolStripMenuItem(group) { Enabled = false });
			foreach (var slot in members)
			{
				var item = new ToolStripMenuItem(LabelOf(slot) + (slot.Dead ? "  (нет данных)" : ""))
				{
					Checked = slot.Settings.Enabled,
					CheckOnClick = true,
				};
				var captured = slot;
				// The tick is put there by CheckOnClick before anything is asked of us, so a
				// refusal has to take it back — the list stays open now, and a tick next to an
				// icon that is not there is a lie the user goes on looking at.
				item.Click += (_, _) => { if (!SetEnabled(captured, item.Checked)) item.Checked = !item.Checked; };
				all.DropDownItems.Add(item);
			}
		}
		var overflows = Overflows();
		if (overflows.Count > 0)
		{
			all.DropDownItems.Add(new ToolStripSeparator());
			foreach (var line in overflows)
				all.DropDownItems.Add(new ToolStripMenuItem(line) { Enabled = false });
		}
		return all;
	}

	/// <summary>
	/// Light or dark digits for this icon. "Авто" picks by the brightness of the plate, so a
	/// light background chosen from the colour dialog gets black digits instead of white on white.
	/// </summary>
	private ToolStripMenuItem InkMenu(IconSlot slot)
	{
		var menu = new ToolStripMenuItem("Цвет цифр");
		var current = slot.Settings.Ink;
		foreach (var (label, value) in new[] { ("Авто", (string)null), ("Светлые", "light"), ("Тёмные", "dark") })
		{
			var choice = value;
			var item = new ToolStripMenuItem(label) { Checked = current == choice };
			item.Click += (_, _) => SetInk(slot, choice);
			menu.DropDownItems.Add(item);
		}
		return menu;
	}

	/// <summary>What the thresholds mean for this icon, in the units it is actually read in.</summary>
	private string ThresholdHint(IconSlot slot)
	{
		if (!AlertsOn(slot)) return "Сейчас выключено: плашка всегда своего цвета";
		var ci = CultureInfo.InvariantCulture;
		var warn = WarnOf(slot);
		var crit = CritOf(slot);
		if (slot.Metric.StallAlarm)
			return "Красная, когда вентилятор остановлен";
		if (slot.Metric.Inverted)
			return $"Жёлтая при заряде ниже {(100 - warn).ToString("0", ci)} %, " +
				   $"красная ниже {(100 - crit).ToString("0", ci)} % и сразу при переходе на батарею";
		if (slot.Id.StartsWith("free.", StringComparison.Ordinal))
			return $"Жёлтая когда занято больше {warn.ToString("0", ci)} %, красная больше {crit.ToString("0", ci)} %";
		if (slot.Id == "worst")
			return "Повторяет самый тревожный из остальных значков";
		return $"Жёлтая при {warn.ToString("0", ci)} {slot.Metric.Unit}, красная при {crit.ToString("0", ci)} {slot.Metric.Unit}";
	}

	private void SetNotify(IconSlot slot, bool on)
	{
		Own(slot).NotifyOnCritical = on == slot.Metric.NotifyByDefault ? null : on;
		Persist(slot);
	}

	private void SetInk(IconSlot slot, string ink)
	{
		Own(slot).Ink = ink;
		Persist(slot);
		slot.Icon?.SetInk(InkOf(slot));
	}

	private void PickColor(IconSlot slot)
	{
		using var dialog = new ColorDialog { Color = PlateOf(slot), FullOpen = true, AnyColor = true };
		if (dialog.ShowDialog() != DialogResult.OK) return;
		Own(slot).Color = $"#{dialog.Color.R:X2}{dialog.Color.G:X2}{dialog.Color.B:X2}";
		Persist(slot);
		slot.Icon?.SetPlate(dialog.Color);

		// A plate held at its warning colour ignores the chosen one, which looks like the
		// setting did nothing.
		if (slot.Icon?.IsAlerting == true)
			Info($"Цвет сохранён, но сейчас значок подсвечен порогом " +
				 $"({slot.LastSeverity?.ToString("0", CultureInfo.InvariantCulture)} ≥ " +
				 $"{WarnOf(slot).ToString("0", CultureInfo.InvariantCulture)}).\n" +
				 "Выбранный цвет появится, когда значение опустится ниже порога, либо снимите\n" +
				 "галочку «Подсвечивать при перегрузке».");
	}

	private void SetAlerts(IconSlot slot, bool on)
	{
		// Null rather than true, so an icon nobody has changed keeps no entry in the file at all.
		Own(slot).Alerts = on ? null : false;
		Persist(slot);
		slot.Icon?.SetThresholds(EffWarn(slot), EffCrit(slot));
	}

	/// <summary>
	/// Asks for the thresholds in the unit the user reads on the icon, and stores them in the
	/// unit severity is measured in. For the UPS those are not the same: the icon shows charge,
	/// severity grows as the charge falls, and the dialog used to print "yellow threshold
	/// (% charge): 50" and then store 20 as a severity when "red 20" was typed — a red plate at
	/// 80 % charge, with the same inverted numbers landing in TrayMon.json.
	/// </summary>
	private void EditThresholds(IconSlot slot)
	{
		var ci = CultureInfo.InvariantCulture;
		var inverted = slot.Metric.Inverted;
		double Display(double severity) => inverted ? 100 - severity : severity;
		double Store(double shown) => inverted ? 100 - shown : shown;

		var warn = Prompt($"Жёлтый порог ({slot.Metric.Unit})\n{ThresholdHint(slot)}",
			Display(WarnOf(slot)).ToString("0.###", ci));
		if (warn is null) return;
		var crit = Prompt($"Красный порог ({slot.Metric.Unit})", Display(CritOf(slot)).ToString("0.###", ci));
		if (crit is null) return;

		if (!double.TryParse(warn, NumberStyles.Float, ci, out var w) ||
			!double.TryParse(crit, NumberStyles.Float, ci, out var c))
		{
			Info("Порог должен быть числом; точка как разделитель дробной части.", MessageBoxIcon.Warning);
			return;
		}

		var settings = Own(slot);
		settings.Warn = Store(w);
		settings.Crit = Store(c);
		if (settings.Warn >= settings.Crit)
		{
			Info(inverted
				? "Красный порог должен быть ниже жёлтого: тревога здесь — это низкий заряд."
				: "Красный порог должен быть выше жёлтого.", MessageBoxIcon.Warning);
			settings.Warn = null;
			settings.Crit = null;
			return;
		}
		Persist(slot);
		slot.Icon?.SetThresholds(EffWarn(slot), EffCrit(slot));
	}

	private void Rename(IconSlot slot)
	{
		var name = Prompt("Подпись значка", LabelOf(slot));
		if (name is null) return;
		Own(slot).Label = string.IsNullOrWhiteSpace(name) ? null : name;   // empty restores the default
		Persist(slot);
		slot.Icon?.Update(slot.LastText, slot.LastSeverity, $"{LabelOf(slot)}   {slot.LastDetail}");
	}

	/// <summary>
	/// Whether this slot is something the menu can be opened from. "Enabled" is not enough —
	/// an enabled slot that never produced a reading had no icon at all — and "not dead" is
	/// wrong in the other direction, since a dead slot does keep a grey, clickable icon. What
	/// carries the menu is an icon that exists, so that is what both checks ask about.
	/// </summary>
	private static bool CarriesMenu(IconSlot slot) => slot.Settings.Enabled && slot.Icon is not null;

	/// <returns>False when the change was refused, so the caller can put its tick back.</returns>
	private bool SetEnabled(IconSlot slot, bool enabled)
	{
		// The menu lives on the icons; hiding the last one would leave no way back in — not
		// even to quit.
		if (!enabled && _order.Count(CarriesMenu) <= 1)
		{
			Info("Последний значок скрыть нельзя — иначе не останется меню.");
			return false;
		}

		Own(slot).Enabled = enabled;
		Persist(slot);
		if (!enabled)
		{
			if (slot.Icon is not null) { slot.Icon.Dispose(); slot.Icon = null; }
			return true;
		}
		// The user's thresholds and ink, not the built-in ones: creating the icon with
		// slot.Metric.Warn made a freshly unhidden icon flash yellow for a tick even with the
		// highlight switched off.
		Push(slot, slot.LastText, slot.LastSeverity);
		return true;
	}

	/// <summary>
	/// Forces the slow sources round now. Having installed smartctl.exe or started an SNMP
	/// agent, the alternative was waiting up to ten minutes with nothing to say it was working.
	/// </summary>
	private void PollNow()
	{
		// Clears the backoff too: this menu item exists for the moment right after installing
		// smartctl.exe or starting an SNMP agent, which is exactly when a source that has been
		// giving no answer for an hour suddenly has one.
		_upsFailures = _raidFailures = 0;
		_upsRetryAt = _raidRetryAt = 0;
		Spawn(RefreshSlow);
		Spawn(RefreshDisks);
		Spawn(RefreshRaidDisk);
		Spawn(RefreshUps);
		Spawn(RefreshTopIo);
		var names = _r.Volumes.Select(v => v.Name).ToArray();
		Spawn(() => RefreshSpace(names));
		Info("Опрос запущен. Медленные источники (SMART, RAID, ИБП) ответят в течение нескольких секунд.");
	}

	private static void ToggleAutostart(bool on)
	{
		var ok = on ? Autostart.Enable(out var error) : Autostart.Disable(out error);
		if (ok)
		{
			// A security check that silently did not run must not look like one that passed.
			var unverified = on && Autostart.LastCheckError is not null
				? "\n\nПрава на папку проверить не удалось (" + Autostart.LastCheckError + ").\n" +
				  "Убедитесь сами, что писать в неё может только администратор."
				: "";
			Info((on ? "TrayMon будет запускаться при входе в Windows." : "Автозапуск отключён.") + unverified);
			return;
		}
		Info($"Не получилось: {error}", MessageBoxIcon.Warning);
	}

	private static void CreateShortcut()
	{
		var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
		if (Autostart.CreateShortcut(desktop, out var path, out var error))
			Info($"Ярлык создан:\n{path}");
		else
			Info($"Не получилось: {error}", MessageBoxIcon.Warning);
	}

	private static void OpenConfigFile()
	{
		try
		{
			// The full path, not a bare name: CreateProcess searches the application folder
			// first, and this process holds an elevated token.
			var notepad = Path.Combine(Environment.SystemDirectory, "notepad.exe");
			Process.Start(new ProcessStartInfo(notepad, $"\"{Config.Path}\"") { UseShellExecute = true });
			Info("Файл читается только при старте, а любое изменение из меню перезаписывает его целиком.\n\n" +
				 "Сохранив правку, выберите «Перечитать настройки» — иначе она пропадёт.");
		}
		catch (Exception ex)
		{
			Info($"Не удалось открыть файл настроек: {ex.Message}", MessageBoxIcon.Warning);
		}
	}

	/// <summary>
	/// Re-reads the file and applies it to the live icons. Without this a hand edit — the only
	/// way to set the UPS address — did nothing until a restart, and was then wiped by the first
	/// change made from the menu.
	/// </summary>
	private void ReloadConfig(bool silent = false)
	{
		// Loaded into a local first. Assigning straight into _config handed the program the empty
		// object Load() returns on a bad file — and the next change from the menu then saved that
		// emptiness over the real file, taking every colour, label, threshold, the UPS address
		// and the adapter filter with it.
		var fresh = Config.Load();
		if (fresh.LoadError is not null)
		{
			if (!silent) Info($"Файл не прочитан: {fresh.LoadError}", MessageBoxIcon.Warning);
			return;
		}
		_config = fresh;

		foreach (var slot in _order)
		{
			slot.Settings = _config.Get(slot.Id);
			if (!slot.Settings.Enabled)
			{
				if (slot.Icon is not null) { slot.Icon.Dispose(); slot.Icon = null; }
				continue;
			}
			if (slot.Icon is null) { Push(slot, slot.LastText, slot.LastSeverity); continue; }
			slot.Icon.SetPlate(PlateOf(slot));
			slot.Icon.SetInk(InkOf(slot));
			slot.Icon.SetThresholds(EffWarn(slot), EffCrit(slot));
			slot.Icon.Update(slot.LastText, slot.LastSeverity, $"{LabelOf(slot)}   {slot.LastDetail}");
		}
		EnsureSomethingVisible();
		if (silent) return;
		Info("Настройки перечитаны.\n\nАдрес ИБП, путь к smartctl, фильтр адаптеров и период опроса " +
			 "применятся после перезапуска программы — они читаются один раз при старте.");
	}

	/// <summary>
	/// The "you cannot hide them all" rule, applied to the file as well as to the menu. Setting
	/// every icon to false by hand is a legitimate edit of a file the README invites people to
	/// edit, and it left a process with no icons, no menu and no way out but Task Manager.
	/// </summary>
	private void EnsureSomethingVisible()
	{
		// The same predicate the menu uses. Asking only about Enabled let a file that switched
		// everything off except one source that cannot answer — cpu.temp without an elevated
		// token — pass the check and leave a process with no icons at all.
		if (_order.Count == 0 || _order.Any(CarriesMenu)) return;
		var first = _order.FirstOrDefault(s => s.Icon is not null) ?? _order[0];
		Own(first).Enabled = true;
		_config.Save();
		first.Settings = _config.Get(first.Id);
		Push(first, first.LastText, first.LastSeverity);
		Later("В настройках не осталось ни одного значка, на котором живёт меню.\n" +
			  $"Включён «{LabelOf(first)}».");
	}

	// ---- windows ----

	private void ShowSummary(bool byScore = false)
	{
		// Version 4 of the tray protocol sends NIN_SELECT for every click, and the second one
		// arrives inside the modal loop of the first window — two identical windows for one
		// double click, one on top of the other.
		if (_summaryOpen) return;
		_summaryOpen = true;
		try
		{
			var ci = CultureInfo.InvariantCulture;
			var text = new StringBuilder();
			if (byScore)
			{
				text.AppendLine("По остроте состояния");
				foreach (var slot in _order.OrderByDescending(s => Score(s) ?? -1))
					text.AppendLine($"    {Rank(slot),4}  {LabelOf(slot),-32} {(slot.LastText ?? "—"),6}   " +
									$"{slot.LastDetail.Replace("\n", " · ")}");
			}
			else
			{
				foreach (var group in Groups)
				{
					var members = _order.Where(s => s.Metric.Group == group).OrderBy(s => s.Metric.Order).ToList();
					if (members.Count == 0) continue;
					text.AppendLine(group);
					foreach (var slot in members)
						text.AppendLine($"    {LabelOf(slot),-32} {(slot.LastText ?? "—"),6}   {slot.LastDetail.Replace("\n", " · ")}");
					text.AppendLine();
				}
			}
			text.AppendLine();
			text.AppendLine($"тик {_tick.ToString(ci)}, ошибок за сеанс: {_tickErrors.ToString(ci)}");
			TextWindow("TrayMon — сводка", text.ToString());
		}
		finally { _summaryOpen = false; }
	}

	private string Rank(IconSlot slot)
	{
		var score = Score(slot);
		return score is null ? "  —" : score.Value.ToString("0", CultureInfo.InvariantCulture);
	}

	/// <summary>
	/// Everything that explains why an icon is empty, in the place the user actually is. All of
	/// it was already in memory and visible only from a console mode that a
	/// requireAdministrator manifest makes awkward to reach.
	/// </summary>
	private void ShowDiagnostics()
	{
		var ci = CultureInfo.InvariantCulture;
		var text = new StringBuilder();
		text.AppendLine($"TrayMon {Version}");
		text.AppendLine($"exe:              {Environment.ProcessPath}");
		text.AppendLine($"права:            {(IsElevated ? "администратор" : "обычный пользователь")}");
		text.AppendLine();
		text.AppendLine($"счётчик CPU:      {_perf.CounterInUse}");
		text.AppendLine($"драйвер датчиков: {(!_lhm.Available ? "НЕДОСТУПЕН — " + (_lhm.LastError ?? "нужен запуск от администратора") : _lhm.DriverBlocked ? "ЗАБЛОКИРОВАН — " + _lhm.LastError : IsElevated ? "загружен" : "загружен, но без прав администратора температуры не читаются")}");
		if (_lhm.Ring0Note is not null) text.AppendLine($"                  {_lhm.Ring0Note}");
		text.AppendLine($"температуры дисков: {(_disk.LastError is null ? "через драйвер накопителей" : "нет — " + _disk.LastError)}");
		text.AppendLine($"NVML (GPU):       {(_gpu.CardCount > 0 ? $"карт: {_gpu.CardCount}" : "нет — " + (_gpu.LastError ?? "драйвер NVIDIA не найден"))}");
		text.AppendLine($"smartctl:         {(_hdd.Available ? _hdd.ExePath : "не найден: " + _hdd.ExePath)}");
		if (_hdd.LastError is not null) text.AppendLine($"                  {_hdd.LastError}");
		text.AppendLine($"ИБП:              {(_ups.Present ? "отвечает" : "нет ответа")} на {_ups.Endpoint}" +
						$"{(_ups.LastError is null ? "" : " — " + _ups.LastError)}");
		text.AppendLine();
		text.AppendLine($"файл настроек:    {Config.Path}");
		if (_config.LoadError is not null) text.AppendLine($"                  {_config.LoadError}");
		if (_config.ChangedOnDisk) text.AppendLine("                  файл изменён на диске — «Перечитать настройки»");
		text.AppendLine($"автозапуск:       {(Autostart.IsEnabled ? "включён" : "выключен")}");
		if (Autostart.PointsElsewhere(out var command))
			text.AppendLine($"                  задача запускает другой файл: {command}");
		var folder = Path.GetDirectoryName(Environment.ProcessPath);
		if (Autostart.WritableByNonAdmins(folder, out var who))
			text.AppendLine($"                  ВНИМАНИЕ: папку может изменить {who} — автозапуск отсюда небезопасен");
		if (Autostart.LastCheckError is not null)
			text.AppendLine($"                  права на папку прочитать не удалось: {Autostart.LastCheckError}");
		// Reported, not enforced: an owner holds WRITE_DAC without any Allow entry, and on a
		// normal installation that owner is the administrator who created the folder.
		var owner = Autostart.OwnerOf(folder);
		if (owner is not null) text.AppendLine($"                  владелец папки: {owner}");
		text.AppendLine();
		text.AppendLine($"значков:          показано {_order.Count(s => s.Settings.Enabled).ToString(ci)} из {_order.Count.ToString(ci)}");
		text.AppendLine($"слоты:            сеть {_netPool.Used}/{_netPool.Size}, тома {_volumePool.Used}/{_volumePool.Size}, " +
						$"свободно {_freePool.Used}/{_freePool.Size}, NVMe {_diskPool.Used}/{_diskPool.Size}, " +
						$"RAID {_raidPool.Used}/{_raidPool.Size}, вентиляторы {_fanPool.Used}/{_fanPool.Size}, " +
						$"GPU {_gpuPool.Used}/{_gpuPool.Size}");
		foreach (var line in Overflows()) text.AppendLine($"                  {line}");
		var dead = _order.Where(s => s.Dead).ToList();
		if (dead.Count > 0)
		{
			text.AppendLine();
			text.AppendLine("молчащие источники:");
			foreach (var slot in dead) text.AppendLine($"    {LabelOf(slot),-32} {slot.LastDetail}");
		}
		text.AppendLine();
		var interval = _timer?.Interval ?? _intervalMs;
		text.AppendLine($"тик:              {_tick.ToString(ci)} (по {interval.ToString(ci)} мс" +
						$"{(_sessionIdle ? ", сеанс заблокирован — опрос замедлен" : "")})");
		var perTick = _tick > 0 ? _tickTicks * 1000.0 / Stopwatch.Frequency / _tick : 0;
		var perPool = _tick > 0 ? Volatile.Read(ref _poolTicks) * 1000.0 / Stopwatch.Frequency / _tick : 0;
		text.AppendLine($"время тика:       {perTick.ToString("0.00", ci)} мс в среднем " +
						$"({(perTick / interval * 100).ToString("0.00", ci)} % ядра на потоке интерфейса)");
		text.AppendLine($"фоновые опросы:   {perPool.ToString("0.00", ci)} мс на тик " +
						$"({(perPool / interval * 100).ToString("0.00", ci)} % ядра в Task.Run)");
		// The number the whole project exists for, and until now it could only be got with an
		// external script. Everything above measures our own threads; this measures the process.
		// It still does not include the repaint the shell does in explorer.exe — see CLAUDE.md.
		try
		{
			_self.Refresh();
			var cpuSeconds = _self.TotalProcessorTime.TotalSeconds;
			var upSeconds = Math.Max(1, (DateTime.Now - _self.StartTime).TotalSeconds);
			text.AppendLine($"процессор всего:  {cpuSeconds.ToString("0.0", ci)} с за " +
							$"{(upSeconds / 60).ToString("0", ci)} мин работы " +
							$"({(100 * cpuSeconds / upSeconds).ToString("0.00", ci)} % одного ядра)");
			text.AppendLine($"рабочее множество:{(_self.WorkingSet64 / 1048576.0).ToString("0", ci)} МБ");
		}
		catch (Exception ex) { text.AppendLine($"процессор всего:  не прочитан — {ex.Message}"); }
		text.AppendLine($"отрисовок:        {TrayValueIcon.Renders.ToString(ci)}");
		text.AppendLine($"вызовов в трей:   {TrayValueIcon.ShellCalls.ToString(ci)} " +
						$"({(_tick > 0 ? (TrayValueIcon.ShellCalls / (double)_tick) : 0).ToString("0.00", ci)} на тик)");
		text.AppendLine($"ошибок за сеанс:  {_tickErrors.ToString(ci)}");
		if (_lastTickError is not null) text.AppendLine($"последняя:        {_lastTickError}");
		if (WindowsLog.LastError is not null) text.AppendLine($"                  {WindowsLog.LastError}");
		if (_journal.Count > 0)
		{
			text.AppendLine();
			text.AppendLine("переходы тревог (последние):");
			foreach (var line in _journal) text.AppendLine("    " + line);
		}
		TextWindow("TrayMon — диагностика", text.ToString());
	}

	private static string Version =>
		Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
		?? Assembly.GetExecutingAssembly().GetName().Version?.ToString()
		?? "—";

	private void ShowAbout() =>
		TextWindow("TrayMon — о программе",
			$"TrayMon {Version}\n" +
			"Монитор железа в области уведомлений Windows: одна иконка — одно число.\n\n" +
			"Лицензия MIT. Использует LibreHardwareMonitorLib (MPL 2.0) для температур\n" +
			"и вентиляторов, NVML для видеокарт, smartctl для дисков за RAID-контроллером.\n\n" +
			$".NET {Environment.Version}\n" +
			$"{Environment.OSVersion.VersionString}\n" +
			$"{(IsElevated ? "Запущен с правами администратора" : "Запущен без прав администратора")}");

	/// <summary>
	/// Undoes what the program can create. Being able to remove something cleanly is a condition
	/// for trying an unfamiliar executable at all — and deleting the exe on its own used to leave
	/// a scheduled task launching a file that is no longer there, at every logon.
	/// </summary>
	private void Uninstall()
	{
		var answer = MessageBox.Show(
			"Удалить задачу планировщика, ярлык на рабочем столе и запомненные Windows позиции значков?\n\n" +
			"Да — удалить и настройки TrayMon.json тоже.\n" +
			"Нет — оставить TrayMon.json.\n" +
			"Отмена — ничего не делать.\n\n" +
			"Сам файл TrayMon.exe программа не удаляет — уберите его вручную после выхода.",
			"TrayMon — удаление", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
		if (answer == DialogResult.Cancel) return;

		// Everything that could write the program back into existence is stopped first. The
		// report below is modal, the timer went on firing underneath it, and the first-run
		// housekeeping wrote TrayMon.json again fifteen ticks later — so "tried it, did not like
		// it, removed it" in the first minute left the settings file behind.
		_timer?.Stop();
		_configDirty = false;
		_configWatcher?.Dispose();
		_configWatcher = null;

		// Icons out of the tray before the registry is cleaned: the entries are keyed by
		// "path to exe + GUID", and an icon still registered has its entry written straight back.
		foreach (var slot in _order) { slot.Icon?.Dispose(); slot.Icon = null; }

		var report = Autostart.Uninstall(answer == DialogResult.Yes);
		report.Add("");
		report.Add("Осталось удалить сам файл TrayMon.exe и папку программы.");
		TextWindow("TrayMon — удаление", string.Join('\n', report));
		ExitThread();
	}

	// ---- small dialogs ----

	/// <summary>
	/// A message box that cannot end up behind other windows. The program has no visible window
	/// of its own, so an unowned box goes to the back and looks like a hang.
	/// </summary>
	private static void Info(string text, MessageBoxIcon icon = MessageBoxIcon.Information)
	{
		using var owner = Anchor();
		MessageBox.Show(owner, text, "TrayMon", MessageBoxButtons.OK, icon);
	}

	private static Form Anchor()
	{
		var form = new Form
		{
			StartPosition = FormStartPosition.CenterScreen,
			FormBorderStyle = FormBorderStyle.None,
			ShowInTaskbar = false,
			Size = new Size(1, 1),
			Opacity = 0,
			TopMost = true,
		};
		form.Show();
		return form;
	}

	/// <summary>A read-only window for text worth copying — diagnostics, the summary, a report.</summary>
	private static void TextWindow(string title, string text)
	{
		// Declared before the form so it outlives every control that points at it, and is
		// released on the way out whatever happens inside.
		using var mono = new Font("Consolas", 9f);
		using var form = new Form
		{
			Text = title,
			StartPosition = FormStartPosition.CenterScreen,
			// Everything is laid out by the layout engine and scaled by font, because this
			// program declares PerMonitorV2 and therefore owns the consequences at 150 %.
			AutoScaleMode = AutoScaleMode.Font,
			ClientSize = new Size(680, 460),
			MinimizeBox = false,
			ShowInTaskbar = false,
			TopMost = true,
		};
		var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Padding = new Padding(8) };
		layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
		layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
		var box = new TextBox
		{
			Dock = DockStyle.Fill,
			Multiline = true,
			ReadOnly = true,
			ScrollBars = ScrollBars.Both,
			WordWrap = false,
			Font = mono,
			Text = text.Replace("\n", Environment.NewLine),
		};
		var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, AutoSize = true };
		var close = new Button { Text = "Закрыть", AutoSize = true, DialogResult = DialogResult.OK };
		var copy = new Button { Text = "Копировать", AutoSize = true };
		copy.Click += (_, _) => { try { Clipboard.SetText(box.Text); } catch (Exception) { /* clipboard busy */ } };
		buttons.Controls.Add(close);
		buttons.Controls.Add(copy);
		layout.Controls.Add(box, 0, 0);
		layout.Controls.Add(buttons, 0, 1);
		form.Controls.Add(layout);
		form.AcceptButton = close;
		form.CancelButton = close;
		form.ShowDialog();
	}

	private static string Prompt(string title, string current)
	{
		using var form = new Form
		{
			Text = "TrayMon",
			AutoScaleMode = AutoScaleMode.Font,
			AutoSize = true,
			AutoSizeMode = AutoSizeMode.GrowAndShrink,
			FormBorderStyle = FormBorderStyle.FixedDialog,
			StartPosition = FormStartPosition.CenterScreen,
			MinimizeBox = false,
			MaximizeBox = false,
			ShowInTaskbar = false,
			TopMost = true,
		};
		var layout = new TableLayoutPanel
		{
			Dock = DockStyle.Fill,
			ColumnCount = 1,
			RowCount = 3,
			AutoSize = true,
			AutoSizeMode = AutoSizeMode.GrowAndShrink,
			Padding = new Padding(12),
		};
		var caption = new Label { Text = title, AutoSize = true, MaximumSize = new Size(360, 0), Margin = new Padding(0, 0, 0, 6) };
		var box = new TextBox { Text = current, Width = 360, Margin = new Padding(0, 0, 0, 8) };
		var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill };
		var cancel = new Button { Text = "Отмена", AutoSize = true, DialogResult = DialogResult.Cancel };
		var ok = new Button { Text = "OK", AutoSize = true, DialogResult = DialogResult.OK };
		buttons.Controls.Add(cancel);
		buttons.Controls.Add(ok);
		layout.Controls.Add(caption, 0, 0);
		layout.Controls.Add(box, 0, 1);
		layout.Controls.Add(buttons, 0, 2);
		form.Controls.Add(layout);
		form.AcceptButton = ok;
		form.CancelButton = cancel;
		return form.ShowDialog() == DialogResult.OK ? box.Text.Trim() : null;
	}

	// ---- number formatting ----
	//
	// The rules themselves live in Format, so they can be checked without a tray. These are the
	// names the rest of this file has always used.

	private static string Mb(double v) => Format.Mb(v);
	private static string Mb2(double v) => Format.Mb2(v);
	private static string MbitWhole(double megabytesPerSecond) => Format.MbitWhole(megabytesPerSecond);
	private static string Mbit(double megabytesPerSecond) => Format.Mbit(megabytesPerSecond);
	private static string Rpm(double v) => Format.Rpm(v);
	private static double Stalled(double rpm) => Format.Stalled(rpm);
	private static string Whole(double? v) => Format.Whole(v);
	private static string Pct(double? v) => Format.Pct(v);
	private static string Deg(double? v) => Format.Deg(v);

	private bool _disposed;

	protected override void Dispose(bool disposing)
	{
		// Guarded because the context can be disposed both by the framework and by Main.
		if (disposing && !_disposed)
		{
			_disposed = true;
			_timer?.Stop();
			_timer?.Dispose();
			_configWatcher?.Dispose();
			if (!_dry)
			{
				Microsoft.Win32.SystemEvents.SessionSwitch -= OnSessionSwitch;
				Microsoft.Win32.SystemEvents.SessionEnding -= OnSessionEnding;
			}

			// Wait for the background readers first. Closing the sensor library unloads a ring-0
			// driver, and doing that while a SMART query is still in flight took the process down
			// on exit — the tasks were not kept anywhere, so nothing could wait for them.
			Task[] pending;
			lock (_background) pending = _background.Where(t => !t.IsCompleted).ToArray();
			if (pending.Length > 0)
			{
				try { Task.WaitAll(pending, 5000); } catch (Exception) { /* going away regardless */ }
			}

			foreach (var slot in _order) slot.Icon?.Dispose();
			_menu?.Dispose();
			_sync?.Dispose();
			// Only what this instance built. In the dry run the sensors belong to --once, which
			// is still using them and disposes them itself.
			if (_ownsSensors)
			{
				_perf?.Dispose();
				_gpu?.Dispose();
				_lhm?.Dispose();
			}
			_self?.Dispose();
		}
		base.Dispose(disposing);
	}
}
