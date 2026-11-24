function renderInspector(node, descriptor, callbacks) {
  const container = document.getElementById("inspector-body");
  container.innerHTML = "";

  if (!node || !descriptor) {
    container.innerHTML = `<div class="debug-empty">${t("inspector.empty")}</div>`;
    return;
  }

  // General section
  const general = document.createElement("div");
  general.className = "insp-section";
  general.innerHTML = `<div class="insp-title">${t("inspector.general")}</div>`;

  const titleField = document.createElement("div");
  titleField.className = "insp-field";
  titleField.innerHTML = `<label>${t("inspector.title")}</label>`;
  const titleInput = document.createElement("input");
  titleInput.type = "text";
  titleInput.value = node.title || descriptor.displayName;
  titleInput.addEventListener("input", () => {
    node.title = titleInput.value;
    callbacks.onChanged();
  });
  titleField.appendChild(titleInput);
  general.appendChild(titleField);

  const enabledField = document.createElement("div");
  enabledField.className = "insp-field";
  const enabledLabel = document.createElement("label");
  enabledLabel.className = "insp-check";
  const enabledCheck = document.createElement("input");
  enabledCheck.type = "checkbox";
  enabledCheck.checked = node.enabled;
  enabledCheck.addEventListener("change", () => {
    node.enabled = enabledCheck.checked;
    callbacks.onChanged();
  });
  enabledLabel.append(enabledCheck, document.createTextNode(t("inspector.enabled")));
  enabledField.appendChild(enabledLabel);
  general.appendChild(enabledField);

  if (descriptor.description) {
    const descEl = document.createElement("div");
    descEl.className = "insp-desc";
    descEl.textContent = descriptor.description;
    general.appendChild(descEl);
  }

  const btnRow = document.createElement("div");
  btnRow.className = "insp-btn-row";
  if (descriptor.supportsManualTrigger) {
    const trig = document.createElement("button");
    trig.className = "btn-secondary";
    trig.textContent = "▶ " + t("inspector.trigger");
    trig.addEventListener("click", () => callbacks.onTrigger(node.id));
    btnRow.appendChild(trig);
  }
  if (callbacks.onOpenHelp) {
    const helpBtn = document.createElement("button");
    helpBtn.className = "btn-secondary";
    helpBtn.textContent = "? " + t("help.openDocs");
    helpBtn.addEventListener("click", () => callbacks.onOpenHelp(node.typeId));
    btnRow.appendChild(helpBtn);
  }
  const del = document.createElement("button");
  del.className = "btn-secondary";
  del.style.color = "#f87171";
  del.textContent = t("inspector.delete");
  del.addEventListener("click", () => callbacks.onDelete());
  btnRow.appendChild(del);
  general.appendChild(btnRow);

  container.appendChild(general);

  // Parameters section
  const isHubNode = node.typeId === "interception.hub";
  const hubFilterKeys = isHubNode
    ? new Set(["kb1Filter", "kb2Filter", "kb3Filter", "kb4Filter", "mouse1Filter", "mouse2Filter", "mouse3Filter", "mouse4Filter"])
    : null;

  const renderParamField = (p) => {
      const field = document.createElement("div");
      field.className = "insp-field";
      const val = node.parameters?.[p.key] ?? p.defaultValue ?? "";

      if (p.kind === "boolean") {
        const lbl = document.createElement("label");
        lbl.className = "insp-check";
        const chk = document.createElement("input");
        chk.type = "checkbox";
        chk.checked = Boolean(val);
        chk.addEventListener("change", () => {
          node.parameters[p.key] = chk.checked;
          callbacks.onChanged();
        });
        lbl.append(chk, document.createTextNode(p.label));
        field.appendChild(lbl);
      } else {
        const lbl = document.createElement("label");
        lbl.textContent = p.label;
        field.appendChild(lbl);

        if (p.kind === "select") {
          const sel = document.createElement("select");
          for (const opt of p.options || []) {
            const o = document.createElement("option");
            o.value = opt.value;
            o.textContent = opt.label;
            if (String(val) === String(opt.value)) o.selected = true;
            sel.appendChild(o);
          }
          sel.addEventListener("change", () => {
            node.parameters[p.key] = sel.value;
            callbacks.onChanged();
          });
          field.appendChild(sel);
        } else if (p.kind === "multiline") {
          const ta = document.createElement("textarea");
          ta.rows = 8;
          ta.className = "insp-code";
          ta.spellcheck = false;
          ta.value = val;
          ta.addEventListener("input", () => {
            node.parameters[p.key] = ta.value;
            callbacks.onChanged();
          });
          field.appendChild(ta);
        } else if ((p.kind === "key" || p.kind === "keyCombo") && typeof KeyPicker !== "undefined") {
          const ctrl = KeyPicker.createInspectorControl(val, p.placeholder, p.kind, (nextVal) => {
            node.parameters[p.key] = nextVal;
            callbacks.onChanged();
          });
          field.appendChild(ctrl);
        } else if (p.kind === "ruleList" && typeof KeyPicker !== "undefined") {
          const ctrl = KeyPicker.createRuleListEditor(val, (nextRules) => {
            node.parameters[p.key] = nextRules;
            callbacks.onChanged();
          });
          field.appendChild(ctrl);
        } else {
          const inp = document.createElement("input");
          inp.type = p.secret ? "password" : (p.kind === "number" ? "number" : "text");
          inp.value = val;
          if (p.placeholder) inp.placeholder = p.placeholder;
          inp.addEventListener("input", () => {
            node.parameters[p.key] = p.kind === "number" ? Number(inp.value) : inp.value;
            callbacks.onChanged();
          });
          field.appendChild(inp);
        }
      }

      if (p.description) {
        const d = document.createElement("div");
        d.className = "insp-desc";
        d.textContent = p.description;
        field.appendChild(d);
      }

      return field;
  };

  // Unified visual Device & Channel Mapper for Interception Hub / Device (placed right above Parameters)
  if (descriptor.widgetKind === "interception-device") {
    const icSec = document.createElement("div");
    icSec.className = "insp-section";

    const head = document.createElement("div");
    head.className = "ic-map-head";
    const headTitle = document.createElement("div");
    headTitle.className = "insp-title";
    headTitle.style.marginBottom = "0";
    headTitle.textContent = isHubNode ? "Devices & Hub Channels" : "Connected Devices";

    const scanBtn = document.createElement("button");
    scanBtn.type = "button";
    scanBtn.className = "mini-btn";
    scanBtn.textContent = "↻ Scan";
    scanBtn.title = "Rescan connected Interception keyboards and mice";
    head.append(headTitle, scanBtn);

    const hint = document.createElement("div");
    hint.className = "insp-desc";
    hint.style.marginBottom = "6px";
    hint.textContent = isHubNode
      ? "Check a device to route it to a Hub port, or choose a specific port (KB 1–4 / Mouse 1–4). Press Deploy to activate."
      : "Select a physical keyboard or mouse below, then press Deploy to hook it.";

    const devList = document.createElement("div");
    devList.className = "ic-dev-list";

    const matchesFilter = (dev, rawFilter) => {
      const f = String(rawFilter || "").trim();
      if (!f) return false;
      if (/^\d+$/.test(f)) return Number(f) === dev.slot;
      let hwidPart = f;
      let wantInst = 1;
      const hashIdx = f.lastIndexOf("#");
      if (hashIdx > 0 && hashIdx < f.length - 1 && /^\d+$/.test(f.slice(hashIdx + 1))) {
        hwidPart = f.slice(0, hashIdx).trim();
        wantInst = Number(f.slice(hashIdx + 1));
      }
      if (hwidPart !== "*" && !dev.hardwareId.toLowerCase().includes(hwidPart.toLowerCase())) {
        return false;
      }
      return (dev.instanceIndex || 1) === wantInst;
    };

    const makeFilterValue = (dev, allDevices) => {
      const sameHwidCount = allDevices.filter(
        (d) => d.kind === dev.kind && d.hardwareId.toLowerCase() === dev.hardwareId.toLowerCase()
      ).length;
      return sameHwidCount > 1 ? `${dev.hardwareId}#${dev.instanceIndex || 1}` : dev.hardwareId;
    };

    const renderDevices = (snap) => {
      devList.innerHTML = "";
      if (!snap) {
        devList.innerHTML = `<div class="insp-desc">Scanning devices…</div>`;
        return;
      }
      if (!snap.driverAvailable) {
        devList.innerHTML = `<div class="insp-desc" style="color:#f87171">${snap.lastError || "Interception DLL / driver not available"}</div>`;
        return;
      }
      const devices = snap.devices || [];
      if (devices.length === 0) {
        devList.innerHTML = `<div class="insp-desc">No devices detected. Click ↻ Scan.</div>`;
        return;
      }

      if (isHubNode) {
        const groups = [
          { kind: "keyboard", title: "Keyboards", prefix: "kb", labelPrefix: "KB", keys: ["kb1Filter", "kb2Filter", "kb3Filter", "kb4Filter"] },
          { kind: "mouse", title: "Mice", prefix: "mouse", labelPrefix: "Mouse", keys: ["mouse1Filter", "mouse2Filter", "mouse3Filter", "mouse4Filter"] },
        ];

        for (const grp of groups) {
          const grpDevs = devices.filter((d) => d.kind === grp.kind);
          const grpLbl = document.createElement("div");
          grpLbl.className = "ic-grp-lbl";
          grpLbl.textContent = `${grp.title} (${grpDevs.length})`;
          devList.appendChild(grpLbl);

          const matchedKeys = new Set();

          for (const dev of grpDevs) {
            const assignedKey = grp.keys.find((k) => matchesFilter(dev, node.parameters?.[k])) || "";
            if (assignedKey) matchedKeys.add(assignedKey);

            const row = document.createElement("div");
            row.className = `ic-dev-row${assignedKey ? " is-assigned" : ""}`;

            const chk = document.createElement("input");
            chk.type = "checkbox";
            chk.className = "ic-dev-chk";
            chk.checked = Boolean(assignedKey);
            chk.title = assignedKey ? "Uncheck to disconnect from Hub port" : "Check to assign to next free Hub port";
            chk.addEventListener("change", () => {
              if (chk.checked) {
                const freeKey = grp.keys.find((k) => !String(node.parameters?.[k] || "").trim());
                if (!freeKey) {
                  chk.checked = false;
                  return;
                }
                node.parameters[freeKey] = makeFilterValue(dev, devices);
              } else if (assignedKey) {
                node.parameters[assignedKey] = "";
              }
              callbacks.onChanged();
              renderInspector(node, descriptor, callbacks);
            });

            const info = document.createElement("div");
            info.className = "ic-dev-info";
            const nameLine = document.createElement("div");
            nameLine.className = "ic-dev-name";
            const slotBadge = document.createElement("span");
            slotBadge.className = "ic-slot-tag";
            slotBadge.textContent = `${dev.kind === "keyboard" ? "KB" : "M"}#${dev.slot}`;
            const brandSpan = document.createElement("span");
            brandSpan.textContent = dev.displayName || dev.hardwareId;
            brandSpan.title = `${dev.displayName || ""}\n${dev.hardwareId}`;
            nameLine.append(slotBadge, brandSpan);

            const hwidLine = document.createElement("div");
            hwidLine.className = "ic-dev-hwid";
            hwidLine.textContent = dev.hardwareId;
            hwidLine.title = dev.hardwareId;
            info.append(nameLine, hwidLine);

            const sel = document.createElement("select");
            sel.className = "ic-port-sel";
            sel.title = "Choose Hub output port for this device";
            const offOpt = document.createElement("option");
            offOpt.value = "";
            offOpt.textContent = "— Off";
            if (!assignedKey) offOpt.selected = true;
            sel.appendChild(offOpt);

            grp.keys.forEach((k, i) => {
              const o = document.createElement("option");
              o.value = k;
              const occupied = Boolean(String(node.parameters?.[k] || "").trim()) && k !== assignedKey;
              o.textContent = `${grp.labelPrefix} ${i + 1}${occupied ? " •" : ""}`;
              if (k === assignedKey) o.selected = true;
              sel.appendChild(o);
            });

            sel.addEventListener("change", () => {
              const targetKey = sel.value;
              const prevTargetVal = targetKey ? String(node.parameters?.[targetKey] || "") : "";
              if (assignedKey) node.parameters[assignedKey] = "";
              if (targetKey) {
                if (prevTargetVal && assignedKey) {
                  // Swap smoothly if moving onto an already-occupied slot
                  node.parameters[assignedKey] = prevTargetVal;
                }
                node.parameters[targetKey] = makeFilterValue(dev, devices);
              }
              callbacks.onChanged();
              renderInspector(node, descriptor, callbacks);
            });

            row.append(chk, info, sel);
            devList.appendChild(row);
          }

          // Show any configured filter that didn't match a currently connected device (e.g. unplugged)
          grp.keys.forEach((k, i) => {
            const rawVal = String(node.parameters?.[k] || "").trim();
            if (!rawVal || matchedKeys.has(k)) return;

            const row = document.createElement("div");
            row.className = "ic-dev-row is-offline";

            const badge = document.createElement("span");
            badge.className = "ic-slot-tag is-warn";
            badge.textContent = `${grp.labelPrefix} ${i + 1}`;

            const info = document.createElement("div");
            info.className = "ic-dev-info";
            info.innerHTML = `<div class="ic-dev-name">Offline / Custom filter</div><div class="ic-dev-hwid">${rawVal}</div>`;

            const clr = document.createElement("button");
            clr.type = "button";
            clr.className = "mini-btn danger";
            clr.textContent = "×";
            clr.title = "Clear filter";
            clr.addEventListener("click", () => {
              node.parameters[k] = "";
              callbacks.onChanged();
              renderInspector(node, descriptor, callbacks);
            });

            row.append(badge, info, clr);
            devList.appendChild(row);
          });
        }
      } else {
        // Single Interception Device node: show selectable cards with active highlight
        const curKind = String(node.parameters?.deviceKind || "").toLowerCase();
        const curFilter = String(node.parameters?.hardwareIdFilter || "").trim();
        const curInst = Number(node.parameters?.instanceIndex || 1);

        for (const dev of devices) {
          const isSelected = curKind === dev.kind && curFilter &&
            dev.hardwareId.toLowerCase().includes(curFilter.toLowerCase()) &&
            (dev.instanceIndex || 1) === curInst;

          const row = document.createElement("button");
          row.type = "button";
          row.className = `ic-dev-row ic-dev-btn${isSelected ? " is-assigned" : ""}`;

          const badge = document.createElement("span");
          badge.className = "ic-slot-tag";
          badge.textContent = `${dev.kind === "keyboard" ? "KB" : "M"}#${dev.slot}`;

          const info = document.createElement("div");
          info.className = "ic-dev-info";
          const nameLine = document.createElement("div");
          nameLine.className = "ic-dev-name";
          nameLine.textContent = dev.displayName || dev.hardwareId;
          const hwidLine = document.createElement("div");
          hwidLine.className = "ic-dev-hwid";
          hwidLine.textContent = dev.hardwareId;
          info.append(nameLine, hwidLine);

          const mark = document.createElement("span");
          mark.className = "ic-sel-mark";
          mark.textContent = isSelected ? "● Active" : "Select";

          row.append(badge, info, mark);
          row.addEventListener("click", () => {
            node.parameters.deviceKind = dev.kind;
            node.parameters.hardwareIdFilter = dev.hardwareId;
            node.parameters.instanceIndex = dev.instanceIndex || 1;
            callbacks.onChanged();
            renderInspector(node, descriptor, callbacks);
          });
          devList.appendChild(row);
        }
      }
    };

    const cachedState = callbacks.getWidgetState ? callbacks.getWidgetState(node.id) : null;
    if (cachedState && Array.isArray(cachedState.devices) && cachedState.devices.length > 0) {
      renderDevices(cachedState);
    } else {
      renderDevices(null);
      Api.getInterceptionDevices(node.parameters?.dllPath || "").then((snap) => {
        if (devList.isConnected) renderDevices(snap);
      }).catch(() => {});
    }

    scanBtn.addEventListener("click", async () => {
      devList.innerHTML = `<div class="insp-desc">Scanning…</div>`;
      const snap = await Api.getInterceptionDevices(node.parameters?.dllPath || "");
      renderDevices(snap);
    });

    icSec.append(head, hint, devList);
    container.appendChild(icSec);
  }

  if (descriptor.parameters?.length > 0) {
    const pSec = document.createElement("div");
    pSec.className = "insp-section";
    pSec.innerHTML = `<div class="insp-title">${t("inspector.parameters")}</div>`;

    const hiddenHubParams = [];
    for (const p of descriptor.parameters) {
      if (hubFilterKeys && hubFilterKeys.has(p.key)) {
        hiddenHubParams.push(p);
        continue;
      }
      pSec.appendChild(renderParamField(p));
    }

    if (hiddenHubParams.length > 0) {
      const adv = document.createElement("details");
      adv.className = "key-builder-details";
      adv.style.marginTop = "8px";
      const sum = document.createElement("summary");
      sum.textContent = "Advanced: Raw HWID Filter Strings (KB 1–4 / Mouse 1–4)";
      adv.appendChild(sum);
      const wrap = document.createElement("div");
      wrap.style.marginTop = "6px";
      for (const p of hiddenHubParams) {
        wrap.appendChild(renderParamField(p));
      }
      adv.appendChild(wrap);
      pSec.appendChild(adv);
    }

    container.appendChild(pSec);
  }

  // Ports section
  const portsSec = document.createElement("div");
  portsSec.className = "insp-section";
  portsSec.innerHTML = `<div class="insp-title">${t("inspector.ports")}</div>`;
  const list = document.createElement("div");
  list.className = "insp-port-list";

  for (const port of [...(descriptor.inputs || []), ...(descriptor.outputs || [])]) {
    const row = document.createElement("div");
    row.className = "insp-port-row";
    const arrow = port.direction === "input" ? "→" : "←";
    row.innerHTML = `
      <span class="insp-port-dot" style="background:${getPortColor(port.dataType)}"></span>
      <span>${port.direction === "input" ? "IN" : "OUT"} ${arrow} <strong>${port.label}</strong></span>
      <span style="margin-left:auto;color:var(--text-dim);font-family:var(--font-mono);font-size:10px">${port.dataType}</span>
    `;
    list.appendChild(row);
  }

  portsSec.appendChild(list);
  container.appendChild(portsSec);

  // Live OBS scenes & audio inputs picker
  if (descriptor.widgetKind === "obs-status") {
    const obsState = callbacks.getWidgetState ? callbacks.getWidgetState(node.id) : null;
    if (obsState && ((obsState.inputs?.length > 0) || (obsState.scenes?.length > 0))) {
      const obsSec = document.createElement("div");
      obsSec.className = "insp-section";
      obsSec.innerHTML = `<div class="insp-title">OBS Discovered</div>`;

      if (obsState.inputs?.length > 0) {
        const lbl = document.createElement("div");
        lbl.className = "insp-desc";
        lbl.textContent = "Audio Inputs (click to set):";
        const chips = document.createElement("div");
        chips.className = "insp-chip-list";
        for (const name of obsState.inputs) {
          const c = document.createElement("button");
          c.type = "button";
          c.className = "insp-chip";
          c.textContent = name;
          c.addEventListener("click", () => {
            node.parameters.audioSource = name;
            callbacks.onChanged();
            renderInspector(node, descriptor, callbacks);
          });
          chips.appendChild(c);
        }
        obsSec.append(lbl, chips);
      }

      if (obsState.scenes?.length > 0) {
        const lbl = document.createElement("div");
        lbl.className = "insp-desc";
        lbl.style.marginTop = "6px";
        lbl.textContent = "Scenes (click to set default):";
        const chips = document.createElement("div");
        chips.className = "insp-chip-list";
        for (const name of obsState.scenes) {
          const c = document.createElement("button");
          c.type = "button";
          c.className = "insp-chip";
          c.textContent = name;
          c.addEventListener("click", () => {
            node.parameters.sceneName = name;
            callbacks.onChanged();
            renderInspector(node, descriptor, callbacks);
          });
          chips.appendChild(c);
        }
        obsSec.append(lbl, chips);
      }

      container.appendChild(obsSec);
    }
  }
}

