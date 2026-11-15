// SPDX-License-Identifier: MIT
import { curvedWire, directWireHitsBox, wireY } from './wire-path.js';
const kinds = { source: 0, event: 1, condition: 2, transform: 3, script: 3, action: 4, destination: 5 };

export function layoutGraph(configuration, width = 1000, cellPadding = 3, minimumHeight = 240, laneCount = 5) {
  width = Math.max(840, Number(width) || 1000);
  const laneWidth = width / laneCount;
  const nodeHeight = 40 + Math.min(8, Math.max(3, cellPadding)) * 2;
  const rowHeight = nodeHeight + 26;
  const ordered = [...(configuration.nodes || [])].sort((a, b) => (a.order || 0) - (b.order || 0) || String(a.id).localeCompare(String(b.id)));
  const byId = new Map(ordered.map(node => [node.id, node]));
  const edges = (configuration.edges || []).filter(edge => byId.has(edge.from) && byId.has(edge.to));
  const outgoing = new Map(ordered.map(node => [node.id, []]));
  const incoming = new Map(ordered.map(node => [node.id, []]));
  const neighbours = new Map(ordered.map(node => [node.id, []]));
  const rank = new Map(ordered.map((node, index) => [node.id, index]));
  for (const edge of edges) {
    outgoing.get(edge.from).push(edge.to); incoming.get(edge.to).push(edge.from);
    neighbours.get(edge.from).push(edge.to); neighbours.get(edge.to).push(edge.from);
  }
  for (const list of outgoing.values()) list.sort((a, b) => rank.get(a) - rank.get(b));
  const nodes = new Map(), visited = new Set();
  let nextRow = 0;
  // Keep complete independent chains together; never pack unrelated nodes by column.
  const starts = [...ordered.filter(node => node.kind === 'source'), ...ordered.filter(node => node.kind !== 'source')];
  for (const start of starts) {
    if (visited.has(start.id)) continue;
    const component = new Set(), stack = [start.id];
    while (stack.length) {
      const id = stack.pop();
      if (component.has(id)) continue;
      component.add(id); visited.add(id); stack.push(...neighbours.get(id));
    }
    const occupied = new Set();
    const place = (id, preferredRow) => {
      if (nodes.has(id)) return;
      const node = byId.get(id), lane = kinds[node.kind] ?? 3;
      let row = preferredRow;
      if (occupied.has(`${row}:${lane}`)) row = nextRow;
      nextRow = Math.max(nextRow, row + 1); occupied.add(`${row}:${lane}`);
      nodes.set(id, { node, lane, row, chain: start.id, x: lane * laneWidth + 18, y: 46 + row * rowHeight, width: laneWidth - 36, height: nodeHeight });
      let first = true;
      for (const child of outgoing.get(id)) {
        if (nodes.has(child)) continue; // A merge belongs to the first route reaching it.
        place(child, first ? row : nextRow); first = false;
      }
    };
    const roots = ordered.filter(node => component.has(node.id) && (node.kind === 'source' || !incoming.get(node.id).length));
    for (const root of roots) place(root.id, nextRow);
    // Defensive support for malformed/cyclic input; server validation still owns validity.
    for (const node of ordered) if (component.has(node.id) && !nodes.has(node.id)) place(node.id, nextRow);
  }
  const compact = points => points.filter((point, index) => {
    if (index && point.x === points[index - 1].x && point.y === points[index - 1].y) return false;
    if (!index || index === points.length - 1) return true;
    const previous = points[index - 1], next = points[index + 1];
    return !(previous.x === point.x && point.x === next.x || previous.y === point.y && point.y === next.y);
  });
  const boxes = [...nodes.values()];
  const segments = points => points.slice(1).map((point, index) => [points[index], point]);
  const hitsBox = points => segments(points).some(([a, b]) => boxes.some(box => {
    // Strict interior: touching the named endpoint is permitted, travelling through it is not.
    if (a.y === b.y) return a.y > box.y && a.y < box.y + box.height && Math.max(a.x, b.x) > box.x && Math.min(a.x, b.x) < box.x + box.width;
    return a.x > box.x && a.x < box.x + box.width && Math.max(a.y, b.y) > box.y && Math.min(a.y, b.y) < box.y + box.height;
  }));
  const crossingCost = (points, previous) => {
    let cost = 0;
    for (const [a, b] of segments(points)) for (const route of previous) for (const [c, d] of route.segments) {
      if (c.x !== d.x && c.y !== d.y) {
        const cross = (p, q, r) => (q.x - p.x) * (r.y - p.y) - (q.y - p.y) * (r.x - p.x);
        if (cross(a, b, c) * cross(a, b, d) < 0 && cross(c, d, a) * cross(c, d, b) < 0) cost += 300;
        continue;
      }
      const horizontal = a.y === b.y, otherHorizontal = c.y === d.y;
      if (horizontal === otherHorizontal) {
        const sameLine = horizontal ? a.y === c.y : a.x === c.x;
        const overlap = horizontal ? Math.min(Math.max(a.x, b.x), Math.max(c.x, d.x)) - Math.max(Math.min(a.x, b.x), Math.min(c.x, d.x)) : Math.min(Math.max(a.y, b.y), Math.max(c.y, d.y)) - Math.max(Math.min(a.y, b.y), Math.min(c.y, d.y));
        if (sameLine && overlap > .01) cost += 10000 + overlap;
      } else {
        const h = horizontal ? [a, b] : [c, d], v = horizontal ? [c, d] : [a, b];
        if (v[0].x > Math.min(h[0].x, h[1].x) && v[0].x < Math.max(h[0].x, h[1].x) && h[0].y > Math.min(v[0].y, v[1].y) && h[0].y < Math.max(v[0].y, v[1].y)) cost += 300;
      }
    }
    return cost;
  };
  const routed = [];
  function slots(node, neighbours) {
    const center = node.y + node.height / 2;
    const ordered = [...neighbours].sort((a, b) =>
      nodes.get(a).row - nodes.get(b).row || nodes.get(a).lane - nodes.get(b).lane || rank.get(a) - rank.get(b));
    const spacing = Math.min(13, (node.height - 14) / Math.max(1, ordered.length - 1));
    return new Map(ordered.map((id, index) => [id, center + (index - (ordered.length - 1) / 2) * spacing]));
  }
  for (const node of boxes) {
    node.inputs = slots(node, incoming.get(node.node.id));
    node.outputs = slots(node, outgoing.get(node.node.id));
  }
  // Reserve aligned routes first, so detours can avoid them instead of obscuring the main chains.
  const edgeOrder = [...edges].sort((a, b) => Number(nodes.get(b.from).row === nodes.get(b.to).row && nodes.get(b.from).lane < nodes.get(b.to).lane) - Number(nodes.get(a.from).row === nodes.get(a.to).row && nodes.get(a.from).lane < nodes.get(a.to).lane));
  for (const [index, edge] of edgeOrder.entries()) {
    const from = nodes.get(edge.from), to = nodes.get(edge.to);
    if (from.row === to.row && from.lane < to.lane) {
      if (from.outputs.size === 1) from.outputs.set(edge.to, to.inputs.get(edge.from));
      else if (to.inputs.size === 1) to.inputs.set(edge.from, from.outputs.get(edge.to));
    }
    const source = { x: from.x + from.width, y: from.outputs.get(edge.to) };
    const target = { x: to.x, y: to.inputs.get(edge.from) };
    let points = [source, target];
    const direct = source.x < target.x && !boxes.some(box => directWireHitsBox(source, target, box));
    if (!direct) {
      // Unique gutter offsets and local row-gap tracks prevent unrelated paths from sharing a line.
      const offset = 6 + 8 * (index + 1) / (edges.length + 1);
      const exit = source.x + offset, entry = target.x - offset;
      const chainRows = [...nodes.values()].filter(node => node.chain === from.chain).map(node => node.row);
      const firstRow = Math.min(...chainRows), lastRow = Math.max(...chainRows);
      const candidates = [];
      // A short elbow can stay entirely inside one column gap.
      candidates.push([source, { x: exit, y: source.y }, { x: exit, y: target.y }, target]);
      candidates.push([source, { x: entry, y: source.y }, { x: entry, y: target.y }, target]);
      for (let row = firstRow - 1; row <= lastRow; row++) {
        const channel = row < 0 ? 38 + (index + 1) / (edges.length + 1) * 4 : 46 + row * rowHeight + nodeHeight + 6 + (index + 1) / (edges.length + 1) * 10;
        candidates.push([source, { x: exit, y: source.y }, { x: exit, y: channel }, { x: entry, y: channel }, { x: entry, y: target.y }, target]);
      }
      let best = null, bestCost = Infinity;
      for (const candidate of candidates.map(compact)) {
        if (hitsBox(candidate)) continue;
        const length = segments(candidate).reduce((sum, [a, b]) => sum + Math.abs(a.x - b.x) + Math.abs(a.y - b.y), 0);
        const cost = length + candidate.length * 8 + crossingCost(candidate, routed);
        if (cost < bestCost) { best = candidate; bestCost = cost; }
      }
      points = best || candidates.at(-1);
    }
    const path = curvedWire(points);
    const trace = direct && source.y !== target.y
      ? Array.from({ length: 9 }, (_, i) => ({
        x: source.x + (target.x - source.x) * i / 8, y: wireY(source, target, i / 8)
      }))
      : points;
    routed.push({ edge, path, points, segments: segments(trace) });
  }
  return { width, height: Math.max(minimumHeight, 46 + Math.max(1, nextRow) * rowHeight + 12), laneWidth, nodes, edges: routed };
}

