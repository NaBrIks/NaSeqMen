// SPDX-License-Identifier: MIT
import { keyName } from './input/key-labels.js';

export function scanCodeLabel(code, flags = 0, includeCode = true) {
  const raw = ((flags & 2) ? 'E0:' : (flags & 4) ? 'E1:' : '') + Number(code).toString(16).toUpperCase().padStart(2, '0');
  const name = keyName('key:' + raw);
  return name ? name + (includeCode ? ' [' + raw + ']' : '') : raw;
}

// Presentation formats are requested by the owning module, not selected by node type.
export function parameterValue(parameter, value, settings, { tr, connection }) {
  if (value === null || value === undefined) return '—';
  if (parameter.resource === 'connection.devices') {
    const device = connection?.settings?.devices?.find(d => d.id === value);
    return device?.name || String(value);
  }
  if (parameter.format === 'scan-code') return scanCodeLabel(value, settings.flags ?? 0);
  if (parameter.format === 'mouse-button') return tr('mouseButton') + ' ' + value;
  if (parameter.format === 'key-steps' && Array.isArray(value))
    return value.map(step => scanCodeLabel(step.code, step.flags, false) + (step.down ? '↓' : '↑')).join(' → ');
  if (parameter.format === 'input-policies' && Array.isArray(value))
    return value.map(policy => policy.device + ': ' + tr(policy.dropUnmatched ? 'drop' : 'pass')).join('; ');
  if (typeof value === 'boolean') return tr(value ? 'yes' : 'no');
  if (typeof value === 'object') return JSON.stringify(value);
  return String(value);
}

export function nodeSummary(node, descriptor, context) {
  return (descriptor?.summaryFields ?? []).flatMap(id => {
    const value = node.settings?.[id];
    if (value === null || value === undefined) return [];
    const parameter = descriptor.parameters?.find(p => p.id === id) ?? {id};
    if (parameter.format === 'input-policies' && Array.isArray(value))
      return value.map((policy, index) => ({id: id + '.' + index, label: policy.device,
        value: context.tr(policy.dropUnmatched ? 'drop' : 'pass')}));
    return [{ id, label: parameter.labelKey ? context.translate(parameter.labelKey, id) : context.tr(id),
      value: parameterValue(parameter, value, node.settings, context) }];
  });
}
