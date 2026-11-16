// SPDX-License-Identifier: MIT
import { t, getLanguage, hasTranslation, onLanguageChange } from './i18n.js';
import { curvedWire } from './graph/wire-path.js';
import { layoutDeviceSections, sectionHeight } from './graph/device-sections.js';
import { projectDestinations } from './graph/destinations.js';
import { sectionControls } from './graph/section-controls.js';
export { layoutGraph } from './graph/layout.js';
const NS = 'http://www.w3.org/2000/svg';
const lanes = ['graph.lane.sources', 'graph.lane.events', 'graph.lane.conditions', 'graph.lane.processing', 'graph.lane.actions', 'graph.lane.destinations'];
const portTypeKeys = { any: 'graph.portType.any', number: 'graph.portType.number', text: 'graph.portType.text', bool: 'graph.portType.bool' };
const phaseKeys = { down: 'graph.phase.down', up: 'graph.phase.up', change: 'graph.phase.change' };
const operationNames = { equals: 'graph.operation.equals', scale: 'graph.operation.scale', text: 'graph.operation.text', key: 'graph.operation.key', chord: 'graph.operation.chord', launch: 'graph.operation.launch', foreground: 'graph.operation.foreground', processRunning: 'graph.operation.processRunning', startRecord: 'graph.operation.startRecord', stopRecord: 'graph.operation.stopRecord', toggleRecord: 'graph.operation.toggleRecord', setMute: 'graph.operation.setMute', toggleMute: 'graph.operation.toggleMute', setVolume: 'graph.operation.setVolume', setScene: 'graph.operation.setScene', request: 'graph.operation.request' };
let nextGraph = 0;
const modePaths = { play: 'M8 5l12 7-12 7z', pause: 'M9 5v14m6-14v14' };
const addKinds = [['source'], ['event'], ['condition'], ['transform', 'script'], ['action'], ['action']];

function svgElement(tag, attributes = {}, text) {
  const element = document.createElementNS(NS, tag);
  for (const [key, value] of Object.entries(attributes)) element.setAttribute(key, String(value));
  if (text !== undefined) element.textContent = text;
  return element;
}

function shortText(text, width, average = 6.5) {
  const characters = Array.from(String(text ?? ''));
  const limit = Math.max(4, Math.floor(width / average));
  return characters.length <= limit ? characters.join('') : characters.slice(0, limit - 1).join('') + '…';
}

export function portType(node, direction) {
  if (node.kind === 'transform' && node.operation === 'scale') return 'number';
  if (direction === 'out' && node.kind === 'transform' && node.operation === 'text') return 'text';
  if (direction === 'out' && node.kind === 'event' && node.operation === 'sequence') return 'number';
  return (direction === 'out' ? node.outputType : node.inputType) || 'any';
}

function portTypeLabel(node, direction) {
  const type = portType(node, direction);
  return portTypeKeys[type] ? t(portTypeKeys[type]) : type;
}

export function compatiblePorts(from, to) {
  if (!from || !to || from.id === to.id || from.kind === 'action' || to.kind === 'source') return false;
  const miss = eventMissLink(from, to);
  if (to.kind === 'event' && to.operation === 'default' && !miss) return false;
  const output = portType(from, miss ? 'in' : 'out'), input = portType(to, 'in');
  return output === 'any' || input === 'any' || output === input;
}

export function eventMissLink(from, to) {
  return from.kind === 'event' && from.operation !== 'default' && to.kind === 'event';
}

