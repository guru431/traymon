# Findings — TrayMon
Побочные находки, только `open`. Ревизия: MonthlyStratReview 1-го числа. Stale >90 дней → alert.
Новые записи сверху. Выполненные — удаляются (след в `git log`), отклонённые — переносятся в [FINDINGS-archive.md](FINDINGS-archive.md).

## 2026-09-19 · code-review role:review: Денайлист .sanitize-patterns склеивается в ОДНУ строку: `tr ... [P1]
**Context:** auto-cron `ClaudeCodeReviewWeekly` (provider=ocg), file traymon/.githooks/_scan.sh:78-80, category=security
**What:** Денайлист .sanitize-patterns склеивается в ОДНУ строку: `tr -d` с литеральным переводом строки удаляет все переводы строк между шаблонами, не заменяя их разделителем. grep -Ef трактует каждую строку файла как отдельный шаблон, а после склейки остаётся один шаблон — конкатенация всех строк (например, `192\.168\.1\.[0-9]+0nk\.ru`). В результате personal-data denylist не срабатывает ни на одно реальное значение, и персональные данные/внутренние имена беспрепятственно коммитятся, хотя хук сообщает, что проверка выполнена (файл найден, scan_rc==0). Вероятно, задумывалось `tr -d '\r'` (срез CR у Windows-строк) или `tr '\n' '\n'`-подобное сохранение строк.
**Evidence:** `grep -vE '^[[:space:]]*$' "$scan_sp" 2>/dev/null | tr -d ' ' > "$scan_pat" || true`
**Proposal:** Удалять только CR, сохраняя строки как отдельные шаблоны: `tr -d '\r'` (или `sed 's/\r$//'`). Дополнительно добавить регрессионный кейс в tools/Test-GitHooks.sh с двумя строками в .sanitize-patterns, каждая из которых должна срабатывать независимо.
**Status:** open

## 2026-09-19 · code-review role:review: Комментарий функции утверждает, что прежняя версия ошибочно ... [P3]
**Context:** auto-cron `ClaudeCodeReviewWeekly` (provider=ocg), file traymon/tools/Check-ReadmeOutput.ps1:55-57, category=correctness
**What:** Комментарий функции утверждает, что прежняя версия ошибочно сводила `GPU 0 mem`, `GPU 0 fan` и `GPU 0` к одному `GPU N`, и что это исправлено, но правило `if ($label -match '\sN')` схлопывает их точно так же: 'GPU N mem' содержит ' N' и обрезается до первого слова 'GPU'. Из-за этого элементы опционального списка 'GPU N mem' и 'GPU N fan' никогда не совпадут (они уже заменены на 'GPU'), и расхождение состава GPU-подстрок между выводом и README не обнаруживается.
**Evidence:** `if ($label -match '\sN') { $label = ($label -split '\s+')[0] }`
**Proposal:** Схлопывать хвост только когда после подписи остались именно числа без слов, например: `if ($label -match '^\S+(\s+N(\.N)?)+$') { $label = ($label -split '\s+')[0] }` — тогда 'uptime N.N h' → 'uptime', а 'GPU N mem' сохранится.
**Status:** open

## 2026-09-19 · code-review role:review: Stamp заполняется только в успешной ветке чтения. При ошибке... [P3]
**Context:** auto-cron `ClaudeCodeReviewWeekly` (provider=ocg), file traymon/src/Config.cs:296-312, category=bug
**What:** Stamp заполняется только в успешной ветке чтения. При ошибке чтения (битый JSON, временная блокировка файла) возвращается конфиг с Stamp = default(DateTime), поэтому ChangedOnDisk сразу возвращает true (`File.GetLastWriteTimeUtc(Path) != Stamp`), и watcher/«Перечитать настройки» считают неотредактированный файл изменённым — возможны повторные циклы перезагрузки/уведомлений, пока файл не прочитается успешно.
**Evidence:** `try { return File.Exists(Path) && File.GetLastWriteTimeUtc(Path) != Stamp; }`
**Proposal:** При ошибке чтения устанавливать Stamp = File.GetLastWriteTimeUtc(Path) (в try/catch, как в успешной ветке), либо хранить отдельный флаг «Stamp неизвестен» и считать ChangedOnDisk = false, пока файл не прочитан успешно.
**Status:** open

