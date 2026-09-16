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
///
/// The answer is treated as hostile input. Windows has no privileged ports, so any process of
/// any user can occupy UDP 127.0.0.1:161 while the real agent is stopped and reply whatever it
/// likes to this one, which runs elevated.
///
/// What the checks here do and do not buy is worth being precise about. A process squatting on
/// the port receives the GetRequest itself, so it holds the community, the request id and the
/// OIDs: checking the PDU type, the id and the community only keeps out *other* datagrams —
/// stale replies and stray traffic — not a squatter. Against a squatter the defence is that
/// there is nothing to attack: the parser is bounded so it cannot be hung or crashed by what
/// comes back, and the worst a forgery can achieve is to lie about the battery.
/// </summary>
public sealed class UpsSensor
{
	// PowerNet MIB, enterprise 318.
	private const string CapacityOid = "1.3.6.1.4.1.318.1.1.1.2.2.1.0";   // %, Gauge32
	private const string RunTimeOid = "1.3.6.1.4.1.318.1.1.1.2.2.3.0";    // TimeTicks, hundredths of a second
	private const string StatusOid = "1.3.6.1.4.1.318.1.1.1.4.1.1.0";     // 2 = on line, 3 = on battery
	private const string LoadOid = "1.3.6.1.4.1.318.1.1.1.4.2.3.0";       // % of rated load
	private const string ReplaceOid = "1.3.6.1.4.1.318.1.1.1.2.2.4.0";    // 2 = battery needs replacing

	private const int NeedsReplacing = 2;

	/// <summary>
	/// SNMPv1 <c>noSuchName</c> — the one error that means "this agent does not carry that OID"
	/// and therefore the one that justifies dropping a varbind for good. Everything else
	/// (<c>tooBig</c>, <c>genErr</c>) is the agent having a bad moment, and treating those the same
	/// way permanently removed the battery charge or the power status from a UPS that had both.
	/// </summary>
	private const int NoSuchName = 2;

	private const byte GetResponse = 0xA2;

	private readonly string _host;
	private readonly int _port;
	private readonly int _timeoutMs;
	private readonly byte[] _community;

	/// <summary>
	/// The varbinds still being asked for. An SNMPv1 GET is all or nothing: an agent that does
	/// not carry one of these OIDs — Back-UPS models and PowerChute Personal have no output load,
	/// non-APC agents have none of them — answers noSuchName and returns *no* values at all. The
	/// icon then said "no answer from the SNMP agent" about an agent that had answered. The
	/// offending varbind is named by the error index, so it is dropped and remembered.
	/// </summary>
	private readonly List<string> _oids = new()
		{ CapacityOid, RunTimeOid, StatusOid, LoadOid, ReplaceOid };

	private static readonly string[] AllOids =
		{ CapacityOid, RunTimeOid, StatusOid, LoadOid, ReplaceOid };

	private readonly List<string> _dropped = new();

	private int _requestId = 1;

	/// <summary>True once the agent has answered at least once; until then the icon stays hidden,
	/// exactly as the GPU icons do on a machine without an NVIDIA driver.</summary>
	public bool Present { get; private set; }

	/// <summary>Why the last query failed; shown by --once and by the diagnostics window.</summary>
	public string LastError { get; private set; }

	public string Endpoint => $"{_host}:{_port}";

	/// <summary>True when the agent answered but has no battery-charge OID — almost always an
	/// agent that does not speak the APC PowerNet MIB at all.</summary>
	public bool ChargeUnavailable => _dropped.Contains(CapacityOid);

	public UpsSensor(UpsSettings settings)
	{
		_host = settings.Host;
		_port = settings.Port;
		_timeoutMs = settings.TimeoutMs;
		_community = Encoding.ASCII.GetBytes(settings.Community);
	}

