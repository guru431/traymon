using System.Diagnostics;
using System.Text.Json;

namespace TrayMon;

/// <summary>
/// Temperatures and health of SATA disks sitting behind a RAID controller.
///
/// Nothing in the Windows storage stack exposes them — an array is one device — but Intel RST
/// and several other controllers answer CSMI pass-through, and smartmontools speaks it. Hence
/// an external smartctl.exe instead of an API call.
///
/// Devices are discovered by `smartctl --scan-open -d csmi` (configurable, because LSI and
/// Adaptec want a different -d), so this works on any machine with such a controller and needs
/// no per-host configuration. Results are deduplicated by serial number: some drivers answer
/// with the same disk on several CSMI ports.
///
/// Everything is read from smartctl's own JSON rather than from its human-readable table. The
/// table cost three separate bugs: only attribute 194 was matched, so Samsung and Intel SATA
/// SSDs — which report temperature in 190 — had no icon at all; a disk whose WHEN_FAILED column
/// said FAILING_NOW no longer matched the pattern, so a disk vanished from the tray exactly as
/// it started to die; and NVMe and SCSI answers have no attribute table to match. smartctl picks
/// the right attribute out of its own drive database, so none of that is ours to guess.
///
/// If smartctl.exe is missing, or there is no RAID controller, this simply returns nothing and
/// the icons do not appear.
/// </summary>
public sealed class HddSensor
{
	/// <summary>One device answers in about 100 ms; ten seconds means it is not going to.</summary>
	private const int RunTimeoutMs = 10000;

	/// <summary>
	/// How long a discovered device list is trusted. It used to be kept for the lifetime of the
	/// process and dropped only when *nothing* answered, so a disk added or moved to another port
	/// while one healthy disk kept answering was never noticed — and "Опросить датчики сейчас" did
	/// not clear it either. A scan costs one more smartctl run, so it is rare rather than free.
	/// </summary>
	private const long RediscoverAfterMs = 60L * 60 * 1000;

	private readonly string _exe;
	private readonly string _scanArgs;
	private List<(string Device, string Type)> _devices;
	private long _discoveredAt;
	private bool? _safe;

	/// <summary>Why the last run failed, if it did; shown by the diagnostics window.</summary>
	public string LastError { get; private set; }

	public string ExePath => _exe;

	public bool Available => File.Exists(_exe);

	public HddSensor(ToolSettings tools = null)
	{
		// README calls smartctl an optional external tool, so where it lives is a setting;
		// empty means the copy next to TrayMon.exe, which is the normal case. A relative path is
		// resolved against the program folder and never against the working directory — under
		// the logon task that directory is %SystemRoot%\System32, and this process holds an
		// elevated token, so "smartctl.exe" would have meant "whatever is in System32".
		var configured = tools?.Smartctl;
		_exe = string.IsNullOrWhiteSpace(configured)
			? Path.Combine(AppContext.BaseDirectory, "smartctl.exe")
			: Path.IsPathFullyQualified(configured)
				? configured
				: Path.Combine(AppContext.BaseDirectory, configured);
		_scanArgs = tools?.SmartctlScan ?? "--scan-open -d csmi";
	}

	/// <summary>
	/// Every distinct disk behind the controller. Blocking, roughly 100 ms per device —
	/// call from a background task and rarely.
	/// </summary>
	public List<RaidDisk> ReadAll()
	{
		var disks = new List<RaidDisk>();
		if (!Available)
		{
			LastError = "smartctl.exe не найден: " + _exe;
			return disks;
		}

		// Where this runs from is checked once, not because of the icons but because of the token:
		// TrayMon holds an elevated one, the path comes out of a file the user can edit, and
		// CreateProcess would happily start whatever is sitting there.
		if (!Safe()) return disks;

		if (_devices is null || Environment.TickCount64 - _discoveredAt > RediscoverAfterMs)
		{
			_devices = Discover();
			_discoveredAt = Environment.TickCount64;
		}
		var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

		foreach (var (device, type) in _devices)
		{
			// -H rides along in the query that was already being made: a disk that is failing
			// matters more than a disk that is warm, and it costs no extra process and no extra
			// poll — the same run, the same ten minutes, one more flag.
			// -n standby,0 leaves a sleeping HDD asleep: waking a parked drive every ten minutes
			// to read its temperature costs the drive far more than the reading is worth.
			using var json = RunJson($"-j -H -A -i -n standby,0 -d {type} {device}");
			if (json is null) continue;

			var root = json.RootElement;
			var temp = Nested(root, "temperature", "current");
			var health = Health(root);
			// Health without a temperature is still an answer, and the most important one there
			// is: a disk in standby, or one whose firmware reports no temperature, used to be
			// dropped here — before the SMART verdict was even looked at — so a failing disk
			// produced no icon, no red plate and no event.
			if (temp is null && health.Length == 0) continue;

			var serial = Text(root, "serial_number") ?? "";
			var key = serial.Length > 0 ? serial : device;
			if (!seen.Add(key)) continue;   // same disk answering on another port

			var name = Text(root, "model_name") ?? Text(root, "device_model") ?? "RAID disk";
			disks.Add(new RaidDisk(name.Trim(), temp, serial, health));
		}

		// Nothing answered: the controller or the disks changed, so look again next time.
		if (disks.Count == 0) _devices = null;
		else LastError = null;
		return disks;
	}

	/// <summary>
	/// Forgets the device list, so the next read scans again. "Опросить датчики сейчас" exists for
	/// the moment right after the hardware changed, which is exactly when a cached list is wrong.
	/// </summary>
	public void Rediscover() => _devices = null;

