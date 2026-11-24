let catalog = [];
let pluginsInfo = null;
let workspace = null;
let activeFlowId = null;
let dirty = false;
let canvas = null;
let debugPanel = null;
let flashEnabled = localStorage.getItem("naseqmen.flash") !== "0";
let densityMode = localStorage.getItem("naseqmen.density") || "full";
let engineConnected = false;
let enginePaused = false;
const widgetStates = new Map();
const nodeStatuses = new Map();

async function init() {
  applyI18n();

  debugPanel = new DebugPanel();
  canvas = new NodeCanvas({
    onDirty: markDirty,
    onSelectionChange: handleSelectionChange,
    onTriggerNode: handleTriggerNode,
    onInteractNode: handleInteractNode,
    onSpawnNode: spawnNode,
    onCanvasContextMenu: showCanvasContextMenu,
    onNodeContextMenu: showNodeContextMenu,
    onOpenNodeHelp: (typeId) => HelpPanel.showNode(typeId, true),
    onAfterRender: applyCachedStates,
  });

  bindTopbar();
  bindRightTabs();
  bindPaletteSearch();
  bindSidebarResizers();

  document.addEventListener("click", hideContextMenu);

  const langs = await Api.getLanguages();
  populateLanguageSelector(langs);

  catalog = await Api.getCatalog(currentLang);
  pluginsInfo = await Api.getPlugins();
  canvas.setCatalog(catalog);
  HelpPanel.init(catalog);
  renderPalette();

  workspace = await Api.getWorkspace();
  if (!workspace.flows || workspace.flows.length === 0) {
    workspace.flows = [{ id: "flow-1", name: "Flow 1", enabled: true, nodes: [], edges: [] }];
  }
  selectFlow(workspace.flows[0].id);
  setDirty(false);

  const history = await Api.getDebugHistory();
  debugPanel.setHistory(history);

  const liveState = await Api.getState();
  enginePaused = Boolean(liveState.paused);
  if (liveState.widgets) {
    for (const [nid, st] of Object.entries(liveState.widgets)) {
      widgetStates.set(nid, st);
    }
  }
  if (liveState.statuses) {
    for (const [nid, st] of Object.entries(liveState.statuses)) {
      nodeStatuses.set(nid, st);
    }
  }
  applyCachedStates();

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
  applyCachedStates();
  renderInfoPanel(workspace, catalog, pluginsInfo);
}

function applyCachedStates() {
  for (const [nid, st] of widgetStates) {
    NodeWidgets.applyState(nid, st);
  }
  for (const [nid, st] of nodeStatuses) {
    canvas.updateNodeStatus(nid, st);
  }
}

function setDirty(isDirty) {
  dirty = isDirty;
  const btn = document.getElementById("btn-deploy");
  btn.classList.toggle("dirty", dirty);
  btn.textContent = t(dirty ? "action.deploy" : "action.deployed");
}