	/// <summary>
	/// Blocks for up to the socket timeout, so the caller runs it off the UI thread. The whole
	/// reading is published as one object: the charge and the "on battery" flag decide the colour
	/// of the plate together, and mixing halves of two different answers is exactly how a red
	/// plate appears next to a battery that is full.
	/// </summary>
	public void Read(Readings r)
	{
		Dictionary<string, (byte Tag, byte[] Raw)> answer = null;
		try
		{
			// At most three passes: each failed one drops exactly the varbind the agent named.
			for (var attempt = 0; attempt < 3 && answer is null; attempt++)
			{
				var reply = Get(_oids);
				if (reply.Error == 0) { answer = reply.Vars; break; }
				// Only noSuchName removes a varbind. Any non-zero status used to, so one bad
				// moment from the agent could cost the charge or the power status for the rest of
				// the session — and nothing, not even "Опросить датчики сейчас", brought it back.
				if (reply.Error == NoSuchName &&
					reply.ErrorIndex >= 1 && reply.ErrorIndex <= _oids.Count && _oids.Count > 1)
				{
					_dropped.Add(_oids[reply.ErrorIndex - 1]);
					_oids.RemoveAt(reply.ErrorIndex - 1);
					continue;
				}
				throw new InvalidOperationException(
					$"агент вернул ошибку SNMP {reply.Error.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
			}
			if (answer is null) throw new InvalidOperationException("агент не отдал ни одного из запрошенных OID");
			LastError = _dropped.Count == 0
				? null
				: $"агент не отдаёт {_dropped.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)} " +
				  "из пяти значений — они не показываются";
		}
		catch (Exception ex)
		{
			// Agent down, service stopped, or the community was refused. Nothing is known now —
			// including whether the UPS is on battery, so that flag goes back to "unknown"
			// rather than staying at whatever the last answer said.
			LastError = ex is SocketException { SocketErrorCode: SocketError.ConnectionReset }
				? "порт закрыт: служба SNMP-агента не запущена"
				: ex.GetType().Name + ": " + ex.Message;
			r.Ups = UpsReading.Silent;
			return;
		}

		Present = true;
		var ticks = InRange(Number(answer, RunTimeOid), 0, 100 * 60 * 60 * 100);   // up to 100 hours
		var status = InRange(Number(answer, StatusOid), 1, 255);
		r.Ups = new UpsReading
		{
			Answered = true,
			// Range-checked, not merely parsed. A gauge of -1 or 200 is not a charge, and it went
			// straight onto the plate and into the five-minute window as if it had been measured;
			// the parser only ever asked whether the bytes were an integer.
			Charge = InRange(Number(answer, CapacityOid), 0, 100),
			RunTimeMin = ticks.HasValue ? ticks.Value / 6000.0 : null,   // hundredths of a second → minutes
			Load = InRange(Number(answer, LoadOid), 0, 200),
			Status = status.HasValue ? (int)status.Value : null,
			NeedsNewBattery = Number(answer, ReplaceOid) == NeedsReplacing,
		};
	}

	/// <summary>
	/// Asks for the full set of OIDs again. What an agent carries changes when PowerChute is
	/// upgraded or the UPS is replaced, and a varbind dropped as noSuchName had no way back.
	/// </summary>
	public void Rediscover()
	{
		_dropped.Clear();
		_oids.Clear();
		_oids.AddRange(AllOids);
	}

	private static double? InRange(double? value, double min, double max) =>
		value.HasValue && value.Value >= min && value.Value <= max ? value : null;

	private static double? Number(Dictionary<string, (byte Tag, byte[] Raw)> vars, string oid)
	{
		if (!vars.TryGetValue(oid, out var v)) return null;
		// INTEGER, Counter32, Gauge32 and TimeTicks are all plain big-endian integers here;
		// anything else (notably the 0x80..0x82 "no such object" markers) means no value.
		if (v.Tag is not (0x02 or 0x41 or 0x42 or 0x43)) return null;
		if (v.Raw.Length == 0 || v.Raw.Length > 8) return null;   // nothing this MIB carries is wider
		// INTEGER is signed in BER; the gauge types are not.
		long n = v.Tag == 0x02 && (v.Raw[0] & 0x80) != 0 ? -1 : 0;
		foreach (var b in v.Raw) n = (n << 8) | b;
		return n;
	}

	// ---- SNMPv1 ----

	/// <summary>One parsed GetResponse: the varbinds, plus which one the agent objected to.</summary>
	internal sealed class SnmpReply
	{
		public int Error;
		public int ErrorIndex;
		public Dictionary<string, (byte Tag, byte[] Raw)> Vars;
	}

	private SnmpReply Get(List<string> oids)
	{
		var bindings = new List<byte>();
		foreach (var oid in oids)
			bindings.AddRange(Tlv(0x30, Concat(EncodeOid(oid), Tlv(0x05, Array.Empty<byte>()))));

		// Kept positive: EncodeInt only writes positive integers, and a wrapped negative id
		// would go out as 0 and match every stale answer.
		var requestId = _requestId;
		_requestId = _requestId >= int.MaxValue - 1 ? 1 : _requestId + 1;
		var pdu = Tlv(0xA0, Concat(                       // 0xA0 = GetRequest
			EncodeInt(requestId),
			EncodeInt(0),                                 // error status
			EncodeInt(0),                                 // error index
			Tlv(0x30, bindings.ToArray())));
		var message = Tlv(0x30, Concat(
			EncodeInt(0),                                 // version 1 is encoded as 0
			Tlv(0x04, _community),
			pdu));

		using var udp = new UdpClient();
		udp.Client.SendTimeout = _timeoutMs;
		udp.Connect(_host, _port);
		udp.Send(message, message.Length);

		// Keep reading until the deadline: a datagram that is not an answer to this request —
		// a stale reply, or a forgery from something squatting on the port — is dropped and the
		// wait continues, instead of being taken for the reading.
		var from = new System.Net.IPEndPoint(System.Net.IPAddress.Any, 0);
		var deadline = Environment.TickCount64 + _timeoutMs;
		while (true)
		{
			var left = (int)(deadline - Environment.TickCount64);
			if (left <= 0) throw new TimeoutException($"агент не ответил за {_timeoutMs} мс");
			udp.Client.ReceiveTimeout = left;

			byte[] response;
			try { response = udp.Receive(ref from); }
			catch (SocketException e) when (e.SocketErrorCode == SocketError.ConnectionReset)
			{
				// ICMP port unreachable: nothing is listening. Reporting that as "the agent did
				// not answer in 1500 ms" sent people looking for a slow network.
				throw;
			}
			catch (SocketException) { throw new TimeoutException($"агент не ответил за {_timeoutMs} мс"); }

			var parsed = ParseVarBinds(response, requestId);
			if (parsed is not null) return parsed;
		}
	}

	/// <summary>
	/// Walks into the response far enough to reach the varbind list. Every layer is a TLV, so
	/// "enter" means stepping past a tag and a length, and "skip" means stepping past the value
	/// as well. Returns null when the datagram is not a well-formed answer to <paramref name="requestId"/>
	/// — the caller keeps waiting rather than believing it.
	/// </summary>
	/// <remarks>
	/// Internal rather than private so the tests can throw crafted datagrams at it directly —
	/// including the one whose negative TLV length used to walk the cursor backwards and hold a
	/// logical processor until the process was killed. That is not a bug a tray can demonstrate.
	/// </remarks>
	internal SnmpReply ParseVarBinds(byte[] buf, int requestId)
	{
		try
		{
			var at = 0;
			var limit = buf.Length;
			var messageEnd = Enter(buf, ref at, limit, out var messageTag);
			if (messageTag != 0x30) return null;
			limit = Math.Min(limit, messageEnd);

			Skip(buf, ref at, limit);                            // version
			var community = ReadValue(buf, ref at, limit);       // community
			if (!community.Raw.AsSpan().SequenceEqual(_community)) return null;

			var pduEnd = Enter(buf, ref at, limit, out var pduTag);
			if (pduTag != GetResponse) return null;               // a trap or a request, not our answer
			limit = Math.Min(limit, pduEnd);

			var id = ReadValue(buf, ref at, limit);               // request id
			if (SignedOf(id) != requestId) return null;           // an answer to some earlier query

			var error = ReadValue(buf, ref at, limit);            // error status
			var index = ReadValue(buf, ref at, limit);            // error index — which varbind
			var end = Enter(buf, ref at, limit, out var listTag);
			if (listTag != 0x30) return null;
			end = Math.Min(end, limit);

			var reply = new SnmpReply
			{
				Error = (int)Math.Clamp(SignedOf(error), 0, 255),
				// 1-based, and it is the whole point of the retry: it names the OID this agent
				// does not carry, so the next request can go out without it.
				ErrorIndex = (int)Math.Clamp(SignedOf(index), 0, 255),
				Vars = new Dictionary<string, (byte, byte[])>(),
			};
			if (reply.Error != 0) return reply;   // noSuchName and friends: no values at all

			while (at < end)
			{
				var start = at;
				var bindingEnd = Enter(buf, ref at, end, out var bindTag);
				// Forward progress is required. A length field that decodes to a point at or
				// before the cursor would otherwise send this loop round for ever, on a thread
				// with no timeout and no way in to stop it — one crafted datagram was enough to
				// hold a whole logical processor until the process was killed.
				if (bindTag != 0x30 || bindingEnd <= start || bindingEnd > end) return null;
				var name = ReadValue(buf, ref at, bindingEnd);
				var value = ReadValue(buf, ref at, bindingEnd);
				reply.Vars[DecodeOid(name.Raw)] = value;
				at = bindingEnd;
			}
			return reply;
		}
		catch (FormatException)
		{
			return null;   // truncated or malformed — not an answer we can use
		}
	}

	private static long SignedOf((byte Tag, byte[] Raw) value)
	{
		if (value.Raw.Length == 0 || value.Raw.Length > 8) return long.MinValue;
		long n = (value.Raw[0] & 0x80) != 0 ? -1 : 0;
		foreach (var b in value.Raw) n = (n << 8) | b;
		return n;
	}

	/// <summary>Steps past a tag and its length, returning where the value ends.</summary>
	private static int Enter(byte[] buf, ref int at, int limit, out byte tag)
	{
		Need(at, 1, limit);
		tag = buf[at];
		at++;
		var length = ReadLength(buf, ref at, limit);
		return at + length;
	}

	private static void Skip(byte[] buf, ref int at, int limit) => at = Enter(buf, ref at, limit, out _);

	private static (byte Tag, byte[] Raw) ReadValue(byte[] buf, ref int at, int limit)
	{
		Need(at, 1, limit);
		var tag = buf[at];
		at++;
		var length = ReadLength(buf, ref at, limit);
		var raw = new byte[length];
		Array.Copy(buf, at, raw, 0, length);
		at += length;
		return (tag, raw);
	}

	/// <summary>
	/// The one place a length is decoded, so the one place it has to be checked. The long form
	/// can name up to 127 bytes of length; accumulating those into an int silently overflows
	/// into a negative number, and a negative length walks the cursor backwards.
	/// </summary>
	private static int ReadLength(byte[] buf, ref int at, int limit)
	{
		Need(at, 1, limit);
		var first = buf[at];
		at++;
		if (first < 0x80) { Need(at, first, limit); return first; }

		var count = first & 0x7F;
		if (count is 0 or > 4) throw new FormatException("BER: длина в " + count + " байт");
		Need(at, count, limit);
		long length = 0;
		for (var i = 0; i < count; i++) { length = (length << 8) | buf[at]; at++; }
		if (length < 0 || length > limit - at) throw new FormatException("BER: длина выходит за пакет");
		return (int)length;
	}

	private static void Need(int at, int count, int limit)
	{
		if (at < 0 || count < 0 || at > limit - count) throw new FormatException("BER: чтение за границей пакета");
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
			else if (value > int.MaxValue) return "";   // a sub-identifier this long is not an OID we asked for
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
