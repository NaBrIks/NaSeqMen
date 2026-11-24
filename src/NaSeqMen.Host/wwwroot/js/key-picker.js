/**
 * Layout-independent Key & KeyCombo capture and visual builder
 * producing exact canonical names compatible with ScanCodeMap (LCtrl+X, LCtrl+K, LCtrl+D, LWin, X1, ...).
 */
const KeyPicker = {
  MODIFIER_CODES: new Set([
    "ControlLeft", "ControlRight",
    "ShiftLeft", "ShiftRight",
    "AltLeft", "AltRight",
    "MetaLeft", "MetaRight",
  ]),

  CODE_MAP: {
    Escape: "Esc",
    Minus: "Minus",
    Equal: "Equal",
    Backspace: "Backspace",
    Tab: "Tab",
    BracketLeft: "LeftBracket",
    BracketRight: "RightBracket",
    Enter: "Enter",
    ControlLeft: "LCtrl",
    ControlRight: "RCtrl",
    Semicolon: "Semicolon",
    Quote: "Apostrophe",
    Backquote: "Grave",
    ShiftLeft: "LShift",
    ShiftRight: "RShift",
    Backslash: "Backslash",
    Comma: "Comma",
    Period: "Period",
    Slash: "Slash",
    AltLeft: "LAlt",
    AltRight: "RAlt",
    Space: "Space",
    CapsLock: "CapsLock",
    NumLock: "NumLock",
    ScrollLock: "ScrollLock",
    PrintScreen: "PrintScreen",
    Pause: "Pause",
    Home: "Home",
    End: "End",
    PageUp: "PageUp",
    PageDown: "PageDown",
    Insert: "Insert",
    Delete: "Delete",
    ArrowUp: "Up",
    ArrowDown: "Down",
    ArrowLeft: "Left",
    ArrowRight: "Right",
    MetaLeft: "LWin",
    MetaRight: "RWin",
    ContextMenu: "Apps",
    NumpadMultiply: "NumpadMultiply",
    NumpadSubtract: "NumpadSubtract",
    NumpadAdd: "NumpadAdd",
    NumpadDecimal: "NumpadDecimal",
    NumpadDivide: "NumpadDivide",
    NumpadEnter: "NumpadEnter",
  },

  MOUSE_BUTTONS: {
    1: "Middle",
    3: "X1",
    4: "X2",
  },

  MOD_CHIPS: ["LCtrl", "LShift", "LAlt", "LWin"],
  SPECIAL_CHIPS: ["Enter", "Space", "Tab", "Esc", "Backspace", "Delete", "F1", "F2", "F5", "F9", "F12"],
  MOUSE_CHIPS: ["MouseLeft", "MouseRight", "Middle", "X1", "X2", "WheelUp", "WheelDown"],

  PREFIX_SINGLE: ["LWin+", "RWin+", "LCtrl+", "RCtrl+", "LShift+", "RShift+", "LAlt+", "RAlt+"],
  PREFIX_MULTI: [
    "LCtrl+LShift+", "LCtrl+LAlt+", "LAlt+LShift+",
    "LWin+LShift+", "LWin+LCtrl+", "LWin+LAlt+",
    "LCtrl+LAlt+LShift+", "LWin+LCtrl+LShift+",
  ],
  MOUSE_TOKENS: ["MouseLeft", "MouseRight", "Middle", "X1", "X2", "WheelUp", "WheelDown", "WheelLeft", "WheelRight"],
  SPECIAL_TOKENS: [
    "Esc", "Tab", "Space", "Enter", "Backspace", "Delete", "Insert",
    "Home", "End", "PageUp", "PageDown", "PrintScreen", "Pause", "CapsLock", "Apps",
    "Up", "Down", "LeftArrow", "RightArrow",
  ],
  FKEY_TOKENS: ["F1", "F2", "F3", "F4", "F5", "F6", "F7", "F8", "F9", "F10", "F11", "F12"],
  IS_MOD_RE: /^(l|r)?(ctrl|shift|alt|win)$/i,

  _activeStop: null,
  _activeMenuCleanup: null,
  _activeMenuAnchor: null,

  codeToCanonical(code) {
    if (!code) return null;
    if (this.CODE_MAP[code]) return this.CODE_MAP[code];
    if (/^Key([A-Z])$/.test(code)) return code.slice(3);
    if (/^Digit([0-9])$/.test(code)) return code.slice(5);
    if (/^F([1-9]|1[0-2])$/.test(code)) return code;
    if (/^Numpad([0-9])$/.test(code)) return code;
    return null;
  },

  stopActiveCapture() {
    if (this._activeStop) {
      this._activeStop();
      this._activeStop = null;
    }
  },

  closePrefixMenu() {
    if (this._activeMenuCleanup) {
      this._activeMenuCleanup();
      this._activeMenuCleanup = null;
      this._activeMenuAnchor = null;
    }
  },

  applyQuickToken(input, token, mode, onCommit) {
    const isPrefix = token.endsWith("+");
    let nextVal = token;

    if (mode === "key") {
      nextVal = isPrefix ? token.slice(0, -1) : token;
    } else {
      const trimmed = (input.value || "").trim();
      if (!trimmed) {
        nextVal = token;
      } else if (trimmed.endsWith(",")) {
        nextVal = `${trimmed} ${token}`;
      } else if (isPrefix) {
        const parts = trimmed.split(",").map((s) => s.trim()).filter(Boolean);
        const last = parts.pop() || "";
        const existing = last.split("+").map((s) => s.trim()).filter(Boolean);
        const addMods = token.slice(0, -1).split("+").filter(Boolean);
        const existingMods = existing.filter((k) => this.IS_MOD_RE.test(k));
        const existingKeys = existing.filter((k) => !this.IS_MOD_RE.test(k));
        const mergedMods = [...existingMods];
        for (const m of addMods) {
          if (!mergedMods.some((e) => e.toLowerCase() === m.toLowerCase())) {
            mergedMods.push(m);
          }
        }
        if (existingKeys.length > 0) {
          parts.push([...mergedMods, ...existingKeys].join("+"));
        } else {
          parts.push(`${mergedMods.join("+")}+`);
        }
        nextVal = parts.join(", ");
      } else {
        const parts = trimmed.split(",").map((s) => s.trim()).filter(Boolean);
        const last = parts.pop() || "";
        const existing = last.split("+").map((s) => s.trim()).filter(Boolean);
        const existingMods = existing.filter((k) => this.IS_MOD_RE.test(k));
        if (existingMods.length > 0) {
          parts.push([...existingMods, token].join("+"));
        } else {
          parts.push(token);
        }
        nextVal = parts.join(", ");
      }
    }

    input.value = nextVal;
    onCommit(nextVal);
    input.focus();
    const len = input.value.length;
    try {
      input.setSelectionRange(len, len);
    } catch {
      // ignore if input cannot select range
    }
  },

  openPrefixMenu(anchorBtn, input, mode, onCommit) {
    if (this._activeMenuAnchor === anchorBtn) {
      this.closePrefixMenu();
      return;
    }
    this.stopActiveCapture();
    this.closePrefixMenu();

    const menu = document.createElement("div");
    menu.className = "key-prefix-menu";
    menu.addEventListener("pointerdown", (e) => e.stopPropagation());
    menu.addEventListener("click", (e) => e.stopPropagation());

    const addSection = (title, tokens, extraClass = "") => {
      const sec = document.createElement("div");
      sec.className = "key-prefix-sec";
      const lbl = document.createElement("div");
      lbl.className = "key-prefix-lbl";
      lbl.textContent = title;
      const grid = document.createElement("div");
      grid.className = "key-prefix-grid";
      for (const tok of tokens) {
        const b = document.createElement("button");
        b.type = "button";
        b.className = `key-prefix-item${extraClass ? " " + extraClass : ""}`;
        b.textContent = tok;
        b.addEventListener("click", (e) => {
          e.stopPropagation();
          this.applyQuickToken(input, tok, mode, onCommit);
          this.closePrefixMenu();
        });
        grid.appendChild(b);
      }
      sec.append(lbl, grid);
      menu.appendChild(sec);
    };

    if (mode === "key") {
      addSection("Modifiers", this.PREFIX_SINGLE.map((s) => s.slice(0, -1)), "is-mod");
    } else {
      addSection("Modifiers (+)", this.PREFIX_SINGLE, "is-mod");
      addSection("Multi-Modifier (+)", this.PREFIX_MULTI, "is-mod");
    }
    addSection("Mouse", this.MOUSE_TOKENS, "is-mouse");
    addSection("Special & Nav", this.SPECIAL_TOKENS);
    addSection("Function Keys", this.FKEY_TOKENS);

    document.body.appendChild(menu);

    const rect = anchorBtn.getBoundingClientRect();
    const menuRect = menu.getBoundingClientRect();
    const pad = 8;
    let left = rect.right - menuRect.width;
    if (left < pad) left = Math.min(rect.left, window.innerWidth - menuRect.width - pad);
    if (left < pad) left = pad;
    let top = rect.bottom + 4;
    if (top + menuRect.height > window.innerHeight - pad) {
      top = Math.max(pad, rect.top - menuRect.height - 4);
    }
    menu.style.left = `${Math.round(left)}px`;
    menu.style.top = `${Math.round(top)}px`;

    anchorBtn.classList.add("is-open");

    const onOutsideDown = (e) => {
      if (e.target === anchorBtn || anchorBtn.contains(e.target) || menu.contains(e.target)) return;
      this.closePrefixMenu();
    };
    const onKeyDown = (e) => {
      if (e.key === "Escape") {
        e.preventDefault();
        this.closePrefixMenu();
      }
    };
    const onScrollOrResize = (e) => {
      if (e.target && menu.contains(e.target)) return;
      this.closePrefixMenu();
    };

    const cleanup = () => {
      anchorBtn.classList.remove("is-open");
      menu.remove();
      window.removeEventListener("pointerdown", onOutsideDown, true);
      window.removeEventListener("keydown", onKeyDown, true);
      window.removeEventListener("resize", onScrollOrResize);
      window.removeEventListener("scroll", onScrollOrResize, true);
    };

    this._activeMenuCleanup = cleanup;
    this._activeMenuAnchor = anchorBtn;
    window.addEventListener("pointerdown", onOutsideDown, true);
    window.addEventListener("keydown", onKeyDown, true);
    window.addEventListener("resize", onScrollOrResize);
    window.addEventListener("scroll", onScrollOrResize, true);
  },

  createPrefixButton(inp, mode, onCommit, compact = false) {
    const btn = document.createElement("button");
    btn.type = "button";
    btn.className = `key-prefix-btn${compact ? " compact" : ""}`;
    btn.textContent = "+▾";
    btn.title = mode === "keyCombo"
      ? "Insert modifier prefix (LWin+, LCtrl+, …) or special/mouse key"
      : "Pick key or mouse button from list";
    btn.addEventListener("click", (e) => {
      e.stopPropagation();
      this.openPrefixMenu(btn, inp, mode, onCommit);
    });
    return btn;
  },

  /**
   * Starts live capture on a button + input pair.
   * mode: "key" (single key or mouse button) | "keyCombo" (modifiers + key, optional append step)
   */
  startCapture(btn, input, mode, onCommit, appendStep = false) {
    if (btn.classList.contains("recording")) {
      this.stopActiveCapture();
      return;
    }
    this.stopActiveCapture();

    const origText = btn.textContent;
    btn.classList.add("recording");
    btn.textContent = mode === "keyCombo" ? "Press combo…" : "Press key…";

    const heldMods = new Set();

    const cleanup = () => {
      btn.classList.remove("recording");
      btn.textContent = origText;
      window.removeEventListener("keydown", onKeyDown, true);
      window.removeEventListener("keyup", onKeyUp, true);
      window.removeEventListener("mousedown", onMouseDown, true);
      window.removeEventListener("blur", cleanup);
      if (this._activeStop === cleanup) this._activeStop = null;
    };

    const commit = (token) => {
      let nextVal = token;
      if (mode === "keyCombo" && appendStep && input.value.trim()) {
        nextVal = `${input.value.trim().replace(/,\s*$/, "")}, ${token}`;
      }
      input.value = nextVal;
      cleanup();
      onCommit(nextVal);
    };

    const onKeyDown = (e) => {
      e.preventDefault();
      e.stopPropagation();

      if (e.code === "Escape" && heldMods.size === 0 && !e.ctrlKey && !e.altKey && !e.shiftKey) {
        cleanup();
        return;
      }

      const name = this.codeToCanonical(e.code);
      if (!name) return;

      if (mode === "key") {
        commit(name);
        return;
      }

      // keyCombo mode: collect held modifiers, finalize on first non-modifier key
      if (this.MODIFIER_CODES.has(e.code)) {
        heldMods.add(name);
        btn.textContent = [...heldMods].join("+") + "+…";
        return;
      }

      const mods = [];
      if (heldMods.has("LCtrl") || (!heldMods.has("RCtrl") && e.ctrlKey)) mods.push("LCtrl");
      if (heldMods.has("RCtrl")) mods.push("RCtrl");
      if (heldMods.has("LShift") || (!heldMods.has("RShift") && e.shiftKey)) mods.push("LShift");
      if (heldMods.has("RShift")) mods.push("RShift");
      if (heldMods.has("LAlt") || (!heldMods.has("RAlt") && e.altKey)) mods.push("LAlt");
      if (heldMods.has("RAlt")) mods.push("RAlt");
      if (heldMods.has("LWin") || (!heldMods.has("RWin") && e.metaKey)) mods.push("LWin");
      if (heldMods.has("RWin")) mods.push("RWin");

      mods.push(name);
      commit(mods.join("+"));
    };

    const onKeyUp = (e) => {
      if (mode !== "keyCombo") return;
      const name = this.codeToCanonical(e.code);
      if (name && heldMods.has(name) && heldMods.size === 1) {
        // User tapped a modifier by itself (e.g. LWin)
        e.preventDefault();
        e.stopPropagation();
        commit(name);
      } else if (name) {
        heldMods.delete(name);
      }
    };

    const onMouseDown = (e) => {
      if (e.target === btn) return;
      const mb = this.MOUSE_BUTTONS[e.button];
      if (mb) {
        e.preventDefault();
        e.stopPropagation();
        commit(mb);
      } else {
        cleanup();
      }
    };

    this._activeStop = cleanup;
    window.addEventListener("keydown", onKeyDown, true);
    window.addEventListener("keyup", onKeyUp, true);
    window.addEventListener("mousedown", onMouseDown, true);
    window.addEventListener("blur", cleanup);
  },

  /**
   * Compact inline control (for node body & rule rows): [ input ] [ ⌨ ] [ +▾ ]
   */
  createInlineControl(value, placeholder, mode, onCommit) {
    const row = document.createElement("div");
    row.className = "key-input-row";

    const inp = document.createElement("input");
    inp.type = "text";
    inp.value = value;
    if (placeholder) inp.placeholder = placeholder;
    inp.addEventListener("input", () => onCommit(inp.value));

    const rec = document.createElement("button");
    rec.type = "button";
    rec.className = "key-rec-btn compact";
    rec.title = mode === "keyCombo"
      ? "Capture hotkey combo (Esc to cancel)"
      : "Capture key or X1/X2/Middle mouse button (Esc to cancel)";
    rec.textContent = "⌨";
    rec.addEventListener("click", (e) => {
      e.stopPropagation();
      this.closePrefixMenu();
      this.startCapture(rec, inp, mode, onCommit, false);
    });

    const pfx = this.createPrefixButton(inp, mode, onCommit, true);

    row.append(inp, rec, pfx);
    return row;
  },

  /**
   * Rich Inspector control: [ input ] [ Rec ] [ +▾ ] [ +Step ] [ × ] + clickable builder chips
   */
  createInspectorControl(value, placeholder, mode, onCommit) {
    const root = document.createElement("div");
    root.className = "key-picker-inspector";

    const row = document.createElement("div");
    row.className = "key-input-row";

    const inp = document.createElement("input");
    inp.type = "text";
    inp.value = value;
    if (placeholder) inp.placeholder = placeholder;
    inp.addEventListener("input", () => onCommit(inp.value));

    const rec = document.createElement("button");
    rec.type = "button";
    rec.className = "key-rec-btn";
    rec.textContent = "⌨ Rec";
    rec.title = mode === "keyCombo"
      ? "Press hotkey combo on keyboard (Esc to cancel)"
      : "Press key or X1/X2/Middle button (Esc to cancel)";
    rec.addEventListener("click", () => {
      this.closePrefixMenu();
      this.startCapture(rec, inp, mode, onCommit, false);
    });

    const pfx = this.createPrefixButton(inp, mode, onCommit, false);

    row.append(inp, rec, pfx);

    if (mode === "keyCombo") {
      const addStep = document.createElement("button");
      addStep.type = "button";
      addStep.className = "mini-btn";
      addStep.textContent = "+Step";
      addStep.title = "Record another step in sequence (e.g. LCtrl+K, LCtrl+D)";
      addStep.addEventListener("click", () => this.startCapture(rec, inp, mode, onCommit, true));
      row.appendChild(addStep);
    }

    const clr = document.createElement("button");
    clr.type = "button";
    clr.className = "mini-btn";
    clr.textContent = "×";
    clr.title = "Clear";
    clr.addEventListener("click", () => {
      inp.value = "";
      onCommit("");
    });
    row.appendChild(clr);
    root.appendChild(row);

    // Collapsible quick builder chips (ideal for LWin, X1, X2, Enter, etc.)
    const details = document.createElement("details");
    details.className = "key-builder-details";
    const summary = document.createElement("summary");
    summary.textContent = mode === "keyCombo" ? "Key / Mouse Builder" : "Pick Key / Mouse Button";
    details.appendChild(summary);

    const addGroup = (title, tokens, isModifier = false) => {
      const lbl = document.createElement("div");
      lbl.className = "key-builder-lbl";
      lbl.textContent = title;
      const chips = document.createElement("div");
      chips.className = "insp-chip-list";
      for (const tok of tokens) {
        const chip = document.createElement("button");
        chip.type = "button";
        chip.className = "insp-chip key-chip";
        chip.textContent = tok;
        chip.addEventListener("click", () => {
          let nextVal = tok;
          if (mode === "keyCombo") {
            const cur = inp.value.trim();
            if (!cur) {
              nextVal = tok;
            } else if (cur.endsWith(",") || cur.endsWith("+")) {
              nextVal = cur.endsWith(",") ? `${cur} ${tok}` : `${cur}${tok}`;
            } else if (isModifier) {
              // Insert modifier into the last step of the sequence
              const parts = cur.split(",").map((s) => s.trim()).filter(Boolean);
              const last = parts.pop() || "";
              const keys = last.split("+").map((s) => s.trim()).filter(Boolean);
              if (!keys.some((k) => k.toLowerCase() === tok.toLowerCase())) {
                keys.unshift(tok);
              }
              parts.push(keys.join("+"));
              nextVal = parts.join(", ");
            } else {
              // Append to current step if it ends with modifier(s), else replace last step or append
              const parts = cur.split(",").map((s) => s.trim()).filter(Boolean);
              const last = parts[parts.length - 1] || "";
              const lastKeys = last.split("+").map((s) => s.trim()).filter(Boolean);
              const allMods = lastKeys.length > 0 && lastKeys.every((k) => /^(l|r)?(ctrl|shift|alt|win)$/i.test(k));
              if (allMods) {
                parts[parts.length - 1] = `${last}+${tok}`;
                nextVal = parts.join(", ");
              } else {
                nextVal = `${cur}, ${tok}`;
              }
            }
          }
          inp.value = nextVal;
          onCommit(nextVal);
        });
        chips.appendChild(chip);
      }
      details.append(lbl, chips);
    };

    addGroup("Modifiers", this.MOD_CHIPS, true);
    addGroup("Keys", this.SPECIAL_CHIPS, false);
    addGroup("Mouse", this.MOUSE_CHIPS, false);

    root.appendChild(details);
    return root;
  },

  /**
   * Multi-rule Switch editor for ParameterKind.RuleList:
   * Array of { from, to, mode, enabled } with inline key capture on every row.
   */
  createRuleListEditor(rawRules, onCommit) {
    const rules = Array.isArray(rawRules)
      ? rawRules.map((r) => ({
          from: r?.from ?? "",
          to: r?.to ?? "",
          mode: r?.mode ?? "tapOnDown",
          enabled: r?.enabled !== false,
        }))
      : [];

    const root = document.createElement("div");
    root.className = "rule-list-editor";

    const listEl = document.createElement("div");
    listEl.className = "rule-list-items";

    const commit = () => onCommit(rules.map((r) => ({ ...r })));

    const renderRows = () => {
      listEl.innerHTML = "";
      if (rules.length === 0) {
        const empty = document.createElement("div");
        empty.className = "insp-desc";
        empty.textContent = "No rules yet. Click + Add Rule to map e.g. LWin+D → LCtrl+X or X1 → LCtrl+C.";
        listEl.appendChild(empty);
        return;
      }

      rules.forEach((rule, idx) => {
        const card = document.createElement("div");
        card.className = `rule-card${rule.enabled ? "" : " is-disabled"}`;

        const topRow = document.createElement("div");
        topRow.className = "rule-row-top";

        const chk = document.createElement("input");
        chk.type = "checkbox";
        chk.checked = rule.enabled;
        chk.title = "Enable / Disable rule";
        chk.addEventListener("change", () => {
          rule.enabled = chk.checked;
          card.classList.toggle("is-disabled", !rule.enabled);
          commit();
        });

        const modeSel = document.createElement("select");
        modeSel.className = "rule-mode-sel";
        for (const [mv, ml] of [["tapOnDown", "Tap"], ["mirrorHold", "Hold"], ["suppressOnly", "Block"]]) {
          const opt = document.createElement("option");
          opt.value = mv;
          opt.textContent = ml;
          if (rule.mode === mv) opt.selected = true;
          modeSel.appendChild(opt);
        }
        modeSel.addEventListener("change", () => {
          rule.mode = modeSel.value;
          commit();
        });

        const delBtn = document.createElement("button");
        delBtn.type = "button";
        delBtn.className = "mini-btn danger";
        delBtn.textContent = "×";
        delBtn.title = "Delete rule";
        delBtn.addEventListener("click", () => {
          rules.splice(idx, 1);
          renderRows();
          commit();
        });

        const idxBadge = document.createElement("span");
        idxBadge.className = "rule-idx";
        idxBadge.textContent = `#${idx + 1}`;

        topRow.append(chk, idxBadge, modeSel, delBtn);

        const fromRow = document.createElement("div");
        fromRow.className = "rule-io-row";
        const fromLbl = document.createElement("span");
        fromLbl.className = "rule-io-lbl";
        fromLbl.textContent = "In";
        const fromCtrl = this.createInlineControl(rule.from, "e.g. LWin+D or X1", "keyCombo", (val) => {
          rule.from = val;
          commit();
        });
        fromRow.append(fromLbl, fromCtrl);

        const toRow = document.createElement("div");
        toRow.className = "rule-io-row";
        const toLbl = document.createElement("span");
        toLbl.className = "rule-io-lbl";
        toLbl.textContent = "Out";
        const toCtrl = this.createInlineControl(rule.to, "e.g. LCtrl+X", "keyCombo", (val) => {
          rule.to = val;
          commit();
        });
        const stepBtn = document.createElement("button");
        stepBtn.type = "button";
        stepBtn.className = "mini-btn";
        stepBtn.textContent = ",+";
        stepBtn.title = "Append next sequence step (e.g. LCtrl+K, LCtrl+D)";
        const toInp = toCtrl.querySelector("input");
        const toRec = toCtrl.querySelector(".key-rec-btn");
        stepBtn.addEventListener("click", (e) => {
          e.stopPropagation();
          this.closePrefixMenu();
          this.startCapture(toRec, toInp, "keyCombo", (val) => {
            rule.to = val;
            commit();
          }, true);
        });
        toCtrl.appendChild(stepBtn);
        toRow.append(toLbl, toCtrl);

        card.append(topRow, fromRow, toRow);
        listEl.appendChild(card);
      });
    };

    const addBtn = document.createElement("button");
    addBtn.type = "button";
    addBtn.className = "btn-secondary rule-add-btn";
    addBtn.textContent = "+ Add Rule";
    addBtn.addEventListener("click", () => {
      rules.push({ from: "", to: "", mode: "tapOnDown", enabled: true });
      renderRows();
      commit();
    });

    renderRows();
    root.append(listEl, addBtn);
    return root;
  },
};
