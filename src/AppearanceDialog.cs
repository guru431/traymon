using System.Globalization;

namespace TrayMon;

/// <summary>A draft only: no setting changes until the owner accepts the dialog.</summary>
internal sealed class AppearanceDialog : Form
{
	private readonly Color _defaultPlate;
	private Color _plate;
	private bool _defaultColor;
	private readonly ComboBox _ink;
	private readonly ComboBox _theme;
	private readonly Panel _preview;
	private readonly Label _contrast;

	internal string Plate => _defaultColor ? null : $"#{_plate.R:X2}{_plate.G:X2}{_plate.B:X2}";
	internal string Ink => _ink.SelectedIndex switch { 1 => "light", 2 => "dark", _ => null };
	internal string Theme => _theme.SelectedIndex switch { 1 => "light", 2 => "dark", _ => null };

	internal AppearanceDialog(Color plate, Color defaultPlate, bool defaultColor, string ink, string theme)
	{
		_defaultPlate = defaultPlate;
		_plate = plate;
		_defaultColor = defaultColor;
		Text = "TrayMon — оформление";
		AutoScaleMode = AutoScaleMode.Font;
		StartPosition = FormStartPosition.CenterScreen;
		ClientSize = new Size(640, 420);
		MinimumSize = new Size(560, 400);
		MinimizeBox = false;
		ShowInTaskbar = false;
		TopMost = true;
		var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = new Padding(12) };
		layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
		layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
		layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
		layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
		layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
		var choices = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
		var background = new Button { Text = "Фон…", AutoSize = true };
		background.Click += (_, _) =>
		{
			using var picker = new ColorDialog { Color = _plate, FullOpen = true, AnyColor = true };
			if (picker.ShowDialog(this) != DialogResult.OK) return;
			_plate = picker.Color;
			_defaultColor = false;
			RefreshPreview();
		};
		_ink = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 120, AccessibleName = "Цвет цифр" };
		_ink.Items.AddRange(new object[] { "Авто", "Светлые", "Тёмные" });
		_ink.SelectedIndex = ink switch { "light" => 1, "dark" => 2, _ => 0 };
		_theme = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 140, AccessibleName = "Тема окон и меню" };
		_theme.Items.AddRange(new object[] { "Системная", "Светлая", "Тёмная" });
		_theme.SelectedIndex = theme switch { "light" => 1, "dark" => 2, _ => 0 };
		choices.Controls.AddRange(new Control[] { background, new Label { Text = "Цифры:", AutoSize = true, Padding = new Padding(0, 8, 0, 0) }, _ink,
			new Label { Text = "Окна:", AutoSize = true, Padding = new Padding(0, 8, 0, 0) }, _theme });
		layout.Controls.Add(choices);
		layout.Controls.Add(new Label { Text = "Аварийная плашка временно заменяет обычный фон. Образцы — в настоящем размере.", AutoSize = true, MaximumSize = new Size(600, 0) });
		_preview = new Panel { Dock = DockStyle.Fill, AutoScroll = true, AutoScrollMinSize = new Size(490, 205), AccessibleName = "Предпросмотр 16, 24 и 32 пикселя" };
		_preview.Paint += (_, e) => PaintPreview(e.Graphics);
		layout.Controls.Add(_preview);
		_contrast = new Label { AutoSize = true, MaximumSize = new Size(600, 0) };
		layout.Controls.Add(_contrast);
		var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
		var apply = new Button { Text = "Применить", AutoSize = true, DialogResult = DialogResult.OK };
		var cancel = new Button { Text = "Отмена", AutoSize = true, DialogResult = DialogResult.Cancel };
		var reset = new Button { Text = "По умолчанию", AutoSize = true };
		reset.Click += (_, _) => { _plate = _defaultPlate; _defaultColor = true; _ink.SelectedIndex = 0; RefreshPreview(); };
		buttons.Controls.AddRange(new Control[] { apply, cancel, reset });
		layout.Controls.Add(buttons);
		Controls.Add(layout);
		AcceptButton = apply;
		CancelButton = cancel;
		_ink.SelectedIndexChanged += (_, _) => RefreshPreview();
		_theme.SelectedIndexChanged += (_, _) => { WindowTheme.Apply(this, Theme); RefreshPreview(); };
		WindowTheme.Apply(this, theme);
		RefreshPreview();
	}

	private Color? RequestedInk => Ink switch { "light" => Color.White, "dark" => Color.Black, _ => null };

	private void RefreshPreview()
	{
		var colors = new[] { _plate, TrayValueIcon.WarnPlate, TrayValueIcon.CritPlate, TrayValueIcon.DeadPlate };
		var ratios = colors.Select(c => TrayValueIcon.Contrast(c, RequestedInk ?? TrayValueIcon.AutoInk(c))).ToArray();
		_contrast.Text = "Контраст (норма / жёлтый / красный / нет данных): " +
			string.Join(" / ", ratios.Select(r => r.ToString("0.00", CultureInfo.InvariantCulture) + ":1")) +
			(ratios.Any(r => r < 4.5) ? "\nМелкие цифры могут читаться плохо; попробуйте «Авто»." : "");
		_preview.Invalidate();
	}

	private void PaintPreview(Graphics graphics)
	{
		var states = new[] { ("Норма", _plate, Alarm.Normal), ("Жёлтый", TrayValueIcon.WarnPlate, Alarm.Warning),
			("Красный", TrayValueIcon.CritPlate, Alarm.Critical), ("Нет данных", TrayValueIcon.DeadPlate, Alarm.Dead) };
		var scale = DeviceDpi / 96f;
		var saved = graphics.Save();
		graphics.TranslateTransform(_preview.AutoScrollPosition.X, _preview.AutoScrollPosition.Y);
		using var labels = new SolidBrush(_preview.ForeColor);
		for (var row = 0; row < states.Length; row++)
		{
			var (name, color, level) = states[row];
			var y = (int)((8 + row * 48) * scale);
			graphics.DrawString(name, Font, labels, 0, y + 8);
			var x = (int)(110 * scale);
			foreach (var side in new[] { 16, 24, 32 })
			{
				foreach (var text in new[] { "85", "1200", "9999", "1.2" })
				{
					using var bitmap = new Bitmap(side, side);
					using (var g = Graphics.FromImage(bitmap)) TrayValueIcon.PaintIcon(g, side, text, color, RequestedInk, level);
					graphics.DrawImageUnscaled(bitmap, x, y);
					x += side + 4;
				}
				x += 12;
			}
		}
		graphics.Restore(saved);
	}
}
