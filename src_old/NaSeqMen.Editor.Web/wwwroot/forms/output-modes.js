// SPDX-License-Identifier: MIT
import { t } from '../i18n.js';
import { bind, el, modal } from './common.js';

export function renderOutputMode(button, configuration) {
  button.disabled = !configuration;
  button.setAttribute('aria-pressed', String(!!configuration?.dryRun));
  const enabled = configuration?.nodes.filter(node => node.enabled) ?? [];
  const actions = enabled.filter(node => node.kind === 'action');
  const mode = !configuration ? 'idle' : configuration.dryRun ? 'global'
    : actions.length > 0 && actions.every(node => node.previewOnly) ? 'allActions'
    : enabled.some(node => node.previewOnly) ? 'scoped' : 'live';
  button.dataset.mode = mode;
  button.querySelector('span').textContent = t(mode === 'idle' ? 'outputs.title' : 'outputs.status.' + mode);
}

export function editOutputModes(getConfiguration, Table, save) {
  const configuration = getConfiguration();
  const view = modal('outputs.title');
  const modes = el('div', 'toolbar');
  view.body.append(modes);

  const actions = configuration.nodes.filter(node => node.kind === 'action');
  const changed = new Map();
  const toggle = row => {
    const input = el('input');
    input.type = 'checkbox';
    input.checked = changed.get(row.id) ?? !!row.previewOnly;
    input.setAttribute('aria-label', t('outputs.actionLabel', { name: row.name }));
    input.dataset.tooltip = t('forms.outputMode.actionHelp');
    input.addEventListener('change', () => changed.set(row.id, input.checked));
    return input;
  };
  const host = el('div', 'table-scroll output-mode-table');
  const list = new Table(host, [
    { key: 'name', label: 'forms.role.action', width: 235 },
    { label: 'forms.outputMode.test', width: 95, render: toggle }
  ], { empty: 'outputs.empty' });
  const rows = actions.map(node => ({ ...node, name: node.name || t('graph.unnamed') }));
  list.set(rows);
  view.body.append(host);

  for (const [preview, key] of [[true, 'outputs.allTest'], [false, 'outputs.allLive']]) {
    const button = el('button', '', key);
    button.type = 'button';
    button.disabled = actions.length === 0;
    bind(button, 'data-tooltip', 'outputs.bulkHelp');
    button.addEventListener('click', () => {
      for (const node of actions) changed.set(node.id, preview);
      list.render();
    });
    modes.append(button);
  }

  return view.open(async () => {
    const candidate = structuredClone(getConfiguration());
    for (const node of candidate.nodes) {
      if (node.kind === 'action' && changed.has(node.id)) node.previewOnly = changed.get(node.id);
    }
    await save(candidate);
  }).finally(() => list.dispose());
}
