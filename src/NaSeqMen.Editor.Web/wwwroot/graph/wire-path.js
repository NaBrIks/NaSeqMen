// SPDX-License-Identifier: MIT

function towards(from, to, distance) {
  const length = Math.hypot(to.x - from.x, to.y - from.y);
  const fraction = Math.min(.4, distance / (length || 1));
  return { x: from.x + (to.x - from.x) * fraction, y: from.y + (to.y - from.y) * fraction };
}

const xy = point => `${point.x},${point.y}`;

export function wireY(start, end, t) {
  return start.y + (end.y - start.y) * t * t * (3 - 2 * t);
}

// The direct cubic moves at a constant rate along X and monotonically along Y.
// Its vertical extent inside a box therefore comes from the two horizontal bounds.
export function directWireHitsBox(start, end, box) {
  const left = Math.max(box.x, start.x), right = Math.min(box.x + box.width, end.x);
  if (right <= left) return false;
  const width = end.x - start.x;
  const first = wireY(start, end, (left - start.x) / width);
  const last = wireY(start, end, (right - start.x) / width);
  return Math.max(first, last) > box.y - 2 && Math.min(first, last) < box.y + box.height + 2;
}

export function curvedWire(points) {
  const start = points[0], end = points.at(-1);
  if (points.length === 2) {
    if (start.y === end.y) return `M${xy(start)} H${end.x}`;
    const third = (end.x - start.x) / 3;
    return `M${xy(start)} C${start.x + third},${start.y} ${end.x - third},${end.y} ${xy(end)}`;
  }
  let path = `M${xy(start)}`;
  for (let index = 1; index < points.length - 1; index++) {
    const corner = points[index];
    const before = towards(corner, points[index - 1], 10);
    const after = towards(corner, points[index + 1], 10);
    path += ` L${xy(before)} Q${xy(corner)} ${xy(after)}`;
  }
  return path + ` L${xy(end)}`;
}
