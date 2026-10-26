# Findings — TrayMon
Побочные находки, только `open`. Ревизия: MonthlyStratReview 1-го числа. Stale >90 дней → alert.
Новые записи сверху. Выполненные — удаляются (след в `git log`), отклонённые — переносятся в [FINDINGS-archive.md](FINDINGS-archive.md).

## 2026-10-24 · code-review role:review: Аргументы сканирования из настроек (_scanArgs) подставляются... [P3]
**Context:** auto-cron `ClaudeCodeReviewWeekly` (provider=ocg), file traymon/src/HddSensor.cs:137-141, category=security
**What:** Аргументы сканирования из настроек (_scanArgs) подставляются в командную строку smartctl без валидации, хотя имена устройств и типы из вывода сканирования тщательно проверяются через Plain(). Safe() проверяет только путь к exe, но не содержимое _scanArgs, которое приходит из того же редактируемого пользователем файла настроек — при элевированном процессе это несогласованность с собственной моделью угроз, описанной в комментариях к PathOf и Safe.
**Evidence:** `private HddSensor(ToolSettings tools = null) { _exe = PathOf(tools?.Smartctl); _scanArgs = tools?.SmartctlScan ?? "--scan-open -d csmi"; }`
**Proposal:** Валидировать _scanArgs тем же Plain() (или белым списком разрешённых флагов) в конструкторе, либо применять Plain() к результату перед подстановкой в RunJson("-j " + _scanArgs).
**Status:** open

## 2026-10-24 · code-review role:review: Дедупликация диска, отвечающего на двух портах, может отдать... [P3]
**Context:** auto-cron `ClaudeCodeReviewWeekly` (provider=ocg), file traymon/src/HddSensor.cs:96-104, category=correctness
**What:** Дедупликация диска, отвечающего на двух портах, может отдать устаревшие данные: если порт A ответил «asleep» (в seen добавляется ключ по serial из _lastAwake), а порт B на той же итерации ответил живыми данными с тем же серийником, свежая температура отбрасывается по !seen.Add, и остаётся копия last with { Temp = null, Asleep = true }. Порядок обхода портов определяет, какие данные покажет иконка.
**Evidence:** `if (!_lastAwake.TryGetValue(device, out var last)) { unseen++; continue; } if (!seen.Add(Key(last.Serial, device))) continue; disks.Add(last with { Temp = null, Asleep = true });`
**Proposal:** Двухпроходный обход: сначала собрать живые ответы, и только для устройств без живого ответа подставлять _lastAwake; либо при конфликте «asleep-копия vs живой ответ» предпочитать живой.
**Status:** open

## 2026-10-24 · code-review role:review: Код возврата первого запуска (--once) не проверяется, в отли... [P3]
**Context:** auto-cron `ClaudeCodeReviewWeekly` (provider=ocg), file traymon/tools/Check-ReadmeOutput.ps1:76-79, category=bug
**What:** Код возврата первого запуска (--once) не проверяется, в отличие от второго запуска (--icons), где $iconsExit анализируется явно. Если сборка падает на --once, скрипт не сообщит о крахе, а выдаст вводящий в заблуждение отчёт «есть в выводе, нет в README» по всем строкам.
**Evidence:** `$output = & dotnet $Dll --once 2>&1 | ForEach-Object { "$_" } $ErrorActionPreference = "Stop"`
**Proposal:** После первого запуска сохранить $onceExit = $LASTEXITCODE и при ненулевом значении вывести сообщение о падении сборки и выйти с ошибкой, как это уже сделано для --icons.
**Status:** open

