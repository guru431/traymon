using System.Text.Json;
using System.Text.Json.Serialization;

namespace TrayMon;

/// <summary>Per-icon settings the user can change from the tray menu.</summary>
public sealed class IconSettings
{
	/// <summary>
	/// Settings of an icon nobody has ever touched. Shared by every such icon and never written
	/// to: <see cref="Config.Get"/> hands this out instead of inserting an entry, so merely
	/// reading a setting on every tick cannot grow the dictionary or the file on disk.
	/// </summary>
	internal static readonly IconSettings Default = new();

	public bool Enabled { get; set; } = true;

	/// <summary>Plate colour as #RRGGBB; null keeps the built-in colour of that metric.</summary>
	public string Color { get; set; }

	/// <summary>Tooltip caption; null keeps the built-in one.</summary>
	public string Label { get; set; }

	/// <summary>Colour of the digits: "light", "dark", or null to pick by the plate colour.</summary>
	public string Ink { get; set; }

	/// <summary>Value at which the plate turns yellow; null keeps the built-in threshold.</summary>
	public double? Warn { get; set; }

	/// <summary>Value at which the plate turns red; null keeps the built-in threshold.</summary>
	public double? Crit { get; set; }

	/// <summary>
	/// False switches threshold colouring off for this icon — the plate then always shows its
	/// own colour. A flag rather than a pair of unreachable thresholds: writing 1e9 into Warn
	/// and Crit destroyed whatever the user had set, and the file is meant to be read by people.
	/// Null means "on", so the entry only appears once somebody turns it off.
	/// </summary>
	public bool? Alerts { get; set; }

	/// <summary>
	/// Whether a transition into the red raises a balloon. Null means the built-in choice for
	/// that metric — on for the ones that mean hardware is failing (a RAID disk, a stopped fan,
	/// free space, the UPS), off for everything else.
	/// </summary>
	public bool? NotifyOnCritical { get; set; }

	/// <summary>
	/// True marks this source as one the machine is expected to have: losing it is then an event
	/// of its own, reported once and counted in the summary icon, instead of a grey plate nobody
	/// notices. Null and false mean "optional", which is what every source is by default — a new
	/// machine must not raise an alarm for every technology it does not carry.
	/// </summary>
	public bool? Required { get; set; }

	/// <summary>
	/// What a fan reading of zero means here: <c>alarm</c> (any standstill is a failure, the
	/// built-in choice), <c>zero-ok</c> (a fan with a legitimate zero-rpm mode — only a fan that
	/// stops while its duty cycle is being driven is alarming) or <c>off</c> (watch the speed,
	/// never raise an alarm). Null is <c>alarm</c>.
	/// </summary>
	public string Stall { get; set; }

	/// <summary>
	/// For the free-space icons only: an extra condition in gigabytes left. Five per cent of a
	/// 16 TB array and five per cent of a 120 GB system volume are the same percentage and a
	/// completely different practical risk, so the percentage thresholds alone could not express
	/// both. Whichever condition is worse decides the colour, and the tooltip says which fired.
	/// </summary>
	public double? WarnGb { get; set; }

	public double? CritGb { get; set; }
}

/// <summary>
/// Where to ask for the UPS reading. The defaults fit a UPS on a serial port of this machine,
/// published over SNMP by PowerChute; another host or community only needs the file edited.
/// The setters keep the default when the file hands over an empty value — the file is meant to
/// be edited by hand, and a blank community must not stop the program from starting.
/// </summary>
public sealed class UpsSettings
{
	private string _host = "127.0.0.1";
	private int _port = 161;
	private string _community = "public";
	private int _timeoutMs = 1500;

	public string Host
	{
		get => _host;
		set { if (!string.IsNullOrWhiteSpace(value)) _host = value; }
	}

	public int Port
	{
		get => _port;
		set { if (value is > 0 and <= 65535) _port = value; }
	}

	public string Community
	{
		get => _community;
		set { if (!string.IsNullOrWhiteSpace(value)) _community = value; }
	}

	/// <summary>
	/// How long to wait for the answer. 1.5 s is plenty for an agent on the loopback, but a
	/// network card of a UPS a few hops away can be slower — hence a setting rather than a
	/// constant. The query runs off the UI thread, so a longer wait costs nothing on a tick.
	/// </summary>
	public int TimeoutMs
	{
		get => _timeoutMs;
		set { if (value is >= 200 and <= 30000) _timeoutMs = value; }
	}
}

