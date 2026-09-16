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

		// Everything printed below goes through this. The promise of an output fit for a public
		// issue tracker used to be kept by hand, in the lines that print the RAID serial — while
		// the icon layer printed slot ids, captions and tooltips carrying the same serial, and an
		// unhandled error message carrying absolute paths.
		var hide = Secrets(config);
		void Say(string line) => Console.WriteLine(hide.Clean(line));

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
		using var lhm = new LhmSensor(config.Sensors);
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

		// Serial numbers only exist once something has answered, so they join the list here.
		foreach (var d in r.RaidDisks) hide.Hide(d.Serial, "<serial>");
		foreach (var d in r.Disks) hide.Hide(d.Serial, "<serial>");

		Say("");
		Say($"CPU      {Fmt(r.CpuLoad)} %      {Fmt(r.Slow.CpuTemp)} °C");
		Say($"RAM      {Fmt(r.MemLoad)} %      {r.MemUsedGb.ToString("0.0", ci)} / {r.MemTotalGb.ToString("0.0", ci)} GB");
		if (r.Gpus.Count == 0)
			Say($"GPU      no NVIDIA driver ({gpu.LastError ?? "nvml.dll missing"})");
		foreach (var g in r.Gpus)
		{
			Say($"GPU {g.Index}    {Fmt(g.Load)} %      {Fmt(g.Temp)} °C   {g.Name}");
			Say($"GPU {g.Index} mem {Fmt(g.MemLoad)} %      {g.MemUsedGb.ToString("0.0", ci)} / {g.MemTotalGb.ToString("0.0", ci)} GB");
			Say($"GPU {g.Index} fan {(g.FanRpm.HasValue ? g.FanRpm.Value.ToString("0", ci).PadLeft(4) + " rpm" : "   — rpm")}   " +
				$"{(g.FanDuty.HasValue ? g.FanDuty.Value.ToString("0", ci) + "%" : "—")}");
		}
		foreach (var d in r.Disks)
			Say($"disk     {d.Temp.ToString("0", ci)} °C     {d.Name}" +
				$"{(d.WearPercent.HasValue ? "   wear " + d.WearPercent.Value.ToString("0", ci) + "%" : "")}" +
				$"{(d.SparePercent.HasValue ? "   spare " + d.SparePercent.Value.ToString("0", ci) + "%" : "")}");
		if (r.Disks.Count == 0)
			Say("disk     no temperatures (no drive answered the storage query, or disks hidden behind RAID)");
		if (r.RaidDisks.Count == 0)
			Say("raid     no answer over CSMI (smartctl.exe missing, or no RAID controller here)");
		foreach (var d in r.RaidDisks)
			Say($"raid     {(d.Temp.HasValue ? d.Temp.Value.ToString("0", ci) : " —")} °C     {d.Name} <serial>   " +
				$"health {(string.IsNullOrEmpty(d.Health) ? "—" : d.Health)}   (behind RAID controller)");
		foreach (var f in r.Slow.Fans)
			Say($"fan      {f.Rpm.ToString("0", ci).PadLeft(4)} rpm   {(f.Duty.HasValue ? f.Duty.Value.ToString("0", ci) + "%" : "—")}   {f.Chip}/{f.Name}{(f.Rpm == 0 ? "   (header empty)" : "")}");
		foreach (var n in r.Nets)
			Say($"net      ↓ {(n.InMb * 8).ToString("0.00", ci)} / ↑ {(n.OutMb * 8).ToString("0.00", ci)} Mbit/s   " +
				$"link {(n.LinkMb > 0 ? (n.LinkMb * 8).ToString("0", ci) : "unknown")} Mbit/s   {n.Name}");
		if (r.Nets.Count == 0)
			Say("net      no physical adapter with a link");
		foreach (var v in r.Volumes)
			Say($"volume   {v.Name,-4} R {v.ReadMb.ToString("0.00", ci).PadLeft(8)} / W {v.WriteMb.ToString("0.00", ci).PadLeft(8)} MB/s");
		foreach (var s in r.Space)
			Say($"free     {s.Name,-4} {s.FreeGb.ToString("0", ci).PadLeft(6)} of {s.TotalGb.ToString("0", ci)} GB");
		Say($"uptime   {(r.UptimeHours.HasValue ? r.UptimeHours.Value.ToString("0.0", ci) + " h" : "—")}");
		Say($"top io   {string.Join(", ", r.TopIo.Select(t => $"{t.Name} {t.Mb.ToString("0.0", ci)}"))}");
		if (r.Battery is not null)
			Say($"battery  {r.Battery.Charge.ToString("0", ci).PadLeft(3)} %      " +
				$"{(r.Battery.OnBattery ? "on battery" : "on line")}" +
				$"{(r.Battery.MinutesLeft.HasValue ? ", " + r.Battery.MinutesLeft.Value.ToString("0", ci) + " min left" : "")}");
		if (ups.Present)
			Say($"ups      {Fmt(r.Ups.Charge)} %      {r.Ups.StatusText}, " +
				$"{Fmt(r.Ups.RunTimeMin)} min left, load {Fmt(r.Ups.Load)} %{(r.Ups.NeedsNewBattery ? ", REPLACE BATTERY" : "")}");
		else
			Say($"ups      no answer over SNMP at <host from TrayMon.json> ({ups.LastError})");

		Say("");
		Say($"counter in use: {perf.CounterInUse}");
		Say($"sensor driver:  {SensorDriverLine(lhm)}");
		if (lhm.Ring0Note is not null) Say($"ring-0 device:  {lhm.Ring0Note}");
		Say($"elevated:       {(TrayApp.IsElevated ? "yes" : "no")}");
		Say($"smartctl:       {(hdd.Available ? "found" : "not found")}");
		if (hdd.Note is not null) Say($"                {hdd.Note}");
		// Whether, not who: the identity would be a domain and account name, and this output goes
		// into public issue trackers. The name is in the diagnostics window instead.
		// The whole load path, not just the folder: a file inside a well-protected folder can carry
		// its own permissive ACL, and the configured smartctl.exe is started with our token.
		var loose = Autostart.InstallUnsafe(config.Tools?.Smartctl, out _);
		Say($"install folder: {(loose ? "WRITABLE by a non-administrator — do not enable autostart from here" : Autostart.LastCheckError is not null ? "permissions not readable — " + Autostart.LastCheckError : "writable by administrators only")}");
		Say($"settings file:  TrayMon.json {(File.Exists(Config.Path) ? "found" : "absent, defaults in use")}" +
			$"{(config.LoadError is null ? "" : " — " + config.LoadError)}" +
			$"{(config.LoadNote is null ? "" : " — " + config.LoadNote)}");
		Say("cost, ms (cold / warm):");
		foreach (var source in cold.Keys)
			Say($"                {source,-14} {cold[source].ToString("0.0", ci).PadLeft(7)} / " +
				$"{warm[source].ToString("0.0", ci).PadLeft(6)}");

		var failures = 0;
		if (withIcons)
		{
			Say("");
			// The warmed sensors are handed over rather than built again: a second PerfSensors
			// read its rate counters milliseconds after establishing their baseline (so the
			// numbers were noise), and a second Computer.Open() in the same process opened the
			// ring-0 driver twice for no reason.
			using var dry = new TrayApp(perf, gpu, lhm, disk, hdd, ups);
			Say(dry.DryRun(ticks, hide));
			failures = dry.Errors;
		}
		Say("");
		// A smoke test that always returns zero is not a smoke test. An exception inside the icon
		// layer is caught per family, so the run finishes and prints — and used to report success.
		return failures > 0 ? 1 : 0;
	}

	/// <summary>
	/// Everything that must not leave this machine. Registered once and applied to every line
	/// <c>--once</c> prints, including the ones that come out of an exception message.
	/// </summary>
	private static Redactor Secrets(Config config)
	{
		var hide = new Redactor();
		hide.Hide(config?.Ups?.Host, "<ups host>");
		// Not the built-in names. "Administrator" identifies nobody, and hiding it mangled the
		// English text around it: the line about folder permissions came out as "WRITABLE by a
		// non-<user>", which is both ugly and harder to act on.
		if (!Generic(Environment.UserName)) hide.Hide(Environment.UserName, "<user>");
		hide.Hide(Environment.MachineName, "<host>");
		if (!Generic(Environment.UserDomainName)) hide.Hide(Environment.UserDomainName, "<domain>");
		hide.Hide(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "<profile>");
		hide.Hide(AppContext.BaseDirectory.TrimEnd('\\'), "<app>");
		hide.Hide(Environment.ProcessPath, "<app>\\TrayMon.exe");
		hide.Hide(config?.Tools?.Smartctl, "<smartctl>");
		hide.Hide(config?.Log?.Path, "<log>");
		return hide;
	}

	/// <summary>A built-in account or workgroup name: it names no person and is a word the rest of
	/// the report uses in its ordinary sense.</summary>
	private static bool Generic(string name) =>
		name is not null &&
		new[] { "administrator", "admin", "user", "guest", "system", "workgroup", "администратор", "пользователь" }
			.Contains(name.Trim(), StringComparer.OrdinalIgnoreCase);

	private static string SensorDriverLine(LhmSensor lhm) =>
		lhm.Disabled ? "DISABLED — " + lhm.LastError
		: !lhm.Available ? "UNAVAILABLE — " + (lhm.LastError ?? "run elevated, or the driver refused to load")
		: lhm.DriverBlocked ? "BLOCKED — " + lhm.LastError
		: "loaded";

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
		return Merge(fromDriver, fromLibrary);
	}

	/// <summary>
	/// Joins the two sources. Pure, so the three defects it used to have can be checked against
	/// lists instead of against a machine: one drive answering the storage query made every
	/// library-only disk vanish, a dictionary keyed by model kept only the last of two identical
	/// models, and a substring match handed one wear figure to several physical disks.
	/// </summary>
	internal static List<DiskReading> Merge(List<DiskReading> fromDriver, List<DiskReading> fromLibrary)
	{
		if (fromDriver.Count == 0) return fromLibrary;

		var merged = new List<DiskReading>(fromDriver.Count + fromLibrary.Count);
		var used = new bool[fromLibrary.Count];

		// Model names differ in spacing and vendor prefix between the two sources, so the match is
		// loose: one contains the other. It is only used when it is unambiguous **in both
		// directions** — two drives of the same model with one wear figure between them cannot be
		// told apart, and guessing is how one drive's wear ended up on another's icon.
		var claims = new int[fromLibrary.Count];
		for (var i = 0; i < fromLibrary.Count; i++)
			foreach (var d in fromDriver)
				if (Similar(fromLibrary[i].Name, d.Name)) claims[i]++;

		foreach (var d in fromDriver)
		{
			var hit = -1;
			var ambiguous = false;
			for (var i = 0; i < fromLibrary.Count; i++)
			{
				if (used[i] || claims[i] > 1 || !Similar(fromLibrary[i].Name, d.Name)) continue;
				if (hit >= 0) { ambiguous = true; break; }
				hit = i;
			}
			if (ambiguous || hit < 0) { merged.Add(d); continue; }
			used[hit] = true;
			merged.Add(d with
			{
				WearPercent = fromLibrary[hit].WearPercent,
				SparePercent = fromLibrary[hit].SparePercent,
			});
		}

		// Whatever only the library saw. One drive answering the storage query used to make every
		// disk that only the library knows about disappear from the list altogether — and with it
		// its icon, which had been there a minute earlier.
		for (var i = 0; i < fromLibrary.Count; i++)
		{
			if (used[i]) continue;
			if (merged.Any(m => Similar(m.Name, fromLibrary[i].Name))) continue;
			merged.Add(fromLibrary[i]);
		}
		return merged;
	}

	private static bool Similar(string a, string b) =>
		!string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(b) &&
		(a.Contains(b, StringComparison.OrdinalIgnoreCase) || b.Contains(a, StringComparison.OrdinalIgnoreCase));

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
	/// Poll rate while the session is locked or disconnected. Nobody can see the icons, and a
	/// repaint nobody can see is not worth two seconds of a core — on a server with the RDP
	/// session closed that is most of the day. It slows the *drawing*: the schedules below are
	/// wall-clock, so how long a real problem stays unnoticed does not change with it.
	/// </summary>
	private const int IdleTickMs = 30000;

	// Periods in milliseconds, not in ticks.
	//
	// Ticks were wrong for these. TickMs is configurable up to a minute and the timer slows to
	// thirty seconds while the session is locked, so a "30 s" UPS poll became one every 450 s and
	// a "600 s" RAID poll one every 2.5 hours — and the Windows-log events those produce exist
	// precisely for the case where nobody is watching the tray. Detection time now depends on the
	// tick only through its granularity: a source is polled at the first tick after its deadline.
	//
	// There is no phase arithmetic any more, and the rule that used to be written here — "if two
	// periods share a divisor, their phases must differ modulo it" — was both wrong (a coincidence
	// is possible exactly when the phases agree modulo gcd, not modulo any shared divisor) and
	// violated by its own table: 31 and 3 are coprime, so those two met regularly whatever the
	// phases were. What actually matters is that no heavy read happens on the UI thread (they are
	// all in Spawn), that two of them cannot enter the sensor library at once (its own lock), and
	// that a slow answer cannot pile up on the next one (the _*RefreshRunning flags).
	private const int GpuEveryMs = 4000;
	private const int SlowEveryMs = 6000;        // CPU temperature and fans
	private const int IoEveryMs = 6000;          // network and volume throughput
	private const int TopIoEveryMs = 12000;      // the ~250-instance process query
	private const int UpsEveryMs = 30000;        // a UDP round trip to the SNMP agent
	private const int StatsEveryMs = 30000;      // recompute the min/avg/max line for tooltips
	private const int DiskEveryMs = 62000;
	private const int SpaceEveryMs = 300000;     // free space changes slowly and costs a syscall
	private const int RaidEveryMs = 600000;      // spawns smartctl.exe; HDD temperature drifts slowly

	/// <summary>
	/// How old a published reading may be before its icons stop counting as alive. The background
	/// readers publish a snapshot and a timestamp; the UI thread used to re-mark a slot as seen on
	/// every tick from whatever was in the cache, so a reader that hung — which is exactly what the
	/// running flag protects against, and therefore exactly what stops new attempts — kept showing
	/// hours-old numbers in normal colours. Two periods plus the slowest timeout here.
	/// </summary>
	private static long MaxAgeMs(int periodMs) => 2L * periodMs + 30000;

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
	private const double NeverAlerts = Alarm.Never;

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
	// NotifyByDefault, like the other sources that mean hardware is in trouble. Both README
	// versions promised a notification for the UPS and it had none: the only interrupt it ever
	// raised was the separate "switched to battery" message, which ignored the setting entirely,
	// so a low charge or a worn battery said nothing at all unless the user found the checkbox.
	private static readonly Metric UpsMetric = new() { Group = "Питание", Order = 0, Label = "ИБП", Unit = "% заряда", Plate = UpsPlate, Warn = 50, Crit = 75, Pool = new[] { UpsGuid }, Inverted = true, NotifyByDefault = true };
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

		/// <summary>The number itself, in the unit the icon draws, kept for the CSV trail: the
		/// drawn text changes unit under its own feet (GB to TB, hours to days, rpm to per cent)
		/// and a column of those cannot be read back as a series.</summary>
		public double? LastValue;

		/// <summary>The unit <see cref="LastValue"/> is in, for the same reason.</summary>
		public string LastUnit = "";

		/// <summary>When the reading behind <see cref="LastValue"/> was actually measured.</summary>
		public DateTime MeasuredAt;

		public string LastDetail = "";

		/// <summary>The part of the tooltip that goes after the five-minute line. A tray tooltip is
		/// cut at 127 characters and an adapter description takes most of them, so the network icon
		/// keeps its units line last and lets that be what is lost.</summary>
		public string DetailTail = "";

		public long SeenTick = -1;
		/// <summary>Wall clock of the last reading, so a slot is forgotten after a day whatever
		/// the tick rate happens to be.</summary>
		public long SeenAtMs = Environment.TickCount64;
		/// <summary>The last number this slot actually had, kept after it goes grey: "— the
		/// adapter is gone, last 12 Mbit/s at 14:20" answers a question a bare dash cannot.</summary>
		public string FarewellText;
		public DateTime FarewellAt;
		public bool Dead;

		/// <summary>
		/// Alert level last reported: -2 never had a reading, -1 dead, 0 normal, 1 yellow, 2 red.
		/// Transitions between these are what the journal and the balloons are made of — and the
		/// difference between -2 and -1 is why a RAID array that is already failing when the program
		/// starts now reports itself: "coming back from silence is not an alarm" used to swallow
		/// the very first state as well.
		/// </summary>
		public int Level = Alarm.Unknown;

		/// <summary>Severity of the last reading, kept even for an icon nobody is showing —
		/// the summary icon exists precisely so a hidden metric can still raise the alarm.</summary>
		public double? Severity;

		public readonly Stats Stats = new();
		public string StatsText = "";

		/// <summary>Measurement stamp already fed to <see cref="Stats"/>, so a cached reading from
		/// a source polled every ten minutes is not added to the window three hundred times.</summary>
		public long StatsStamp = -1;

		/// <summary>Tick on which something else already notified about this slot, so one event
		/// does not produce both a special message and a generic threshold one.</summary>
		public long NotifiedTick = -1;
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

	// Deadlines, in Environment.TickCount64. See the period constants: what used to be "every Nth
	// tick with phase P" is now "not before this moment".
	private long _gpuAt, _ioAt, _slowAt, _topIoAt, _upsAt, _statsAt, _diskAt, _spaceAt, _raidAt;

	/// <summary>Set while the session was idle, so the return to it can refresh what went stale
	/// instead of waiting for the next deadline of every source in turn.</summary>
	private bool _catchUp;

	/// <summary>Lines written to the CSV trail but not yet on disk, and how many were dropped
	/// because the writer was still busy. See <see cref="WriteLogLine"/>.</summary>
	private readonly Queue<string> _logQueue = new();
	private int _logWriting;
	private int _logDropped;
	private bool _logHeaderChecked;

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

	/// <summary>Monotonic start of this run, so "per cent of the window" is per cent of a real
	/// window and not of ticks multiplied by whatever the interval happens to be now.</summary>
	private readonly long _startedTicks = Stopwatch.GetTimestamp();

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
		_lhm = lhm ?? new LhmSensor(_config.Sensors);
		_disk = disk ?? new DiskSensor();
		_hdd = hdd ?? new HddSensor(_config.Tools);
		_ups = ups ?? new UpsSensor(_config.Ups);
		_intervalMs = Math.Max(TickMs, _config.TickMs);

		// The pools remember what they handed out, in the settings file: the assignment used to
		// live only in memory, so after a restart with one device absent the next device took its
		// GUID — and with it the tray position and the visibility Windows keeps against it.
		var store = (IGuidStore)_config;
		_gpuPool = new GuidPool("видеокарт", "gpu", GpuGuids, store);
		_vramPool = new GuidPool("значков видеопамяти", "vram", VramGuids, store);
		_gpuTempPool = new GuidPool("температур GPU", "gpu.temp", GpuTempGuids, store);
		_gpuFanPool = new GuidPool("вентиляторов GPU", "fan.gpu", GpuFanGuids, store);
		_netPool = new GuidPool("сетевых адаптеров", "net", NetGuids, store);
		_volumePool = new GuidPool("томов", "vol", VolumeGuids, store);
		_freePool = new GuidPool("значков свободного места", "free", FreeGuids, store);
		_diskPool = new GuidPool("дисков", "disk", DiskGuids, store);
		_raidPool = new GuidPool("дисков за RAID", "disk.raid", RaidTempGuids, store);
		_fanPool = new GuidPool("вентиляторов", "fan", FanGuids, store);
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

		// No explicit first round of the slow sources here: every deadline starts at zero, so the
		// first tick — which runs at the end of this constructor — spawns all of them anyway, and
		// doing it twice only meant the running flags refused the second attempt.
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
		// Coming back: refresh everything at once rather than up to thirty seconds later for the
		// cheap sources and up to ten minutes later for the rest. The first thing somebody does
		// after unlocking is look at the icons.
		if (!busy) return;
		_catchUp = true;
		OnTick(null, EventArgs.Empty);
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
	public string DryRun(int ticks = 1, Redactor hide = null)
	{
		hide ??= new Redactor();
		var report = new StringBuilder();
		var before = new Dictionary<string, string>(StringComparer.Ordinal);

		for (var i = 0; i < ticks; i++)
		{
			if (i > 0) Thread.Sleep(_intervalMs);   // real spacing, so rate counters mean something
			// Nothing is read here any more. The sources used to be polled by this method and then
			// again by Tick() — so the CPU rate came out of two collects milliseconds apart — and
			// the lists were assigned without bumping the versions the families check, which made
			// the second and third pass keep showing the first reading. Now the dry run exercises
			// exactly the publication path the running program uses: Spawn() runs inline while
			// _dry, so the background readers happen in order on this thread.
			OnTick(null, EventArgs.Empty);   // through OnTick, so the tick times itself here too
			foreach (var slot in _order)
			{
				var now = $"{slot.LastText ?? "—"}/{Alarm.Name(slot.Level)}";
				var known = before.TryGetValue(slot.Id, out var was);
				before[slot.Id] = now;
				if (known && was == now) continue;
				// The first tick is the initial state, and the table below already prints it. What
				// is worth seeing is what moved afterwards — fading, coming back, changing level.
				if (i == 0) continue;
				report.AppendLine($"  tick {(i + 1).ToString(CultureInfo.InvariantCulture)}: " +
								  $"{slot.Id} {(known ? was : "—")} → {now}");
			}
		}

		report.AppendLine($"icons: {_order.Count(s => s.Settings.Enabled)} shown of {_order.Count} known" +
						  $"   ticks: {ticks}   errors: {_tickErrors}");
		foreach (var slot in _order)
			report.AppendLine(
				$"  {(slot.Settings.Enabled ? "on " : "off")} {slot.Guid.ToString()[^2..]}  " +
				$"{slot.Id,-28} {(slot.LastText ?? "—"),5}  {State(slot)} {LabelOf(slot)}   " +
				$"{(slot.LastDetail + slot.StatsText + slot.DetailTail).Replace("\n", " · ")}");
		foreach (var line in Overflows()) report.AppendLine("  " + line);
		if (_lastTickError is not null) report.AppendLine("  last error: " + _lastTickError);
		// Renders and shell calls are zero here by construction, and saying so matters: they are
		// the two numbers a change to the tray path is judged by, and a run that prints "0 / 0"
		// without explanation looks exactly like the broken P/Invoke they exist to catch. The
		// place to read them is "Диагностика…" in a running program.
		report.AppendLine("shell not touched in dry run — renders and shell calls stay 0 here; " +
						  "read them in «Диагностика…»");
		// Not comparable to the running program's figure: here the background readers run inline
		// inside the tick, on purpose, so that the dry run exercises the same publication path.
		report.AppendLine($"tick {(_tickTicks * 1000.0 / Stopwatch.Frequency / Math.Max(1, _tick)).ToString("0.00", CultureInfo.InvariantCulture)} ms " +
						  "(includes the background reads, which the running program does off this thread)");
		// Slot ids, captions and tooltips carry disk serial numbers and whatever the user called
		// their icons; the promise about this output being safe to paste into a public tracker has
		// to cover them too.
		return hide.Clean(report.ToString());
	}

	/// <summary>Errors caught during this run, so <c>--once --icons</c> can fail instead of
	/// printing a report and returning success.</summary>
	public int Errors => _tickErrors;

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
		var io = Due(ref _ioAt, IoEveryMs);
		_perf.Read(_r, io);   // CPU every tick, network and volumes rarer
		if (io) _r.IoAt = Environment.TickCount64;
		MemorySensor.Read(_r);
		BatterySensor.Read(_r);
		// Free: GetTickCount64, not a PDH query over the System object. See PerfSensors.ReadUptime.
		_r.UptimeHours = PerfSensors.ReadUptime();
		if (Due(ref _gpuAt, GpuEveryMs)) { _gpu.Read(_r); _r.GpusAt = Environment.TickCount64; }

		// The heavy readers are all off the UI thread, each behind its own flag. The sensor
		// library alone measured 18-45 ms, which is a fifth of a second of a frozen message pump
		// every six seconds when it ran here.
		if (Due(ref _slowAt, SlowEveryMs)) Spawn(RefreshSlow);
		// The ~250-instance process query is the most expensive PDH read here and was the one
		// thing left running on the UI thread, against a README that said otherwise.
		if (Due(ref _topIoAt, TopIoEveryMs) && _r.Volumes.Sum(v => v.ReadMb + v.WriteMb) > 0.5)
			Spawn(RefreshTopIo);
		if (Due(ref _diskAt, DiskEveryMs)) Spawn(RefreshDisks);
		if (Due(ref _raidAt, RaidEveryMs) && Environment.TickCount64 >= _raidRetryAt) Spawn(RefreshRaidDisk);
		if (Due(ref _upsAt, UpsEveryMs) && Environment.TickCount64 >= _upsRetryAt) Spawn(RefreshUps);
		if (Due(ref _spaceAt, SpaceEveryMs))
		{
			// The names are taken here and not inside the task: the volume list belongs to the UI
			// thread and is cleared and refilled on it from here.
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
		// Every tick from the second one on, not only on the second. A settings file with every
		// icon switched off is a legitimate hand edit, and a pooled icon can be the last one
		// carrying the menu while its device is unplugged — Fade then handed the slot back after a
		// day and left a process with no icons, no menu and no way out but Task Manager. Not on the
		// first tick: it runs inside the constructor, before the message loop exists, and this can
		// put a dialog on the screen.
		if (!_dry && _tick >= 2) EnsureSomethingVisible();
		Housekeeping();
		// Losing every colour, label and threshold is not something to mention only to
		// someone who thinks to open the diagnostics window. On the second tick, for the same
		// reason as above. Only a file that could not be read at all gets this dialog: a value
		// that had to be adjusted is a note for the diagnostics window, and announcing it as
		// "файл не прочитан, взяты значения по умолчанию" was a lie about a file that had been
		// read and applied.
		if (_tick == 2 && !_dry && _config.LoadError is not null)
			Later($"Файл настроек не прочитан, взяты значения по умолчанию:\n\n{_config.LoadError}",
				MessageBoxIcon.Warning);
		if (Due(ref _statsAt, StatsEveryMs)) RefreshStats();
		// Cleared once every deadline of this tick has been asked: coming back to a session makes
		// all of them due at once, which is the point — a source polled every ten minutes must not
		// keep a ten-minute-old number on the screen somebody has just started looking at.
		_catchUp = false;
		PickUpConfigEdit();
		WriteLogLine();
	}

	/// <summary>
	/// Whether this deadline has passed, moving it on if it has. Wall clock, so a source keeps its
	/// real period whatever the tick rate is set to and whatever the session is doing.
	/// </summary>
	private bool Due(ref long at, int periodMs)
	{
		var now = Environment.TickCount64;
		if (now < at && !_catchUp) return false;
		at = now + periodMs;
		return true;
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
		var slowFresh = Fresh(_r.SlowAt, SlowEveryMs);

		var cpu = Slot("cpu", CpuMetric, CpuGuid);
		Record(cpu, _r.CpuLoad, unit: "%");
		Show(cpu, Whole(_r.CpuLoad), _r.CpuLoad,
			$"{Pct(_r.CpuLoad)}   температура {(slowFresh ? Deg(_r.Slow.CpuTemp) : "—")}");

		// 88/95 rather than 80/90: on this hypervisor 156 of 192 GB in use is the normal
		// resting state, and a plate that is permanently yellow says nothing.
		var ram = Slot("ram", RamMetric, RamGuid);
		// Recorded in the unit the icon draws (gigabytes), coloured by the unit the thresholds
		// are in (percent) — otherwise the five-minute line in the tooltip is in a different
		// unit from the number above it.
		Record(ram, _r.MemLoad.HasValue ? _r.MemUsedGb : null, _r.MemLoad, unit: "ГБ");
		// Commit charge next to physical use: it is the number that answers "why is everything
		// paging while RAM sits at 60 %", and it comes out of the same syscall.
		var commit = _r.CommitUsedGb.HasValue
			? $"   выделено {_r.CommitUsedGb.Value.ToString("0.0", ci)} / {_r.CommitTotalGb.ToString("0.0", ci)} ГБ"
			: "";
		Show(ram, Whole(_r.MemLoad.HasValue ? _r.MemUsedGb : null), _r.MemLoad,
			$"{_r.MemUsedGb.ToString("0.0", ci)} / {_r.MemTotalGb.ToString("0.0", ci)} ГБ   {Pct(_r.MemLoad)}{commit}");

		// Only when there is a sensor library to read at all. A machine with no NVIDIA card used
		// to carry three permanent grey dashes and one without an elevated token a fourth, while
		// the README promised the opposite. What is missing now says why, in the tooltip.
		if (!_lhm.Available) return;
		var slow = _r.Slow;
		var cpuTemp = Slot("cpu.temp", CpuTempMetric, CpuTempGuid);
		if (slowFresh) Record(cpuTemp, slow.CpuTemp, unit: "°C", stamp: _r.SlowAt);
		Show(cpuTemp, Whole(slow.CpuTemp), slow.CpuTemp, $"пакет {Deg(slow.CpuTemp)}");
	}

	/// <summary>Whether a background reader's last snapshot is recent enough to be shown as a
	/// measurement rather than as a memory. See <see cref="MaxAgeMs"/>.</summary>
	private static bool Fresh(long at, int periodMs) =>
		at != 0 && Environment.TickCount64 - at <= MaxAgeMs(periodMs);

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
				slot.LastValue = null;
				// Severity as well. Clearing only LastSeverity left the summary icon scoring a
				// source that had gone away — CPU 90 kept a score of 107 for as long as the program
				// ran — and wrote that severity into every CSV row after it.
				slot.Severity = null;
				slot.LastDetail = Reason(slot) + Farewell(slot);
				slot.DetailTail = "";
				NoteLevelChange(slot, Alarm.Dead);
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
			// Unless it is the only icon the menu is left on. One enabled pooled icon whose device
			// is unplugged was enough to remove the last way into the program after a day.
			if (CarriesMenu(slot) && _order.Count(CarriesMenu) <= 1) continue;
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
		// A reader that has not come back is a different story from a source that answered
		// "nothing", and the icon is the only place either of them is visible.
		if (Stale(slot, out var stale)) return stale;
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
		if (slot.Id == "worst") return "нет ни одного источника, который можно оценить";
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
	/// <summary>
	/// Whether this slot's family has a published snapshot that is too old to be shown. Answered
	/// per family rather than per slot, because the age belongs to the reader, not to the device.
	/// </summary>
	private bool Stale(IconSlot slot, out string message)
	{
		message = null;
		long at;
		int period;
		if (slot.Id.StartsWith("disk.raid.", StringComparison.Ordinal)) { at = _r.RaidAt; period = RaidEveryMs; }
		else if (slot.Id.StartsWith("disk.", StringComparison.Ordinal)) { at = _r.DisksAt; period = DiskEveryMs; }
		else if (slot.Id.StartsWith("free.", StringComparison.Ordinal)) { at = _r.SpaceAt; period = SpaceEveryMs; }
		else if (slot.Id == "ups") { at = _r.UpsAt; period = UpsEveryMs; }
		else if (slot.Id == "cpu.temp" ||
				 (slot.Id.StartsWith("fan.", StringComparison.Ordinal) &&
				  !slot.Id.StartsWith("fan.gpu", StringComparison.Ordinal))) { at = _r.SlowAt; period = SlowEveryMs; }
		else return false;

		if (at == 0 || Fresh(at, period)) return false;
		var seconds = (Environment.TickCount64 - at) / 1000;
		message = "опрос не завершился — данные устарели на " +
				  seconds.ToString(CultureInfo.InvariantCulture) + " с";
		return true;
	}

	private string SensorHint(string whenWorking) =>
		_lhm.Disabled ? "драйвер датчиков выключен настройкой Sensors.UseSensorDriver"
		: !IsElevated ? "нужен запуск от администратора — температуры читаются через драйвер"
		: !_lhm.Available ? "драйвер датчиков не загрузился" + (_lhm.LastError is null ? "" : ": " + _lhm.LastError)
		// The one state nothing in the program could fix and nothing reported: the library opens,
		// says it is fine, and its driver is being refused by HVCI or removed by the antivirus.
		: _lhm.DriverBlocked ? _lhm.LastError
		: whenWorking;

	// ---- the metric families ----

	private void ShowGpus()
	{
		var ci = CultureInfo.InvariantCulture;
		foreach (var g in _r.Gpus)
		{
			// The UUID tail, not the enumeration index. NVML orders by PCI bus, so adding,
			// removing or swapping a card moved every index along, and the settings — colour,
			// caption, thresholds — then belonged to a different card. Old ids are carried over
			// once, by Migrate, so nobody loses what they configured.
			var key = g.Key ?? g.Index.ToString(ci);
			var suffix = _r.Gpus.Count > 1 ? $" {g.Index + 1}" : "";
			var old = g.Index.ToString(ci);

			var load = Pooled(Migrate($"gpu.{old}", $"gpu.{key}"), GpuMetric, _gpuPool, key, GpuMetric.Label + suffix);
			if (load is not null)
			{
				Record(load, g.Load, unit: "%", stamp: _r.GpusAt);
				Show(load, Whole(g.Load), g.Load, $"{g.Name}   ядро {Pct(g.Load)}   температура {Deg(g.Temp)}");
			}

			var vram = Pooled(Migrate($"vram.{old}", $"vram.{key}"), VramMetric, _vramPool, key, VramMetric.Label + suffix);
			if (vram is not null)
			{
				Record(vram, g.MemLoad.HasValue ? g.MemUsedGb : null, g.MemLoad, unit: "ГБ", stamp: _r.GpusAt);
				Show(vram, Whole(g.MemLoad.HasValue ? g.MemUsedGb : null), g.MemLoad,
					$"{g.MemUsedGb.ToString("0.0", ci)} / {g.MemTotalGb.ToString("0.0", ci)} ГБ   {Pct(g.MemLoad)}");
			}

			var temp = Pooled(Migrate($"gpu.temp.{old}", $"gpu.temp.{key}"), GpuTempMetric, _gpuTempPool, key,
				GpuTempMetric.Label + suffix);
			if (temp is not null)
			{
				Record(temp, g.Temp, unit: "°C", stamp: _r.GpusAt);
				Show(temp, Whole(g.Temp), g.Temp, $"{Deg(g.Temp)}   загрузка {Pct(g.Load)}");
			}

			if (!g.FanRpm.HasValue && !g.FanDuty.HasValue) continue;
			// A driver without nvmlDeviceGetFanSpeedRPM leaves only the duty cycle, and a bare
			// "35" next to fan icons where "1.2" means twelve hundred revolutions is a different
			// unit with nothing to say so. The caption carries the unit in that case.
			var byDuty = !g.FanRpm.HasValue;
			var fanLabel = GpuFanMetric.Label + suffix + (byDuty ? " (%)" : "");
			var fan = Pooled(Migrate($"fan.gpu.{old}", $"fan.gpu.{key}"), GpuFanMetric, _gpuFanPool, key, fanLabel);
			if (fan is null) continue;
			var speed = g.FanRpm ?? g.FanDuty ?? 0;
			// A duty cycle is not proof of rotation: the driver reports what it asked for, and a
			// seized fan reports the same 35 %. With no tachometer the standstill alarm has nothing
			// to stand on, so it is not raised — and the tooltip says why.
			Record(fan, speed, byDuty ? 0 : StallSeverity(fan, g.FanRpm.Value, g.FanDuty),
				unit: byDuty ? "%" : "об/мин", stamp: _r.GpusAt);
			Show(fan,
				byDuty ? Whole(g.FanDuty) : Rpm(g.FanRpm.Value),
				byDuty ? 0 : StallSeverity(fan, g.FanRpm.Value, g.FanDuty),
				byDuty
					? $"{Pct(g.FanDuty)} задания (драйвер не сообщает обороты, остановку не видно)"
					: $"{g.FanRpm.Value.ToString("0", ci)} об/мин   задание {Pct(g.FanDuty)}");
		}
	}

	/// <summary>
	/// Carries an icon's settings over from an id that is no longer generated, and returns the new
	/// id. Nothing happens when there is nothing to move, which is the normal case.
	/// </summary>
	private string Migrate(string from, string to)
	{
		if (!_slots.ContainsKey(to) && _config.Rename(from, to)) _configDirty = true;
		return to;
	}

	/// <summary>
	/// What a fan reading of zero means for this icon. Every stop used to be a red plate, which is
	/// right for a CPU cooler and wrong for the case fans that spend an idle desktop switched off
	/// on purpose — the alarm was then permanent, and a permanent alarm is not one.
	/// </summary>
	private static double StallSeverity(IconSlot slot, double rpm, double? duty) =>
		slot.Settings.Stall switch
		{
			// Only a fan that is being driven and still does not turn.
			"zero-ok" => rpm > 0 || !(duty > 0) ? 0 : 100,
			"off" => 0,
			_ => Stalled(rpm),
		};

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
		// A snapshot nobody has refreshed for three periods is a memory, not a reading.
		if (!Fresh(_r.DisksAt, DiskEveryMs)) return;
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
			Record(slot, d.Temp, d.Worn ? NeverAlerts : d.Temp, unit: "°C", stamp: _r.DisksAt);
			if (!changed) continue;
			var wear = d.WearPercent.HasValue
				? $"   износ {d.WearPercent.Value.ToString("0", CultureInfo.InvariantCulture)}%" : "";
			var spare = d.SparePercent.HasValue
				? $"   резерв {d.SparePercent.Value.ToString("0", CultureInfo.InvariantCulture)}%" : "";
			Show(slot, Whole(d.Temp), d.Worn ? NeverAlerts : d.Temp,
				$"{Deg(d.Temp)}{wear}{spare}{(d.Worn ? "   РЕСУРС ИСЧЕРПАН" : "")}");
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
	internal static string[] DiskKeys(List<DiskReading> disks)
	{
		// Ordinals are handed out in serial-number order, not in enumeration order. Two drives of
		// the same model swapped their "#2" whenever the storage stack enumerated them the other
		// way round, and the settings and the tray position went with it.
		var order = Enumerable.Range(0, disks.Count)
			.OrderBy(i => disks[i].Serial ?? "￿", StringComparer.OrdinalIgnoreCase)
			.ToArray();
		var keys = new string[disks.Count];
		var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
		foreach (var i in order)
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
		if (!Fresh(_r.RaidAt, RaidEveryMs)) return;
		for (var i = 0; i < disks.Count; i++)
		{
			var d = disks[i];
			var key = string.IsNullOrEmpty(d.Serial) ? i.ToString(CultureInfo.InvariantCulture) : d.Serial;
			var label = string.IsNullOrEmpty(d.Serial) ? d.Name : $"{d.Name} {d.Serial}";
			var slot = Pooled($"disk.raid.{key}", RaidMetric, _raidPool, key, label);
			if (slot is null) continue;

			// A disk that is failing matters more than a disk that is warm, so a bad health
			// verdict takes the plate straight to red whatever the temperature says — including
			// when there is no temperature at all, which is why the severity is passed even when
			// the value is missing.
			var failing = HddSensor.Failing(d.Health);
			var severity = failing ? NeverAlerts : d.Temp;
			Record(slot, d.Temp, severity, unit: "°C", stamp: _r.RaidAt);
			if (!changed) continue;
			var health = string.IsNullOrEmpty(d.Health) ? "" : failing ? $"   ЗДОРОВЬЕ: {d.Health}" : "   здоровье в норме";
			var noTemp = d.Temp.HasValue ? "" : "   температуры нет (standby или прошивка её не отдаёт)";
			Show(slot, Whole(d.Temp), severity, $"{Deg(d.Temp)}{health}{noTemp}");
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
		if (!Fresh(_r.SlowAt, SlowEveryMs)) return;
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
			// the list there meant it never got one until the program was restarted. A header the
			// user has marked as expected gets one straight away, stopped or not.
			if (fan.Rpm > 0) _fanSeen.Add(key);
			var expected = _config.Get($"fan.{key}").Required ?? false;
			if (!expected && !_fanSeen.Contains(key)) continue;

			var label = fan.Name == CpuFanSensorName ? $"Вентилятор CPU ({fan.Name})" : key;
			var slot = Pooled(Migrate($"fan.{fan.Name}", $"fan.{key}"), FanMetric, _fanPool, key, label);
			if (slot is null) continue;
			var severity = StallSeverity(slot, fan.Rpm, fan.Duty);
			Record(slot, fan.Rpm, severity, unit: "об/мин", stamp: _r.SlowAt);
			var duty = fan.Duty.HasValue ? $"   задание {Pct(fan.Duty)}" : "";
			var policy = slot.Settings.Stall switch
			{
				"zero-ok" => "   остановка при нулевом задании — норма",
				"off" => "   тревога по остановке выключена",
				_ => "",
			};
			Show(slot, Rpm(fan.Rpm), severity,
				$"{fan.Rpm.ToString("0", CultureInfo.InvariantCulture)} об/мин{duty}{policy}");
		}
	}

	/// <summary>
	/// A stable key per fan: always the chip and the sensor name.
	///
	/// It used to be the bare name while that name was unique on the board, and the chip-qualified
	/// form only when it was not — so the key of an existing icon flipped between <c>Fan #1</c> and
	/// <c>Nuvoton NCT6796D/Fan #1</c> as a second SuperIO chip appeared and went, taking the
	/// settings and the tray identity with it. Old ids are carried over once by Migrate.
	/// </summary>
	private static string[] FanKeys(List<FanReading> fans)
	{
		var keys = new string[fans.Count];
		for (var i = 0; i < fans.Count; i++) keys[i] = $"{fans[i].Chip}/{fans[i].Name}";
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
			//
			// With no link speed there is nothing to express a percentage against, so the plate
			// keeps its own colour instead of being coloured by a number that was invented. The
			// tooltip says so, and Net.Bandwidth in the settings is how to give it a figure.
			var known = n.LinkMb > 0;
			var utilisation = known ? 100 * Math.Max(n.InMb, n.OutMb) / n.LinkMb : 0;
			var mbit = total * 8;
			// Gigabits past ten thousand megabits: five digits do not fit a 16-pixel plate in any
			// font, and a 10GbE link reaches them. The unit moves into the caption, the way free
			// space already does past 1000 GB.
			var inGbit = Format.TooWide(mbit);
			slot.DefaultLabel = inGbit ? $"{n.Name} (Гбит/с)" : n.Name;
			Record(slot, mbit, utilisation, unit: "Мбит/с", stamp: _r.IoAt);
			var of = known
				? $"   {utilisation.ToString("0", CultureInfo.InvariantCulture)}% от " +
				  $"{(n.LinkMb * 8).ToString("0", CultureInfo.InvariantCulture)} Мбит/с"
				: "   скорость линка неизвестна";
			// The units line is the tail: a tooltip is cut at 127 characters, an adapter
			// description takes most of them, and this is the part worth losing.
			Show(slot,
				inGbit ? Format.PerThousand(mbit) : MbitWhole(total),
				utilisation,
				$"↓ {Mbit(n.InMb)} / ↑ {Mbit(n.OutMb)} Мбит/с",
				$"\n{Mb2(n.InMb)} / {Mb2(n.OutMb)} МБ/с{of}");
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
			// Gigabytes per second past ten thousand megabytes: an NVMe array reaches it, and five
			// digits do not fit the plate. Unit in the caption, as everywhere else.
			var inGb = Format.TooWide(total);
			slot.DefaultLabel = inGb ? $"{v.Name} (ГБ/с)" : v.Name;
			Record(slot, total, 0, unit: "МБ/с", stamp: _r.IoAt);   // volumes never alarm

			// Built at most once per tick: the line does not depend on the volume, and the list
			// behind it is refreshed once every twelve seconds. A list nobody has refreshed since
			// the disks went quiet is dropped rather than shown: it used to name the process that
			// was busy hours ago, under every idle volume, with nothing to say how old it was.
			if (!built)
			{
				built = true;
				var top = Fresh(_r.TopIoAt, TopIoEveryMs) ? _r.TopIo : null;
				_topIoLine = top is { Count: > 0 }
					? "\n" + string.Join(" · ", top.Select(t => $"{t.Name} {Mb2(t.Mb)}"))
					: "";
			}
			Show(slot, inGb ? Format.PerThousand(total) : Mb(total), 0,
				$"{Mb2(total)} МБ/с   чтение {Mb2(v.ReadMb)} / запись {Mb2(v.WriteMb)}",
				// Per-process figures, for every device at once and for network and pipes as
				// well as disks — the counter is IO Data Bytes/sec, and calling it "I/O across
				// all devices" under a *volume* icon read as if it were this volume's disk
				// traffic. Splitting it by volume needs a kernel trace costing 5-10 % of a core,
				// which is the whole budget of this program.
				_topIoLine.Length > 0 ? "\nввод-вывод процессов (диск + сеть):" + _topIoLine : "");
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
		if (!Fresh(_r.SpaceAt, SpaceEveryMs)) return;
		var ci = CultureInfo.InvariantCulture;
		foreach (var s in _r.Space)
		{
			var slot = Pooled($"free.{s.Name}", FreeMetric, _freePool, s.Name, $"Свободно {s.Name}");
			if (slot is null) continue;
			var usedPercent = s.TotalGb > 0 ? 100 * (s.TotalGb - s.FreeGb) / s.TotalGb : 0;
			// Percentages alone could not express both volumes on a normal machine: five per cent
			// of a 16 TB array is 800 GB and five per cent of a 120 GB system disk is 6 GB. An
			// optional condition in gigabytes left is folded in on the same scale, and the tooltip
			// says which of the two fired.
			var byGb = GigabyteSeverity(slot, s.FreeGb);
			var severity = Math.Max(usedPercent, byGb);
			Record(slot, s.FreeGb, severity, unit: "ГБ", stamp: _r.SpaceAt);

			// Terabytes past a thousand gigabytes. A RAID volume on a server has five digits of
			// free space, the drawing code knows steps only up to four characters, and the fifth
			// was drawn straight off the edge of a 16-pixel icon. The unit moves into the caption,
			// the way the GPU fan says "(%)" when the driver only gives a duty cycle.
			var inTb = s.FreeGb >= 1000;
			slot.DefaultLabel = inTb ? $"Свободно {s.Name} (ТБ)" : $"Свободно {s.Name}";
			var why = byGb > usedPercent ? "   порог по остатку в ГБ" : "";
			Show(slot, inTb ? (s.FreeGb / 1024).ToString("0.0", ci) : Whole(s.FreeGb),
				severity,
				$"{s.FreeGb.ToString("0.0", ci)} ГБ свободно из {s.TotalGb.ToString("0.0", ci)}   " +
				$"занято {Pct(usedPercent)}{why}");
		}
	}

	/// <summary>
	/// The gigabytes-left condition expressed on the same "percent used" scale the plate is
	/// coloured by, so the two conditions can simply be compared. Zero when it is not configured.
	/// </summary>
	private double GigabyteSeverity(IconSlot slot, double freeGb)
	{
		var warnGb = slot.Settings.WarnGb;
		var critGb = slot.Settings.CritGb;
		if (critGb.HasValue && freeGb <= critGb.Value) return CritOf(slot);
		if (warnGb.HasValue && freeGb <= warnGb.Value) return WarnOf(slot);
		return 0;
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
		if (!Fresh(_r.UpsAt, UpsEveryMs)) return;
		var severity = UpsSeverity(slot, ups);
		// The severity is passed even when there is no charge gauge: an agent that answers
		// "on battery" without a capacity OID is in the state the icon exists for, and the slot
		// used to go grey and drop out of the summary because Record had no number to draw.
		Record(slot, ups.Charge, severity, unit: "%", stamp: _r.UpsAt);

		// Going onto battery is the event a UPS is bought for. Through the same notification
		// policy as everything else: this branch used to call Balloon and the event log directly,
		// so "Уведомлять при переходе в красный" did not switch it off, and one outage could
		// produce both this message and a generic threshold one.
		if (ups.OnBattery.HasValue && ups.OnBattery != _lastOnBattery)
		{
			var left = ups.RunTimeMin.HasValue
				? $", ещё {ups.RunTimeMin.Value.ToString("0", CultureInfo.InvariantCulture)} мин"
				: "";
			// The first answer counts too: _lastOnBattery being empty used to swallow it, so a UPS
			// already running on battery when the program started said nothing.
			var what = ups.OnBattery.Value
				? $"ИБП {(_lastOnBattery.HasValue ? "перешёл на батарею" : "работает от батареи")}. Заряд {Pct(ups.Charge)}{left}."
				: _lastOnBattery.HasValue ? "Питание от сети восстановлено." : null;
			if (what is not null) Notify(slot, what, critical: ups.OnBattery.Value);
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
		// Under five minutes of runtime counts here too. It used to be applied only after this
		// early return, so a UPS reporting three minutes left and no charge gauge was "unknown".
		if (!ups.Charge.HasValue && !ups.Degraded && !ups.NeedsNewBattery && !(ups.RunTimeMin is < 5))
			return null;

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
		Record(slot, battery.Charge, severity, unit: "%");
		var left = battery.MinutesLeft.HasValue
			? $"   ещё {battery.MinutesLeft.Value.ToString("0", CultureInfo.InvariantCulture)} мин"
			: "";
		Show(slot, Whole(battery.Charge), severity,
			$"{Pct(battery.Charge)} {(battery.OnBattery ? "от батареи" : "от сети")}{left}");
	}

	private void ShowUptime()
	{
		if (!_r.UptimeHours.HasValue) return;
		var slot = Slot("uptime", UptimeMetric, UptimeGuid);
		Record(slot, _r.UptimeHours.Value, 0, unit: "ч");   // a long uptime is not an alarm
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

		IconSlot worst = null;
		double top = double.MinValue;
		var scored = 0;
		var silent = 0;
		var worstLevel = Alarm.Normal;
		foreach (var candidate in _order)
		{
			if (ReferenceEquals(candidate, slot)) continue;
			// Not "not dead": Dead is only cleared by Fade, which runs after this, so a source that
			// came back this tick was skipped and the summary could say "тревог нет" about it.
			if (candidate.SeenTick != _tick)
			{
				// Only sources the user called expected count as a gap worth reporting; every
				// machine is missing some technology, and a fresh install must not look broken.
				if (candidate.Settings.Required ?? false) silent++;
				continue;
			}
			// Severity comes from Record, not from Show, so an icon the user has hidden still
			// counts here — the whole point is to watch everything with one slot in the tray.
			if (!candidate.Severity.HasValue) continue;

			var score = Score(candidate);
			if (score is null) continue;
			scored++;
			// The colour follows the worst *level*, not a second hysteresis over the score: the
			// two disagreed, so a CPU whose own plate was still red showed up as yellow here.
			if (candidate.Level > worstLevel) worstLevel = candidate.Level;
			if (score <= top) continue;
			top = score.Value;
			worst = candidate;
		}

		// Nothing measurable at all. Leaving the slot unmarked lets Fade() grey it with a reason,
		// which is the honest answer: "тревог нет" over no data is the failure this program is
		// written to avoid.
		if (scored == 0) return;
		Alive(slot);

		var gaps = silent > 0
			? $"   нет данных: {silent.ToString(CultureInfo.InvariantCulture)}"
			: "";
		if (worst is null || (worstLevel == Alarm.Normal && top < Scale.Yellow))
		{
			Show(slot, silent > 0 ? "?" : "ок", 0,
				(worst is null ? "тревог нет" : $"худшее — {LabelOf(worst)}, {Whole(top)} усл. ед.") + gaps,
				level: worstLevel);
			return;
		}
		Show(slot, Whole(top), top,
			$"{LabelOf(worst)} — {Whole(top)} усл. ед. по общей шкале (78 — жёлтый, 100 — красный)" +
			$"{(top >= 100 ? "   ПОРОГ ПРЕВЫШЕН" : "")}{gaps}",
			level: worstLevel);
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
		if (_slots.TryGetValue(id, out var known))
		{
			// The caption is refreshed, not only set at birth: it carries the unit when a family
			// has to change it (free space in TB, a GPU fan reporting only a duty cycle, the
			// network in gigabits) and the ordinal when a second card appears. An icon created
			// before the change kept the old unit in its label for ever.
			if (label is not null) known.DefaultLabel = label;
			return known;
		}

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
	/// <param name="unit">Unit of <paramref name="value"/>, for the CSV trail.</param>
	/// <param name="stamp">When the reading was measured, for sources published from a background
	/// task. Defaults to now, which is right for everything read on the tick. It also keeps the
	/// five-minute window honest: a cached value re-offered on every tick used to be added to it
	/// three hundred times, so a source polled every ten minutes had a "window" of one number.</param>
	private void Record(IconSlot slot, double? value, double? severity = null, string unit = null,
						long stamp = 0)
	{
		// Alive when there is either a number to draw or a state to colour by. A UPS answering
		// "on battery" without a charge gauge is the case this exists for: the severity was known
		// and alarming, and the slot still went grey and dropped out of the summary icon.
		if (!value.HasValue && !severity.HasValue) return;
		slot.SeenTick = _tick;
		slot.SeenAtMs = Environment.TickCount64;
		slot.Severity = severity ?? value;
		if (unit is not null) slot.LastUnit = unit;
		if (!value.HasValue) return;

		var at = stamp == 0 ? Environment.TickCount64 : stamp;
		slot.LastValue = value;
		slot.MeasuredAt = DateTime.Now;
		if (at == slot.StatsStamp) return;
		slot.StatsStamp = at;
		slot.Stats.Add(value.Value, at);
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
	/// <param name="tail">Part of the tooltip that belongs after the five-minute line, because a
	/// tooltip is cut at 127 characters and this is the part worth losing.</param>
	/// <param name="level">Alert level to use instead of the one this slot's own thresholds would
	/// give. Only the summary icon needs it: its plate has to be exactly the worst plate in the
	/// tray, and deriving it a second time from a non-linear score is how the two came to
	/// disagree.</param>
	private void Show(IconSlot slot, string text, double? severity, string detail, string tail = "",
					  int? level = null)
	{
		// A source that produced nothing this tick belongs to Fade(), which owns the grey plate
		// and the explanation in the tooltip. Letting a caller overwrite that with its own empty
		// formatting is how "—" ends up with no reason next to it.
		if (slot.SeenTick != _tick) return;

		slot.LastText = text;
		slot.LastSeverity = severity;
		slot.LastDetail = detail;
		slot.DetailTail = tail;
		Push(slot, text, severity, level);
	}

	/// <summary>
	/// The whole tooltip. The five-minute line is appended here rather than baked into each
	/// family's detail string: it is recomputed on its own schedule, and a family that only
	/// re-formats when its data version changes used to carry a statistics line up to ten minutes
	/// out of date — or none at all.
	/// </summary>
	private string Tooltip(IconSlot slot) =>
		$"{LabelOf(slot)}   {slot.LastDetail}{(slot.Dead ? "" : slot.StatsText)}{slot.DetailTail}";

	/// <summary>Sends the slot's current state to its tray icon, creating the icon if needed.</summary>
	private void Push(IconSlot slot, string text, double? severity, int? forced = null)
	{
		// One level, for the plate, the journal, the notification and the summary icon alike.
		var level = forced ?? Alarm.LevelOf(slot.Level, severity, WarnOf(slot), CritOf(slot), AlertsOn(slot));
		if (forced.HasValue && !AlertsOn(slot)) level = Alarm.Normal;
		NoteLevelChange(slot, level);
		if (_dry || !slot.Settings.Enabled) return;

		var tooltip = Tooltip(slot);
		if (slot.Icon is null)
		{
			// Everything the icon needs is known here, so it is born finished: one render and one
			// NIM_ADD instead of a grey placeholder followed by a colour, an ink and a value.
			slot.Icon = new TrayValueIcon(OnIconRightClick, OnIconLeftClick, PlateOf(slot), slot.Guid,
				InkOf(slot), text, level, tooltip);
			return;
		}
		slot.Icon.SetInk(InkOf(slot));
		slot.Icon.Update(text, level, tooltip);
	}

	/// <summary>
	/// Records a change of alert colour, and interrupts for the ones that mean hardware is
	/// failing. Nothing in the program could answer "why was it red at three in the morning" —
	/// the five-minute window in the tooltip is five minutes, and the CSV trail is off by default.
	/// A transition is a rare event, so this costs nothing on a tick.
	/// </summary>
	private void NoteLevelChange(IconSlot slot, int level)
	{
		if (level == slot.Level) return;
		var was = slot.Level;
		slot.Level = level;
		if (was == Alarm.Unknown && level <= Alarm.Normal) return;   // nothing happened yet

		var line = $"{DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture)}  {LabelOf(slot)}: " +
				   $"{Alarm.Name(was)} → {Alarm.Name(level)}" +
				   (slot.LastText is null ? "" : $" ({slot.LastText})");
		_journal.Enqueue(line);
		while (_journal.Count > 50) _journal.Dequeue();
		if (_dry) return;

		// Losing a source the user marked as expected is an event in its own right: a grey plate
		// is easy to miss, and one summary icon can look perfectly healthy while the thing it was
		// summarising has stopped answering.
		if (level == Alarm.Dead && was >= Alarm.Normal && (slot.Settings.Required ?? false))
		{
			Notify(slot, $"{LabelOf(slot)}: данные пропали — {slot.LastDetail}", critical: false);
			return;
		}
		if (level == Alarm.Dead) return;
		if (was == Alarm.Dead && level >= Alarm.Normal && (slot.Settings.Required ?? false) && level < Alarm.Critical)
		{
			Notify(slot, $"{LabelOf(slot)}: данные снова есть — {slot.LastDetail}", critical: false);
			return;
		}

		// Into the red, from anywhere that is not the red — including from "nothing was known yet".
		// The old guard dropped every transition out of -1, which meant a RAID array already
		// failing when the program started, a volume already full, or a source that came back
		// straight into the red said nothing at all.
		if (level != Alarm.Critical || was == Alarm.Critical) return;
		Notify(slot, $"{LabelOf(slot)}: {slot.LastDetail}", critical: true);
	}

	/// <summary>
	/// One balloon and one event-log line per transition, through one policy. The UPS used to have
	/// a second path of its own that ignored the "Уведомлять при переходе в красный" setting
	/// entirely, and could produce two messages for one event.
	/// </summary>
	private void Notify(IconSlot slot, string what, bool critical)
	{
		if (_dry) return;
		if (!(slot.Settings.NotifyOnCritical ?? slot.Metric.NotifyByDefault)) return;
		if (slot.NotifiedTick == _tick) return;
		slot.NotifiedTick = _tick;
		Balloon("TrayMon", what, warning: critical);
		// And into the Application log, where a server's existing monitoring will find it: a
		// balloon needs somebody in front of the screen, and these are exactly the events for
		// which there is nobody.
		WindowsLog.Write("TrayMon: " + what.Replace("\n", " · "), critical);
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
		var now = Environment.TickCount64;
		var ci = CultureInfo.InvariantCulture;
		foreach (var slot in _order)
		{
			// A real time window now, over readings that carry their own timestamps: the ring of
			// 150 numbers was five minutes only at the built-in tick rate, and the label said
			// "5 мин" whatever the tick and however long the session had been locked.
			var window = slot.Stats.Window(now);
			slot.StatsText = window is null
				? ""
				// Kept short on purpose: a tray tooltip is 127 characters and no more, and a network
				// icon's own line already takes a hundred of them. A verbose form was simply cut off.
				: $"\n5 мин: {window.Value.Min.ToString(Digits(window.Value.Max), ci)}…" +
				  $"{window.Value.Max.ToString(Digits(window.Value.Max), ci)}, " +
				  $"ср {window.Value.Avg.ToString(Digits(window.Value.Max), ci)}";

			// Pushed now rather than at the next reformat. A family with a version — disks, RAID,
			// the UPS — only rebuilds its detail when the data changes, so its statistics line was
			// up to ten minutes behind the one it claimed to show.
			if (slot.Icon is not null) slot.Icon.Update(slot.LastText, slot.Level, Tooltip(slot));
		}
	}

	private static string Digits(double max) => max < 10 ? "0.0" : "0";

	private void Housekeeping()
	{
		if (_firstRun && !_greeted && _tick >= 2 && !_dry)
		{
			_greeted = true;
			// Information, not a warning triangle: "TrayMon is running" is not a problem.
			Balloon("TrayMon работает",
				"Значки в области уведомлений. Правой кнопкой по любому из них — меню: " +
				"остальные метрики, цвета, автозапуск.",
				warning: false);
		}

		// The pools hand out a tray identity the first time they see a device, and that assignment
		// has to outlive the process or the next start gives it to somebody else.
		if (_config.TakeDirty()) _configDirty = true;

		// Written on a schedule, not on every tick: a settings file is the one thing here that
		// touches the disk regularly. The flag is cleared only on success — clearing it regardless
		// meant a single failed write threw away the automatic first-run choices for good.
		if (!_configDirty || _dry || _tick % 15 != 0) return;
		if (_config.Save(out var error)) { _configDirty = false; _firstRun = false; return; }
		_lastTickError = "настройки не сохранены: " + error;
	}

	/// <summary>
	/// Optional CSV trail. Off by default and never faster than every 30 s — at tick rate this
	/// would cost as much as the sensors it records.
	/// </summary>
	/// <summary>The CSV header, and the contract with whoever reads the file back.</summary>
	private const string LogHeader = "time;id;value;unit;severity;status;measured_at";

	private void WriteLogLine()
	{
		if (_dry || !_config.Log.Enabled) return;
		// Monotonic, not DateTime.Now: a clock stepped backwards — which is what a machine does
		// after an NTP correction or a resume — stopped the log for however long the step was.
		var now = Environment.TickCount64;
		if (now - _lastLogAt < _config.Log.EverySeconds * 1000L) return;
		_lastLogAt = now;

		var ci = CultureInfo.InvariantCulture;
		var stamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:sszzz", ci);
		// One row per slot rather than nine fixed columns. The fixed set left out disk
		// temperatures, fans, volumes and top-io — half of what "why was the server slow last
		// night" actually needs — and a long format needs no header change when a machine has
		// a different set of icons from the one the columns were written for.
		//
		// The raw number and its unit, not the text from the plate: that text changes unit under
		// its own feet — gigabytes to terabytes, hours to days, rpm to per cent — so one column of
		// one id could not be read back as a series at all. Ids go through the quoting rules,
		// because a device description can contain the separator.
		var lines = new StringBuilder();
		foreach (var slot in _order)
		{
			if (!slot.Severity.HasValue && !slot.LastValue.HasValue) continue;
			var status = slot.Dead ? "dead" : slot.SeenTick == _tick ? "ok" : "stale";
			lines.Append(stamp).Append(';').Append(Csv.Field(slot.Id)).Append(';')
				 .Append(slot.LastValue.HasValue ? slot.LastValue.Value.ToString("0.###", ci) : "").Append(';')
				 .Append(Csv.Field(slot.LastUnit)).Append(';')
				 .Append(slot.Severity.HasValue ? slot.Severity.Value.ToString("0.0", ci) : "").Append(';')
				 .Append(status).Append(';')
				 .Append(slot.MeasuredAt == default ? "" : slot.MeasuredAt.ToString("yyyy-MM-dd HH:mm:sszzz", ci))
				 .Append('\n');
		}
		if (lines.Length == 0) return;

		// Off the UI thread. File.AppendAllText on a log redirected to a network share, or on a
		// disk a scanner is busy with, blocked the timer — and with it every metric, the menu and
		// the delivery of events — for as long as the write took. The queue is bounded by one
		// batch in flight: a writer that cannot keep up drops rows and says how many.
		lock (_logQueue)
		{
			if (_logQueue.Count >= 4) { _logDropped++; return; }
			_logQueue.Enqueue(lines.ToString());
		}
		if (Interlocked.Exchange(ref _logWriting, 1) == 1) return;
		Spawn(FlushLog);
	}

	private void FlushLog()
	{
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

			while (true)
			{
				string batch;
				lock (_logQueue)
				{
					if (_logQueue.Count == 0) return;
					batch = _logQueue.Dequeue();
				}
				Roll(path);
				if (!File.Exists(path)) File.AppendAllText(path, LogHeader + "\n");
				else if (!_logHeaderChecked)
				{
					_logHeaderChecked = true;
					// An older file has different columns. Appending to it would produce a table
					// nothing can parse, so it is set aside once and a fresh one started.
					using var reader = new StreamReader(path);
					if (reader.ReadLine() != LogHeader)
					{
						reader.Dispose();
						File.Move(path, path + ".old", overwrite: true);
						File.AppendAllText(path, LogHeader + "\n");
					}
				}
				File.AppendAllText(path, batch);
			}
		}
		catch (Exception ex)
		{
			// A full or read-only disk must not take the monitor down with it.
			_lastTickError = "журнал: " + ex.Message;
		}
		finally { Volatile.Write(ref _logWriting, 0); }
	}

	/// <summary>Rolls the trail over once it reaches its limit. A monitor that fills the disk it
	/// is watching is a special kind of useless.</summary>
	private void Roll(string path)
	{
		try
		{
			var info = new FileInfo(path);
			if (!info.Exists || info.Length < _config.Log.MaxMb * 1024L * 1024) return;
			File.Move(path, path + ".1", overwrite: true);
		}
		catch (Exception) { /* busy or read-only: the write below will report it */ }
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
		// The dry run does the work inline, in order, on this thread. It used to skip it entirely
		// and read the sources itself instead, which meant --once --icons exercised a path the
		// running program never takes: the versions the families check were not bumped, so the
		// second and third tick kept redisplaying the first reading.
		if (_dry) { work(); return; }
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

	// Every one of these publishes a snapshot *and* the moment it was measured, and clears the data
	// when it fails. A reader that throws used to leave the previous numbers in place with nothing
	// to say how old they were, which is the one failure this program is written to avoid.

	private void RefreshSlow()
	{
		if (Interlocked.Exchange(ref _slowRefreshRunning, 1) == 1) return;
		try
		{
			_r.Slow = _lhm.ReadSlow();
			Volatile.Write(ref _r.SlowAt, Environment.TickCount64);
		}
		catch (Exception ex) { _r.Slow = SlowReading.Empty; Trouble(ex); }
		finally { Volatile.Write(ref _slowRefreshRunning, 0); }
	}

	private void RefreshDisks()
	{
		if (Interlocked.Exchange(ref _diskRefreshRunning, 1) == 1) return;
		try
		{
			_r.Disks = Program.DiskTemps(_disk, _lhm);
			Volatile.Write(ref _r.DisksAt, Environment.TickCount64);
			Interlocked.Increment(ref _disksVersion);
		}
		catch (Exception ex)
		{
			_r.Disks = new List<DiskReading>();
			Interlocked.Increment(ref _disksVersion);
			Trouble(ex);
		}
		finally { Volatile.Write(ref _diskRefreshRunning, 0); }
	}

	private void RefreshTopIo()
	{
		if (Interlocked.Exchange(ref _topIoRefreshRunning, 1) == 1) return;
		try
		{
			// A gap longer than two periods makes the sample a baseline instead of a reading.
			_r.TopIo = _perf.TopIoProcesses(3, TopIoEveryMs * 2);
			Volatile.Write(ref _r.TopIoAt, Environment.TickCount64);
		}
		catch (Exception ex) { _r.TopIo = new List<(string, double)>(); Trouble(ex); }
		finally { Volatile.Write(ref _topIoRefreshRunning, 0); }
	}

	private void RefreshSpace(string[] volumes)
	{
		if (Interlocked.Exchange(ref _spaceRefreshRunning, 1) == 1) return;
		try
		{
			_r.Space = SpaceSensor.Read(volumes);
			Volatile.Write(ref _r.SpaceAt, Environment.TickCount64);
		}
		catch (Exception ex) { _r.Space = new List<(string, double, double)>(); Trouble(ex); }
		finally { Volatile.Write(ref _spaceRefreshRunning, 0); }
	}

	private void RefreshRaidDisk()
	{
		if (Interlocked.Exchange(ref _raidRefreshRunning, 1) == 1) return;
		try
		{
			_r.RaidDisks = _hdd.ReadAll();
			Volatile.Write(ref _r.RaidAt, Environment.TickCount64);
			Interlocked.Increment(ref _raidVersion);
			Backoff(ref _raidFailures, ref _raidRetryAt, _r.RaidDisks.Count > 0);
		}
		catch (Exception ex)
		{
			_r.RaidDisks = new List<RaidDisk>();
			Interlocked.Increment(ref _raidVersion);
			Trouble(ex);
		}
		finally { Volatile.Write(ref _raidRefreshRunning, 0); }
	}

	private void RefreshUps()
	{
		if (Interlocked.Exchange(ref _upsRefreshRunning, 1) == 1) return;
		try
		{
			_ups.Read(_r);
			Volatile.Write(ref _r.UpsAt, Environment.TickCount64);
			Interlocked.Increment(ref _upsVersion);
			Backoff(ref _upsFailures, ref _upsRetryAt, _r.Ups.Answered);
		}
		catch (Exception ex)
		{
			_r.Ups = UpsReading.Silent;
			Interlocked.Increment(ref _upsVersion);
			Trouble(ex);
		}
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
	/// <remarks>
	/// The renderer is no longer told a pair of thresholds at all — it is told a level, and this
	/// flag is one of the things that decides it. Passing Warn = Crit = 1e9 instead was not enough:
	/// a failing disk carries a severity of exactly 1e9, so <c>s >= crit</c> painted it red with
	/// the highlight switched off, while the journal and the summary icon agreed it was normal.
	/// </remarks>
	private bool AlertsOn(IconSlot slot) =>
		(slot.Settings.Alerts ?? true) && WarnOf(slot) < NeverAlerts;

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

	/// <summary>
	/// Writes the settings, keeping whatever was edited on disk in the meantime.
	///
	/// The file is serialised whole, so a menu click between somebody's save in an editor and the
	/// next debounced reload used to write the in-memory object over their edit and stamp it as
	/// current. The one setting that was just changed here is carried onto the fresh file instead.
	/// </summary>
	private void Persist(IconSlot slot)
	{
		_config.Tidy(slot.Id);
		if (_config.ChangedOnDisk)
		{
			var fresh = Config.Read();
			if (fresh.LoadError is null)
			{
				var mine = _config.Icons.TryGetValue(slot.Id, out var entry) ? entry : null;
				// Slot assignments are ours, never edited by hand, and must not be lost to a reload.
				foreach (var (pool, table) in _config.Slots) fresh.Slots[pool] = table;
				if (mine is null) fresh.Icons.Remove(slot.Id);
				else fresh.Icons[slot.Id] = mine;
				_config = fresh;
				// And the icons follow it: a colour or a caption edited in the file otherwise sat
				// in memory unapplied, because the save below makes the file look already read.
				ApplySettings();
			}
		}
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

		// The frequent things at the top, everything about the installation in a submenu of its
		// own: colour, caption, thresholds and notifications all belong to the icon that was
		// clicked, while autostart, the shortcut, the settings file and removal do not, and mixing
		// the two in one long list is how somebody reaches "Удалить TrayMon…" looking for a colour.
		if (clicked is not null)
		{
			_menu.Items.Add(new ToolStripMenuItem($"{LabelOf(clicked)}: {clicked.LastText ?? "—"}") { Enabled = false });
			_menu.Items.Add(new ToolStripSeparator());
			_menu.Items.Add(MetricMenu(clicked));
			_menu.Items.Add("Скрыть этот значок", null, (_, _) => SetEnabled(clicked, false));
		}

		_menu.Items.Add(VisibilityMenu());
		_menu.Items.Add("Сводка…", null, (_, _) => ShowSummary());
		_menu.Items.Add("Опросить датчики сейчас", null, (_, _) => PollNow());
		_menu.Items.Add(new ToolStripSeparator());
		_menu.Items.Add(ProgramMenu());
		_menu.Items.Add(new ToolStripSeparator());
		_menu.Items.Add("Выход", null, (_, _) => ExitThread());

		icon.PrepareMenu();
		_menu.Show(Cursor.Position);
	}

	/// <summary>Everything that belongs to one icon.</summary>
	private ToolStripMenuItem MetricMenu(IconSlot clicked)
	{
		var menu = new ToolStripMenuItem("Настроить этот значок");
		menu.DropDownItems.Add("Цвет фона…", null, (_, _) => PickColor(clicked));
		menu.DropDownItems.Add(InkMenu(clicked));
		menu.DropDownItems.Add("Переименовать…", null, (_, _) => Rename(clicked));
		// Not offered where there is nothing to set: a fan is judged on standing still, and
		// a metric that never alarms has no thresholds to type into. The dialog used to
		// accept "600 rpm" into a scale that only ever holds 0 and 100, which quietly turned
		// the standstill alarm off while its checkbox still said it was on.
		if (!clicked.Metric.StallAlarm && clicked.Metric.Warn < NeverAlerts)
			menu.DropDownItems.Add("Пороги…", null, (_, _) => EditThresholds(clicked));
		if (clicked.Id.StartsWith("free.", StringComparison.Ordinal))
			menu.DropDownItems.Add("Порог по остатку в ГБ…", null, (_, _) => EditGbThresholds(clicked));
		if (clicked.Metric.StallAlarm) menu.DropDownItems.Add(StallMenu(clicked));
		if (clicked.Metric.Warn < NeverAlerts && !clicked.Metric.StallAlarm)
		{
			var alerts = new ToolStripMenuItem("Подсвечивать при перегрузке")
			{
				Checked = AlertsOn(clicked),
				CheckOnClick = true,
				ToolTipText = ThresholdHint(clicked),
			};
			alerts.Click += (_, _) => SetAlerts(clicked, alerts.Checked);
			menu.DropDownItems.Add(alerts);
		}
		if (clicked.Metric.Warn < NeverAlerts)
		{
			var notify = new ToolStripMenuItem("Уведомлять при переходе в красный")
			{
				Checked = clicked.Settings.NotifyOnCritical ?? clicked.Metric.NotifyByDefault,
				CheckOnClick = true,
				ToolTipText = "Всплывающее уведомление один раз на переход, а не на каждый тик",
			};
			notify.Click += (_, _) => SetNotify(clicked, notify.Checked);
			menu.DropDownItems.Add(notify);
		}
		var required = new ToolStripMenuItem("Источник обязателен")
		{
			Checked = clicked.Settings.Required ?? false,
			CheckOnClick = true,
			ToolTipText = "Сообщать о пропаже и возврате данных и считать их в сводном значке",
		};
		required.Click += (_, _) => SetRequired(clicked, required.Checked);
		menu.DropDownItems.Add(required);
		return menu;
	}

	/// <summary>Everything that belongs to the installation rather than to an icon.</summary>
	private ToolStripMenuItem ProgramMenu()
	{
		var menu = new ToolStripMenuItem("Программа");
		var state = Autostart.State(out var command, out var note);
		var autostart = new ToolStripMenuItem("Запускать при входе в Windows")
		{
			Checked = state == Autostart.TaskState.Ours,
			CheckOnClick = true,
			ToolTipText = state switch
			{
				Autostart.TaskState.Foreign => $"Задача уже есть и запускает {command ?? "другую программу"}",
				Autostart.TaskState.Disabled => "Задача есть, но не запустится: " + note,
				Autostart.TaskState.Broken => "Задача есть, но в ней нет действия",
				Autostart.TaskState.Unreadable => "Состояние задачи прочитать не удалось: " + note,
				_ => "Задача планировщика с правами администратора — без неё не читается температура CPU",
			},
		};
		// The tick is only moved by what actually happened, and the state is read back afterwards:
		// CheckOnClick puts it there before anything is asked of us.
		autostart.Click += (_, _) => autostart.Checked = ToggleAutostart(autostart.Checked);
		menu.DropDownItems.Add(autostart);
		menu.DropDownItems.Add("Создать ярлык на рабочем столе", null, (_, _) => CreateShortcut());
		menu.DropDownItems.Add(new ToolStripSeparator());
		menu.DropDownItems.Add("Настройки в файле…", null, (_, _) => OpenConfigFile());
		menu.DropDownItems.Add("Перечитать настройки", null, (_, _) => ReloadConfig());
		menu.DropDownItems.Add(new ToolStripSeparator());
		menu.DropDownItems.Add("Диагностика…", null, (_, _) => ShowDiagnostics());
		menu.DropDownItems.Add("О программе…", null, (_, _) => ShowAbout());
		menu.DropDownItems.Add(new ToolStripSeparator());
		menu.DropDownItems.Add("Удалить TrayMon…", null, (_, _) => Uninstall());
		return menu;
	}

	/// <summary>
	/// The gigabytes-left condition for a free-space icon. Percentages alone cannot express both a
	/// 16 TB array and a 120 GB system volume: five per cent of one is 800 GB and of the other 6.
	/// </summary>
	private void EditGbThresholds(IconSlot slot)
	{
		var ci = CultureInfo.InvariantCulture;
		var warn = Prompt("Жёлтый порог: осталось меньше, ГБ\nПусто — условие не применяется",
			slot.Settings.WarnGb?.ToString("0.###", ci) ?? "");
		if (warn is null) return;
		var crit = Prompt("Красный порог: осталось меньше, ГБ\nПусто — условие не применяется",
			slot.Settings.CritGb?.ToString("0.###", ci) ?? "");
		if (crit is null) return;

		double? w = null, c = null;
		if (warn.Length > 0)
		{
			if (!double.TryParse(warn, NumberStyles.Float, ci, out var value) || !Config.Sane(value))
			{
				Info("Порог должен быть конечным неотрицательным числом.", MessageBoxIcon.Warning);
				return;
			}
			w = value;
		}
		if (crit.Length > 0)
		{
			if (!double.TryParse(crit, NumberStyles.Float, ci, out var value) || !Config.Sane(value))
			{
				Info("Порог должен быть конечным неотрицательным числом.", MessageBoxIcon.Warning);
				return;
			}
			c = value;
		}
		if (w.HasValue && c.HasValue && c >= w)
		{
			Info("Красный порог должен быть меньше жёлтого: это остаток места.\n\n" +
				 "Прежние значения оставлены без изменений.", MessageBoxIcon.Warning);
			return;
		}
		var settings = Own(slot);
		settings.WarnGb = w;
		settings.CritGb = c;
		Persist(slot);
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
			return slot.Settings.Stall switch
			{
				"zero-ok" => "Красная, если вентилятор стоит при ненулевом задании",
				"off" => "Тревога по остановке выключена",
				_ => "Красная, когда вентилятор остановлен",
			};
		// The UPS and a laptop battery are not the same promise, and the shared wording claimed the
		// laptop one turns red the moment the mains is unplugged. It does not: below the mains its
		// severity is zero, and the charge thresholds only apply while it is discharging.
		if (slot.Metric.Inverted)
			return $"Жёлтая при заряде ниже {(100 - warn).ToString("0", ci)} %, " +
				   $"красная ниже {(100 - crit).ToString("0", ci)} %" +
				   (slot.Id == "ups"
					   ? " и сразу при переходе ИБП на батарею"
					   : "; при питании от сети подсветки нет");
		if (slot.Id.StartsWith("free.", StringComparison.Ordinal))
			return $"Жёлтая когда занято больше {warn.ToString("0", ci)} %, красная больше {crit.ToString("0", ci)} %" +
				   (slot.Settings.WarnGb.HasValue || slot.Settings.CritGb.HasValue
					   ? " либо по остатку в ГБ"
					   : "");
		if (slot.Id == "worst")
			return "Повторяет самый тревожный из остальных значков: 78 — жёлтый порог любой метрики, 100 — её красный";
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
		Push(slot, slot.LastText, slot.LastSeverity);
	}

	/// <summary>
	/// What a stopped fan means for this icon. Every zero used to be red, which is right for a CPU
	/// cooler and wrong for the case fans that switch themselves off on an idle desktop — and a
	/// permanent alarm is not an alarm.
	/// </summary>
	private ToolStripMenuItem StallMenu(IconSlot slot)
	{
		var menu = new ToolStripMenuItem("При остановке");
		var current = slot.Settings.Stall;
		var options = new[]
		{
			("Тревога при любой остановке", (string)null, "Как было: ноль оборотов — красная плашка"),
			("Штатный zero-RPM разрешён", "zero-ok", "Красная, только если вентилятор не крутится при ненулевом задании"),
			("Только наблюдение", "off", "Обороты видны, тревоги нет"),
		};
		foreach (var (label, value, hint) in options)
		{
			var choice = value;
			var item = new ToolStripMenuItem(label) { Checked = current == choice, ToolTipText = hint };
			item.Click += (_, _) =>
			{
				Own(slot).Stall = choice;
				Persist(slot);
				Push(slot, slot.LastText, slot.LastSeverity);
			};
			menu.DropDownItems.Add(item);
		}
		return menu;
	}

	/// <summary>
	/// Marks a source as one this machine is expected to have, so losing it is reported rather than
	/// left as a grey plate nobody notices, and counted by the summary icon.
	/// </summary>
	private void SetRequired(IconSlot slot, bool required)
	{
		Own(slot).Required = required ? true : null;
		Persist(slot);
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
			!double.TryParse(crit, NumberStyles.Float, ci, out var c) ||
			// TryParse accepts NaN and Infinity, and overflows a long decimal to Infinity. Both
			// compare false against everything, which switches the alarm off without saying so.
			!double.IsFinite(w) || !double.IsFinite(c))
		{
			Info("Порог должен быть конечным числом; точка как разделитель дробной части.", MessageBoxIcon.Warning);
			return;
		}

		// Validated before anything is changed. The pair used to be written into the live settings
		// first and then reset to null on a bad combination — so a mistyped threshold discarded
		// whatever had been configured before it, and the renderer ran on the broken pair until the
		// next update.
		double warnStore = Store(w), critStore = Store(c);
		if (warnStore >= critStore || !Config.Sane(warnStore) || !Config.Sane(critStore))
		{
			Info(inverted
				? "Красный порог должен быть ниже жёлтого: тревога здесь — это низкий заряд.\n\n" +
				  "Прежние пороги оставлены без изменений."
				: "Красный порог должен быть выше жёлтого.\n\nПрежние пороги оставлены без изменений.",
				MessageBoxIcon.Warning);
			return;
		}

		var settings = Own(slot);
		settings.Warn = warnStore;
		settings.Crit = critStore;
		Persist(slot);
		Push(slot, slot.LastText, slot.LastSeverity);
	}

	private void Rename(IconSlot slot)
	{
		var name = Prompt("Подпись значка", LabelOf(slot));
		if (name is null) return;
		Own(slot).Label = string.IsNullOrWhiteSpace(name) ? null : name;   // empty restores the default
		Persist(slot);
		slot.Icon?.Update(slot.LastText, slot.Level, Tooltip(slot));
	}

	/// <summary>
	/// Whether this slot is something the menu can be opened from. "Enabled" is not enough —
	/// an enabled slot that never produced a reading had no icon at all — and "not dead" is
	/// wrong in the other direction, since a dead slot does keep a grey, clickable icon. What
	/// carries the menu is an icon that exists, so that is what both checks ask about.
	/// </summary>
	/// <remarks>
	/// "An icon object exists" is not enough either: a busy or refusing shell leaves the object in
	/// place without ever accepting it, and there is no menu on something the tray never showed.
	/// </remarks>
	private static bool CarriesMenu(IconSlot slot) =>
		slot.Settings.Enabled && slot.Icon is not null && slot.Icon.Registered;

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
		// And the discovered device list and the dropped SNMP varbinds. This item exists for the
		// moment right after the hardware or the agent changed, which is exactly when a cached
		// device list is wrong and an OID dropped as noSuchName may be there after all.
		_hdd.Rediscover();
		_ups.Rediscover();
		Spawn(RefreshSlow);
		Spawn(RefreshDisks);
		Spawn(RefreshRaidDisk);
		Spawn(RefreshUps);
		Spawn(RefreshTopIo);
		var names = _r.Volumes.Select(v => v.Name).ToArray();
		Spawn(() => RefreshSpace(names));
		Info("Опрос запущен. Медленные источники (SMART, RAID, ИБП) ответят в течение нескольких секунд.");
	}

	/// <returns>Whether autostart is enabled for this installation *after* the attempt — the menu
	/// tick follows the scheduler, not the click.</returns>
	private bool ToggleAutostart(bool on)
	{
		if (!on)
		{
			Info(Autostart.Disable(evenIfForeign: false, out var offError)
				? "Автозапуск отключён."
				: $"Не получилось: {offError}", MessageBoxIcon.Information);
			return Autostart.IsEnabled;
		}

		// A task under the shared name that starts another file belongs to another installation,
		// and taking it over disables that one. Asked, not done silently.
		var replace = false;
		if (Autostart.State(out var command, out _) == Autostart.TaskState.Foreign)
		{
			var answer = MessageBox.Show(
				$"Задача «TrayMon» уже есть и запускает:\n{command}\n\n" +
				"Это другая установка TrayMon или другая учётная запись. Перезаписать её?",
				"TrayMon — автозапуск", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
			if (answer != DialogResult.Yes) return Autostart.IsEnabled;
			replace = true;
		}

		if (Autostart.Enable(replace, out var error))
		{
			Info("TrayMon будет запускаться при входе в Windows.");
			return Autostart.IsEnabled;
		}
		Info($"Не получилось: {error}", MessageBoxIcon.Warning);
		return Autostart.IsEnabled;
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
			// What it actually does now. The old text said the file is read only at startup, which
			// stopped being true when the watcher was added — and "через секунду" is not the promise
			// either: the reload happens on the first tick after a second of quiet, which at a
			// raised TickMs or a locked session is tens of seconds later.
			Info("Правка подхватывается сама — на первом опросе после того, как файл перестанут\n" +
				 "менять (при обычном периоде это несколько секунд, при увеличенном — дольше).\n" +
				 "«Перечитать настройки» делает это немедленно.\n\n" +
				 "Адрес ИБП, путь к smartctl, фильтр адаптеров, период опроса и профиль датчиков\n" +
				 "применяются только при перезапуске программы.");
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
		// Read, not Load: the recovery path renames the file it cannot parse and that is not the
		// watcher's business. An editor writes a file in more than one step, so an intermediate
		// state is normal here — taking somebody's settings file away from them over one is not.
		var fresh = Config.Read();
		if (fresh.LoadError is not null)
		{
			// Silently ignoring it used to leave the program on the old object with nothing said,
			// so a file that had been broken for an hour looked like one that was being applied.
			_lastTickError = "настройки не перечитаны: " + fresh.LoadError;
			if (!silent) Info($"Файл не прочитан: {fresh.LoadError}", MessageBoxIcon.Warning);
			return;
		}
		foreach (var (pool, table) in _config.Slots)
			if (!fresh.Slots.ContainsKey(pool)) fresh.Slots[pool] = table;
		_config = fresh;
		ApplySettings();
		EnsureSomethingVisible();
		if (silent) return;
		Info("Настройки перечитаны.\n\nАдрес ИБП, путь к smartctl, фильтр адаптеров и период опроса " +
			 "применятся после перезапуска программы — они читаются один раз при старте.");
	}

	/// <summary>Hands the settings now in <c>_config</c> to every slot and every live icon.</summary>
	private void ApplySettings()
	{
		foreach (var slot in _order)
		{
			slot.Settings = _config.Get(slot.Id);
			if (!slot.Settings.Enabled)
			{
				if (slot.Icon is not null) { slot.Icon.Dispose(); slot.Icon = null; }
				continue;
			}
			slot.Icon?.SetPlate(PlateOf(slot));
			slot.Icon?.SetInk(InkOf(slot));
			Push(slot, slot.LastText, slot.LastSeverity);
		}
	}

	/// <summary>
	/// The "you cannot hide them all" rule, applied to the file as well as to the menu. Setting
	/// every icon to false by hand is a legitimate edit of a file the README invites people to
	/// edit, and it left a process with no icons, no menu and no way out but Task Manager.
	/// </summary>
	private void EnsureSomethingVisible()
	{
		if (_order.Count == 0) return;
		// Enabled, deliberately — not the CarriesMenu predicate the menu uses. This runs on every
		// tick now (a pooled slot can be the last carrier and its device can be unplugged), and a
		// shell that is merely busy refuses every add and retries on its own: asking about
		// registration here would switch extra icons on and raise a dialog every two seconds
		// during a logon, which is the failure mode the whole program avoids elsewhere.
		if (_order.Any(s => s.Settings.Enabled)) return;

		var first = _order.FirstOrDefault(s => s.Icon is not null) ?? _order[0];
		Own(first).Enabled = true;
		if (_config.Save(out var error)) _configDirty = false;
		else _lastTickError = "настройки не сохранены: " + error;
		first.Settings = _config.Get(first.Id);
		Push(first, first.LastText, first.LastSeverity);
		// Said once per occurrence, not once per tick.
		if (_ensuredVisible) return;
		_ensuredVisible = true;
		Later("В настройках не осталось ни одного значка, на котором живёт меню.\n" +
			  $"Включён «{LabelOf(first)}».");
	}

	private bool _ensuredVisible;

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
				text.AppendLine("По остроте состояния (усл. ед.: 78 — жёлтый порог метрики, 100 — красный)");
				foreach (var slot in _order.OrderByDescending(s => Score(s) ?? -1))
					text.AppendLine($"    {Rank(slot),4}  {State(slot)}  {LabelOf(slot),-32} " +
									$"{(slot.LastText ?? "—"),6} {slot.LastUnit,-7} {Detail(slot)}");
			}
			else
			{
				foreach (var group in Groups)
				{
					var members = _order.Where(s => s.Metric.Group == group).OrderBy(s => s.Metric.Order).ToList();
					if (members.Count == 0) continue;
					text.AppendLine(group);
					foreach (var slot in members)
						text.AppendLine($"    {State(slot)}  {LabelOf(slot),-32} {(slot.LastText ?? "—"),6} " +
										$"{slot.LastUnit,-7} {Detail(slot)}");
					text.AppendLine();
				}
			}
			text.AppendLine();
			var dead = _order.Count(s => s.Dead);
			text.AppendLine($"тик {_tick.ToString(ci)}, без данных: {dead.ToString(ci)}, " +
							$"ошибок за сеанс: {_tickErrors.ToString(ci)}");
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
	/// The state of a slot in a form that survives being printed without colour — the summary
	/// window is text, and "red" is not a word it can use.
	/// </summary>
	private static string State(IconSlot slot)
	{
		var name = slot.Level switch
		{
			Alarm.Critical => "КРИТ",
			Alarm.Warning => "предупр",
			Alarm.Normal => "норма",
			Alarm.Dead => "нет данных",
			_ => "—",
		};
		return name.PadRight(10);
	}

	private static string Detail(IconSlot slot)
	{
		var measured = slot.MeasuredAt == default
			? ""
			: $"   [{slot.MeasuredAt.ToString("HH:mm:ss", CultureInfo.InvariantCulture)}]";
		return slot.LastDetail.Replace("\n", " · ") + measured;
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
		text.AppendLine($"драйвер датчиков: {(_lhm.Disabled ? "ВЫКЛЮЧЕН настройкой — " + _lhm.LastError : !_lhm.Available ? "НЕДОСТУПЕН — " + (_lhm.LastError ?? "нужен запуск от администратора") : _lhm.DriverBlocked ? "ЗАБЛОКИРОВАН — " + _lhm.LastError : IsElevated ? "загружен" : "загружен, но без прав администратора температуры не читаются")}");
		if (_lhm.Ring0Note is not null) text.AppendLine($"                  {_lhm.Ring0Note}");
		text.AppendLine($"температуры дисков: {(_disk.LastError is null ? "через драйвер накопителей" : "нет — " + _disk.LastError)}");
		text.AppendLine($"NVML (GPU):       {(_gpu.CardCount > 0 ? $"карт: {_gpu.CardCount}" : "нет — " + (_gpu.LastError ?? "драйвер NVIDIA не найден"))}");
		text.AppendLine($"smartctl:         {(_hdd.Available ? _hdd.ExePath : "не найден: " + _hdd.ExePath)}");
		if (_hdd.LastError is not null) text.AppendLine($"                  {_hdd.LastError}");
		if (_hdd.Note is not null) text.AppendLine($"                  {_hdd.Note}");
		text.AppendLine($"ИБП:              {(_ups.Present ? "отвечает" : "нет ответа")} на {_ups.Endpoint}" +
						$"{(_ups.LastError is null ? "" : " — " + _ups.LastError)}");
		text.AppendLine();
		// When each background source last finished, and when it will be asked again. "Загружен" and
		// "отвечает" say nothing about whether the number on the screen was measured a second or an
		// hour ago, and that is the question a grey-looking tray actually raises.
		text.AppendLine("фоновые источники (последнее измерение → следующая попытка):");
		Age(text, "датчики, вентиляторы", _r.SlowAt, _slowAt, SlowEveryMs);
		Age(text, "процессы по в/в", _r.TopIoAt, _topIoAt, TopIoEveryMs);
		Age(text, "температуры дисков", _r.DisksAt, _diskAt, DiskEveryMs);
		Age(text, "свободное место", _r.SpaceAt, _spaceAt, SpaceEveryMs);
		Age(text, "диски за RAID", _r.RaidAt, Math.Max(_raidAt, _raidRetryAt), RaidEveryMs);
		Age(text, "ИБП", _r.UpsAt, Math.Max(_upsAt, _upsRetryAt), UpsEveryMs);
		text.AppendLine();
		text.AppendLine($"файл настроек:    {Config.Path}");
		if (_config.LoadError is not null) text.AppendLine($"                  {_config.LoadError}");
		if (_config.LoadNote is not null) text.AppendLine($"                  {_config.LoadNote}");
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
		// Registered, not merely created: a shell that refuses or times out leaves an icon object
		// behind that shows nothing, and "показано 17" was the same number either way.
		text.AppendLine($"значков:          включено {_order.Count(s => s.Settings.Enabled).ToString(ci)} из {_order.Count.ToString(ci)}, " +
						$"принято оболочкой {_order.Count(s => s.Icon is not null && s.Icon.Registered).ToString(ci)}");
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
		// Duration, not CPU, and said so. Both are Stopwatch readings: the tick includes whatever
		// the thread was preempted for, and the pool figure includes waiting on a UDP datagram, on
		// an external process and on the sensor library's lock — so a UPS timeout used to raise
		// "% ядра в Task.Run" without a single instruction being executed. Concurrent work also
		// adds up beyond the wall clock. The only line here that measures CPU is the next one.
		var window = Math.Max(1.0, (Stopwatch.GetTimestamp() - _startedTicks) * 1000.0 / Stopwatch.Frequency);
		text.AppendLine($"длительность тика:{perTick.ToString("0.00", ci)} мс в среднем " +
						$"({(perTick * _tick / window * 100).ToString("0.00", ci)} % времени потока интерфейса)");
		text.AppendLine($"фоновые опросы:   {perPool.ToString("0.00", ci)} мс на тик — это длительность " +
						"(ожидание сети, внешнего процесса и блокировки), а не процессорное время");
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
		if (_logDropped > 0)
			text.AppendLine($"журнал CSV:       строк потеряно {_logDropped.ToString(ci)} — запись не успевает");
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

	/// <summary>One line about a background source: how old its snapshot is and when it is due.</summary>
	private static void Age(StringBuilder text, string what, long measuredAt, long nextAt, int periodMs)
	{
		var ci = CultureInfo.InvariantCulture;
		var now = Environment.TickCount64;
		var age = measuredAt == 0
			? "ни разу"
			: ((now - measuredAt) / 1000).ToString(ci) + " с назад" +
			  (Fresh(measuredAt, periodMs) ? "" : " — УСТАРЕЛО");
		var next = nextAt <= now ? "в следующем тике" : ((nextAt - now) / 1000).ToString(ci) + " с";
		text.AppendLine($"    {what,-22} {age,-22} {next}");
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
		// A list of what will happen and two buttons that say what they do. It used to be
		// Yes/No/Cancel, where "Нет" also uninstalled — it only meant "keep TrayMon.json" — so the
		// obvious way to back out of the dialog went ahead and removed everything.
		if (!ConfirmUninstall(out var alsoSettings)) return;

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

		var report = Autostart.Uninstall(alsoSettings);
		report.Add("");
		report.Add("Осталось удалить сам файл TrayMon.exe и папку программы.");
		TextWindow("TrayMon — удаление", string.Join('\n', report));
		ExitThread();
	}

	/// <summary>
	/// The removal dialog: what will be removed, an explicit choice about the settings file, and
	/// buttons named after the action. Esc and the window's close button both cancel.
	/// </summary>
	private static bool ConfirmUninstall(out bool alsoSettings)
	{
		alsoSettings = false;
		using var form = new Form
		{
			Text = "TrayMon — удаление",
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
		var what = new Label
		{
			AutoSize = true,
			MaximumSize = new Size(460, 0),
			Margin = new Padding(0, 0, 0, 8),
			Text = "Будет удалено:\n" +
				   "    • задача планировщика (только если она запускает эту копию)\n" +
				   "    • ярлык на рабочем столе (только если он ведёт на эту копию)\n" +
				   "    • запомненные Windows позиции значков этой копии\n\n" +
				   "Сам файл TrayMon.exe программа не удаляет — уберите его вручную после выхода.",
		};
		var keep = new CheckBox
		{
			Text = "Удалить и файл настроек TrayMon.json",
			AutoSize = true,
			Margin = new Padding(0, 0, 0, 8),
		};
		var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill };
		var cancel = new Button { Text = "Отмена", AutoSize = true, DialogResult = DialogResult.Cancel };
		var remove = new Button { Text = "Удалить", AutoSize = true, DialogResult = DialogResult.OK };
		buttons.Controls.Add(cancel);
		buttons.Controls.Add(remove);
		layout.Controls.Add(what, 0, 0);
		layout.Controls.Add(keep, 0, 1);
		layout.Controls.Add(buttons, 0, 2);
		form.Controls.Add(layout);
		// Cancel, not the destructive one, on Enter as well as on Esc.
		form.AcceptButton = cancel;
		form.CancelButton = cancel;
		if (form.ShowDialog() != DialogResult.OK) return false;
		alsoSettings = keep.Checked;
		return true;
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

			// The pending settings, before anything else. They used to be written only on every
			// fifteenth tick and on session shutdown, so "tried it, switched four icons on, quit"
			// inside the first thirty seconds lost the lot — and on a first run that also meant the
			// automatic choice of which icons to show was made again from scratch next time.
			if (_configDirty && !_dry)
			{
				_configDirty = false;
				try { _config.Save(); } catch (Exception) { /* going away regardless */ }
			}
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
			// Whatever the CSV writer still holds, with a bound on the wait: a trail on a slow share
			// must not keep the process alive, and must not be silently dropped either.
			if (!_dry && Volatile.Read(ref _logWriting) == 1)
			{
				var until = Environment.TickCount64 + 2000;
				while (Volatile.Read(ref _logWriting) == 1 && Environment.TickCount64 < until) Thread.Sleep(50);
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