export class GraphView {
  constructor(container, callbacks = {}) {
    this.container = container;
    this.callbacks = callbacks;
    this.configuration = { nodes: [], edges: [] };
    this.devices = [];
    this.modules = [];
    this.collapsedDevices = new Set();
    this.zoom = 1;
    this.selected = null;
    this.selectedEdge = null;
    this.wireStart = null;
    this.nodeElements = new Map();
    this.edgeElements = new Map();
    this.timers = new Map();
    this.pendingModes = new Set();
    this.addTargets = new Map();
    this.titleMeasure = document.createElement('canvas').getContext('2d');
    this.id = `graph-${++nextGraph}`;
    container.classList.add('graph-host');
    this.svg = svgElement('svg', { class: 'graph-surface', role: 'group' });
    container.replaceChildren(this.svg);
    this.svg.addEventListener('pointermove', event => this.pointerMove(event));
    this.svg.addEventListener('pointerup', event => this.pointerUp(event));
    this.svg.addEventListener('pointercancel', () => this.cancelWire());
    this.svg.addEventListener('keydown', event => { if (event.key === 'Escape') { this.cancelWire(); event.preventDefault(); } });
    this.svg.addEventListener('click', event => {
      if (event.target === this.svg || event.target.classList.contains('graph-lane-background')) {
        this.cancelWire(); this.select(null);
      }
    });
    this.resize = new ResizeObserver(() => {
      const width = Math.max(840, container.clientWidth / this.zoom - 2);
      if (!this.layout || Math.abs(this.layout.width - width) > 1) this.render();
    });
    this.resize.observe(container);
    this.unsubscribeLanguage = onLanguageChange(() => this.render());
    this.densityObserver = new MutationObserver(() => this.render());
    this.densityObserver.observe(container.closest('.lu-shell') || document.documentElement, { attributes: true, attributeFilter: ['data-density'] });
    this.render();
  }

  setConfiguration(configuration, modules = this.modules) {
    this.configuration = configuration || { nodes: [], edges: [] };
    this.modules = modules;
    if (!this.configuration.nodes?.some(node => node.id === this.selected)) this.selected = null;
    if (this.selectedEdge && !this.configuration.edges.some(edge => edge.from === this.selectedEdge.from && edge.to === this.selectedEdge.to)) this.selectEdge(null);
    this.cancelWire();
    this.render();
  }

  setZoom(value) {
    this.zoom = Math.min(2, Math.max(0.6, Number(value) || 1));
    this.render();
  }

  setDevices(devices) {
    this.devices = devices;
    this.render();
  }

  select(id) {
    if (this.selectedEdge) this.selectEdge(null);
    this.selected = id;
    for (const [nodeId, element] of this.nodeElements) {
      element.classList.toggle('graph-selected', nodeId === id);
      element.setAttribute('aria-pressed', String(nodeId === id));
    }
    this.callbacks.onSelect?.(this.configuration.nodes.find(node => node.id === id) || null);
  }

  selectEdge(edge) {
    if (edge && this.selected) this.select(null);
    this.selectedEdge = edge;
    for (const [key, line] of this.edgeElements) {
      const selected = !!edge && key === JSON.stringify([edge.from, edge.to]);
      line.classList.toggle('graph-wire-selected', selected);
      line.parentElement.setAttribute('aria-pressed', String(selected));
    }
    this.callbacks.onEdgeSelect?.(edge);
  }

