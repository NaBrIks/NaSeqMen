// SPDX-License-Identifier: MIT
import { GraphView } from './graph.js';
import { chooseNodeRole, editNode, editConnection, editMapping, editLinks, injectEvent, showDetails } from './forms.js';
import { t, getLanguage, onLanguageChange, bindTranslations, registerTranslations, localizeMessage, displayName } from './i18n.js';
import { initializeShell, updateConnections, selectSettingsConnection } from './shell.js';
import { createConnectionSettings } from './connection-settings.js';
import { createEventDraft, windowsExplorerSteps } from './input/event-drafts.js';
import { SequenceCapture } from './input/sequence-capture.js';
import { DiscoveryCapture } from './input/discovery-capture.js';
import { editOutputModes, renderOutputMode } from './forms/output-modes.js';

const $ = id => document.getElementById(id);
const state = { config: { nodes: [], edges: [], connections: [], mappings: [], dryRun: true }, modules: [], devices: [], watch: new Set(), inputs: new Map(), traces: [], socket: null, connected: false, selected: null };
let lastMessage = '', traceSequence = 0, logFrame = 0;
let configurationLoaded = false;
const inputGroups = new Map();
const sequenceCapture = new SequenceCapture();
let capturedInterfaces = new Set();
const discoveryCapture = new DiscoveryCapture(
  request => api('devices/capture', 'POST', request, { keepalive: true }),
  active => { capturedInterfaces = active; devicesTable.render(); },
  error => status(error, true)
);
function updateDiscoveryCapture() {
  const available = state.connected && !$('tab-devices').hidden
    ? state.devices.filter(device => ['mouse', 'keyboard'].includes(device.kind) && state.watch.has(device.id))
      .map(device => device.id) : [];
  discoveryCapture.select(available);
}
function discoverConfiguredDevices() {
  const sources = new Set(state.config.nodes.filter(node => node.kind === 'source' && node.module === 'windows.input')
    .map(node => node.settings?.source));
  for (const device of state.devices) {
    if (!device.physicalDeviceId || !sources.has(device.id)) continue;
    const family = state.devices.filter(item => ['mouse', 'keyboard'].includes(item.kind) &&
      item.physicalDeviceId === device.physicalDeviceId);
    if (family.some(item => item.kind === 'mouse') && family.some(item => item.kind === 'keyboard')) {
      for (const member of family) state.watch.add(member.id);
    }
  }
  devicesTable.render();
}
let settingsConnection = null;
const connectionSettings = createConnectionSettings($('connection-settings'), {
  onEdit: id => editConnection(state.config, state.modules, id, save).catch(error => status(error, true)),
  onNavigate: id => showSettings(id)
});
function showSettings(id = null) {
  settingsConnection = id;
  document.querySelectorAll('.page').forEach(page => page.hidden = page.id !== 'tab-settings');
  $('settings-overview').hidden = id !== null;
  connectionSettings.show(id, state.config, state.modules);
  selectSettingsConnection(id);
  updateDiscoveryCapture();
}
function el(tag, text, className) { const node = document.createElement(tag); if (text != null) node.textContent = t(text); if (className) node.className = className; return node; }
function tip(node, text) { node.dataset.tooltip = t(text); return node; }
function messageText(value) {
  if (typeof value === 'string') return t(value);
  if (value?.issues?.length) return value.issues.map(localizeMessage).join('\n');
  return value instanceof Error ? value.message : localizeMessage(value) || value?.errors?.join('\n') || '';
}
function status(message, error = false) { lastMessage = message; $('status').textContent = messageText(message); $('status').classList.toggle('error', error); $('status').dataset.tooltip = messageText(message); }
function action(id, callback) { $(id).addEventListener('click', () => Promise.resolve().then(callback).catch(error => status(error, true))); }
async function api(path, method = 'GET', body, options = {}) {
  const response = await fetch('/api/' + path, { ...options, method, headers: body === undefined ? {} : { 'Content-Type': 'application/json' }, body: body === undefined ? undefined : JSON.stringify(body) });
  const text = await response.text(); let data;
  try { data = text ? JSON.parse(text) : {}; } catch { throw new Error(text || `HTTP ${response.status}`); }
  if (!response.ok) {
    const error = new Error();
    Object.defineProperty(error, 'message', { get: () => messageText(data) || `HTTP ${response.status}` });
    throw error;
  }
  return data;
}
async function save(config) {
  const result = await api('config', 'PUT', config);
  if (!result.valid) { const error = new Error(); Object.defineProperty(error, 'message', { get: () => messageText(result) || t('ui.settings_were_rejected') }); throw error; }
  state.config = await api('config'); renderConfiguration(); status('ui.settings_saved_by_the_executor');
}
const graph = new GraphView($('graph'), {
  onSelect(node) { state.selected = typeof node === 'string' ? node : node?.id; const selected = state.config.nodes.find(n => n.id === state.selected); $('selected-node').textContent = selected?.name || t('ui.select_a_node'); },
  onEdit(node) { openNode(typeof node === 'string' ? node : node.id).catch(error => status(error, true)); },
  async onMode(id, mode) {
    const config = structuredClone(state.config);
    const node = config.nodes.find(item => item.id === id);
    if (!node) return;
    node.enabled = mode !== 'off';
    node.previewOnly = mode === 'test';
    await save(config);
  },
  onEdgeSelect(edge) { $('delete-link').disabled = !edge; },
  onRemoveEdge(edge) { removeEdge(edge).catch(error => status(error, true)); },
  async onLink(from, to) {
    try {
      const config = structuredClone(state.config); if (config.edges.some(e => e.from === from && e.to === to)) throw new Error(t('ui.this_link_already_exists'));
      config.edges.push({ from, to }); await save(config);
    } catch (error) { status(error, true); }
  }, onError: message => status(message, true)
});

