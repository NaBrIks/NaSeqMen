const Api = {
  async getCatalog(lang = "en") {
    const res = await fetch(`/api/catalog?lang=${encodeURIComponent(lang || "en")}`);
    if (!res.ok) throw new Error("Failed to load catalog");
    return res.json();
  },

  async getLanguages() {
    const res = await fetch("/api/languages");
    if (!res.ok) return [{ code: "en", nativeName: "English" }, { code: "ru", nativeName: "Русский" }];
    return res.json();
  },

  async getNodeDocs(typeId, lang = "en") {
    const res = await fetch(`/api/docs/${encodeURIComponent(typeId)}?lang=${encodeURIComponent(lang || "en")}`);
    if (!res.ok) return null;
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

  async getPlugins() {
    const res = await fetch("/api/plugins");
    if (!res.ok) return { pluginsDirectory: "", plugins: [] };
    return res.json();
  },

  async invokePluginAction(pluginId, action, data = null) {
    const res = await fetch(`/api/plugins/${encodeURIComponent(pluginId)}/${encodeURIComponent(action)}`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(data || {}),
    });
    if (!res.ok) return null;
    return res.json();
  },

  async getInterceptionDevices(dllPath = "") {
    return this.invokePluginAction("naseqmen.interception", "devices", { dllPath });
  },

  async panicReleaseInterception() {
    await this.invokePluginAction("naseqmen.interception", "panic-release");
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
