# AGENTS — TrayMon

Что программа делает — [README.md](README.md). Правила и ловушки — [CLAUDE.md](CLAUDE.md),
читать до правки кода. Почему правила такие — [docs/design-notes.md](docs/design-notes.md),
как мерить цену правки — [docs/measuring.md](docs/measuring.md).

## Сборка и проверка

```powershell
dotnet publish src/TrayMon.csproj -c Release -o out
out\TrayMon.exe --once                    # из консоли, открытой ОТ АДМИНИСТРАТОРА: значения + цена источников
out\TrayMon.exe --once --icons --ticks 3  # тики слоя иконок; ненулевой код = исключение
dotnet out\TrayMon.dll --once             # то же без прав: манифест берётся у dotnet.exe
dotnet test tests/TrayMon.Tests/TrayMon.Tests.csproj
sh tools/Test-GitHooks.sh                 # секрет-хуки, во временных репозиториях
```

## Без чего сломаешь продукт

- Цена наблюдения — единственное преимущество (0.52 % ядра): новый источник, более частый опрос
  или правка отрисовки — только с замером по `docs/measuring.md`.
- GUID иконок в `src/Program.cs` не менять; GUID выдаёт только `GuidPool` по аппаратному ключу
  источника; смена ключа — через `TrayApp.Migrate` → `Config.Rename`.
- Уровень тревоги считает только `Alarm.LevelOf`. Нет ни значения, ни доказанного состояния —
  слот не живой; датчик при отказе обнуляет своё поле.
- Процесс держит админский токен: внешние программы и DLL — по полному пути, автозапуск — только
  из защищённой папки (`Autostart.InstallUnsafe`).
- Интерфейс и README — по-русски, код и комментарии — по-английски. README две, поведение
  правится в обеих. Репозиторий зеркалится в публичный GitHub: весь вывод `--once` идёт через
  `Redactor`, `git push` подтверждает человек.
