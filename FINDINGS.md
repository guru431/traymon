# Findings — TrayMon
Побочные находки, только `open`. Ревизия: MonthlyStratReview 1-го числа. Stale >90 дней → alert.
Новые записи сверху. Выполненные — удаляются (след в `git log`), отклонённые — переносятся в [FINDINGS-archive.md](FINDINGS-archive.md).

## 2026-10-17 · code-review role:review: Диск, впервые опрошенный уже в спящем режиме (например, Tray... [P3]
**Context:** auto-cron `ClaudeCodeReviewWeekly` (provider=ocg), file traymon/src/HddSensor.cs:117-121, category=correctness
**What:** Диск, впервые опрошенный уже в спящем режиме (например, TrayMon стартовал, когда диск уже стоял), не попадает ни в disks, ни в _lastAwake — иконки у него не будет до первого пробуждения, хотя диск исправен и отвечает. Это осознанный трейд-офф (будить диск нельзя), но результат — «серая зона»: исправный диск невидим неопределённо долго, и «Опросить датчики сейчас» это не меняет.
**Evidence:** `if (!_lastAwake.TryGetValue(device, out var last) || !seen.Add(Key(last.Serial, device))) continue;`
**Proposal:** Как минимум отразить такой диск в LastError/Note («N дисков в standby, ещё не опрошены»), чтобы отсутствие иконки читалось как «спит», а не «не отвечает».
**Status:** open

## 2026-10-17 · code-review role:review: Под `set -eu` source несуществующего или падающего `.githook... [P3]
**Context:** auto-cron `ClaudeCodeReviewWeekly` (provider=ocg), file traymon/scripts/enable-guard.sh:79-82, category=bug
**What:** Под `set -eu` source несуществующего или падающего `.githooks/_scan.sh` завершает скрипт сразу, до финального блока статуса: пользователь не увидит ни «NOT done», ни накопленных предупреждений (rc), а получит голый bash-трейс. Все предыдущие проверки аккуратно накапливают rc и печатают статус — а этот шаг их перечёркивает.
**Evidence:** `. .githooks/_scan.sh`
**Proposal:** Обернуть: `if [ -f .githooks/_scan.sh ]; then . .githooks/_scan.sh; else echo "[warn] .githooks/_scan.sh missing — cannot locate denylist"; rc=1; fi` и при наличии — вызвать scan_denylist_path в под-оболочке с fallback.
**Status:** open

## 2026-10-17 · code-review role:review: Фолбэк для временного файла паттернов — фиксированное имя "$... [P3]
**Context:** auto-cron `ClaudeCodeReviewWeekly` (provider=ocg), file traymon/.githooks/_scan.sh:108, category=correctness
**What:** Фолбэк для временного файла паттернов — фиксированное имя "$scan_sp.tmp" рядом с denylist. Если mktemp недоступен, а два hook-процесса (например, pre-commit в разных worktree одного репозитория, разделяющем общий .git) выполняются одновременно, они запишут один и тот же файл и прочитают чужой/частичный набор паттернов — проверка персональных данных станет непредсказуемой.
**Evidence:** `scan_pat=$(mktemp 2>/dev/null || echo "$scan_sp.tmp")`
**Proposal:** Добавлять уникальный суффикс: `echo "$scan_sp.$$.$RANDOM.tmp"` (или использовать mktemp с шаблоном), и гарантированно удалять файл в trap/finally-ветке.
**Status:** open

## 2026-10-17 · code-review role:review: Атомарная запись через File.Replace работает только на NTFS/... [P3]
**Context:** auto-cron `ClaudeCodeReviewWeekly` (provider=ocg), file traymon/src/Config.cs:в методе Save, category=portability
**What:** Атомарная запись через File.Replace работает только на NTFS/локальных томах; на FAT32 (внешние диски, некоторые VPS-образы) и части SMB-путей Replace возвращает IOException, и Save полностью проваливается, хотя простое File.Move(temp, Path, overwrite:true) сработало бы. Ошибка корректно возвращается вызывающему, но на таких файловых системах настройки не сохраняются вообще, хотя механизм это позволяет.
**Evidence:** `if (File.Exists(Path)) File.Replace(temp, Path, null);`
**Proposal:** При IOException от File.Replace делать фолбэк на File.Move(temp, Path, overwrite: true) (чуть менее атомарно, но лучше, чем полная потеря сохранения).
**Status:** open

## 2026-10-17 · code-review role:review: MeasuredAt перезаписывается текущим временем даже для кэширо... [P3]
**Context:** auto-cron `ClaudeCodeReviewWeekly` (provider=ocg), file traymon/src/Program.cs:~Record(), category=correctness
**What:** MeasuredAt перезаписывается текущим временем даже для кэшированного показания. Guard StatsStamp существует именно для того, чтобы отличить повторное предложение того же измерения от нового, но MeasuredAt стоит до проверки `if (at == slot.StatsStamp) return;`. Для источников с длинным периодом (UPS 30 с, RAID 600 с, space 300 с), значение которых переформатируется на каждом тике, поле 'When the reading behind LastValue was actually measured' и колонка measured_at в CSV показывают время переформатирования, а не измерения — то же для метки [HH:mm:ss] в окне сводки.
**Evidence:** `slot.LastValue = value; slot.MeasuredAt = DateTime.Now; if (at == slot.StatsStamp) return;`
**Proposal:** Перенести `slot.MeasuredAt = DateTime.Now;` ниже проверки `if (at == slot.StatsStamp) return;`, чтобы при повторном предложении кэшированного измерения сохранялся исходный момент замера.
**Status:** open

## 2026-10-17 · code-review role:review: При двух дисках одинаковой модели, из которых драйвер ответи... [P3]
**Context:** auto-cron `ClaudeCodeReviewWeekly` (provider=ocg), file traymon/src/Program.cs:~Merge(), финальный цикл, category=correctness
**What:** При двух дисках одинаковой модели, из которых драйвер ответил за один, а библиотека видит оба, сопоставление признаётся неоднозначным и драйверный диск добавляется без wear — это задокументировано. Но затем в финальном цикле оба library-only диска отбрасываются guard'ом `merged.Any(m => Similar(...))`, потому что в merged уже лежит драйверный диск той же модели. Второй физический диск полностью исчезает из списка (и его иконка пропадает), хотя его температура/износ были известны.
**Evidence:** `if (used[i]) continue; if (merged.Any(m => Similar(m.Name, fromLibrary[i].Name))) continue; merged.Add(fromLibrary[i]);`
**Proposal:** В финальном цикле сравнивать не со всеми элементами merged, а только с теми, что были взяты из fromDriver без сопоставления (не поглотившие library-запись), либо вести отдельный счётчик совпадающих по модели драйверных и library-дисков и разрешать добавление, когда library-дисков этой модели больше, чем уже добавленных.
**Status:** open

