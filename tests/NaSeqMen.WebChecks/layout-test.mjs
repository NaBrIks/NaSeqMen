// SPDX-License-Identifier: MIT
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
const { layoutGraph } = await import('../../src/NaSeqMen.Editor.Web/wwwroot/graph/layout.js');
const { directWireHitsBox } = await import('../../src/NaSeqMen.Editor.Web/wwwroot/graph/wire-path.js');
const { graphEn, graphRu } = await import('../../src/NaSeqMen.Editor.Web/wwwroot/graph-translations.js');
let checks = 0;
const check = (value, label) => { assert(value, label); checks++; console.log('PASS ' + label); };
const node = (id, kind, order = 0) => ({ id, name: id, kind, order });
const edge = (from, to) => ({ from, to });
const row = (layout, id) => layout.nodes.get(id).row;
const segments = points => points.slice(1).map((point, index) => [points[index], point]);
function checkGeometry(layout, label) {
  for (const route of layout.edges) for (const [a, b] of segments(route.points)) {
    for (const box of layout.nodes.values()) {
      const crosses = a.x !== b.x && a.y !== b.y
        ? directWireHitsBox(a, b, box)
        : a.y === b.y
        ? a.y > box.y && a.y < box.y + box.height && Math.max(a.x, b.x) > box.x + .01 && Math.min(a.x, b.x) < box.x + box.width - .01
        : a.x > box.x && a.x < box.x + box.width && Math.max(a.y, b.y) > box.y + .01 && Math.min(a.y, b.y) < box.y + box.height - .01;
      assert(!crosses, `${label}: ${route.edge.from} → ${route.edge.to} crosses ${box.node.id}`);
    }
  }
  for (let i = 0; i < layout.edges.length; i++) for (let j = i + 1; j < layout.edges.length; j++) {
    const a = layout.edges[i], b = layout.edges[j];
    if ([a.edge.from, a.edge.to].some(id => id === b.edge.from || id === b.edge.to)) continue;
    for (const [p, q] of segments(a.points)) for (const [r, s] of segments(b.points)) {
      if ((p.x !== q.x && p.y !== q.y) || (r.x !== s.x && r.y !== s.y)) continue;
      const h = p.y === q.y, otherH = r.y === s.y;
      if (h !== otherH || (h ? p.y !== r.y : p.x !== r.x)) continue;
      const overlap = h ? Math.min(Math.max(p.x, q.x), Math.max(r.x, s.x)) - Math.max(Math.min(p.x, q.x), Math.min(r.x, s.x)) : Math.min(Math.max(p.y, q.y), Math.max(r.y, s.y)) - Math.max(Math.min(p.y, q.y), Math.min(r.y, s.y));
      assert(overlap < .01, `${label}: unrelated edges overlap`);
    }
  }
  check(true, label + ': no block intersections or unrelated line overlaps');
}

export const ownerFixture = {
  nodes: [node('demo', 'source'), node('condition', 'condition'), node('scale', 'transform'), node('copy', 'action'), node('script-in', 'source', 1), node('script', 'script', 1), node('enter', 'action', 1), node('obs-info', 'source', 2), node('request', 'action', 2), node('obs-response', 'source', 3), node('text', 'transform', 3)],
  edges: [edge('demo', 'condition'), edge('condition', 'scale'), edge('scale', 'copy'), edge('script-in', 'script'), edge('script', 'enter'), edge('obs-info', 'request'), edge('obs-response', 'text')]
};
const owner = layoutGraph(ownerFixture);
check(['demo', 'condition', 'scale', 'copy'].every(id => row(owner, id) === 0), 'full four-stage chain is aligned');
check(['script-in', 'script', 'enter'].every(id => row(owner, id) === 1), 'skipped condition leaves its cell empty');
check(row(owner, 'obs-info') === 2 && row(owner, 'request') === 2, 'direct source to action stays on its own third row');
check(row(owner, 'obs-response') === 3 && row(owner, 'text') === 3, 'response transform aligns with fourth source');
check(owner.edges.every(route => route.points.length === 2 && route.points[0].y === route.points[1].y && / H/.test(route.path)), 'all seven owner links are straight without sag');
checkGeometry(owner, 'owner fixture');

