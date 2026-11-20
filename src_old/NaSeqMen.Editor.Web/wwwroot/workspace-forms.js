// SPDX-License-Identifier: MIT
import { scanCodeLabel } from './workspace-presentation.js';
import { knownKeyControls } from './input/key-labels.js';
// Builds ordinary controls from module-owned parameter descriptors.
export function parameterForm(parameters, settings, { tr, html, project, translate, connection }) {
  const element = html('section', undefined, 'workspace-parameter-form');
  const entries = [];
  const labelFor = p => p.labelKey ? translate(p.labelKey, p.id) : tr(p.id) === 'workspace.' + p.id ? p.id : tr(p.id);
  function control(p, value, record = settings) {
    const container = html('div', undefined, 'workspace-parameter');
    const label = html('label', labelFor(p)); container.append(label);
    let read;
    if (p.type === 'any') {
      const kind = html('select');
      for (const id of ['text', 'number', 'boolean', 'object']) { const option = html('option', tr('valueType.' + id)); option.value = id; kind.append(option); }
      kind.value = typeof value === 'string' ? 'text' : typeof value === 'number' ? 'number' : typeof value === 'boolean' ? 'boolean' : 'object';
      const input = html('input'); input.value = value === undefined ? '' : typeof value === 'string' ? value : JSON.stringify(value);
      label.append(kind, input);
      read = () => kind.value === 'text' ? input.value : kind.value === 'number' ? Number(input.value) : JSON.parse(input.value);
    } else if (p.type === 'array' && p.properties?.length) {
      const rows = html('div', undefined, 'workspace-array'); const getters = [];
      function add(item = {}) {
        const row = html('div', undefined, 'workspace-array-row');
        const children = p.properties.map(child => { const c = control(child, item[child.id] ?? child.defaultValue, item); row.append(c.container); return [child.id, c]; });
        const remove = html('button', '×'); remove.type = 'button'; remove.setAttribute('aria-label', tr('remove'));
        const get = () => ({...item, ...Object.fromEntries(children.map(([key,c]) => [key,c.read()]))}); getters.push(get);
        remove.addEventListener('click', () => { getters.splice(getters.indexOf(get), 1); row.remove(); }); row.append(remove); rows.append(row);
      }
      (Array.isArray(value) ? value : []).forEach(add);
      const plus = html('button', '+ ' + tr('addRow')); plus.type = 'button'; plus.addEventListener('click', () => add());
      container.append(rows, plus); read = () => getters.map(get => get());
    } else {
      let input;
      const resourceOptions = p.resource === 'connection.devices'
        ? (connection?.settings?.devices ?? []).map(device => ({value:device.id,label:device.name || device.id})) : null;
      const options = ['page', 'pages'].includes(p.resource) ? project.pages.map(page => ({value:page.id,label:page.name})) : resourceOptions ?? p.options;
      if (options?.length) {
        const entries = options.map(option => ({value: typeof option === 'object' ? option.value ?? option.id : option, label: typeof option === 'object' ? option.label ?? option.value ?? option.id : option}));
        const optionLabel = entry => p.resource ? String(entry.label) : translate('workspace.' + p.id + '.' + entry.label,
          translate('workspace.' + entry.label, String(entry.label || '—')));
        if (p.resource && value !== undefined && value !== null && !entries.some(entry => entry.value === value))
          entries.unshift({value, label: value + ' (' + tr('unavailable') + ')'});
        if (options.length <= 4 && !p.resource) {
          input = html('div', undefined, 'workspace-radio-list'); const group = crypto.randomUUID();
          entries.forEach((entry, index) => {
            const choice = html('label'); const radio = html('input'); radio.type = 'radio'; radio.name = group;
            radio.value = String(index); radio.checked = value === entry.value || value === undefined && index === 0;
            choice.append(radio, document.createTextNode(optionLabel(entry))); input.append(choice);
          });
          read = () => entries[Number(input.querySelector(':checked')?.value ?? 0)].value;
        } else {
          input = html('select');
          entries.forEach((entry, index) => { const o = html('option', optionLabel(entry)); o.value = String(index); input.append(o); });
          input.value = String(Math.max(0, entries.findIndex(entry => entry.value === value)));
          read = () => entries[Number(input.value)].value;
        }
      } else if (p.type === 'boolean') {
        input = html('input'); input.type = 'checkbox'; input.checked = value === true; read = () => input.checked;
      } else if (p.type === 'number' || p.type === 'integer') {
        input = html('input'); input.type = 'number'; input.step = p.type === 'integer' ? '1' : 'any'; input.value = value ?? ''; read = () => input.value === '' ? null : Number(input.value);
      } else if (p.type === 'array' && p.itemType === 'text') {
        input = html('input'); input.value = Array.isArray(value) ? value.map(v => typeof v === 'string' ? v : v.id + ':' + (v.type ?? 'any')).join(', ') : ''; read = () => input.value.split(',').map(x => x.trim()).filter(Boolean).map(x => x.includes(':') ? {id:x.split(':')[0].trim(),type:x.split(':')[1].trim()} : x);
      } else if (p.type === 'array' || p.type === 'object' || p.type === 'any' || p.type === 'json') {
        input = html('textarea'); input.value = JSON.stringify(value ?? (p.type === 'array' ? [] : {}), null, 2); read = () => JSON.parse(input.value);
      } else if (p.type === 'code' || p.id === 'code') {
        input = html('textarea'); input.classList.add('workspace-code'); input.value = value ?? ''; read = () => input.value;
      } else {
        input = html('input'); input.value = value ?? ''; read = () => input.value;
      }
      input.required = Boolean(p.required); input.classList.add('form-control'); label.append(input);
      if (p.format === 'scan-code') {
        const known = html('datalist'); known.id = 'keys-' + crypto.randomUUID();
        const prefix = record.flags & 2 ? 'E0:' : record.flags & 4 ? 'E1:' : '';
        for (const [key, name] of knownKeyControls()) {
          const raw = key.slice(4); const extended = raw.includes(':') ? raw.split(':')[0] + ':' : '';
          if (extended !== prefix) continue;
          const option = html('option'); option.value = parseInt(raw.split(':').at(-1), 16); option.label = name; known.append(option);
        }
        input.setAttribute('list', known.id); container.append(known);
        const display = html('output', '', 'workspace-key-name'); label.append(display);
        const update = () => { display.textContent = input.value === '' ? '' : scanCodeLabel(Number(input.value), record.flags ?? 0); };
        input.addEventListener('input', update); update();
      }
    }
    if (p.descriptionKey) container.dataset.tooltip = translate(p.descriptionKey, p.id);
    return {container, read};
  }
  for (const parameter of parameters) { const c = control(parameter, settings[parameter.id] ?? parameter.defaultValue); entries.push([parameter.id,c]); element.append(c.container); }
  const advanced = html('details'); const summary = html('summary', tr('advanced')); advanced.append(summary);
  const extra = Object.fromEntries(Object.entries(settings).filter(([key]) => !parameters.some(p => p.id === key)));
  const raw = html('textarea'); raw.value = JSON.stringify(extra, null, 2); raw.setAttribute('aria-label', tr('advanced')); advanced.append(raw); element.append(advanced);
  return {element, read() { const extra = JSON.parse(raw.value); if (!extra || typeof extra !== 'object' || Array.isArray(extra)) throw Error(tr('settings')); return {...extra, ...Object.fromEntries(entries.map(([key,c]) => [key,c.read()]))}; }};
}
