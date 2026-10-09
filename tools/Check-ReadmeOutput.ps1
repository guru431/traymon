<#
.SYNOPSIS
    Сверяет примеры вывода в README.md с тем, что печатает свежая сборка.

.DESCRIPTION
    Примеры `--once` в README отставали от кода уже дважды: печаталось `link 2500 Mbit/s`
    там, где программа выдавала 2384, и `disk.<номер>` там, где идентификатор давно другой.
    Скрипт не сравнивает числа — они у каждого свои — а проверяет, что набор *подписей*
    строк совпадает: каждая метка, которую печатает сборка, встречается в примере README,
    и наоборот. Вторым проходом то же делается для примера `--once --icons`: виды строк
    отчёта слоя иконок и семейства слотов.

    Запускается вручную и перед публикацией (`/github-push`). Ничего не правит.

.EXAMPLE
    pwsh tools\Check-ReadmeOutput.ps1
    powershell -File tools\Check-ReadmeOutput.ps1 -Dll out\TrayMon.dll
#>
[CmdletBinding()]
param(
    # Сборка запускается через рантайм: манифест просит requireAdministrator, и из обычной
    # консоли exe не стартует, а dotnet.exe — стартует и остаётся непривилегированным.
    [string]$Dll = "out\TrayMon.dll",
    [string]$Readme = "README.md"
)

$ErrorActionPreference = "Stop"
Set-Location (Split-Path -Parent $PSScriptRoot)

if (-not (Test-Path $Dll)) { throw "Нет сборки $Dll — сначала dotnet publish src/TrayMon.csproj -c Release -o out" }
if (-not (Test-Path $Readme)) { throw "Нет файла $Readme" }

# Подпись строки — всё до первого разрыва в два и более пробелов (либо до двоеточия),
# с числами, заменёнными на N. Грубо, но не «первое слово»: прежняя версия сводила
# `GPU 0 mem`, `GPU 0 fan` и `GPU 0` к одному `GPU N`, а строки с отступом — состав блока
# `cost, ms` и вывод `--icons` — выбрасывала целиком, поэтому не замечала ни новых
# источников, ни исчезнувших.
function Get-Labels([string[]]$lines) {
    $labels = @()
    foreach ($line in $lines) {
        if ($line -notmatch '\S') { continue }
        $text = $line.Trim()
        # Подпись: до двоеточия либо до первого двойного пробела.
        if ($text -match '^([^:]{1,30}):') { $label = $Matches[1] }
        else { $label = ($text -split '\s{2,}')[0] }
        $label = ($label -replace '\d+', 'N').Trim()
        # Хвост значения без разделителя («uptime N.N h», «GPU N mem N %») — снимаем ровно
        # одно замыкающее «число (+ единица)». Прежнее «оставить первое слово» схлопывало
        # `GPU N mem` и `GPU N fan` в тот же `GPU` — ровно то, что комментарий выше объявлял
        # исправленным, — и расхождение состава GPU-строк не обнаруживалось вовсе.
        $words = @($label -split '\s+')
        if ($words.Count -gt 2 -and $words[-1] -notmatch '^N(\.N)?$' -and $words[-2] -match '^N(\.N)?$') {
            $words = $words[0..($words.Count - 3)]   # число вместе с единицей измерения
        }
        if ($words.Count -gt 1 -and $words[-1] -match '^N(\.N)?$') {
            $words = $words[0..($words.Count - 2)]
        }
        # Буква тома («volume   C:») — такой же машинно-зависимый хвост, как номер карты,
        # и её тоже надо свести: иначе на машине с лишним томом скрипт сообщает о расхождении
        # с README там, где расходится только железо.
        if ($words.Count -gt 1 -and $words[-1] -match '^[^\W\d_]$') {
            $words = $words[0..($words.Count - 2)]
        }
        $label = $words -join ' '
        if ($label) { $labels += $label }
    }
    $labels | Sort-Object -Unique
}

Write-Host "Запуск: dotnet $Dll --once"
# Windows PowerShell 5.1 превращает каждую строку stderr нативной команды под 2>&1 в
# NativeCommandError, и при "Stop" первая же из них — сообщение хоста dotnet, необработанное
# исключение — обрывала скрипт вместо сверки. В pwsh 7 так не бывает, отсюда разное поведение.
$ErrorActionPreference = "Continue"
$output = & dotnet $Dll --once 2>&1 | ForEach-Object { "$_" }
$ErrorActionPreference = "Stop"

# Пример в README — первый блок ``` после строки с --once
$readmeLines = Get-Content $Readme
$blocks = @()
$inBlock = $false
$current = @()
foreach ($line in $readmeLines) {
    if ($line -match '^\s*```') {
        if ($inBlock) { $blocks += , $current; $current = @() }
        $inBlock = -not $inBlock
        continue
    }
    if ($inBlock) { $current += $line }
}

$example = $blocks | Where-Object { ($_ -join "`n") -match '(?m)^counter in use:' } | Select-Object -First 1
if (-not $example) { throw "В $Readme нет блока с примером вывода --once (ищется строка 'counter in use:')" }

$fromCode = Get-Labels $output
$fromDocs = Get-Labels $example

$missing = $fromCode | Where-Object { $_ -notin $fromDocs }
$stale = $fromDocs | Where-Object { $_ -notin $fromCode }

# Строки, которых на конкретной машине может не быть: нет карты NVIDIA, нет ИБП, нет RAID,
# нет батареи, запуск без прав администратора. Их отсутствие в выводе — не расхождение
# с документом.
$optional = @('GPU', 'GPU N mem', 'GPU N fan', 'raid', 'ups', 'disk', 'fan', 'battery',
              'ring-N device', 'ring-N')