async function removeEdge(edge) {
  if (!edge) return;
  const config = structuredClone(state.config);
  config.edges = config.edges.filter(item => item.from !== edge.from || item.to !== edge.to);
  if (config.edges.length !== state.config.edges.length) await save(config);
}

// Fixed columns, sorting and filters. Rows are replaced only on configuration changes,
// or once per animation frame for diagnostics. Preserve the reader's visible anchor.
class Table {
  constructor(host, columns, { rowClick, tail = false, empty = 'ui.no_entries' } = {}) {
    this.host = host; this.columns = columns; this.rowClick = rowClick; this.tail = tail; this.empty = empty;
    this.filters = {}; this.sort = null; this.rows = []; this.table = el('table'); this.body = el('tbody'); this.unsubscribe = [];
    this.table.style.minWidth = columns.reduce((sum, c) => sum + c.width, 0) + 'px';
    const head = el('thead'), row = el('tr'), cols = el('colgroup');
    for (const column of columns) {
      const col = el('col'); col.style.width = column.width + 'px'; cols.append(col);
      const th = el('th');
      if (!column.key) { th.textContent = t(column.label || ''); th.dataset.i18n = column.label || ''; }
      else {
        const sort = tip(el('button', column.label), t(column.help || column.label) + '\n' + t('ui.click_to_sort'));
        sort.dataset.i18n = column.label;
        sort.addEventListener('click', () => { this.sort = { key: column.key, direction: this.sort?.key === column.key ? -this.sort.direction : 1 }; this.render(); }); th.append(sort);
        const filter = tip(el('input'), t('ui.filter') + ' ' + t(column.label) + '. ' + t('ui.an_empty_field_shows_all_rows')); filter.setAttribute('aria-label', t('ui.filter') + ' ' + t(column.label));
        this.unsubscribe.push(onLanguageChange(() => { tip(sort, t(column.help || column.label) + '\n' + t('ui.click_to_sort')); tip(filter, t('ui.filter') + ' ' + t(column.label) + '. ' + t('ui.an_empty_field_shows_all_rows')); filter.setAttribute('aria-label', t('ui.filter') + ' ' + t(column.label)); }));
        filter.addEventListener('input', () => { this.filters[column.key] = filter.value.toLocaleLowerCase(); this.render(); }); th.append(filter);
      }
      row.append(th);
    }
    head.append(row); this.table.append(cols, head, this.body); host.replaceChildren(this.table);
  }
  set(rows) { this.rows = rows; this.render(); }
  dispose() { for (const unsubscribe of this.unsubscribe) unsubscribe(); }
  render() {
    const host = this.host, atEnd = host.scrollHeight - host.clientHeight - host.scrollTop <= 2;
    const oldTop = host.scrollTop;
    const oldRows = [...this.body.children];
    const anchor = oldRows.find(row => row.offsetTop + row.offsetHeight > oldTop + this.table.tHead.offsetHeight);
    const anchorId = anchor?.dataset.rowId, delta = anchor ? anchor.offsetTop - oldTop : 0;
    let rows = this.rows.filter(row => Object.entries(this.filters).every(([key, value]) => String(row[key] ?? '').toLocaleLowerCase().includes(value)));
    if (this.sort) rows = [...rows].sort((a, b) => String(a[this.sort.key] ?? '').localeCompare(String(b[this.sort.key] ?? ''), getLanguage(), { numeric: true }) * this.sort.direction);
    const fragment = document.createDocumentFragment();
    for (const data of rows) {
      const row = el('tr'); row.dataset.rowId = String(data.id ?? '');
      for (const column of this.columns) {
        const cell = el('td'); if (column.render) cell.append(column.render(data)); else { cell.textContent = data[column.key] ?? ''; tip(cell, (column.help ? t(column.help) + '\n' : '') + String(data[column.key] ?? '')); }
        row.append(cell);
      }
      if (this.rowClick) row.addEventListener('dblclick', () => this.rowClick(data)); fragment.append(row);
    }
    if (!rows.length) { const row = el('tr'), cell = el('td', this.empty, 'empty-row'); cell.colSpan = this.columns.length; row.append(cell); fragment.append(row); }
    this.body.replaceChildren(fragment);
    if (this.tail && atEnd) host.scrollTop = host.scrollHeight;
    else if (anchorId) { const replacement = [...this.body.children].find(row => row.dataset.rowId === anchorId); host.scrollTop = replacement ? replacement.offsetTop - delta : 0; }
    else host.scrollTop = oldTop;
  }
}
function editButton(callback, help = 'ui.open_row_settings') { const button = tip(el('button', null, 'row-action'), help); button.append(el('i', null, 'ti ti-pencil')); button.firstChild.setAttribute('aria-hidden', 'true'); button.setAttribute('aria-label', t(help)); button.addEventListener('click', () => Promise.resolve(callback()).catch(error => status(error, true))); return button; }
function observeButton(device) {
  const supported = ['mouse', 'keyboard'].includes(device.kind), active = state.watch.has(device.id);
  const captured = capturedInterfaces.has(device.id);
  const button = el('button', null, 'row-action' + (active ? ' active' : '') + (captured ? ' captured' : ''));
  const icon = el('i', null, 'ti ti-' + (!supported ? 'ban' : active ? 'player-stop' : 'player-play')); icon.setAttribute('aria-hidden', 'true');
  button.append(icon, document.createTextNode(t(!supported ? 'ui.unavailable' : !state.connected ? 'ui.disconnected'
    : captured ? 'input.capture.active' : active ? 'ui.active' : 'ui.off')));
  button.disabled = !supported || !state.connected;
  tip(button, !supported ? 'ui.an_auxiliary_hid_was_detected_but_reading_its'
    : captured ? 'input.capture.stopHelp' : device.canSuppress ? 'input.capture.startHelp'
    : active ? 'ui.stop_discovery_for_this_interface_the_active_graph' : 'ui.start_discovering_buttons_on_this_interface_you_can');
  button.setAttribute('aria-label', t(active ? 'ui.stop' : 'ui.start') + ' ' + t('ui.discovery_2') + ' ' + device.name);
  button.addEventListener('click', () => {
    if (active) { state.watch.delete(device.id); for (const input of state.inputs.values()) if (input.source === device.id) clearHighlight(input); }
    else state.watch.add(device.id);
    devicesTable.render(); updateDiscoveryCapture(); status(t(active ? 'ui.stopped' : 'ui.discovery_enabled') + ' ' + device.name);
  }); return button;
}
const devicesTable = new Table($('devices'), [
  { label: 'ui.discovery', width: 125, render: observeButton },
  { label: 'forms.operation.sequence', width: 116, render: sequenceButton },
  { label: 'ui.device_interface', key: 'name', width: 310, help: 'ui.one_physical_mouse_can_expose_multiple_interfaces' },
  { label: 'ui.type', key: 'kind', width: 90, help: 'ui.mouse_mouse_interface_keyboard_keyboard_interface_hid_auxiliary' },
  { label: 'ui.suppression', key: 'suppression', width: 120, help: 'input.capture.capabilityHelp' },
  { label: 'ui.device_path', key: 'hardwareId', width: 460, help: 'ui.exact_windows_interface_identity' }
]);
const mappingsTable = new Table($('mappings'), [
  { width: 30, render: row => editButton(() => editMapping(state.config, row.id, null, save)) },
  { label: 'ui.button_name', key: 'name', width: 200 }, { label: 'ui.original_code', key: 'control', width: 150 },
  { label: 'ui.policy', key: 'policy', width: 100, help: 'ui.map_use_a_name_in_the_graph_observe' },
  { label: 'ui.interface', key: 'source', width: 450 }
], { empty: 'ui.no_saved_mappings_yet' });
const connectionsTable = new Table($('connections'), [
  { width: 30, render: row => editButton(() => editConnection(state.config, state.modules, row.id, save)) },
  { label: 'ui.name', key: 'name', width: 220 }, { label: 'ui.module', key: 'module', width: 180 },
  { label: 'ui.enabled', key: 'enabledText', width: 100 }, { label: 'ui.parameters', key: 'parameters', width: 400 },
  { width: 70, render: row => { const button = tip(el('button', 'ui.delete', 'row-action'), 'ui.delete_this_connection_first_remove_graph_nodes_that'); button.addEventListener('click', async () => { try { const config = structuredClone(state.config); config.connections = config.connections.filter(c => c.id !== row.id); await save(config); } catch (error) { status(error, true); } }); return button; } }
]);
const stages = { input: 'ui.input', received: 'ui.received', routed: 'ui.routed', passed: 'ui.passed', blocked: 'ui.blocked', transformed: 'ui.transformed', script: 'C#', intended: 'ui.command', suppressed: 'ui.dry_run_output', completed: 'ui.completed', error: 'ui.error' };
const logsTable = new Table($('logs'), [
  { label: 'ui.time', key: 'time', width: 105 }, { label: 'ui.node_2', key: 'node', width: 220 },
  { label: 'ui.stage', key: 'stage', width: 130, help: 'ui.processing_stage_one_input_can_produce_several_diagnostic' },
  { label: 'ui.result', key: 'message', width: 330 }, { label: 'ui.data', key: 'data', width: 320 }
], { tail: true, empty: 'ui.new_events_appear_here_double_click_a_row', rowClick: row => showDetails(t('ui.diagnostics'), [row.time, row.node, row.stage, row.message, row.data].join('\n')) });

