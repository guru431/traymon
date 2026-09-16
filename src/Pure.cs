using System.Globalization;
using System.Text;

namespace TrayMon;

/// <summary>
/// Where a pool keeps the assignment it handed out, so the same source gets the same tray
/// identity after a restart. The table used to be rebuilt from scratch every time and filled in
/// the order sources happened to answer first: with A and B present, A took the first GUID; after
/// a restart without A, B took it — and B then inherited the position, and the visibility, of an
/// icon that belonged to something else.
/// </summary>
internal interface IGuidStore
{
	bool TryGet(string pool, string key, out Guid guid);

	/// <summary>Which key this identity is remembered for, or null when nobody claims it.</summary>
	string OwnerOf(string pool, Guid guid);

	void Set(string pool, string key, Guid guid);

	void Remove(string pool, string key);
}

/// <summary>
/// Hands out a stable tray identity per key from a fixed pool, and never hands the same one
/// to two live keys. Adapters, volumes, disks and fans all come and go while the machine
/// runs, and a GUID taken by position in a sorted list moves the moment a new letter or a
/// new serial number appears — the icon that owned it is then deleted by the newcomer's
/// registration and never comes back.
/// </summary>
internal sealed class GuidPool
{
	private readonly Guid[] _pool;
	private readonly Dictionary<string, Guid> _taken = new(StringComparer.Ordinal);
	private readonly HashSet<string> _refused = new(StringComparer.Ordinal);
	private readonly IGuidStore _store;

	public GuidPool(string what, string id, Guid[] pool, IGuidStore store = null)
	{
		What = what;
		Id = id;
		_pool = pool;
		_store = store;
	}

	/// <summary>What this pool holds, for the "there was not enough room" message.</summary>
	public string What { get; }

	/// <summary>Stable ASCII name of the pool in the settings file. Never the Russian caption:
	/// it is a key somebody reads and edits.</summary>
	public string Id { get; }

	public int Size => _pool.Length;
	public int Used => _taken.Count;

	/// <summary>How many distinct sources were left without an icon. The limits used to be
	/// silent: twelve volumes on a server simply meant four of them did not exist, and the
	/// only place that said so was a line in the README.</summary>
	public int Refused => _refused.Count;

	public Guid? For(string key)
	{
		if (_taken.TryGetValue(key, out var known)) return known;

		// What this key was given last time, if it is one of ours and nobody alive holds it.
		// Without this the promise "one metric always has the same GUID" only held while the set
		// of devices did not change between runs.
		if (_store is not null && _store.TryGet(Id, key, out var remembered) &&
			Array.IndexOf(_pool, remembered) >= 0 && !_taken.ContainsValue(remembered))
		{
			_taken[key] = remembered;
			_refused.Remove(key);
			return remembered;
		}

		foreach (var candidate in _pool)
		{
			if (_taken.ContainsValue(candidate)) continue;
			// Not one somebody else is remembered by either: handing out a GUID that another
			// device is merely absent from would move that device's icon when it came back.
			var owner = _store?.OwnerOf(Id, candidate);
			if (owner is not null && !string.Equals(owner, key, StringComparison.Ordinal)) continue;
			_taken[key] = candidate;
			_refused.Remove(key);
			_store?.Set(Id, key, candidate);
			return candidate;
		}

		// Nothing free that is not spoken for. A remembered assignment must not outrank a source
		// that is here now, so the second pass ignores the remembered table.
		foreach (var candidate in _pool)
		{
			if (_taken.ContainsValue(candidate)) continue;
			_taken[key] = candidate;
			_refused.Remove(key);
			_store?.Set(Id, key, candidate);
			return candidate;
		}

		if (_refused.Count < 64) _refused.Add(key);   // more sources than prepared slots
		return null;
	}

	/// <summary>Gives the identity back — and forgets the remembered assignment with it, or the
	/// next device would be refused a slot nothing alive is using.</summary>
	public void Release(string key)
	{
		_taken.Remove(key);
		_store?.Remove(Id, key);
	}
}

