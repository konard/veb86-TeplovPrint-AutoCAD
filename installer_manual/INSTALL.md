# Teplov Print — ручная установка (AutoCAD 2021 и nanoCAD 24)

Архив предназначен для установки **без прав администратора**: все файлы размещаются
только в профиле текущего пользователя (`C:\Users\<имя>\AppData\Roaming`, он же `%APPDATA%`).

## Состав архива

| Путь в архиве | Назначение | Куда устанавливается |
| --- | --- | --- |
| `AppData\Roaming\Autodesk\ApplicationPlugins\TeplovPrint.bundle\` | пакет для AutoCAD (`PackageContents.xml` + DLL) | `%APPDATA%\Autodesk\ApplicationPlugins\TeplovPrint.bundle\` |
| `…\TeplovPrint.bundle\Contents\AutoCAD2015-2024\AcadBatchPlot.dll` | плагин для **AutoCAD 2021** (подходит для AutoCAD 2015–2024 x64) | — |
| `…\TeplovPrint.bundle\Contents\AutoCAD2025-2027\AcadBatchPlot.Core.dll` | плагин для AutoCAD 2025–2027 x64 | — |
| `AppData\Roaming\TeplovPrint\nanoCAD24\NcadBatchPlot.dll` | плагин для **nanoCAD 24** x64 | `%APPDATA%\TeplovPrint\nanoCAD24\` |
| `install.cmd` | копирует папку `AppData` в профиль пользователя | — |
| `uninstall.cmd` | удаляет установленные файлы из профиля пользователя | — |

Рядом с каждой DLL лежат её зависимости (`Newtonsoft.Json.dll`, `PdfSharp*.dll`,
`PianNoCN.dll`, `pdfium.dll` и т. д.) и папка `Plotters` с конфигурациями плоттеров.
Не перемещайте DLL отдельно от этих файлов.

## 1. Распаковка

1. Закройте AutoCAD и nanoCAD.
2. Если ранее был установлен `Teplov-Print-AutoCAD-Setup-*.exe`, удалите его через
   «Параметры → Приложения», чтобы AutoCAD не загрузил две копии плагина.
3. Распакуйте архив в любую временную папку.
4. Windows может пометить скачанные файлы как полученные из интернета. Перед распаковкой
   откройте свойства ZIP-архива и установите флажок **«Разблокировать»**, иначе .NET может
   отказаться загружать DLL.

## 2. Копирование файлов в профиль пользователя

**Вариант А — автоматически.** Запустите `install.cmd` двойным щелчком (права администратора
не нужны). Скрипт скопирует содержимое папки `AppData\Roaming` архива в `%APPDATA%`.

**Вариант Б — вручную.**

1. Нажмите `Win+R`, введите `%USERPROFILE%` и нажмите Enter — откроется `C:\Users\<имя>`.
2. Скопируйте туда папку `AppData` из архива и подтвердите слияние папок.

После копирования должны существовать файлы:

```text
%APPDATA%\Autodesk\ApplicationPlugins\TeplovPrint.bundle\PackageContents.xml
%APPDATA%\Autodesk\ApplicationPlugins\TeplovPrint.bundle\Contents\AutoCAD2015-2024\AcadBatchPlot.dll
%APPDATA%\TeplovPrint\nanoCAD24\NcadBatchPlot.dll
```

## 3. AutoCAD 2021

AutoCAD автоматически загружает пакеты из папки пользователя
`%APPDATA%\Autodesk\ApplicationPlugins`, поэтому достаточно запустить AutoCAD.

1. Запустите AutoCAD 2021.
2. Если появится предупреждение безопасности о загрузке `AcadBatchPlot.dll`, выберите
   **«Всегда загружать»** либо добавьте папку в доверенные пути (команда `OPTIONS` →
   «Файлы» → «Надёжные папки» или системная переменная `TRUSTEDPATHS`):

   ```text
   %APPDATA%\Autodesk\ApplicationPlugins\TeplovPrint.bundle\...
   ```

3. Откройте меню **Teplov → Пакетная печать** или выполните команду `ZBP_SHOW_PANEL`.

Если плагин не загрузился автоматически, выполните `NETLOAD` и выберите
`%APPDATA%\Autodesk\ApplicationPlugins\TeplovPrint.bundle\Contents\AutoCAD2015-2024\AcadBatchPlot.dll`.

## 4. nanoCAD 24

1. Запустите nanoCAD 24 x64.
2. Выполните команду `NETLOAD` (или `APPLOAD`) и выберите файл
   `%APPDATA%\TeplovPrint\nanoCAD24\NcadBatchPlot.dll`.
   В поле имени файла можно вставить путь целиком — `%APPDATA%` будет раскрыт.
3. Выполните команду `ZBP_SHOW_PANEL`.

Чтобы плагин загружался при каждом запуске nanoCAD, добавьте
`NcadBatchPlot.dll` в список автозагрузки окна `APPLOAD` («Загрузка приложений» →
«Автозагрузка»).

Особенности nanoCAD:

- команды плагина доступны из командной строки (`ZBP_SHOW_PANEL`, `ZBP_SETTINGS` и т. д.,
  полный список — в `README.md` проекта); пункт меню **Teplov** создаётся, только если
  интерфейс nanoCAD это позволяет;
- команда `ZBP_INSTALL_AUTOLOAD` рассчитана на AutoCAD; для nanoCAD используйте автозагрузку
  `APPLOAD`, описанную выше.

## 5. Обновление

1. Закройте AutoCAD и nanoCAD.
2. Распакуйте новую версию архива и повторите шаг 2 с заменой файлов.

## 6. Удаление

1. Закройте AutoCAD и nanoCAD.
2. Запустите `uninstall.cmd` или удалите вручную папки:

   ```text
   %APPDATA%\Autodesk\ApplicationPlugins\TeplovPrint.bundle
   %APPDATA%\TeplovPrint
   ```

3. В nanoCAD уберите `NcadBatchPlot.dll` из списка автозагрузки `APPLOAD`.

Пользовательские настройки плагина хранятся отдельно в `%APPDATA%\AcadBatchPlot`; удалите
эту папку, если настройки больше не нужны.
