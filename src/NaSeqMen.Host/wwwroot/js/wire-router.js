/**
 * Smooth Blender-style Bezier wire routing & pastel wire coloring for NaSeqMen.
 */
const WireRouter = {
  // Soft, harmonious pastel palette readable on dark canvas (#181a1f)
  PASTEL_PALETTE: [
    "#93c5fd", // soft sky
    "#c4b5fd", // soft lavender
    "#86efac", // pastel mint
    "#fcd34d", // warm cream gold
    "#f9a8d4", // blush rose
    "#67e8f9", // soft cyan
    "#fdba74", // soft peach
    "#6ee7b7", // seafoam
    "#a5b4fc", // periwinkle
    "#fca5a5", // soft coral
    "#bef264", // soft sage
    "#d8b4fe", // soft lilac
  ],

  hashString(str) {
    let h = 2166136261;
    for (let i = 0; i < str.length; i++) {
      h ^= str.charCodeAt(i);
      h = Math.imul(h, 16777619);
    }
    return h >>> 0;
  },

  computeNodeLevels(flow) {
    const levels = new Map();
    if (!flow || !flow.nodes.length) return levels;

    const nodeById = new Map(flow.nodes.map((n) => [n.id, n]));
    const inDeg = new Map(flow.nodes.map((n) => [n.id, 0]));
    const adj = new Map(flow.nodes.map((n) => [n.id, []]));

    for (const e of flow.edges) {
      const a = nodeById.get(e.fromNodeId);
      const b = nodeById.get(e.toNodeId);
      if (!a || !b || a.id === b.id) continue;
      if (b.x > a.x + 20) {
        adj.get(a.id).push(b.id);
        inDeg.set(b.id, (inDeg.get(b.id) || 0) + 1);
      }
    }

    const queue = [];
    for (const [id, deg] of inDeg) {
      if (deg === 0) {
        levels.set(id, 0);
        queue.push(id);
      }
    }

    while (queue.length > 0) {
      const cur = queue.shift();
      const curLvl = levels.get(cur) || 0;
      for (const next of adj.get(cur) || []) {
        const nextLvl = Math.max(levels.get(next) || 0, curLvl + 1);
        levels.set(next, nextLvl);
        inDeg.set(next, (inDeg.get(next) || 1) - 1);
        if (inDeg.get(next) <= 0) queue.push(next);
      }
    }

    for (const n of flow.nodes) {
      if (!levels.has(n.id)) levels.set(n.id, 0);
    }
    return levels;
  },

  getEdgeColors(edge, fromPortType, toPortType, colorMode, nodeLevels, isBackward, randomSeed) {
    if (colorMode === "random") {
      const idx = (this.hashString(edge.id) + randomSeed * 7) % this.PASTEL_PALETTE.length;
      const c = this.PASTEL_PALETTE[idx];
      return { c1: c, c2: c };
    }

    if (colorMode === "level") {
      if (isBackward) {
        return { c1: "#fdba74", c2: "#f9a8d4" };
      }
      const lvl = nodeLevels.get(edge.fromNodeId) || 0;
      const c = this.PASTEL_PALETTE[lvl % this.PASTEL_PALETTE.length];
      return { c1: c, c2: c };
    }

    return {
      c1: getPortColor(fromPortType),
      c2: getPortColor(toPortType),
    };
  },

  /**
   * Builds a smooth cubic Bezier path (`d`) and midpoint `{mx, my}`.
   * Forward edges use a clean single Bezier; backward loop-back edges arc smoothly
   * over the top or under the bottom and enter the target port horizontally from the left.
   */
  buildPath(p1, p2, options) {
    const {
      rects = [],
      fromNodeId = null,
      toNodeId = null,
      laneIndex = 0,
    } = options || {};

    const isBackward = p2.x <= p1.x + 28;
    if (!isBackward) {
      const dist = Math.max(50, Math.min(220, Math.abs(p2.x - p1.x) * 0.5));
      return {
        d: `M ${p1.x} ${p1.y} C ${p1.x + dist} ${p1.y}, ${p2.x - dist} ${p2.y}, ${p2.x} ${p2.y}`,
        mx: (p1.x + p2.x) / 2,
        my: (p1.y + p2.y) / 2,
      };
    }

    const fromRect = rects.find((r) => r.id === fromNodeId) || { left: p1.x - 180, right: p1.x, top: p1.y - 30, bottom: p1.y + 60 };
    const toRect = rects.find((r) => r.id === toNodeId) || { left: p2.x, right: p2.x + 180, top: p2.y - 30, bottom: p2.y + 60 };

    const spanLeft = Math.min(p2.x, fromRect.left, toRect.left) - 20;
    const spanRight = Math.max(p1.x, fromRect.right, toRect.right) + 20;
    const inSpan = rects.filter((r) => r.right > spanLeft && r.left < spanRight);

    const clearance = 38 + laneIndex * 18;
    const topY = Math.min(...inSpan.map((r) => r.top), p1.y, p2.y) - clearance;
    const botY = Math.max(...inSpan.map((r) => r.bottom), p1.y, p2.y) + clearance;

    // Pick whichever smooth arc (over top vs under bottom) is closer to the endpoints
    const costTop = Math.abs(p1.y - topY) + Math.abs(p2.y - topY);
    const costBot = Math.abs(p1.y - botY) + Math.abs(p2.y - botY);
    const arcY = costTop <= costBot ? topY : botY;

    const midX = (p1.x + p2.x) / 2;
    const stubOut = 75 + laneIndex * 14;
    const stubIn = 75 + laneIndex * 14;

    // Control points push smoothly outside source-right and target-left so the curve
    // arcs cleanly around corners and enters p2 horizontally from the left.
    const cp1x = p1.x + stubOut;
    const cp2x = Math.max(midX + 60, p1.x + stubOut * 0.85);
    const cp3x = Math.min(midX - 60, p2.x - stubIn * 0.85);
    const cp4x = p2.x - stubIn;

    const d =
      `M ${p1.x} ${p1.y} ` +
      `C ${cp1x} ${p1.y}, ${cp2x} ${arcY}, ${midX} ${arcY} ` +
      `C ${cp3x} ${arcY}, ${cp4x} ${p2.y}, ${p2.x} ${p2.y}`;

    return { d, mx: midX, my: arcY };
  },
};
