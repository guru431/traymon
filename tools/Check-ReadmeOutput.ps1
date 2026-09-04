<#
.SYNOPSIS
    Сверяет примеры вывода в README.md с тем, что печатает свежая сборка.

.DESCRIPTION
    Примеры `--once` в README отставали от кода уже дважды: печаталось `link 2500 Mbit/s`
    там, где программа выдавала 2384, и `disk.<номер>` там, где идентификатор давно другой.
    Скрипт не сравнивает числа — они у каждого свои — а проверяет, что набор *подписей*
    строк совпадает: каждая метка, которую печатает сборка, встречается в примере README,
    и наоборот.

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

# Подпись строки — её первое слово, с числами, заменёнными на N. Нарочно грубо: цель —
# заметить исчезнувшую, переименованную или новую *строку*, а не сверить значения, которые
# у каждой машины свои.
function Get-Labels([string[]]$lines) {
    $labels = @()
    foreach ($line in $lines) {
        if ($line -notmatch '\S') { continue }
        if ($line -match '^\s') { continue }
        $label = ($line -split '\s+')[0].Trim()
        $label = $label -replace '\d+', 'N'
        if ($label) { $labels += $label }
    }
    $labels | Sort-Object -Unique
}

Write-Host "Запуск: dotnet $Dll --once"
$output = & dotnet $Dll --once 2>&1 | ForEach-Object { "$_" }

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
$optional = @('GPU', 'raid', 'ups', 'disk', 'fan', 'battery', 'ring-N')
$stale = $stale | Where-Object { $_ -notin $optional }
$missing = $missing | Where-Object { $_ -notin $optional }

if ($missing) {
    Write-Host "`nЕсть в выводе, нет в README:" -ForegroundColor Yellow
    $missing | ForEach-Object { Write-Host "  $_" }
}
if ($stale) {
    Write-Host "`nЕсть в README, нет в выводе (и это не опциональное железо):" -ForegroundColor Yellow
    $stale | ForEach-Object { Write-Host "  $_" }
}
if (-not $missing -and -not $stale) {
    Write-Host "`nПример в README совпадает с выводом по составу строк." -ForegroundColor Green
    exit 0
}
Write-Host "`nПоправьте пример в README.md (и, если строка описана и там, в README.en.md)."
exit 1
