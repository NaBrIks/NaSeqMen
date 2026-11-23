const PORT_COLORS = {
  any: "#9ca3af",
  event: "#34d399",
  string: "#facc15",
  number: "#60a5fa",
  boolean: "#f472b6",
  object: "#a78bfa",
  binary: "#fb923c",
};

function getPortColor(dataType) {
  return PORT_COLORS[(dataType || "any").toLowerCase()] || PORT_COLORS.any;
}

function isPortCompatible(sourceType, targetType) {
  const s = (sourceType || "any").toLowerCase();
  const tgt = (targetType || "any").toLowerCase();
  if (s === tgt || s === "any" || tgt === "any" || tgt === "event") return true;
  return false;
}

class NodeCanvas {
  constructor(callbacks) {
    this.cb = callbacks;
    this.container = document.getElementById("canvas-container");
    this.gridEl = document.getElementById("canvas-grid");
    this.viewport = document.getElementById("canvas-viewport");
    this.edgesDefs = document.getElementById("edges-defs");
    this.edgesGroup = document.getElementById("edges-group");
    this.tempWire = document.getElementById("temp-wire");
    this.nodesLayer = document.getElementById("nodes-layer");
    this.marqueeEl = document.getElementById("marquee-box");
    this.zoomLabel = document.getElementById("zoom-label");

    this.panX = 40;
    this.panY = 40;
    this.scale = 1;

    this.flow = null;
    this.catalogMap = new Map();
    this.selectedNodeIds = new Set();
    this.selectedEdgeId = null;

    this.dragState = null;
    this.resizeState = null;
    this.wireState = null;
    this.panState = null;
    this.marqueeState = null;

    this._bindEvents();
    this._applyTransform();
  }

  setCatalog(descriptors) {
    this.catalogMap.clear();
    descriptors.forEach((d) => this.catalogMap.set(d.typeId, d));
  }

  setFlow(flow) {
    this.flow = flow;
    this.selectedNodeIds.clear();
    this.selectedEdgeId = null;
    this.render();
    this.cb.onSelectionChange(null);
  }

  screenToWorld(clientX, clientY) {
    const rect = this.container.getBoundingClientRect();
    return {
      x: (clientX - rect.left - this.panX) / this.scale,
      y: (clientY - rect.top - this.panY) / this.scale,
    };
  }

  _applyTransform() {
    this.viewport.style.transform = `translate(${this.panX}px, ${this.panY}px) scale(${this.scale})`;
    const gridSize = 24 * this.scale;
    this.gridEl.style.backgroundSize = `${gridSize}px ${gridSize}px`;
    this.gridEl.style.backgroundPosition = `${this.panX}px ${this.panY}px`;
    this.zoomLabel.textContent = `${Math.round(this.scale * 100)}%`;
  }

  zoomBy(factor, centerX = null, centerY = null) {
    const rect = this.container.getBoundingClientRect();
    const cx = centerX !== null ? centerX - rect.left : rect.width / 2;
    const cy = centerY !== null ? centerY - rect.top : rect.height / 2;

    const newScale = Math.min(2.2, Math.max(0.3, this.scale * factor));
    const ratio = newScale / this.scale;
    this.panX = cx - (cx - this.panX) * ratio;
    this.panY = cy - (cy - this.panY) * ratio;
    this.scale = newScale;
    this._applyTransform();
  }