  render() {
    for (const timer of this.timers.values()) clearTimeout(timer);
    this.timers.clear();
    this.nodeElements.clear(); this.edgeElements.clear(); this.addTargets.clear();
    this.container.setAttribute('aria-label', t('graph.accessibleName'));
    this.svg.setAttribute('lang', getLanguage());
    this.svg.setAttribute('data-tooltip', t('graph.help'));
    this.titleMeasure.font = getComputedStyle(this.svg).font;
    const cellPadding = parseFloat(getComputedStyle(this.container).getPropertyValue('--cell-y')) || 3;
    const projected = projectDestinations(this.configuration, this.modules);
    this.actionTargets = projected.destinations;
    this.layout = layoutDeviceSections(projected.configuration, this.devices, this.collapsedDevices,
      this.container.clientWidth / this.zoom - 2, cellPadding, lanes.length);
    const { width, height, laneWidth, nodes, edges, sections } = this.layout;
    this.svg.replaceChildren();
    this.svg.setAttribute('viewBox', `0 0 ${width} ${height}`);
    this.svg.setAttribute('width', width * this.zoom);
    this.svg.setAttribute('height', height * this.zoom);
    const defs = svgElement('defs');
    const marker = svgElement('marker', { id: `${this.id}-arrow`, viewBox: '0 0 8 8', refX: 7, refY: 4, markerWidth: 5, markerHeight: 5, orient: 'auto-start-reverse' });
    marker.append(svgElement('path', { d: 'M0 0 L8 4 L0 8 z', fill: 'context-stroke' }));
    defs.append(marker); this.svg.append(defs);
    for (let lane = 0; lane < lanes.length; lane++) {
      this.svg.append(svgElement('rect', { class: 'graph-lane-background', x: lane * laneWidth, y: 0, width: laneWidth, height }));
      this.svg.append(svgElement('text', { class: 'graph-lane-label', x: lane * laneWidth + 10, y: 21 }, t(lanes[lane])));
    }
    this.svg.append(svgElement('line', { class: 'graph-divider', x1: 0, y1: 33, x2: width, y2: 33 }));
    for (const section of sections) this.renderSection(section, width);
    if (this.selected && !nodes.has(this.selected)) this.select(null);
    if (this.selectedEdge && (!nodes.has(this.selectedEdge.from) || !nodes.has(this.selectedEdge.to))) this.selectEdge(null);
    for (const { edge, path, offset } of edges) {
      const line = svgElement('path', { class: 'graph-wire', d: path, 'marker-end': `url(#${this.id}-arrow)` });
      if (edge.outcome === 'miss') line.setAttribute('stroke-dasharray', '5 3');
      const key = JSON.stringify([edge.from, edge.to]);
      this.edgeElements.set(key, line);
      if (edge.delivery) {
        const group = svgElement('g', { class: 'graph-delivery', 'aria-hidden': 'true' });
        if (offset) group.setAttribute('transform', `translate(0 ${offset})`);
        group.append(line);
        this.svg.append(group);
        continue;
      }
      const label = `${nodes.get(edge.from).node.name} → ${nodes.get(edge.to).node.name}`;
      const caption = nodes.get(edge.from).node.kind === 'event'
        ? label + ' · ' + t(edge.outcome === 'miss' ? 'forms.outcome.miss' : 'forms.outcome.match') : label;
      const wire = svgElement('g', { class: 'graph-edge', role: 'button', tabindex: '0', 'aria-label': caption, 'data-tooltip': caption + '\n' + t('graph.wireHelp') });
      if (offset) wire.setAttribute('transform', `translate(0 ${offset})`);
      wire.append(svgElement('path', { class: 'graph-wire-hit', d: path }), line);
      wire.addEventListener('click', () => { this.selectEdge(edge); wire.focus(); });
      wire.addEventListener('keydown', event => {
        if ([' ', 'Enter', 'Delete'].includes(event.key)) {
          event.preventDefault();
          this.selectEdge(edge);
          if (event.key === 'Delete') this.callbacks.onRemoveEdge?.(edge);
        }
      });
      this.svg.append(wire);
    }
    this.selectEdge(this.selectedEdge);
    this.preview = svgElement('path', { class: 'graph-wire graph-wire-preview', hidden: '' });
    this.svg.append(this.preview);
    for (const bounds of nodes.values()) {
      if (bounds.node.kind === 'destination') this.renderDestination(bounds);
      else this.renderNode(bounds);
    }
    for (const row of this.layout.addRows) this.renderAddRow(row, laneWidth);
    if (!nodes.size && !sections.length) this.svg.append(svgElement('text', { class: 'graph-empty', x: 18, y: 66 }, t('graph.empty')));
    if (this.wireStart && nodes.has(this.wireStart)) this.markCompatible();
  }

  renderAddRow({ sectionId, y }, laneWidth) {
    for (const [lane, kinds] of addKinds.entries()) {
      const x = (lane + .5) * laneWidth;
      const label = t(lane === 5 ? 'graph.addDestination' : 'graph.addInLane', { lane: t(lanes[lane]) });
      const target = { sectionId, lane, kinds, point: { x: x - 13, y } };
      const control = svgElement('g', { class: 'graph-add', role: 'button', tabindex: 0,
        'aria-label': label, 'data-tooltip': label, 'data-add-lane': lane });
      control.append(
        svgElement('rect', { class: 'graph-add-hit', x: x - 23, y: y - 13, width: 43, height: 26 }),
        svgElement('circle', { class: 'graph-add-ring', cx: x, cy: y, r: 9 }),
        svgElement('path', { d: `M${x - 4} ${y}h8m-4 -4v8` }),
        svgElement('circle', { class: 'graph-add-dock', cx: target.point.x, cy: y, r: 3 })
      );
      this.addTargets.set(control, target);
      const activate = event => {
        event.stopPropagation();
        if (!this.suppressClick) this.addAt(target);
      };
      control.addEventListener('click', activate);
      control.addEventListener('keydown', event => {
        if (!['Enter', ' '].includes(event.key)) return;
        event.preventDefault();
        activate(event);
      });
      this.svg.append(control);
    }
  }

