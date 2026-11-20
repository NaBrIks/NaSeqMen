class DebugPanel {
  constructor() {
    this.listEl = document.getElementById("debug-list");
    this.filterEl = document.getElementById("debug-filter");
    this.pauseBtn = document.getElementById("btn-debug-pause");
    this.clearBtn = document.getElementById("btn-debug-clear");

    this.entries = [];
    this.paused = false;

    this.filterEl.addEventListener("input", () => this.render());
    this.pauseBtn.addEventListener("click", () => {
      this.paused = !this.paused;
      this.pauseBtn.classList.toggle("active", this.paused);
    });
    this.clearBtn.addEventListener("click", async () => {
      await Api.clearDebug();
      this.clear();
    });
  }

  setHistory(items) {
    this.entries = items.slice(-200);
    this.render();
  }

  addEntry(entry) {
    if (this.paused) return;
    this.entries.push(entry);
    if (this.entries.length > 200) this.entries.shift();

    const wasAtBottom = this.listEl.scrollHeight - this.listEl.scrollTop - this.listEl.clientHeight < 28;
    this.render();
    if (wasAtBottom) {
      this.listEl.scrollTop = this.listEl.scrollHeight;
    }
  }

  clear() {
    this.entries = [];
    this.render();
  }

  render() {
    const q = this.filterEl.value.trim().toLowerCase();
    const filtered = q
      ? this.entries.filter((e) => JSON.stringify(e).toLowerCase().includes(q))
      : this.entries;

    this.listEl.innerHTML = "";
    if (filtered.length === 0) {
      this.listEl.innerHTML = `<div class="debug-empty">${t("debug.empty")}</div>`;
      return;
    }

    const frag = document.createDocumentFragment();
    for (const entry of filtered) {
      frag.appendChild(this._createItem(entry));
    }
    this.listEl.appendChild(frag);
  }

  _createItem(entry) {
    const item = document.createElement("div");
    item.className = "debug-item";

    const dt = new Date(entry.timestamp);
    const timeStr = dt.toLocaleTimeString([], { hour12: false, hour: "2-digit", minute: "2-digit", second: "2-digit" })
      + "." + String(dt.getMilliseconds()).padStart(3, "0");

    const head = document.createElement("div");
    head.className = "debug-item-head";
    head.innerHTML = `
      <span class="debug-item-time">${timeStr}</span>
      <span class="debug-item-node">${this._esc(entry.nodeTitle || entry.nodeId)}</span>
      ${entry.label ? `<span class="debug-item-tag">${this._esc(entry.label)}</span>` : ""}
      ${entry.topic ? `<span class="debug-item-topic">${this._esc(entry.topic)}</span>` : ""}
    `;
    item.appendChild(head);

    const tree = document.createElement("div");
    tree.className = "json-tree";
    const valueToShow = entry.fullBasket
      ? {
          payload: entry.payload,
          topic: entry.topic,
          properties: entry.properties || {},
          meta: {
            messageId: entry.messageId,
            sourceNodeId: entry.sourceNodeId,
            sourcePortId: entry.sourcePortId,
          },
        }
      : entry.payload;

    tree.appendChild(this._renderJsonValue(valueToShow, "msg", true));
    item.appendChild(tree);
    return item;
  }

  _renderJsonValue(val, key = null, expandRoot = false) {
    const keyHtml = key !== null ? `<span class="json-key">${this._esc(key)}</span>: ` : "";

    if (val === null || val === undefined) {
      const span = document.createElement("div");
      span.innerHTML = `${keyHtml}<span class="json-null">null</span>`;
      return span;
    }

    if (typeof val === "string") {
      const span = document.createElement("div");
      span.innerHTML = `${keyHtml}<span class="json-string">"${this._esc(val)}"</span>`;
      return span;
    }

    if (typeof val === "number") {
      const span = document.createElement("div");
      span.innerHTML = `${keyHtml}<span class="json-number">${val}</span>`;
      return span;
    }

    if (typeof val === "boolean") {
      const span = document.createElement("div");
      span.innerHTML = `${keyHtml}<span class="json-bool">${val}</span>`;
      return span;
    }

    const isArr = Array.isArray(val);
    const entries = isArr ? val.map((v, i) => [String(i), v]) : Object.entries(val);
    const details = document.createElement("details");
    details.className = "json-branch";
    if (expandRoot) details.open = true;

    const summary = document.createElement("summary");
    summary.innerHTML = `${keyHtml}${isArr ? `Array(${entries.length})` : `Object {${entries.length}}`}`;
    details.appendChild(summary);

    const children = document.createElement("div");
    children.className = "json-children";
    for (const [k, v] of entries) {
      children.appendChild(this._renderJsonValue(v, k, k === "payload"));
    }
    details.appendChild(children);
    return details;
  }

  _esc(s) {
    return String(s).replace(/[&<>"']/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));
  }
}