/// <summary>
/// A window of one metric, for the min/avg/max line in the tooltip.
///
/// Values carry their own timestamps. A ring of 150 numbers was called "five minutes" because
/// the tick was two seconds — but the tick is configurable up to a minute and slows to thirty
/// seconds while the session is locked, so the same ring covered anything from five minutes to
/// an hour and a quarter, and extremes from before a long gap stayed in a line labelled "5 мин".
/// </summary>
internal sealed class Stats
{
	/// <summary>Enough for the window at the built-in tick rate; a slower tick simply puts
	/// fewer points in the same five minutes.</summary>
	private const int Capacity = 150;

	public const long WindowMs = 5 * 60 * 1000;

	private readonly double[] _values = new double[Capacity];
	private readonly long[] _at = new long[Capacity];
	private int _count, _next;

	public void Add(double v, long atMs)
	{
		_values[_next] = v;
		_at[_next] = atMs;
		_next = (_next + 1) % Capacity;
		if (_count < Capacity) _count++;
	}

	/// <summary>Min, average and max of the readings inside the window, or null when there are
	/// fewer than two of them — one point is not a range.</summary>
	public (double Min, double Avg, double Max)? Window(long nowMs, long windowMs = WindowMs)
	{
		double min = double.MaxValue, max = double.MinValue, sum = 0;
		var n = 0;
		for (var i = 0; i < _count; i++)
		{
			if (nowMs - _at[i] > windowMs) continue;
			var v = _values[i];
			if (v < min) min = v;
			if (v > max) max = v;
			sum += v;
			n++;
		}
		return n > 1 ? (min, sum / n, max) : null;
	}
}

/// <summary>
/// The one place an alert level is decided: -1 no data, 0 normal, 1 yellow, 2 red.
///
/// There used to be three of these — the renderer applied hysteresis, the journal and the
/// balloons used bare thresholds, and the summary icon applied a second hysteresis to a
/// non-linear score. A CPU falling from 85 to 82.9 against 70/85 therefore kept a red plate
/// while the summary turned yellow, and a value oscillating around the threshold wrote "entered
/// the red" into the journal over and over for an icon that never left it.
/// </summary>
internal static class Alarm
{
	/// <summary>Never had a reading. Different from <see cref="Dead"/>: a source that is already
	/// failing when the program starts has to be reported, and "coming back from silence is not
	/// an alarm" must not swallow it.</summary>
	public const int Unknown = -2;

	public const int Dead = -1, Normal = 0, Warning = 1, Critical = 2;

	/// <summary>
	/// The level this severity calls for, rising at once and falling only once the value has
	/// cleared the threshold by a margin. Without the margin a CPU sitting at 69-71 % against a
	/// threshold of 70 repainted the icon and called into the shell on every single tick — the
	/// exact cost the coarse numbers are there to avoid, reintroduced through the colour.
	/// </summary>
	public static int LevelOf(int prior, double? severity, double warn, double crit, bool alerts)
	{
		if (!severity.HasValue) return Dead;
		// Highlighting switched off is a flag, not a pair of unreachable thresholds: comparing
		// against a sentinel of 1e9 still turned the plate red for a failing disk, whose severity
		// is that same sentinel.
		if (!alerts || !double.IsFinite(warn) || !double.IsFinite(crit) || crit <= warn) return Normal;

		var s = severity.Value;
		var margin = Math.Max(0.5, 0.03 * Math.Max(1, crit));
		if (s >= crit) return Critical;
		if (s >= warn) return prior == Critical && s >= crit - margin ? Critical : Warning;
		if (s >= warn - margin && prior >= Warning) return Warning;
		return Normal;
	}

	public static string Name(int level) => level switch
	{
		Critical => "красный",
		Warning => "жёлтый",
		Normal => "норма",
		Dead => "серый",
		_ => "неизвестно",
	};
}

/// <summary>
/// The common scale behind the summary icon: 0 is idle, 78 is a metric's own yellow threshold,
/// 100 is its own red one, and past that the number keeps growing so "how far over" stays
/// visible. Pure arithmetic, kept apart from the slots so it can be checked directly.
/// </summary>
internal static class Scale
{
	/// <summary>Where the summary icon turns yellow. Every metric's own warning maps here.</summary>
	public const double Yellow = 78;

	public const double Red = 100;

