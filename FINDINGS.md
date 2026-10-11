# Findings — TrayMon
Побочные находки, только `open`. Ревизия: MonthlyStratReview 1-го числа. Stale >90 дней → alert.
Новые записи сверху. Выполненные — удаляются (след в `git log`), отклонённые — переносятся в [FINDINGS-archive.md](FINDINGS-archive.md).

## 2026-10-10 · code-review role:review: trap на EXIT перезаписывается при каждом вызове new_repo, по... [P3]
**Context:** auto-cron `ClaudeCodeReviewWeekly` (provider=ocg), file traymon/tools/Test-GitHooks.sh:48-56, category=bug
**What:** trap на EXIT перезаписывается при каждом вызове new_repo, поэтому удаляется только последний созданный временный репозиторий; все предыдущие остаются на диске до конца сессии/перезагрузки. Комментарий в коде утверждает обратное («Removed on any exit»), но trap в sh не накапливается — каждый new_repo подменяет его новым значением $repo.
**Evidence:** `trap 'rm -rf "$repo"' EXIT`
**Proposal:** Накапливать список каталогов (cleanups="$cleanups $repo") и ставить trap один раз в начале скрипта: trap 'rm -rf $cleanups' EXIT, либо удалять репозиторий явно в конце каждого блока-кейса.
**Status:** open

## 2026-10-10 · code-review role:review: Дедупликация дисков без серийного номера по-прежнему не рабо... [P3]
**Context:** auto-cron `ClaudeCodeReviewWeekly` (provider=ocg), file traymon/src/DiskSensor.cs:80, category=correctness
**What:** Дедупликация дисков без серийного номера по-прежнему не работает: fallback-ключ включает индекс итерации i, поэтому один и тот же диск за multipath/виртуальным контроллером без serial даёт уникальный ключ на каждой итерации — ровно тот дефект, который комментарий объявляет исправленным. Исправление покрывает только случай, когда serial есть.
**Evidence:** `if (!seen.Add(string.IsNullOrEmpty(serial) ? name + "|" + i : serial)) continue;`
**Proposal:** Для дисков без serial дедуплицировать по имени (name), а не по name+index: если два PhysicalDriveN отвечают одинаковым model name без serial, показывать один; индекс в ключе гарантирует уникальность и обнуляет смысл проверки.
**Status:** open

## 2026-10-10 · code-review role:review: UnsafeToRun не проверяет владение самим файлом. Владелец фай... [P3]
**Context:** auto-cron `ClaudeCodeReviewWeekly` (provider=ocg), file traymon/src/Autostart.cs:LooseFile/Trusted (методы NearEnd файла), category=security
**What:** UnsafeToRun не проверяет владение самим файлом. Владелец файла в Windows имеет неявные READ_CONTROL/WRITE_DAC и может в любой момент выдать себе WRITE_DATA, т.е. подменить файл, который запускается задачей с RunLevel HighestAvailable. При этом Trusted() явно считает SID CreatorOwnerId доверенным, так что правило вида (A;;GA;;;CO) на файле, принадлежащем обычному пользователю, молча пропускается. OwnerOf() сообщает только о владельце папки и сознательно «report rather than enforce» (это задокументировано), но для отдельного файла владение проверяется дёшево (GetAccessControl(AccessControlSections.Owner)) и вовсе не проверяется — гэп не покрыт ни проверкой, ни отчётом.
**Evidence:** `if (rule.IdentityReference is not SecurityIdentifier sid || Trusted(sid)) continue;`
**Proposal:** В UnsafeToRun дополнительно проверить владельца файла (GetAccessControl(AccessControlSections.Owner)) и, если владелец не администратор/SYSTEM/TrustedInstaller, вернуть true с описанием; в Trusted() не считать CreatorOwner доверенным для файлов, либо проверять, резолвится ли CreatorOwner в администратора.
**Status:** open

## 2026-10-10 · code-review role:review: При отказе GlobalMemoryStatusEx обнуляются только MemLoad и ... [P3]
**Context:** auto-cron `ClaudeCodeReviewWeekly` (provider=ocg), file traymon/src/Sensors.cs:MemorySensor.Read, ветка отказа, category=inconsistency
**What:** При отказе GlobalMemoryStatusEx обнуляются только MemLoad и CommitUsedGb; MemUsedGb, MemTotalGb и CommitTotalGb остаются от предыдущего успешного опроса. Это противоречит собственному принципу, сформулированному в соседнем комментарии того же блока («Leaving the previous numbers in place kept the icon alive and coloured on data nobody had measured»): иконка уходит в серый по MemLoad=null, но тултип продолжает показывать гигабайты, которые никто не измерял.
**Evidence:** `r.MemLoad = null; r.CommitUsedGb = null; return;`
**Proposal:** В ветке отказа обнулять также MemUsedGb/MemTotalGb/CommitTotalGb (или завести единый флаг «память недоступна», по которому тултип строится как для отсутствия источника).
**Status:** open

