using System.Net.Sockets;
using System.Text;

namespace TrayMon;

/// <summary>
/// UPS charge over SNMP. Older Smart-UPS models have no network card: the APC Smart protocol
/// runs over RS-232 into PowerChute, which republishes the readings as an SNMPv1 agent — so the
/// query goes to the loopback, not to the UPS itself. Windows knows nothing about such a UPS
/// (no <c>Win32_Battery</c>, no <c>GetSystemPowerStatus</c>), which is why this goes over the wire.
///
/// One GET carries every varbind, so a reading costs a single UDP round trip. SNMP is spoken by
/// hand: a GET request and a walk over the TLVs of the answer is a fraction of the code of a
/// dependency, and only these five values are ever asked for.
/// </summary>
public sealed class UpsSensor
{
	// PowerNet MIB, enterprise 318.
	private const string CapacityOid = "1.3.6.1.4.1.318.1.1.1.2.2.1.0";   // %, Gauge32
	private const string RunTimeOid = "1.3.6.1.4.1.318.1.1.1.2.2.3.0";    // TimeTicks, hundredths of a second
	private const string StatusOid = "1.3.6.1.4.1.318.1.1.1.4.1.1.0";     // 2 = on line, 3 = on battery
	private const string LoadOid = "1.3.6.1.4.1.318.1.1.1.4.2.3.0";       // % of rated load
	private const string ReplaceOid = "1.3.6.1.4.1.318.1.1.1.2.2.4.0";    // 2 = battery needs replacing

	private const int OnBatteryStatus = 3;
	private const int NeedsReplacing = 2;

	private readonly string _host;
	private readonly int _port;
	private readonly byte[] _community;

	private int _requestId = 1;

	/// <summary>True once the agent has answered at least once; until then the icon stays hidden,
	/// exactly as the GPU icons do on a machine without an NVIDIA driver.</summary>
	public bool Present { get; private set; }

	/// <summary>Why the last query failed; shown by --once.</summary>
	public string LastError { get; private set; }

	public UpsSensor(UpsSettings settings)
	{
		_host = settings.Host;
		_port = settings.Port;
		_community = Encoding.ASCII.GetBytes(settings.Community);
	}

	/// <summary>
	/// Blocks for up to the socket timeout, so the caller runs it off the UI thread. Values are
	/// cleared when the agent goes quiet — a UPS that stopped answering is worth seeing.
	/// </summary>
	public void Read(Readings r)
	{
		Dictionary<string, (byte Tag, byte[] Raw)> answer;
		try
		{
			answer = Get(CapacityOid, RunTimeOid, StatusOid, LoadOid, ReplaceOid);
			LastError = null;
		}
		catch (Exception ex)
		{
			// Agent down, service stopped, or the community was refused — nothing to show.
			LastError = ex.GetType().Name + ": " + ex.Message;
			r.UpsCharge = null;
			r.UpsRunTimeMin = null;
			r.UpsLoad = null;
			return;
		}

		Present = true;
		r.UpsCharge = Number(answer, CapacityOid);
		var ticks = Number(answer, RunTimeOid);
		r.UpsRunTimeMin = ticks.HasValue ? ticks.Value / 6000.0 : null;   // hundredths of a second → minutes
		r.UpsLoad = Number(answer, LoadOid);
		r.UpsOnBattery = Number(answer, StatusOid) == OnBatteryStatus;
		r.UpsNeedsNewBattery = Number(answer, ReplaceOid) == NeedsReplacing;
	}

	private static double? Number(Dictionary<string, (byte Tag, byte[] Raw)> vars, string oid)
	{
		if (!vars.TryGetValue(oid, out var v)) return null;
		// INTEGER, Counter32, Gauge32 and TimeTicks are all plain big-endian integers here;
		// anything else (notably the 0x80..0x82 "no such object" markers) means no value.
		if (v.Tag is not (0x02 or 0x41 or 0x42 or 0x43)) return null;
		long n = 0;
		foreach (var b in v.Raw) n = (n << 8) | b;
		return n;
	}

	// ---- SNMPv1 ----

	private Dictionary<string, (byte Tag, byte[] Raw)> Get(params string[] oids)
	{
		var bindings = new List<byte>();
		foreach (var oid in oids)
			bindings.AddRange(Tlv(0x30, Concat(EncodeOid(oid), Tlv(0x05, Array.Empty<byte>()))));

		var pdu = Tlv(0xA0, Concat(                       // 0xA0 = GetRequest
			EncodeInt(unchecked(_requestId++)),
			EncodeInt(0),                                 // error status
			EncodeInt(0),                                 // error index
			Tlv(0x30, bindings.ToArray())));
		var message = Tlv(0x30, Concat(
			EncodeInt(0),                                 // version 1 is encoded as 0
			Tlv(0x04, _community),
			pdu));

		using var udp = new UdpClient();
		udp.Client.ReceiveTimeout = 1500;
		udp.Client.SendTimeout = 1500;
		udp.Connect(_host, _port);
		udp.Send(message, message.Length);

		var from = new System.Net.IPEndPoint(System.Net.IPAddress.Any, 0);
		var response = udp.Receive(ref from);
		return ParseVarBinds(response);
	}