  fitView() {
    if (!this.flow || this.flow.nodes.length === 0) {
      this.panX = 40;
      this.panY = 40;
      this.scale = 1;
      this._applyTransform();
      return;
    }

    let minX = Infinity, minY = Infinity, maxX = -Infinity, maxY = -Infinity;
    for (const n of this.flow.nodes) {
      const el = this.nodesLayer.querySelector(`[data-node-id="${n.id}"]`);
      const w = el ? el.offsetWidth : 220;
      const h = el ? el.offsetHeight : 140;
      minX = Math.min(minX, n.x);
      minY = Math.min(minY, n.y);
      maxX = Math.max(maxX, n.x + w);
      maxY = Math.max(maxY, n.y + h);
    }

    const rect = this.container.getBoundingClientRect();
    const pad = 70;
    const contentW = Math.max(100, maxX - minX);
    const contentH = Math.max(100, maxY - minY);
    this.scale = Math.min(1.15, Math.max(0.35, Math.min((rect.width - pad * 2) / contentW, (rect.height - pad * 2) / contentH)));
    this.panX = (rect.width - contentW * this.scale) / 2 - minX * this.scale;
    this.panY = (rect.height - contentH * this.scale) / 2 - minY * this.scale;
    this._applyTransform();
  }

  render() {
    this._renderNodes();
    this._renderEdges();
  }

  _renderNodes() {
    this.nodesLayer.innerHTML = "";
    if (!this.flow) return;

    for (const node of this.flow.nodes) {
      const desc = this.catalogMap.get(node.typeId);
      const card = this._createNodeElement(node, desc);
      this.nodesLayer.appendChild(card);
    }
  }

