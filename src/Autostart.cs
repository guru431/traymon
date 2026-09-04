using System.Runtime.InteropServices;
using System.Security;
using System.Security.AccessControl;
using System.Security.Principal;
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
///
/// The task is created through the Task Scheduler COM service rather than by handing an XML
/// file to schtasks.exe. The file was the problem: it had to be written somewhere both this
/// process and schtasks could reach, which meant %TEMP% — a folder any process of this user can
/// write to — and it was read back moments later by a process holding an elevated token. A
/// watcher on that folder had a window in which to replace the &lt;Command&gt; element and get
/// itself a HighestAvailable task, which is exactly the asset the folder check exists to
/// protect. COM takes the document as a string, so there is no file, no search for schtasks.exe
/// along the executable's own folder, an HRESULT instead of parsed stderr, and no XML to read
/// back and unescape.
/// </summary>
internal static class Autostart
{
	public const string TaskName = "TrayMon";

	/// <summary>TASK_CREATE_OR_UPDATE.</summary>
	private const int CreateOrUpdate = 6;

	/// <summary>TASK_LOGON_INTERACTIVE_TOKEN.</summary>
	private const int InteractiveToken = 3;

	private static string ExePath => Path.ChangeExtension(Environment.ProcessPath ?? "TrayMon.exe", ".exe");

	private static dynamic Service()
	{
		var type = Type.GetTypeFromProgID("Schedule.Service");
		if (type is null) return null;
		dynamic service = Activator.CreateInstance(type);
		service.Connect();
		return service;
	}

	/// <summary>
	/// Reads the task back. <paramref name="command"/> is what it launches, null when the task is
	/// there but carries no exec action.
	/// </summary>
	/// <returns>False when there is no such task, or the scheduler could not be reached.</returns>
	private static bool TryReadTask(out string command)
	{
		command = null;
		try
		{
			dynamic service = Service();
			if (service is null) return false;
			dynamic folder = service.GetFolder("\\");
			dynamic task = folder.GetTask(TaskName);   // throws when it does not exist
			dynamic actions = task.Definition.Actions;
			if (actions.Count < 1) return true;
			// The COM collection is one-based, and Path comes back already unescaped — the string
			// comparison used to be made against raw XML, where an ampersand in the path read as
			// "&amp;" and the diagnostics window announced that the task launched another file.
			string path = actions[1].Path;
			command = path?.Trim().Trim('"');
			return true;
		}
		catch (Exception) { return false; }
	}

	/// <summary>
	/// True when a task exists *and* it starts this executable. The task alone is not enough:
	/// it holds an absolute path, so after the exe is moved the task still exists and points at
	/// nothing, while the menu tick claimed everything was fine.
	/// </summary>
	public static bool IsEnabled
	{
		get
		{
			if (!TryReadTask(out var command)) return false;
			return command is null || string.Equals(command, ExePath, StringComparison.OrdinalIgnoreCase);
		}
	}

	/// <summary>True when a task exists but launches some other path — a moved or copied exe.</summary>
	public static bool PointsElsewhere(out string command)
	{
		if (!TryReadTask(out command)) { command = null; return false; }
		return command is not null && !string.Equals(command, ExePath, StringComparison.OrdinalIgnoreCase);
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

		error = null;
		try
		{
			dynamic service = Service();
			if (service is null) { error = "планировщик заданий недоступен (Schedule.Service)"; return false; }
			dynamic folder = service.GetFolder("\\");
			folder.RegisterTask(TaskName, TaskXml(), CreateOrUpdate, null, null, InteractiveToken, null);
			return true;
		}
		catch (Exception ex)
		{
			error = ex.Message;
			return false;
		}
	}