$stale = $stale | Where-Object { $_ -notin $optional }
$missing = $missing | Where-Object { $_ -notin $optional }

$failed = $false
if ($missing) {
    Write-Host "`nЕсть в выводе, нет в README:" -ForegroundColor Yellow
    $missing | ForEach-Object { Write-Host "  $_" }
    $failed = $true
}
if ($stale) {
    Write-Host "`nЕсть в README, нет в выводе (и это не опциональное железо):" -ForegroundColor Yellow
    $stale | ForEach-Object { Write-Host "  $_" }
    $failed = $true
}
if (-not $failed) { Write-Host "`nПример --once в README совпадает с выводом по составу строк." -ForegroundColor Green }

# ---- Второй проход: пример `--once --icons` ----
#
# Формат отчёта слоя иконок уже менялся (код возврата, строка `shell not touched`), а сверял
# скрипт только `--once`. Здесь каждая строка отчёта относится к одному виду: переход по тикам,
# итог `icons:`, строка слота, `shell not touched`, `tick N ms`, `last error`. Строка, которая ни
# к одному виду не подходит, — расхождение формата, где бы она ни нашлась. Виды строк сверяются в
# обе стороны. Слоты — только в одну: пример в README намеренно короткий, но каждое семейство в нём
# обязано существовать в выводе (или быть железом, которого на этой машине может не быть), —
# так ловится идентификатор старой схемы вроде `disk.0`.
function Get-Family([string]$id) {
    foreach ($prefix in 'gpu.temp.', 'fan.gpu.', 'disk.raid.', 'gpu.', 'vram.', 'disk.', 'fan.', 'net.', 'vol.', 'free.') {
        if ($id.StartsWith($prefix)) { return "$prefix*" }
    }
    return $id
}

function Get-IconLabels([string[]]$lines) {
    $labels = @()
    $started = $false
    foreach ($line in $lines) {
        if ($line -notmatch '\S') { continue }
        # Отчёт слоя иконок начинается с первого перехода или с итоговой строки.
        if (-not $started) {
            if ($line -match '^\s*tick \d+:' -or $line -match '^icons: ') { $started = $true } else { continue }
        }
        if ($line -match '^\s*tick \d+:') { $labels += 'tick N:'; continue }
        if ($line -match '^icons: ') { $labels += (($line -replace '\d+', 'N') -replace '\s+', ' ').Trim(); continue }
        if ($line -match '^\s+(on|off)\s+[0-9A-Fa-f]{2}\s+(\S+)') { $labels += 'slot ' + (Get-Family $Matches[2]); continue }
        if ($line -match '^shell not touched') { $labels += 'shell not touched'; continue }
        if ($line -match '^tick [\d.]+ ms') { $labels += 'tick N ms'; continue }
        if ($line -match '^\s+last error:') { $labels += 'last error'; continue }
        if ($line -match ': без значка осталось') { $labels += 'overflow'; continue }
        $labels += "?? $($line.Trim())"
    }
    $labels | Sort-Object -Unique
}

Write-Host "`nЗапуск: dotnet $Dll --once --icons --ticks 2"
$ErrorActionPreference = "Continue"
$iconsOutput = & dotnet $Dll --once --icons --ticks 2 2>&1 | ForEach-Object { "$_" }
$iconsExit = $LASTEXITCODE
$ErrorActionPreference = "Stop"
if ($iconsExit -ne 0) {
    Write-Host "`n--once --icons вернул код ${iconsExit}: слой иконок бросил исключение." -ForegroundColor Yellow
    $failed = $true
}

$iconsExample = $blocks | Where-Object { ($_ -join "`n") -match '(?m)^icons: \d+ shown of' } | Select-Object -First 1
if (-not $iconsExample) { throw "В $Readme нет блока с примером вывода --once --icons (ищется строка 'icons: N shown of')" }

$fromCode = Get-IconLabels $iconsOutput
$fromDocs = Get-IconLabels $iconsExample
$optionalKinds = @('tick N:', 'last error', 'overflow')
$optionalFamilies = @('gpu.*', 'vram.*', 'gpu.temp.*', 'fan.gpu.*', 'disk.*', 'disk.raid.*', 'fan.*',
                      'net.*', 'vol.*', 'free.*', 'cpu.temp', 'ups', 'battery')

$missing = $fromCode | Where-Object {
    $_ -notlike 'slot *' -and $_ -notlike '?? *' -and $_ -notin $fromDocs -and $_ -notin $optionalKinds
}
$stale = $fromDocs | Where-Object {
    if ($_ -like 'slot *') { $_ -notin $fromCode -and $_.Substring(5) -notin $optionalFamilies }
    else { $_ -notin $fromCode -and $_ -notin $optionalKinds }
}
$unknown = $fromCode | Where-Object { $_ -like '?? *' }

if ($missing) {
    Write-Host "`n--icons: вид строки есть в выводе, нет в README:" -ForegroundColor Yellow
    $missing | ForEach-Object { Write-Host "  $_" }
    $failed = $true
}
if ($stale) {
    Write-Host "`n--icons: есть в README, нет в выводе (и это не опциональное железо):" -ForegroundColor Yellow
    $stale | ForEach-Object { Write-Host "  $_" }
    $failed = $true
}
if ($unknown) {
    Write-Host "`n--icons: строки, формат которых скрипт не знает, — поправьте его вместе с README:" -ForegroundColor Yellow
    $unknown | ForEach-Object { Write-Host "  $_" }
    $failed = $true
}
if (-not $failed) {
    Write-Host "`nПример --once --icons в README совпадает с выводом по виду строк и семействам слотов." -ForegroundColor Green
    exit 0
}
Write-Host "`nПоправьте пример в README.md (и, если строка описана и там, в README.en.md)."
exit 1
