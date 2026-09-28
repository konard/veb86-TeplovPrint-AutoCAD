# Сборка и выпуск

## Требования

- Windows;
- .NET SDK, указанный в `global.json`;
- установленные reference assemblies AutoCAD для целевой версии;
- восстановленные NuGet-пакеты.

## Сборка

Собрать всё решение:

```powershell
dotnet build LA.BatchPlot.sln
```

Собрать конкретную целевую версию:

```powershell
.\scripts\build-dll.ps1 -Target AutoCAD -Configuration Release
.\scripts\build-dll.ps1 -Target AutoCAD2025 -Configuration Release
```

Для обеих сборок используйте `-Target All`. Скрипт не создаёт альтернативные каталоги: результаты всегда попадают в `bin-acad` и `bin-acad2025-2027`.

## Пакет выпуска

`scripts/package-release.ps1` формирует локальный выпуск только для AutoCAD из двух текущих папок сборки. Перед запуском убедитесь, что обе DLL собраны:

```powershell
.\scripts\package-release.ps1 -Version 1.0.0
```

Архивы создаются в папке `release` и не входят в исходный код.
