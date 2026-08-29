using System.Diagnostics;
using System.Security;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;

namespace TrayMon;

/// <summary>
/// Start with Windows, and a desktop shortcut for starting by hand.
///
/// Autostart is a scheduled task rather than a Startup-folder shortcut: TrayMon needs an
/// elevated token (the CPU temperature comes from an MSR driver), and a shortcut would raise
/// a UAC prompt at every logon. A task with RunLevel HighestAvailable does not.
///
/// That is also why the folder is checked first. The task is the one thing here that grants a
/// standing, prompt-free elevation to whatever sits at a path; if an ordinary process of the
/// same user can overwrite that path, the task will happily start the replacement.
/// </summary>
internal static class Autostart
{
	public const string TaskName = "TrayMon";

	private static string ExePath => Path.ChangeExtension(Environment.ProcessPath ?? "TrayMon.exe", ".exe");

	private static string TaskFile => Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "Tasks", TaskName);

	/// <summary>
	/// True when a task exists *and* it starts this executable. The file alone is not enough:
	/// the task holds an absolute path, so after the exe is moved the task still exists and
	/// points at nothing, while the menu tick claimed everything was fine.
	/// </summary>
	public static bool IsEnabled
	{
		get
		{
			try
			{
				if (!File.Exists(TaskFile)) return false;
				var command = CommandInTask();
				return command is null || string.Equals(command, ExePath, StringComparison.OrdinalIgnoreCase);
			}
			catch (Exception) { return false; }
		}
	}

	/// <summary>True when a task exists but launches some other path — a moved or copied exe.</summary>
	public static bool PointsElsewhere(out string command)
	{
		command = null;
		try
		{
			if (!File.Exists(TaskFile)) return false;
			command = CommandInTask();
			return command is not null && !string.Equals(command, ExePath, StringComparison.OrdinalIgnoreCase);
		}
		catch (Exception) { return false; }
	}

	private static string CommandInTask()
	{
		var xml = File.ReadAllText(TaskFile);
		var open = xml.IndexOf("<Command>", StringComparison.OrdinalIgnoreCase);
		var close = xml.IndexOf("</Command>", StringComparison.OrdinalIgnoreCase);
		if (open < 0 || close <= open) return null;
		open += "<Command>".Length;
		return xml.Substring(open, close - open).Trim().Trim('"');
	}

	public static bool Enable(out string error)
	{
		// A task started with the highest available token and no prompt is exactly the asset an
		// unprivileged process would like to point somewhere else. If the folder lets anyone but
		// administrators write, creating that task hands them elevation, so it is refused.
		if (WritableByNonAdmins(Path.GetDirectoryName(ExePath), out var who))
		{
			error =
				$"Папка {Path.GetDirectoryName(ExePath)} доступна на запись ({who}).\n" +
				"Задача запускает TrayMon с правами администратора и без запроса UAC, поэтому\n" +
				"любой процесс этого пользователя смог бы подменить exe и получить эти права.\n" +
				"Перенесите программу в %ProgramFiles%\\TrayMon и включите автозапуск оттуда.";
			return false;
		}

		var xml = Path.Combine(Path.GetTempPath(), $"TrayMon-task-{Guid.NewGuid():N}.xml");
		try
		{
			// schtasks /XML only accepts UTF-16. The file name carries a fresh GUID: a fixed one
			// in %TEMP% is writable by anything running as this user, and it is read back by a
			// process holding an elevated token.
			File.WriteAllText(xml, TaskXml(), Encoding.Unicode);
			return Run("schtasks.exe", $"/Create /TN \"{TaskName}\" /XML \"{xml}\" /F", out error);
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

	public static string ShortcutPath => Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "TrayMon.lnk");

	/// <summary>
	/// Undoes everything the program is able to create: the scheduled task, the desktop
	/// shortcut and the tray positions Windows remembers for our icons. The settings file is
	/// left to the caller to ask about. Returns a line per action for the report.
	/// </summary>
	public static List<string> Uninstall(bool alsoSettings)
	{
		var report = new List<string>();

		if (File.Exists(TaskFile))
			report.Add(Disable(out var taskError) ? "Задача планировщика удалена" : "Задача планировщика: " + taskError);
		else
			report.Add("Задачи планировщика не было");

		try
		{
			if (File.Exists(ShortcutPath)) { File.Delete(ShortcutPath); report.Add("Ярлык на рабочем столе удалён"); }
			else report.Add("Ярлыка на рабочем столе не было");
		}
		catch (Exception ex) { report.Add("Ярлык: " + ex.Message); }

		report.Add($"Позиции значков в реестре: удалено записей — {ForgetTrayPositions()}");

		if (alsoSettings)
		{
			try
			{
				if (File.Exists(Config.Path)) { File.Delete(Config.Path); report.Add("TrayMon.json удалён"); }
				else report.Add("TrayMon.json не найден");
			}
			catch (Exception ex) { report.Add("TrayMon.json: " + ex.Message); }
		}
		else report.Add("TrayMon.json оставлен на месте");

		return report;
	}

	/// <summary>
	/// Removes the per-icon tray settings Windows keeps for this executable. They are keyed by
	/// "path to exe + GUID", so leaving them behind means a reinstall inherits the visibility
	/// of icons the user hid months ago.
	/// </summary>
	private static int ForgetTrayPositions()
	{
		var removed = 0;
		try
		{
			using var root = Registry.CurrentUser.OpenSubKey(@"Control Panel\NotifyIconSettings", writable: true);
			if (root is null) return 0;
			foreach (var name in root.GetSubKeyNames())
			{
				string exe;
				using (var item = root.OpenSubKey(name))
					exe = item?.GetValue("ExecutablePath") as string;
				if (!string.Equals(exe, ExePath, StringComparison.OrdinalIgnoreCase)) continue;
				root.DeleteSubKeyTree(name, throwOnMissingSubKey: false);
				removed++;
			}
		}
		catch (Exception)
		{
			// Nothing here is essential; a leftover key costs the user nothing but tidiness.
		}
		return removed;
	}

	/// <summary>
	/// Why the last permission check could not be carried out, if it could not. The check itself
	/// fails open — refusing autostart because an ACL was unreadable would be worse than the risk
	/// — but a security check that quietly did not happen must at least be visible, so this is
	/// shown in the diagnostics window and next to the confirmation.
	/// </summary>
	public static string LastCheckError { get; private set; }

	/// <summary>
	/// Whether anyone outside the administrative accounts may write into a folder. Used before
	/// granting the folder a prompt-free elevated launch.
	/// </summary>
	public static bool WritableByNonAdmins(string folder, out string who)
	{
		who = null;
		LastCheckError = null;
		if (string.IsNullOrEmpty(folder)) return false;
		try
		{
			const FileSystemRights dangerous =
				FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.Delete |
				FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;

			var rules = new DirectoryInfo(folder)
				.GetAccessControl(AccessControlSections.Access)
				.GetAccessRules(true, true, typeof(SecurityIdentifier));

			foreach (FileSystemAccessRule rule in rules)
			{
				if (rule.AccessControlType != AccessControlType.Allow) continue;
				if ((rule.FileSystemRights & dangerous) == 0) continue;
				if (rule.IdentityReference is not SecurityIdentifier sid || Trusted(sid)) continue;
				who = Describe(sid);
				return true;
			}
			return false;
		}
		catch (Exception ex)
		{
			// Cannot read the ACL — do not stand in the user's way over a check that failed,
			// but do not pretend it passed either.
			LastCheckError = ex.GetType().Name + ": " + ex.Message;
			return false;
		}
	}

	private static bool Trusted(SecurityIdentifier sid) =>
		sid.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid) ||
		sid.IsWellKnown(WellKnownSidType.LocalSystemSid) ||
		sid.IsWellKnown(WellKnownSidType.CreatorOwnerSid) ||
		// TrustedInstaller owns most of %ProgramFiles%.
		sid.Value == "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";

	private static string Describe(SecurityIdentifier sid)
	{
		try { return sid.Translate(typeof(NTAccount)).Value; }
		catch (Exception) { return sid.Value; }
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
			// Both pipes are drained at once. Reading one to the end and then the other only
			// works while the child stays under a pipe buffer on the stream nobody is reading.
			var stdout = p.StandardOutput.ReadToEndAsync();
			var stderr = p.StandardError.ReadToEndAsync();
			if (!p.WaitForExit(15000))
			{
				try { p.Kill(entireProcessTree: true); } catch (Exception) { /* already gone */ }
				error = "schtasks не ответил за 15 с";
				return false;
			}
			p.WaitForExit();   // lets the two readers finish before ExitCode is looked at
			if (p.ExitCode == 0) return true;

			var err = Text(stderr);
			error = string.IsNullOrWhiteSpace(err) ? Text(stdout) : err;
			if (string.IsNullOrWhiteSpace(error)) error = "schtasks вернул код " + p.ExitCode;
			return false;
		}
		catch (Exception ex)
		{
			error = ex.Message;
			return false;
		}
	}

	private static string Text(Task<string> reader)
	{
		try { return reader.GetAwaiter().GetResult().Trim(); }
		catch (Exception) { return ""; }
	}

	/// <summary>
	/// Interactive logon trigger for the current user, highest privileges, no time limit,
	/// and a few restarts in case the sensor driver is not ready right after logon.
	/// Every value that comes from the environment is escaped: a domain, a user name or a path
	/// containing an ampersand used to produce a document schtasks would not parse, and the
	/// failure looked like autostart simply not working.
	/// </summary>
	private static string TaskXml()
	{
		var user = SecurityElement.Escape($"{Environment.UserDomainName}\\{Environment.UserName}");
		var exe = SecurityElement.Escape(ExePath);
		var task = SecurityElement.Escape(TaskName);
		return $"""
			<?xml version="1.0" encoding="UTF-16"?>
			<Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
			  <RegistrationInfo>
			    <Description>Tray monitor: CPU/RAM/GPU load, temperatures and fan speeds.</Description>
			    <URI>\{task}</URI>
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
			      <Command>{exe}</Command>
			    </Exec>
			  </Actions>
			</Task>
			""";
	}
}
