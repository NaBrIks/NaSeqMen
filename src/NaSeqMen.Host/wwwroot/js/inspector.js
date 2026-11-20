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
  const del = document.createElement("button");
  del.className = "btn-secondary";
  del.style.color = "#f87171";
  del.textContent = t("inspector.delete");
  del.addEventListener("click", () => callbacks.onDelete());
  btnRow.appendChild(del);
  general.appendChild(btnRow);

  container.appendChild(general);

  // Parameters section
  if (descriptor.parameters?.length > 0) {
    const pSec = document.createElement("div");
    pSec.className = "insp-section";
    pSec.innerHTML = `<div class="insp-title">${t("inspector.parameters")}</div>`;

    for (const p of descriptor.parameters) {
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
          ta.rows = 3;
          ta.value = val;
          ta.addEventListener("input", () => {
            node.parameters[p.key] = ta.value;
            callbacks.onChanged();
          });
          field.appendChild(ta);
        } else {
          const inp = document.createElement("input");
          inp.type = p.kind === "number" ? "number" : "text";
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

      pSec.appendChild(field);
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
}

function renderInfoPanel(workspace, catalog) {
  const el = document.getElementById("info-body");
  const flowCount = workspace?.flows?.length || 0;
  const nodeCount = (workspace?.flows || []).reduce((s, f) => s + f.nodes.length, 0);
  const edgeCount = (workspace?.flows || []).reduce((s, f) => s + f.edges.length, 0);

  el.innerHTML = `
    <div class="insp-section">
      <div class="insp-title">${t("info.paradigm")}</div>
      <div class="insp-desc">${t("info.paradigmText")}</div>
    </div>
    <div class="insp-section">
      <div class="insp-title">${t("info.summary")}</div>
      <div class="insp-desc">Flows: ${flowCount} · Nodes: ${nodeCount} · Wires: ${edgeCount}</div>
    </div>
    <div class="insp-section">
      <div class="insp-title">${t("info.catalog")} (${catalog.length})</div>
      <div class="insp-port-list">
        ${catalog.map((d) => `
          <div class="insp-port-row">
            <span class="insp-port-dot" style="background:${d.color}"></span>
            <span>${d.displayName}</span>
            <span style="margin-left:auto;color:var(--text-dim);font-size:10px">${d.typeId}</span>
          </div>
        `).join("")}
      </div>
    </div>
  `;
}
