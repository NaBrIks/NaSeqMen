// SPDX-License-Identifier: MIT
import { t } from '../i18n.js';
import { el, field, modal, forget, setOptions, descriptorChoice, replaceById, FormError } from './common.js';
import { roles, types, hints, parameters, parameterField } from './node-fields.js';

const builtIn = ['', 'forms.option.builtIn'];
const unrestricted = ['', 'forms.option.unrestricted'];
const titles = Object.fromEntries(roles);

export async function chooseNodeRole() {
  const view = modal('forms.title.chooseNodeRole', 'forms.action.continue');
  const grid = el('div', 'form-grid');
  view.body.append(grid);
  const role = field(grid, 'kind', 'forms.field.kind', 'source', { options: roles });
  return await view.open(async () => {}) ? role.value : null;
}

function connectionField(ctx, moduleId, locked = false) {
  const choices = ctx.config.connections.filter(item => !moduleId || item.module === moduleId);
  const options = [unrestricted, ...choices.map(item => [item.id, item.name, false])];
  const control = ctx.add('connection', ctx.original.connectionId, { options });
  control.disabled = locked;
  return control;
}

function moduleField(ctx, builtIns, modules) {
  const current = ctx.original.module;
  const usesPlugin = !builtIns.some(([id]) => id === current);
  const options = modules.map(descriptorChoice);
  if (!options.length) options.push(['', 'forms.option.noPlugins']);
  const plugin = ctx.add('plugin', usesPlugin ? current : '', { options, dropdown: true });
  if (!builtIns.length) return plugin;
  const pluginMode = '@plugin';
  const mode = ctx.add('module', usesPlugin ? pluginMode : current, {
    options: [...builtIns, [pluginMode, 'forms.option.plugin']]
  });
  const pluginRow = plugin.closest('.form-row');
  mode.closest('.form-row').after(pluginRow);
  const update = () => { pluginRow.hidden = mode.value !== pluginMode; };
  mode.addEventListener('change', update);
  update();
  return {
    get value() { return mode.value === pluginMode ? plugin.value : mode.value; },
    get missingPlugin() { return mode.value === pluginMode && !plugin.value; },
    addEventListener(event, listener) {
      mode.addEventListener(event, listener);
      plugin.addEventListener(event, listener);
    }
  };
}

function sourceForm(ctx) {
  const modules = ctx.modules.filter(module => module.hasInput || module.id === ctx.original.module);
  const module = moduleField(ctx, [builtIn, ['test', 'forms.option.synthetic']], modules);
  const connection = connectionField(ctx, module.value);
  module.addEventListener('change', () => {
    const list = ctx.config.connections.filter(item => !module.value || item.module === module.value);
    setOptions(connection, [unrestricted, ...list.map(item => [item.id, item.name, false])], '');
  });
  ctx.port('outputType');
  ctx.renderParameters(['source', ...['control', 'phase'].filter(key => Object.hasOwn(ctx.original.settings || {}, key))]);
  return () => {
    if (module.missingPlugin) throw new FormError('forms.error.choosePlugin');
    return { module: module.value, connectionId: connection.value };
  };
}

function eventForm(ctx) {
  const operation = ctx.add('operation', ctx.original.operation, {
    options: [['match', 'forms.operation.match'], ['sequence', 'forms.operation.sequence']]
  });
  const connection = connectionField(ctx, 'windows.input', ctx.attached);
  ctx.port('inputType');
  let stopCapture;
  const render = () => {
    stopCapture?.();
    const sequence = operation.value === 'sequence';
    connection.closest('.form-row').hidden = !sequence;
    ctx.renderParameters(parameters('event', operation.value, '', ctx.modules));
    const source = ctx.controls.get('source');
    if (source && ctx.attached) source.readOnly = true;
    if (sequence && ctx.capture) {
      const bar = el('div', 'toolbar');
      ctx.view.body.append(bar);
      const dispose = ctx.capture.bind(ctx.view.dialog, bar, ctx.controls.get('steps'), () => ({
        source: source.value, connectionId: connection.value
      }));
      stopCapture = () => { dispose(); bar.remove(); stopCapture = null; };
    }
  };
  operation.addEventListener('change', render);
  render();
  return () => ({
    operation: operation.value,
    module: operation.value === 'sequence' ? 'windows.input' : ctx.original.module,
    connectionId: operation.value === 'sequence' ? connection.value : ctx.original.connectionId
  });
}

function conditionForm(ctx) {
  return moduleOperationForm(ctx, 'conditions', [['equals', 'forms.operation.equals'], ['modifierHeld', 'forms.operation.modifierHeld']]);
}

function actionForm(ctx) {
  ctx.port('inputType');
  return moduleOperationForm(ctx, 'actions', []);
}

