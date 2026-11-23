const NodeWidgets = {
  create(node, desc, interactFn) {
    const kind = desc?.widgetKind;
    if (!kind) return null;

    const host = document.createElement("div");
    host.className = `node-widget node-widget-${kind}`;
    host.dataset.widgetNodeId = node.id;
    host.dataset.widgetKind = kind;
    host.addEventListener("mousedown", (e) => e.stopPropagation());

    if (kind === "button") {
      this._buildButton(host, node, interactFn);
    } else if (kind === "slider") {
      this._buildSlider(host, node, interactFn);
    } else if (kind === "vu-meter") {
      this._buildVuMeter(host, node);
    } else if (kind === "obs-status") {
      this._buildObsStatus(host, node, interactFn);
    } else if (kind === "interception-device") {
      this._buildInterceptionStatus(host, node, interactFn);
    } else if (kind === "hotkey-switch") {
      this._buildHotkeySwitch(host, node);
    } else if (kind === "input-probe") {
      this._buildInputProbe(host, node, interactFn);
    } else {
      return null;
    }

    return host;
  },

  _buildButton(host, node, interactFn) {
    const btn = document.createElement("button");
    btn.type = "button";
    btn.className = "vbtn";
    btn.textContent = node.parameters?.buttonLabel || "Press";

    const mode = () => (node.parameters?.mode || "momentary").toLowerCase();
    let pressed = false;

    btn.addEventListener("pointerdown", (e) => {
      e.stopPropagation();
      if (mode() === "momentary") {
        pressed = true;
        btn.classList.add("is-active");
        interactFn(node.id, "press");
      }
    });

    const releaseIfMomentary = () => {
      if (pressed && mode() === "momentary") {
        pressed = false;
        btn.classList.remove("is-active");
        interactFn(node.id, "release");
      }
    };

    btn.addEventListener("pointerup", releaseIfMomentary);
    btn.addEventListener("pointerleave", releaseIfMomentary);
    btn.addEventListener("click", (e) => {
      e.stopPropagation();
      if (mode() === "toggle") {
        interactFn(node.id, "press");
      }
    });

    host.appendChild(btn);
  },

  _buildSlider(host, node, interactFn) {
    const min = Number(node.parameters?.min ?? 0);
    const max = Number(node.parameters?.max ?? 100);
    const step = Number(node.parameters?.step ?? 1) || 1;
    const val = Number(node.parameters?.defaultValue ?? min);
    const unit = node.parameters?.unit ?? "%";

    const row = document.createElement("div");
    row.className = "vslider-row";

    const range = document.createElement("input");
    range.type = "range";
    range.className = "vslider-range";
    range.min = String(min);
    range.max = String(max);
    range.step = String(step);
    range.value = String(val);

    const readout = document.createElement("span");
    readout.className = "vslider-val";
    readout.textContent = `${val}${unit}`;

    let lastSent = 0;
    let pendingTimer = null;

    const sendNow = () => {
      lastSent = performance.now();
      interactFn(node.id, "change", { value: Number(range.value) });
    };

    range.addEventListener("pointerdown", () => { range.dataset.dragging = "1"; });
    window.addEventListener("pointerup", () => { delete range.dataset.dragging; });

    range.addEventListener("input", () => {
      const u = node.parameters?.unit ?? "";
      readout.textContent = `${range.value}${u}`;
      const elapsed = performance.now() - lastSent;
      if (elapsed >= 35) {
        clearTimeout(pendingTimer);
        sendNow();
      } else {
        clearTimeout(pendingTimer);
        pendingTimer = setTimeout(sendNow, 35 - elapsed);
      }
    });

    range.addEventListener("change", () => {
      clearTimeout(pendingTimer);
      sendNow();
    });

    row.append(range, readout);
    host.appendChild(row);
  },

  _buildVuMeter(host, node) {
    const unit = node.parameters?.unit ?? "%";
    host.innerHTML = `
      <div class="vu-head">
        <span class="vu-source">—</span>
        <span class="vu-readout">0${unit}</span>
      </div>
      <div class="vu-track"><div class="vu-fill vu-left" style="width:0%"></div><div class="vu-peak" style="left:0%"></div></div>
      <div class="vu-track"><div class="vu-fill vu-right" style="width:0%"></div><div class="vu-peak vu-peak-r" style="left:0%"></div></div>
    `;
  },

  _buildObsStatus(host, node, interactFn) {
    host.innerHTML = `
      <div class="obs-widget-row">
        <span class="obs-dot"></span>
        <span class="obs-text">Offline</span>
        <button type="button" class="mini-btn obs-refresh" title="Refresh OBS scenes & inputs">↻</button>
      </div>
      <div class="obs-scene"></div>
    `;
    host.querySelector(".obs-refresh").addEventListener("click", (e) => {
      e.stopPropagation();
      interactFn(node.id, "refresh");
    });
  },

  _buildInterceptionStatus(host, node, interactFn) {
    host.innerHTML = `
      <div class="ic-widget-row">
        <span class="ic-dot"></span>
        <span class="ic-text">Idle</span>
      </div>
      <div class="ic-actions">
        <button type="button" class="mini-btn ic-scan">Scan</button>
        <button type="button" class="mini-btn danger ic-panic" title="Emergency release all filters">Release</button>
      </div>
    `;
    host.querySelector(".ic-scan").addEventListener("click", (e) => {
      e.stopPropagation();
      interactFn(node.id, "refreshDevices");
    });
    host.querySelector(".ic-panic").addEventListener("click", (e) => {
      e.stopPropagation();
      interactFn(node.id, "panicRelease");
    });
  },

  _buildHotkeySwitch(host, node) {
    const rules = Array.isArray(node.parameters?.rules) ? node.parameters.rules.filter((r) => r?.enabled !== false && r?.from) : [];
    const lines = rules.slice(0, 4).map((r) => {
      const target = r.mode === "suppressOnly" || !r.to ? "block" : r.to;
      return `<div class="hsw-line"><span class="hsw-from">${r.from}</span><span class="hsw-arr">→</span><span class="hsw-to">${target}</span></div>`;
    }).join("");
    const extra = rules.length > 4 ? `<div class="hsw-more">+${rules.length - 4} more…</div>` : "";
    host.innerHTML = `
      <div class="hsw-list">${lines || '<div class="hsw-more">Add rules in Inspector</div>'}${extra}</div>
      <div class="hsw-last"></div>
    `;
  },

  _buildInputProbe(host, node, interactFn) {
    host.innerHTML = `
      <div class="probe-controls">
        <button type="button" class="probe-arm-btn">Arm Capture</button>
        <button type="button" class="mini-btn probe-clear" title="Clear terminal">Clear</button>
      </div>
      <div class="probe-term"><div class="probe-empty">Waiting for strokes… (Esc disarms)</div></div>
    `;
    host.querySelector(".probe-arm-btn").addEventListener("click", (e) => {
      e.stopPropagation();
      interactFn(node.id, "toggle");
    });
    host.querySelector(".probe-clear").addEventListener("click", (e) => {
      e.stopPropagation();
      interactFn(node.id, "clear");
    });
  },

  applyState(nodeId, state) {
    const host = document.querySelector(`[data-widget-node-id="${nodeId}"]`);
    if (!host || !state) return;
    const kind = host.dataset.widgetKind;

    if (kind === "button") {
      const btn = host.querySelector(".vbtn");
      if (btn) {
        btn.classList.toggle("is-active", Boolean(state.active));
        if (state.label) btn.textContent = state.label;
      }
    } else if (kind === "slider") {
      const range = host.querySelector(".vslider-range");
      const readout = host.querySelector(".vslider-val");
      if (range && !range.dataset.dragging && typeof state.value === "number") {
        if (typeof state.min === "number") range.min = String(state.min);
        if (typeof state.max === "number") range.max = String(state.max);
        if (typeof state.step === "number") range.step = String(state.step);
        range.value = String(state.value);
      }
      if (readout && typeof state.value === "number") {
        readout.textContent = `${state.value}${state.unit ?? ""}`;
      }
    } else if (kind === "vu-meter") {
      const left = Math.max(0, Math.min(100, Number(state.left ?? state.level ?? 0)));
      const right = Math.max(0, Math.min(100, Number(state.right ?? state.level ?? 0)));
      const peak = Math.max(0, Math.min(100, Number(state.peak ?? left)));

      const fillL = host.querySelector(".vu-left");
      const fillR = host.querySelector(".vu-right");
      const peakL = host.querySelector(".vu-peak");
      const peakR = host.querySelector(".vu-peak-r");
      const srcEl = host.querySelector(".vu-source");
      const readEl = host.querySelector(".vu-readout");

      if (fillL) fillL.style.width = `${left}%`;
      if (fillR) fillR.style.width = `${right}%`;
      if (peakL) peakL.style.left = `${peak}%`;
      if (peakR) peakR.style.left = `${peak}%`;
      if (srcEl) srcEl.textContent = state.source || "Signal";
      if (readEl) {
        readEl.textContent = state.db !== null && state.db !== undefined
          ? `${Number(state.db).toFixed(1)} dB`
          : `${Math.round(Number(state.level ?? 0))}${state.unit ?? "%"}`;
      }
    } else if (kind === "obs-status") {
      const dot = host.querySelector(".obs-dot");
      const txt = host.querySelector(".obs-text");
      const sceneEl = host.querySelector(".obs-scene");
      if (dot) {
        dot.classList.toggle("ok", Boolean(state.connected));
        dot.classList.toggle("err", Boolean(state.error));
      }
      if (txt) {
        txt.textContent = state.connected
          ? `Connected${state.obsVersion ? " v" + state.obsVersion : ""}`
          : (state.error || "Offline");
      }
      if (sceneEl) {
        sceneEl.textContent = state.scene ? `Scene: ${state.scene}` : "";
      }
    } else if (kind === "interception-device") {
      const dot = host.querySelector(".ic-dot");
      const txt = host.querySelector(".ic-text");
      if (dot) {
        dot.classList.toggle("ok", Boolean(state.driverAvailable && state.active && !state.panicTriggered));
        dot.classList.toggle("err", Boolean(state.panicTriggered || (!state.driverAvailable && state.active)));
      }
      if (txt) {
        if (state.panicTriggered) txt.textContent = "PANIC: Released";
        else if (!state.driverAvailable) txt.textContent = "DLL/Driver missing";
        else if (state.mode === "hub" && state.active) {
          const chCount = Object.keys(state.boundSlots || {}).length;
          txt.textContent = state.monitorAll ? `Hooked: ALL (${chCount} ch)` : `Hooked: ${chCount} ch`;
        }
        else if (state.resolvedSlot > 0) txt.textContent = `Hooked slot #${state.resolvedSlot}`;
        else if (state.active) txt.textContent = "Driver OK (unmatched)";
        else txt.textContent = `Driver OK (${(state.devices || []).length} dev)`;
      }
    } else if (kind === "hotkey-switch") {
      const lastEl = host.querySelector(".hsw-last");
      if (lastEl) {
        lastEl.textContent = state.lastMatch ? `Last: ${state.lastMatch}` : `${state.ruleCount ?? 0} active rules`;
      }
    } else if (kind === "input-probe") {
      const btn = host.querySelector(".probe-arm-btn");
      const term = host.querySelector(".probe-term");
      if (btn) {
        btn.classList.toggle("is-armed", Boolean(state.armed));
        btn.classList.toggle("is-blocking", Boolean(state.blocking));
        if (state.armed) {
          btn.textContent = state.blocking
            ? `● BLOCKING (${state.remainingSec ?? 0}s) — Stop`
            : `● Armed (${state.remainingSec ?? 0}s) — Stop`;
        } else {
          btn.textContent = state.reason ? `Arm (${state.reason})` : "Arm Capture";
        }
      }
      if (term && Array.isArray(state.lines)) {
        if (state.lines.length === 0) {
          term.innerHTML = `<div class="probe-empty">Waiting for strokes… (Esc disarms)</div>`;
        } else {
          term.innerHTML = state.lines.map((l) => `
            <div class="probe-line${l.blocked ? " is-blocked" : ""}">
              <span class="probe-dev">${l.device || ""}</span>
              <span class="probe-ph">${l.phase || ""}</span>
              <span class="probe-key">${l.key || ""}</span>
              <span class="probe-ex">${l.extra || ""}</span>
              ${l.blocked ? '<span class="probe-blk">BLK</span>' : ""}
            </div>
          `).join("");
          term.scrollTop = term.scrollHeight;
        }
      }
    }
  },
};
