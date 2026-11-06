namespace TrayMon;

/// <summary>Only public WinForms colours; native colour/message dialogs remain system-owned.</summary>
internal static class WindowTheme
{
	internal static string Current;

	private sealed class Palette : ProfessionalColorTable
	{
		internal readonly Color Background, Text, Selection, SelectedText, Border;
		internal Palette(string theme)
		{
			var system = SystemInformation.HighContrast || theme is not ("light" or "dark");
			Background = system ? SystemColors.Window : theme == "dark" ? Color.FromArgb(32, 32, 32) : Color.White;
			Text = system ? SystemColors.WindowText : theme == "dark" ? Color.WhiteSmoke : Color.Black;
			Selection = system ? SystemColors.Highlight : theme == "dark" ? Color.FromArgb(60, 75, 95) : Color.FromArgb(215, 232, 250);
			SelectedText = system ? SystemColors.HighlightText : Text;
			Border = system ? SystemColors.WindowFrame : theme == "dark" ? Color.Gray : Color.Silver;
			UseSystemColors = system;
		}
		public override Color ToolStripDropDownBackground => Background;
		public override Color ImageMarginGradientBegin => Background;
		public override Color ImageMarginGradientMiddle => Background;
		public override Color ImageMarginGradientEnd => Background;
		public override Color MenuItemSelected => Selection;
		public override Color MenuItemSelectedGradientBegin => Selection;
		public override Color MenuItemSelectedGradientEnd => Selection;
		public override Color MenuItemPressedGradientBegin => Selection;
		public override Color MenuItemPressedGradientMiddle => Selection;
		public override Color MenuItemPressedGradientEnd => Selection;
		public override Color MenuItemBorder => Border;
		public override Color MenuBorder => Border;
		public override Color SeparatorDark => Border;
		public override Color SeparatorLight => Background;
		public override Color CheckBackground => Selection;
		public override Color CheckSelectedBackground => Selection;
	}

	private sealed class Renderer(Palette palette) : ToolStripProfessionalRenderer(palette)
	{
		protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
		{
			e.TextColor = !e.Item.Enabled ? SystemColors.GrayText : e.Item.Selected ? palette.SelectedText : palette.Text;
			base.OnRenderItemText(e);
		}
	}

	internal static void Apply(Control control, string theme)
	{
		var palette = new Palette(theme);
		Apply(control, palette);
	}

	private static void Apply(Control control, Palette palette)
	{
		control.BackColor = palette.Background;
		control.ForeColor = palette.Text;
		if (control is Button button)
		{
			button.FlatStyle = FlatStyle.Flat;
			button.FlatAppearance.BorderColor = palette.Border;
			button.FlatAppearance.MouseOverBackColor = palette.Selection;
		}
		if (control is ToolStrip strip)
		{
			strip.Renderer = SystemInformation.HighContrast ? new ToolStripSystemRenderer() : new Renderer(palette);
			foreach (var item in strip.Items.OfType<ToolStripDropDownItem>())
				if (item.HasDropDownItems) Apply(item.DropDown, palette);
		}
		foreach (Control child in control.Controls) Apply(child, palette);
	}
}

/// <summary>A right-click opens settings without activating the visibility checkbox.</summary>
internal sealed class VisibilityItem(string text, Action settings) : ToolStripMenuItem(text)
{
	private bool _settingsPress;

	protected override void OnMouseDown(MouseEventArgs e)
	{
		_settingsPress = e.Button == MouseButtons.Right;
		if (!_settingsPress) base.OnMouseDown(e);
	}

	protected override void OnClick(EventArgs e)
	{
		// WinForms dispatches Click before OnMouseUp. Suppress it before CheckOnClick
		// can toggle visibility, including after an earlier left press enabled right-clicks.
		if (!_settingsPress) base.OnClick(e);
	}

	protected override void OnMouseLeave(EventArgs e)
	{
		_settingsPress = false;
		base.OnMouseLeave(e);
	}

	protected override void OnMouseUp(MouseEventArgs e)
	{
		if (e.Button == MouseButtons.Right) { _settingsPress = false; settings(); return; }
		_settingsPress = false;
		base.OnMouseUp(e);
	}
}