  _createNodeElement(node, desc) {
    const el = document.createElement("div");
    el.className = "node-card";
    if (this.selectedNodeIds.has(node.id)) el.classList.add("selected");
    if (node.collapsed) el.classList.add("collapsed");
    if (!node.enabled) el.classList.add("disabled");

    const hasIn = (desc?.inputs?.length || 0) > 0;
    const hasOut = (desc?.outputs?.length || 0) > 0;
    if (hasIn && hasOut) el.classList.add("two-col-ports");
    if (node.width && node.width >= 120) {
      el.classList.add("has-custom-width");
      el.style.width = `${Math.round(node.width)}px`;
    }

    el.dataset.nodeId = node.id;
    el.style.left = `${node.x}px`;
    el.style.top = `${node.y}px`;
    el.style.setProperty("--node-color", desc?.color || "#4b5563");

    // Left-side trigger button (Node-RED style)
    if (desc?.supportsManualTrigger) {
      const trigBtn = document.createElement("button");
      trigBtn.className = "node-trigger-btn";
      trigBtn.title = t("inspector.trigger");
      trigBtn.textContent = "▶";
      trigBtn.addEventListener("mousedown", (e) => e.stopPropagation());
      trigBtn.addEventListener("click", (e) => {
        e.stopPropagation();
        this.cb.onTriggerNode(node.id);
      });
      el.appendChild(trigBtn);
    }

    // Header
    const header = document.createElement("div");
    header.className = "node-header";

    const collapseBtn = document.createElement("button");
    collapseBtn.className = "node-collapse-btn";
    collapseBtn.textContent = node.collapsed ? "▸" : "▾";
    collapseBtn.addEventListener("mousedown", (e) => e.stopPropagation());
    collapseBtn.addEventListener("click", (e) => {
      e.stopPropagation();
      node.collapsed = !node.collapsed;
      this.cb.onDirty();
      this.render();
    });

    const titleSpan = document.createElement("span");
    titleSpan.className = "node-title";
    titleSpan.textContent = node.title || desc?.displayName || node.typeId;

    header.append(collapseBtn, titleSpan);
    el.appendChild(header);

    // Body
    const body = document.createElement("div");
    body.className = "node-body";

    // Ports: side-by-side 2-column grid (inputs left, outputs right) to save vertical space
    if (hasIn || hasOut) {
      const portsGrid = document.createElement("div");
      portsGrid.className = `ports-grid ${hasIn && hasOut ? "two-col" : "one-col"}`;

      if (hasIn) {
        const inCol = document.createElement("div");
        inCol.className = "ports-col ports-col-in";
        for (const port of desc.inputs) {
          inCol.appendChild(this._createPortRow(node.id, port, "input"));
        }
        portsGrid.appendChild(inCol);
      }

      if (hasOut) {
        const outCol = document.createElement("div");
        outCol.className = "ports-col ports-col-out";
        for (const port of desc.outputs) {
          outCol.appendChild(this._createPortRow(node.id, port, "output"));
        }
        portsGrid.appendChild(outCol);
      }

      body.appendChild(portsGrid);
    }

    // Interactive widget (Button, Slider, VU Meter, OBS/Interception status)
    if (typeof NodeWidgets !== "undefined" && desc?.widgetKind) {
      const widgetEl = NodeWidgets.create(node, desc, (nid, action, data) => {
        this.cb.onInteractNode(nid, action, data);
      });
      if (widgetEl) {
        el.classList.add("has-widget");
        body.appendChild(widgetEl);
      }
    }

    // Inline Parameters (Blender style; OBS keeps its config strictly in Inspector)
    const inlineParams = desc?.widgetKind === "obs-status"
      ? []
      : (desc?.parameters || []).filter((p) => p.inline);
    if (inlineParams.length > 0) {
      const paramsWrap = document.createElement("div");
      paramsWrap.className = "node-params";
      paramsWrap.addEventListener("mousedown", (e) => e.stopPropagation());

      for (const p of inlineParams) {
        paramsWrap.appendChild(this._createInlineParam(node, p));
      }
      body.appendChild(paramsWrap);
    }

    // Live status footer
    const statusBar = document.createElement("div");
    statusBar.className = "node-status-bar";
    statusBar.dataset.statusFor = node.id;
    statusBar.style.display = "none";
    statusBar.innerHTML = `<span class="node-status-dot"></span><span class="node-status-text"></span>`;
    body.appendChild(statusBar);

    el.appendChild(body);

    // Right-edge width resize handle (drag to resize, double-click to reset to auto)
    const resizeHandle = document.createElement("div");
    resizeHandle.className = "node-resize-handle";
    resizeHandle.title = "Drag to resize width · Double-click to reset";
    resizeHandle.addEventListener("mousedown", (e) => {
      if (e.button !== 0) return;
      e.stopPropagation();
      el.classList.add("resizing");
      this.resizeState = {
        node,
        card: el,
        startX: e.clientX,
        startWidth: el.offsetWidth,
        moved: false,
      };
    });
    resizeHandle.addEventListener("dblclick", (e) => {
      e.stopPropagation();
      node.width = null;
      el.classList.remove("has-custom-width");
      el.style.width = "";
      this.cb.onDirty();
      this._renderEdges();
    });
    el.appendChild(resizeHandle);

    // Node selection & drag
    el.addEventListener("mousedown", (e) => {
      if (e.button !== 0) return;
      e.stopPropagation();
      this._handleNodeMouseDown(e, node);
    });

    el.addEventListener("contextmenu", (e) => {
      e.preventDefault();
      e.stopPropagation();
      if (!this.selectedNodeIds.has(node.id)) {
        this.selectedNodeIds.clear();
        this.selectedNodeIds.add(node.id);
        this.selectedEdgeId = null;
        this._syncSelectionClasses();
        this.cb.onSelectionChange(node);
      }
      this.cb.onNodeContextMenu(e.clientX, e.clientY, node);
    });

    return el;
  }

  _createPortRow(nodeId, port, dir) {
    const row = document.createElement("div");
    row.className = `port-row ${dir}`;

    const socket = document.createElement("div");
    socket.className = "port-socket";
    socket.style.setProperty("--port-color", getPortColor(port.dataType));
    socket.dataset.nodeId = nodeId;
    socket.dataset.portId = port.id;
    socket.dataset.dir = dir;
    socket.dataset.dataType = port.dataType;
    socket.title = `${port.label} (${port.dataType})`;

    socket.addEventListener("mousedown", (e) => {
      if (e.button !== 0) return;
      e.stopPropagation();
      this._startWireDrag(e, nodeId, port.id, dir, port.dataType);
    });

    const label = document.createElement("span");
    label.className = "port-label";
    label.textContent = port.label;

    const tag = document.createElement("span");
    tag.className = "port-type-tag";
    tag.textContent = port.dataType;

    if (dir === "input") {
      row.append(socket, label, tag);
    } else {
      row.append(tag, label, socket);
    }

    return row;
  }