  canDropAt(target) {
    const origin = this.layout.nodes.get(this.wireStart);
    return origin && origin.sectionId === target.sectionId && origin.lane < target.lane;
  }

  addAt(target) {
    const from = this.wireStart;
    if ((from && !this.canDropAt(target)) || (!from && target.sectionId && target.lane !== 0)) {
      this.cancelWire();
      this.callbacks.onError?.(t('graph.addSelectOutput'));
      return;
    }
    this.cancelWire();
    Promise.resolve(this.callbacks.onAdd?.(target.kinds, from, target.sectionId))
      .catch(error => this.callbacks.onError?.(error));
  }

  renderSection(section, width) {
    const { id, y, name, kinds, closed } = section;
    const type = kinds.has('mouse') && kinds.has('keyboard') ? t('graph.device.mouseKeyboard')
      : kinds.has('mouse') ? t('ui.mouse') : kinds.has('keyboard') ? t('graph.device.keyboard') : t('graph.device.generic');
    const label = id ? (name ? name + ' · ' + type : type) : t('graph.device.other');
    const sectionElement = svgElement('g', { class: 'graph-section', 'aria-label': label });
    const index = this.layout.sections.indexOf(section);
    const focus = control => this.svg.querySelectorAll('.graph-section')[index]?.querySelector(`[data-section-control="${control}"]`)?.focus();
    const toggle = () => {
      if (closed) this.collapsedDevices.delete(id);
      else this.collapsedDevices.add(id);
      this.cancelWire();
      this.render();
      focus('collapse');
    };
    const setMode = async mode => {
      if (this.sectionModePending) return;
      this.sectionModePending = true;
      try {
        await this.callbacks.onSectionMode?.(section.sources.map(source => source.id), mode);
        focus(mode);
      } catch (error) { this.callbacks.onError?.(error); }
      finally { this.sectionModePending = false; }
    };
    sectionElement.append(
      svgElement('rect', { x: 0, y, width, height: sectionHeight }),
      ...sectionControls(svgElement, section, label, toggle, setMode),
      svgElement('text', { x: 108, y: y + 18 }, shortText(label, width - 120))
    );
    this.svg.append(sectionElement);
  }

  renderNode(bounds) {
    const { node, x, y, width, height } = bounds;
    const detail = (node.previewOnly ? t('graph.previewMode') + ' · ' : '') + this.detail(node);
    const group = svgElement('g', {
      class: `graph-node${this.selected === node.id ? ' graph-selected' : ''}${node.enabled === false ? ' graph-disabled' : ''}${node.previewOnly && node.enabled !== false ? ' graph-preview-only' : ''}`,
      tabindex: 0, role: 'button', 'aria-pressed': String(this.selected === node.id), 'aria-label': t('graph.nodeLabel', { name: node.name || t('graph.unnamed'), detail })
    });
    group.dataset.nodeId = node.id;
    group.dataset.nodeRole = node.kind;
    group.setAttribute('data-tooltip', `${node.name || t('graph.unnamed')}\n${detail}\n${node.enabled === false ? t('graph.disabled') + '\n' : ''}${t('graph.nodeHelp')}`);
    group.append(svgElement('rect', { class: 'graph-node-box', x, y, width, height }));
    const title = node.name || t('graph.unnamed');
    const available = width - 53;
    let visibleTitle = title;
    if (this.titleMeasure.measureText(title).width > available) {
      const characters = Array.from(title).slice(0, Math.ceil(available / 3));
      while (characters.length && this.titleMeasure.measureText(characters.join('') + '…').width > available)
        characters.pop();
      visibleTitle = characters.join('') + '…';
    }
    group.append(svgElement('text', { class: 'graph-node-title', x: x + 9, y: y + height / 2 - 5 }, visibleTitle));
    group.append(svgElement('text', { class: 'graph-node-detail', x: x + 9, y: y + height / 2 + 12 }, shortText(detail, width - 18, 6.2)));
    group.append(this.renderModeControls(bounds));
    group.addEventListener('click', event => { if (!event.target.closest('[data-port],.graph-mode')) this.select(node.id); });
    group.addEventListener('dblclick', event => { if (!event.target.closest('[data-port],.graph-mode')) { this.cancelWire(); this.callbacks.onEdit?.(node); } });
    group.addEventListener('keydown', event => {
      if (event.target !== group) return;
      if (event.key === 'Enter') { event.preventDefault(); this.select(node.id); this.callbacks.onEdit?.(node); }
      if (event.key === ' ') { event.preventDefault(); this.select(node.id); }
    });
    if (node.kind !== 'source') this.renderPorts(group, node, 'in', x, bounds.inputs, y + height / 2);
    if (node.kind !== 'action') this.renderPorts(group, node, 'out', x + width, bounds.outputs, y + height / 2);
    else for (const portY of bounds.outputs.values()) {
      group.append(svgElement('circle', { class: 'graph-delivery-dot', cx: x + width, cy: portY, r: 3 }));
    }
    this.nodeElements.set(node.id, group);
    this.svg.append(group);
  }

