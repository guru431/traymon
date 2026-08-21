using System.Diagnostics;
using System.Text;

namespace TrayMon;

/// <summary>
/// Start with Windows, and a desktop shortcut for starting by hand.
///
/// Autostart is a scheduled task rather than a Startup-folder shortcut: TrayMon needs an
/// elevated token (the CPU temperature comes from an MSR driver), and a shortcut would raise
/// a UAC prompt at every logon. A task with RunLevel Highest does not.
/// </summary>
internal static class Autostart
{
	public const string TaskName = "TrayMon";

	private static string ExePath => Path.ChangeExtension(Environment.ProcessPath ?? "TrayMon.exe", ".exe");

	private static string TaskFile => Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "Tasks", TaskName);

	/// <summary>Reading the task file beats running schtasks /Query on every menu open.</summary>
	public static bool IsEnabled
	{
		get { try { return File.Exists(TaskFile); } catch (Exception) { return false; } }
	}

	public static bool Enable(out string error)
	{
		var xml = Path.Combine(Path.GetTempPath(), "TrayMon-task.xml");
		try
		{
			// schtasks /XML only accepts UTF-16.
			File.WriteAllText(xml, TaskXml(), Encoding.Unicode);
			var ok = Run("schtasks.exe", $"/Create /TN \"{TaskName}\" /XML \"{xml}\" /F", out error);
			return ok;
		}
		catch (Exception ex)
		{
			error = ex.Message;
			return false;
		}
		finally
		{
			try { File.Delete(xml); } catch (Exception) { /* temp file, never mind */ }
		}
	}

	public static bool Disable(out string error) =>
		Run("schtasks.exe", $"/Delete /TN \"{TaskName}\" /F", out error);

	/// <summary>Creates (or replaces) a shortcut to the executable.</summary>
	public static bool CreateShortcut(string folder, out string path, out string error)
	{
		path = Path.Combine(folder, "TrayMon.lnk");
		error = null;
		try
		{
			var shellType = Type.GetTypeFromProgID("WScript.Shell");
			if (shellType is null) { error = "WScript.Shell недоступен"; return false; }

			dynamic shell = Activator.CreateInstance(shellType);
			dynamic link = shell.CreateShortcut(path);
			link.TargetPath = ExePath;
			link.WorkingDirectory = Path.GetDirectoryName(ExePath);
			link.Description = "TrayMon — датчики машины в области уведомлений";
			link.IconLocation = ExePath + ",0";
			link.Save();
			return true;
		}
		catch (Exception ex)
		{
			error = ex.Message;
			return false;
		}
	}

	private static bool Run(string exe, string args, out string error)
	{
		error = null;
		try
		{
			using var p = Process.Start(new ProcessStartInfo(exe, args)
			{
				CreateNoWindow = true,
				UseShellExecute = false,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
			});
			var stdout = p.StandardOutput.ReadToEnd();
			var stderr = p.StandardError.ReadToEnd();
			p.WaitForExit(15000);
			if (p.ExitCode == 0) return true;
			error = string.IsNullOrWhiteSpace(stderr) ? stdout.Trim() : stderr.Trim();
			return false;
		}
		catch (Exception ex)
		{
			error = ex.Message;
			return false;
		}
	}

	/// <summary>
	/// Interactive logon trigger for the current user, highest privileges, no time limit,
	/// and a few restarts in case the sensor driver is not ready right after logon.
	/// </summary>
	private static string TaskXml()
	{
		var user = $"{Environment.UserDomainName}\\{Environment.UserName}";
		return $"""
			<?xml version="1.0" encoding="UTF-16"?>
			<Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
			  <RegistrationInfo>
			    <Description>Tray monitor: CPU/RAM/GPU load, temperatures and fan speeds. Source: network/scripts/TrayMon</Description>
			    <URI>\{TaskName}</URI>
			  </RegistrationInfo>
			  <Triggers>
			    <LogonTrigger>
			      <Enabled>true</Enabled>
			      <UserId>{user}</UserId>
			    </LogonTrigger>
			  </Triggers>
			  <Principals>
			    <Principal id="Author">
			      <UserId>{user}</UserId>
			      <LogonType>InteractiveToken</LogonType>
			      <RunLevel>HighestAvailable</RunLevel>
			    </Principal>
			  </Principals>
			  <Settings>
			    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
			    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
			    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
			    <AllowHardTerminate>true</AllowHardTerminate>
			    <StartWhenAvailable>true</StartWhenAvailable>
			    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
			    <IdleSettings>
			      <StopOnIdleEnd>false</StopOnIdleEnd>
			      <RestartOnIdle>false</RestartOnIdle>
			    </IdleSettings>
			    <AllowStartOnDemand>true</AllowStartOnDemand>
			    <Enabled>true</Enabled>
			    <Hidden>false</Hidden>
			    <RunOnlyIfIdle>false</RunOnlyIfIdle>
			    <DisallowStartOnRemoteAppSession>false</DisallowStartOnRemoteAppSession>
			    <UseUnifiedSchedulingEngine>true</UseUnifiedSchedulingEngine>
			    <WakeToRun>false</WakeToRun>
			    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
			    <Priority>7</Priority>
			    <RestartOnFailure>
			      <Interval>PT1M</Interval>
			      <Count>3</Count>
			    </RestartOnFailure>
			  </Settings>
			  <Actions Context="Author">
			    <Exec>
			      <Command>{ExePath}</Command>
			    </Exec>
			  </Actions>
			</Task>
			""";
	}
}