  _createInlineParam(node, param) {
    const wrap = document.createElement("div");
    wrap.className = "inline-param";
    const val = node.parameters?.[param.key] ?? param.defaultValue ?? "";

    const lbl = document.createElement("label");
    lbl.textContent = param.label;
    lbl.title = param.description || param.label;

    if (param.kind === "boolean") {
      const inp = document.createElement("input");
      inp.type = "checkbox";
      inp.checked = Boolean(val);
      inp.addEventListener("change", () => {
        node.parameters[param.key] = inp.checked;
        this.cb.onDirty();
        this.cb.onSelectionChange(this._getSingleSelectedNode());
      });
      wrap.append(inp, lbl);
    } else if (param.kind === "select") {
      const sel = document.createElement("select");
      for (const opt of param.options || []) {
        const o = document.createElement("option");
        o.value = opt.value;
        o.textContent = opt.label;
        if (String(val) === String(opt.value)) o.selected = true;
        sel.appendChild(o);
      }
      sel.addEventListener("change", () => {
        node.parameters[param.key] = sel.value;
        this.cb.onDirty();
      });
      wrap.append(lbl, sel);
    } else if ((param.kind === "key" || param.kind === "keyCombo") && typeof KeyPicker !== "undefined") {
      const ctrl = KeyPicker.createInlineControl(val, param.placeholder, param.kind, (nextVal) => {
        node.parameters[param.key] = nextVal;
        this.cb.onDirty();
      });
      wrap.append(lbl, ctrl);
    } else {
      const inp = document.createElement("input");
      inp.type = param.kind === "number" ? "number" : "text";
      inp.value = val;
      if (param.placeholder) inp.placeholder = param.placeholder;
      inp.addEventListener("input", () => {
        node.parameters[param.key] = param.kind === "number" ? Number(inp.value) : inp.value;
        this.cb.onDirty();
      });
      wrap.append(lbl, inp);
    }

    return wrap;
  }

  _getSocketWorldPos(nodeId, portId, dir) {
    const card = this.nodesLayer.querySelector(`[data-node-id="${nodeId}"]`);
    if (!card) return null;

    const node = this.flow.nodes.find((n) => n.id === nodeId);
    if (!node) return null;

    if (node.collapsed) {
      return {
        x: dir === "output" ? node.x + card.offsetWidth : node.x,
        y: node.y + 14,
      };
    }

    const socket = card.querySelector(`.port-socket[data-port-id="${portId}"][data-dir="${dir}"]`);
    if (!socket) {
      return { x: node.x + (dir === "output" ? card.offsetWidth : 0), y: node.y + 24 };
    }

    const cardRect = card.getBoundingClientRect();
    const sockRect = socket.getBoundingClientRect();
    const dx = (sockRect.left + sockRect.width / 2 - cardRect.left) / this.scale;
    const dy = (sockRect.top + sockRect.height / 2 - cardRect.top) / this.scale;
    return { x: node.x + dx, y: node.y + dy };
  }

  _bezierPath(p1, p2) {
    // Graceful under-arc for loop-back wires (e.g. Chord Matcher -> Interception Return)
    if (p2.x < p1.x - 20) {
      const dx = Math.abs(p1.x - p2.x);
      const bowY = Math.max(120, Math.min(240, dx * 0.32));
      const c1x = p1.x + Math.min(140, dx * 0.3);
      const c2x = p2.x - Math.min(140, dx * 0.3);
      const midY = Math.max(p1.y, p2.y) + bowY;
      return `M ${p1.x} ${p1.y} C ${c1x} ${midY}, ${c2x} ${midY}, ${p2.x} ${p2.y}`;
    }
    const dist = Math.max(50, Math.abs(p2.x - p1.x) * 0.52);
    return `M ${p1.x} ${p1.y} C ${p1.x + dist} ${p1.y}, ${p2.x - dist} ${p2.y}, ${p2.x} ${p2.y}`;
  }

