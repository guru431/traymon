using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace TrayMon;

/// <summary>
/// One tray slot: a small icon with a number on a coloured plate, plus its own tooltip.
/// The plate colour identifies the metric; it turns yellow and then red once the value
/// crosses the warning and critical thresholds.
///
/// Registered through Shell_NotifyIcon with its own guidItem rather than through WinForms
/// NotifyIcon. Without a GUID the Windows 11 / Server 2025 tray treats every icon of a
/// process as one group and moves them together — with one, each icon keeps its own place
/// and the user can drag them into any order.
///
/// Redraws only when the drawn text or colour changes, and destroys the previous icon
/// handle — GetHicon leaks a GDI handle per call otherwise.
/// </summary>
public sealed class TrayValueIcon : IDisposable
{
	private const int NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2, NIM_SETVERSION = 4;
	private const int NIF_MESSAGE = 0x01, NIF_ICON = 0x02, NIF_TIP = 0x04, NIF_GUID = 0x20, NIF_SHOWTIP = 0x80;
	private const int NOTIFYICON_VERSION_4 = 4;
	private const int WM_TRAYCALLBACK = 0x0400 + 1;   // WM_APP + 1
	private const int WM_CONTEXTMENU = 0x007B, WM_RBUTTONUP = 0x0205;

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

	[DllImport("shell32.dll", CharSet = CharSet.Unicode)]
	private static extern bool Shell_NotifyIcon(int message, ref NotifyIconData data);