function markDirty() {
  setDirty(true);
  renderInfoPanel(workspace, catalog, pluginsInfo);
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
        <button type="button" class="palette-help-btn" title="${t("help.openDocs")}">?</button>
      `;

      item.querySelector(".palette-help-btn").addEventListener("click", (e) => {
        e.stopPropagation();
        HelpPanel.showNode(desc.typeId, true);
      });

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
  if (node && typeof HelpPanel !== "undefined") {
    HelpPanel.showNode(node.typeId, false);
  }
  renderInspector(node, desc, {
    onChanged: () => {
      markDirty();
      canvas.render();
      applyCachedStates();
    },
    onTrigger: handleTriggerNode,
    onDelete: () => canvas.deleteSelection(),
    onOpenHelp: (typeId) => HelpPanel.showNode(typeId, true),
    getWidgetState: (id) => widgetStates.get(id) || null,
  });
}

async function handleTriggerNode(nodeId) {
  if (dirty) {
    await deployNow();
  }
  await Api.triggerNode(nodeId);
}

async function handleInteractNode(nodeId, action, data = null) {
  if (dirty && action !== "change") {
    await deployNow();
  }
  await Api.interactNode(nodeId, action, data);
}

async function syncStateFromServer() {
  const liveState = await Api.getState();
  enginePaused = Boolean(liveState.paused);
  widgetStates.clear();
  nodeStatuses.clear();
  if (liveState.widgets) {
    for (const [nid, st] of Object.entries(liveState.widgets)) {
      widgetStates.set(nid, st);
    }
  }
  if (liveState.statuses) {
    for (const [nid, st] of Object.entries(liveState.statuses)) {
      nodeStatuses.set(nid, st);
    }
  }
  applyCachedStates();
  updateStatusBadge();
}

async function deployNow() {
  await Api.deployWorkspace(workspace);
  setDirty(false);
  await syncStateFromServer();
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
    nodeStatuses.set(evt.data.nodeId, evt.data.statusText);
    canvas.updateNodeStatus(evt.data.nodeId, evt.data.statusText);
  } else if (evt.kind === "node.widget") {
    widgetStates.set(evt.data.nodeId, evt.data.state);
    NodeWidgets.applyState(evt.data.nodeId, evt.data.state);
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

  applyDensityMode(densityMode, false);
  document.querySelectorAll(".density-btn[data-density]").forEach((btn) => {
    btn.addEventListener("click", () => applyDensityMode(btn.dataset.density, true));
  });

  applyWireColorMode(canvas.wireColorMode, false);
  document.querySelectorAll("[data-wire-color]").forEach((btn) => {
    btn.addEventListener("click", () => {
      const next = btn.dataset.wireColor;
      if (next === "random" && canvas.wireColorMode === "random") {
        canvas.wireRandomSeed++;
      }
      applyWireColorMode(next, true);
    });
  });

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
    await syncStateFromServer();
    setTimeout(() => canvas.fitView(), 40);
  });

  document.getElementById("lang-select").addEventListener("change", async (e) => {
    await switchLanguage(e.target.value);
  });
}

function populateLanguageSelector(languages) {
  const sel = document.getElementById("lang-select");
  if (!sel) return;
  const list = Array.isArray(languages) && languages.length > 0
    ? languages
    : [{ code: "en", nativeName: "English" }, { code: "ru", nativeName: "Русский" }];
  sel.innerHTML = "";
  for (const lang of list) {
    const opt = document.createElement("option");
    opt.value = lang.code;
    opt.textContent = `${lang.code.toUpperCase()} · ${lang.nativeName}`;
    sel.appendChild(opt);
  }
  if (!list.some((l) => l.code === currentLang)) {
    currentLang = list[0].code;
    localStorage.setItem("naseqmen.lang", currentLang);
  }
  sel.value = currentLang;
}

async function switchLanguage(langCode) {
  setLang(langCode);
  catalog = await Api.getCatalog(currentLang);
  canvas.setCatalog(catalog);
  canvas.render();
  renderPalette();
  HelpPanel.setCatalog(catalog);
  setDirty(dirty);
  updateStatusBadge();
  debugPanel.render();
  handleSelectionChange(canvas._getSingleSelectedNode());
  renderInfoPanel(workspace, catalog, pluginsInfo);
}

function applyDensityMode(mode, rerenderEdges = true) {
  densityMode = ["full", "medium", "compact"].includes(mode) ? mode : "full";
  localStorage.setItem("naseqmen.density", densityMode);
  document.getElementById("app").setAttribute("data-density", densityMode);
  document.querySelectorAll(".density-btn[data-density]").forEach((b) => {
    b.classList.toggle("active", b.dataset.density === densityMode);
  });
  if (rerenderEdges && canvas) {
    requestAnimationFrame(() => canvas._renderEdges());
  }
}

function applyWireColorMode(mode, rerender = true) {
  const valid = ["type", "level", "random"].includes(mode) ? mode : "type";
  canvas.wireColorMode = valid;
  localStorage.setItem("naseqmen.wireColor", valid);
  document.querySelectorAll("[data-wire-color]").forEach((b) => {
    b.classList.toggle("active", b.dataset.wireColor === valid);
  });
  if (rerender && canvas) canvas._renderEdges();
}

function activateRightTab(tabName) {
  document.querySelectorAll(".right-tab").forEach((b) => {
    b.classList.toggle("active", b.dataset.tab === tabName);
  });
  document.querySelectorAll(".right-panel").forEach((p) => {
    p.classList.toggle("active", p.id === `panel-${tabName}`);
  });
}

function bindRightTabs() {
  document.querySelectorAll(".right-tab").forEach((btn) => {
    btn.addEventListener("click", () => activateRightTab(btn.dataset.tab));
  });
}

function bindPaletteSearch() {
  document.getElementById("palette-search").addEventListener("input", renderPalette);
}

function bindSidebarResizers() {
  setupSidebarResizer("resizer-right", "sidebar-right", "naseqmen.rightWidth", "right", 310, 220, 760);
  setupSidebarResizer("resizer-left", "sidebar-left", "naseqmen.leftWidth", "left", 215, 150, 420);
}

function setupSidebarResizer(resizerId, sidebarId, storageKey, side, defaultWidth, minWidth, maxWidth) {
  const resizer = document.getElementById(resizerId);
  const sidebar = document.getElementById(sidebarId);
  if (!resizer || !sidebar) return;

  const saved = Number(localStorage.getItem(storageKey));
  if (saved >= minWidth && saved <= maxWidth) {
    sidebar.style.width = `${saved}px`;
  }

  let drag = null;

  resizer.addEventListener("mousedown", (e) => {
    if (e.button !== 0) return;
    e.preventDefault();
    resizer.classList.add("dragging");
    drag = { startX: e.clientX, startWidth: sidebar.getBoundingClientRect().width };
    document.body.style.cursor = "col-resize";
  });

  resizer.addEventListener("dblclick", () => {
    sidebar.style.width = `${defaultWidth}px`;
    localStorage.setItem(storageKey, String(defaultWidth));
    if (canvas) requestAnimationFrame(() => canvas._renderEdges());
  });

  window.addEventListener("mousemove", (e) => {
    if (!drag) return;
    const dx = e.clientX - drag.startX;
    const raw = side === "right" ? drag.startWidth - dx : drag.startWidth + dx;
    const maxDynamic = Math.min(maxWidth, Math.floor(window.innerWidth * 0.65));
    const width = Math.round(Math.max(minWidth, Math.min(maxDynamic, raw)));
    sidebar.style.width = `${width}px`;
    localStorage.setItem(storageKey, String(width));
  });

  window.addEventListener("mouseup", () => {
    if (!drag) return;
    drag = null;
    resizer.classList.remove("dragging");
    document.body.style.cursor = "";
    if (canvas) requestAnimationFrame(() => canvas._renderEdges());
  });
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
  items.push({ label: `? ${t("ctx.help")}`, action: () => HelpPanel.showNode(node.typeId, true) });
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
        applyCachedStates();
      },
    },
    ...(node.width ? [{
      label: t("ctx.resetWidth"),
      action: () => {
        node.width = null;
        markDirty();
        canvas.render();
        applyCachedStates();
      },
    }] : []),
    { sep: true },
    { label: t("ctx.delete"), danger: true, action: () => canvas.deleteSelection() }
  );
  showContextMenu(clientX, clientY, items);
}

init();
