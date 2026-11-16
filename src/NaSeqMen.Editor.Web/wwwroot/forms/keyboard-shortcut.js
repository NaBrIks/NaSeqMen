// SPDX-License-Identifier: MIT
import { t } from '../i18n.js';
import { bind, el } from './common.js';

const presets = [
  ['Ctrl+C', 'copy'], ['Ctrl+V', 'paste'], ['Ctrl+X', 'cut'], ['Ctrl+A', 'selectAll'],
  ['Ctrl+Z', 'undo'], ['Ctrl+Y', 'redo'], ['Ctrl+S', 'save'], ['Ctrl+F', 'find'],
  ['Alt+Tab', 'switchWindow'], ['Win+E', 'explorer'], ['Ctrl+Shift+Esc', 'taskManager'], ['Esc', 'escape']
];

const namedKeys = new Map([
  ...['Enter', 'Tab', 'Space', 'Backspace', 'Delete', 'Insert', 'Home', 'End', 'PageUp', 'PageDown', 'MediaPlayPause']
    .map(key => [key, key]),
  ['Escape', 'Esc'], ['ArrowLeft', 'Left'], ['ArrowUp', 'Up'], ['ArrowRight', 'Right'], ['ArrowDown', 'Down'],
  ['AudioVolumeMute', 'VolumeMute'], ['AudioVolumeDown', 'VolumeDown'], ['AudioVolumeUp', 'VolumeUp']
]);

function shortcut(event) {
  if (event.isComposing || event.getModifierState('AltGraph')) return null;
  const code = event.code;
  const key = /^Key[A-Z]$/.test(code) ? (/^[a-z]$/i.test(event.key) ? event.key.toUpperCase() : code.slice(3))
    : /^Digit[0-9]$/.test(code) ? code.slice(5)
    : /^F(?:[1-9]|1[0-9]|2[0-4])$/.test(code) ? code
    : namedKeys.get(code);
  if (!key) return null;
  return [event.ctrlKey && 'Ctrl', event.altKey && 'Alt', event.shiftKey && 'Shift', event.metaKey && 'Win', key]
    .filter(Boolean).join('+');
}

export function addShortcutControls(input) {
  const row = el('div', 'shortcut-control');
  const record = el('button', 'shortcut-record');
  record.type = 'button';
  const choices = el('select', 'form-control shortcut-presets');
  bind(choices, 'aria-label', 'forms.shortcut.presets');
  choices.append(el('option', '', 'forms.shortcut.presets'));
  choices.options[0].value = '';
  for (const [keys, label] of presets) {
    const option = el('option');
    option.value = keys;
    bind(option, 'text', () => t('forms.shortcut.' + label) + ' · ' + keys);
    choices.append(option);
  }
  input.replaceWith(row);
  row.append(input, record, choices);
  let recording = false;
  let capturedCode = null;
  const setRecording = active => {
    recording = active;
    input.readOnly = active;
    row.dataset.recording = String(active);
    record.setAttribute('aria-pressed', String(active));
    bind(record, 'text', active ? 'forms.shortcut.waiting' : 'forms.shortcut.record');
    bind(record, 'data-tooltip', active ? 'forms.shortcut.cancelHelp' : 'forms.shortcut.recordHelp');
  };
  const setValue = value => {
    input.value = value;
    input.dispatchEvent(new Event('input', { bubbles: true }));
  };
  choices.addEventListener('change', () => {
    if (choices.value) setValue(choices.value);
    choices.selectedIndex = 0;
  });
  record.addEventListener('pointerdown', event => {
    if (recording) event.preventDefault();
  });
  record.addEventListener('click', () => {
    setRecording(!recording);
    if (recording) input.focus();
  });
  input.addEventListener('blur', () => {
    capturedCode = null;
    if (recording) setRecording(false);
  });
  input.addEventListener('keyup', event => {
    if (event.code === capturedCode) capturedCode = null;
  });
  input.addEventListener('keydown', event => {
    if (!recording && !(event.repeat && event.code === capturedCode)) return;
    event.preventDefault();
    event.stopPropagation();
    if (event.repeat) return;
    if (event.key === 'Escape' && !event.ctrlKey && !event.altKey && !event.shiftKey && !event.metaKey) {
      setRecording(false);
      return;
    }
    const value = shortcut(event);
    if (!value) return;
    capturedCode = event.code;
    setRecording(false);
    setValue(value);
  });
  setRecording(false);
}
