using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace TrayMon;

/// <summary>
/// Writes the handful of events that mean hardware is failing — a RAID disk failing its SMART
/// verdict, a fan that has stopped, the UPS on battery, a volume out of space — into the Windows
/// Application log.
///
/// This is the one thing here that reaches beyond the machine's own screen. A tray icon needs
/// somebody looking at it; a server does not have one. Every monitoring system already installed
/// on such a machine reads the event log, so one line there costs nothing and is picked up by
/// tooling that exists, instead of asking anyone to add a process.
///
/// Written through ReportEvent rather than System.Diagnostics.EventLog: that class is a separate
/// NuGet package on .NET, and the README promises one executable plus a few DLLs. The source is
/// registered on first use — the program runs elevated, so it can — pointing at the same generic
/// message file eventcreate.exe uses, which is what makes the text render instead of appearing
/// as "the description for event ID cannot be found".
///
/// Transitions happen once in months. Nothing here runs on a tick.
/// </summary>
internal static class WindowsLog
{
	private const string Source = "TrayMon";
	private const ushort EventTypeWarning = 2, EventTypeError = 1;

	/// <summary>Event id 1000, the generic "information" string in EventCreate's message table.</summary>
	private const uint GenericEventId = 1000;

	[DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern IntPtr RegisterEventSourceW(string server, string source);

	[DllImport("advapi32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool DeregisterEventSource(IntPtr handle);

	[DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool ReportEventW(IntPtr handle, ushort type, ushort category, uint eventId,
											IntPtr userSid, ushort stringCount, uint dataSize,
											string[] strings, IntPtr rawData);

	private static bool _registered;

	/// <summary>Why the last write did not happen; shown in the diagnostics window.</summary>
	public static string LastError { get; private set; }

	public static void Write(string message, bool critical)
	{
		var handle = IntPtr.Zero;
		try
		{
			Register();
			handle = RegisterEventSourceW(null, Source);
			if (handle == IntPtr.Zero)
			{
				LastError = "журнал Windows: источник не открылся (код " +
							Marshal.GetLastWin32Error().ToString(System.Globalization.CultureInfo.InvariantCulture) + ")";
				return;
			}
			if (!ReportEventW(handle, critical ? EventTypeError : EventTypeWarning, 0, GenericEventId,
							  IntPtr.Zero, 1, 0, new[] { message }, IntPtr.Zero))
				LastError = "журнал Windows: запись не принята (код " +
							Marshal.GetLastWin32Error().ToString(System.Globalization.CultureInfo.InvariantCulture) + ")";
			else
				LastError = null;
		}
		catch (Exception ex)
		{
			// A monitor that cannot write a log line is still a monitor.
			LastError = "журнал Windows: " + ex.Message;
		}
		finally
		{
			if (handle != IntPtr.Zero) DeregisterEventSource(handle);
		}
	}

	/// <summary>
	/// Creates the source key if it is not there. Attempted once per run and never fatal: without
	/// it the events are still recorded, they simply render with a note that the description could
	/// not be found.
	/// </summary>
	private static void Register()
	{
		if (_registered) return;
		_registered = true;
		try
		{
			const string path = @"SYSTEM\CurrentControlSet\Services\EventLog\Application\" + Source;
			using (var existing = Registry.LocalMachine.OpenSubKey(path)) if (existing is not null) return;
			using var key = Registry.LocalMachine.CreateSubKey(path);
			if (key is null) return;
			key.SetValue("EventMessageFile",
				Path.Combine(Environment.SystemDirectory, "EventCreate.exe"), RegistryValueKind.ExpandString);
			key.SetValue("TypesSupported", 7, RegistryValueKind.DWord);   // error, warning, information
		}
		catch (Exception ex)
		{
			// Not elevated, or policy forbids it. The events still go in.
			LastError = "журнал Windows: источник не зарегистрирован — " + ex.Message;
		}
	}
}
