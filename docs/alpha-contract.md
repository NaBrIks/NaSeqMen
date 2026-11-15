# Контракт первой альфы

Текущий контракт реализации 2026-11-15; развитие API версионируется. По прямому выбору владельца проекты используют .NET 11, установлен SDK 11.0.100 и runtime 11.0.0. Новый HTML/CSS/JavaScript редактор находится рядом с сохранённым WPF. API исполнителя — HTTP + WebSocket на `http://127.0.0.1:17963`; слушать только loopback, не порт 443. Авторизация локальным случайным token из `local/api-token.txt`, заголовок `X-NaSeqMen-Token`, включая WebSocket. Другой origin и запрос без token не допускаются. Модули и приложения — отдельные csproj.

Браузер работает с `http://127.0.0.1:17964`, который обслуживает `NaSeqMen.Editor.Web` на ASP.NET Core 11. Этот сервер раздаёт `wwwroot` и передаёт только перечисленные ниже API на фиксированный loopback-адрес исполнителя. Token читает и добавляет сервер; браузеру он не выдаётся. Проверки Host/Origin и полный контракт proxy описаны в [web-editor.md](web-editor.md). Контракт JSON и правила исполнения для WPF и HTML одинаковы.

Общие POCO/records/interfaces находятся в src/NaSeqMen.Contracts/Contracts.cs. JSON camelCase, System.Text.Json web defaults. Значение Signal.Value передаётся строкой с явным ValueType: text, number, boolean; числа invariant culture. Это сообщение, не запись состояния. Времена UTC.

## API

| Метод | Назначение |
|---|---|
| GET /api/health | {name, version, dryRun}; доступен без token только для обнаружения процесса |
| GET /api/config | Configuration |
| POST /api/config/validate | Configuration → ValidationResult, без выполнения |
| PUT /api/config | Проверить/скомпилировать, сохранить и применить Configuration; ValidationResult; при ошибке 400, старая конфигурация остаётся |
| GET /api/modules | ModuleDescriptor[] |
| GET /api/devices | DeviceDescriptor[] |
| POST /api/inject | Signal → {accepted:true}; только синтетический источник, нельзя выдавать за аппаратный |
| WS /api/diagnostics | Живые Diagnostic, без истории/replay/snapshot |

Исполнитель: `--root <absolute project dir>`, `--port 17963` (optional); по умолчанию root = current directory. WPF Editor: `--root <project dir>`, `--url http://127.0.0.1:17963`; `--smoke <png>` для автономной визуальной проверки с демонстрационной конфигурацией без действий. HTML-сервер: `--root <project dir>`, `--port 17964`, `--executor-port 17963`. Запуск HTML через `Start-NaSeqMen-Web.cmd` не заменяет запускатель WPF.

Конфигурация local/config.json. Только файл конфигурации и token сохраняются автоматически. Трассы не пишутся в файлы. В редакторе ограниченное число диагностических строк текущего подключения, не источник графа. GET /api/health — единственное исключение из token guard, оно не возвращает настройки или данные.

## Узлы и модули

- source: Settings module/source/control/phase — необязательные точные фильтры; пустое/отсутствующее поле — любое значение. Node.Module и ConnectionId также ограничивают источник, если указаны. Исходный Signal выдаётся дальше.
- condition: встроенные Operation equals (Settings field/value), module — по Node.Module/Operation вызов EvaluateAsync. Поля field: module, connectionId, source, control, phase, value, valueType. Только прошедшее событие идёт дальше.
- transform: Operation scale (Settings factor/offset, number), text (Settings template, подстановка {value}, {control}, {phase}); выдаёт новое сообщение той же цепочки.
- script: Settings code; пользовательский C# получает Signal Input и ScriptContext Context. Возвращает Signal или null (остановить ветку). Context.EmitAsync(module,connectionId,operation,arguments) идёт через общий dryrun gate; Context.Trace(message). Context.CancellationToken. Это публичный scripting contract первой альфы.
- action: Node.Module/ConnectionId/Operation, Settings — аргументы операции, подстановка {value}/{control}/{phase}; dryrun формирует diagnostic suppressed, модуль ExecuteAsync НЕ вызывается. Внешний результат не симулируется.
- Граф направленный ацикличный. Ветвление — несколько Edge; порядок по Order затем Id. Циклы/дубликаты/несуществующие ссылки/несовместимые операции отклоняются при validation.
- Node.InputType/OutputType: any, text, number, boolean. PortTypes задаёт встроенные типы scale/text. Известная несовместимость контактов отклоняется при проверке; any означает проверку фактического значения во время обработки. C# проверяется на соответствие объявленному выходу. ActionResult.Suppressed=true отличает пробный прогон от ошибки и внешнего успеха.
- InputMapping: Source+Control → Name. Для Policy map перед публикацией в граф заменить Control именем, но raw diagnostic сохраняет исходный Signal. Policy observe ничего не заменяет; suppression отсутствует в alpha backend и конфигурация suppress должна отвергаться явно.

Идентификаторы модулей: windows.input, windows.desktop, obs. Вход RawInput — пассивное наблюдение, не подавление и не замена AHK. windows.input соединение default-local; windows.desktop default-desktop. OBS по явной включённой конфигурации, по умолчанию отсутствует.

Modules отдельные DLL, копируются в artifacts/alpha/modules/. Executor загружает только NaSeqMen.*.dll с конкретными INaModule, общий Contracts не дублируется. Модули не зависят от WPF или HTML UI. Для windows.input допустим скрытый WinForms message loop внутри Executor; ядро не зависит от него.

## Стадии диагностики

input (исходный ввод), received, passed, blocked, transformed, script, intended, suppressed, completed, error. Корреляция Signal.Id сохраняется по цепочке. UI подсвечивает NodeId при событии. Отладка отражает намерение, не ожидаемое состояние программы. Отсутствие соединения с редактором не блокирует исполнение.

Автопрокрутка журнала и группировка исходных сигналов в виртуальные кнопки — поведение клиента. Они не меняют формат Diagnostic, не добавляют API состояния и не объединяют рабочие входы исполнителя. Составные нажатия вроде Win+E автоматически не распознаются: их обучение и подтверждение пока обсуждаются.
