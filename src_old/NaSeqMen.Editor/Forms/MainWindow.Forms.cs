// SPDX-License-Identifier: MIT
using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using NaSeqMen.Contracts;
using ValidationResult = NaSeqMen.Contracts.ValidationResult;

namespace NaSeqMen.Editor;

public sealed partial class MainWindow
{
    private void EditNode(string? id)
    {
        var kinds = new Dictionary<string, string> { ["Источник"] = "source", ["Событие"] = "event", ["Условие"] = "condition", ["Преобразование"] = "transform", ["Сценарий C#"] = "script", ["Действие"] = "action" };
        var original = _configuration.Nodes.FirstOrDefault(n => n.Id == id);
        if (original is null)
        {
            if (id is not null) return;
            string? role = null;
            var picker = new EditWindow("Новый узел", [new("kind", "Роль", "Источник", Choices: kinds.Keys.ToArray())],
                values => { role = values["kind"]; return Task.FromResult<string?>(null); }, "Продолжить") { Owner = this, Height = 300 };
            if (picker.ShowDialog() != true || role is null) return;
            original = new NodeDefinition { Name = role, Kind = kinds[role], Order = _configuration.Nodes.Count };
        }
        var operations = _modules.SelectMany(m => m.Actions.Concat(m.Conditions)).GroupBy(o => o.Id).ToDictionary(g => g.Key, g => g.First().Name);
        operations["equals"] = "Сравнить поле"; operations["scale"] = "Преобразовать шкалу"; operations["text"] = "Сформировать текст";
        operations["match"] = "Отбор события"; operations["sequence"] = "Последовательность клавиш"; operations["modifierHeld"] = "Модификатор удерживается";
        var fields = new List<EditField> { new("name", "Название", original.Name),
            new("module", "Модуль", _modules.FirstOrDefault(m => m.Id == original.Module)?.Name ?? original.Module, Choices: _modules.Select(m => m.Name).Prepend("").ToArray()),
            new("connection", "Соединение", ConnectionLabel(original.ConnectionId), Choices: _configuration.Connections.Select(c => c.Name).Prepend("").ToArray()),
            new("operation", "Операция", operations.GetValueOrDefault(original.Operation, original.Operation), Choices: operations.Values.Prepend("").ToArray()),
            new("inputType", "Тип входа", original.InputType, Choices: PortTypes.Supported), new("outputType", "Тип выхода", original.OutputType, Choices: PortTypes.Supported),
            new("enabled", "Включён", original.Enabled ? "Да" : "Нет", Choices: ["Да", "Нет"]) };
        NodeDefinition? selected = null;
        var choice = new EditWindow("Узел · выбор операции", fields, values =>
        {
            if (string.IsNullOrWhiteSpace(values["name"])) return Task.FromResult<string?>("Введите название");
            selected = new NodeDefinition { Id = original.Id, Name = values["name"], Kind = original.Kind, Order = original.Order,
                Module = _modules.FirstOrDefault(m => m.Name == values["module"])?.Id ?? values["module"],
                ConnectionId = ConnectionId(values["connection"]), Operation = operations.FirstOrDefault(o => o.Value == values["operation"]).Key ?? values["operation"],
                Enabled = values["enabled"] == "Да", PreviewOnly = original.PreviewOnly,
                InputType = values["inputType"], OutputType = values["outputType"] };
            return Task.FromResult<string?>(null);
        }, "Параметры →") { Owner = this, Height = 340 };
        if (choice.ShowDialog() != true || selected is null) return;
        var parameters = NodeParameters(selected);
        var parameterFields = parameters.Select(key => new EditField(key, ParameterLabel(key), original.Settings.GetValueOrDefault(key, key == "phase" && selected.Kind == "action" ? "press" : DefaultParameter(key)),
            Multiline: key is "code" or "data" or "steps", Choices: key switch { "phase" when selected.Kind == "action" => ["press", "down", "up"], "phase" => ["", "down", "up", "change"], "field" => ["module", "source", "control", "phase", "value", "valueType"], "muted" => ["true", "false"], _ => null })).ToList();
        ShowEdit("Параметры · " + selected.Name, parameterFields, async values =>
        {
            var candidate = Clone();
            candidate.Nodes.RemoveAll(n => n.Id == selected.Id);
            selected.Settings = new(original.Settings);
            foreach (var (key, value) in values) selected.Settings[key] = value;
            candidate.Nodes.Add(selected);
            return await ApplyAsync(candidate);
        });
    }