	/// <summary>Where the tool is weakly protected but is started anyway — see <see cref="Safe"/>.
	/// Shown by the diagnostics window and by <c>--once</c>.</summary>
	public string Note { get; private set; }

	/// <summary>
	/// Whether this smartctl may be started at all. The path is configurable and this process runs
	/// elevated, so a tool an ordinary process can replace is a way to have anything executed with
	/// an administrator token.
	///
	/// Refusing is only worth it where it buys something, and that is the narrow case: the
	/// program's own folder is protected and the configured tool is somewhere weaker — the
	/// settings file redirecting an elevated process out of a safe folder. When the program folder
	/// itself is writable by a non-administrator, TrayMon.exe and every DLL beside it can be
	/// replaced just as easily; refusing to start smartctl there changes nothing about the risk and
	/// silently costs the RAID temperatures, which is exactly what it did on a live machine.
	/// Checked once: an ACL is not free and does not move.
	/// </summary>
	private bool Safe()
	{
		if (_safe.HasValue) return _safe.Value;
		_safe = true;
		var protectedFolder = !Autostart.WritableByNonAdmins(AppContext.BaseDirectory, out _);
		if (!Autostart.UnsafeToRun(_exe, out var who)) return true;

		if (protectedFolder)
		{
			_safe = false;
			LastError = "smartctl.exe не запущен: он вне защищённой папки программы, и его может " +
						"подменить " + who + ", а TrayMon работает с правами администратора";
			return false;
		}
		Note = "smartctl.exe запускается из папки, доступной на запись (" + who + "). " +
			   "TrayMon работает с правами администратора, поэтому подменённый файл получил бы их же — " +
			   "но то же верно и для самого TrayMon.exe рядом с ним";
		return true;
	}

	/// <summary>True when the disk reported anything other than a clean bill of health.</summary>
	public static bool Failing(string health) =>
		!string.IsNullOrEmpty(health) &&
		!health.Equals("PASSED", StringComparison.OrdinalIgnoreCase) &&
		!health.Equals("OK", StringComparison.OrdinalIgnoreCase);

	private static string Health(JsonElement root)
	{
		if (!root.TryGetProperty("smart_status", out var status)) return "";
		if (!status.TryGetProperty("passed", out var passed)) return "";
		return passed.ValueKind == JsonValueKind.True ? "PASSED" : "FAILING_NOW";
	}

	private static string Text(JsonElement root, string name) =>
		root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
			? value.GetString()
			: null;

	private static double? Nested(JsonElement root, string outer, string inner)
	{
		if (!root.TryGetProperty(outer, out var section)) return null;
		if (!section.TryGetProperty(inner, out var value)) return null;
		return value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var d) ? d : null;
	}

	private List<(string Device, string Type)> Discover()
	{
		var found = new List<(string, string)>();
		using var json = RunJson("-j " + _scanArgs);
		if (json is null) return found;
		if (!json.RootElement.TryGetProperty("devices", out var devices) ||
			devices.ValueKind != JsonValueKind.Array) return found;
		foreach (var device in devices.EnumerateArray())
		{
			var name = Text(device, "name");
			var type = Text(device, "type");
			// Both go straight onto a command line, so anything that would split or requote the
			// arguments is refused rather than escaped. Real values look like "/dev/csmi0,0"
			// and "csmi"; nothing legitimate here contains whitespace or a quote.
			if (!Plain(name) || (type is not null && !Plain(type))) continue;
			if (!string.IsNullOrEmpty(name)) found.Add((name, string.IsNullOrEmpty(type) ? "auto" : type));
		}
		return found;
	}

	private static bool Plain(string value) =>
		!string.IsNullOrEmpty(value) && value.All(c => !char.IsWhiteSpace(c) && c != '"' && c != '\'');

	/// <summary>Runs smartctl and parses its JSON; null when it did not run or did not answer.</summary>
	private JsonDocument RunJson(string args)
	{
		var output = Run(args);
		if (string.IsNullOrWhiteSpace(output)) return null;
		try { return JsonDocument.Parse(output); }
		catch (JsonException ex)
		{
			LastError = "smartctl вернул не JSON: " + ex.Message;
			return null;
		}
	}

	private string Run(string args)
	{
		try
		{
			using var p = Process.Start(new ProcessStartInfo(_exe, args)
			{
				CreateNoWindow = true,
				UseShellExecute = false,
				RedirectStandardOutput = true,
				// stderr is deliberately left alone. Redirecting a pipe nobody reads is a
				// deadlock waiting for an unusual CSMI answer: the pipe fills at about 4 KB,
				// smartctl blocks writing to it, stops writing stdout and never closes it, and
				// the read below never returns.
				RedirectStandardError = false,
			});

			// Started before the wait, not read to the end before it. ReadToEnd blocks until the
			// child closes stdout, so the timeout underneath it was never reached: a smartctl
			// stuck on a dying disk — the very case the timeout was written for — held the
			// refresh flag for ever, greyed out every RAID icon and outlived the process.
			var reader = p.StandardOutput.ReadToEndAsync();
			if (!p.WaitForExit(RunTimeoutMs))
			{
				LastError = "smartctl не ответил за " + RunTimeoutMs / 1000 + " с: " + args;
				try { p.Kill(entireProcessTree: true); } catch (Exception) { /* already gone */ }
				try { reader.Wait(1000); } catch (Exception) { /* the pipe dies with the child */ }
				return null;
			}
			p.WaitForExit();   // lets the reader finish now that the child is done
			try { return reader.GetAwaiter().GetResult(); }
			catch (Exception) { return null; }
		}
		catch (Exception ex)
		{
			LastError = ex.GetType().Name + ": " + ex.Message;
			return null;   // smartctl missing or refused to run; icons just stay away
		}
	}
}
