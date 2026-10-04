# Findings — TrayMon
Побочные находки, только `open`. Ревизия: MonthlyStratReview 1-го числа. Stale >90 дней → alert.
Новые записи сверху. Выполненные — удаляются (след в `git log`), отклонённые — переносятся в [FINDINGS-archive.md](FINDINGS-archive.md).

## 2026-10-03 · code-review role:review: Контракт сканера заявлен как fail-closed: «A scanner error (... [P2]
**Context:** auto-cron `ClaudeCodeReviewWeekly` (provider=ocg), file traymon/.githooks/_scan.sh:78-84, category=inconsistency
**What:** Контракт сканера заявлен как fail-closed: «A scanner error (grep rc>1) fails closed, so a broken regex in the file blocks the commit rather than silently passing it». Но это относится только ко второму grep (по телу коммита). Подготовка файла паттернов — `grep -vE ... "$scan_sp" 2>/dev/null | tr -d ... > "$scan_pat" || true` — при любой ошибке grep (файл существует, но нечитаем по правам, ошибка чтения) даёт пустой $scan_pat, ветка `[ -s "$scan_pat" ]` не выполняется, и персональная проверка молча пропускается (return 0), т.е. fail-open — ровно то, что комментарий обещает исключить. stderr при этом подавлен 2>/dev/null, так что и диагностики нет (в отличие от случая «файл не найден», где печатается WARN).
**Evidence:** `grep -vE '^[[:space:]]*$' "$scan_sp" 2>/dev/null | tr -d '`
**Proposal:** Не подавлять stderr и не маскировать rc подготовки: проверить rc grep (rc>1 → BLOCKED, как это уже сделано для второго grep), а пустой результат после успешного grep с существующим файлом трактовать как ошибку подготовки, а не как «паттернов нет».
**Status:** open

## 2026-10-03 · code-review role:review: Карта засчитывается как «не ответившая» по коду возврата одн... [P3]
**Context:** auto-cron `ClaudeCodeReviewWeekly` (provider=ocg), file traymon/src/Sensors.cs:~560 (GpuSensor.Read / ReadCard), category=correctness
**What:** Карта засчитывается как «не ответившая» по коду возврата одного лишь nvmlDeviceGetUtilization, даже если память/температура/вентиляторы с той же карты успешно прочитаны. Три таких опроса подряд по всем картам (например, транзиентный NVML_ERROR_UNKNOWN=999 от utilization на загруженном драйвере) запускают полный Reinit (nvmlShutdown + nvmlInit), хотя карта фактически отвечает. Комментарий описывает критерий как «not one card answered», но реализация проверяет только один из пяти вызовов.
**Evidence:** `status = NvmlGetUtilization(card.Handle, out var util);`
**Proposal:** Считать карту ответившей, если хотя бы один из вызовов (utilization/memory/temperature) вернул 0: например, lost++ только когда и utilization, и memory, и temperature вернули ошибку.
**Status:** open

## 2026-10-03 · code-review role:review: Защита настроек от затирания асимметрична. На стартовом пути... [P3]
**Context:** auto-cron `ClaudeCodeReviewWeekly` (provider=ocg), file traymon/src/Config.cs:~330 (Read) и Save, category=inconsistency
**What:** Защита настроек от затирания асимметрична. На стартовом пути (Load → recover:true) битый JSON сохраняется как .bad перед тем, как программа продолжит с дефолтами. Но на пути перечитки (Read → recover:false) при JsonException файл остаётся на месте без копии, а Save() не проверяет LoadError — следующее изменение из меню сериализует текущий (дефолтный) объект поверх битого файла, и его содержимое теряется без .bad. Комментарий в Read объясняет отказ от rename транзиентностью блокировки, но это не отменяет того, что последующий Save перезапишет файл без бэкапа.
**Evidence:** `return Stamped(new Config { LoadError = recover ? Keep(ex) : Describe(ex) + " — файл не тронут" });`
**Proposal:** Либо в Save() при непустом LoadError и ChangedOnDisk-совпадающем штампе сначала скопировать текущий файл в .bad (как Keep), либо в Read(recover:false) при JsonException тоже создавать .bad-копию, не переименовывая оригинал.
**Status:** open

## 2026-10-03 · code-review role:review: Гонка между WriteLogLine и FlushLog: если писатель уже прове... [P3]
**Context:** auto-cron `ClaudeCodeReviewWeekly` (provider=ocg), file traymon/src/Program.cs:FlushLog / WriteLogLine, category=bug
**What:** Гонка между WriteLogLine и FlushLog: если писатель уже проверил очередь, нашёл её пустой и вышел из lock, но ещё не дошёл до finally с Volatile.Write(ref _logWriting, 0), то основной поток успевает enqueue новую партию, Interlocked.Exchange возвращает 1, и Spawn не вызывается. Новая партия остаётся в очереди до следующего WriteLogLine, т.е. до истечения Log.EverySeconds (по умолчанию 30 с), хотя писатель уже не работает.
**Evidence:** `if (Interlocked.Exchange(ref _logWriting, 1) == 1) return;`
**Proposal:** После Exchange==1 перепроверить очередь под lock и, если появились новые партии, снова попытаться стать писателем (или сделать Exchange до enqueue / использовать очередь с сигналом).
**Status:** open

## 2026-10-03 · code-review role:review: GetResponse с error-status 0, но без единого varbind (обреза... [P3]
**Context:** auto-cron `ClaudeCodeReviewWeekly` (provider=ocg), file traymon/src/UpsSensor.cs:Read, цикл попыток, category=correctness
**What:** GetResponse с error-status 0, но без единого varbind (обрезанный/подделанный/нестандартный ответ) принимается как успешный ответ: цикл попыток завершается, Present=true, Answered=true, все значения null. ИБП помечается «отвечает», хотя ни одного значения не получено, и retry-цикл (предназначенный как раз для выбрасывания мусорных датаграмм) не продолжает ждать настоящий ответ.
**Evidence:** `if (reply.Error == 0) { answer = reply.Vars; break; }`
**Proposal:** Считать ответ успешным только при reply.Error == 0 && reply.Vars.Count > 0; иначе продолжить чтение до дедлайна.
**Status:** open

## 2026-10-03 · code-review role:review: Регистрация серийных номеров в Redactor для RAID/дисков — мё... [P3]
**Context:** auto-cron `ClaudeCodeReviewWeekly` (provider=ocg), file traymon/src/Program.cs:RunOnce, блок RAID-вывода, category=optimization
**What:** Регистрация серийных номеров в Redactor для RAID/дисков — мёртвый код: строка RAID печатает литеральный <serial>, а не d.Serial, и серийные номера ни в одной строке RunOnce не интерполируются. Hide-вызовы ничего не скрывают (не вредно, но обещание «everything printed below goes through this» создаёт ложное впечатление, что серийник где-то выводится и редактируется).
**Evidence:** `Say($"raid {(d.Temp.HasValue ? d.Temp.Value.ToString("0", ci) : " —")} °C {d.Name} <serial> " +`
**Proposal:** Либо убрать hide.Hide(d.Serial, ...) как мёртвый код, либо печатать реальный d.Serial, если он нужен в отчёте (тогда Hide действительно нужен).
**Status:** open

