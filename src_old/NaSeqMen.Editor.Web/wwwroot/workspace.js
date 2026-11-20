import { parameterForm } from './workspace-forms.js';
import { nodeSummary } from './workspace-presentation.js';
// SPDX-License-Identifier: MIT
import { initializeShell, updateConnections } from './shell.js';
import { t, hasTranslation, registerTranslations, onLanguageChange } from './i18n.js';
import { workspaceLabels } from './workspace-labels.js';

registerTranslations(workspaceLabels);
const tr = key => t('workspace.' + key);
const root = document.querySelector('.naseq-workspace');
root.classList.add('workspace-projects');
const nav = document.querySelector('#shell-navigation');
const html = (tag, text, className) => {
  const e = document.createElement(tag);
  if (text !== undefined) e.textContent = text;
  if (className) e.className = className;
  return e;
};
const button = (key, run, icon) => {
  const e = html('button', tr(key));
  e.type = 'button';
  if (icon) {
    const glyph = html('i', undefined, 'ti ti-' + icon);
    glyph.setAttribute('aria-hidden', 'true');
    e.prepend(glyph, document.createTextNode(' '));
  }
  e.addEventListener('click', () => Promise.resolve().then(run).catch(error => status('error', error.message)));
  return e;
};
const selector = (options, value) => {
  const e = html('select');
  for (const [id, title] of options) { const option = html('option', title); option.value = id; e.append(option); }
  e.value = value ?? options[0]?.[0] ?? '';
  return e;
};
const field = (name, input) => { const e = html('label', tr(name)); e.append(input); return e; };
const text = (value = '') => { const e = html('input'); e.value = value; return e; };
const json = (value = {}) => { const e = html('textarea'); e.value = JSON.stringify(value, null, 2); return e; };
const id = () => crypto.randomUUID();
let data = { version: 2, projects: [] }, catalog = [], adapters = [], projectId, pageId, tab = 'graph';
let picked, output, wireSelected = null, dirty = false, paused = false, debug = true;
let sideTab = 'properties', runtimeState = null, paletteFilter = '', clearedThrough = '';
const dirtyProjects = new Set();
let message = '', statusKey = '', isError = false, logFingerprint = '';
let connected = false;
const debugWindow = new URLSearchParams(location.search).get('view') === 'debug';
if (debugWindow) {
  debug = true;
  document.body.classList.add('workspace-debug-window');
  document.title = tr('trace') + ' · NaSeqMen';
}
let modePending = false;
const appliedProjects = new Set();
let lastActivity = 0, activityInitialized = false, polling = false;
const watching = new Set();
const editor = html('section', undefined, 'workspace-editor');
const toolbar = html('div', undefined, 'toolbar');
const tabs = html('div', undefined, 'workspace-pages');
const canvas = html('div', undefined, 'workspace-canvas');
const board = html('div', undefined, 'workspace-board');
const connections = html('div'); connections.id = 'workspace-connections';
const log = html('div', undefined, 'workspace-log');
const workArea = html('div', undefined, 'workspace-workarea');
const palette = html('aside', undefined, 'workspace-palette');
const paletteSearch = text(); paletteSearch.type = 'search';
const paletteList = html('div', undefined, 'workspace-palette-list');
paletteSearch.addEventListener('input', () => { paletteFilter = paletteSearch.value; drawPalette(); });
palette.append(paletteSearch, paletteList);
const center = html('div', undefined, 'workspace-center');
const inspector = html('aside', undefined, 'workspace-inspector');
const sideTabs = html('div', undefined, 'workspace-side-tabs');
const properties = html('div', undefined, 'workspace-properties');
const splitter = html('div', undefined, 'workspace-side-splitter');
splitter.tabIndex = 0; splitter.setAttribute('role', 'separator'); splitter.setAttribute('aria-orientation', 'vertical');
let sideWidth = Number(localStorage.getItem('naseqmen.workspace.sideWidth')) || 310;
function resizeSide(width) {
  sideWidth = Math.max(230, Math.min(width, Math.max(230, workArea.clientWidth * .6)));
  inspector.style.width = sideWidth + 'px';
}
splitter.addEventListener('pointerdown', event => {
  if (event.button !== 0) return;
  splitter.setPointerCapture(event.pointerId);
  const start = event.clientX, initial = sideWidth;
  const move = e => resizeSide(initial + start - e.clientX);
  const done = () => { splitter.removeEventListener('pointermove', move); localStorage.setItem('naseqmen.workspace.sideWidth', sideWidth); };
  splitter.addEventListener('pointermove', move); splitter.addEventListener('lostpointercapture', done, { once: true });
});
splitter.addEventListener('keydown', event => {
  if (!['ArrowLeft', 'ArrowRight'].includes(event.key)) return;
  event.preventDefault(); resizeSide(sideWidth + (event.key === 'ArrowLeft' ? 20 : -20));
});
const footer = html('footer', undefined, 'workspace-footer');
const statusButton = html('button', '', 'workspace-status');
statusButton.addEventListener('click', () => { if (message) popup('details', [html('pre', message)], null); });
footer.append(statusButton); canvas.append(board);
center.append(tabs, canvas, connections);
inspector.append(sideTabs, properties, log);
workArea.append(palette, center, splitter, inspector);
editor.append(toolbar, workArea, footer);
root.replaceChildren(editor);
const currentProject = () => data.projects.find(p => p.id === projectId);
const currentPage = () => currentProject()?.pages.find(p => p.id === pageId);
const describe = node => catalog.find(type => type.id === node.type);
const nameOf = type => hasTranslation('workspace.' + type.id) ? tr(type.id) : type.label;
const nodeTitle = node => node.name || (node.labelKey && hasTranslation('workspace.' + node.labelKey)
  ? tr(node.labelKey) : describe(node) ? nameOf(describe(node)) : node.type);
