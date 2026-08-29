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

	/// <summary>Value at which the plate turns yellow; null keeps the built-in threshold.
	/// Set both this and <see cref="Crit"/> above any reachable value to switch the
	/// threshold colouring off and always see the chosen plate colour.</summary>
	public double? Warn { get; set; }

	/// <summary>Value at which the plate turns red; null keeps the built-in threshold.</summary>
	public double? Crit { get; set; }
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

	/// <summary>Substrings that mark an adapter as virtual; null keeps the built-in list.</summary>
	public List<string> NotPhysical
	{
		get => _notPhysical;
		set { if (value is { Count: > 0 }) _notPhysical = value; }
	}

	[JsonIgnore]
	public string[] Filters => _notPhysical is { Count: > 0 } ? _notPhysical.ToArray() : BuiltInNotPhysical;
}

/// <summary>External tools. Empty means "the copy next to TrayMon.exe", which is the normal case.</summary>
public sealed class ToolSettings
{
	/// <summary>Full path to smartctl.exe. README calls it an optional external tool, so where
	/// it lives should not be hard-wired to the install folder.</summary>
	public string Smartctl { get; set; }
}

/// <summary>
/// Optional CSV trail, off by default. Answers "why was the server slow last night", which
/// nothing else here can. The interval is floored at 30 s on purpose: writing every tick would
/// cost as much as the sensors themselves and break the whole point of the program.
/// </summary>
public sealed class LogSettings
{
	private int _everySeconds = 60;

	public bool Enabled { get; set; }

	public int EverySeconds
	{
		get => _everySeconds;
		set { if (value >= 30) _everySeconds = value; }
	}

	/// <summary>Where to write; empty means TrayMon.csv next to the executable.</summary>
	public string Path { get; set; }
}

/// <summary>
/// Settings file next to the executable. Written whenever something changes in the tray menu,
/// so it can also be edited by hand — but the program reads it only at startup and serialises
/// the whole object on the next change, so an edit made while TrayMon runs is overwritten
/// unless it is re-read first ("Перечитать настройки" in the menu).
/// </summary>
public sealed class Config
{
	private static readonly JsonSerializerOptions JsonOptions = new()
	{
		WriteIndented = true,
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
	};

	public Dictionary<string, IconSettings> Icons { get; set; } = new();

	public UpsSettings Ups { get; set; } = new();

	public NetSettings Net { get; set; } = new();

	public ToolSettings Tools { get; set; } = new();

	public LogSettings Log { get; set; } = new();

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

	public static Config Load()
	{
		try
		{
			if (File.Exists(Path))
			{
				var config = JsonSerializer.Deserialize<Config>(File.ReadAllText(Path), JsonOptions) ?? new Config();
				// Any of these can be an explicit null in a hand-edited file, and a null Icons
				// used to take the program down on the first tick rather than at load time.
				config.Icons ??= new Dictionary<string, IconSettings>();
				config.Ups ??= new UpsSettings();
				config.Net ??= new NetSettings();
				config.Tools ??= new ToolSettings();
				config.Log ??= new LogSettings();
				foreach (var key in config.Icons.Where(p => p.Value is null).Select(p => p.Key).ToList())
					config.Icons[key] = new IconSettings();
				config.Stamp = File.GetLastWriteTimeUtc(Path);
				return config;
			}
		}
		catch (Exception ex)
		{
			// A broken file must not keep the program from starting — but it must not be thrown
			// away in silence either: the first change from the menu would overwrite hand-made
			// colours, labels and the UPS address with defaults, with nothing said about it.
			var spoiled = new Config { LoadError = Keep(ex) };
			return spoiled;
		}
		return new Config();
	}

	/// <summary>Moves an unreadable file aside so the user still has it, and says where it went.</summary>
	private static string Keep(Exception ex)
	{
		var message = ex.GetType().Name + ": " + ex.Message;
		try
		{
			var bad = Path + ".bad";
			File.Delete(bad);
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
	/// </summary>
	public bool Save(out string error)
	{
		error = null;
		var temp = Path + ".tmp";
		try
		{
			File.WriteAllText(temp, JsonSerializer.Serialize(this, JsonOptions));
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
		if (s.Enabled && s.Color is null && s.Label is null && s.Ink is null && s.Warn is null && s.Crit is null)
			Icons.Remove(id);
	}
}