function time(value) { const d = new Date(value); return d.toLocaleTimeString(getLanguage() === 'en' ? 'en-GB' : 'ru-RU', { hour12: false }) + '.' + String(d.getMilliseconds()).padStart(3, '0'); }
function controlName(control) { const name = ({ 'mouse:button1': 'ui.left', 'mouse:button2': 'ui.right', 'mouse:button3': 'ui.middle', 'mouse:button4': 'ui.side_1', 'mouse:button5': 'ui.side_2', 'mouse:wheel': 'ui.wheel', 'mouse:horizontalWheel': 'ui.wheel_2' })[control]; return name ? t(name) : control.replace(/^key:/, t('ui.key') + ' '); }
function clearHighlight(input) { clearTimeout(input.timer); input.button.classList.remove('pressed', 'pulse'); }
function inputGroup(input) {
  let group = inputGroups.get(input.source);
  if (!group) {
    const section = el('section', null, 'input-group'), title = el('header', null, 'input-group-title'), events = el('div', null, 'input-group-events');
    section.dataset.source = input.source; section.append(title, events); $('inputs').append(section);
    group = { section, title, events }; inputGroups.set(input.source, group);
  }
  const device = state.devices.find(d => d.id === input.source);
  group.title.textContent = (device ? displayName(device) : t(input.kind === 'mouse' ? 'ui.mouse' : 'ui.keyb')) + ' · ' + input.number;
  tip(group.title, input.source);
  return group;
}
function addInputToGraph(input) {
  const mapping = state.config.mappings.find(m => m.source === input.source && m.control === input.control);
  const name = mapping?.name || controlName(input.control);
  const device = state.devices.find(item => item.id === input.source);
  return openEventDraft({ ...input, connectionId: input.connectionId || '', name: displayName(device) },
    name, 'match', { control: mapping?.policy === 'map' ? mapping.name : input.control, phase: '' });
}

