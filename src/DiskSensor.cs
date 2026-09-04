using System.Runtime.InteropServices;
using System.Text;

namespace TrayMon;

/// <summary>
/// Temperature of every directly attached disk, straight from the storage driver.
///
/// The sensor library can do this too, but only with an elevated token and only through the
/// ring-0 driver that HVCI and Defender now refuse to load. <c>IOCTL_STORAGE_QUERY_PROPERTY</c>
/// with <c>StorageDeviceTemperatureProperty</c> asks the same question of the same drive through
/// the ordinary storage stack: no kernel driver, and no elevation either — the device is opened
/// with zero desired access, which is enough for a property query and is granted to anyone.
///
/// Wear figures still come from the sensor library (they are an NVMe log page, not a property),
/// so the two are merged by model name where both answer.
/// </summary>
public sealed class DiskSensor
{
	private const uint IoctlStorageQueryProperty = 0x2D1400;
	private const uint OpenExisting = 3;
	private const uint FileShareRead = 1, FileShareWrite = 2;

	private const uint StorageDeviceProperty = 0;
	private const uint StorageDeviceTemperatureProperty = 77;
	private const uint PropertyStandardQuery = 0;

	/// <summary>More than any machine this program is aimed at, and a bound on the probe loop.</summary>
	private const int MaxDrives = 32;

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern IntPtr CreateFileW(string name, uint access, uint share, IntPtr security,
											 uint disposition, uint flags, IntPtr template);

	[DllImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool CloseHandle(IntPtr handle);

	[DllImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool DeviceIoControl(IntPtr device, uint code, byte[] input, int inputSize,
											   byte[] output, int outputSize, out int returned, IntPtr overlapped);

	/// <summary>Why the last pass found nothing, if it found nothing.</summary>
	public string LastError { get; private set; }

	/// <summary>
	/// Every physical drive that answers with a temperature. Costs one open and two ioctls per
	/// drive, so it belongs on the slow schedule with the rest of the SMART traffic.
	/// </summary>
	public List<DiskReading> Read()
	{
		var disks = new List<DiskReading>();
		var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		string lastFailure = null;
		var opened = 0;

		for (var i = 0; i < MaxDrives; i++)
		{
			var handle = IntPtr.Zero;
			try
			{
				// Zero desired access: a property query needs no read right, and asking for one
				// would make this require administrator for no reason at all.
				handle = CreateFileW($@"\\.\PhysicalDrive{i.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
									 0, FileShareRead | FileShareWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
				if (handle == IntPtr.Zero || handle == new IntPtr(-1)) continue;
				opened++;

				var temp = Temperature(handle);
				if (temp is null) continue;
				var name = Model(handle) ?? "Диск " + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
				// The same disk can appear twice behind a multipath or virtual controller.
				if (!seen.Add(name + "|" + temp.Value.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + "|" + i))
					continue;
				disks.Add(new DiskReading(name, temp.Value, null, null));
			}
			catch (Exception ex)
			{
				lastFailure = ex.GetType().Name + ": " + ex.Message;
			}
			finally
			{
				if (handle != IntPtr.Zero && handle != new IntPtr(-1)) CloseHandle(handle);
			}
		}

		// How many drives answered at all, not just how many had a temperature: "no disks here"
		// and "these disks do not report temperature" are different problems, and a virtual disk
		// under a hypervisor is always the second one.
		LastError = disks.Count > 0
			? null
			: lastFailure ?? $"дисков опрошено {opened.ToString(System.Globalization.CultureInfo.InvariantCulture)}, " +
							 "температуру не отдал ни один";
		return disks;
	}

	private static double? Temperature(IntPtr handle)
	{
		var output = new byte[512];
		if (!Query(handle, StorageDeviceTemperatureProperty, output, out var returned)) return null;
		// STORAGE_TEMPERATURE_DATA_DESCRIPTOR: Version, Size, CriticalTemperature,
		// WarningTemperature, InfoCount, 2 reserved bytes, then InfoCount × STORAGE_TEMPERATURE_INFO.
		const int header = 16, entry = 16;
		if (returned < header + entry) return null;
		var count = BitConverter.ToUInt16(output, 12);
		if (count == 0) return null;
		// Entry 0 is the composite temperature — the number every tool shows for the drive.
		var celsius = BitConverter.ToInt16(output, header + 2);
		// The driver reports SHRT_MIN for "not measured", and no disk is colder than -40 or
		// hotter than 200 while it is still answering ioctls.
		return celsius is > -40 and < 200 ? celsius : null;
	}

	private static string Model(IntPtr handle)
	{
		var output = new byte[1024];
		if (!Query(handle, StorageDeviceProperty, output, out var returned) || returned < 36) return null;
		// STORAGE_DEVICE_DESCRIPTOR: VendorIdOffset at 12, ProductIdOffset at 16 — both offsets
		// into this same buffer, or 0 when the driver did not supply the string.
		var vendor = AnsiAt(output, BitConverter.ToInt32(output, 12), returned);
		var product = AnsiAt(output, BitConverter.ToInt32(output, 16), returned);
		var name = string.Join(' ', new[] { vendor, product }.Where(s => !string.IsNullOrWhiteSpace(s))).Trim();
		return string.IsNullOrWhiteSpace(name) ? null : name;
	}

	private static string AnsiAt(byte[] buffer, int offset, int limit)
	{
		if (offset <= 0 || offset >= limit) return null;
		var end = offset;
		while (end < limit && buffer[end] != 0) end++;
		return Encoding.ASCII.GetString(buffer, offset, end - offset).Trim();
	}

	private static bool Query(IntPtr handle, uint propertyId, byte[] output, out int returned)
	{
		// STORAGE_PROPERTY_QUERY is PropertyId, QueryType and a variable tail this call does not
		// use; sixteen zeroed bytes cover it on every driver.
		var input = new byte[16];
		BitConverter.GetBytes(propertyId).CopyTo(input, 0);
		BitConverter.GetBytes(PropertyStandardQuery).CopyTo(input, 4);
		return DeviceIoControl(handle, IoctlStorageQueryProperty, input, input.Length,
							   output, output.Length, out returned, IntPtr.Zero) && returned > 0;
	}
}