  _renderEdges() {
    this.edgesDefs.innerHTML = "";
    this.edgesGroup.innerHTML = "";
    if (!this.flow) return;

    const ns = "http://www.w3.org/2000/svg";

    for (const edge of this.flow.edges) {
      const p1 = this._getSocketWorldPos(edge.fromNodeId, edge.fromPortId, "output");
      const p2 = this._getSocketWorldPos(edge.toNodeId, edge.toPortId, "input");
      if (!p1 || !p2) continue;

      const fromNode = this.flow.nodes.find((n) => n.id === edge.fromNodeId);
      const toNode = this.flow.nodes.find((n) => n.id === edge.toNodeId);
      const fromPort = this.catalogMap.get(fromNode?.typeId)?.outputs?.find((p) => p.id === edge.fromPortId);
      const toPort = this.catalogMap.get(toNode?.typeId)?.inputs?.find((p) => p.id === edge.toPortId);

      const c1 = getPortColor(fromPort?.dataType);
      const c2 = getPortColor(toPort?.dataType);
      const compatible = isPortCompatible(fromPort?.dataType, toPort?.dataType);

      // Gradient definition (Blender style)
      const gradId = `grad-${edge.id}`;
      const grad = document.createElementNS(ns, "linearGradient");
      grad.id = gradId;
      grad.setAttribute("gradientUnits", "userSpaceOnUse");
      grad.setAttribute("x1", p1.x);
      grad.setAttribute("y1", p1.y);
      grad.setAttribute("x2", p2.x);
      grad.setAttribute("y2", p2.y);

      const s1 = document.createElementNS(ns, "stop");
      s1.setAttribute("offset", "0%");
      s1.setAttribute("stop-color", c1);
      const s2 = document.createElementNS(ns, "stop");
      s2.setAttribute("offset", "100%");
      s2.setAttribute("stop-color", c2);
      grad.append(s1, s2);
      this.edgesDefs.appendChild(grad);

      const d = this._bezierPath(p1, p2);

      const hit = document.createElementNS(ns, "path");
      hit.setAttribute("class", "wire-hit");
      hit.setAttribute("d", d);
      hit.addEventListener("mousedown", (e) => {
        e.stopPropagation();
        this.selectedNodeIds.clear();
        this.selectedEdgeId = edge.id;
        this._syncSelectionClasses();
        this._renderEdges();
        this.cb.onSelectionChange(null);
      });

      const wire = document.createElementNS(ns, "path");
      wire.setAttribute("class", `wire${this.selectedEdgeId === edge.id ? " selected" : ""}${!compatible ? " invalid" : ""}`);
      wire.setAttribute("d", d);
      wire.setAttribute("stroke", compatible ? `url(#${gradId})` : "#ef4444");
      wire.dataset.edgeId = edge.id;

      this.edgesGroup.append(hit, wire);

      // Warning icon on incompatible wire (Blender style)
      if (!compatible) {
        const mx = (p1.x + p2.x) / 2;
        const my = (p1.y + p2.y) / 2;
        const badge = document.createElementNS(ns, "g");
        badge.setAttribute("class", "wire-warning-badge");
        badge.innerHTML = `
          <rect x="${mx - 9}" y="${my - 9}" width="18" height="18" rx="4" fill="#991b1b" stroke="#f87171" stroke-width="1.2"/>
          <text x="${mx}" y="${my + 4}" text-anchor="middle" fill="#fff" font-size="11" font-weight="bold">!</text>
        `;
        this.edgesGroup.appendChild(badge);
      }
    }
  }

