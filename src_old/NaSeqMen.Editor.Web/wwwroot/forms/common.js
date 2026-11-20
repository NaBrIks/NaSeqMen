// SPDX-License-Identifier: MIT
import { t, getLanguage, onLanguageChange, hasTranslation } from '../i18n.js';
const bindings = new Map();
function forget(root) { for (const node of bindings.keys()) if (node === root || root.contains(node)) bindings.delete(node); }
function bind(node, key, source) {
  if (!bindings.has(node)) bindings.set(node, new Map());
  bindings.get(node).set(key, source);
  const value = typeof source === 'function' ? source() : t(source);
  if (key === 'text') node.textContent = value;
  else node.setAttribute(key, value);
}
const hint = (node, source) => bind(node, 'data-tooltip', source);
function literal(node, key, value) {
  bindings.get(node)?.delete(key);
  if (key === 'text') node.textContent = value;
  else node.setAttribute(key, value);
}
class FormError extends Error {
  constructor(key) { super(key); this.translationKey = key; }
}
const choices = (values, prefix) => values.map(value => [value, prefix + (value || 'any')]);
const descriptorChoice = item => item.nameKey && hasTranslation(item.nameKey) ? [item.id, item.nameKey, true] : [item.id, item.name, false];
onLanguageChange(() => {
  for (const [node, values] of bindings) {
    if (!node.isConnected) { bindings.delete(node); continue; }
    for (const [key, source] of values) bind(node, key, source);
  }
  for (const dialog of document.querySelectorAll('.form-dialog')) dialog.lang = getLanguage();
});
const el = (tag, className, text, translate = true) => {
  const node = document.createElement(tag);
  if (className) node.className = className;
  if (text != null) { if (translate && text !== '') bind(node, 'text', text); else node.textContent = text; }
  return node;
};
function normalizeOptions(options) { return options.map(option => Array.isArray(option) ? option : [option, option, false]); }
function setOptions(control, options, selected = '') {
  const normalized = normalizeOptions(options);
  if (selected && !normalized.some(([value]) => value === selected)) normalized.push([selected, selected, false]);
  for (const child of control.children) forget(child);
  control.classList.toggle('form-choices-long', normalized.length > 8);
  control.replaceChildren(...normalized.map(([value, caption, translate = true]) => {
    const option = el('label', 'form-choice');
    const radio = el('input');
    radio.type = 'radio';
    radio.name = control.dataset.field;
    radio.value = value;
    option.append(radio, el('span', '', caption || 'forms.placeholder.none', caption ? translate : true));
    return option;
  }));
  const radio = [...control.querySelectorAll('input')].find(input => input.value === selected) ??
    control.querySelector('input');
  if (radio) radio.checked = true;
}

function radioGroup(key, labelId) {
  const group = el('fieldset', 'form-choices');
  group.dataset.field = key;
  group.setAttribute('aria-labelledby', labelId);
  Object.defineProperty(group, 'value', {
    get: () => group.querySelector('input:checked')?.value ?? ''
  });
  return group;
}