function openEventDraft(endpoint, name, operation, settings) {
  const { seed, attach } = createEventDraft(state.config, endpoint, name, operation, settings);
  return editNode(state.config, state.modules, null, save, seed, attach, sequenceCapture);
}

function openNode(id) {
  return editNode(state.config, state.modules, id, save, null, null, sequenceCapture);
}

function sequenceButton(device) {
  const box = el('span');
  if (device.kind !== 'keyboard') return box;
  const connection = state.config.connections.find(item => item.enabled && item.module === 'windows.input' &&
    item.settings?.devicePath?.toLowerCase() === device.id.toLowerCase()) ||
    state.config.connections.find(item => item.enabled && item.module === 'windows.input' && !item.settings?.devicePath);
  const button = tip(el('button', 'ui.add_sequence', 'row-action'), 'ui.sequence_help');
  button.disabled = !connection || !state.connected;
  button.addEventListener('click', () => openEventDraft(
    { module: connection.module, connectionId: connection.id, source: device.id, name: device.name },
    'Win+E', 'sequence', { eventName: 'Win+E', steps: windowsExplorerSteps, timeoutMs: '1500' }
  ).catch(error => status(error, true)));
  box.append(button);
  return box;
}
function updateInput(input) {
  const mapping = state.config.mappings.find(m => m.source === input.source && m.control === input.control);
  input.button.textContent = mapping?.name || controlName(input.control);
  inputGroup(input);
  input.add.setAttribute('aria-label', t('ui.add_event_to_graph')); tip(input.add, 'ui.add_event_to_graph');
  tip(input.button, input.button.textContent + '\n' + t('ui.last_signal') + ' ' + t(({ down: 'ui.pressed', up: 'ui.released', change: 'ui.changed' }[input.phase] || input.phase)) + ' · ' + input.value + ' · ' + input.time + '\n' + t('ui.code') + ' ' + input.control + '\n' + t('ui.interface_2') + ' ' + input.source + '\n' + t('ui.click_for_details_and_to_assign_a_name'));
}
function discovered(signal, timestamp) {
  if (!state.watch.has(signal.source)) return;
  const key = JSON.stringify([signal.source, signal.control]); let input = state.inputs.get(key);
  if (!input) {
    if (state.inputs.size >= 512) { status('ui.the_panel_contains_512_distinct_inputs_clear_the', true); return; }
    const device = state.devices.find(d => d.id === signal.source); if (!device) return;
    const index = state.devices.filter(d => d.kind === device.kind).findIndex(d => d.id === device.id) + 1;
    input = { source: signal.source, control: signal.control, module: signal.module, connectionId: signal.connectionId, kind: device.kind, number: index, button: el('button', '', 'input-key'), add: el('button', null, 'input-add icon-only') };
    const icon = el('i', null, 'ti ti-plus'); icon.setAttribute('aria-hidden', 'true'); input.add.append(icon);
    input.add.addEventListener('click', () => addInputToGraph(input).catch(error => status(error, true)));
    input.button.addEventListener('click', () => {
      const mapping = state.config.mappings.find(m => m.source === input.source && m.control === input.control);
      editMapping(state.config, mapping?.id, { source: input.source, control: input.control, phase: input.phase, value: input.value, time: input.time }, save).catch(error => status(error, true));
    });
    if (!state.inputs.size) $('inputs').replaceChildren(); state.inputs.set(key, input);
    const item = el('span', null, 'input-item'); item.append(input.button, input.add); inputGroup(input).events.append(item);
  }
  input.phase = signal.phase; input.value = signal.value; input.time = time(timestamp); clearHighlight(input);
  input.button.classList.toggle('pressed', signal.phase === 'down'); input.button.classList.toggle('pulse', signal.phase === 'change');
  if (signal.phase === 'change') input.timer = setTimeout(() => input.button.classList.remove('pulse'), 350);
  updateInput(input);
}
function diagnostic(d) {
  const signal = d.signal; const node = state.config.nodes.find(n => n.id === d.nodeId);
  state.traces.push({ diagnostic: d, id: ++traceSequence, time: time(d.time), node: node?.name || (d.stage === 'input' ? t('ui.input') + ': ' + (signal?.module || '') : ''), stage: t(stages[d.stage] || d.stage), stageKey: d.stage, message: localizeMessage(d), data: signal ? `${signal.control} / ${signal.phase} / ${signal.value}` : '' });
  if (state.traces.length > 400) state.traces.splice(0, state.traces.length - 400);
  if (!logFrame) logFrame = requestAnimationFrame(() => { logFrame = 0; logsTable.set(state.traces); });
  graph.flash(d);
  if (d.stage === 'input' && signal) {
    sequenceCapture.accept(signal);
    discovered(signal, d.time);
  }
}
function connected(value) {
  state.connected = value; $('connection-state').textContent = t(value ? 'ui.connected' : 'ui.disconnected'); $('connection-state').classList.toggle('online', value);
  if (!value) { state.watch.clear(); for (const input of state.inputs.values()) clearHighlight(input); }
  devicesTable.render();
  if (value && !$('tab-devices').hidden) discoverConfiguredDevices();
  updateDiscoveryCapture();
}
function clearInputs() { for (const input of state.inputs.values()) clearHighlight(input); state.inputs.clear(); inputGroups.clear(); $('inputs').replaceChildren(el('span', 'ui.press_a_physical_button_on_an_observed_device', 'empty')); }
function renderConfiguration() {
  renderOutputMode($('output-modes'), configurationLoaded ? state.config : null);
  graph.setConfiguration(state.config, state.modules); mappingsTable.set(state.config.mappings);
  $('selected-node').textContent = state.config.nodes.find(node => node.id === state.selected)?.name || t('ui.select_a_node');
  connectionsTable.set(state.config.connections.map(c => ({ ...c, enabledText: t(c.enabled ? 'ui.yes' : 'ui.no'), parameters: Object.entries(c.settings).filter(([k]) => !/password|token|secret/i.test(k)).map(([k, v]) => `${k}=${v}`).join('; ') })));
  updateConnections(state.config.connections);
  connectionSettings.show(settingsConnection, state.config, state.modules);
  for (const input of state.inputs.values()) updateInput(input);
}
async function refreshDevices() {
  state.devices = (await api('devices')).sort((a, b) => Number(!['mouse', 'keyboard'].includes(a.kind)) - Number(!['mouse', 'keyboard'].includes(b.kind)));
  graph.setDevices(state.devices);
  for (const source of state.watch) if (!state.devices.some(d => d.id === source)) { state.watch.delete(source); for (const input of state.inputs.values()) if (input.source === source) clearHighlight(input); }
  devicesTable.set(state.devices.map(d => ({ ...d, name: displayName(d), suppression: t(d.canSuppress ? 'ui.available' : 'ui.no_observe_only') })));
  updateDiscoveryCapture();
}
async function reload() { const [config, modules] = await Promise.all([api('config'), api('modules')]); state.config = config; state.modules = modules; configurationLoaded = true; renderConfiguration(); await refreshDevices(); }
async function connect() {
  if (state.socket) { const old = state.socket; state.socket = null; old.close(); }
  connected(false); registerTranslations(await api('localization')); await reload(); state.traces = []; logsTable.set([]); clearInputs();
  const socket = new WebSocket(location.origin.replace(/^http/, 'ws') + '/api/diagnostics'); state.socket = socket;
  socket.addEventListener('open', () => { if (state.socket !== socket) return; connected(true); status('ui.connected_to_executor_viewing_current_session_events'); });
  socket.addEventListener('message', event => { if (state.socket !== socket) return; try { diagnostic(JSON.parse(event.data)); } catch (error) { status(error, true); } });
  socket.addEventListener('close', () => { if (state.socket === socket) { connected(false); status('ui.diagnostics_disconnected_click_connect', true); } });
  socket.addEventListener('error', () => { if (state.socket === socket) status('ui.could_not_connect_to_the_diagnostic_stream', true); });
}

