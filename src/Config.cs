using System.Text.Json;
using System.Text.Json.Serialization;

namespace TrayMon;

/// <summary>Per-icon settings the user can change from the tray menu.</summary>
public sealed class IconSettings
{
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
	private string _community = "PowerChuteUser";

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
}

/// <summary>
/// Settings file next to the executable. Written whenever something changes in the tray menu,
/// so it can also be edited by hand — the program reads it only at startup.
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

	[JsonIgnore]
	public static string Path => System.IO.Path.Combine(AppContext.BaseDirectory, "TrayMon.json");

	public static Config Load()
	{
		try
		{
			if (File.Exists(Path))
			{
				var config = JsonSerializer.Deserialize<Config>(File.ReadAllText(Path), JsonOptions) ?? new Config();
				config.Ups ??= new UpsSettings();   // "Ups": null in a hand-edited file
				return config;
			}
		}
		catch (Exception)
		{
			// A broken file must not keep the program from starting; defaults win and the
			// next change overwrites it.
		}
		return new Config();
	}

	public void Save()
	{
		try { File.WriteAllText(Path, JsonSerializer.Serialize(this, JsonOptions)); }
		catch (Exception) { /* read-only folder — settings just do not persist */ }
	}

	public IconSettings For(string id)
	{
		if (!Icons.TryGetValue(id, out var s)) Icons[id] = s = new IconSettings();
		return s;
	}
}