  pulseNode(nodeId) {
    const card = this.nodesLayer.querySelector(`[data-node-id="${nodeId}"]`);
    if (!card) return;
    card.classList.add("active-flash");
    clearTimeout(card._flashTimer);
    card._flashTimer = setTimeout(() => card.classList.remove("active-flash"), 260);
  }

  pulseWire(edgeId, fromNodeId, toNodeId) {
    const wire = this.edgesGroup.querySelector(`[data-edge-id="${edgeId}"]`);
    if (wire) {
      wire.classList.add("pulse");
      clearTimeout(wire._pulseTimer);
      wire._pulseTimer = setTimeout(() => wire.classList.remove("pulse"), 240);
    }

    this.pulseNode(fromNodeId);
    this.pulseNode(toNodeId);
  }

  updateNodeStatus(nodeId, text) {
    const bar = this.nodesLayer.querySelector(`[data-status-for="${nodeId}"]`);
    if (!bar) return;
    if (!text) {
      bar.style.display = "none";
      return;
    }
    bar.style.display = "flex";
    bar.querySelector(".node-status-text").textContent = text;
  }

  _handleNodeMouseDown(e, node) {
    if (e.shiftKey || e.ctrlKey) {
      if (this.selectedNodeIds.has(node.id)) this.selectedNodeIds.delete(node.id);
      else this.selectedNodeIds.add(node.id);
    } else if (!this.selectedNodeIds.has(node.id)) {
      this.selectedNodeIds.clear();
      this.selectedNodeIds.add(node.id);
    }

    this.selectedEdgeId = null;
    this._syncSelectionClasses();
    this._renderEdges();
    this.cb.onSelectionChange(this._getSingleSelectedNode());

    const startWorld = this.screenToWorld(e.clientX, e.clientY);
    const origins = new Map();
    for (const id of this.selectedNodeIds) {
      const n = this.flow.nodes.find((x) => x.id === id);
      if (n) origins.set(id, { x: n.x, y: n.y });
    }

    this.dragState = { startWorld, origins, moved: false };
  }

  _startWireDrag(e, nodeId, portId, dir, dataType) {
    // Blender ergonomics: dragging from a connected input socket detaches the existing edge to rewire it
    if (dir === "input") {
      const existingIdx = this.flow.edges.findIndex((ed) => ed.toNodeId === nodeId && ed.toPortId === portId);
      if (existingIdx !== -1) {
        const [removed] = this.flow.edges.splice(existingIdx, 1);
        this.cb.onDirty();
        this._renderEdges();
        const fromNode = this.flow.nodes.find((n) => n.id === removed.fromNodeId);
        const fromPort = this.catalogMap.get(fromNode?.typeId)?.outputs?.find((p) => p.id === removed.fromPortId);
        this.wireState = {
          nodeId: removed.fromNodeId,
          portId: removed.fromPortId,
          dir: "output",
          dataType: fromPort?.dataType || "any",
        };
        this._updateTempWire(e.clientX, e.clientY);
        return;
      }
    }

    this.wireState = { nodeId, portId, dir, dataType };
    this._updateTempWire(e.clientX, e.clientY);
  }

  _updateTempWire(clientX, clientY) {
    if (!this.wireState) return;
    const start = this._getSocketWorldPos(this.wireState.nodeId, this.wireState.portId, this.wireState.dir);
    const end = this.screenToWorld(clientX, clientY);
    if (!start) return;

    const p1 = this.wireState.dir === "output" ? start : end;
    const p2 = this.wireState.dir === "output" ? end : start;
    this.tempWire.setAttribute("d", this._bezierPath(p1, p2));
    this.tempWire.style.display = "block";
  }