  renderModeControls({ node, x, y, width }) {
    const current = node.enabled === false ? 'off' : node.previewOnly ? 'test' : 'live';
    const controls = svgElement('g', { class: 'graph-mode-controls', 'data-mode': current });
    for (const [action, offset] of [['play', 39], ['pause', 21]]) {
      const enabled = action === 'play';
      const selected = enabled === (current !== 'off');
      const label = t('graph.nodeMode.' + action, { name: node.name || t('graph.unnamed') });
      const control = svgElement('g', {
        class: 'graph-mode', role: 'button', tabindex: 0, 'aria-label': label, 'aria-pressed': selected,
        'data-tooltip': label + ' · ' + t('graph.nodeMode.' + current), 'data-action': action
      });
      if (this.pendingModes.has(node.id)) control.setAttribute('aria-disabled', 'true');
      control.append(
        svgElement('rect', { x: x + width - offset, y: y + 3, width: 17, height: 21, rx: 2 }),
        svgElement('path', { d: modePaths[action], transform: `translate(${x + width - offset + 1.5} ${y + 6}) scale(.58)`, 'aria-hidden': 'true' })
      );
      const setMode = async event => {
        event.stopPropagation();
        if (selected || this.pendingModes.has(node.id)) return;
        this.pendingModes.add(node.id);
        for (const button of controls.children) button.setAttribute('aria-disabled', 'true');
        try {
          await this.callbacks.onMode?.(node.id, enabled);
          this.nodeElements.get(node.id)?.querySelector(`.graph-mode[data-action="${action}"]`)?.focus();
        } catch (error) { this.callbacks.onError?.(error); }
        finally {
          this.pendingModes.delete(node.id);
          for (const button of this.nodeElements.get(node.id)?.querySelectorAll('.graph-mode') ?? [])
            button.removeAttribute('aria-disabled');
        }
      };
      control.addEventListener('click', setMode);
      control.addEventListener('keydown', event => {
        if (!['Enter', ' '].includes(event.key)) return;
        event.preventDefault();
        setMode(event);
      });
      controls.append(control);
    }
    return controls;
  }

  renderDestination({ node, x, y, width, height, inputs }) {
    const group = svgElement('g', {
      class: `graph-destination${node.enabled === false ? ' graph-disabled' : ''}`,
      role: 'group', 'aria-label': node.name, 'data-tooltip': node.name + '\n' + t('graph.destination.help')
    });
    group.append(
      svgElement('rect', { class: 'graph-node-box', x, y, width, height }),
      svgElement('text', { class: 'graph-node-title', x: x + 9, y: y + height / 2 + 4 }, shortText(node.name, width - 18))
    );
    for (const portY of inputs.values()) {
      group.append(svgElement('circle', { class: 'graph-delivery-dot', cx: x, cy: portY, r: 3 }));
    }
    this.nodeElements.set(node.id, group);
    this.svg.append(group);
  }