const portName = port => ({ in: tr('inputPort'), out: tr('outputPort'), error: tr('errorPort') })[port] ?? port;

async function api(path = '', method = 'GET', value) {
  const response = await fetch('/api/workspace' + path, { method, ...(value === undefined ? {} : {
    headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(value)
  }) });
  const result = await response.json().catch(() => ({}));
  if (!response.ok) { if (result.state) runtimeState = result.state; throw new Error(result.message || response.statusText); }
  return result;
}
function status(key, detail = '') {
  statusKey = key; message = detail; isError = key === 'error';
  statusButton.textContent = tr(key); statusButton.dataset.error = String(isError);
}
function changed() { dirtyProjects.add(projectId); dirty = true; status('dirty'); render(); }
function select(project, page = project?.pages[0]) {
  projectId = project?.id; pageId = page?.id; picked = output = wireSelected = null;
  logFingerprint = ''; root.scrollTop = 0; canvas.scrollTo(0, 0); render();
}
async function inject(node, payload) {
  if (dirtyProjects.has(projectId) || !appliedProjects.has(projectId)) throw Error(tr('playFirst'));
  await api('/inject', 'POST', { project: projectId, page: pageId, node: node.id, data: payload });
  debug = true; sideTab = 'trace';
  render();
  await readTrace();
}
async function load() {
  if (dirty && !confirm(tr('unsaved'))) return;
  [data, catalog, adapters] = await Promise.all([api(), api('/catalog'), api('/adapters')]);
  appliedProjects.clear();
  data.projects.forEach(project => appliedProjects.add(project.id));
  dirtyProjects.clear(); dirty = false; connected = true; status('ready');
  runtimeState = await api('/state');
  document.querySelector('#connection-state').textContent = tr('ready');
  const params = new URLSearchParams(location.search);
  const project = currentProject() ?? data.projects.find(p => p.id === params.get('project')) ?? data.projects[0];
  if (debugWindow && !pageId) pageId = params.get('page');
  select(project, project?.pages.find(p => p.id === pageId) ?? project?.pages[0]);
}
function popup(key, fields, apply, layout = {}) {
  const dialog = html('dialog', undefined, 'form-dialog workspace-dialog');
  const form = html('form', undefined, 'form-shell');
  const heading = html('header', undefined, 'form-heading');
  const title = html('h2', tr(key));
  title.id = 'workspace-dialog-title';
  dialog.setAttribute('aria-labelledby', title.id);
  heading.append(title);
  const body = html('div', undefined, 'form-body');
  body.append(...fields);
  const bar = html('div', undefined, 'toolbar');
  const error = html('output', '', 'workspace-status'); error.dataset.error = 'true';
  bar.append(error, button(apply ? 'cancel' : 'close', () => dialog.close()));
  if (apply) {
    const save = html('button', tr('save')); save.type = 'submit'; bar.append(save);
    form.addEventListener('submit', async e => {
      e.preventDefault();
      try { await apply(); dialog.close(); }
      catch (err) { error.textContent = tr('error'); error.dataset.tooltip = err.message; }
    });
  }
  form.append(heading, body, bar); dialog.append(form);
  layout.onOpen?.(dialog);
  document.querySelector('#shell').append(dialog);
  dialog.addEventListener('close', () => dialog.remove(), { once: true }); dialog.showModal();
}
function addProject() {
  const name = text();
  popup('addProject', [field('name', name)], () => {
    if (!name.value.trim()) throw Error(tr('name'));
    const project = { id: id(), name: name.value.trim(), connections: [], pages: [{ id: id(), name: tr('defaultPage'), nodes: [], wires: [] }], dryRun: true };
    data.projects.push(project); select(project); changed();
  });
}
function addPage() {
  const name = text();
  popup('addPage', [field('name', name)], () => {
    if (!name.value.trim()) throw Error(tr('name'));
    const project = currentProject(), page = { id: id(), name: name.value.trim(), nodes: [], wires: [] };
    project.pages.push(page); select(project, page); changed();
  });
}
function rename(item) {
  const name = text(item.name);
  popup('edit', [field('name', name)], () => {
    if (!name.value.trim()) throw Error(tr('name'));
    item.name = name.value.trim();
    changed();
  });
}
function editConnection(existing) {
  const name = text(existing?.name);
  const adapter = selector(adapters.map(x => [x.id, x.id]), existing?.adapter); adapter.disabled = Boolean(existing);
  const enabled = html('input'); enabled.type = 'checkbox'; enabled.checked = existing?.enabled ?? true;
  const holder = html('div'); let parameters;
  function update() {
    parameters = parameterForm(adapters.find(a => a.id === adapter.value)?.parameters ?? [], existing?.settings ?? {},
      { tr, html, project: currentProject(), translate: (key, fallback) => hasTranslation(key) ? t(key) : fallback });
    holder.replaceChildren(parameters.element);
  }
  adapter.addEventListener('change', update); update();
  popup(existing ? 'editConnection' : 'addConnection', [field('name', name), field('type', adapter), field('enabled', enabled), holder], () => {
    if (!name.value.trim()) throw Error(tr('name'));
    const next = { id: existing?.id ?? id(), name: name.value.trim(), adapter: adapter.value, enabled: enabled.checked, settings: parameters.read() };
    if (existing) Object.assign(existing, next); else currentProject().connections.push(next);
    changed();
  }, { onOpen: dialog => dialog.classList.add('workspace-dialog-wide') });
}
function editNode(existing) {
  if (!existing) return;
  const name = text(existing.name);
  const descriptor = describe(existing);
  const connection = selector(currentProject().connections.filter(c => c.adapter === descriptor?.adapter).map(c => [c.id, c.name]), existing.connectionId);
  const fields = [field('name', name), field('type', html('output', nameOf(descriptor)))];
  if (descriptor?.adapter) fields.push(field('connection', connection));
  const holder = html('div'); let controls;
  function updateControls() {
    const values = controls ? controls.read() : existing.settings ?? {};
    controls = parameterForm(descriptor?.parameters ?? [], values, {
      tr, html, project: currentProject(), connection: currentProject().connections.find(c => c.id === connection.value),
      translate: (key, fallback) => hasTranslation(key) ? t(key) : fallback
    });
    holder.replaceChildren(controls.element);
  }
  connection.addEventListener('change', () => { try { updateControls(); } catch (error) { status('error', error.message); } });
  updateControls(); fields.push(holder);
  popup('edit', fields, () => {
    existing.name = name.value.trim(); existing.connectionId = descriptor?.adapter ? connection.value || null : null;
    existing.settings = controls.read(); changed();
  }, { onOpen: dialog => dialog.classList.add('workspace-node-dialog') });
}
function ports(node) {
  if (node.type === 'map') return [
    { id: 'in', direction: 'Input', type: node.settings?.inputType ?? 'any' },
    { id: 'out', direction: 'Output', type: node.settings?.outputType ?? 'any' }
  ];
  if (node.type === 'subgraph') {
    return (currentProject().pages.find(p => p.id === node.settings?.page)?.nodes ?? []).filter(n => ['inlet', 'outlet'].includes(n.type))
      .map(n => ({ id: n.settings?.port ?? n.id, direction: n.type === 'inlet' ? 'Input' : 'Output', type: n.settings?.type ?? 'any' }));
  }
  const read = (entries, direction) => entries.map(p => typeof p === 'string'
    ? { id: p, direction, type: 'any' } : { ...p, direction, type: p.type ?? 'any' });
  const result = node.type === 'script' && Array.isArray(node.settings?.inputs)
    ? read(node.settings.inputs, 'Input') : [...(describe(node)?.ports ?? [])];
  if (node.type === 'script' || node.type === 'switch') {
    const extra = node.settings?.outputs ?? (node.type === 'script' ? ['out'] : []);
    if (Array.isArray(extra)) result.push(...read(extra, 'Output'));
    if (node.type === 'switch') result.push({ id: 'default', direction: 'Output', type: 'any' });
  }
  return result;
}
function choosePort(node, port) {
  if (port.direction === 'Output') { output = { node: node.id, port }; status('selectPort'); render(); return; }
  if (!output) return;
  if (port.type !== output.port.type && port.type !== 'any' && output.port.type !== 'any') { status('error', tr('incompatible')); return; }
  if (node.id === output.node) { status('error', tr('incompatible')); return; }
  const wire = { from: output.node, output: output.port.id, to: node.id, input: port.id };
  const wires = currentPage().wires;
  if (!wires.some(w => Object.keys(wire).every(k => wire[k] === w[k]))) wires.push(wire);
  output = null; changed();
}
function drawBoard() {
  const page = currentPage();
  const points = new Map();
  const svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg'); svg.classList.add('workspace-wires');
  board.replaceChildren(svg);
  if (!page?.nodes.length) { board.append(html('span', tr('empty'), 'empty')); return; }
  const key = (node, port, direction) => JSON.stringify([node, port, direction]);
  for (const node of page.nodes) {
    const box = html('article', undefined, 'workspace-node');
    box.dataset.node = node.id;
    box.dataset.category = describe(node)?.category ?? '';
    box.dataset.selected = String(picked === node.id);
    box.style.left = Math.max(0, node.x) + 'px'; box.style.top = Math.max(0, node.y) + 'px';
    const head = html('button', nodeTitle(node), 'workspace-node-head');
    head.type = 'button'; head.dataset.tooltip = head.textContent;
    head.addEventListener('click', () => { picked = node.id; wireSelected = null; sideTab = 'properties'; render(); });
    head.addEventListener('dblclick', () => editNode(node));
    head.addEventListener('pointerdown', event => {
      if (event.button !== 0) return;
      const start = [event.clientX, event.clientY, node.x, node.y];
      const move = e => {
        if (e.pointerId !== event.pointerId) return;
        if (!head.hasPointerCapture(event.pointerId)) {
          if (Math.abs(e.clientX - start[0]) + Math.abs(e.clientY - start[1]) < 4) return;
          head.setPointerCapture(event.pointerId);
        }
        node.x = Math.max(0, start[2] + e.clientX - start[0]); node.y = Math.max(0, start[3] + e.clientY - start[1]);
        box.style.left = node.x + 'px'; box.style.top = node.y + 'px'; drawWires();
      };
      const up = () => {
        window.removeEventListener('pointermove', move);
        window.removeEventListener('pointerup', up);
        window.removeEventListener('pointercancel', up);
        if (node.x !== start[2] || node.y !== start[3]) changed();
      };
      window.addEventListener('pointermove', move);
      window.addEventListener('pointerup', up);
      window.addEventListener('pointercancel', up);
    });
    const columns = html('div', undefined, 'workspace-ports');
    const sides = { Input: html('div'), Output: html('div') }; columns.append(sides.Input, sides.Output);
    for (const port of ports(node)) {
      const control = html('button', undefined, 'workspace-port'); control.type = 'button';
      control.append(html('span', portName(port.id))); control.dataset.direction = port.direction; control.dataset.port = port.id; control.dataset.nodeId = node.id;
      control.setAttribute('aria-label', nodeTitle(node) + ' · ' + portName(port.id));
      control.addEventListener('pointerdown', event => startWire(event, node, port, control));
      control.dataset.tooltip = port.id + ' · ' + port.type;
      control.setAttribute('aria-pressed', String(output?.node === node.id && output.port.id === port.id && port.direction === 'Output'));
      control.addEventListener('click', () => { if (!suppressPortClick) choosePort(node, port); });
      points.set(key(node.id, port.id, port.direction), control); sides[port.direction]?.append(control);
    }
    const heading = html('div', undefined, 'workspace-node-heading');
    heading.append(head);
    if (node.type === 'inject' || describe(node)?.manualInput) {
      const send = button('send', () => inject(node, node.settings?.payload ?? {}), 'player-play');
      send.className = 'workspace-node-inject'; send.disabled = dirtyProjects.has(projectId) || !appliedProjects.has(projectId);
      const label = node.type === 'inject' ? tr('send') : nodeTitle(node);
      send.setAttribute('aria-label', label);
      send.dataset.tooltip = label;
      send.replaceChildren(send.firstChild);
      heading.append(send);
    }
    box.append(heading);
    if (!node.connectionId) box.append(html('div', describe(node) ? nameOf(describe(node)) : node.type, 'workspace-node-source'));
    const connection = currentProject().connections.find(c => c.id === node.connectionId);
    if (connection) {
      const description = [describe(node) ? nameOf(describe(node)) : node.type, connection.name].join(' · ');
      const source = html('div', description, 'workspace-node-source');
      source.dataset.tooltip = description;
      box.append(source);
      if (Array.isArray(connection.settings?.devices)) {
        const devices = connection.settings?.devices ?? [];
        const summary = html('button', devices.map(d => tr(d.kind)).join(' + '), 'workspace-node-interfaces');
        summary.type = 'button';
        summary.dataset.tooltip = tr('captureInterfaces');
        summary.addEventListener('click', () => {
          const bindings = devices.map(d => html('p', tr(d.kind) + ' · ' + d.hardwareId));
          popup('captureInterfaces', bindings, null);
        });
        box.append(summary);
      }
    }
    const summaryRows = nodeSummary(node, describe(node), {tr, connection, translate: (key, fallback) => hasTranslation(key) ? t(key) : fallback});
    if (summaryRows.length) {
      box.classList.add('workspace-node-described');
      const summary = html('div', undefined, 'workspace-node-summary');
      for (const entry of summaryRows) {
        const row = html('div', entry.label + ': ' + entry.value);
        row.dataset.tooltip = row.textContent; summary.append(row);
      }
      box.append(summary);
    }
    box.append(columns); board.append(box);
  }
  board.style.width = Math.max(...[...board.querySelectorAll('.workspace-node')].map(n => n.offsetLeft + n.offsetWidth + 20)) + 'px';
  board.style.height = Math.max(...[...board.querySelectorAll('.workspace-node')].map(n => n.offsetTop + n.offsetHeight + 20)) + 'px';
  function drawWires() {
    const bounds = board.getBoundingClientRect();
    svg.replaceChildren();
    page.wires.forEach((w, index) => {
      const a = points.get(key(w.from, w.output, 'Output'))?.getBoundingClientRect();
      const b = points.get(key(w.to, w.input, 'Input'))?.getBoundingClientRect();
      if (!a || !b) return;
      const x1 = a.right - bounds.left, x2 = b.left - bounds.left, y1 = a.top + a.height / 2 - bounds.top, y2 = b.top + b.height / 2 - bounds.top;
      const dx = Math.max(25, Math.abs(x2 - x1) * .4);
      const path = document.createElementNS(svg.namespaceURI, 'path');
      path.dataset.wire = String(index);
      if (x2 <= x1) {
        // Return edges travel outside the cards instead of through their captions.
        const targetBounds = points.get(key(w.to, w.input, 'Input')).closest('.workspace-node').getBoundingClientRect();
        const right = Math.max(x1, targetBounds.right - bounds.left) + 12, left = Math.max(4, x2 - 10), bottom = Math.max(y1, y2) + 16;
        path.setAttribute('d', `M ${x1} ${y1} Q ${right} ${y1} ${right} ${y1 + 8} L ${right} ${bottom - 6} Q ${right} ${bottom} ${right - 6} ${bottom} L ${left + 6} ${bottom} Q ${left} ${bottom} ${left} ${bottom - 6} L ${left} ${y2 + 6} Q ${left} ${y2} ${x2} ${y2}`);
      } else path.setAttribute('d', 'M ' + x1 + ' ' + y1 + ' C ' + (x1 + dx) + ' ' + y1 + ' ' + (x2 - dx) + ' ' + y2 + ' ' + x2 + ' ' + y2);
      path.classList.toggle('selected', wireSelected === index);
      path.addEventListener('click', () => { wireSelected = index; picked = null; render(); });
      svg.append(path);
    });
  }
  drawWires();
}