/// <summary>
/// Which adapters are not a physical NIC. PDH reports adapter descriptions, so virtual
/// switches, tunnels and capture drivers can only be told apart by name — and any fixed list
/// is wrong on some machine, which is why this one is editable while poll intervals are not.
/// </summary>
public sealed class NetSettings
{
	public static readonly string[] BuiltInNotPhysical =
	{
		"Loopback", "isatap", "Teredo", "vEthernet", "Virtual", "Pseudo", "Npcap",
		"WAN Miniport", "Bluetooth", "QoS", "Filter",
	};

	private List<string> _notPhysical;
	private List<string> _include;

	/// <summary>
	/// Substrings that mark an adapter as virtual; null keeps the built-in list. Empty and null
	/// entries are dropped: <c>[""]</c> matches every description and used to hide every adapter,
	/// and a literal <c>null</c> in the list threw once every six seconds inside the network family.
	/// </summary>
	public List<string> NotPhysical
	{
		get => _notPhysical;
		set
		{
			var clean = value?.Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
			if (clean is { Count: > 0 }) _notPhysical = clean;
		}
	}

	/// <summary>
	/// Substrings of the adapters to keep, whatever the denylist says. Deliberately a second
	/// list rather than a rewrite of the first: switching on the VPN or the virtual adapter you
	/// actually care about used to mean copying the whole built-in denylist out of the README and
	/// editing it — and every substring forgotten on the way brought back a Bluetooth or Teredo
	/// icon. Empty means "everything the denylist allows".
	/// </summary>
	public List<string> Include
	{
		get => _include;
		set
		{
			var clean = value?.Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
			if (clean is { Count: > 0 }) _include = clean;
		}
	}

	/// <summary>
	/// Link speed in Mbit/s to use for the "percent of the link" colour, per adapter substring.
	/// A driver that does not fill Current Bandwidth in leaves the utilisation unknown, and a
	/// Wi-Fi link rate is not a capacity at all — so this is the only honest way to say what the
	/// percentage should be measured against. Zero and negative values are ignored.
	/// </summary>
	public Dictionary<string, double> Bandwidth { get; set; }

	[JsonIgnore]
	public string[] Filters => _notPhysical is { Count: > 0 } ? _notPhysical.ToArray() : BuiltInNotPhysical;

	[JsonIgnore]
	public string[] Included => _include is { Count: > 0 } ? _include.ToArray() : Array.Empty<string>();

	/// <summary>The configured link speed for this adapter description, or null.</summary>
	public double? BandwidthFor(string adapter)
	{
		if (Bandwidth is null || string.IsNullOrEmpty(adapter)) return null;
		foreach (var (pattern, mbit) in Bandwidth)
		{
			if (string.IsNullOrWhiteSpace(pattern) || !(mbit > 0)) continue;
			if (adapter.Contains(pattern, StringComparison.OrdinalIgnoreCase)) return mbit;
		}
		return null;
	}
}

/// <summary>
/// Which sources may be opened at all. The only entry so far is the low-level sensor library:
/// it loads a ring-0 driver that Microsoft has block-listed, and a machine where nobody needs the
/// CPU package temperature or the NVMe wear figures has no reason to carry that surface at all.
/// </summary>
public sealed class SensorSettings
{
	/// <summary>
	/// False keeps LibreHardwareMonitor — and therefore WinRing0 — unopened. CPU temperature,
	/// motherboard fans and NVMe wear then have no source and their icons say so; everything
	/// else, disk temperatures included, works exactly as before.
	/// </summary>
	public bool UseSensorDriver { get; set; } = true;
}

/// <summary>External tools. Empty means "the copy next to TrayMon.exe", which is the normal case.</summary>
public sealed class ToolSettings
{
	/// <summary>Full path to smartctl.exe. README calls it an optional external tool, so where
	/// it lives should not be hard-wired to the install folder. A relative value is resolved
	/// against the program folder, never against the working directory: under the logon task
	/// that directory is %SystemRoot%\System32, and this process holds an elevated token.</summary>
	public string Smartctl { get; set; }

	private string _smartctlScan = "--scan-open -d csmi";

	/// <summary>
	/// How to enumerate the disks behind the controller. CSMI covers Intel RST, which is what
	/// this was written for, but LSI and Adaptec want <c>-d megaraid,N</c> or <c>-d aacraid,...</c>
	/// — and that cannot be guessed, so it is a setting rather than a constant.
	/// </summary>
	public string SmartctlScan
	{
		get => _smartctlScan;
		set { if (!string.IsNullOrWhiteSpace(value)) _smartctlScan = value; }
	}
}

