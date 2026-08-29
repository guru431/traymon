using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace TrayMon;

/// <summary>
/// One tray slot: a small icon with a number on a coloured plate, plus its own tooltip.
/// The plate colour identifies the metric; it turns yellow and then red once the value
/// crosses the warning and critical thresholds, and grows a corner notch at the same time so
/// the alarm survives a colour-blind reader and a bad monitor.
///
/// Registered through Shell_NotifyIcon with its own guidItem rather than through WinForms
/// NotifyIcon. Without a GUID the Windows 11 / Server 2025 tray treats every icon of a
/// process as one group and moves them together — with one, each icon keeps its own place
/// and the user can drag them into any order.
///
/// Redraws only when the drawn text or colour changes, and destroys the previous icon
/// handle — GetHicon leaks a GDI handle per call otherwise. Everything a redraw needs beyond
/// the digits themselves — the font, the plate outline, the brushes, the bitmap — is built
/// once and kept, because a repaint plus a call into the shell is the most expensive thing
/// this program does.
/// </summary>
public sealed class TrayValueIcon : IDisposable
{
	private const int NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2, NIM_SETVERSION = 4;
	private const int NIF_MESSAGE = 0x01, NIF_ICON = 0x02, NIF_TIP = 0x04, NIF_INFO = 0x10,
					  NIF_GUID = 0x20, NIF_SHOWTIP = 0x80;
	private const int NIIF_WARNING = 0x02;
	private const int NOTIFYICON_VERSION_4 = 4;

	private const int WM_TRAYCALLBACK = 0x0400 + 1;   // WM_USER + 1 (WM_APP is 0x8000)
	private const int WM_CONTEXTMENU = 0x007B, WM_RBUTTONUP = 0x0205;
	private const int WM_LBUTTONUP = 0x0202, WM_MOUSEMOVE = 0x0200;
	private const int NIN_SELECT = 0x0400, NIN_KEYSELECT = 0x0401, NIN_POPUPOPEN = 0x0406;
	private const int WM_SETTINGCHANGE = 0x001A, WM_DISPLAYCHANGE = 0x007E, WM_DPICHANGED = 0x02E0;

	private const int MSGFLT_ALLOW = 1;

	/// <summary>Tooltip limit of a version-4 icon. Version 0 would only take 63, which is why
	/// NIM_SETVERSION is sent after every successful add, including the fallback paths.</summary>
	private const int TipLimit = 127;

	/// <summary>How long after the pointer was last seen an icon still counts as watched.</summary>
	private const int HoverWindowMs = 4000;

