@echo off
chcp 65001 >nul
setlocal

rem Teplov Print: removes the per-user installation made by install.cmd.
rem User settings in %APPDATA%\AcadBatchPlot are kept.

set "FAILED="
for %%D in ("%APPDATA%\Autodesk\ApplicationPlugins\TeplovPrint.bundle" "%APPDATA%\TeplovPrint") do (
    if exist "%%~D" (
        rmdir /s /q "%%~D"
        if exist "%%~D" (
            echo Не удалось удалить %%~D. Закройте AutoCAD и nanoCAD.
            set "FAILED=1"
        ) else (
            echo Удалено: %%~D
        )
    )
)

if defined FAILED (
    pause
    exit /b 1
)

echo Готово. Настройки в %APPDATA%\AcadBatchPlot сохранены.
pause
exit /b 0