	public static bool Disable(out string error)
	{
		error = null;
		try
		{
			dynamic service = Service();
			if (service is null) { error = "планировщик заданий недоступен (Schedule.Service)"; return false; }
			dynamic folder = service.GetFolder("\\");
			folder.DeleteTask(TaskName, 0);
			return true;
		}
		catch (Exception ex)
		{
			error = ex.Message;
			return false;
		}
	}

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
	///
	/// The caller removes the icons from the tray first: the registry entries are keyed by
	/// "path to exe + GUID", and an icon still registered gets its entry written back the moment
	/// the shell notices it go.
	/// </summary>
	public static List<string> Uninstall(bool alsoSettings)
	{
		var report = new List<string>();

		if (TryReadTask(out _))
			report.Add(Disable(out var taskError) ? "Задача планировщика удалена" : "Задача планировщика: " + taskError);
		else
			report.Add("Задачи планировщика не было");

		try
		{
			if (File.Exists(ShortcutPath)) { File.Delete(ShortcutPath); report.Add("Ярлык на рабочем столе удалён"); }
			else report.Add("Ярлыка на рабочем столе не было");
		}
		catch (Exception ex) { report.Add("Ярлык: " + ex.Message); }

		report.Add(ForgetTrayPositions());

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

	[DllImport("shell32.dll", CharSet = CharSet.Unicode)]
	private static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid id,
												   uint flags, IntPtr token, out IntPtr path);

	[DllImport("ole32.dll")]
	private static extern void CoTaskMemFree(IntPtr memory);

	/// <summary>
	/// Removes the per-icon tray settings Windows keeps for this executable. They are keyed by
	/// "path to exe + GUID", so leaving them behind means a reinstall inherits the visibility
	/// of icons the user hid months ago.
	///
	/// The stored path is not always absolute: Windows 11 writes it relative to a known folder,
	/// as "{6D809377-...}\TrayMon\TrayMon.exe" for anything under Program Files. Comparing that
	/// literally against our own path reported "0 entries removed" in precisely the folder the
	/// README tells people to install into.
	/// </summary>
	private static string ForgetTrayPositions()
	{
		var removed = 0;
		try
		{
			using var root = Registry.CurrentUser.OpenSubKey(@"Control Panel\NotifyIconSettings", writable: true);
			// The key is a Windows 11 mechanism. Windows 10 and Server 2019-2022 keep the same
			// state inside TrayNotify\IconStreams, in an undocumented binary blob nothing here
			// will touch — so say that, rather than reporting a clean sweep of nothing.
			if (root is null) return "Позиции значков: этой версии Windows такого ключа нет — чистить нечего";
			foreach (var name in root.GetSubKeyNames())
			{
				string exe;
				using (var item = root.OpenSubKey(name))
					exe = item?.GetValue("ExecutablePath") as string;
				if (!string.Equals(Expand(exe), ExePath, StringComparison.OrdinalIgnoreCase)) continue;
				root.DeleteSubKeyTree(name, throwOnMissingSubKey: false);
				removed++;
			}
		}
		catch (Exception ex)
		{
			// Nothing here is essential; a leftover key costs the user nothing but tidiness.
			return "Позиции значков: " + ex.Message;
		}
		return $"Позиции значков в реестре: удалено записей — {removed.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
	}

	/// <summary>Turns "{known-folder GUID}\rest\of\path" into a real path; anything else is returned as is.</summary>
	private static string Expand(string stored)
	{
		if (string.IsNullOrEmpty(stored) || stored[0] != '{') return stored;
		var close = stored.IndexOf('}');
		if (close < 0 || !Guid.TryParse(stored.Substring(0, close + 1), out var folderId)) return stored;

		var buffer = IntPtr.Zero;
		try
		{
			if (SHGetKnownFolderPath(folderId, 0, IntPtr.Zero, out buffer) != 0) return stored;
			var root = Marshal.PtrToStringUni(buffer);
			if (string.IsNullOrEmpty(root)) return stored;
			return Path.Combine(root, stored.Substring(close + 1).TrimStart('\\'));
		}
		catch (Exception) { return stored; }
		finally { if (buffer != IntPtr.Zero) CoTaskMemFree(buffer); }
	}

	/// <summary>
	/// Why the last permission check could not be carried out, if it could not. The check itself
	/// fails open — refusing autostart because an ACL was unreadable would be worse than the risk
	/// — but a security check that quietly did not happen must at least be visible, so this is
	/// shown in the diagnostics window and next to the confirmation.
	/// </summary>
	public static string LastCheckError { get; private set; }

