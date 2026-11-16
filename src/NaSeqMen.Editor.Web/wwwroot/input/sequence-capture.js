// SPDX-License-Identifier: MIT
import { t, onLanguageChange } from '../i18n.js';
import { keyboardCaptureSource } from './discovery-capture.js';

export class SequenceCapture {
  #listeners = new Set();

  constructor(discovery) { this.discovery = discovery; }

  accept(signal) {
    for (const listener of this.#listeners) listener(signal);
  }

  bind(dialog, container, field, endpoint) {
    const button = document.createElement('button');
    button.type = 'button';
    const status = document.createElement('span');
    status.className = 'sequence-capture-status';
    status.setAttribute('aria-live', 'polite');
    container.append(button, status);
    let timer, listener, message = '';
    const render = () => {
      button.textContent = t(listener ? 'forms.capture.cancel' : 'forms.capture.start');
      status.textContent = message ? t(message) : '';
    };
    const stop = key => {
      if (listener) {
        this.#listeners.delete(listener);
        this.discovery.stopRecording();
      }
      listener = null;
      clearTimeout(timer);
      message = key;
      render();
    };
    const unsubscribe = onLanguageChange(render);
    dialog.addEventListener('close', () => { stop(''); unsubscribe(); }, { once: true });
    button.addEventListener('click', async () => {
      if (listener) { stop(''); return; }
      const target = endpoint();
      if (!target.source || !target.connectionId) { stop('forms.capture.chooseSource'); return; }
      const steps = [];
      const held = new Set();
      let armed = false;
      message = 'forms.capture.preparing';
      listener = signal => {
        if (!button.isConnected) { stop(''); return; }
        if (!armed || signal.module !== 'windows.input' || (signal.source !== keyboardCaptureSource &&
            (signal.source !== target.source || signal.connectionId !== target.connectionId))) return;
        if (signal.control === 'device:connection') { stop('forms.capture.incomplete'); return; }
        if (!signal.control.startsWith('key:')) return;
        if (signal.phase === 'down') {
          if (held.has(signal.control)) return;
          held.add(signal.control);
        } else if (signal.phase !== 'up' || !held.delete(signal.control)) return;
        steps.push(signal.control + ' ' + signal.phase);
        if (steps.length > 16) { stop('forms.capture.incomplete'); return; }
        if (held.size === 0) {
          field.value = steps.join('\n');
          stop('forms.capture.complete');
        }
      };
      const current = listener;
      this.#listeners.add(listener);
      render();
      const ready = await this.discovery.startRecording(target.source);
      if (listener !== current) return;
      if (!ready) { stop('forms.capture.unavailable'); return; }
      armed = true;
      message = 'forms.capture.listening';
      timer = setTimeout(() => stop('forms.capture.incomplete'), 5000);
      render();
    });
    render();
    return () => { stop(''); unsubscribe(); };
  }
}