  detail(node) {
    if (node.kind === 'event' && node.operation === 'sequence')
      return node.settings?.eventName || t('forms.operation.sequence');
    if (node.kind === 'event' && node.operation === 'chord')
      return t('forms.operation.inputChord') + ' · ' + node.settings.modifiers;
    if (node.kind === 'event' && node.operation === 'default')
      return t('forms.operation.default');
    if (node.kind === 'condition' && node.operation === 'modifierHeld')
      return t('forms.operation.modifierHeld') + ' · ' + t('forms.modifier.' + node.settings.modifier);
    if (node.kind === 'source') {
      const phase = node.settings?.phase;
      return [node.settings?.control || t('graph.anySignal'), phaseKeys[phase] ? t(phaseKeys[phase]) : phase || t('graph.anyPhase')].join(' · ');
    }
    if (node.kind === 'script') return 'C# · ' + portTypeLabel(node, 'in') + ' → ' + portTypeLabel(node, 'out');
    const connection = this.configuration.connections?.find(item => item.id === node.connectionId)?.name;
    const roleKey = `graph.role.${node.kind}`;
    const role = hasTranslation(roleKey) ? t(roleKey) : node.kind;
    const operation = operationNames[node.operation] ? t(operationNames[node.operation]) : node.operation;
    if (node.kind === 'action') return [operation || role, node.settings?.keys || node.settings?.key].filter(Boolean).join(' · ');
    return [connection || node.module, operation || role].filter(Boolean).join(' · ');
  }

  renderPorts(group, node, direction, x, slots, center) {
    const positions = slots.size ? [...slots.values()] : [center];
    positions.forEach((y, index) => {
      const before = y - (positions[index - 1] ?? -Infinity);
      const after = (positions[index + 1] ?? Infinity) - y;
      const radius = Math.min(10, before / 2, after / 2);
      group.append(this.port(node, direction, x, y, radius));
    });
  }

  port(node, direction, x, y, radius) {
    const label = t(direction === 'out' ? 'graph.output' : 'graph.input');
    const port = svgElement('g', { class: `graph-port graph-port-${direction}`, tabindex: 0, role: 'button', 'aria-label': t('graph.portLabel', { label, name: node.name || t('graph.unnamed'), type: portTypeLabel(node, direction) }) });
    port.dataset.port = direction; port.dataset.nodeId = node.id;
    port.setAttribute('data-tooltip', `${label}: ${portTypeLabel(node, direction)}. ${t(direction === 'out' ? 'graph.outputHelp' : 'graph.inputHelp')}`);
    port.append(svgElement('circle', { class: 'graph-port-hit', cx: x, cy: y, r: radius }));
    port.append(svgElement('circle', { class: 'graph-port-dot', cx: x, cy: y, r: Math.min(4, radius * .65) }));
    const startWire = () => {
      this.wireStart = node.id; this.wireOrigin = { x, y }; this.markCompatible();
    };
    port.addEventListener('pointerdown', event => {
      if (event.button !== 0) return;
      event.stopPropagation();
      if (direction === 'out') {
        startWire(); this.dragStart = { x: event.clientX, y: event.clientY }; this.dragging = false;
      }
    });
    port.addEventListener('click', event => {
      event.stopPropagation();
      if (this.suppressClick) { this.suppressClick = false; return; }
      if (direction === 'out') startWire();
      else this.completeWire(node.id);
    });
    port.addEventListener('keydown', event => {
      if (event.key !== 'Enter' && event.key !== ' ') return;
      event.preventDefault(); event.stopPropagation();
      if (direction === 'out') startWire();
      else this.completeWire(node.id);
    });
    return port;
  }

  markCompatible() {
    const from = this.configuration.nodes.find(node => node.id === this.wireStart);
    for (const [id, group] of this.nodeElements) {
      const target = this.configuration.nodes.find(node => node.id === id);
      group.classList.toggle('graph-compatible', compatiblePorts(from, target));
      group.classList.toggle('graph-wire-source', id === this.wireStart);
    }
    for (const [control, target] of this.addTargets) control.classList.toggle('graph-add-compatible', !!this.canDropAt(target));
  }

