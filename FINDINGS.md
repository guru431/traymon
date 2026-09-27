# Findings — TrayMon
Побочные находки, только `open`. Ревизия: MonthlyStratReview 1-го числа. Stale >90 дней → alert.
Новые записи сверху. Выполненные — удаляются (след в `git log`), отклонённые — переносятся в [FINDINGS-archive.md](FINDINGS-archive.md).

## 2026-09-26 · code-review role:review: BatteryLifePercent == 255 означает «неизвестно» (как и ACLin... [P3]
**Context:** auto-cron `ClaudeCodeReviewWeekly` (provider=ocg), file traymon/src/Sensors.cs:ок. 250-260 (BatterySensor.Read), category=correctness
**What:** BatteryLifePercent == 255 означает «неизвестно» (как и ACLineStatus = 255, который корректно обработан отдельной веткой), но условие отбрасывает всю батарею целиком: пользователь с батареей, чей процент временно неизвестен, теряет иконку и событие вместо серого/неизвестного состояния.
**Evidence:** `if (!GetSystemPowerStatus(out var s) || s.BatteryFlag == NoBattery || s.BatteryLifePercent > 100)`
**Proposal:** Различать 255 (unknown) и >100 (мусор): при 255 публиковать BatteryReading с Charge = null/Unknown-уровнем, а не null.
**Status:** open

## 2026-09-26 · code-review role:review: Если Marshal.AllocHGlobal бросит исключение (нехватка памяти... [P3]
**Context:** auto-cron `ClaudeCodeReviewWeekly` (provider=ocg), file traymon/src/PerfSensors.cs:ок. 130-135 (PdhQuery.ReadArray), category=bug
**What:** Если Marshal.AllocHGlobal бросит исключение (нехватка памяти) после FreeHGlobal, поле _buffer останется висеть на уже освобождённом адресе, а _bufferSize — обновлённым; следующий вызов ReadArray передаст освобождённый указатель в PDH (use-after-free), а Dispose попытается освободить его повторно.
**Evidence:** `if (_buffer != IntPtr.Zero) Marshal.FreeHGlobal(_buffer); _bufferSize = size; _buffer = Marshal.AllocHGlobal((int)size);`
**Proposal:** Обновлять _buffer только после успешного AllocHGlobal: сначала alloc во временную переменную, затем FreeHGlobal старого и присвоение.
**Status:** open

## 2026-09-26 · code-review role:review: При отказе GlobalMemoryStatusEx обнуляются MemLoad и CommitU... [P3]
**Context:** auto-cron `ClaudeCodeReviewWeekly` (provider=ocg), file traymon/src/Sensors.cs:ок. 330-340 (MemorySensor.Read, ветка ошибки), category=inconsistency
**What:** При отказе GlobalMemoryStatusEx обнуляются MemLoad и CommitUsedGb, но MemUsedGb/MemTotalGb/CommitTotalGb остаются от предыдущего успешного опроса: иконка уходит в серый, а тултип продолжает показывать устаревшие гигабайты — тот самый «цвет поверх неизмеренных данных», который код везде устраняет.
**Evidence:** `r.MemLoad = null; r.CommitUsedGb = null; return;`
**Proposal:** В той же ветке обнулять MemUsedGb/MemTotalGb/CommitTotalGb (или публиковать весь блок как недоступный).
**Status:** open

## 2026-09-26 · code-review role:review: Список из пяти OID продублирован дважды: инициализатор поля ... [P3]
**Context:** auto-cron `ClaudeCodeReviewWeekly` (provider=ocg), file traymon/src/UpsSensor.cs:ок. 75-85, category=inconsistency
**What:** Список из пяти OID продублирован дважды: инициализатор поля _oids и статический массив AllOids — два независимых источника истины; при добавлении/изменении OID в одном месте Rediscover() начнёт опрашивать другой набор, чем стартовый.
**Evidence:** `private readonly List<string> _oids = new() { CapacityOid, RunTimeOid, StatusOid, LoadOid, ReplaceOid };`
**Proposal:** Инициализировать _oids из AllOids: `private readonly List<string> _oids = new(AllOids);` (перенести объявление AllOids выше).
**Status:** open

