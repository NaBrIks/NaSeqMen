let catalog = [];
let workspace = null;
let activeFlowId = null;
let dirty = false;
let canvas = null;
let debugPanel = null;
let flashEnabled = localStorage.getItem("naseqmen.flash") !== "0";
let engineConnected = false;
let enginePaused = false;

async function init() {
  applyI18n();

  debugPanel = new DebugPanel();
  canvas = new NodeCanvas({
    onDirty: markDirty,
    onSelectionChange: handleSelectionChange,
    onTriggerNode: handleTriggerNode,
    onSpawnNode: spawnNode,
    onCanvasContextMenu: showCanvasContextMenu,
    onNodeContextMenu: showNodeContextMenu,
  });

  bindTopbar();
  bindRightTabs();
  bindPaletteSearch();

  document.addEventListener("click", hideContextMenu);

  catalog = await Api.getCatalog();
  canvas.setCatalog(catalog);
  renderPalette();

  workspace = await Api.getWorkspace();
  if (!workspace.flows || workspace.flows.length === 0) {
    workspace.flows = [{ id: "flow-1", name: "Flow 1", enabled: true, nodes: [], edges: [] }];
  }
  selectFlow(workspace.flows[0].id);
  setDirty(false);

  const history = await Api.getDebugHistory();
  debugPanel.setHistory(history);

  Api.subscribeEvents(handleEngineEvent, (connected) => {
    engineConnected = connected;
    updateStatusBadge();
  });

  setTimeout(() => canvas.fitView(), 60);
}

function updateStatusBadge() {
  const badge = document.getElementById("engine-status");
  badge.classList.remove("offline", "paused");
  if (!engineConnected) {
    badge.classList.add("offline");
    badge.textContent = t("status.offline");
  } else if (enginePaused) {
    badge.classList.add("paused");
    badge.textContent = t("status.paused");
  } else {
    badge.textContent = t("status.connected");
  }
}

function getActiveFlow() {
  return workspace?.flows.find((f) => f.id === activeFlowId) || workspace?.flows[0];
}

function selectFlow(flowId) {
  activeFlowId = flowId;
  renderFlowTabs();
  canvas.setFlow(getActiveFlow());
  renderInfoPanel(workspace, catalog);
}

function setDirty(isDirty) {
  dirty = isDirty;
  const btn = document.getElementById("btn-deploy");
  btn.classList.toggle("dirty", dirty);
  btn.textContent = t(dirty ? "action.deploy" : "action.deployed");
}

function markDirty() {
  setDirty(true);
  renderInfoPanel(workspace, catalog);
}

function renderFlowTabs() {
  const wrap = document.getElementById("flow-tabs");
  wrap.innerHTML = "";

  for (const flow of workspace.flows) {
    const tab = document.createElement("div");
    tab.className = `flow-tab${flow.id === activeFlowId ? " active" : ""}`;

    const name = document.createElement("span");
    name.textContent = flow.name;
    name.addEventListener("dblclick", (e) => {
      e.stopPropagation();
      const next = prompt("Flow name:", flow.name);
      if (next && next.trim()) {
        flow.name = next.trim();
        markDirty();
        renderFlowTabs();
      }
    });

    tab.appendChild(name);

    if (workspace.flows.length > 1) {
      const close = document.createElement("span");
      close.className = "flow-tab-close";
      close.textContent = "×";
      close.addEventListener("click", (e) => {
        e.stopPropagation();
        workspace.flows = workspace.flows.filter((f) => f.id !== flow.id);
        markDirty();
        selectFlow(workspace.flows[0].id);
      });
      tab.appendChild(close);
    }

    tab.addEventListener("click", () => selectFlow(flow.id));
    wrap.appendChild(tab);
  }
}

