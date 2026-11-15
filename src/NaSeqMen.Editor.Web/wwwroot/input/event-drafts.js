// SPDX-License-Identifier: MIT
export const windowsExplorerSteps = [
  'key:E0:5B down', 'key:12 down', 'key:12 up', 'key:E0:5B up'
].join('\n');

export function createEventDraft(config, endpoint, name, operation, settings) {
  const existing = config.nodes.find(node => node.kind === 'source' && node.enabled !== false &&
    node.module === endpoint.module && node.connectionId === endpoint.connectionId &&
    node.settings?.source === endpoint.source && !node.settings.control &&
    !node.settings.phase && !node.settings.module &&
    (node.inputType || 'any') === 'any' && (node.outputType || 'any') === 'any');
  const source = existing || {
    id: crypto.randomUUID(), name: endpoint.name, kind: 'source', enabled: true,
    module: endpoint.module, connectionId: endpoint.connectionId,
    settings: { source: endpoint.source }, order: config.nodes.length
  };
  return {
    seed: {
      name, kind: 'event', module: endpoint.module, connectionId: endpoint.connectionId,
      operation, settings: { source: endpoint.source, ...settings }
    },
    attach(candidate, eventId) {
      if (!existing) candidate.nodes.push(source);
      candidate.edges.push({ from: source.id, to: eventId });
    }
  };
}
