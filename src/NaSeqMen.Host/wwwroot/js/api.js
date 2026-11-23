const Api = {
  async getCatalog() {
    const res = await fetch("/api/catalog");
    if (!res.ok) throw new Error("Failed to load catalog");
    return res.json();
  },

  async getWorkspace() {
    const res = await fetch("/api/workspace");
    if (!res.ok) throw new Error("Failed to load workspace");
    return res.json();
  },

  async deployWorkspace(workspace) {
    const res = await fetch("/api/workspace/deploy", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(workspace),
    });
    if (!res.ok) throw new Error("Failed to deploy workspace");
    return res.json();
  },

  async resetWorkspace() {
    const res = await fetch("/api/workspace/reset", { method: "POST" });
    if (!res.ok) throw new Error("Failed to reset workspace");
    return res.json();
  },

  async triggerNode(nodeId, payload = null) {
    const res = await fetch(`/api/nodes/${encodeURIComponent(nodeId)}/trigger`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ payload }),
    });
    return res.ok;
  },

  async interactNode(nodeId, action, data = null) {
    const res = await fetch(`/api/nodes/${encodeURIComponent(nodeId)}/interact`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ action, data }),
    });
    return res.ok;
  },

  async getState() {
    const res = await fetch("/api/state");
    if (!res.ok) return { paused: false, widgets: {}, statuses: {} };
    return res.json();
  },

  async getInterceptionDevices(dllPath = "") {
    const q = dllPath ? `?dllPath=${encodeURIComponent(dllPath)}` : "";
    const res = await fetch(`/api/interception/devices${q}`);
    if (!res.ok) return null;
    return res.json();
  },

  async panicReleaseInterception() {
    await fetch("/api/interception/panic-release", { method: "POST" });
  },

  async getDebugHistory() {
    const res = await fetch("/api/debug");
    if (!res.ok) return [];
    return res.json();
  },

  async clearDebug() {
    await fetch("/api/debug", { method: "DELETE" });
  },

  subscribeEvents(onEvent, onStatus) {
    const es = new EventSource("/api/events");
    es.onopen = () => onStatus(true);
    es.onerror = () => onStatus(false);
    es.onmessage = (ev) => {
      try {
        const parsed = JSON.parse(ev.data);
        onEvent(parsed);
      } catch { /* ignore */ }
    };
    return es;
  },
};