	/// <summary>
	/// Walks into the response far enough to reach the varbind list. Every layer is a TLV, so
	/// "enter" means stepping past a tag and a length, and "skip" means stepping past the value
	/// as well.
	/// </summary>
	private static Dictionary<string, (byte Tag, byte[] Raw)> ParseVarBinds(byte[] buf)
	{
		var at = 0;
		Enter(buf, ref at);                     // message sequence
		Skip(buf, ref at);                      // version
		Skip(buf, ref at);                      // community
		Enter(buf, ref at);                     // response PDU
		Skip(buf, ref at);                      // request id
		var error = ReadValue(buf, ref at);     // error status
		Skip(buf, ref at);                      // error index
		var end = Enter(buf, ref at);           // varbind list

		var result = new Dictionary<string, (byte, byte[])>();
		if (error.Raw.Length > 0 && error.Raw[0] != 0) return result;   // noSuchName and friends

		while (at < end)
		{
			var bindingEnd = Enter(buf, ref at);
			var name = ReadValue(buf, ref at);
			var value = ReadValue(buf, ref at);
			result[DecodeOid(name.Raw)] = value;
			at = bindingEnd;
		}
		return result;
	}

	/// <summary>Steps past a tag and its length, returning where the value ends.</summary>
	private static int Enter(byte[] buf, ref int at)
	{
		at++;
		var length = ReadLength(buf, ref at);
		return at + length;
	}

	private static void Skip(byte[] buf, ref int at) => at = Enter(buf, ref at);

	private static (byte Tag, byte[] Raw) ReadValue(byte[] buf, ref int at)
	{
		var tag = buf[at];
		at++;
		var length = ReadLength(buf, ref at);
		var raw = new byte[length];
		Array.Copy(buf, at, raw, 0, length);
		at += length;
		return (tag, raw);
	}

	private static int ReadLength(byte[] buf, ref int at)
	{
		var first = buf[at];
		at++;
		if (first < 0x80) return first;
		var count = first & 0x7F;
		var length = 0;
		for (var i = 0; i < count; i++) { length = (length << 8) | buf[at]; at++; }
		return length;
	}

	private static string DecodeOid(byte[] raw)
	{
		if (raw.Length == 0) return "";
		var parts = new List<long> { raw[0] / 40, raw[0] % 40 };
		long value = 0;
		for (var i = 1; i < raw.Length; i++)
		{
			value = (value << 7) | (uint)(raw[i] & 0x7F);
			if ((raw[i] & 0x80) == 0) { parts.Add(value); value = 0; }
		}
		return string.Join('.', parts);
	}

	private static byte[] EncodeOid(string oid)
	{
		var parts = oid.Split('.').Select(uint.Parse).ToArray();
		var body = new List<byte> { (byte)(parts[0] * 40 + parts[1]) };
		for (var i = 2; i < parts.Length; i++)
		{
			var group = new List<byte>();
			var value = parts[i];
			group.Add((byte)(value & 0x7F));
			value >>= 7;
			while (value > 0) { group.Insert(0, (byte)((value & 0x7F) | 0x80)); value >>= 7; }
			body.AddRange(group);
		}
		return Tlv(0x06, body.ToArray());
	}

	private static byte[] EncodeInt(int value)
	{
		var body = new List<byte>();
		if (value == 0) body.Add(0);
		else
		{
			var v = value;
			while (v > 0) { body.Insert(0, (byte)(v & 0xFF)); v >>= 8; }
			if (body[0] >= 0x80) body.Insert(0, 0);   // keep it positive
		}
		return Tlv(0x02, body.ToArray());
	}

	private static byte[] Tlv(byte tag, byte[] payload)
	{
		var result = new List<byte> { tag };
		if (payload.Length < 0x80) result.Add((byte)payload.Length);
		else
		{
			var length = new List<byte>();
			var v = payload.Length;
			while (v > 0) { length.Insert(0, (byte)(v & 0xFF)); v >>= 8; }
			result.Add((byte)(0x80 | length.Count));
			result.AddRange(length);
		}
		result.AddRange(payload);
		return result.ToArray();
	}

	private static byte[] Concat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();
}