	/// <summary>Ceiling, so a severity of 1e9 (a failing RAID disk) does not print as a novel.</summary>
	public const double Ceiling = 999;

	public static double? Score(double? severity, double warn, double crit)
	{
		if (!severity.HasValue) return null;
		if (crit <= 0 || warn >= crit) return null;
		var v = severity.Value;
		if (v <= 0) return 0;
		if (v < warn) return Yellow * v / warn;
		if (v < crit) return Yellow + (Red - Yellow) * (v - warn) / (crit - warn);
		return Math.Min(Red + (Red - Yellow) * (v - crit) / (crit - warn), Ceiling);
	}
}

/// <summary>
/// How numbers reach an icon. Everything drawn is whole or one decimal, and everything rounds
/// away from zero, because the tooltips are formatted with ToString("0") and a plate reading 50
/// beside a tooltip reading 51 % is a bug report waiting to happen.
/// </summary>
internal static class Format
{
	private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;

	/// <summary>
	/// Above this the number does not fit an icon in any font size worth reading. Five digits
	/// were drawn straight off both edges of a 16-pixel plate — possible on 10GbE and on any
	/// full-duplex link once both directions are added — so a family that can reach it switches
	/// to a bigger unit and says so in its caption, the way free space already did past 1000 GB.
	/// </summary>
	public const double IconCeiling = 10000;

	public static double Round(double v) => Math.Round(v, 0, MidpointRounding.AwayFromZero);

	/// <summary>True when this value needs a bigger unit to stay readable on a plate.</summary>
	public static bool TooWide(double v) => Math.Abs(Round(v)) >= IconCeiling;

	/// <summary>The same value a thousand times bigger a unit: 12000 Мбит/с → "12.0" Гбит/с.</summary>
	public static string PerThousand(double v) => (v / 1000).ToString("0.0", Ci);

	/// <summary>MB/s for an icon — always whole. Tenths would be unreadable at 16 pixels anyway,
	/// and every changed digit costs a repaint plus a call into the shell: five icons flickering
	/// through decimals measured at +0.66 % of a core, three times the price of the data.</summary>
	public static string Mb(double v) => Round(v).ToString("0", Ci);

	/// <summary>MB/s for a tooltip, always with one decimal.</summary>
	public static string Mb2(double v) => v.ToString("0.0", Ci);

	/// <summary>
	/// Network icons count in megabits, not megabytes — and whole ones, like every other icon.
	/// Megabits are the unit a link is rated in and the only one in which an ordinary working
	/// day is visible at all: background chatter of 30 KB/s is 0 MB/s however many decimals are
	/// drawn. It is also 0.24 Mbit/s, so the icon only lifts off the floor at about 62 KB/s —
	/// still eight times more sensitive than megabytes, which is the whole argument.
	/// </summary>
	public static string MbitWhole(double megabytesPerSecond) => Round(megabytesPerSecond * 8).ToString("0", Ci);

	/// <summary>Mbit/s for a tooltip, always with one decimal.</summary>
	public static string Mbit(double megabytesPerSecond) => (megabytesPerSecond * 8).ToString("0.0", Ci);

	/// <summary>Fan speed in thousands: 586 rpm shows as 0.6, 1240 as 1.2. Four digits do not fit
	/// into 16 pixels, and a coarser number also stops the icon repainting on every drift.</summary>
	public static string Rpm(double v) => (v / 1000).ToString("0.0", Ci);

	public static string Whole(double? v) => v.HasValue ? Round(v.Value).ToString("0", Ci) : null;

	public static string Pct(double? v) => v.HasValue ? v.Value.ToString("0", Ci) + "%" : "—";

	public static string Deg(double? v) => v.HasValue ? v.Value.ToString("0", Ci) + "°C" : "—";

	/// <summary>Severity for a fan: only a standstill is alarming, so it is 100 or 0.</summary>
	public static double Stalled(double rpm) => rpm > 0 ? 0 : 100;
}

/// <summary>
/// One field of the CSV trail. The separator is a semicolon and a device description can contain
/// one — an unquoted id then shifted every column after it, in a file whose only purpose is to be
/// read back later.
/// </summary>
internal static class Csv
{
	public static string Field(string value)
	{
		if (string.IsNullOrEmpty(value)) return "";
		if (value.IndexOfAny(new[] { ';', '"', '\r', '\n' }) < 0) return value;
		return "\"" + value.Replace("\"", "\"\"") + "\"";
	}
}

