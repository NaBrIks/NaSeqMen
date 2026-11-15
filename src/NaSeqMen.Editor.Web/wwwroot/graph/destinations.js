// SPDX-License-Identifier: MIT
import { displayName, hasTranslation, t } from '../i18n.js';

export function projectDestinations(configuration, modules) {
  const nodes = [...configuration.nodes];
  const edges = [...configuration.edges];
  const destinations = new Map();
  const knownIds = new Set(nodes.map(node => node.id));
  const connections = new Map((configuration.connections || []).map(connection => [connection.id, connection]));
  const byModule = new Map(modules.map(module => [module.id, module]));
  for (const action of configuration.nodes.filter(node => node.kind === 'action')) {
    const module = byModule.get(action.module);
    const key = module?.actions?.find(operation => operation.id === action.operation)?.destinationNameKey;
    const namedConnection = connections.get(action.connectionId)?.name;
    const name = key && hasTranslation(key) ? t(key)
      : namedConnection || displayName(module) || t('graph.destination.unavailable');
    let id = '@destination:' + action.id;
    while (knownIds.has(id)) id += ':';
    knownIds.add(id);
    nodes.push({ id, name, kind: 'destination', enabled: action.enabled });
    edges.push({ from: action.id, to: id, delivery: true });
    destinations.set(action.id, id);
  }
  return { configuration: { ...configuration, nodes, edges }, destinations };
}
