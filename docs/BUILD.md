# Сборка и выпуск

## Требования

- Windows;
- .NET SDK, указанный в `global.json`;
- восстановленные NuGet-пакеты (reference assemblies AutoCAD берутся из пакетов `AutoCAD.NET`/`AutoCAD.NET.Core`, nanoCAD 24 — из `ModPlus.nanoCAD.API.24`, установленный CAD для сборки не нужен).

## Сборка

Собрать всё решение:

```powershell
dotnet build LA.BatchPlot.sln
```

Собрать конкретную целевую версию:

```powershell
.\scripts\build-dll.ps1 -Target AutoCAD -Configuration Release
.\scripts\build-dll.ps1 -Target AutoCAD2025 -Configuration Release
.\scripts\build-dll.ps1 -Target NanoCAD24 -Configuration Release
```

Для всех сборок используйте `-Target All`. Скрипт не создаёт альтернативные каталоги: результаты всегда попадают в `bin-acad`, `bin-acad2025-2027` и `bin-nanocad24`.

Сборка nanoCAD 24 (`src/NcadBatchPlot`, .NET 6) компилирует те же исходники с символами `AUTOCAD;NANOCAD`: пространства имён `Autodesk.AutoCAD.*` заменяются на `HostMgd.*`/`Teigha.*` в ветках `#if NANOCAD`. Ветка `NANOCAD` должна проверяться раньше `AUTOCAD` и `ACAD_CORE`.

## Пакет выпуска

`scripts/package-release.ps1` формирует локальный выпуск из трёх папок сборки. Перед запуском соберите все DLL (`-Target All`):

```powershell
.\scripts\package-release.ps1 -Version 1.0.0
.\scripts\test-release-package.ps1 -Version 1.0.0
```

Архивы создаются в папке `release` и не входят в исходный код:

- `Teplov-Print-AutoCAD2015-2024-v<версия>.zip`, `Teplov-Print-AutoCAD2025-2027-v<версия>.zip`, `Teplov-Print-nanoCAD24-v<версия>.zip` — содержимое папок сборки;
- `Teplov-Print-manual-install-v<версия>.zip` — ручная установка в профиль пользователя: структура архива повторяет `%USERPROFILE%` (`AppData\Roaming\...`), внутри `INSTALL.md` из `installer_manual`, `install.cmd` и `uninstall.cmd`.

`test-release-package.ps1` проверяет состав архивов (наличие DLL и зависимостей, соответствие `PackageContents.xml`, отсутствие `.pdb` и чужих `runtimes`).

## Автоматическая сборка и выпуск

Workflow `.github/workflows/build.yml` на каждый push и pull request собирает все DLL на `windows-latest`, формирует и проверяет архивы и прикладывает к запуску артефакты `TeplovPrint-AutoCAD2021-dll`, `TeplovPrint-nanoCAD24-dll` и `release-archives`.

Выпуск GitHub `v<версия>` с архивами публикуется автоматически при push в основную ветку, если выпуска с текущей `<Version>` ещё нет, при push тега `v<версия>` или при ручном запуске workflow. Чтобы выпустить новую версию, измените `<Version>` (и связанные `AssemblyVersion`/`FileVersion`) во всех `.csproj`.