## 2026-10-10 · code-review role:review: Массивный путь ReadArray фильтрует элементы по CStatus (отбр... [P3]
**Context:** auto-cron `ClaudeCodeReviewWeekly` (provider=ocg), file traymon/src/PerfSensors.cs:PdhQuery.Read vs PdhQuery.ReadArray, category=inconsistency
**What:** Массивный путь ReadArray фильтрует элементы по CStatus (отбрасывая инстансы без валидных данных), а скалярный Read игнорирует CStatus полностью: при rc==0 возвращается DoubleValue даже если счётчик сообщил невалидный статус (например, только что появившийся инстанс без базовой линии для rate-счётчика). Те же «stray number that reads as a real measurement», от которых массивный путь защищён, могут пройти через скалярный — в данном проекте это CPU-счётчик, т.е. главный показатель.
**Evidence:** `if (PdhGetFormattedCounterValue(counter, PDH_FMT_DOUBLE, out _, out var v) != 0) return null; return v.DoubleValue;`
**Proposal:** В Read после успешного вызова проверять v.CStatus на PDH_CSTATUS_VALID_DATA/PDH_CSTATUS_NEW_DATA и возвращать null иначе — зеркально логике ReadArray.
**Status:** open

## 2026-10-10 · code-review role:review: Штамп времени берётся после десериализации содержимого: соде... [P3]
**Context:** auto-cron `ClaudeCodeReviewWeekly` (provider=ocg), file traymon/src/Config.cs:Read(bool recover), пара чтение/штамп, category=correctness
**What:** Штамп времени берётся после десериализации содержимого: содержимое прочитано в момент T0, а Stamp = File.GetLastWriteTimeUtc(Path) — в момент T1. Если файл был перезаписан между T0 и T1 (редактор/скрипт), Stamp совпадёт с этой чужой правкой, ChangedOnDisk вернёт false, и следующее Save молча перезапишет правку, которая не попала ни в прочитанный объект, ни в защиту MustReadBeforeWrite. Именно от такой тихой потери настроек весь механизм штампа и защищает.
**Evidence:** `config.Validate(); config.Stamp = File.GetLastWriteTimeUtc(Path);`
**Proposal:** Снимать штамп до чтения содержимого (или брать min из штампа до и после чтения): тогда правка, произошедшая во время чтения, сделает штамп старше файла и ChangedOnDisk/MustReadBeforeWrite сработают.
**Status:** open

## 2026-10-10 · code-review role:review: При переполнении очереди журнала батч отбрасывается, но _las... [P3]
**Context:** auto-cron `ClaudeCodeReviewWeekly` (provider=ocg), file traymon/src/Program.cs:WriteLogLine (блок lock (_logQueue)), category=correctness
**What:** При переполнении очереди журнала батч отбрасывается, но _lastLogAt уже был продвинут в начале метода. В результате после сброса следующая попытка записи будет только через полный Log.EverySeconds: при медленном писателе (сетевой шар) строки теряются не «по мере переполнения очереди», а целыми интервалами, и реальная частота потерь ниже заявленного буфера в 4 батча. Кроме того, _logDropped только растёт и никогда не сбрасывается, так что диагностика показывает накопленные потери за весь сеанс, а не текущее состояние.
**Evidence:** `if (now - _lastLogAt < _config.Log.EverySeconds * 1000L) return; _lastLogAt = now;`
**Proposal:** Продвигать _lastLogAt только после успешной постановки батча в очередь (или в ветке отказа возвращать _lastLogAt назад / не отбрасывать батч, а ждать следующего тика). _logDropped можно сбрасывать при успешной записи.
**Status:** open

## 2026-10-10 · code-review role:review: Если в конфиге есть required-id, который никогда не сможет п... [P3]
**Context:** auto-cron `ClaudeCodeReviewWeekly` (provider=ocg), file traymon/src/Program.cs:ExpectRequired / Expect, category=optimization
**What:** Если в конфиге есть required-id, который никогда не сможет получить слот (например, id старой схемы: "fan.Fan #1" без '/' или карточный id вида "gpu.0"), Expect() для него делает return, ничего не создав, но слот в _slots так и не появляется. В итоге missing остаётся true на каждом тике, и на каждом тике (каждые 2 с, на горячем пути UI-потока) заново выполняется LINQ-запрос с materialизацией списка ids — постоянная аллокация без какого-либо шанса на изменение результата, пока конфиг не перечитан.
**Evidence:** `var ids = _config.Icons.Where(p => p.Value?.Required == true && !_slots.ContainsKey(p.Key)) .Select(p => p.Key).ToList();`
**Proposal:** Кэшировать список неудовлетворимых required-id и пересчитывать его только при Adopt/ReloadConfig, либо запоминать id, для которых Expect вернулся без создания слота, и исключать их из проверки до следующей перезагрузки конфига.
**Status:** open

## 2026-10-10 · Настройки значков `disk.<номер>` не перенесены на ключ по модели [P3]
**Context:** `TrayMon.json` боевой установки: в `Icons` остались `disk.0`, `disk.1`, `disk.raid`, а живые значки — `disk.<модель>`, `disk.<модель>#2`, `disk.raid.<серийник>`.
**What:** с 1.2.0 id диска строится по модели (`ShowDisks` → `DiskKeys`), но `Migrate` со старого `disk.<индекс>` нет — вопреки правилу «смена схемы идентификатора переносит прежнюю запись». Цвет, подпись и пороги, заданные старым значкам, молча пропали; здесь цвета совпадали с умолчанием, поэтому видимого вреда нет, остались только мёртвые записи.
**Proposal:** скорее wontfix: соответствие индекса и модели после смены порядка дисков не восстановить, а затронуты только установки до 1.2.0. Если делать — однократный перенос при единственном диске модели; иначе вычистить мёртвые `disk.<число>` в `Config.Validate` с заметкой в `LoadNote`.
**Status:** open