## 2026-10-24 · code-review role:review: Диск, у которого библиотека отдаёт износ (Percentage Used / ... [P3]
**Context:** auto-cron `ClaudeCodeReviewWeekly` (provider=ocg), file traymon/src/Sensors.cs:RefreshDiskTemps (ветка внутри foreach по storage), category=correctness
**What:** Диск, у которого библиотека отдаёт износ (Percentage Used / Available Spare), но не отдаёт сенсор температуры, отбрасывается целиком до чтения износа: continue срабатывает раньше, чем wear читается. Это расходится с заявленным в том же файле приоритетом («A disk running out of spare blocks matters more than a warm one») — на некоторых USB-мостах/контроллерах SMART-износ есть, а температура через эту библиотеку не приходит, и тогда единственный значимый сигнал (Worn) не попадает в иконку вовсе.
**Evidence:** `var t = hw.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Temperature && s.Name == "Temperature"); if (t?.Value is null) continue;`
**Proposal:** При t?.Value is null не делать continue, если у диска есть wear-сенсоры: добавить DiskReading с Temp=null (или NaN-маркером) и заполненными WearPercent/SparePercent, а иконка уже умеет показывать «нет значения» по отдельному полю.
**Status:** open

## 2026-10-24 · code-review role:review: Перебор Dictionary<string,double> не даёт детерминированного... [P3]
**Context:** auto-cron `ClaudeCodeReviewWeekly` (provider=ocg), file traymon/src/Config.cs:NetSettings.BandwidthFor, category=correctness
**What:** Перебор Dictionary<string,double> не даёт детерминированного порядка: при двух пересекающихся шаблонах (например "Ethernet" и "Ethernet 3") побеждает тот, что раньше в бакет-порядке хеш-таблицы, а не тот, что раньше в файле или длиннее. Результат «какой configured link speed применится» зависит от порядка вставки/истории ручных правок файла, а не от читаемого пользователем порядка строк.
**Evidence:** `foreach (var (pattern, mbit) in Bandwidth) { if (string.IsNullOrWhiteSpace(pattern) || !(mbit > 0)) continue; if (adapter.Contains(pattern, StringComparison.OrdinalIgnoreCase)) return mbit; }`
**Proposal:** Хранить Bandwidth как список пар (List<KeyValuePair<string,double>>) либо при чтении сортировать ключи по убыванию длины, чтобы более специфичный шаблон выигрывал детерминированно.
**Status:** open

## 2026-10-24 · code-review role:review: Чужой/нечитаемый ярлык всё равно удаляется. Если ShortcutTar... [P3]
**Context:** auto-cron `ClaudeCodeReviewWeekly` (provider=ocg), file traymon/src/Autostart.cs:Uninstall, ветка ярлыка, category=correctness
**What:** Чужой/нечитаемый ярлык всё равно удаляется. Если ShortcutTarget вернул null (COM недоступен, .lnk битый) или ExePath равен null (запуск через dotnet без apphost), условие «оставить чужой ярлык» ложно и срабатывает ветка File.Delete — хотя комментарий прямо говорит «A file called TrayMon.lnk is not proof that it is ours». Тот же класс ошибки, который в этой же функции аккуратно обработан для задачи планировщика (Foreign не удаляется).
**Evidence:** `else if (ShortcutTarget(ShortcutPath) is { } target && ExePath is not null && !string.Equals(target, ExePath, StringComparison.OrdinalIgnoreCase)) report.Add($"Ярлык на рабочем столе оставлен: он указывает на {target}"); else { File.Delete(ShortcutPath); report.Add("Ярлык на рабочем столе удалён"); `
**Proposal:** Удалять ярлык только когда цель прочитана и совпадает с ExePath (или файл отсутствует); при нечитаемой цели — оставлять с сообщением «не удалось прочитать цель ярлыка».
**Status:** open