function moduleOperationForm(ctx, category, builtInOptions) {
  const module = moduleField(ctx, builtInOptions.length ? [builtIn] : [],
    ctx.modules.filter(item => item[category]?.length || item.id === ctx.original.module));
  const connection = connectionField(ctx, module.value);
  const operation = ctx.add('operation', '', { options: [] });
  const renderParameters = () => ctx.renderParameters(parameters(ctx.original.kind, operation.value, module.value, ctx.modules));
  const configure = (operationId, connectionId) => {
    const available = ctx.config.connections.filter(item => item.module === module.value && module.value);
    const matchingConnection = available.find(item => item.id === connectionId);
    setOptions(connection, [unrestricted, ...available.map(item => [item.id, item.name, false])], matchingConnection?.id || '');
    connection.closest('.form-row').hidden = !module.value;
    const options = module.value
      ? (ctx.modules.find(item => item.id === module.value)?.[category] || []).map(descriptorChoice)
      : builtInOptions;
    setOptions(operation, options, operationId);
    renderParameters();
  };
  module.addEventListener('change', () => configure('', ''));
  operation.addEventListener('change', renderParameters);
  configure(ctx.original.operation, ctx.original.connectionId);
  return () => {
    if (module.missingPlugin) throw new FormError('forms.error.choosePlugin');
    if (!operation.value) throw new FormError('forms.error.moduleOperation');
    return { module: module.value, connectionId: connection.value, operation: operation.value };
  };
}

function transformForm(ctx) {
  const operation = ctx.add('operation', ctx.original.operation, {
    options: [['scale', 'forms.operation.scale'], ['text', 'forms.operation.text']]
  });
  const render = () => ctx.renderParameters(parameters('transform', operation.value, '', []));
  operation.addEventListener('change', render);
  render();
  return () => operation.value === 'scale'
    ? { operation: 'scale', inputType: 'number', outputType: 'number' }
    : { operation: 'text', inputType: ctx.original.operation === 'scale' ? 'any' : ctx.original.inputType, outputType: 'text' };
}

function scriptForm(ctx) {
  ctx.port('inputType');
  ctx.port('outputType');
  ctx.renderParameters(['code']);
  ctx.view.body.append(el('p', 'form-note', 'forms.help.scriptBoundary'));
  return () => ({});
}

const editors = { source: sourceForm, event: eventForm, condition: conditionForm, action: actionForm, transform: transformForm, script: scriptForm };

export function editNode(config, modules, id, save, seed = null, attach = null, capture = null) {
  const existing = id ? config.nodes.find(node => node.id === id) : null;
  if (id && !existing) throw new FormError('forms.error.nodeMissing');
  const original = structuredClone(existing || {
    id: crypto.randomUUID(), name: '', kind: 'source', module: '', connectionId: '',
    operation: '', inputType: 'any', outputType: 'any', order: config.nodes.length,
    enabled: true, settings: {}, ...seed
  });
  const editor = editors[original.kind];
  if (!editor) throw new FormError('forms.error.nodeMissing');

  const view = modal(titles[original.kind]);
  view.dialog.dataset.nodeRole = original.kind;
  const main = el('div', 'form-grid');
  const parameterGrid = el('div', 'form-grid');
  const parameterTitle = el('h3', 'form-section-title', 'forms.section.parameters');
  view.body.append(main, parameterTitle, parameterGrid);
  const name = field(main, 'name', 'forms.field.name', original.name || t(titles[original.kind]), { required: true });
  const enabled = field(main, 'enabled', 'forms.field.enabled', original.enabled !== false, { checkbox: true });
  const preview = ['source', 'action'].includes(original.kind)
    ? field(main, 'previewOnly', 'forms.field.outputMode', original.previewOnly ? 'test' : 'live', {
        options: [['live', 'forms.outputMode.live'], ['test', 'forms.outputMode.test']],
        hint: original.kind === 'source' ? 'forms.outputMode.sourceHelp' : 'forms.outputMode.actionHelp'
      })
    : null;
  const settings = { ...original.settings };
  const controls = new Map();
  const ports = new Map();
  const remember = () => { for (const [key, control] of controls) settings[key] = control.value; };
  const add = (key, value, options) => field(main, key, 'forms.field.' + key, value, { ...options, hint: hints[key] });
  const readRole = editor({
    original, config, modules, view, capture, attached: !!attach, controls, add,
    port(key) { ports.set(key, add(key, original[key] || 'any', { options: types })); },
    renderParameters(keys) {
      remember();
      controls.clear();
      forget(parameterGrid);
      parameterGrid.replaceChildren();
      for (const key of keys) controls.set(key, parameterField(parameterGrid, key, settings[key], original.kind));
      parameterTitle.hidden = keys.length === 0;
    }
  });
  return view.open(async () => {
    remember();
    const candidate = structuredClone(config);
    const selectedPorts = Object.fromEntries([...ports].map(([key, control]) => [key, control.value]));
    replaceById(candidate.nodes, {
      ...original, ...selectedPorts, ...readRole(), name: name.value.trim(), enabled: enabled.checked,
      previewOnly: preview?.value === 'test', settings
    });
    attach?.(candidate, original.id);
    await save(candidate);
  });
}