/// <summary>
/// Optional CSV trail, off by default. Answers "why was the server slow last night", which
/// nothing else here can. The interval is floored at 30 s on purpose: writing every tick would
/// cost as much as the sensors themselves and break the whole point of the program.
/// </summary>
public sealed class LogSettings
{
	private int _everySeconds = 60;
	private int _maxMb = 32;

	public bool Enabled { get; set; }

	public int EverySeconds
	{
		get => _everySeconds;
		set { if (value >= 30) _everySeconds = value; }
	}

	/// <summary>Where to write; empty means TrayMon.csv next to the executable.</summary>
	public string Path { get; set; }

	/// <summary>
	/// Size at which the file is rolled over to <c>.1</c> and started again. A monitor that fills
	/// the disk it is watching is a special kind of useless, and seventeen rows every thirty
	/// seconds is about 40 MB a year — small, but unbounded is unbounded. 1-4096 MB.
	/// </summary>
	public int MaxMb
	{
		get => _maxMb;
		set { if (value is >= 1 and <= 4096) _maxMb = value; }
	}
}

/// <summary>
/// Settings file next to the executable. Written whenever something changes in the tray menu,
/// so it can also be edited by hand — but the program reads it only at startup and serialises
/// the whole object on the next change, so an edit made while TrayMon runs is overwritten
/// unless it is re-read first ("Перечитать настройки" in the menu).
/// </summary>
public sealed class Config : IGuidStore
{
	// Lenient on the way in, strict on the way out. This file is one the README invites people
	// to edit by hand, and the strict defaults silently dropped whatever did not match exactly:
	// a lower-case "warn" was ignored and then erased by the next Save, and a trailing comma or
	// a // comment threw the whole file out and renamed it to .bad.
	private static readonly JsonSerializerOptions JsonOptions = new()
	{
		WriteIndented = true,
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
		PropertyNameCaseInsensitive = true,
		ReadCommentHandling = JsonCommentHandling.Skip,
		AllowTrailingCommas = true,
	};

	private int _tickMs = 2000;

	/// <summary>
	/// How often everything is polled. Only upwards: the price of this program is proportional
	/// to this number, and letting it be lowered would turn the one measurement the project is
	/// built on into whatever the file happens to say. 2 s is the built-in rate; 5 or 10 s is
	/// for machines where even 0.5 % of a core is worth arguing about.
	/// </summary>
	public int TickMs
	{
		get => _tickMs;
		set { if (value is >= 2000 and <= 60000) _tickMs = value; }
	}

	public Dictionary<string, IconSettings> Icons { get; set; } = new();

	/// <summary>
	/// Which tray identity each source of a family was given: pool name → source key → GUID.
	/// Written by the program, not meant to be edited. It exists because the assignment used to
	/// live only in memory: after a restart with one device missing, the next device took its
	/// GUID — and with it the position in the tray and the visibility Windows remembers.
	/// </summary>
	public Dictionary<string, Dictionary<string, string>> Slots { get; set; } = new();

	public UpsSettings Ups { get; set; } = new();

	public NetSettings Net { get; set; } = new();

	public ToolSettings Tools { get; set; } = new();

	public LogSettings Log { get; set; } = new();

	public SensorSettings Sensors { get; set; } = new();

	[JsonIgnore]
	public static string Path => System.IO.Path.Combine(AppContext.BaseDirectory, "TrayMon.json");

	/// <summary>What went wrong while loading, if anything; shown by --once and by the menu.</summary>
	[JsonIgnore]
	public string LoadError { get; private set; }

	/// <summary>Write time of the file this object was read from, so an edit made behind our
	/// back can be noticed instead of being silently overwritten.</summary>
	[JsonIgnore]
	public DateTime Stamp { get; private set; }

	/// <summary>True when the file on disk has been edited since it was read.</summary>
	[JsonIgnore]
	public bool ChangedOnDisk
	{
		get
		{
			try { return File.Exists(Path) && File.GetLastWriteTimeUtc(Path) != Stamp; }
			catch (Exception) { return false; }
		}
	}

	/// <summary>
	/// Reads the file at startup, and moves an unreadable one aside so the next Save does not
	/// overwrite a file somebody spent an evening on.
	/// </summary>
	public static Config Load() => Read(recover: true);

	/// <summary>
	/// Reads the file without touching anything on disk. This is what the watcher and
	/// "Перечитать настройки" use: the recovery path renames the file being edited, and a
	/// half-written intermediate save — an editor writes in more than one step — is not a reason
	/// to take somebody's settings file away from them and delete the previous backup on the way.
	/// </summary>
	public static Config Read() => Read(recover: false);