function renderInfoPanel(workspace, catalog, pluginsInfo = null) {
  const el = document.getElementById("info-body");
  const flowCount = workspace?.flows?.length || 0;
  const nodeCount = (workspace?.flows || []).reduce((s, f) => s + f.nodes.length, 0);
  const edgeCount = (workspace?.flows || []).reduce((s, f) => s + f.edges.length, 0);

  const plugins = pluginsInfo?.plugins || [];
  const pluginsHtml = plugins.length > 0
    ? `
      <div class="insp-section">
        <div class="insp-title">Modules &amp; Plugins (${plugins.length})</div>
        ${pluginsInfo?.pluginsDirectory ? `<div class="insp-desc" style="margin-bottom:6px;word-break:break-all;">Dir: ${pluginsInfo.pluginsDirectory}</div>` : ""}
        <div class="insp-port-list">
          ${plugins.map((p) => {
            const dotColor = p.error ? "#ef4444" : (p.source === "plugin" ? "#34d399" : "#60a5fa");
            const badge = p.error ? "ERR" : (p.source === "plugin" ? "PLUGIN" : "CORE");
            return `
              <div class="insp-port-row" title="${p.error || p.assemblyPath || p.pluginId}">
                <span class="insp-port-dot" style="background:${dotColor}"></span>
                <span>${p.displayName}</span>
                <span style="margin-left:auto;color:var(--text-dim);font-family:var(--font-mono);font-size:10px">${badge} · ${(p.nodeTypeIds || []).length}</span>
              </div>
            `;
          }).join("")}
        </div>
      </div>
    `
    : "";

  el.innerHTML = `
    <div class="insp-section">
      <div class="insp-title">${t("info.paradigm")}</div>
      <div class="insp-desc">${t("info.paradigmText")}</div>
    </div>
    <div class="insp-section">
      <div class="insp-title">${t("info.summary")}</div>
      <div class="insp-desc">Flows: ${flowCount} · Nodes: ${nodeCount} · Wires: ${edgeCount}</div>
    </div>
    ${pluginsHtml}
    <div class="insp-section">
      <div class="insp-title">${t("info.catalog")} (${catalog.length})</div>
      <div class="insp-port-list">
        ${catalog.map((d) => `
          <div class="insp-port-row info-doc-link" data-doc-type="${d.typeId}" title="${t("help.openDocs")}">
            <span class="insp-port-dot" style="background:${d.color}"></span>
            <span class="info-doc-name">${d.displayName}</span>
            <span class="info-doc-id">${d.typeId}</span>
            <button type="button" class="info-help-btn" data-doc-type="${d.typeId}" title="${t("help.openDocs")}">?</button>
          </div>
        `).join("")}
      </div>
    </div>
  `;

  el.querySelectorAll(".info-doc-link").forEach((row) => {
    row.addEventListener("click", () => {
      if (typeof HelpPanel !== "undefined") {
        HelpPanel.showNode(row.dataset.docType, true);
      }
    });
  });
}
