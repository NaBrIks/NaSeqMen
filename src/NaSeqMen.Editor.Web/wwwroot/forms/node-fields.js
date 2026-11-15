// SPDX-License-Identifier: MIT
import { choices, field } from './common.js';
const types = choices(['any', 'text', 'number', 'boolean'], 'forms.valueType.');
const roles = [['source', 'forms.role.source'], ['event', 'forms.role.event'], ['condition', 'forms.role.condition'], ['transform', 'forms.role.transform'], ['script', 'forms.role.script'], ['action', 'forms.role.action']];
const labels = { source: 'forms.field.source', control: 'forms.field.control', phase: 'forms.field.phase', code: 'forms.field.code', field: 'forms.field.field', value: 'forms.field.value', factor: 'forms.field.factor', offset: 'forms.field.offset', template: 'forms.field.template', keys: 'forms.field.keys', key: 'forms.field.key', file: 'forms.field.file', arguments: 'forms.field.arguments', process: 'forms.field.process', input: 'forms.field.input', muted: 'forms.field.muted', volume: 'forms.field.volume', scene: 'forms.field.scene', type: 'forms.field.type', data: 'forms.field.data' };
const hints = {
  name: 'forms.help.name',
  kind: 'forms.help.kind',
  module: 'forms.help.module',
  connection: 'forms.help.connection',
  operation: 'forms.help.operation',
  source: 'forms.help.source',
  control: 'forms.help.control',
  phase: 'forms.help.phase',
  code: 'forms.help.code',
  value: 'forms.help.value',
  inputType: 'forms.help.inputType',
  outputType: 'forms.help.outputType',
  enabled: 'forms.help.enabled',
  field: 'forms.help.field',
  factor: 'forms.help.factor',
  offset: 'forms.help.offset',
  template: 'forms.help.template',
  keys: 'forms.help.keys',
  key: 'forms.help.key',
  file: 'forms.help.file',
  arguments: 'forms.help.arguments',
  process: 'forms.help.process',
  input: 'forms.help.input',
  volume: 'forms.help.volume',
  muted: 'forms.help.muted',
  scene: 'forms.help.scene',
  type: 'forms.help.type',
  data: 'forms.help.data',
  url: 'forms.help.url',
  password: 'forms.help.password',
  devicePath: 'forms.help.devicePath',
  includeMovement: 'forms.help.includeMovement',
  policy: 'forms.help.policy',
  time: 'forms.help.time',
  from: 'forms.help.from',
  to: 'forms.help.to',
  mode: 'forms.help.mode'
};
const defaults = { code: 'return Input;', factor: '1', offset: '0', template: '{value}', field: 'phase', keys: 'Ctrl+C', volume: '{value}', data: '{}' };
for (const key of ['steps', 'timeoutMs', 'eventName', 'modifier', 'scope', 'ignoreRepeat']) {
  labels[key] = 'forms.field.' + key;
  hints[key] = 'forms.help.' + key;
}
function parameters(kind, operation, module, modules) {
  if (kind === 'source') return ['source', 'control', 'phase'];
  if (kind === 'event') return operation === 'sequence' ? ['source', 'steps', 'timeoutMs', 'eventName'] : ['control', 'phase', 'ignoreRepeat'];
  if (kind === 'script') return ['code'];
  if (kind === 'condition' && operation === 'equals') return ['field', 'value'];
  if (kind === 'condition' && operation === 'modifierHeld') return ['modifier', 'scope'];
  if (kind === 'transform') return operation === 'scale' ? ['factor', 'offset'] : ['template'];
  return (modules.find(m => m.id === module)?.[kind === 'condition' ? 'conditions' : 'actions'] || []).find(o => o.id === operation)?.parameters || [];
}
function parameterField(grid, key, value, kind) {
  const options = key === 'phase' ? choices(kind === 'action' ? ['press', 'down', 'up'] : ['', 'down', 'up', 'change'], 'forms.phase.')
    : key === 'field' ? choices(['module', 'connectionId', 'source', 'control', 'phase', 'value', 'valueType'], 'forms.signalField.')
    : key === 'muted' || key === 'ignoreRepeat' ? choices(['false', 'true'], 'forms.boolean.')
    : key === 'scope' ? choices(['source', 'global'], 'forms.scope.')
    : key === 'modifier' ? choices(['Shift', 'Ctrl', 'Alt', 'Win'], 'forms.modifier.') : null;
  return field(grid, key, labels[key] || key, value ?? (key === 'phase' && kind === 'action' ? 'press' : defaults[key] || ''), { options, hint: hints[key], literalLabel: !labels[key], multiline: ['code', 'data', 'steps'].includes(key), rows: key === 'code' ? 11 : 4 });
}

export { types, roles, hints, parameters, parameterField };
