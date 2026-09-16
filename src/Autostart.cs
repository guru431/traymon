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

	/// <summary>TASK_TRIGGER_LOGON.</summary>
	private const int LogonTrigger = 9;

	private static string _exePath;
	private static bool _exeResolved;

	/// <summary>
	/// The executable a task or a shortcut should start — null when there is no such thing.
	///
	/// <c>Environment.ProcessPath</c> is not it. Running the assembly through the runtime
	/// (<c>dotnet out\TrayMon.dll</c>, which both README versions recommend for looking at the
	/// unprivileged behaviour) makes that dotnet.exe, so the menu built a shortcut and a task for
	/// dotnet.exe without the DLL argument, checked the permissions of the runtime's folder, and
	/// looked for dotnet's path among the remembered tray positions. The apphost next to the
	/// assembly is the real answer; when it is absent the install actions have nothing to point at
	/// and say so instead of creating something broken.
	/// </summary>
	public static string ExePath
	{
		get
		{
			if (_exeResolved) return _exePath;
			_exeResolved = true;
			var running = Environment.ProcessPath;
			var apphost = Path.Combine(AppContext.BaseDirectory, "TrayMon.exe");
			if (string.IsNullOrEmpty(running))
				return _exePath = File.Exists(apphost) ? apphost : null;
			if (!string.Equals(Path.GetFileNameWithoutExtension(running), "dotnet", StringComparison.OrdinalIgnoreCase))
				return _exePath = Path.ChangeExtension(running, ".exe");
			return _exePath = File.Exists(apphost) ? apphost : null;
		}
	}

	/// <summary>False when the program was started through the runtime and there is no apphost to
	/// register — the menu then explains that instead of installing dotnet.exe.</summary>
	public static bool Installable => ExePath is not null;

	private const string NoApphost =
		"программа запущена через рантайм (dotnet TrayMon.dll), и рядом со сборкой нет TrayMon.exe.\n" +
		"Задача автозапуска и ярлык указывали бы на dotnet.exe без аргумента — установка отменена.";

	private static dynamic Service()
	{
		var type = Type.GetTypeFromProgID("Schedule.Service");
		if (type is null) return null;
		dynamic service = Activator.CreateInstance(type);
		service.Connect();
		return service;
	}

	/// <summary>What the scheduler holds under our name.</summary>
	public enum TaskState
	{
		/// <summary>No such task.</summary>
		None,

		/// <summary>The scheduler could not be reached, so nothing is known.</summary>
		Unreadable,

		/// <summary>Starts this executable, for this account, enabled, with a logon trigger.</summary>
		Ours,

		/// <summary>Exists and starts something else, or belongs to another account.</summary>
		Foreign,

		/// <summary>Ours, but switched off or missing the trigger that would ever start it.</summary>
		Disabled,

		/// <summary>Exists and carries no action at all — it can never start anything.</summary>
		Broken,
	}

	/// <summary>
	/// Reads the task back properly.
	///
	/// This used to compare the path of the first action and nothing else, and treated "no action
	/// at all" as a match, so the menu showed autostart as enabled for a task that was switched
	/// off, had lost its logon trigger, belonged to another account or carried nothing to run.
	/// </summary>
	public static TaskState State(out string command, out string note)
	{
		command = null;
		note = null;
		try
		{
			dynamic service = Service();
			if (service is null) { note = "планировщик заданий недоступен (Schedule.Service)"; return TaskState.Unreadable; }
			dynamic folder = service.GetFolder("\\");
			dynamic task;
			try { task = folder.GetTask(TaskName); }
			catch (Exception) { return TaskState.None; }

			dynamic definition = task.Definition;
			dynamic actions = definition.Actions;
			if (actions.Count < 1) { note = "задача без действия — запускать нечего"; return TaskState.Broken; }

			// The COM collection is one-based, and Path comes back already unescaped — the string
			// comparison used to be made against raw XML, where an ampersand in the path read as
			// "&amp;" and the diagnostics window announced that the task launched another file.
			string path = actions[1].Path;
			command = path?.Trim().Trim('"');
			if (ExePath is null || !string.Equals(command, ExePath, StringComparison.OrdinalIgnoreCase))
			{
				note = "задача запускает другой файл";
				return TaskState.Foreign;
			}

			string principal = null;
			try { principal = definition.Principal.UserId as string; } catch (Exception) { /* older scheduler */ }
			if (principal is not null && !IsCurrentUser(principal))
			{
				note = "задача принадлежит другой учётной записи";
				return TaskState.Foreign;
			}

			bool enabled = true;
			try { enabled = (bool)task.Enabled; } catch (Exception) { /* older scheduler */ }
			if (!enabled) { note = "задача отключена в планировщике"; return TaskState.Disabled; }

			if (!HasLogonTrigger(definition))
			{
				note = "у задачи нет включённого триггера входа в систему";
				return TaskState.Disabled;
			}
			return TaskState.Ours;
		}
		catch (Exception ex)
		{
			note = ex.Message;
			return TaskState.Unreadable;
		}
	}

	private static bool HasLogonTrigger(dynamic definition)
	{
		try
		{
			dynamic triggers = definition.Triggers;
			for (var i = 1; i <= triggers.Count; i++)
			{
				dynamic trigger = triggers[i];
				if ((int)trigger.Type != LogonTrigger) continue;
				if ((bool)trigger.Enabled) return true;
			}
			return false;
		}
		catch (Exception) { return true; }   // cannot tell — do not call a working task broken
	}

	private static bool IsCurrentUser(string principal)
	{
		var wanted = principal.Trim();
		try
		{
			using var identity = WindowsIdentity.GetCurrent();
			if (string.Equals(wanted, identity.User?.Value, StringComparison.OrdinalIgnoreCase)) return true;
			if (string.Equals(wanted, identity.Name, StringComparison.OrdinalIgnoreCase)) return true;
		}
		catch (Exception) { /* fall through to the environment form */ }
		return string.Equals(wanted, $"{Environment.UserDomainName}\\{Environment.UserName}",
							 StringComparison.OrdinalIgnoreCase) ||
			   string.Equals(wanted, Environment.UserName, StringComparison.OrdinalIgnoreCase);
	}

	/// <summary>
	/// True when a task exists, starts this executable for this account and is actually able to
	/// start. The task alone is not enough: it holds an absolute path, so after the exe is moved
	/// the task still exists and points at nothing.
	/// </summary>
	public static bool IsEnabled => State(out _, out _) == TaskState.Ours;

	/// <summary>True when a task exists but launches some other path — a moved or copied exe.</summary>
	public static bool PointsElsewhere(out string command) =>
		State(out command, out _) == TaskState.Foreign && command is not null;

	/// <param name="replaceForeign">
	/// True when the user has been shown that the existing task starts another file, or belongs to
	/// another account, and has agreed to take it over. One task name is shared by every
	/// installation and every user, so a second copy used to overwrite the first one's task in
	/// silence.
	/// </param>
	public static bool Enable(bool replaceForeign, out string error)
	{
		error = null;
		if (ExePath is null) { error = NoApphost; return false; }

		// A task started with the highest available token and no prompt is exactly the asset an
		// unprivileged process would like to point somewhere else. If anything in the load path
		// lets somebody but an administrator write, creating that task hands them elevation.
		if (InstallUnsafe(out var what))
		{
			error =
				$"{what}\n" +
				"Задача запускает TrayMon с правами администратора и без запроса UAC, поэтому\n" +
				"любой процесс этого пользователя смог бы подменить файл и получить эти права.\n" +
				"Перенесите программу в %ProgramFiles%\\TrayMon и включите автозапуск оттуда.";
			return false;
		}
		// A check that could not run is not a check that passed. It used to fail open here and
		// merely add a caveat to the confirmation, which made "проверено" and "не проверялось"
		// look the same in the one place where the program grants standing elevation.
		if (LastCheckError is not null)
		{
			error =
				"Права на папку программы прочитать не удалось: " + LastCheckError + ".\n" +
				"Задача даёт постоянный запуск с повышением без запроса UAC, поэтому она не\n" +
				"создаётся, пока не подтверждено, что писать в папку может только администратор.";
			return false;
		}

		var state = State(out var command, out _);
		if (state == TaskState.Foreign && !replaceForeign)
		{
			error =
				$"Задача «{TaskName}» уже есть и запускает {command ?? "другую программу"}.\n" +
				"Это другая установка TrayMon или другая учётная запись. Перезаписав задачу,\n" +
				"вы отключите автозапуск той копии.";
			return false;
		}

		try
		{
			dynamic service = Service();
			if (service is null) { error = "планировщик заданий недоступен (Schedule.Service)"; return false; }
			dynamic folder = service.GetFolder("\\");
			folder.RegisterTask(TaskName, TaskXml(), CreateOrUpdate, null, null, InteractiveToken, null);
			// Read back: RegisterTask succeeding is not the same as a task that will start, and the
			// menu tick has to follow what the scheduler actually holds.
			var after = State(out _, out var note);
			if (after == TaskState.Ours) return true;
			error = "задача создана, но проверка вернула «" + (note ?? after.ToString()) + "»";
			return false;
		}
		catch (Exception ex)
		{
			error = ex.Message;
			return false;
		}
	}

	/// <param name="evenIfForeign">
	/// True to delete a task that starts another file or belongs to another account. Removing one
	/// installation used to delete the task of another: the name is shared, and the code knew the
	/// task pointed elsewhere and deleted it anyway.
	/// </param>
	public static bool Disable(bool evenIfForeign, out string error)
	{
		error = null;
		var state = State(out var command, out _);
		if (state == TaskState.Foreign && !evenIfForeign)
		{
			error = $"задача «{TaskName}» запускает {command ?? "другую программу"} — она оставлена на месте";
			return false;
		}
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
		if (ExePath is null) { error = NoApphost; return false; }
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

		switch (State(out var command, out var note))
		{
			case TaskState.None:
				report.Add("Задачи планировщика не было");
				break;
			case TaskState.Unreadable:
				report.Add("Задача планировщика: прочитать не удалось — " + note);
				break;
			case TaskState.Foreign:
				// Not ours to remove. It used to be deleted regardless, so uninstalling one copy
				// silently disabled the autostart of another installation or another user.
				report.Add($"Задача планировщика оставлена: она запускает {command ?? "другую программу"}");
				break;
			default:
				report.Add(Disable(evenIfForeign: false, out var taskError)
					? "Задача планировщика удалена"
					: "Задача планировщика: " + taskError);
				break;
		}

		try
		{
			if (!File.Exists(ShortcutPath)) report.Add("Ярлыка на рабочем столе не было");
			else if (ShortcutTarget(ShortcutPath) is { } target && ExePath is not null &&
					 !string.Equals(target, ExePath, StringComparison.OrdinalIgnoreCase))
				report.Add($"Ярлык на рабочем столе оставлен: он указывает на {target}");
			else { File.Delete(ShortcutPath); report.Add("Ярлык на рабочем столе удалён"); }
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

	/// <summary>What a .lnk actually points at, or null when it cannot be read. A file called
	/// TrayMon.lnk is not proof that it is ours.</summary>
	private static string ShortcutTarget(string path)
	{
		try
		{
			var shellType = Type.GetTypeFromProgID("WScript.Shell");
			if (shellType is null) return null;
			dynamic shell = Activator.CreateInstance(shellType);
			dynamic link = shell.CreateShortcut(path);
			string target = link.TargetPath;
			return string.IsNullOrWhiteSpace(target) ? null : target.Trim().Trim('"');
		}
		catch (Exception) { return null; }
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
		if (ExePath is null) return "Позиции значков: путь к exe не определён — ничего не удалено";
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

	/// <summary>
	/// Whether this file could be replaced by somebody who is not an administrator — either
	/// because of its own ACL or because of the folders above it.
	///
	/// Checking the folder alone was not enough: a folder can carry a perfect ACL while the exe,
	/// a DLL beside it or the settings file inside it grants Modify to Users, which is all it takes
	/// to have the autostart task launch somebody else's code with an elevated token.
	/// </summary>
	public static bool UnsafeToRun(string file, out string who)
	{
		who = null;
		if (string.IsNullOrWhiteSpace(file)) return false;
		if (WritableByNonAdmins(Path.GetDirectoryName(file), out var folderWho))
		{
			who = folderWho;
			return true;
		}
		var folderError = LastCheckError;
		try
		{
			var info = new FileInfo(file);
			if (info.Exists && LooseFile(info, out var fileWho))
			{
				who = $"{fileWho} — у самого файла {info.Name}";
				return true;
			}
		}
		catch (Exception ex)
		{
			LastCheckError = folderError ?? ex.GetType().Name + ": " + ex.Message;
			return false;
		}
		LastCheckError = folderError;
		return false;
	}

	/// <summary>
	/// The whole load path of this installation: the executable, everything loaded beside it, the
	/// settings file that can redirect an external tool, and that tool itself.
	/// </summary>
	/// <param name="smartctl">Configured path to smartctl.exe, or null when the built-in one is
	/// used. It is started by a process holding an elevated token, so where it lives matters as
	/// much as where TrayMon lives.</param>
	public static bool InstallUnsafe(string smartctl, out string what)
	{
		what = null;
		var errors = new List<string>();
		var folder = ExePath is null ? AppContext.BaseDirectory : Path.GetDirectoryName(ExePath);

		if (WritableByNonAdmins(folder, out var who))
		{
			what = $"Папка {folder} доступна на запись ({who}).";
			return true;
		}
		if (LastCheckError is not null) errors.Add(LastCheckError);

		var files = new List<string>();
		if (ExePath is not null) files.Add(ExePath);
		if (File.Exists(Config.Path)) files.Add(Config.Path);
		if (!string.IsNullOrWhiteSpace(smartctl)) files.Add(smartctl);
		try { files.AddRange(Directory.EnumerateFiles(folder, "*.dll")); }
		catch (Exception ex) { errors.Add(ex.GetType().Name + ": " + ex.Message); }

		foreach (var file in files)
		{
			if (!UnsafeToRun(file, out var fileWho))
			{
				if (LastCheckError is not null) errors.Add(LastCheckError);
				continue;
			}
			what = $"Файл {file} может изменить {fileWho}.";
			LastCheckError = null;
			return true;
		}

		LastCheckError = errors.Count > 0 ? errors[0] : null;
		return false;
	}

	/// <summary>Convenience overload for the places that have no settings at hand.</summary>
	public static bool InstallUnsafe(out string what) => InstallUnsafe(null, out what);

	private static bool LooseFile(FileInfo file, out string who)
	{
		who = null;
		var rules = file
			.GetAccessControl(AccessControlSections.Access)
			.GetAccessRules(true, true, typeof(SecurityIdentifier));

		foreach (FileSystemAccessRule rule in rules)
		{
			if (rule.AccessControlType != AccessControlType.Allow) continue;
			if ((rule.FileSystemRights & DangerousHere) == 0) continue;
			if (rule.IdentityReference is not SecurityIdentifier sid || Trusted(sid)) continue;
			who = Describe(sid);
			return true;
		}
		return false;
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