function addFromPalette(type, x, y) {
  const page = currentPage(); if (!page) return;
  const settings = Object.fromEntries((type.parameters ?? []).filter(p => p.defaultValue !== null && p.defaultValue !== undefined).map(p => [p.id, structuredClone(p.defaultValue)]));
  const node = { id: id(), type: type.id, name: '', connectionId: currentProject().connections.find(c => c.adapter === type.adapter)?.id ?? null,
    settings, x: Math.max(10, Math.round(x / 10) * 10), y: Math.max(10, Math.round(y / 10) * 10) };
  page.nodes.push(node); picked = node.id; sideTab = 'properties'; changed();
}
function drawPalette() {
  paletteSearch.placeholder = tr('searchNodes'); paletteSearch.setAttribute('aria-label', tr('searchNodes'));
  paletteList.replaceChildren();
  const groups = new Map();
  for (const type of catalog) {
    if (!(nameOf(type) + ' ' + type.id).toLowerCase().includes(paletteFilter.toLowerCase())) continue;
    const group = type.adapter || type.category || 'logic';
    if (!groups.has(group)) { const section = html('section'); section.append(html('h3', hasTranslation('workspace.' + group) ? tr(group) : group)); groups.set(group, section); paletteList.append(section); }
    const item = html('button', nameOf(type), 'workspace-palette-node');
    item.type = 'button'; item.draggable = true; item.dataset.type = type.id;
    item.dataset.tooltip = type.id + ' · ' + tr('dragNode');
    item.addEventListener('dragstart', event => { event.dataTransfer.setData('application/x-naseqmen-node', type.id); event.dataTransfer.effectAllowed = 'copy'; });
    item.addEventListener('dblclick', () => addFromPalette(type, canvas.scrollLeft + 30, canvas.scrollTop + 30));
    item.addEventListener('keydown', event => { if (event.key === 'Enter') { event.preventDefault(); addFromPalette(type, canvas.scrollLeft + 30, canvas.scrollTop + 30); } });
    groups.get(group).append(item);
  }
}
canvas.addEventListener('dragover', event => { if (event.dataTransfer.types.includes('application/x-naseqmen-node')) { event.preventDefault(); event.dataTransfer.dropEffect = 'copy'; } });
canvas.addEventListener('drop', event => {
  const type = catalog.find(t => t.id === event.dataTransfer.getData('application/x-naseqmen-node')); if (!type) return;
  event.preventDefault(); const rect = board.getBoundingClientRect(); addFromPalette(type, event.clientX - rect.left, event.clientY - rect.top);
});
function drawProperties() {
  properties.replaceChildren(); const node = currentPage()?.nodes.find(n => n.id === picked);
  if (!node) { properties.append(html('p', tr('selectNode'))); return; }
  const descriptor = describe(node), connection = currentProject().connections.find(c => c.id === node.connectionId);
  const table = html('table');
  for (const [label, value] of [['name', nodeTitle(node)], ['type', descriptor ? nameOf(descriptor) : node.type], ['connection', connection?.name ?? '—']]) {
    const row = html('tr'); row.append(html('th', tr(label)), html('td', value)); table.append(row);
  }
  properties.append(table, button('edit', () => editNode(node), 'pencil'));
  const summary = nodeSummary(node, descriptor, {tr, connection, translate: (key, fallback) => hasTranslation(key) ? t(key) : fallback});
  if (summary.length) {
    const values = html('table');
    for (const entry of summary) { const row = html('tr'); row.append(html('th', entry.label), html('td', entry.value)); values.append(row); }
    properties.append(values);
  }
  if (connection) properties.append(button('editConnection', () => editConnection(connection), 'plug'));
  const list = html('table');
  for (const port of ports(node)) { const row = html('tr'); row.append(html('td', port.direction === 'Input' ? '→' : '←'), html('td', portName(port.id)), html('td', port.type)); list.append(row); }
  properties.append(html('h3', tr('ports')), list);
  const details = html('details'); details.append(html('summary', tr('settings')), html('pre', JSON.stringify(node.settings, null, 2))); properties.append(details);
}
let cancelWire = null, suppressPortClick = false;
function startWire(event, node, port, control) {
  if (event.button !== 0) return;
  event.preventDefault(); event.stopPropagation(); cancelWire?.();
  const svg = board.querySelector('svg'), line = document.createElementNS(svg.namespaceURI, 'path');
  line.classList.add('workspace-wire-preview'); svg.append(line);
  const begin = control.getBoundingClientRect(), bounds = board.getBoundingClientRect();
  const x = (port.direction === 'Output' ? begin.right : begin.left) - bounds.left, y = begin.top + begin.height / 2 - bounds.top;
  let moved = false;
  const move = e => {
    moved ||= Math.abs(e.clientX - event.clientX) + Math.abs(e.clientY - event.clientY) > 3;
    const endX = e.clientX - bounds.left, endY = e.clientY - bounds.top, d = Math.max(35, Math.abs(endX - x) * .4) * (port.direction === 'Output' ? 1 : -1);
    line.setAttribute('d', `M ${x} ${y} C ${x+d} ${y} ${endX-d} ${endY} ${endX} ${endY}`);
    board.querySelectorAll('.workspace-port').forEach(e => e.classList.remove('workspace-port-target'));
    document.elementFromPoint(e.clientX, e.clientY)?.closest('.workspace-port')?.classList.add('workspace-port-target');
  };
  const cancel = () => { line.remove(); window.removeEventListener('pointermove', move); window.removeEventListener('pointerup', up); window.removeEventListener('pointercancel', cancel); window.removeEventListener('keydown', key); board.querySelectorAll('.workspace-port-target').forEach(e => e.classList.remove('workspace-port-target')); cancelWire = null; };
  const up = e => {
    const target = document.elementFromPoint(e.clientX, e.clientY)?.closest('.workspace-port'); cancel();
    if (!moved) return;
    suppressPortClick = true; setTimeout(() => suppressPortClick = false, 0);
    const other = currentPage()?.nodes.find(n => n.id === target?.dataset.nodeId);
    const otherPort = other && ports(other).find(p => p.id === target.dataset.port && p.direction === target.dataset.direction);
    if (!otherPort || other.id === node.id || otherPort.direction === port.direction) return;
    if (port.direction === 'Output') { output = { node: node.id, port }; choosePort(other, otherPort); }
    else { output = { node: other.id, port: otherPort }; choosePort(node, port); }
  };
  const key = e => { if (e.key === 'Escape') { e.preventDefault(); cancel(); } };
  cancelWire = cancel; window.addEventListener('pointermove', move); window.addEventListener('pointerup', up); window.addEventListener('pointercancel', cancel); window.addEventListener('keydown', key); move(event);
}