export const branchFixture = {
  nodes: [node('source', 'source'), node('a', 'condition'), node('b', 'condition', 1), node('ta', 'transform'), node('tb', 'transform', 1), node('merge', 'action'), node('independent', 'source', 2), node('independent-out', 'action', 2)],
  edges: [edge('source', 'a'), edge('source', 'b'), edge('a', 'ta'), edge('b', 'tb'), edge('ta', 'merge'), edge('tb', 'merge'), edge('independent', 'independent-out')]
};
const branch = layoutGraph(branchFixture);
check(row(branch, 'a') === 0 && row(branch, 'ta') === 0 && row(branch, 'b') === 1 && row(branch, 'tb') === 1, 'fanout keeps each branch in a neighbouring row');
check(row(branch, 'merge') === 0 && row(branch, 'independent') > row(branch, 'tb'), 'merge is shared and independent chain stays outside the branch group');
checkGeometry(branch, 'fanout and merge');
const upper = branch.edges.find(({ edge }) => edge.from === 'source' && edge.to === 'a');
const lower = branch.edges.find(({ edge }) => edge.from === 'source' && edge.to === 'b');
check(upper.points[0].y === branch.nodes.get('source').outputs.get('a') &&
      lower.points[0].y === branch.nodes.get('source').outputs.get('b') &&
      lower.points[0].y - upper.points[0].y >= 10, 'branches start at separate distributed contacts');
check(/ H/.test(upper.path) && / C/.test(lower.path) && !/[LQ]/.test(lower.path),
      'aligned branch is straight and offset branch is a single cubic');
const obstacle = layoutGraph({
  nodes: [node('s', 'source'), node('c', 'condition'), node('a', 'action', 1)],
  edges: [edge('s', 'c'), edge('c', 'a'), edge('s', 'a')]
});
check(/ Q/.test(obstacle.edges.find(({ edge }) => edge.from === 's' && edge.to === 'a').path),
      'blocked direct connection detours around the intervening node');
checkGeometry(obstacle, 'blocked straight route');

const sameLane = layoutGraph({ nodes: [node('source', 'source'), node('a', 'condition'), node('b', 'condition'), node('c', 'transform'), node('d', 'script'), node('out', 'action')], edges: [edge('source', 'a'), edge('a', 'b'), edge('b', 'c'), edge('c', 'd'), edge('d', 'out')] });
check(row(sameLane, 'b') > row(sameLane, 'a') && row(sameLane, 'd') > row(sameLane, 'c'), 'consecutive same-stage nodes occupy separate rows');
checkGeometry(sameLane, 'same-lane route');
const mergedSources = layoutGraph({ nodes: [node('s1', 'source'), node('s2', 'source', 1), node('t', 'transform'), node('out', 'action')], edges: [edge('s1', 't'), edge('s2', 't'), edge('t', 'out')] });
check(row(mergedSources, 's1') !== row(mergedSources, 's2') && mergedSources.nodes.size === 4, 'two sources merge without duplicating descendants');
checkGeometry(mergedSources, 'merged sources');
check(layoutGraph(ownerFixture, 900, 8).nodes.get('demo').height > owner.nodes.get('demo').height && row(layoutGraph(ownerFixture, 900, 8), 'text') === 3, 'density changes size without changing chain membership');
check(layoutGraph({ nodes: [node('orphan', 'transform')], edges: [{ from: 'missing', to: 'orphan' }] }).edges.length === 0, 'missing endpoints are ignored defensively');
check(JSON.stringify(Object.keys(graphEn).sort()) === JSON.stringify(Object.keys(graphRu).sort()) && Object.entries(graphEn).every(([key, value]) => key.startsWith('graph.') && typeof value === 'string' && !/[А-Яа-яЁё]/.test(value)), 'graph catalogs have identical stable keys and canonical English values');
const source = readFileSync(new URL('../../src/NaSeqMen.Editor.Web/wwwroot/graph.js', import.meta.url), 'utf8');
check(!/[А-Яа-яЁё]/.test(source), 'graph behavior contains no hardcoded Russian display strings');
const five = layoutGraph({ nodes: [node('s', 'source'), node('e', 'event'), node('c', 'condition'), node('p', 'script'), node('a', 'action')], edges: [edge('s','e'),edge('e','c'),edge('c','p'),edge('p','a')] });
assert.deepEqual([...five.nodes.values()].map(item => [item.lane, item.row]), [[0,0],[1,0],[2,0],[3,0],[4,0]]);
checkGeometry(five, 'five-stage chain');
console.log(`PASS ${checks}/${checks} layout checks`);
