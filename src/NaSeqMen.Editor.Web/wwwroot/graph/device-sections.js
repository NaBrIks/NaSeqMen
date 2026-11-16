// SPDX-License-Identifier: MIT
import { layoutGraph } from './layout.js';

export const sectionHeight = 27;
export const graphHeaderHeight = 34;
const addRowHeight = 32;

function groupGraph(configuration, devices) {
  const deviceByPath = new Map(devices.map(device => [device.id.toLowerCase(), device]));
  const byId = new Map(configuration.nodes.map(node => [node.id, node]));
  const adjacent = new Map(configuration.nodes.map(node => [node.id, []]));
  const outgoing = new Map(configuration.nodes.map(node => [node.id, []]));
  for (const edge of configuration.edges) {
    if (byId.has(edge.from) && byId.has(edge.to)) {
      adjacent.get(edge.from).push(edge.to);
      adjacent.get(edge.to).push(edge.from);
      outgoing.get(edge.from).push(edge);
    }
  }
  const groups = new Map();
  const seen = new Set();
  for (const start of byId.keys()) {
    if (seen.has(start)) continue;
    const stack = [start], component = [];
    while (stack.length) {
      const id = stack.pop();
      if (seen.has(id)) continue;
      seen.add(id);
      component.push(byId.get(id));
      stack.push(...adjacent.get(id));
    }
    const sources = component.filter(node => node.kind === 'source');
    const ids = new Set(sources.map(node => node.module === 'windows.input'
      ? deviceByPath.get(node.settings?.source?.toLowerCase())?.physicalDeviceId : undefined));
    const id = ids.size === 1 ? [...ids][0] : undefined;
    if (!groups.has(id)) groups.set(id, { nodes: [], edges: [] });
    const group = groups.get(id);
    group.nodes.push(...component);
    for (const node of component) group.edges.push(...outgoing.get(node.id));
  }
  return groups;
}

export function layoutDeviceSections(configuration, devices, collapsed, width, cellPadding, laneCount) {
  if (!devices.some(device => device.physicalDeviceId)) {
    return flatLayout();
  }
  const groups = groupGraph(configuration, devices);
  if (![...groups.keys()].some(Boolean)) {
    return flatLayout();
  }
  const nodes = new Map(), edges = [], sections = [], addRows = [];
  let y = graphHeaderHeight;
  const others = groups.get(undefined);
  groups.delete(undefined);
  if (others) groups.set(undefined, others);
  for (const [id, group] of groups) {
    const interfaces = devices.filter(device => device.physicalDeviceId === id);
    const closed = collapsed.has(id);
    sections.push({
      id, y, closed, sources: group.nodes.filter(node => node.kind === 'source'),
      name: id ? interfaces.find(device => device.physicalDeviceName)?.physicalDeviceName : undefined,
      kinds: new Set(interfaces.map(device => device.kind))
    });
    y += sectionHeight;
    if (closed) continue;
    const layout = layoutGraph(group, width, cellPadding, 0, laneCount);
    const offset = y - graphHeaderHeight;
    for (const [nodeId, node] of layout.nodes) {
      const translateSlots = slots => new Map([...slots].map(([peer, value]) => [peer, value + offset]));
      nodes.set(nodeId, { ...node, sectionId: id, y: node.y + offset, inputs: translateSlots(node.inputs), outputs: translateSlots(node.outputs) });
    }
    for (const route of layout.edges) edges.push({ ...route, offset });
    y += layout.height - graphHeaderHeight;
    addRows.push({ sectionId: id, y: y + addRowHeight / 2 });
    y += addRowHeight;
  }
  width = Math.max(840, Number(width) || 1000);
  return { width, height: Math.max(95, y), laneWidth: width / laneCount, nodes, edges, sections, addRows };

  function flatLayout() {
    const layout = layoutGraph(configuration, width, cellPadding, 208, laneCount);
    return { ...layout, height: layout.height + addRowHeight, sections: [], addRows: [{ y: layout.height + addRowHeight / 2 }] };
  }
}
