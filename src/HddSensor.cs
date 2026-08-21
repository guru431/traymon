using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace TrayMon;

/// <summary>
/// Temperatures of SATA disks sitting behind a RAID controller.
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

	private readonly string _exe = Path.Combine(AppContext.BaseDirectory, "smartctl.exe");
	private List<string> _devices;   // discovered once; ports do not move while Windows runs

	public bool Available => File.Exists(_exe);

	/// <summary>
	/// Every distinct disk behind the controller. Blocking, roughly 100 ms per device —
	/// call from a background task and rarely.
	/// </summary>
	public List<(string Name, double Temp, string Serial)> ReadAll()
	{
		var disks = new List<(string, double, string)>();
		if (!Available) return disks;

		_devices ??= Discover();
		var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

		foreach (var device in _devices)
		{
			var output = Run($"-A -i {device}");
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
			disks.Add((name, value, serial.Success ? serial.Groups[1].Value : ""));
		}

		// Nothing answered: the controller or the disks changed, so look again next time.
		if (disks.Count == 0) _devices = null;
		return disks;
	}

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
				RedirectStandardError = true,
			});
			var output = p.StandardOutput.ReadToEnd();
			p.WaitForExit(10000);
			return output;
		}
		catch (Exception)
		{
			return null;   // smartctl missing or refused to run; icons just stay away
		}
	}
}