    private string[] NodeParameters(NodeDefinition node) => node.Kind switch
    {
        "source" => ["source", "control", "phase"],
        "event" when node.Operation == "sequence" => ["source", "steps", "timeoutMs", "eventName"],
        "event" => ["control", "phase"],
        "script" => ["code"],
        "condition" when node.Operation == "equals" => ["field", "value"],
        "condition" when node.Operation == "modifierHeld" => ["modifier", "scope"],
        "transform" when node.Operation == "scale" => ["factor", "offset"],
        "transform" when node.Operation == "text" => ["template"],
        _ => _modules.FirstOrDefault(m => m.Id == node.Module)?.Actions.Concat(_modules.First(m => m.Id == node.Module).Conditions).FirstOrDefault(o => o.Id == node.Operation)?.Parameters ?? []
    };

    private static string ParameterLabel(string key) => key switch
    {
        "source" => "Интерфейс / источник", "control" => "Кнопка / имя", "phase" => "Фаза", "code" => "Код C#", "field" => "Поле события", "value" => "Значение", "factor" => "Множитель", "offset" => "Смещение", "template" => "Шаблон текста", "keys" => "Сочетание клавиш", "key" => "Клавиша", "file" => "Программа / файл", "arguments" => "Аргументы", "process" => "Имя процесса", "input" => "Источник звука", "muted" => "Без звука", "volume" => "Громкость 0…1", "scene" => "Сцена", "type" => "Запрос OBS", "data" => "Параметры JSON", _ => key
    };
    private static string DefaultParameter(string key) => key switch { "code" => "return Input;", "factor" => "1", "offset" => "0", "template" => "{value}", "field" => "phase", "keys" => "Ctrl+C", "volume" => "{value}", "data" => "{}", _ => "" };

    private void EditConnection(string? id)
    {
        var original = _configuration.Connections.FirstOrDefault(c => c.Id == id) ?? new ConnectionDefinition { Name = "Новое соединение" };
        ConnectionDefinition? selected = null;
        var choice = new EditWindow("Соединение", [new("name", "Название", original.Name), new("module", "Модуль", _modules.FirstOrDefault(m => m.Id == original.Module)?.Name ?? original.Module, Choices: _modules.Select(m => m.Name).ToArray()),
            new("enabled", "Включено", original.Enabled ? "Да" : "Нет", Choices: ["Да", "Нет"])], values =>
        {
            if (string.IsNullOrWhiteSpace(values["name"])) return Task.FromResult<string?>("Введите название");
            var module = _modules.FirstOrDefault(m => m.Name == values["module"] || m.Id == values["module"]);
            if (module is null) return Task.FromResult<string?>("Выберите доступный модуль");
            selected = new ConnectionDefinition { Id = original.Id, Name = values["name"], Module = module.Id, Enabled = values["enabled"] == "Да" };
            return Task.FromResult<string?>(null);
        }, "Параметры →") { Owner = this, Height = 260 };
        if (choice.ShowDialog() != true || selected is null) return;
        var fields = selected.Module switch
        {
            "obs" => new EditField[] { new("url", "Адрес WebSocket", original.Settings.GetValueOrDefault("url", "ws://127.0.0.1:4455")), new("password", "Пароль", original.Settings.GetValueOrDefault("password", ""), Secret: true) },
            "windows.input" => [new("devicePath", "Интерфейс (пусто: все)", original.Settings.GetValueOrDefault("devicePath", "")), new("includeMovement", "Движение мыши", original.Settings.GetValueOrDefault("includeMovement", "false"), Choices: ["false", "true"])],
            _ => original.Settings.Select(p => new EditField(p.Key, p.Key, p.Value)).ToArray()
        };
        ShowEdit("Параметры · " + selected.Name, fields, async values =>
        {
            var candidate = Clone(); candidate.Connections.RemoveAll(c => c.Id == selected.Id);
            selected.Settings = values; candidate.Connections.Add(selected);
            return await ApplyAsync(candidate);
        });
    }