## 2026-09-26 · code-review role:review: Скрипт заявляет поддержку запуска через `powershell -File` (... [P3]
**Context:** auto-cron `ClaudeCodeReviewWeekly` (provider=ocg), file traymon/tools/Check-ReadmeOutput.ps1:37 и 63, category=portability
**What:** Скрипт заявляет поддержку запуска через `powershell -File` (Windows PowerShell 5.1), но при $ErrorActionPreference = "Stop" перенаправление stderr нативной команды (2>&1) в 5.1 оборачивает первую же строку stderr в NativeCommandError и завершает скрипт, вместо сравнения вывода. В pwsh 7 поведение другое — скрипт работает по-разному на двух заявленных оболочках.
**Evidence:** `$ErrorActionPreference = "Stop"`
**Proposal:** Либо явно требовать pwsh 7, либо на время вызова dotnet временно ослаблять $ErrorActionPreference / собирать stderr отдельно.
**Status:** open

## 2026-09-26 · code-review role:review: Если пользователь создал только seed (.sanitize-patterns.md)... [P3]
**Context:** auto-cron `ClaudeCodeReviewWeekly` (provider=ocg), file traymon/scripts/enable-guard.sh:ок. 60-75, category=bug
**What:** Если пользователь создал только seed (.sanitize-patterns.md), а живого denylist (.sanitize-patterns) нет, скрипт попадает в ветку else и печатает "[skip] ... already present", а затем финальное "Done. ... secret-guard active" с rc=0 — хотя персональная проверка в хуках будет пропускаться (hooks лишь печатают WARN при каждом коммите).
**Evidence:** `elif [ ! -e "$live" ] && [ ! -e "$seed" ]; then`
**Proposal:** В ветке else различать случаи: если $live отсутствует — печатать warn и ставить rc=1 (или явно предупреждать, что personal-data check не активен), а не сообщать об успешной активации.
**Status:** open

## 2026-09-26 · code-review role:review: Временные репозитории из mktemp -d удаляются только одним `r... [P3]
**Context:** auto-cron `ClaudeCodeReviewWeekly` (provider=ocg), file traymon/tools/Test-GitHooks.sh:44 и 152, category=bug
**What:** Временные репозитории из mktemp -d удаляются только одним `rm -rf "$repo"` в самом конце; при `set -eu` любой незащищённый сбой (например, git rev-parse в тесте 6) прерывает скрипт и оставляет временные каталоги на диске.
**Evidence:** `repo=$(mktemp -d)`
**Proposal:** Поставить trap 'rm -rf "$repo"' EXIT (или cleanup-функцию) сразу после создания репозитория.
**Status:** open

## 2026-09-26 · code-review role:review: Вызов GPU-сенсора выполняется на UI-потоке внутри Tick() вне... [P2]
**Context:** auto-cron `ClaudeCodeReviewWeekly` (provider=ocg), file traymon/src/Program.cs:Tick(), строка с Due(ref _gpuAt, ...), category=bug
**What:** Вызов GPU-сенсора выполняется на UI-потоке внутри Tick() вне per-family try/catch. Собственные комментарии кода признают, что NVML бросает исключения («NVML after a driver reset», «a source that throws — NVML after a driver reset»), и именно для этого семейства сделан отдельный catch в цикле _families — но _gpu.Read стоит до цикла, и его исключение ловится только внешним catch в OnTick. В результате весь остаток тика пропускается: не запускаются Spawn-читатели (slow/disks/raid/ups/space), не выполняется ни одна семья, Fade(), Housekeeping(), RefreshStats() и WriteLogLine(). При стабильно падающем NVML это повторяется каждый тик — иконки дисков/ИБП/вентиляторов перестают обновляться и уходить в Fade, CSV-журнал молчит. Дополнительно ShowGpus() — единственная семья без проверки Fresh(_r.GpusAt, ...): при упавшем чтении старый список _r.Gpus показывается с обычными цветами неограниченно долго, что противоречит всей схеме MaxAgeMs/Fresh, введённой именно против «hours-old numbers in normal colours».
**Evidence:** `if (Due(ref _gpuAt, GpuEveryMs)) { _gpu.Read(_r); _r.GpusAt = Environment.TickCount64; }`
**Proposal:** Обернуть _gpu.Read в собственный try/catch с Trouble(ex, "GPU") (как сделано для семей), либо перенести чтение GPU в Spawn с флагом _gpuRefreshRunning по образцу остальных; в ShowGpus добавить проверку Fresh(_r.GpusAt, GpuEveryMs) с выходом, чтобы Fade давал серую плашку с причиной по аналогии с ShowDisks/ShowFans.
**Status:** open