function renderPalette() {
  const list = document.getElementById("palette-list");
  const filter = document.getElementById("palette-search").value.trim().toLowerCase();
  list.innerHTML = "";

  const groups = new Map();
  for (const desc of catalog) {
    if (filter && !desc.displayName.toLowerCase().includes(filter) && !desc.category.toLowerCase().includes(filter)) {
      continue;
    }
    if (!groups.has(desc.category)) groups.set(desc.category, []);
    groups.get(desc.category).push(desc);
  }

  for (const [category, items] of groups) {
    const sec = document.createElement("div");
    sec.className = "palette-category";
    sec.innerHTML = `<div class="palette-category-title">${category}</div>`;

    for (const desc of items) {
      const item = document.createElement("div");
      item.className = "palette-item";
      item.draggable = true;
      item.style.setProperty("--node-color", desc.color);
      item.title = desc.description || desc.displayName;
      item.innerHTML = `
        <span class="palette-item-name">${desc.displayName}</span>
        <span class="palette-item-ports">${desc.inputs.length}→${desc.outputs.length}</span>
      `;

      item.addEventListener("dragstart", (e) => {
        e.dataTransfer.setData("text/naseqmen-node-type", desc.typeId);
        e.dataTransfer.effectAllowed = "copy";
      });

      item.addEventListener("dblclick", () => {
        const rect = document.getElementById("canvas-container").getBoundingClientRect();
        const center = canvas.screenToWorld(rect.left + rect.width / 2, rect.top + rect.height / 2);
        spawnNode(desc.typeId, Math.round(center.x - 100), Math.round(center.y - 40));
      });

      sec.appendChild(item);
    }
    list.appendChild(sec);
  }
}

function spawnNode(typeId, x, y) {
  const desc = catalog.find((d) => d.typeId === typeId);
  const flow = getActiveFlow();
  if (!desc || !flow) return;

  const defaults = {};
  for (const p of desc.parameters || []) {
    defaults[p.key] = p.defaultValue ?? "";
  }

  const node = {
    id: "n-" + Math.random().toString(36).slice(2, 9),
    typeId: desc.typeId,
    title: desc.displayName,
    x,
    y,
    collapsed: false,
    enabled: true,
    parameters: defaults,
  };

  flow.nodes.push(node);
  canvas.selectedNodeIds.clear();
  canvas.selectedNodeIds.add(node.id);
  markDirty();
  canvas.render();
  handleSelectionChange(node);
}

function handleSelectionChange(node) {
  const desc = node ? catalog.find((d) => d.typeId === node.typeId) : null;
  renderInspector(node, desc, {
    onChanged: () => {
      markDirty();
      canvas.render();
    },
    onTrigger: handleTriggerNode,
    onDelete: () => canvas.deleteSelection(),
  });
}

async function handleTriggerNode(nodeId) {
  if (dirty) {
    await deployNow();
  }
  await Api.triggerNode(nodeId);
}

async function deployNow() {
  await Api.deployWorkspace(workspace);
  setDirty(false);
}

function handleEngineEvent(evt) {
  if (!evt || !evt.kind) return;
  if (evt.kind === "debug.entry") {
    debugPanel.addEntry(evt.data);
  } else if (evt.kind === "debug.cleared") {
    debugPanel.clear();
  } else if (evt.kind === "node.activity") {
    if (flashEnabled && evt.data?.nodeId) {
      canvas.pulseNode(evt.data.nodeId);
    }
  } else if (evt.kind === "wire.activity") {
    if (flashEnabled) {
      canvas.pulseWire(evt.data.edgeId, evt.data.fromNodeId, evt.data.toNodeId);
    }
  } else if (evt.kind === "node.status") {
    canvas.updateNodeStatus(evt.data.nodeId, evt.data.statusText);
  } else if (evt.kind === "engine.state" || evt.kind === "engine.deployed") {
    if (typeof evt.data?.paused === "boolean") {
      enginePaused = evt.data.paused;
      updateStatusBadge();
    }
  }
}