  _finishWireDrag(e) {
    this.tempWire.style.display = "none";
    const target = document.elementFromPoint(e.clientX, e.clientY)?.closest(".port-socket");
    if (target && this.wireState) {
      const tNodeId = target.dataset.nodeId;
      const tPortId = target.dataset.portId;
      const tDir = target.dataset.dir;

      if (tNodeId !== this.wireState.nodeId && tDir !== this.wireState.dir) {
        const fromNodeId = this.wireState.dir === "output" ? this.wireState.nodeId : tNodeId;
        const fromPortId = this.wireState.dir === "output" ? this.wireState.portId : tPortId;
        const toNodeId = this.wireState.dir === "input" ? this.wireState.nodeId : tNodeId;
        const toPortId = this.wireState.dir === "input" ? this.wireState.portId : tPortId;

        const duplicate = this.flow.edges.some(
          (ed) => ed.fromNodeId === fromNodeId && ed.fromPortId === fromPortId && ed.toNodeId === toNodeId && ed.toPortId === toPortId
        );

        if (!duplicate) {
          this.flow.edges.push({
            id: "e-" + Math.random().toString(36).slice(2, 9),
            fromNodeId,
            fromPortId,
            toNodeId,
            toPortId,
          });
          this.cb.onDirty();
          this._renderEdges();
        }
      }
    }
    this.wireState = null;
  }

  _syncSelectionClasses() {
    this.nodesLayer.querySelectorAll(".node-card").forEach((el) => {
      el.classList.toggle("selected", this.selectedNodeIds.has(el.dataset.nodeId));
    });
  }

  _getSingleSelectedNode() {
    if (!this.flow || this.selectedNodeIds.size !== 1) return null;
    const [id] = this.selectedNodeIds;
    return this.flow.nodes.find((n) => n.id === id) || null;
  }

  deleteSelection() {
    if (!this.flow) return;
    let changed = false;

    if (this.selectedEdgeId) {
      this.flow.edges = this.flow.edges.filter((e) => e.id !== this.selectedEdgeId);
      this.selectedEdgeId = null;
      changed = true;
    }

    if (this.selectedNodeIds.size > 0) {
      const removed = this.selectedNodeIds;
      this.flow.nodes = this.flow.nodes.filter((n) => !removed.has(n.id));
      this.flow.edges = this.flow.edges.filter((e) => !removed.has(e.fromNodeId) && !removed.has(e.toNodeId));
      this.selectedNodeIds.clear();
      changed = true;
    }

    if (changed) {
      this.cb.onDirty();
      this.render();
      this.cb.onSelectionChange(null);
    }
  }

