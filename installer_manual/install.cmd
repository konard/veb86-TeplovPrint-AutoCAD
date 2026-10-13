@echo off
chcp 65001 >nul
setlocal

rem Teplov Print: per-user installation, administrator rights are not required.
rem Copies AppData\Roaming from the archive into %APPDATA% of the current user.

set "SOURCE=%~dp0AppData\Roaming"
if not exist "%SOURCE%\Autodesk\ApplicationPlugins\TeplovPrint.bundle\PackageContents.xml" (
    echo Не найдена папка AppData рядом с install.cmd. Распакуйте архив полностью.
    goto :fail
)

echo Установка Teplov Print в %APPDATA% ...
robocopy "%SOURCE%" "%APPDATA%" /E /R:1 /W:1 /NFL /NDL /NJH /NJS /NP
if errorlevel 8 (
    echo Ошибка копирования. Закройте AutoCAD и nanoCAD и запустите install.cmd снова.
    goto :fail
)

echo.
echo Готово.
echo   AutoCAD 2021: %APPDATA%\Autodesk\ApplicationPlugins\TeplovPrint.bundle
echo   nanoCAD 24:   %APPDATA%\TeplovPrint\nanoCAD24\NcadBatchPlot.dll
echo Подробности - в файле INSTALL.md.
pause
exit /b 0

:fail
pause
exit /b 1
