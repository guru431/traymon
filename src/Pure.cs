using System.Globalization;

namespace TrayMon;

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

	public GuidPool(string what, Guid[] pool) { What = what; _pool = pool; }

	/// <summary>What this pool holds, for the "there was not enough room" message.</summary>
	public string What { get; }

	public int Size => _pool.Length;
	public int Used => _taken.Count;

	/// <summary>How many distinct sources were left without an icon. The limits used to be
	/// silent: twelve volumes on a server simply meant four of them did not exist, and the
	/// only place that said so was a line in the README.</summary>
	public int Refused => _refused.Count;

	public Guid? For(string key)
	{
		if (_taken.TryGetValue(key, out var known)) return known;
		foreach (var candidate in _pool)
		{
			if (_taken.ContainsValue(candidate)) continue;
			_taken[key] = candidate;
			_refused.Remove(key);
			return candidate;
		}
		if (_refused.Count < 64) _refused.Add(key);   // more sources than prepared slots
		return null;
	}

	public void Release(string key) => _taken.Remove(key);
}

/// <summary>Five minutes of one metric, for the min/avg/max line in the tooltip.</summary>
internal sealed class Stats
{
	private readonly double[] _values = new double[150];
	private int _count, _at;

	public void Add(double v)
	{
		_values[_at] = v;
		_at = (_at + 1) % _values.Length;
		if (_count < _values.Length) _count++;
	}

	public bool Ready => _count > 1;

	public (double Min, double Avg, double Max) Window()
	{
		double min = double.MaxValue, max = double.MinValue, sum = 0;
		for (var i = 0; i < _count; i++)
		{
			var v = _values[i];
			if (v < min) min = v;
			if (v > max) max = v;
			sum += v;
		}
		return (min, sum / _count, max);
	}
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

	public static double Round(double v) => Math.Round(v, 0, MidpointRounding.AwayFromZero);

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