function field(grid, key, label, value = '', options = {}) {
  const row = el('div', 'form-row');
  const radios = options.options && !options.dropdown;
  const caption = el(radios ? 'span' : 'label', 'form-label', label, !options.literalLabel);
  const id = 'form-' + crypto.randomUUID();
  caption.id = id + '-caption';
  const input = radios ? radioGroup(key, caption.id)
    : el(options.dropdown ? 'select' : options.multiline ? 'textarea' : 'input', 'form-control');
  input.id = id;
  if (!radios) caption.htmlFor = id;
  input.name = key;
  if (input.tagName === 'INPUT') input.type = options.checkbox ? 'checkbox' : options.secret ? 'password' : 'text';
  if (radios) setOptions(input, options.options, value);
  else if (options.dropdown) {
    const choices = normalizeOptions(options.options);
    if (value && !choices.some(([id]) => id === value)) choices.push([value, value, false]);
    for (const [id, label, translate = true] of choices) {
      const option = el('option', '', label, translate);
      option.value = id;
      input.append(option);
    }
    if (choices.some(([id]) => id === value)) input.value = value;
  }
  else if (options.checkbox) input.checked = !!value;
  else input.value = value ?? '';
  if (options.required) {
    if (radios) input.dataset.required = 'true';
    else input.required = true;
  }
  if (options.readOnly) input.readOnly = true;
  if (options.multiline) { input.rows = options.rows || 7; input.spellcheck = false; input.classList.add('form-code'); }
  input.autocomplete = 'off';
  const help = options.hint || (!options.literalLabel ? label : null);
  for (const node of [input, caption]) { if (help) hint(node, help); else literal(node, 'data-tooltip', label); }
  row.append(caption, input); grid.append(row);
  return input;
}
function modal(title, saveLabel = 'forms.action.save') {
  const dialog = el('dialog', 'form-dialog');
  dialog.lang = getLanguage();
  const returnFocus = document.activeElement;
  const form = el('form', 'form-shell');
  form.noValidate = true;
  const head = el('header', 'form-heading');
  const heading = el('h2', '', title); heading.id = 'form-heading-' + crypto.randomUUID();
  dialog.setAttribute('aria-labelledby', heading.id);
  const close = el('button', 'form-close'); close.append(el('i', 'ti ti-x')); close.firstChild.setAttribute('aria-hidden', 'true'); close.type = 'button'; hint(close, 'forms.help.close'); bind(close, 'aria-label', 'forms.action.close');
  head.append(heading, close);
  const body = el('div', 'form-body');
  const footer = el('footer', 'form-footer');
  const status = el('button', 'form-status', ''); status.type = 'button'; hint(status, 'forms.help.result');
  status.setAttribute('aria-live', 'polite');
  status.addEventListener('click', () => { if (status.textContent) showDetails(t('forms.title.result'), status.dataset.tooltip); });
  const cancel = el('button', '', 'forms.action.cancel'); cancel.type = 'button'; hint(cancel, 'forms.help.cancel');
  const save = el('button', '', saveLabel); save.type = 'submit'; hint(save, saveLabel === 'forms.action.inject' ? 'forms.help.inject' : 'forms.help.save');
  footer.append(status, cancel, save); form.append(head, body, footer); dialog.append(form);
  let saved = false, busy = false;
  const done = new Promise(resolve => {
    dialog.addEventListener('close', () => {
      forget(dialog); dialog.remove();
      document.dispatchEvent(new Event('naseqmen:form-state'));
      returnFocus?.isConnected && returnFocus.focus(); resolve(saved);
    }, { once: true });
  });
  close.addEventListener('click', () => { if (!busy) dialog.close(); });
  cancel.addEventListener('click', () => { if (!busy) dialog.close(); });
  dialog.addEventListener('cancel', event => { if (busy) event.preventDefault(); });
  const open = callback => {
    form.addEventListener('submit', async event => {
      event.preventDefault(); if (busy) return;
      busy = true; save.disabled = true; cancel.disabled = true; close.disabled = true;
      literal(status, 'text', ''); literal(status, 'data-tooltip', '');
      try {
        let missing = null;
        for (const input of form.querySelectorAll('[required],[data-required]')) {
          const invalid = !input.value.trim();
          input.setAttribute('aria-invalid', String(invalid));
          if (invalid && !missing) missing = input;
        }
        if (missing) { (missing.querySelector('input') || missing).focus(); throw new FormError('forms.error.required'); }
        await callback(); saved = true; dialog.close();
      }
      catch (error) {
        if (error instanceof FormError) { bind(status, 'text', error.translationKey); hint(status, error.translationKey); }
        else { const message = () => error?.message || String(error); bind(status, 'text', message); bind(status, 'data-tooltip', message); }
      }
      finally { busy = false; save.disabled = false; cancel.disabled = false; close.disabled = false; }
    });
    (document.querySelector('.lu-shell') || document.body).append(dialog); dialog.showModal();
    document.dispatchEvent(new Event('naseqmen:form-state'));
    (body.querySelector('input[type=text],textarea') || body.querySelector('input:checked,input'))?.focus();
    return done;
  };
  return { dialog, body, open };
}
function replaceById(list, entry) {
  const index = list.findIndex(item => item.id === entry.id);
  if (index < 0) list.push(entry); else list[index] = entry;
}

function showDetails(title, text) {
  const dialog = el('dialog', 'form-dialog form-details');
  dialog.lang = getLanguage();
  const returnFocus = document.activeElement;
  const shell = el('div', 'form-shell'); const head = el('header', 'form-heading');
  const caption = el('h2', '', title, false); caption.id = 'form-details-' + crypto.randomUUID(); dialog.setAttribute('aria-labelledby', caption.id);
  const close = el('button', 'form-close'); close.append(el('i', 'ti ti-x')); close.firstChild.setAttribute('aria-hidden', 'true'); close.type = 'button'; hint(close, 'forms.action.close'); bind(close, 'aria-label', 'forms.action.close'); head.append(caption, close);
  const area = el('textarea', 'form-detail-text'); area.value = String(text ?? ''); area.readOnly = true; area.setAttribute('aria-label', title);
  shell.append(head, area); dialog.append(shell); (document.querySelector('.lu-shell') || document.body).append(dialog);
  close.addEventListener('click', () => dialog.close()); dialog.addEventListener('close', () => { forget(dialog); dialog.remove(); returnFocus?.isConnected && returnFocus.focus(); }, { once: true });
  dialog.showModal(); area.focus();
  return dialog;
}

export { bind, el, field, modal, forget, setOptions, choices, descriptorChoice, replaceById, FormError, showDetails };