function bindTopbar() {
  document.getElementById("btn-deploy").addEventListener("click", deployNow);

  document.getElementById("btn-add-flow").addEventListener("click", () => {
    const idx = workspace.flows.length + 1;
    const flow = { id: "flow-" + Math.random().toString(36).slice(2, 7), name: `Flow ${idx}`, enabled: true, nodes: [], edges: [] };
    workspace.flows.push(flow);
    markDirty();
    selectFlow(flow.id);
  });

  document.getElementById("btn-zoom-in").addEventListener("click", () => canvas.zoomBy(1.15));
  document.getElementById("btn-zoom-out").addEventListener("click", () => canvas.zoomBy(0.85));
  document.getElementById("btn-fit").addEventListener("click", () => canvas.fitView());

  const flashBtn = document.getElementById("btn-flash");
  flashBtn.classList.toggle("active", flashEnabled);
  flashBtn.addEventListener("click", () => {
    flashEnabled = !flashEnabled;
    localStorage.setItem("naseqmen.flash", flashEnabled ? "1" : "0");
    flashBtn.classList.toggle("active", flashEnabled);
  });

  document.getElementById("btn-reset").addEventListener("click", async () => {
    workspace = await Api.resetWorkspace();
    selectFlow(workspace.flows[0].id);
    setDirty(false);
    setTimeout(() => canvas.fitView(), 40);
  });

  document.getElementById("btn-lang").addEventListener("click", () => {
    toggleLang();
    setDirty(dirty);
    updateStatusBadge();
    debugPanel.render();
    handleSelectionChange(canvas._getSingleSelectedNode());
    renderInfoPanel(workspace, catalog);
  });
}

function bindRightTabs() {
  document.querySelectorAll(".right-tab").forEach((btn) => {
    btn.addEventListener("click", () => {
      document.querySelectorAll(".right-tab").forEach((b) => b.classList.remove("active"));
      document.querySelectorAll(".right-panel").forEach((p) => p.classList.remove("active"));
      btn.classList.add("active");
      document.getElementById(`panel-${btn.dataset.tab}`).classList.add("active");
    });
  });
}

function bindPaletteSearch() {
  document.getElementById("palette-search").addEventListener("input", renderPalette);
}

function showContextMenu(x, y, items) {
  const menu = document.getElementById("context-menu");
  menu.innerHTML = "";

  for (const item of items) {
    if (item.sep) {
      const sep = document.createElement("div");
      sep.className = "context-menu-sep";
      menu.appendChild(sep);
    } else if (item.header) {
      const h = document.createElement("div");
      h.className = "context-menu-header";
      h.textContent = item.header;
      menu.appendChild(h);
    } else {
      const row = document.createElement("div");
      row.className = `context-menu-item${item.danger ? " danger" : ""}`;
      row.textContent = item.label;
      row.addEventListener("click", (e) => {
        e.stopPropagation();
        hideContextMenu();
        item.action();
      });
      menu.appendChild(row);
    }
  }

  menu.style.left = `${Math.min(x, window.innerWidth - 190)}px`;
  menu.style.top = `${Math.min(y, window.innerHeight - 240)}px`;
  menu.style.display = "block";
}

function hideContextMenu() {
  document.getElementById("context-menu").style.display = "none";
}

function showCanvasContextMenu(clientX, clientY, worldPos) {
  const items = [
    { header: t("ctx.addNode") },
    ...catalog.map((d) => ({
      label: `+ ${d.displayName}`,
      action: () => spawnNode(d.typeId, Math.round(worldPos.x), Math.round(worldPos.y)),
    })),
    { sep: true },
    { label: t("ctx.fitView"), action: () => canvas.fitView() },
  ];
  showContextMenu(clientX, clientY, items);
}

function showNodeContextMenu(clientX, clientY, node) {
  const desc = catalog.find((d) => d.typeId === node.typeId);
  const items = [];
  if (desc?.supportsManualTrigger) {
    items.push({ label: `▶ ${t("ctx.trigger")}`, action: () => handleTriggerNode(node.id) });
  }
  items.push(
    {
      label: t("ctx.duplicate"),
      action: () => {
        const clone = structuredClone(node);
        clone.id = "n-" + Math.random().toString(36).slice(2, 9);
        clone.x += 32;
        clone.y += 32;
        getActiveFlow().nodes.push(clone);
        markDirty();
        canvas.render();
      },
    },
    {
      label: t("ctx.toggleEnable"),
      action: () => {
        node.enabled = !node.enabled;
        markDirty();
        canvas.render();
      },
    },
    { sep: true },
    { label: t("ctx.delete"), danger: true, action: () => canvas.deleteSelection() }
  );
  showContextMenu(clientX, clientY, items);
}

init();