initializeShell({
  onNavigate(tab) {
    if (tab === 'settings') { showSettings(); return; }
    document.querySelectorAll('.page').forEach(page => page.hidden = page.id !== 'tab-' + tab);
    if (tab === 'devices') discoverConfiguredDevices();
    updateDiscoveryCapture();
  },
  onNavigateConnection: showSettings
});
bindTranslations();
action('connect', connect); action('reload', reload); action('refresh-devices', refreshDevices);
action('status', () => showDetails(t('ui.last_operation'), messageText(lastMessage)));
action('graph-help', () => showDetails(t('ui.graph_help'), t('ui.source_condition_processing_action_drag_an_output_port')));
action('inputs-help', () => showDetails(t('ui.button_discovery_help'), t('ui.enable_mouse_and_keyboard_interfaces_each_interface_code')));
action('log-help', () => showDetails(t('ui.diagnostics_help'), t('ui.the_log_shows_only_new_events_and_processing')));
action('validate', async () => { const result = await api('config/validate', 'POST', state.config); status(result.valid ? 'ui.graph_is_valid_no_errors' : result, !result.valid); });
action('add-node', async () => {
  const kind = await chooseNodeRole();
  if (kind) return editNode(state.config, state.modules, null, save, { kind }, null, sequenceCapture);
});
action('edit-node', () => state.selected ? openNode(state.selected) : status('ui.select_a_graph_node'));
action('delete-link', () => removeEdge(graph.selectedEdge));
action('delete-node', async () => { if (!state.selected) return status('ui.select_a_graph_node'); const config = structuredClone(state.config); config.nodes = config.nodes.filter(n => n.id !== state.selected); config.edges = config.edges.filter(e => e.from !== state.selected && e.to !== state.selected); await save(config); state.selected = null; $('selected-node').textContent = t('ui.select_a_node'); });
action('add-connection', () => editConnection(state.config, state.modules, null, save));
action('links', () => editLinks(state.config, save)); action('inject', () => injectEvent(signal => api('inject', 'POST', signal)));
action('clear-inputs', clearInputs); action('clear-log', () => { state.traces = []; logsTable.set([]); });
action('toggle-log', () => { const collapsed = document.body.classList.toggle('log-collapsed'); $('toggle-log').textContent = (collapsed ? '▸' : '▾') + ' ' + t('ui.live_trace'); $('toggle-log').setAttribute('aria-expanded', String(!collapsed)); });
$('zoom').addEventListener('input', event => graph.setZoom(Number(event.target.value)));
action('output-modes', () => editOutputModes(() => state.config, Table, save));