	private static Config Read(bool recover)
	{
		try
		{
			if (File.Exists(Path))
			{
				var config = JsonSerializer.Deserialize<Config>(File.ReadAllText(Path), JsonOptions) ?? new Config();
				// Any of these can be an explicit null in a hand-edited file, and a null Icons
				// used to take the program down on the first tick rather than at load time.
				config.Icons ??= new Dictionary<string, IconSettings>();
				config.Slots ??= new Dictionary<string, Dictionary<string, string>>();
				config.Ups ??= new UpsSettings();
				config.Net ??= new NetSettings();
				config.Tools ??= new ToolSettings();
				config.Log ??= new LogSettings();
				config.Sensors ??= new SensorSettings();
				foreach (var key in config.Icons.Where(p => p.Value is null).Select(p => p.Key).ToList())
					config.Icons[key] = new IconSettings();
				foreach (var key in config.Slots.Where(p => p.Value is null).Select(p => p.Key).ToList())
					config.Slots[key] = new Dictionary<string, string>(StringComparer.Ordinal);
				config.LoadError = config.Validate();
				config.Stamp = File.GetLastWriteTimeUtc(Path);
				return config;
			}
		}
		catch (JsonException ex)
		{
			// A broken file must not keep the program from starting — but it must not be thrown
			// away in silence either: the first change from the menu would overwrite hand-made
			// colours, labels and the UPS address with defaults, with nothing said about it.
			return new Config { LoadError = recover ? Keep(ex) : Describe(ex) + " — файл не тронут" };
		}
		catch (Exception ex)
		{
			// Everything that is not "the JSON is wrong" is transient: the file open in an editor,
			// an antivirus or OneDrive holding it for a moment. Renaming a perfectly good file to
			// .bad because a read collided with a scanner is how settings disappear for good —
			// the second such collision deleted the previous .bad on the way past.
			return new Config { LoadError = Describe(ex) + " — файл не тронут, настройки взяты по умолчанию" };
		}
		return new Config();
	}

	private static string Describe(Exception ex) => ex.GetType().Name + ": " + ex.Message;

	/// <summary>
	/// Drops the values that would break the program rather than configure it, and says which.
	/// The same rules the threshold dialog applies, because a hand-edited file reaches exactly
	/// the same places: <c>NaN</c> and <c>Infinity</c> parse happily out of JSON and then compare
	/// false against everything, and a pair with the red threshold below the yellow one painted a
	/// red plate while the summary icon refused to score it at all.
	/// </summary>
	private string Validate()
	{
		var dropped = new List<string>();
		foreach (var (id, s) in Icons)
		{
			if (s is null) continue;
			if (!Sane(s.Warn) || !Sane(s.Crit) || (s.Warn.HasValue && s.Crit.HasValue && s.Warn >= s.Crit))
			{
				if (s.Warn.HasValue || s.Crit.HasValue) dropped.Add(id + ": пороги");
				s.Warn = null;
				s.Crit = null;
			}
			if (!Sane(s.WarnGb) || !Sane(s.CritGb) || (s.WarnGb.HasValue && s.CritGb.HasValue && s.WarnGb <= s.CritGb))
			{
				// Gigabytes left, so the red threshold is the *lower* number here.
				if (s.WarnGb.HasValue || s.CritGb.HasValue) dropped.Add(id + ": пороги в ГБ");
				s.WarnGb = null;
				s.CritGb = null;
			}
		}
		return dropped.Count == 0
			? null
			: "непригодные значения в файле пропущены (" + string.Join(", ", dropped) + ")";
	}

	/// <summary>A threshold has to be a finite number in a range a metric can reach.</summary>
	internal static bool Sane(double? value) =>
		!value.HasValue || (double.IsFinite(value.Value) && value.Value is >= 0 and <= 1e6);

	/// <summary>Moves an unreadable file aside so the user still has it, and says where it went.</summary>
	private static string Keep(Exception ex)
	{
		var message = Describe(ex);
		try
		{
			var bad = Path + ".bad";
			// The previous backup is not overwritten. It used to be deleted first, so two bad
			// saves in a row — which is what happens while somebody is learning the format —
			// left nothing but the second broken file.
			if (File.Exists(bad))
				return message + " — предыдущая копия TrayMon.json.bad сохранена, файл оставлен на месте";
			File.Move(Path, bad);
			return message + " — файл сохранён как TrayMon.json.bad, настройки взяты по умолчанию";
		}
		catch (Exception)
		{
			return message + " — настройки взяты по умолчанию";
		}
	}

	public bool Save() => Save(out _);