	/// <summary>Rights on the folder itself that let somebody replace what is in it.</summary>
	private const FileSystemRights DangerousHere =
		FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.Delete |
		FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;

	/// <summary>
	/// Rights on a *parent* that let somebody replace the whole folder. Deliberately narrower:
	/// the root of a volume grants ordinary users AppendData by default, so applying the mask
	/// above to ancestors would declare %ProgramFiles%\TrayMon unsafe on every machine.
	/// </summary>
	private const FileSystemRights DangerousAbove =
		FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.ChangePermissions |
		FileSystemRights.TakeOwnership;

	/// <summary>
	/// Whether anyone outside the administrative accounts may write into a folder — or replace
	/// the folder itself. Used before granting the folder a prompt-free elevated launch.
	///
	/// Ancestors are walked to the root of the volume. Checking only the folder itself missed
	/// the obvious move: D:\Tools\TrayMon can carry a perfect ACL and still be renamed out of
	/// the way, and a replacement put in its place, by anyone who may write into D:\Tools.
	/// </summary>
	public static bool WritableByNonAdmins(string folder, out string who)
	{
		who = null;
		LastCheckError = null;
		if (string.IsNullOrEmpty(folder)) return false;
		try
		{
			var here = true;
			for (var dir = new DirectoryInfo(folder); dir is not null; dir = dir.Parent, here = false)
			{
				if (!Loose(dir, here ? DangerousHere : DangerousAbove, out var identity)) continue;
				who = here ? identity : $"{identity} — в родительской папке {dir.FullName}";
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

	private static bool Loose(DirectoryInfo dir, FileSystemRights dangerous, out string who)
	{
		who = null;
		var rules = dir
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

	/// <summary>
	/// Who owns the folder. An owner holds WRITE_DAC implicitly — without a single Allow entry
	/// naming them — so they can grant themselves everything at any moment. This is reported
	/// rather than enforced: on a normally installed machine the owner is the administrator
	/// account that created the folder, and there is no cheap, reliable way from here to decide
	/// whether an arbitrary SID is an administrator. Refusing on that guess would break autostart
	/// for ordinary installations, which is worse than saying who it is and letting the person
	/// with the elevated console decide.
	/// </summary>
	public static string OwnerOf(string folder)
	{
		try
		{
			if (string.IsNullOrEmpty(folder)) return null;
			var owner = new DirectoryInfo(folder)
				.GetAccessControl(AccessControlSections.Owner)
				.GetOwner(typeof(SecurityIdentifier));
			return owner is SecurityIdentifier sid ? Describe(sid) : null;
		}
		catch (Exception) { return null; }
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

	/// <summary>
	/// Interactive logon trigger for the current user, highest privileges, no time limit,
	/// and a few restarts in case the sensor driver is not ready right after logon.
	///
	/// The account is written as a SID rather than as DOMAIN\user. The scheduler stores a SID
	/// anyway, and the name form had to be resolved by LookupAccountName first — an extra way to
	/// fail for a renamed account, an AzureAD\… principal, an MSA, or a name in Cyrillic.
	/// Everything that comes from the environment is still escaped: a path containing an
	/// ampersand used to produce a document the scheduler would not parse, and the failure looked
	/// like autostart simply not working.
	/// </summary>
	private static string TaskXml()
	{
		string user;
		using (var identity = WindowsIdentity.GetCurrent())
			user = identity.User?.Value ?? $"{Environment.UserDomainName}\\{Environment.UserName}";
		user = SecurityElement.Escape(user);
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
			    <!-- 5, not 7. In the scheduler's scale 4-6 is Normal and 7-8 is Below Normal, and
			         a program launched by hand runs at Normal — so the same build behaved
			         differently depending on how it started. Below Normal also means that under a
			         full load of ordinary-priority processes the UI thread only gets a slice from
			         the anti-starvation boost, roughly every four seconds: the icons then show a
			         stale number at exactly the moment somebody is looking at them. The work is
			         the same either way, so this costs nothing. -->
			    <Priority>5</Priority>
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