onLanguageChange(() => {
  renderConfiguration();
  devicesTable.set(state.devices.map(d => ({ ...d, name: displayName(d), suppression: t(d.canSuppress ? 'ui.available' : 'ui.no_observe_only') })));
  for (const trace of state.traces) {
    trace.stage = t(stages[trace.stageKey] || trace.stageKey);
    trace.message = localizeMessage(trace.diagnostic);
    trace.time = time(trace.diagnostic.time);
    trace.node = state.config.nodes.find(n => n.id === trace.diagnostic.nodeId)?.name || (trace.stageKey === 'input' ? t('ui.input') + ': ' + (trace.diagnostic.signal?.module || '') : '');
  }
  logsTable.set(state.traces);
  $('connection-state').textContent = t(state.connected ? 'ui.connected' : 'ui.disconnected');
  $('toggle-log').textContent = (document.body.classList.contains('log-collapsed') ? '▸' : '▾') + ' ' + t('ui.live_trace');
  if (!state.selected) $('selected-node').textContent = t('ui.select_a_node');
  if (!state.inputs.size) $('inputs').replaceChildren(el('span', 'ui.press_a_physical_button_on_an_observed_device', 'empty'));
  status(lastMessage, $('status').classList.contains('error'));
});
connect().catch(error => status(error, true));

export { Table };
