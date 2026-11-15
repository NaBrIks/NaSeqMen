// SPDX-License-Identifier: MIT
import { t, displayName, onLanguageChange } from './i18n.js';

const parameterKeys = {
  url: 'forms.field.url', password: 'forms.field.password', devicePath: 'forms.field.devicePath',
  includeMovement: 'forms.field.includeMovement'
};
const secretKey = key => /password|passwd|secret|token|credential|api.?key|authorization/i.test(key);
const element = (tag, text, className) => {
  const node = document.createElement(tag);
  if (text != null) node.textContent = text;
  if (className) node.className = className;
  return node;
};
function button(key, icon, callback) {
  const result = element('button', null, 'row-action'); result.type = 'button';
  const glyph = element('i', null, 'ti ti-' + icon); glyph.setAttribute('aria-hidden', 'true');
  result.append(glyph); result.setAttribute('aria-label', t(key)); result.dataset.tooltip = t(key);
  result.addEventListener('click', callback); return result;
}
function safeValue(value, key = '') {
  if (secretKey(key)) return value == null || value === '' ? t('settings.notConfigured') : t('settings.secretConfigured');
  if (value == null || value === '') return t('forms.placeholder.none');
  if (typeof value === 'boolean' || value === 'true' || value === 'false') return t(value === true || value === 'true' ? 'settings.yes' : 'settings.no');
  if (typeof value === 'object') {
    const sanitized = Array.isArray(value) ? value.map(item => safeValue(item)) : Object.fromEntries(Object.entries(value).map(([name, item]) => [name, safeValue(item, name)]));
    return JSON.stringify(sanitized);
  }
  // Credentials embedded in an address must not become visible text or tooltip content.
  if (typeof value === 'string' && /^[a-z][a-z0-9+.-]*:\/\//i.test(value)) {
    try {
      const url = new URL(value);
      if (url.username) url.username = '***';
      if (url.password) url.password = '***';
      for (const name of [...url.searchParams.keys()]) if (secretKey(name)) url.searchParams.set(name, '***');
      return url.toString();
    } catch { return t('settings.invalidAddress'); }
  }
  return String(value);
}

/** Read-only settings navigation. Editing is available only through an explicit page action. */
export function createConnectionSettings(host, { onEdit, onNavigate }) {
  let selected = null, config = { connections: [] }, modules = [], filters = {}, sort = { key: 'name', ascending: true };
  const moduleName = connection => displayName(modules.find(module => module.id === connection.module)) || t('settings.moduleUnavailable');
  const enabledName = connection => t(connection.enabled !== false ? 'settings.yes' : 'settings.no');
  function cell(row, value, tag = 'td') {
    const result = element(tag, value); result.dataset.tooltip = value; row.append(result); return result;
  }
  function overview() {
    const heading = element('div', null, 'toolbar'); heading.append(element('strong', t('settings.connections'))); host.append(heading);
    const scroll = element('div', null, 'table-scroll workspace-pane'); scroll.tabIndex = 0;
    const table = element('table'); table.dataset.settingsOverview = '';
    const columns = [{ key: 'name', caption: 'forms.field.name' }, { key: 'module', caption: 'forms.field.module' }, { key: 'enabled', caption: 'forms.field.connectionEnabled' }];
    const colgroup = element('colgroup'); const actionColumn = element('col'); actionColumn.style.width = '32px'; colgroup.append(actionColumn);
    columns.forEach(() => colgroup.append(element('col'))); table.append(colgroup);
    const head = element('thead'), header = element('tr'); header.append(element('th'));
    const body = element('tbody');
    function rows() {
      body.replaceChildren();
      const entries = (config.connections || []).map(connection => ({ connection, name: connection.name, module: moduleName(connection), enabled: enabledName(connection) }))
        .filter(entry => columns.every(column => String(entry[column.key]).toLocaleLowerCase().includes((filters[column.key] || '').toLocaleLowerCase())))
        .sort((a, b) => String(a[sort.key]).localeCompare(String(b[sort.key])) * (sort.ascending ? 1 : -1));
      for (const entry of entries) {
        const row = element('tr'), action = element('td');
        action.append(button('settings.openConnection', 'chevron-right', () => onNavigate(entry.connection.id)));
        row.append(action); columns.forEach(column => cell(row, entry[column.key])); body.append(row);
      }
      if (!entries.length) { const row = element('tr'); const empty = cell(row, t('settings.noConnections')); empty.colSpan = 4; body.append(row); }
    }
    for (const column of columns) {
      const headerCell = element('th'), sortButton = element('button', t(column.caption)); sortButton.type = 'button'; sortButton.dataset.tooltip = t('settings.sortColumn', { name: t(column.caption) });
      sortButton.addEventListener('click', () => { sort = { key: column.key, ascending: sort.key !== column.key || !sort.ascending }; rows(); });
      const filter = element('input'); filter.type = 'search'; filter.value = filters[column.key] || ''; filter.dataset.settingsFilter = column.key;
      filter.setAttribute('aria-label', t('settings.filterColumn', { name: t(column.caption) })); filter.dataset.tooltip = t('settings.filterColumn', { name: t(column.caption) });
      filter.addEventListener('input', () => { filters[column.key] = filter.value; rows(); });
      headerCell.append(sortButton, filter); header.append(headerCell);
    }
    head.append(header); table.append(head, body); scroll.append(table); host.append(scroll); rows();
  }
  function detail(connection) {
    const toolbar = element('div', null, 'toolbar'); toolbar.append(element('strong', connection.name));
    const edit = button('settings.editConnection', 'pencil', () => onEdit(connection.id)); edit.dataset.settingsEdit = ''; edit.append(element('span', t('settings.editConnection'))); toolbar.append(edit); host.append(toolbar);
    const scroll = element('div', null, 'table-scroll workspace-pane'); scroll.tabIndex = 0;
    const table = element('table'); table.dataset.settingsDetail = '';
    const colgroup = element('colgroup'), nameColumn = element('col'); nameColumn.style.width = '32%'; colgroup.append(nameColumn, element('col')); table.append(colgroup);
    const head = element('thead'), header = element('tr'); cell(header, t('settings.parameter'), 'th'); cell(header, t('settings.value'), 'th'); head.append(header);
    const body = element('tbody');
    const parameters = [[t('forms.field.name'), connection.name], [t('forms.field.module'), moduleName(connection)], [t('forms.field.connectionEnabled'), enabledName(connection)],
      ...Object.entries(connection.settings || {}).map(([name, value]) => [parameterKeys[name] ? t(parameterKeys[name]) : name, safeValue(value, name)])];
    for (const [caption, value] of parameters) { const row = element('tr'); cell(row, caption, 'th').scope = 'row'; cell(row, value); body.append(row); }
    table.append(head, body); scroll.append(table); host.append(scroll);
  }
  function render() {
    host.replaceChildren();
    const connection = (config.connections || []).find(item => item.id === selected);
    if (selected && connection) detail(connection);
    else { selected = null; overview(); }
  }
  onLanguageChange(render);
  return { show(id, nextConfig, nextModules) { selected = id ?? null; config = nextConfig; modules = nextModules; render(); } };
}
