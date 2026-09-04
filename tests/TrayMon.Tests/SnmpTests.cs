using System.Text;
using TrayMon;
using Xunit;

namespace TrayMon.Tests;

/// <summary>
/// The SNMP answer is hostile input by design: Windows has no privileged ports, so anything
/// running as any user can occupy 127.0.0.1:161 while the real agent is stopped and reply to a
/// process holding an elevated token. One crafted datagram has already held a logical processor
/// until the process was killed, and that is not a failure a tray icon can demonstrate.
/// </summary>
public sealed class SnmpParserTests
{
	private const string Community = "public";
	private const string CapacityOid = "1.3.6.1.4.1.318.1.1.1.2.2.1.0";

	private static UpsSensor Sensor() => new(new UpsSettings { Community = Community });

	// ---- a minimal BER writer, so the tests can build exactly the packet they mean ----

	private static byte[] Tlv(byte tag, params byte[][] parts)
	{
		var payload = parts.SelectMany(p => p).ToArray();
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

	private static byte[] Int(int value)
	{
		if (value == 0) return Tlv(0x02, new byte[] { 0 });
		var body = new List<byte>();
		var v = value;
		while (v > 0) { body.Insert(0, (byte)(v & 0xFF)); v >>= 8; }
		if (body[0] >= 0x80) body.Insert(0, 0);
		return Tlv(0x02, body.ToArray());
	}

	private static byte[] Oid(string oid)
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

	private static byte[] Response(int requestId, int error, int errorIndex, string community,
								   params byte[][] varbinds) =>
		Tlv(0x30,
			Int(0),                                        // version 1
			Tlv(0x04, Encoding.ASCII.GetBytes(community)),
			Tlv(0xA2,                                      // GetResponse
				Int(requestId), Int(error), Int(errorIndex),
				Tlv(0x30, varbinds.SelectMany(v => v).ToArray())));

	private static byte[] Gauge(string oid, int value) =>
		Tlv(0x30, Oid(oid), Tlv(0x42, new[] { (byte)value }));

	// ---- what a good answer must do ----

	[Fact]
	public void ReadsAGaugeOutOfAWellFormedAnswer()
	{
		var reply = Sensor().ParseVarBinds(Response(7, 0, 0, Community, Gauge(CapacityOid, 61)), 7);
		Assert.NotNull(reply);
		Assert.Equal(0, reply.Error);
		Assert.True(reply.Vars.ContainsKey(CapacityOid));
		Assert.Equal(0x42, reply.Vars[CapacityOid].Tag);
	}

	/// <summary>noSuchName names the varbind in the error index — the retry depends on it.</summary>
	[Fact]
	public void CarriesTheErrorIndexBack()
	{
		var reply = Sensor().ParseVarBinds(Response(7, 2, 4, Community), 7);
		Assert.NotNull(reply);
		Assert.Equal(2, reply.Error);
		Assert.Equal(4, reply.ErrorIndex);
		Assert.Empty(reply.Vars);
	}

	// ---- what a hostile or stale answer must not do ----

	[Fact]
	public void RejectsAnAnswerToSomebodyElsesRequest() =>
		Assert.Null(Sensor().ParseVarBinds(Response(8, 0, 0, Community, Gauge(CapacityOid, 61)), 7));

	[Fact]
	public void RejectsTheWrongCommunity() =>
		Assert.Null(Sensor().ParseVarBinds(Response(7, 0, 0, "private", Gauge(CapacityOid, 61)), 7));

	[Fact]
	public void RejectsARequestPretendingToBeAnAnswer()
	{
		var asRequest = Tlv(0x30,
			Int(0), Tlv(0x04, Encoding.ASCII.GetBytes(Community)),
			Tlv(0xA0, Int(7), Int(0), Int(0), Tlv(0x30, Gauge(CapacityOid, 61))));
		Assert.Null(Sensor().ParseVarBinds(asRequest, 7));
	}

	[Fact]
	public void RejectsATruncatedDatagram()
	{
		var full = Response(7, 0, 0, Community, Gauge(CapacityOid, 61));
		for (var cut = 1; cut < full.Length; cut++)
			Assert.Null(Sensor().ParseVarBinds(full.Take(cut).ToArray(), 7));
	}

	/// <summary>
	/// The packet that used to hang a thread: a length in long form that overflows an int and
	/// decodes negative, walking the cursor backwards so the varbind loop never advanced. There
	/// was no timeout on that thread and no way in to stop it.
	/// </summary>
	[Fact]
	public void ALengthThatWouldWalkTheCursorBackwardsIsRefused()
	{
		var crafted = new byte[] { 0x30, 0x84, 0xFF, 0xFF, 0xFF, 0xFF, 0x02, 0x01, 0x00 };
		Assert.Null(Sensor().ParseVarBinds(crafted, 7));
	}

	[Fact]
	public void ALengthFieldLongerThanFourBytesIsRefused()
	{
		var crafted = new byte[] { 0x30, 0x88, 1, 1, 1, 1, 1, 1, 1, 1, 0x02, 0x01, 0x00 };
		Assert.Null(Sensor().ParseVarBinds(crafted, 7));
	}

	[Fact]
	public void EmptyAndGarbageDatagramsAreRefused()
	{
		var sensor = Sensor();
		Assert.Null(sensor.ParseVarBinds(Array.Empty<byte>(), 7));
		Assert.Null(sensor.ParseVarBinds(new byte[] { 0xFF, 0xFF, 0xFF }, 7));
	}

	/// <summary>
	/// Random noise, in bulk. Nothing here may throw out of the parser or fail to return, whatever
	/// arrives on the socket.
	/// </summary>
	[Fact]
	public void RandomNoiseNeverThrowsAndAlwaysReturns()
	{
		var sensor = Sensor();
		var random = new Random(20260903);
		for (var i = 0; i < 5000; i++)
		{
			var buffer = new byte[random.Next(1, 96)];
			random.NextBytes(buffer);
			sensor.ParseVarBinds(buffer, 7);   // must simply come back
		}
	}

	/// <summary>Bit-flipped versions of a valid packet: the same, from a much likelier direction.</summary>
	[Fact]
	public void MutatedValidPacketsNeverThrowAndAlwaysReturn()
	{
		var sensor = Sensor();
		var good = Response(7, 0, 0, Community, Gauge(CapacityOid, 61));
		var random = new Random(20260904);
		for (var i = 0; i < 5000; i++)
		{
			var buffer = (byte[])good.Clone();
			for (var flips = random.Next(1, 4); flips > 0; flips--)
				buffer[random.Next(buffer.Length)] = (byte)random.Next(256);
			sensor.ParseVarBinds(buffer, 7);
		}
	}
}

/// <summary>The PowerNet status codes, which decide the colour of the plate.</summary>
public sealed class UpsStatusTests
{
	[Theory]
	[InlineData(3, true)]     // onBattery
	[InlineData(15, true)]    // onBatteryTest
	[InlineData(2, false)]    // onLine
	[InlineData(4, false)]    // AVR boost — not battery, but not healthy either
	[InlineData(10, false)]   // hardware failure bypass
	public void OnBatteryIsOnlyTheTwoStatesThatAre(int status, bool expected) =>
		Assert.Equal(expected, new UpsReading { Status = status }.OnBattery);

	/// <summary>
	/// unknown(1) and a missing OID both mean "nothing is known". Reading either as mains power
	/// is the same mistake in both directions: a green plate over a UPS nobody has heard from.
	/// </summary>
	[Theory]
	[InlineData(1)]
	[InlineData(null)]
	public void UnknownIsNotOnLine(int? status) =>
		Assert.Null(new UpsReading { Status = status }.OnBattery);

	[Theory]
	[InlineData(4)]  [InlineData(6)]  [InlineData(7)]  [InlineData(8)]
	[InlineData(9)]  [InlineData(10)] [InlineData(12)] [InlineData(16)] [InlineData(17)]
	public void BypassAndAvrCountAsDegraded(int status) =>
		Assert.True(new UpsReading { Status = status }.Degraded);

	[Theory]
	[InlineData(2)] [InlineData(3)] [InlineData(13)] [InlineData(14)]
	public void NormalRunningStatesAreNotDegraded(int status) =>
		Assert.False(new UpsReading { Status = status }.Degraded);
}
