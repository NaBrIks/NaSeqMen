// SPDX-License-Identifier: MIT
export const keyboardCaptureSource = '@discovery-keyboard';

export class DiscoveryCapture {
  constructor(request, onActive, onError) {
    this.request = request;
    this.onActive = onActive;
    this.onError = onError;
    this.owner = crypto.randomUUID();
    this.interfaces = [];
    this.active = [];
    this.sent = false;
    this.pending = null;
    this.recording = null;
    this.closing = false;
    this.reportedError = false;
    for (const event of ['focus', 'blur']) window.addEventListener(event, () => this.sync());
    document.addEventListener('visibilitychange', () => this.sync());
    document.addEventListener('naseqmen:form-state', () => this.sync());
    window.addEventListener('pagehide', () => {
      this.closing = true;
      this.sync();
    });
    setInterval(() => this.sync(), 800);
  }

  select(interfaces) {
    this.interfaces = interfaces;
    this.reportedError = false;
    this.sync();
  }

  get allowEditing() { return !this.recording && !!document.querySelector('dialog.form-dialog[open]'); }

  desired() {
    const interfaces = document.hasFocus() && !document.hidden && !this.closing
      ? (this.recording ? [...new Set([...this.interfaces, this.recording])] : this.interfaces) : [];
    return { owner: this.owner, interfaces, allowEditing: interfaces.length > 0 && this.allowEditing };
  }

  async startRecording(source) {
    this.recording = source;
    while (this.pending) await this.pending;
    if (this.recording !== source) return false;
    const active = await this.sync();
    return this.recording === source && active.includes(keyboardCaptureSource);
  }

  stopRecording() {
    this.recording = null;
    this.sync();
  }

  sync() {
    if (this.pending) return this.pending;
    const current = this.desired();
    if (!this.sent && !current.interfaces.length) return Promise.resolve([]);
    this.sent = current.interfaces.length > 0;
    this.pending = this.send(current).finally(() => {
      this.pending = null;
      if (JSON.stringify(current) !== JSON.stringify(this.desired())) this.sync();
    });
    return this.pending;
  }

  async send(current) {
    try {
      const active = await this.request(current);
      this.setActive(active);
      this.reportedError = false;
      return active;
    } catch (error) {
      this.setActive([]);
      if (!this.reportedError && !this.closing) this.onError(error);
      this.reportedError = true;
      return [];
    }
  }

  setActive(interfaces) {
    if (interfaces.length === this.active.length && interfaces.every(item => this.active.includes(item))) return;
    this.active = interfaces;
    this.onActive(new Set(interfaces));
  }
}