## 2026-09-19 · code-review role:review: Autostart.Enable вызывает перегрузку InstallUnsafe() без пар... [P2]
**Context:** auto-cron `ClaudeCodeReviewWeekly` (provider=ocg), file traymon/src/Autostart.cs:236-260 (Enable) vs Program.cs RunOnce, category=security
**What:** Autostart.Enable вызывает перегрузку InstallUnsafe() без параметра smartctl, поэтому путь к smartctl.exe из TrayMon.json не проверяется перед созданием задачи с RunLevel HighestAvailable. При этом собственная документация метода InstallUnsafe(smartctl,...) и Program.RunOnce явно учитывают, что smartctl запускается процессом с elevated-токеном и его местоположение так же важно, как папка TrayMon: заменённый злоумышленником (доступный на запись не-администратору) smartctl.exe получит права администратора при первом же опросе RAID. Diagnostics и --once проверяют smartctl, а фактическая выдача elevation — нет: расхождение между двумя реализациями одной и той же проверки.
**Evidence:** `if (InstallUnsafe(out var what))`
**Proposal:** В Enable() принимать путь smartctl (например, Enable(replaceForeign, smartctl, out error)) и вызывать InstallUnsafe(smartctl, out what); вызывающая сторона в TrayApp имеет _config.Tools.Smartctl.
**Status:** open

## 2026-09-19 · code-review role:review: Скрипт проверяет gitignore только для seed-файла .sanitize-p... [P2]
**Context:** auto-cron `ClaudeCodeReviewWeekly` (provider=ocg), file traymon/scripts/enable-guard.sh:30-56, category=security
**What:** Скрипт проверяет gitignore только для seed-файла .sanitize-patterns.md, но настоящий denylist с реальными секретами — это .sanitize-patterns (без расширения), который скрипт предлагает создать пользователю. Если .gitignore покрывает только '.sanitize-patterns.md' (или конкретное имя), реальный файл с именами/токенами/серийниками будет закоммичен, причём скрипт уже напечатал '[ok] ... secret-guard active' и никакой дополнительной проверки при создании .sanitize-patterns не выполняется — guard-хуки при этом сами не блокируют его, если шаблон не добавлен.
**Evidence:** `if ! git check-ignore -q "$seed" 2>/dev/null; then`
**Proposal:** Проверять gitignore для обоих имён: git check-ignore -q "$seed" .sanitize-patterns, и печатать предупреждение, если .sanitize-patterns не игнорируется (либо проверять его в pre-commit-хуке по имени файла).
**Status:** open

## 2026-09-19 · code-review role:review: ACLineStatus == 255 (unknown) трактуется как 'питание от сет... [P3]
**Context:** auto-cron `ClaudeCodeReviewWeekly` (provider=ocg), file traymon/src/Sensors.cs:BatterySensor.Read, category=correctness
**What:** ACLineStatus == 255 (unknown) трактуется как 'питание от сети': OnBattery = (ACLineStatus == 0) даёт false при неизвестном статусе, и ShowBattery рисует зелёную плашку 'от сети' с severity 0 на машине, где Windows не сообщает статус. Проект последовательно борется с такой же ошибкой для UPS (Status==1 => null, 'состояние неизвестно'), а для батареи тот же случай не обработан.
**Evidence:** `OnBattery = s.ACLineStatus == 0,`
**Proposal:** Вернуть из Read признак неизвестности (например, хранить bool? OnBattery) и в ShowBattery при unknown не считать severity нулём, а показывать нейтральное состояние.
**Status:** open

## 2026-09-19 · code-review role:review: chmod выполняется с '2>/dev/null || true', и при его неудаче... [P3]
**Context:** auto-cron `ClaudeCodeReviewWeekly` (provider=ocg), file traymon/scripts/enable-guard.sh:17-19, category=bug
**What:** chmod выполняется с '2>/dev/null || true', и при его неудаче (например, файловая система без support exec-бита, смонтированная как noexec, или отсутствие файлов) скрипт всё равно печатает '[ok] ... secret-guard active', хотя по собственному же комментарию POSIX git молча пропускает не-executable хук — защита остаётся выключенной при сообщении об обратном.
**Evidence:** `chmod +x .githooks/commit-msg .githooks/pre-commit .githooks/pre-push 2>/dev/null || true`
**Proposal:** Проверять результат: если chmod или последующий test -x хотя бы одного хука не удались, печатать '[warn] hooks not executable — guard NOT active' и возвращать ненулевой код.
**Status:** open

## 2026-09-19 · code-review role:review: Последний fallback сравнивает только имя пользователя без до... [P3]
**Context:** auto-cron `ClaudeCodeReviewWeekly` (provider=ocg), file traymon/src/Autostart.cs:IsCurrentUser, category=correctness
**What:** Последний fallback сравнивает только имя пользователя без домена: задача планировщика, принадлежащая одноимённому пользователю другого домена/машины (например, локальный 'admin' против DOMAIN\admin), будет классифицирована как 'Ours', и Enable перезапишет чужую задачу без предупреждения, а Uninstall удалит её — ровно тот сценарий, от которого защита с TaskState.Foreign построена.
**Evidence:** `string.Equals(wanted, Environment.UserName, StringComparison.OrdinalIgnoreCase);`
**Proposal:** Убрать fallback по голому имени или принимать его только когда машина не в домене (Environment.UserDomainName == MachineName).
**Status:** open