function render() {
  const project = currentProject();
  const page = currentPage();
  const graph = tab === 'graph';
  const projectSelect = selector(data.projects.map(p => [p.id, p.name]), projectId);
  projectSelect.setAttribute('aria-label', tr('project'));
  projectSelect.addEventListener('change', () => select(data.projects.find(p => p.id === projectSelect.value)));
  const node = page?.nodes.find(n => n.id === picked);
  toolbar.replaceChildren(projectSelect, button('addProject', addProject, 'plus'), button('reload', load, 'refresh'),
    button('validate', async () => { await api('/validate', 'POST', data); status('valid'); }, 'list-check'));
  if (project) {
    const play = button(modePending ? 'switchMode' : 'play', async () => {
      if (modePending) return;
      modePending = true; render();
      try {
        const result = await api('/play', 'POST', { project });
        Object.assign(project, result.project); runtimeState = result.state;
        appliedProjects.add(project.id); dirtyProjects.delete(project.id); dirty = dirtyProjects.size > 0;
        status(dirty ? 'dirty' : 'saved');
      } finally { modePending = false; render(); }
    }, 'player-play');
    play.disabled = modePending; play.dataset.tooltip = tr('playHelp');
    const observe = button('observe', async () => {
      modePending = true; render();
      try { await api('/mode', 'POST', { project: project.id, dryRun: true }); project.dryRun = true; runtimeState = await api('/state'); }
      finally { modePending = false; render(); }
    }, 'eye');
    observe.disabled = modePending || !appliedProjects.has(project.id);
    const active = runtimeState?.projects?.find(p => p.id === project.id);
    const hasFailure = active?.connections?.some(c => ['error','unavailable'].includes(c.state));
    const state = html('span', tr(!active ? 'notDeployed' : hasFailure ? 'connectionError' : active.dryRun ? 'observeOnly' : active.active ? dirtyProjects.has(project.id) ? 'previousLive' : 'outputsLive' : 'offline'), 'workspace-run-state');
    state.dataset.mode = hasFailure ? 'error' : active?.dryRun ? 'test' : 'live';
    state.dataset.tooltip = active?.connections?.map(c => c.id + ': ' + c.state + (c.error ? ' · ' + c.error : '')).join('\n') ?? '';
    toolbar.append(play, observe, state);
  }
  if (graph && page) {
    
    if (node) toolbar.append(button('edit', () => editNode(node), 'pencil'));
    const wire = page.wires[wireSelected];
    const focus = node ? { node: node.id, port: '' }
      : wire ? { node: wire.from, port: wire.output } : null;
    if (focus) {
      const key = JSON.stringify([projectId, pageId, focus.node, focus.port]);
      toolbar.append(button(watching.has(key) ? 'unwatch' : 'watch', async () => {
        const enabled = !watching.has(key);
        await api('/watch', 'POST', { project: projectId, page: pageId, ...focus, enabled });
        if (enabled) { watching.add(key); debug = true; sideTab = 'trace'; }
        else watching.delete(key);
        render();
      }, 'eye'));
    }
    if (node?.type === 'inject') toolbar.append(button('send', () => {
      const payload = json(node.settings?.payload ?? {});
      popup('send', [field('data', payload)], () => inject(node, JSON.parse(payload.value)));
    }, 'player-play'));
    if (node || wireSelected !== null) toolbar.append(button('remove', () => {
      if (node) { page.nodes = page.nodes.filter(n => n.id !== picked); page.wires = page.wires.filter(w => w.from !== picked && w.to !== picked); }
      else page.wires.splice(wireSelected, 1);
      picked = output = wireSelected = null; changed();
    }, 'trash'));
  }
  if (tab === 'connections' && project) toolbar.append(button('addConnection', () => editConnection(), 'plug'));
  
  tabs.hidden = !graph; canvas.hidden = !graph; connections.hidden = graph;
  palette.hidden = !graph || debugWindow; center.hidden = debugWindow; splitter.hidden = debugWindow;
  inspector.hidden = false; properties.hidden = sideTab !== 'properties' || debugWindow; log.hidden = sideTab !== 'trace' && !debugWindow;
  debug = sideTab === 'trace' || debugWindow;
  splitter.setAttribute('aria-label', tr('resizeTrace'));
  if (!debugWindow) resizeSide(sideWidth);
  sideTabs.replaceChildren(...['properties', 'trace'].map(key => {
    const b = button(key, () => { sideTab = key; logFingerprint = ''; render(); void readTrace(); }, key === 'trace' ? 'bug' : 'info-circle');
    b.setAttribute('aria-selected', String(sideTab === key)); return b;
  }));
  drawPalette(); drawProperties();
  tabs.replaceChildren();
  if (graph && project) {
    for (const page of project.pages) {
      const b = html('button', page.name);
      b.type = 'button'; b.setAttribute('aria-selected', String(page.id === pageId));
      b.addEventListener('click', () => { if (page.id !== pageId) select(project, page); });
      const item = html('div', undefined, 'workspace-page-tab');
      const pencil = button('renamePage', () => rename(page), 'pencil');
      pencil.className = 'workspace-page-pencil';
      pencil.setAttribute('aria-label', tr('renamePage') + ' · ' + page.name);
      pencil.dataset.tooltip = tr('renamePage');
      pencil.replaceChildren(pencil.firstChild);
      item.append(b, pencil);
      tabs.append(item);
    }
    tabs.append(button('addPage', addPage, 'plus'));
    if (page) {
      if (project.pages.length > 1) tabs.append(button('deletePage', () => {
        if (project.pages.some(p => p.nodes.some(n => n.type === 'subgraph' && n.settings?.page === page.id)))
          throw Error(tr('subgraph'));
        if (!confirm(tr('deletePage') + ' · ' + page.name + '?')) return;
        project.pages = project.pages.filter(p => p.id !== page.id);
        select(project); changed();
      }, 'trash'));
    }
  }
  if (graph) drawBoard();
  else if (tab === 'connections') {
    connections.replaceChildren();
    const table = html('table'); const body = html('tbody');
    for (const entry of project?.connections ?? []) {
      const row = html('tr'); const actions = html('td');
      actions.append(button('edit', () => editConnection(entry), 'pencil'), button('remove', () => {
        if (project.pages.some(p => p.nodes.some(n => n.connectionId === entry.id))) throw Error(tr('connection'));
        project.connections = project.connections.filter(c => c.id !== entry.id); changed();
      }, 'trash'));
      row.append(html('td', entry.name), html('td', entry.adapter), html('td', entry.enabled ? tr('enabled') : '—'), actions);
      body.append(row);
    }
    table.append(body); connections.append(table);
  } else connections.replaceChildren(button('details', () => {
    document.querySelector('#appearance').open = true;
    document.querySelector('#appearance-summary').focus();
  }, 'palette'));
  updateConnections(project?.connections ?? []);
  const traceControls = html('div', undefined, 'toolbar');
  traceControls.append(button(paused ? 'resume' : 'pause', () => { paused = !paused; render(); }),
    button('clear', async () => { const rows = await api('/trace'); clearedThrough = rows.at(-1)?.time ?? ''; logFingerprint = ''; await readTrace(); }));
  if (!debugWindow) traceControls.append(button('detachTrace', () => {
    const url = new URL(location.href);
    url.search = new URLSearchParams({ view: 'debug', project: projectId ?? '', page: pageId ?? '' });
    window.open(url.href, 'NaSeqMenDebug', 'popup,width=1000,height=580');
  }, 'external-link'));
  const traceTable = log.querySelector('table') ?? html('table');
  log.replaceChildren(traceControls, traceTable);
  status(statusKey || 'offline', message);
}
async function readTrace() {
  if (!debug || paused) return;
  const project = currentProject();
  if (!project) return;
  const rows = (await api('/trace')).filter(x => (x.project === project.id || !x.project) && (!x.page || x.page === pageId) && (!clearedThrough || x.time > clearedThrough));
  const fingerprint = JSON.stringify(rows);
  if (fingerprint === logFingerprint) return;
  logFingerprint = fingerprint;
  const table = log.querySelector('table');
  if (!table) return;
  const follow = log.scrollHeight - log.scrollTop - log.clientHeight < 5;
  const heading = html('tr'); for (const key of ['time', 'node', 'data']) heading.append(html('th', tr(key)));
  const header = html('thead'); header.append(heading);
  const body = html('tbody');
  for (const entry of rows) {
    const row = html('tr');
    const value = entry.detail ?? JSON.stringify(entry.data);
    const node = currentPage()?.nodes.find(n => n.id === entry.node);
    const name = node ? nodeTitle(node) : project.connections.find(c => c.id === entry.node)?.name || entry.node;
    const when = new Date(entry.time).toLocaleTimeString([], { hour12: false, fractionalSecondDigits: 3 });
    const nameCell = html('td', name); nameCell.dataset.tooltip = name + ' · ' + portName(entry.port) + ' · ' + entry.kind;
    const valueCell = html('td'); const details = html('button', value, 'workspace-trace-value');
    details.type = 'button'; details.dataset.tooltip = value;
    details.addEventListener('click', () => popup('details', [html('pre', JSON.stringify(entry, null, 2))], null));
    valueCell.append(details); row.append(html('td', when), nameCell, valueCell); body.append(row);
  }
  table.replaceChildren(header, body);
  if (follow) log.scrollTop = log.scrollHeight;
}
async function readActivity() {
  const received = await api('/activity');
  const latest = received.at(-1)?.id ?? lastActivity;
  if (!activityInitialized) { activityInitialized = true; lastActivity = latest; return; }
  const floor = latest < lastActivity ? 0 : lastActivity;
  lastActivity = latest;
  const nodes = new Set(), wires = new Set(), page = currentPage();
  for (const event of received) {
    if (event.id <= floor || event.project !== projectId || event.page !== pageId) continue;
    nodes.add(event.node);
    if (event.wire) {
      const index = page?.wires.findIndex(w => w.from === event.wire.from && w.output === event.wire.output &&
        w.to === event.wire.to && w.input === event.wire.input);
      if (index >= 0) wires.add(String(index));
    }
  }
  for (const element of board.querySelectorAll('[data-node], [data-wire]')) {
    if (!nodes.has(element.dataset.node) && !wires.has(element.dataset.wire)) continue;
    for (const animation of element.getAnimations()) animation.cancel();
    element.animate(element.dataset.node ? [
      { outline: '2px solid var(--lu-primary)', outlineOffset: '1px' },
      { outline: '2px solid transparent', outlineOffset: '1px' }
    ] : [{ strokeWidth: 4 }, { strokeWidth: 1.5 }], { duration: 460 });
  }
}
nav.replaceChildren(...[['graph', 'arrows-exchange'], ['connections', 'plug']].map(([key, icon]) => {
  const item = html('button', undefined, 'nav-link' + (key === 'graph' ? ' active' : ''));
  item.dataset.tab = key;
  item.setAttribute('data-i18n-label', 'workspace.' + key);
  item.setAttribute('data-i18n-tooltip', 'workspace.' + key);
  const label = html('span'); label.dataset.i18n = 'workspace.' + key;
  item.append(html('i', undefined, 'ti ti-' + icon), label);
  return item;
}));
const settings = html('div', undefined, 'settings-menu');
const settingsLink = html('button', undefined, 'nav-link');
settingsLink.dataset.tab = 'settings';
settingsLink.setAttribute('data-i18n-label', 'ui.settings');
settingsLink.setAttribute('data-i18n-tooltip', 'ui.settings');
const settingsLabel = html('span'); settingsLabel.dataset.i18n = 'ui.settings';
const settingsChildren = html('div', undefined, 'nav-children'); settingsChildren.id = 'settings-connections';
settingsLink.append(html('i', undefined, 'ti ti-settings'), settingsLabel);
settings.append(settingsLink, settingsChildren); nav.append(settings);
const appearanceTrigger = html('button'); appearanceTrigger.id = 'open-appearance'; appearanceTrigger.hidden = true;
editor.append(appearanceTrigger);
initializeShell({
  onNavigate: value => { tab = value; render(); },
  onNavigateConnection: id => {
    const connection = currentProject()?.connections.find(c => c.id === id);
    if (connection) editConnection(connection);
  }
});
onLanguageChange(() => {
  document.querySelector('#connection-state').textContent = tr(connected ? 'ready' : 'offline');
  logFingerprint = ''; render(); void readTrace().catch(() => {});
});
window.addEventListener('beforeunload', event => { if (dirty) { event.preventDefault(); event.returnValue = ''; } });
render();
load().catch(error => status('error', error.message));
setInterval(async () => {
  if (polling || document.hidden) return;
  polling = true;
  try { await Promise.all([readActivity(), readTrace()]); if (!connected) { connected = true; document.querySelector('#connection-state').textContent = tr('ready'); } }
  catch { connected = false; document.querySelector('#connection-state').textContent = tr('offline'); status('offline'); }
  finally { polling = false; }
}, 300);