    private void EditMapping(string? id, InputRow? input)
    {
        var original = _configuration.Mappings.FirstOrDefault(m => m.Id == id) ?? new InputMapping { Source = input?.Source ?? "", Control = input?.Control ?? "", Name = "Моя кнопка", Policy = "map" };
        var fields = new List<EditField> { new("name", "Имя кнопки", original.Name), new("source", "Интерфейс", original.Source),
            new("control", "Исходный код", original.Control), new("policy", "Политика", original.Policy, Choices: ["observe", "map"]) };
        if (input is not null) fields.AddRange([new("time", "Время", input.Time, ReadOnly: true), new("phase", "Фаза", input.Phase, ReadOnly: true), new("value", "Значение", input.Value, ReadOnly: true)]);
        ShowEdit("Сопоставление кнопки · исходный ввод не подавляется", fields, async values =>
        {
            var candidate = Clone(); var mapping = candidate.Mappings.FirstOrDefault(m => m.Id == id);
            if (mapping is null) { mapping = new InputMapping { Id = original.Id }; candidate.Mappings.Add(mapping); }
            mapping.Name = values["name"]; mapping.Source = values["source"]; mapping.Control = values["control"]; mapping.Policy = values["policy"];
            return await ApplyAsync(candidate);
        });
    }

    private void ShowLinks()
    {
        var labels = _configuration.Nodes.Select(n => n.Name).ToArray();
        ShowEdit("Соединить или удалить связь", [new("from", "Откуда", "", Choices: labels), new("to", "Куда", "", Choices: labels),
            new("mode", "Действие", "Добавить", Choices: ["Добавить", "Удалить"])], async values =>
        {
            var from = _configuration.Nodes.SingleOrDefault(n => n.Name == values["from"]); var to = _configuration.Nodes.SingleOrDefault(n => n.Name == values["to"]);
            if (from is null || to is null) return "Выберите оба узла; названия должны быть однозначными";
            if (values["mode"] != "Удалить" && !PortTypes.Compatible(from, to)) return "Типы контактов несовместимы";
            var candidate = Clone();
            if (values["mode"] == "Удалить") candidate.Edges.RemoveAll(e => e.From == from.Id && e.To == to.Id);
            else candidate.Edges.Add(new EdgeDefinition { From = from.Id, To = to.Id });
            return await ApplyAsync(candidate);
        });
    }

    private async Task DeleteNodeAsync()
    {
        if (_selectedNode is null) return;
        var candidate = Clone(); candidate.Nodes.RemoveAll(n => n.Id == _selectedNode); candidate.Edges.RemoveAll(e => e.From == _selectedNode || e.To == _selectedNode);
        await ApplyAsync(candidate); _selectedNode = null;
    }

    private void Inject()
    {
        ShowEdit("Синтетическое событие", [new("control", "Имя / код", "demo"), new("phase", "Фаза", "down", Choices: ["down", "up", "change"]),
            new("type", "Тип значения", "number", Choices: ["text", "number", "boolean"]), new("value", "Значение", "1")], async values =>
        {
            var response = await _http.PostAsJsonAsync("api/inject", new Signal { Module = "test", Source = "editor", Control = values["control"], Phase = values["phase"], ValueType = values["type"], Value = values["value"] }, Json);
            if (!response.IsSuccessStatusCode) return await response.Content.ReadAsStringAsync();
            Status("Синтетическое событие передано исполнителю"); return null;
        });
    }

    private void ShowEdit(string title, IEnumerable<EditField> fields, Func<Dictionary<string, string>, Task<string?>> save)
    {
        var window = new EditWindow(title, fields, save) { Owner = this }; window.ShowDialog();
    }

    private void ShowDetails(string title, string text)
    {
        var window = new Window { Owner = this, Title = title, Width = 650, Height = 330, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new TextBox { Text = text, IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(7), VerticalScrollBarVisibility = ScrollBarVisibility.Auto } };
        window.ShowDialog();
    }

}
