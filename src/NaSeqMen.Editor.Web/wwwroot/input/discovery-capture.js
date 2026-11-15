// SPDX-License-Identifier: MIT
export class DiscoveryCapture {
  constructor(request, onActive, onError) {
    this.request = request;
    this.onActive = onActive;
    this.onError = onError;
    this.owner = crypto.randomUUID();
    this.interfaces = [];
    this.active = [];
    this.sent = false;
    this.pending = false;
    this.closing = false;
    this.reportedError = false;
    for (const event of ['focus', 'blur']) window.addEventListener(event, () => this.sync());
    document.addEventListener('visibilitychange', () => this.sync());
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

  async sync() {
    const interfaces = document.hasFocus() && !document.hidden && !this.closing ? this.interfaces : [];
    if (this.pending || (!this.sent && interfaces.length === 0)) return;
    this.pending = true;
    this.sent = interfaces.length > 0;
    try {
      this.setActive(await this.request({ owner: this.owner, interfaces }));
      this.reportedError = false;
    } catch (error) {
      this.setActive([]);
      if (!this.reportedError && !this.closing) this.onError(error);
      this.reportedError = true;
    } finally {
      this.pending = false;
      const desired = document.hasFocus() && !document.hidden && !this.closing ? this.interfaces : [];
      if (desired !== interfaces && (desired.length || interfaces.length)) this.sync();
    }
  }

  setActive(interfaces) {
    if (interfaces.length === this.active.length && interfaces.every(item => this.active.includes(item))) return;
    this.active = interfaces;
    this.onActive(new Set(interfaces));
  }
}