	[DllImport("user32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool DestroyIcon(IntPtr handle);

	[DllImport("user32.dll")]
	private static extern bool SetForegroundWindow(IntPtr hWnd);

	private static readonly Color WarnPlate = Color.FromArgb(214, 170, 0);
	private static readonly Color CritPlate = Color.FromArgb(196, 48, 32);
	private static readonly Color DeadPlate = Color.FromArgb(70, 70, 70);

	private readonly MessageWindow _window;
	private readonly Action<TrayValueIcon> _onRightClick;
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

	/// <param name="onRightClick">Called when the user right-clicks this icon; the owner builds
	/// a menu for whichever icon was clicked.</param>
	/// <param name="plate">Background colour that identifies this metric.</param>
	/// <param name="guid">Stable per-icon identity; must not change between runs, or the
	/// icon loses the position the user dragged it to.</param>
	/// <param name="warn">Severity value (not the drawn number) that turns the plate yellow.</param>
	/// <param name="crit">Severity value that turns the plate red.</param>
	public TrayValueIcon(Action<TrayValueIcon> onRightClick, Color plate, Guid guid, double warn, double crit)
	{
		_onRightClick = onRightClick;
		_plate = plate;
		_guid = guid;
		_warn = warn;
		_crit = crit;
		_window = new MessageWindow(() => _onRightClick(this));
		Update(null, null, "starting…");
	}

	/// <summary>Changes the plate colour and repaints immediately.</summary>
	public void SetPlate(Color plate)
	{
		if (_plate == plate) return;
		_plate = plate;
		_lastText = null;   // force a repaint
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
		_lastText = null;   // force a repaint
		Update(_lastDrawn, _lastSeverity, _lastTip);
	}

	/// <summary>Changes the thresholds and repaints immediately.</summary>
	public void SetThresholds(double warn, double crit)
	{
		if (Math.Abs(_warn - warn) < 0.001 && Math.Abs(_crit - crit) < 0.001) return;
		_warn = warn;
		_crit = crit;
		_lastText = null;
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
		var color = severity is null ? DeadPlate
			: severity.Value >= _crit ? CritPlate
			: severity.Value >= _warn ? WarnPlate
			: _plate;

		if (tooltip.Length > 127) tooltip = tooltip.Substring(0, 127);
		if (_added && text == _lastText && color == _lastColor && tooltip == _lastTip) return;

		var redraw = text != _lastText || color != _lastColor || _iconHandle == IntPtr.Zero;
		if (redraw)
		{
			var previous = _iconHandle;
			_iconHandle = Render(text, color, _ink);
			if (previous != IntPtr.Zero) DestroyIcon(previous);
		}

		_lastText = text;
		_lastColor = color;
		_lastTip = tooltip;
		Send(_added ? NIM_MODIFY : NIM_ADD);
	}

	private void Send(int message)
	{
		var data = NewData();
		if (Shell_NotifyIcon(message, ref data))
		{
			if (message == NIM_ADD)
			{
				_added = true;
				var version = NewData();
				version.uVersion = NOTIFYICON_VERSION_4;
				Shell_NotifyIcon(NIM_SETVERSION, ref version);
			}
			return;
		}

		if (message != NIM_ADD) return;

		// A GUID left over from an earlier exe path blocks the add; drop it and retry.
		var stale = NewData();
		Shell_NotifyIcon(NIM_DELETE, ref stale);
		var retry = NewData();
		if (Shell_NotifyIcon(NIM_ADD, ref retry)) { _added = true; return; }

		// Last resort: identify by window+id. Icons then group again, but they still work.
		_useGuid = false;
		var plain = NewData();
		if (Shell_NotifyIcon(NIM_ADD, ref plain)) _added = true;
	}

	private NotifyIconData NewData() => new()
	{
		cbSize = Marshal.SizeOf<NotifyIconData>(),
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

	/// <param name="forcedInk">Colour of the digits chosen by the user; null picks it by the plate.</param>
	private static IntPtr Render(string text, Color plate, Color? forcedInk)
	{
		// Draw at the size Windows actually asks for, so nothing is rescaled on high-DPI screens.
		var side = Math.Max(16, SystemInformation.SmallIconSize.Height);
		using var bmp = new Bitmap(side, side);
		using (var g = Graphics.FromImage(bmp))
		{
			g.Clear(Color.Transparent);
			g.SmoothingMode = SmoothingMode.AntiAlias;
			using (var back = new SolidBrush(plate))
			using (var path = RoundedSquare(side, Math.Max(2, side / 6)))
				g.FillPath(back, path);

			// Yellow plate is too bright for white digits.
			var ink = forcedInk ?? (plate == WarnPlate ? Color.Black : Color.White);

			g.SmoothingMode = SmoothingMode.None;
			g.TextRenderingHint = TextRenderingHint.SingleBitPerPixelGridFit;
			var fontSize = side * (text.Length > 2 ? 0.56f : 0.72f);
			using var font = new Font("Segoe UI", fontSize, FontStyle.Bold, GraphicsUnit.Pixel);
			using var brush = new SolidBrush(ink);
			var size = g.MeasureString(text, font);
			g.DrawString(text, font, brush, (side - size.Width) / 2f, (side - size.Height) / 2f);
		}
		return bmp.GetHicon();
	}

	private static GraphicsPath RoundedSquare(int side, int radius)
	{
		var d = radius * 2;
		var path = new GraphicsPath();
		path.AddArc(0, 0, d, d, 180, 90);
		path.AddArc(side - d - 1, 0, d, d, 270, 90);
		path.AddArc(side - d - 1, side - d - 1, d, d, 0, 90);
		path.AddArc(0, side - d - 1, d, d, 90, 90);
		path.CloseFigure();
		return path;
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
		_window.DestroyHandle();
	}

	/// <summary>Hidden window that receives the tray callbacks for one icon.</summary>
	private sealed class MessageWindow : NativeWindow
	{
		private readonly Action _onContextMenu;

		public MessageWindow(Action onContextMenu)
		{
			_onContextMenu = onContextMenu;
			CreateHandle(new CreateParams { Caption = "TrayMon", Style = 0, ExStyle = 0, ClassStyle = 0 });
		}

		protected override void WndProc(ref Message m)
		{
			if (m.Msg == WM_TRAYCALLBACK)
			{
				var notification = (int)m.LParam & 0xFFFF;
				if (notification is WM_CONTEXTMENU or WM_RBUTTONUP) _onContextMenu();
				return;
			}
			base.WndProc(ref m);
		}
	}
}