  _bindEvents() {
    this.container.addEventListener("mousedown", (e) => {
      if (e.button === 1 || (e.button === 0 && !e.shiftKey)) {
        // Pan on background drag or middle mouse
        this.selectedNodeIds.clear();
        this.selectedEdgeId = null;
        this._syncSelectionClasses();
        this._renderEdges();
        this.cb.onSelectionChange(null);
        this.panState = { startX: e.clientX, startY: e.clientY, origPanX: this.panX, origPanY: this.panY };
      } else if (e.button === 0 && e.shiftKey) {
        const rect = this.container.getBoundingClientRect();
        this.marqueeState = { x0: e.clientX - rect.left, y0: e.clientY - rect.top };
        this.marqueeEl.style.display = "block";
      }
    });

    window.addEventListener("mousemove", (e) => {
      if (this.resizeState) {
        const dx = (e.clientX - this.resizeState.startX) / this.scale;
        if (Math.abs(dx) > 1) this.resizeState.moved = true;
        const nextW = Math.round(Math.max(120, Math.min(560, this.resizeState.startWidth + dx)));
        this.resizeState.node.width = nextW;
        this.resizeState.card.classList.add("has-custom-width");
        this.resizeState.card.style.width = `${nextW}px`;
        this._renderEdges();
        return;
      }

      if (this.wireState) {
        this._updateTempWire(e.clientX, e.clientY);
        return;
      }

      if (this.dragState) {
        const cur = this.screenToWorld(e.clientX, e.clientY);
        const dx = cur.x - this.dragState.startWorld.x;
        const dy = cur.y - this.dragState.startWorld.y;
        if (Math.abs(dx) > 1 || Math.abs(dy) > 1) this.dragState.moved = true;

        for (const [id, orig] of this.dragState.origins) {
          const n = this.flow.nodes.find((x) => x.id === id);
          if (!n) continue;
          n.x = Math.round(orig.x + dx);
          n.y = Math.round(orig.y + dy);
          const card = this.nodesLayer.querySelector(`[data-node-id="${id}"]`);
          if (card) {
            card.style.left = `${n.x}px`;
            card.style.top = `${n.y}px`;
          }
        }
        this._renderEdges();
        return;
      }

      if (this.panState) {
        this.panX = this.panState.origPanX + (e.clientX - this.panState.startX);
        this.panY = this.panState.origPanY + (e.clientY - this.panState.startY);
        this._applyTransform();
        return;
      }

      if (this.marqueeState) {
        const rect = this.container.getBoundingClientRect();
        const x1 = e.clientX - rect.left;
        const y1 = e.clientY - rect.top;
        const left = Math.min(this.marqueeState.x0, x1);
        const top = Math.min(this.marqueeState.y0, y1);
        const w = Math.abs(x1 - this.marqueeState.x0);
        const h = Math.abs(y1 - this.marqueeState.y0);
        Object.assign(this.marqueeEl.style, { left: `${left}px`, top: `${top}px`, width: `${w}px`, height: `${h}px` });
      }
    });

    window.addEventListener("mouseup", (e) => {
      if (this.resizeState) {
        this.resizeState.card.classList.remove("resizing");
        if (this.resizeState.moved) this.cb.onDirty();
        this.resizeState = null;
      }
      if (this.wireState) {
        this._finishWireDrag(e);
      }
      if (this.dragState) {
        if (this.dragState.moved) this.cb.onDirty();
        this.dragState = null;
      }
      if (this.panState) {
        this.panState = null;
      }
      if (this.marqueeState) {
        this._selectMarquee();
        this.marqueeEl.style.display = "none";
        this.marqueeState = null;
      }
    });

    this.container.addEventListener("wheel", (e) => {
      e.preventDefault();
      const factor = e.deltaY < 0 ? 1.1 : 0.9;
      this.zoomBy(factor, e.clientX, e.clientY);
    }, { passive: false });

    this.container.addEventListener("contextmenu", (e) => {
      e.preventDefault();
      const world = this.screenToWorld(e.clientX, e.clientY);
      this.cb.onCanvasContextMenu(e.clientX, e.clientY, world);
    });

    // Drag & drop from palette
    this.container.addEventListener("dragover", (e) => {
      e.preventDefault();
      e.dataTransfer.dropEffect = "copy";
    });

    this.container.addEventListener("drop", (e) => {
      e.preventDefault();
      const typeId = e.dataTransfer.getData("text/naseqmen-node-type");
      if (!typeId) return;
      const world = this.screenToWorld(e.clientX, e.clientY);
      this.cb.onSpawnNode(typeId, Math.round(world.x - 100), Math.round(world.y - 20));
    });

    window.addEventListener("keydown", (e) => {
      const tag = document.activeElement?.tagName;
      if (tag === "INPUT" || tag === "TEXTAREA" || tag === "SELECT") return;
      if (e.key === "Delete" || e.key === "Backspace") {
        e.preventDefault();
        this.deleteSelection();
      }
    });
  }

  _selectMarquee() {
    if (!this.flow) return;
    const mRect = this.marqueeEl.getBoundingClientRect();
    this.selectedNodeIds.clear();
    this.nodesLayer.querySelectorAll(".node-card").forEach((card) => {
      const r = card.getBoundingClientRect();
      if (r.right >= mRect.left && r.left <= mRect.right && r.bottom >= mRect.top && r.top <= mRect.bottom) {
        this.selectedNodeIds.add(card.dataset.nodeId);
      }
    });
    this._syncSelectionClasses();
    this.cb.onSelectionChange(this._getSingleSelectedNode());
  }
}