## 2026-09-26 · code-review role:review: Периодическое сохранение (каждые 15 тиков) и сохранение в Di... [P2]
**Context:** auto-cron `ClaudeCodeReviewWeekly` (provider=ocg), file traymon/src/Program.cs:Housekeeping(), блок периодического сохранения, category=inconsistency
**What:** Периодическое сохранение (каждые 15 тиков) и сохранение в Dispose пишут _config на диск без проверки ChangedOnDisk, тогда как Persist() специально перечитывает файл при ChangedOnDisk, чтобы не затереть чужую правку. Сценарий: пользователь отредактировал TrayMon.json, но debounce (1 с) ещё не истёк или тик ещё не дошёл до PickUpConfigEdit — Housekeeping в этом же тике выполняется РАНЬШЕ PickUpConfigEdit (порядок в Tick: Housekeeping → ... → PickUpConfigEdit) и сохраняет in-memory объект поверх правки, безвозвратно теряя её. PickUpConfigEdit затем видит Stamp от собственной записи и правку не замечает. То же относится к сохранению в Dispose и в EnsureSomethingVisible.
**Evidence:** `if (!_configDirty || _dry || _tick % 15 != 0) return; if (_config.Save(out var error)) { _configDirty = false; _firstRun = false; return; }`
**Proposal:** Перед Save в Housekeeping (и в Dispose/EnsureSomethingVisible) проверять _config.ChangedOnDisk и при true сначала выполнять ту же логику слияния, что в Persist (перечитать fresh, перенести Slots и dirty-записи), либо отложить сохранение на следующий тик, дав PickUpConfigEdit шанс отработать.
**Status:** open

## 2026-09-26 · code-review role:review: Расхождение с Persist: Persist копирует таблицы слотов безус... [P3]
**Context:** auto-cron `ClaudeCodeReviewWeekly` (provider=ocg), file traymon/src/Program.cs:ReloadConfig(), копирование таблиц слотов, category=inconsistency
**What:** Расхождение с Persist: Persist копирует таблицы слотов безусловно (foreach ... fresh.Slots[pool] = table), а ReloadConfig — только если пул отсутствует в fresh. Если GuidPool выдал новый GUID (Set → _dirty), а сохранение ещё не прошло (пишется раз в 15 тиков), то таблица пула уже есть в файле со старым составом — условие ContainsKey истинно, и несохранённое назначение GUID теряется: после перезапуска слот уйдёт другому устройству, позиция в трее и видимость собьются. Именно эту проблему назначение в файле и должно было устранить.
**Evidence:** `foreach (var (pool, table) in _config.Slots) if (!fresh.Slots.ContainsKey(pool)) fresh.Slots[pool] = table;`
**Proposal:** Копировать таблицы слотов безусловно, как в Persist: foreach (var (pool, table) in _config.Slots) fresh.Slots[pool] = table; — слоты «наши, никогда не редактируются вручную», поэтому свежее in-memory состояние всегда приоритетно.
**Status:** open