	/// <summary>
	/// Writes through a temporary file: this program runs on machines that lose power (that is
	/// what the UPS icon is for), and WriteAllText truncates before it writes, so a cut at the
	/// wrong moment used to leave half a JSON file and lose every setting.
	///
	/// The temporary file is flushed all the way to the device before the rename. NTFS journals
	/// metadata, so the rename can reach the disk while the bytes it points at are still in the
	/// cache — which is the same half-written file the temporary was there to prevent.
	/// </summary>
	public bool Save(out string error)
	{
		error = null;
		var temp = Path + ".tmp";
		try
		{
			var bytes = System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(this, JsonOptions));
			using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None,
											   4096, FileOptions.WriteThrough))
			{
				stream.Write(bytes, 0, bytes.Length);
				stream.Flush(flushToDisk: true);
			}
			if (File.Exists(Path)) File.Replace(temp, Path, null);
			else File.Move(temp, Path);
			Stamp = File.GetLastWriteTimeUtc(Path);
			return true;
		}
		catch (Exception ex)
		{
			error = ex.Message;
			try { File.Delete(temp); } catch (Exception) { /* nothing left to do */ }
			return false;
		}
	}

	/// <summary>Settings of an icon, without creating an entry for it. For reading only.</summary>
	public IconSettings Get(string id) => Icons.TryGetValue(id, out var s) ? s : IconSettings.Default;

	/// <summary>Settings of an icon, creating the entry so it can be written to and saved.</summary>
	public IconSettings For(string id)
	{
		if (!Icons.TryGetValue(id, out var s)) Icons[id] = s = new IconSettings();
		return s;
	}

	/// <summary>Drops the entry once it holds nothing but defaults, so the file stays readable.</summary>
	public void Tidy(string id)
	{
		if (!Icons.TryGetValue(id, out var s)) return;
		if (s.Enabled && s.Color is null && s.Label is null && s.Ink is null && s.Warn is null &&
			s.Crit is null && s.Alerts is null && s.NotifyOnCritical is null && s.Required is null &&
			s.Stall is null && s.WarnGb is null && s.CritGb is null)
			Icons.Remove(id);
	}

	/// <summary>
	/// Carries a settings entry over to a new id. Ids are derived from the hardware, and two of
	/// them had to change to become stable — the card index moved when a card was added, and a fan
	/// key flipped between <c>Fan #1</c> and <c>chip/Fan #1</c> as a second chip appeared and went.
	/// A rename without migration silently discards somebody's colour, caption and thresholds.
	/// </summary>
	/// <returns>True when something was actually moved.</returns>
	public bool Rename(string from, string to)
	{
		if (string.Equals(from, to, StringComparison.Ordinal)) return false;
		if (!Icons.TryGetValue(from, out var s) || Icons.ContainsKey(to)) return false;
		Icons.Remove(from);
		Icons[to] = s;
		return true;
	}

	// ---- IGuidStore: which tray identity each source of a family holds ----

	private Dictionary<string, string> Pool(string pool, bool create)
	{
		if (Slots.TryGetValue(pool, out var known)) return known;
		if (!create) return null;
		return Slots[pool] = new Dictionary<string, string>(StringComparer.Ordinal);
	}

	bool IGuidStore.TryGet(string pool, string key, out Guid guid)
	{
		guid = Guid.Empty;
		var table = Pool(pool, create: false);
		return table is not null && table.TryGetValue(key, out var text) && Guid.TryParse(text, out guid);
	}

	string IGuidStore.OwnerOf(string pool, Guid guid)
	{
		var table = Pool(pool, create: false);
		if (table is null) return null;
		foreach (var (key, text) in table)
			if (Guid.TryParse(text, out var known) && known == guid) return key;
		return null;
	}

	void IGuidStore.Set(string pool, string key, Guid guid)
	{
		var table = Pool(pool, create: true);
		if (table.TryGetValue(key, out var was) && string.Equals(was, guid.ToString(), StringComparison.OrdinalIgnoreCase))
			return;
		table[key] = guid.ToString();
		_slotsChanged = true;
	}

	void IGuidStore.Remove(string pool, string key)
	{
		var table = Pool(pool, create: false);
		if (table is null || !table.Remove(key)) return;
		if (table.Count == 0) Slots.Remove(pool);   // an empty section is noise in a file people read
		_slotsChanged = true;
	}

	private bool _slotsChanged;

	/// <summary>
	/// True once since the last call: the pools have handed out or given back an identity, so the
	/// file is behind. Reported rather than saved on the spot — the caller owns when the disk is
	/// touched, and a settings file is the one thing here that writes on a schedule.
	/// </summary>
	public bool TakeSlotsChanged()
	{
		var changed = _slotsChanged;
		_slotsChanged = false;
		return changed;
	}
}