	[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
	private struct NotifyIconData
	{
		public int cbSize;
		public IntPtr hWnd;
		public int uID;
		public int uFlags;
		public int uCallbackMessage;
		public IntPtr hIcon;
		[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
		public int dwState;
		public int dwStateMask;
		[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
		public int uVersion;
		[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
		public int dwInfoFlags;
		public Guid guidItem;
		public IntPtr hBalloonIcon;
	}

	/// <summary>Computed once: this used to be a Marshal.SizeOf call per icon update.</summary>
	private static readonly int DataSize = Marshal.SizeOf<NotifyIconData>();

	// EntryPoint spelled out: the export is Shell_NotifyIconW, and the wrapper below is not
	// named after it. Without this the runtime looks for "ShellNotifyIcon", fails, and throws
	// on every single icon update — which registers no icons at all while the process stays
	// happily alive.
	[DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", CharSet = CharSet.Unicode)]
	private static extern bool ShellNotifyIcon(int message, ref NotifyIconData data);

	/// <summary>
	/// How many icon bitmaps have been drawn, and how many calls have gone into the shell.
	/// These are the two most expensive things the program does — the project measures the cost
	/// of every data source in --once and used to measure neither of these, so any argument
	/// about the drawing path was a matter of taste rather than of a number.
	/// </summary>
	public static long Renders;

	public static long ShellCalls;

	private static bool Shell_NotifyIcon(int message, ref NotifyIconData data)
	{
		Interlocked.Increment(ref ShellCalls);
		return ShellNotifyIcon(message, ref data);
	}

	[DllImport("user32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool DestroyIcon(IntPtr handle);

	[DllImport("user32.dll")]
	private static extern bool SetForegroundWindow(IntPtr hWnd);

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	private static extern int RegisterWindowMessage(string name);

	[DllImport("user32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool ChangeWindowMessageFilterEx(IntPtr hwnd, int message, int action, IntPtr changeInfo);

	/// <summary>
	/// Broadcast by the shell after explorer.exe restarts: every tray icon is gone from its
	/// table and has to be added again. Without handling it, a restarted Explorer left TrayMon
	/// running with no icons at all — and therefore no menu and no way to quit.
	/// </summary>
	private static readonly int WmTaskbarCreated = RegisterWindowMessage("TaskbarCreated");

	private static readonly Color WarnPlate = Color.FromArgb(214, 170, 0);
	private static readonly Color CritPlate = Color.FromArgb(196, 48, 32);
	private static readonly Color DeadPlate = Color.FromArgb(70, 70, 70);

	private readonly MessageWindow _window;
	private readonly Action<TrayValueIcon> _onRightClick;
	private readonly Action<TrayValueIcon> _onLeftClick;
	private readonly Guid _guid;
	private double _warn;
	private double _crit;
	private Color _plate;

	private Color? _ink;
	private bool _useGuid = true;
	private bool _added;
	private string _lastText;
	private string _lastTip = "";
	private string _lastDrawn;
	private double? _lastSeverity;
	private Color _lastColor;
	private IntPtr _iconHandle = IntPtr.Zero;

	private bool _forceRedraw;
	private bool _checkSize;
	private bool _tipPending;
	private long _hoverAt;
	private int _addFailures;
	private long _retryAt;

	private Bitmap _canvas;
	private Graphics _canvasGraphics;
	private int _side;

	/// <param name="onRightClick">Called when the user right-clicks this icon; the owner builds
	/// a menu for whichever icon was clicked.</param>
	/// <param name="onLeftClick">Called on a plain click or Enter — the gesture every other tray
	/// program answers.</param>
	/// <param name="plate">Background colour that identifies this metric.</param>
	/// <param name="guid">Stable per-icon identity; must not change between runs, or the
	/// icon loses the position the user dragged it to.</param>
	/// <param name="warn">Severity value (not the drawn number) that turns the plate yellow.</param>
	/// <param name="crit">Severity value that turns the plate red.</param>
	public TrayValueIcon(Action<TrayValueIcon> onRightClick, Action<TrayValueIcon> onLeftClick,
						 Color plate, Guid guid, double warn, double crit)
	{
		_onRightClick = onRightClick;
		_onLeftClick = onLeftClick;
		_plate = plate;
		_guid = guid;
		_warn = warn;
		_crit = crit;
		_window = new MessageWindow(this);
		Update(null, null, "запуск…");
	}

	/// <summary>Changes the plate colour and repaints immediately.</summary>
	public void SetPlate(Color plate)
	{
		if (_plate == plate) return;
		_plate = plate;
		_forceRedraw = true;
		Update(_lastDrawn, _lastSeverity, _lastTip);
	}

	/// <summary>
	/// Forces the colour of the digits, or restores the automatic choice when given null.
	/// Repaints immediately.
	/// </summary>
	public void SetInk(Color? ink)
	{
		if (_ink == ink) return;
		_ink = ink;
		_forceRedraw = true;
		Update(_lastDrawn, _lastSeverity, _lastTip);
	}

	/// <summary>Changes the thresholds and repaints immediately.</summary>
	public void SetThresholds(double warn, double crit)
	{
		if (Math.Abs(_warn - warn) < 0.001 && Math.Abs(_crit - crit) < 0.001) return;
		_warn = warn;
		_crit = crit;
		_forceRedraw = true;
		Update(_lastDrawn, _lastSeverity, _lastTip);
	}

	/// <summary>True when the plate currently shows a threshold colour instead of its own.</summary>
	public bool IsAlerting => _lastSeverity.HasValue && _lastSeverity.Value >= _warn;

	/// <param name="text">What to draw, null when the source is unavailable.</param>
	/// <param name="severity">Value the thresholds apply to — a percentage or a temperature,
	/// which is not always what is drawn (the RAM icon shows GB but colours by percent).</param>
	public void Update(string text, double? severity, string tooltip)
	{
		_lastDrawn = text;
		_lastSeverity = severity;
		text ??= "—";
		tooltip ??= "";

		// A metrics broadcast only asks the question; it does not answer it. WM_SETTINGCHANGE is
		// sent to every top-level window by all sorts of unrelated things, and repainting every
		// icon each time one arrives would cost far more than the DPI change it is meant to catch.
		if (_checkSize)
		{
			_checkSize = false;
			if (Math.Max(16, SystemInformation.SmallIconSize.Height) != _side) _forceRedraw = true;
		}
		var color = severity is null ? DeadPlate
			: severity.Value >= _crit ? CritPlate
			: severity.Value >= _warn ? WarnPlate
			: _plate;

		if (tooltip.Length > TipLimit) tooltip = tooltip.Substring(0, TipLimit);

		var visualChanged = text != _lastText || color != _lastColor;
		var tipChanged = tooltip != _lastTip;
		if (_added && !visualChanged && !tipChanged && !_forceRedraw) return;

		_lastTip = tooltip;

		if (visualChanged || _forceRedraw || _iconHandle == IntPtr.Zero)
		{
			_forceRedraw = false;
			var previous = _iconHandle;
			_iconHandle = Render(text, color);
			if (previous != IntPtr.Zero) DestroyIcon(previous);
			_lastText = text;
			_lastColor = color;
			_tipPending = false;
			Send(_added ? NIM_MODIFY : NIM_ADD);
			return;
		}

		if (!_added) { Send(NIM_ADD); _tipPending = false; return; }

		// Only the tooltip moved. Tooltips carry the un-rounded values on purpose, so they change
		// almost every tick — and each call into the shell marshals a 976-byte structure and
		// crosses into explorer.exe. Across a full tray that was about six of them a second, for
		// text nobody is reading. Send it while the pointer is on the icon; otherwise remember
		// that it is stale and flush it the moment the pointer arrives.
		if (Environment.TickCount64 - _hoverAt < HoverWindowMs) Send(NIM_MODIFY);
		else _tipPending = true;
	}

	/// <summary>Balloon notification on this icon, for a state change worth interrupting for.</summary>
	public void Notify(string title, string message)
	{
		if (!_added) return;
		var data = NewData();
		data.uFlags |= NIF_INFO;
		data.szInfoTitle = Cut(title, 63);
		data.szInfo = Cut(message, 255);
		data.dwInfoFlags = NIIF_WARNING;
		Shell_NotifyIcon(NIM_MODIFY, ref data);
	}

	private static string Cut(string s, int max) =>
		string.IsNullOrEmpty(s) ? "" : s.Length <= max ? s : s.Substring(0, max);

	private void Send(int message)
	{
		if (message == NIM_ADD && Environment.TickCount64 < _retryAt) return;

		var data = NewData();
		if (Shell_NotifyIcon(message, ref data))
		{
			if (message == NIM_ADD) { _added = true; _addFailures = 0; SetVersion(); }
			return;
		}

		if (message != NIM_ADD)
		{
			// The shell does not know this icon any more. Ignoring that is how an icon ends up
			// frozen on its last number for the rest of the session: forget that it was added,
			// and the next update puts it back.
			_added = false;
			return;
		}

		// A GUID left over from an earlier exe path blocks the add; drop it and retry.
		var stale = NewData();
		Shell_NotifyIcon(NIM_DELETE, ref stale);
		var retry = NewData();
		if (Shell_NotifyIcon(NIM_ADD, ref retry)) { _added = true; _addFailures = 0; SetVersion(); return; }

		// Last resort: identify by window+id. Icons then group again, but they still work.
		_useGuid = false;
		var plain = NewData();
		if (Shell_NotifyIcon(NIM_ADD, ref plain)) { _added = true; _addFailures = 0; SetVersion(); return; }

		// Back off. Repeating the delete-and-add dance every two seconds for ever is a call into
		// the shell per icon per tick that cannot succeed.
		_useGuid = true;
		_addFailures++;
		_retryAt = Environment.TickCount64 + Math.Min(60000, 2000L * _addFailures);
	}

	/// <summary>Asks for the version-4 protocol — sent after every successful add, including the
	/// retry and the fallback: version 0 caps tooltips at 63 characters and reports clicks
	/// differently, so skipping it there truncated tooltips after the exe was moved.</summary>
	private void SetVersion()
	{
		var version = NewData();
		version.uVersion = NOTIFYICON_VERSION_4;
		Shell_NotifyIcon(NIM_SETVERSION, ref version);
	}

	private NotifyIconData NewData() => new()
	{
		cbSize = DataSize,
		hWnd = _window.Handle,
		uID = 1,
		uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP | (_useGuid ? NIF_GUID : 0),
		uCallbackMessage = WM_TRAYCALLBACK,
		hIcon = _iconHandle,
		szTip = _lastTip ?? "",
		szInfo = "",
		szInfoTitle = "",
		guidItem = _useGuid ? _guid : Guid.Empty,
	};

	/// <summary>Brings the hidden window forward so the menu closes when focus is lost.</summary>
	public void PrepareMenu() => SetForegroundWindow(_window.Handle);

	private void OnTrayMessage(int notification)
	{
		switch (notification)
		{
			case WM_CONTEXTMENU:
			case WM_RBUTTONUP:
				_onRightClick?.Invoke(this);
				break;
			case NIN_SELECT:
			case NIN_KEYSELECT:
			case WM_LBUTTONUP:
				_onLeftClick?.Invoke(this);
				break;
			case WM_MOUSEMOVE:
			case NIN_POPUPOPEN:
				OnHover();
				break;
		}
	}

	private void OnHover()
	{
		_hoverAt = Environment.TickCount64;
		if (!_tipPending) return;
		_tipPending = false;
		Send(NIM_MODIFY);
	}

	/// <summary>Explorer restarted: the shell's table is empty, so add everything again.</summary>
	private void OnTaskbarCreated()
	{
		_added = false;
		_useGuid = true;
		_addFailures = 0;
		_retryAt = 0;
		_forceRedraw = true;
		Update(_lastDrawn, _lastSeverity, _lastTip);
	}

	/// <summary>
	/// DPI, resolution or theme may have changed. The icon size comes from the system, but a
	/// repaint only happens when the number does — so an icon showing a steady value stayed at
	/// the old size after an RDP reconnect and was rescaled by the shell into a blur. The next
	/// update compares the size and repaints only if it really moved.
	/// </summary>
	private void OnMetricsChanged() => _checkSize = true;

	// ---- drawing ----

	// Everything below is touched from the UI thread only: the timer, the menu handlers and the
	// window procedure all run there, so these caches need no locking.
	private static readonly Dictionary<Color, SolidBrush> Inks = new();
	private static readonly Dictionary<(int Side, int Step), Font> Digits = new();
	private static readonly Dictionary<int, GraphicsPath> Outlines = new();

	private static readonly StringFormat Centered = new(StringFormatFlags.NoWrap | StringFormatFlags.NoClip)
	{
		Alignment = StringAlignment.Center,
		LineAlignment = StringAlignment.Center,
		Trimming = StringTrimming.None,
	};

	private IntPtr Render(string text, Color plate)
	{
		Interlocked.Increment(ref Renders);

		// Draw at the size Windows actually asks for, so nothing is rescaled on high-DPI screens.
		var side = Math.Max(16, SystemInformation.SmallIconSize.Height);
		if (_canvas is null || side != _side)
		{
			_canvasGraphics?.Dispose();
			_canvas?.Dispose();
			_side = side;
			_canvas = new Bitmap(side, side);
			_canvasGraphics = Graphics.FromImage(_canvas);
		}

		var g = _canvasGraphics;
		g.Clear(Color.Transparent);
		g.SmoothingMode = SmoothingMode.AntiAlias;
		g.FillPath(Ink(plate), Outline(side));

		var ink = _ink ?? (Luma(plate) > 140 ? Color.Black : Color.White);
		var brush = Ink(ink);

		// A second channel for the alarm. Colour alone loses to deuteranopia — the fan, the
		// critical and the temperature plates collapse into one hue — and to a bad panel.
		if (plate == CritPlate) { Notch(g, brush, side, topRight: true); Notch(g, brush, side, topRight: false); }
		else if (plate == WarnPlate) Notch(g, brush, side, topRight: true);

		g.SmoothingMode = SmoothingMode.None;
		g.TextRenderingHint = TextRenderingHint.SingleBitPerPixelGridFit;
		// Four digits are a normal reading, not an edge case: a 2.5 GbE link carrying a file
		// copy shows 1200 Mbit/s, so there is a step for them too.
		var step = text.Length > 3 ? 2 : text.Length > 2 ? 1 : 0;
		g.DrawString(text, Digit(side, step), brush, new RectangleF(0, 0, side, side), Centered);

		return _canvas.GetHicon();
	}

	/// <summary>A small triangle in a corner: the shape that says "alarm" without a colour.</summary>
	private static void Notch(Graphics g, Brush brush, int side, bool topRight)
	{
		var n = Math.Max(4, side / 4);
		var points = topRight
			? new[] { new Point(side - n, 0), new Point(side, 0), new Point(side, n) }
			: new[] { new Point(0, side - n), new Point(0, side), new Point(n, side) };
		g.FillPolygon(brush, points);
	}

	private static double Luma(Color c) => 0.299 * c.R + 0.587 * c.G + 0.114 * c.B;

	private static SolidBrush Ink(Color color)
	{
		if (Inks.TryGetValue(color, out var brush)) return brush;
		// The palette is finite — twelve plates, three alarm colours, two inks — but a user
		// picking colours from the dialog all afternoon could grow this without bound.
		if (Inks.Count > 64) { foreach (var b in Inks.Values) b.Dispose(); Inks.Clear(); }
		return Inks[color] = new SolidBrush(color);
	}

	private static Font Digit(int side, int step)
	{
		var key = (side, step);
		if (Digits.TryGetValue(key, out var font)) return font;
		var factor = step switch { 2 => 0.44f, 1 => 0.56f, _ => 0.72f };
		return Digits[key] = new Font("Segoe UI", side * factor, FontStyle.Bold, GraphicsUnit.Pixel);
	}

	/// <summary>The plate outline depends on the icon size alone, so it is built once per size.</summary>
	private static GraphicsPath Outline(int side)
	{
		if (Outlines.TryGetValue(side, out var path)) return path;
		var radius = Math.Max(2, side / 6);
		var d = radius * 2;
		path = new GraphicsPath();
		path.AddArc(0, 0, d, d, 180, 90);
		path.AddArc(side - d - 1, 0, d, d, 270, 90);
		path.AddArc(side - d - 1, side - d - 1, d, d, 0, 90);
		path.AddArc(0, side - d - 1, d, d, 90, 90);
		path.CloseFigure();
		return Outlines[side] = path;
	}

	public void Dispose()
	{
		if (_added)
		{
			var data = NewData();
			Shell_NotifyIcon(NIM_DELETE, ref data);
			_added = false;
		}
		if (_iconHandle != IntPtr.Zero) { DestroyIcon(_iconHandle); _iconHandle = IntPtr.Zero; }
		_canvasGraphics?.Dispose();
		_canvas?.Dispose();
		_canvasGraphics = null;
		_canvas = null;
		_window.DestroyHandle();
	}

	/// <summary>Hidden window that receives the tray callbacks for one icon.</summary>
	private sealed class MessageWindow : NativeWindow
	{
		private readonly TrayValueIcon _owner;

		public MessageWindow(TrayValueIcon owner)
		{
			_owner = owner;
			CreateHandle(new CreateParams { Caption = "TrayMon", Style = 0, ExStyle = 0, ClassStyle = 0 });
			// TrayMon runs elevated and explorer.exe does not, so UIPI drops its broadcast
			// unless this window says it wants that one message.
			if (WmTaskbarCreated != 0)
				ChangeWindowMessageFilterEx(Handle, WmTaskbarCreated, MSGFLT_ALLOW, IntPtr.Zero);
		}

		protected override void WndProc(ref Message m)
		{
			if (m.Msg == WM_TRAYCALLBACK)
			{
				_owner.OnTrayMessage((int)m.LParam & 0xFFFF);
				return;
			}
			if (WmTaskbarCreated != 0 && m.Msg == WmTaskbarCreated)
			{
				_owner.OnTaskbarCreated();
				return;
			}
			if (m.Msg is WM_DISPLAYCHANGE or WM_DPICHANGED or WM_SETTINGCHANGE)
				_owner.OnMetricsChanged();
			base.WndProc(ref m);
		}
	}
}
