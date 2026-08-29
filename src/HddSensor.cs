using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace TrayMon;

/// <summary>
/// Temperatures and health of SATA disks sitting behind a RAID controller.
///
/// Nothing in the Windows storage stack exposes them — an array is one device — but Intel RST
/// and several other controllers answer CSMI pass-through, and smartmontools speaks it. Hence
/// an external smartctl.exe instead of an API call.
///
/// Devices are discovered by `smartctl --scan-open -d csmi`, so this works on any machine with
/// such a controller and needs no per-host configuration. Results are deduplicated by serial
/// number: some drivers answer with the same disk on several CSMI ports.
///
/// If smartctl.exe is missing, or there is no RAID controller, this simply returns nothing and
/// the icons do not appear.
/// </summary>
public sealed class HddSensor
{
	private static readonly Regex TempLine = new(@"^\s*194\s+Temperature_Celsius.*?-\s+(\d+)", RegexOptions.Multiline | RegexOptions.Compiled);
	private static readonly Regex TempFallback = new(@"Temperature:\s+(\d+) Celsius", RegexOptions.Compiled);
	private static readonly Regex ModelLine = new(@"(?:Device Model|Model Number):\s+(.+)", RegexOptions.Compiled);
	private static readonly Regex SerialLine = new(@"Serial Number:\s+(\S+)", RegexOptions.Compiled);
	private static readonly Regex ScanLine = new(@"^(/dev/\S+)", RegexOptions.Multiline | RegexOptions.Compiled);
	private static readonly Regex HealthLine = new(
		@"(?:SMART overall-health self-assessment test result|SMART Health Status):\s+(\S+)", RegexOptions.Compiled);

	/// <summary>One device answers in about 100 ms; ten seconds means it is not going to.</summary>
	private const int RunTimeoutMs = 10000;

	private readonly string _exe;
	private List<string> _devices;   // discovered once; ports do not move while Windows runs

	/// <summary>Why the last run failed, if it did; shown by the diagnostics window.</summary>
	public string LastError { get; private set; }

	public string ExePath => _exe;

	public bool Available => File.Exists(_exe);

	public HddSensor(ToolSettings tools = null)
	{
		// README calls smartctl an optional external tool, so where it lives is a setting;
		// empty means the copy next to TrayMon.exe, which is the normal case. The path is never
		// taken from PATH or the working directory — this runs with an elevated token.
		var configured = tools?.Smartctl;
		_exe = string.IsNullOrWhiteSpace(configured)
			? Path.Combine(AppContext.BaseDirectory, "smartctl.exe")
			: configured;
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

		_devices ??= Discover();
		var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

		foreach (var device in _devices)
		{
			// -H rides along in the query that was already being made: a disk that is failing
			// matters more than a disk that is warm, and it costs no extra process and no extra
			// poll — the same run, the same ten minutes, one more flag.
			var output = Run($"-H -A -i {device}");
			if (output is null) continue;

			var temp = TempLine.Match(output);
			var value = temp.Success
				? double.Parse(temp.Groups[1].Value, CultureInfo.InvariantCulture)
				: TempFallback.Match(output) is { Success: true } f
					? double.Parse(f.Groups[1].Value, CultureInfo.InvariantCulture)
					: double.NaN;
			if (double.IsNaN(value)) continue;

			var serial = SerialLine.Match(output);
			var key = serial.Success ? serial.Groups[1].Value : device;
			if (!seen.Add(key)) continue;   // same disk answering on another port

			var model = ModelLine.Match(output);
			var name = model.Success ? model.Groups[1].Value.Trim() : "RAID disk";
			var health = HealthLine.Match(output);
			disks.Add(new RaidDisk(
				name, value,
				serial.Success ? serial.Groups[1].Value : "",
				health.Success ? health.Groups[1].Value.Trim() : ""));
		}

		// Nothing answered: the controller or the disks changed, so look again next time.
		if (disks.Count == 0) _devices = null;
		else LastError = null;
		return disks;
	}

	/// <summary>True when the disk reported anything other than a clean bill of health.</summary>
	public static bool Failing(string health) =>
		!string.IsNullOrEmpty(health) &&
		!health.Equals("PASSED", StringComparison.OrdinalIgnoreCase) &&
		!health.Equals("OK", StringComparison.OrdinalIgnoreCase);

	private List<string> Discover()
	{
		var found = new List<string>();
		var scan = Run("--scan-open -d csmi");
		if (scan is null) return found;
		foreach (Match m in ScanLine.Matches(scan)) found.Add(m.Groups[1].Value);
		return found;
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
				// ReadToEnd below never returns — the timeout underneath it is never reached,
				// because control never gets that far.
				RedirectStandardError = false,
			});
			var output = p.StandardOutput.ReadToEnd();
			if (!p.WaitForExit(RunTimeoutMs))
			{
				// A smartctl stuck on a dying disk would otherwise be left running, one more
				// every ten minutes, each holding handles on the device.
				LastError = "smartctl не ответил за " + RunTimeoutMs / 1000 + " с: " + args;
				try { p.Kill(entireProcessTree: true); } catch (Exception) { /* already gone */ }
				return null;
			}
			return output;
		}
		catch (Exception ex)
		{
			LastError = ex.GetType().Name + ": " + ex.Message;
			return null;   // smartctl missing or refused to run; icons just stay away
		}
	}
}