  pointerMove(event) {
    if (!this.wireStart) return;
    if (this.dragStart && Math.hypot(event.clientX - this.dragStart.x, event.clientY - this.dragStart.y) > 4) this.dragging = true;
    const start = this.wireOrigin;
    if (!start) return;
    const transform = this.svg.getScreenCTM();
    if (!transform) return;
    const target = this.addTargets.get(event.target.closest('.graph-add'));
    const point = target && this.canDropAt(target) ? target.point
      : new DOMPoint(event.clientX, event.clientY).matrixTransform(transform.inverse());
    this.preview.removeAttribute('hidden');
    this.preview.setAttribute('d', curvedWire([start, point]));
  }

  pointerUp(event) {
    if (!this.dragging) { this.dragStart = null; return; }
    const target = event.target.closest('[data-port="in"]');
    const addTarget = this.addTargets.get(event.target.closest('.graph-add'));
    this.suppressClick = true;
    if (target) this.completeWire(target.dataset.nodeId);
    else if (addTarget && this.canDropAt(addTarget)) this.addAt(addTarget);
    else this.cancelWire();
    // A drag ending on another element may not produce a click at all.
    setTimeout(() => { this.suppressClick = false; }, 0);
  }

  completeWire(targetId) {
    if (!this.wireStart) { this.callbacks.onError?.(t('graph.error.selectOutput')); return; }
    const from = this.configuration.nodes.find(node => node.id === this.wireStart);
    const to = this.configuration.nodes.find(node => node.id === targetId);
    const startId = this.wireStart;
    this.cancelWire();
    if (!compatiblePorts(from, to)) { this.callbacks.onError?.(t('graph.error.incompatible')); return; }
    if (this.configuration.edges.some(edge => edge.from === startId && edge.to === targetId)) { this.callbacks.onError?.(t('graph.error.duplicate')); return; }
    Promise.resolve(this.callbacks.onLink?.(startId, targetId, eventMissLink(from, to) ? 'miss' : 'match'))
      .catch(error => this.callbacks.onError?.(error.message || String(error)));
  }

  cancelWire() {
    this.wireStart = null; this.wireOrigin = null; this.dragStart = null; this.dragging = false;
    this.preview?.setAttribute('hidden', '');
    for (const group of this.nodeElements.values()) group.classList.remove('graph-compatible', 'graph-wire-source');
    for (const control of this.addTargets.keys()) control.classList.remove('graph-add-compatible');
  }

  flash(diagnostic) {
    const node = this.nodeElements.get(diagnostic.nodeId);
    const outcome = diagnostic.stage;
    if (node && (node.dataset.nodeRole !== 'action' || ['completed', 'error', 'suppressed'].includes(outcome))) {
      this.pulse(node, 'node:' + diagnostic.nodeId, outcome === 'blocked' || outcome === 'error', outcome === 'suppressed');
    }
    if (diagnostic.stage === 'routed') {
      const key = JSON.stringify([diagnostic.nodeId, diagnostic.nextNodeId]);
      const edge = this.edgeElements.get(key);
      if (edge) this.pulse(edge, 'edge:' + key, false);
    }
    if (outcome === 'completed') {
      const target = this.actionTargets.get(diagnostic.nodeId);
      const key = JSON.stringify([diagnostic.nodeId, target]);
      const edge = this.edgeElements.get(key), destination = this.nodeElements.get(target);
      if (edge) this.pulse(edge, 'edge:' + key, false);
      if (destination) this.pulse(destination, 'node:' + target, false);
    }
  }

  pulse(element, key, failed, suppressed = false) {
    clearTimeout(this.timers.get(key));
    element.classList.toggle('graph-active', !suppressed);
    element.classList.toggle('graph-failed', failed);
    element.classList.toggle('graph-suppressed', suppressed);
    this.timers.set(key, setTimeout(() => {
      element.classList.remove('graph-active', 'graph-failed', 'graph-suppressed'); this.timers.delete(key);
    }, 600));
  }

  dispose() {
    this.resize.disconnect();
    this.densityObserver.disconnect();
    this.unsubscribeLanguage?.();
    for (const timer of this.timers.values()) clearTimeout(timer);
    this.timers.clear(); this.container.replaceChildren();
  }
}