/// <summary>
/// Replaces the things that must not leave the machine with placeholders.
///
/// <c>--once</c> promises output that can be pasted into a public issue, and the main report did
/// mask the RAID serial number — but the icon layer printed slot ids, captions and tooltips, and
/// those carry the same serial, the user's own labels and, inside an unhandled error, absolute
/// paths. One promise, one place to keep it.
/// </summary>
internal sealed class Redactor
{
	/// <summary>Shorter than this and a replacement would hit ordinary words.</summary>
	private const int MinLength = 4;

	private readonly List<(string Value, string With)> _rules = new();

	public void Hide(string value, string with)
	{
		if (string.IsNullOrWhiteSpace(value) || value.Length < MinLength) return;
		var trimmed = value.Trim();
		if (_rules.Any(r => string.Equals(r.Value, trimmed, StringComparison.OrdinalIgnoreCase))) return;
		_rules.Add((trimmed, with));
	}

	public string Clean(string text)
	{
		if (string.IsNullOrEmpty(text) || _rules.Count == 0) return text;
		// Longest first: a profile path contains the user name, and replacing the name first
		// would leave a half-masked path behind.
		var result = new StringBuilder(text);
		foreach (var (value, with) in _rules.OrderByDescending(r => r.Value.Length))
			Replace(result, value, with);
		return result.ToString();
	}

	private static void Replace(StringBuilder text, string value, string with)
	{
		var at = 0;
		while (at <= text.Length - value.Length)
		{
			var found = IndexOf(text, value, at);
			if (found < 0) return;
			text.Remove(found, value.Length);
			text.Insert(found, with);
			at = found + with.Length;
		}
	}

	private static int IndexOf(StringBuilder text, string value, int from)
	{
		for (var i = from; i <= text.Length - value.Length; i++)
		{
			var hit = true;
			for (var j = 0; j < value.Length && hit; j++)
				hit = char.ToUpperInvariant(text[i + j]) == char.ToUpperInvariant(value[j]);
			if (hit) return i;
		}
		return -1;
	}
}

/// <summary>
/// The answer to <c>IOCTL_STORAGE_QUERY_PROPERTY</c> with
/// <c>StorageDeviceTemperatureProperty</c>, decoded.
///
/// Kept apart from the ioctl so the layout can be checked against a buffer instead of against a
/// disk: the previous version read the temperature out of the descriptor's reserved area — header
/// 16 and offset 18 instead of 24 and 26 — and a buffer carrying a real 47 °C decoded as 0.
/// </summary>
internal static class StorageTemperature
{
	/// <summary>Version, Size, CriticalTemperature, WarningTemperature, InfoCount, two reserved
	/// bytes and two reserved ULONGs, then the first STORAGE_TEMPERATURE_INFO.</summary>
	public const int HeaderSize = 24;

	/// <summary>Index, Temperature, OverThreshold, UnderThreshold, three BOOLEANs, a pad byte
	/// and a reserved ULONG.</summary>
	public const int EntrySize = 16;

	/// <summary>Celsius of the composite sensor — the number every tool shows for the drive —
	/// or null when the answer carries none.</summary>
	public static double? Parse(byte[] buffer, int returned)
	{
		if (buffer is null || returned < HeaderSize + EntrySize || returned > buffer.Length) return null;

		var size = BitConverter.ToUInt32(buffer, 4);
		// Size is the whole descriptor as the driver filled it. A value that does not even cover
		// one entry, or that claims more than came back, means this is not the structure we think.
		if (size < HeaderSize + EntrySize || size > returned) return null;

		var count = BitConverter.ToUInt16(buffer, 12);
		if (count == 0) return null;

		var celsius = BitConverter.ToInt16(buffer, HeaderSize + 2);
		// The driver reports SHRT_MIN for "not measured", and no disk is colder than -40 or
		// hotter than 200 while it is still answering ioctls.
		return celsius is > -40 and < 200 ? celsius : null;
	}
}
