using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace TrayMon;

internal sealed partial class TrayApp
{
	/// <summary>Built from fields of the current snapshot, never from local prose or error text.</summary>
	internal string PublicDiagnostics()
	{
		var ci = CultureInfo.InvariantCulture;
		var text = new StringBuilder();
		text.AppendLine($"TrayMon {Version} — обезличенный отчёт");
		text.AppendLine($"права: {(IsElevated ? "администратор" : "обычный пользователь")}");
		text.AppendLine($"драйвер: {(_lhm is null ? "не открыт" : _lhm.Disabled ? "выключен" : !_lhm.Available ? "недоступен" : _lhm.DriverBlocked ? "заблокирован" : !IsElevated ? "недостаточно прав" : "загружен")}");
		text.AppendLine($"GPU: {_r.Gpus.Count}; smartctl: {(_hdd?.Available == true ? "найден" : "недоступен")}; ИБП: {(_r.Ups.Answered ? "отвечает" : "нет ответа")}");
		text.AppendLine($"настройки: {(_config.LoadError is not null ? "не прочитаны" : _config.LoadNote is not null ? "исправлены при чтении" : "прочитаны")}");
		text.AppendLine($"старых настроек дисков без доказанного владельца: {_config.Icons.Keys.Count(LegacyDiskId)} (не применяются)");
		text.AppendLine($"тик: {_tick.ToString(ci)}, интервал {_intervalMs.ToString(ci)} мс; ошибок: {_tickErrors.ToString(ci)}");
		text.AppendLine($"отрисовок: {TrayValueIcon.Renders.ToString(ci)}; вызовов в трей: {TrayValueIcon.ShellCalls.ToString(ci)}");
		text.AppendLine($"длительность тика: {(_tickTicks * 1000.0 / Stopwatch.Frequency / Math.Max(1, _tick)).ToString("0.00", ci)} мс");
		text.AppendLine($"фоновые опросы: {(Volatile.Read(ref _poolTicks) * 1000.0 / Stopwatch.Frequency / Math.Max(1, _tick)).ToString("0.00", ci)} мс на тик");
		if (_self is not null)
		{
			try
			{
				text.AppendLine($"процессор всего: {_self.TotalProcessorTime.TotalSeconds.ToString("0.00", ci)} с; память: {(_self.WorkingSet64 / 1048576.0).ToString("0", ci)} МБ");
			}
			catch (Exception) { text.AppendLine("цена процесса: не прочитана"); }
		}
		if (_cycles is not null) Shares(text);
		text.AppendLine();
		AppendPublicSlots(text);
		text.AppendLine("переходы тревог (без подписей и свободного текста):");
		if (_journal is not null)
			foreach (var entry in _journal)
				text.AppendLine($"  {Math.Max(0, Environment.TickCount64 - entry.At) / 1000} с назад: {Alarm.Name(entry.Level)}; обязательный: {entry.Required}");
		return text.ToString();
	}

	private void AppendPublicSlots(StringBuilder text)
	{
		var numbers = new Dictionary<Metric, int>();
		foreach (var slot in _order)
		{
			numbers.TryGetValue(slot.Metric, out var ordinal);
			numbers[slot.Metric] = ++ordinal;
			var number = ReferenceEquals(slot.Metric, WorstMetric) ? slot.LastSeverity : slot.LastValue;
			var value = number?.ToString("0.##", CultureInfo.InvariantCulture) ?? "—";
			var unit = slot.LastUnit is "%" or "ГБ" or "ТБ" or "°C" or "об/мин" or "Мбит/с" or "МБ/с" or "ч"
				? slot.LastUnit : slot.Metric.Unit;
			var age = Snapshot(slot, out var at, out _) && at > 0
				? $"; снимку {Math.Max(0, Environment.TickCount64 - at) / 1000} с" : "";
			var reason = !slot.Dead ? "" : Stale(slot, out _) ? "; снимок устарел"
				: slot.Id is "cpu.temp" || ReferenceEquals(slot.Metric, FanMetric)
					? !IsElevated ? "; недостаточно прав" : "; датчик недоступен"
				: "; источник не ответил";
			text.AppendLine($"  {(slot.Settings.Enabled ? "on " : "off")} {slot.Guid.ToString()[^2..]} " +
				$"{slot.Metric.Label} {ordinal}: {value} {unit}; {Alarm.Name(slot.Level)}{age}{reason}");
		}
	}
}
