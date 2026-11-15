# Происхождение и зависимости

Дополнение: HTML-оболочка адаптирована из предоставленного владельцем C:\Git\UI.TechnicalShell. Общие CSS, ui-tooltip.js/css, локальный шрифт Tabler и его MIT license перенесены с исходными комментариями в wwwroot/technical-shell. Исходная библиотека не изменяется; исходники предметных приложений LinguaUnlock/SPLA не переносились. Данные API, редактор графа и маршрутизация остаются в NaSeqMen. Порядок интеграции — docs/technical-shell.md.

Отдельное окно HTML использует официальный NuGet Microsoft.Web.WebView2 1.0.4314.35 и WinForms на .NET 11; WebView2 Runtime — компонент Microsoft, не перелицензируется MIT проекта. Старый WPF-редактор остаётся отдельным проектом.

Собственный исходный код NaSeqMen написан независимо в ходе реализации первой альфы. Лицензия — MIT, SPDX в исходниках. Исходники AutoHotkey, AutoHotInterception и Interception не копируются, не переводятся и не включаются в проект. Личный KOE.ahk использован только для определения поведения кнопок; сам файл не входит в проект.

| Зависимость / контракт | Использование | Источник |
|---|---|---|
| .NET / ASP.NET Core / WPF | SDK 11.0.100, framework-dependent приложения .NET 11; установлен runtime 11.0.0 | https://github.com/dotnet |
| Tabler Icons 3.34.1 | Оригинальные SVG player-play, player-stop, ban; WPF: Assets/Tabler, HTML: wwwroot/icons; MIT notice сохранён в обоих каталогах | https://github.com/tabler/tabler-icons/tree/v3.34.1 |
| Microsoft.CodeAnalysis.CSharp.Scripting 4.14.0 | Компиляция пользовательского C#; NuGet, без копирования исходников | https://github.com/dotnet/roslyn |
| Win32 Raw Input / SendInput | Собственные объявления P/Invoke и обработка по публичному API Windows | https://learn.microsoft.com/windows/win32/inputdev/raw-input |
| Interception | Опциональный вызов опубликованного ABI уже установленной DLL для временного обнаружения | https://github.com/oblitum/Interception |
| OBS WebSocket v5 | Собственный клиент по опубликованному протоколу | https://github.com/obsproject/obs-websocket/blob/master/docs/generated/protocol.md |
| Python websockets | Только сквозные локальные проверки, не часть приложений | https://websockets.readthedocs.io/ |

Лицензии зависимостей не заменяются MIT проекта. При внешней поставке нужно приложить notices фактически включённых пакетов. Локальные испытания используют отдельно установленный portable OBS. Его бинарники и исходники не входят в NaSeqMen.

Полный текущий стандарт UI перенесён из указанного владельцем C:\Git\UI.TechnicalShell. Первоначальный редактор и граф самостоятельно реализованы на WPF и сохранены. По дальнейшему прямому выбору владельца рядом создан `NaSeqMen.Editor.Web`: собственные HTML/CSS/JavaScript, SVG-граф и локальный ASP.NET Core 11 proxy без стороннего frontend framework. Общая оболочка TechnicalShell адаптирована к этому HTML; её ресурсы и уведомления об авторстве сохранены. Переход на .NET 11 также является прямым выбором владельца.