## 2026-10-24 · code-review role:review: Основной резолвер зависит от `git rev-parse --path-format=ab... [P3]
**Context:** auto-cron `ClaudeCodeReviewWeekly` (provider=ocg), file traymon/.githooks/_scan.sh:scan_denylist_path, category=portability
**What:** Основной резолвер зависит от `git rev-parse --path-format=absolute`, появившегося в Git 2.31 (2021). На старте Git (RHEL 8, старые образы CI) команда завершается ошибкой, scan_common пуст, и срабатывает fallback на `--show-toplevel` — то есть ровно тот путь связанного worktree, который комментарий называет причиной бага: проверка персональных данных в worktree молча ищет denylist не там (и при отсутствии файла даёт WARN-skip вместо блокировки).
**Evidence:** `scan_sp="$(git rev-parse --show-toplevel)/.sanitize-patterns"`
**Proposal:** При пустом scan_common вычислять общий корень переносимо: `git rev-parse --git-dir`, затем для случая `.git/worktrees/<name>` подняться на два уровня и взять dirname; либо требовать Git >= 2.31 явно в README/хук-обёртке и падать с ошибкой, а не с WARN.
**Status:** open

## 2026-10-24 · code-review role:review: Community-строка кодируется Encoding.ASCII: любой не-ASCII с... [P3]
**Context:** auto-cron `ClaudeCodeReviewWeekly` (provider=ocg), file traymon/src/UpsSensor.cs:конструктор UpsSensor, category=correctness
**What:** Community-строка кодируется Encoding.ASCII: любой не-ASCII символ молча превращается в '?' (0x3F), и аутентификация у агента тихо перестаёт сходиться — диагностика покажет «community refused» без намёка, что виновата перекодировка. SNMP community по RFC — octet string, не обязательно ASCII.
**Evidence:** `_community = Encoding.ASCII.GetBytes(settings.Community);`
**Proposal:** Кодировать как UTF-8 без BOM (Encoding.UTF8.GetBytes) либо явно валидировать community на ASCII при чтении конфига и добавлять запись в LoadNote/LastError.
**Status:** open

## 2026-10-24 · code-review role:review: Валидация пары порогов по ГБ выполняется только когда введен... [P3]
**Context:** auto-cron `ClaudeCodeReviewWeekly` (provider=ocg), file traymon/src/Program.cs:EditGbThresholds, category=correctness
**What:** Валидация пары порогов по ГБ выполняется только когда введены ОБА значения. При вводе только одного значения оно не сверяется с уже сохранённым вторым: можно оставить CritGb=100 и ввести WarnGb=5 — получится пара, где «красный» порог (100 ГБ) больше «жёлтого» (5 ГБ), т.е. красный сработает раньше жёлтого при большем остатке. Для Warn/Crit аналогичная проверка существовала бы через Config.KeepInOrder, но WarnGb/CritGb в KeepInOrder/DefaultWarn/CritOf не участвуют — они читаются только в GigabyteSeverity.
**Evidence:** `if (w.HasValue && c.HasValue && c >= w)`
**Proposal:** При неполном вводе сверять введённое значение с сохранённым парным (slot.Settings.WarnGb/CritGb) и отклонять инвертированную пару тем же сообщением; либо нормализовать пару в Steps.Gigabytes (трактовать больший порог как жёлтый, меньший как красный).
**Status:** open

## 2026-10-24 · code-review role:review: Комментарий обещает «сказано один раз на случай, а не на каж... [P3]
**Context:** auto-cron `ClaudeCodeReviewWeekly` (provider=ocg), file traymon/src/Program.cs:EnsureSomethingVisible, category=inconsistency
**What:** Комментарий обещает «сказано один раз на случай, а не на каждый тик», но флаг _ensuredVisible никогда не сбрасывается — сообщение показывается один раз за весь запуск процесса. Если пользователь снова вручную скрывает все значки через правку файла, второго предупреждения не будет (иконка всё равно включится принудительно, но без объяснения).
**Evidence:** `if (_ensuredVisible) return; _ensuredVisible = true;`
**Proposal:** Сбрасывать _ensuredVisible, когда _order.Any(s => s.Settings.Enabled) снова становится true (например, в ApplySettings или в начале проверки), чтобы повторный случай «все значки скрыты» снова получил сообщение.
**Status:** open

