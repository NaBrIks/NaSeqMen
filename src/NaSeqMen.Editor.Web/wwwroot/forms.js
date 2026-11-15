// SPDX-License-Identifier: MIT
import { t } from './i18n.js';
import { el, field, modal, forget, choices, descriptorChoice, replaceById, FormError } from './forms/common.js';
export { showDetails } from './forms/common.js';
export { editNode, chooseNodeRole } from './forms/node-editor.js';

export function editConnection(config, modules, id, save) {
  const original = structuredClone(config.connections.find(c => c.id === id) || { id: crypto.randomUUID(), name: t('forms.title.newConnection'), module: modules[0]?.id || '', enabled: true, settings: {} });
  const view = modal(id ? 'forms.title.editConnection' : 'forms.title.newConnection');
  const main = el('div', 'form-grid'); view.body.append(main);
  const name = field(main, 'name', 'forms.field.name', original.name, { required: true });
  const module = field(main, 'plugin', 'forms.field.plugin', original.module, {
    options: modules.length ? modules.map(descriptorChoice) : [['', 'forms.option.noPlugins']],
    dropdown: true
  });
  const enabled = field(main, 'enabled', 'forms.field.connectionEnabled', original.enabled !== false, { checkbox: true });
  const grid = el('div', 'form-grid'); view.body.append(el('h3', 'form-section-title', 'forms.section.parameters'), grid);
  const moduleSettings = new Map([[original.module, { ...original.settings }]]); let controls = new Map(), previous = original.module;
  function remember() { const settings = moduleSettings.get(previous) || {}; for (const [key, input] of controls) settings[key] = input.value; moduleSettings.set(previous, settings); }
  function render() {
    remember(); previous = module.value; controls = new Map(); forget(grid); grid.replaceChildren();
    const settings = moduleSettings.get(module.value) || {};
    const fields = module.value === 'obs' ? [['url', 'forms.field.url', 'ws://127.0.0.1:4455'], ['password', 'forms.field.password', '']]
      : module.value === 'windows.input' ? [['devicePath', 'forms.field.devicePath', ''], ['includeMovement', 'forms.field.includeMovement', 'false'], ['captureLibrary', 'forms.field.captureLibrary', '']]
      : Object.keys(settings).map(key => [key, key, '']);
    for (const [key, label, fallback] of fields) controls.set(key, field(grid, key, label, settings[key] ?? fallback, { secret: key.toLowerCase().includes('password'), literalLabel: module.value !== 'obs' && module.value !== 'windows.input', options: key === 'includeMovement' ? choices(['false', 'true'], 'forms.boolean.') : null }));
  }
  module.addEventListener('change', render); render();
  return view.open(async () => {
    if (!name.value.trim() || !module.value) throw new FormError('forms.error.connectionNameModule');
    remember(); const candidate = structuredClone(config);
    replaceById(candidate.connections, { ...original, name: name.value.trim(), module: module.value, enabled: enabled.checked, settings: moduleSettings.get(module.value) || {} });
    await save(candidate);
  });
}

export function editMapping(config, id, input, save) {
  const matched = config.mappings.find(m => m.id === id) || (input && config.mappings.find(m => m.source === input.source && m.control === input.control));
  const original = structuredClone(matched || { id: crypto.randomUUID(), name: t('forms.default.controlName'), source: input?.source || '', control: input?.control || '', policy: 'map' });
  const view = modal('forms.title.mapping');
  const grid = el('div', 'form-grid'); view.body.append(grid);
  const name = field(grid, 'name', 'forms.field.controlName', original.name, { required: true });
  const source = field(grid, 'source', 'forms.field.interface', original.source, { required: true });
  const control = field(grid, 'control', 'forms.field.rawCode', original.control, { required: true });
  const policy = field(grid, 'policy', 'forms.field.policy', original.policy, { options: [['map', 'forms.option.map'], ['observe', 'forms.option.observe']] });
  if (input) {
    field(grid, 'time', 'forms.field.lastSignal', input.time || '', { readOnly: true });
    field(grid, 'phase', 'forms.field.phase', input.phase || '', { readOnly: true });
    field(grid, 'value', 'forms.field.value', input.value ?? '', { readOnly: true });
  }
  return view.open(async () => {
    if (!name.value.trim() || !source.value.trim() || !control.value.trim()) throw new FormError('forms.error.mappingFields');
    const candidate = structuredClone(config);
    if (candidate.mappings.some(m => m.id !== original.id && m.source === source.value && m.control === control.value)) throw new FormError('forms.error.mappingDuplicate');
    replaceById(candidate.mappings, { ...original, name: name.value.trim(), source: source.value, control: control.value, policy: policy.value });
    await save(candidate);
  });
}

export function editLinks(config, save) {
  const view = modal('forms.title.links'); const grid = el('div', 'form-grid'); view.body.append(grid);
  const options = [['', 'forms.option.chooseNode'], ...config.nodes.map(n => [n.id, n.name, false])];
  const from = field(grid, 'from', 'forms.field.from', '', { options, required: true });
  const to = field(grid, 'to', 'forms.field.to', '', { options, required: true });
  const mode = field(grid, 'mode', 'forms.role.action', 'add', { options: [['add', 'forms.action.addLink'], ['remove', 'forms.action.removeLink']] });
  return view.open(async () => {
    if (!from.value || !to.value) throw new FormError('forms.error.linkNodes');
    if (from.value === to.value) throw new FormError('forms.error.selfLink');
    const candidate = structuredClone(config);
    const exists = candidate.edges.some(e => e.from === from.value && e.to === to.value);
    if (mode.value === 'remove') { if (!exists) throw new FormError('forms.error.linkMissing'); candidate.edges = candidate.edges.filter(e => e.from !== from.value || e.to !== to.value); }
    else { if (exists) throw new FormError('forms.error.linkDuplicate'); candidate.edges.push({ from: from.value, to: to.value }); }
    await save(candidate);
  });
}

export function injectEvent(send) {
  const view = modal('forms.title.inject', 'forms.action.inject'); const grid = el('div', 'form-grid'); view.body.append(grid);
  const control = field(grid, 'control', 'forms.field.controlCode', 'demo', { required: true });
  const phase = field(grid, 'phase', 'forms.field.phase', 'down', { options: choices(['down', 'up', 'change'], 'forms.phase.') });
  const type = field(grid, 'inputType', 'forms.field.valueType', 'number', { options: choices(['number', 'text', 'boolean'], 'forms.valueType.') });
  const value = field(grid, 'value', 'forms.field.value', '1');
  return view.open(async () => {
    if (!control.value.trim()) throw new FormError('forms.error.controlCode');
    if (type.value === 'number' && (!value.value.trim() || !Number.isFinite(Number(value.value)))) throw new FormError('forms.error.number');
    if (type.value === 'boolean' && !['true', 'false'].includes(value.value)) throw new FormError('forms.error.boolean');
    await send({ module: 'test', source: 'editor', control: control.value, phase: phase.value, valueType: type.value, value: value.value });
  });
}

