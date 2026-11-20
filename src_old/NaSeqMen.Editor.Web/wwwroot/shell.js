// SPDX-License-Identifier: MIT
// Vanilla DOM adapter for TechnicalShell AppShell/NavBranch. Common assets remain unchanged.
import { t, getLanguage, setLanguage, onLanguageChange, bindTranslations } from './i18n.js';
import './technical-shell/ui-tooltip.js';

const themes = [['light', 'ui.light', 'sun'], ['cream', 'ui.cream', 'ice-cream'], ['emerald', 'ui.emerald', 'trees'], ['dark', 'ui.dark', 'moon']];
const densities = [['nano', 'ui.ultra_compact', 'baseline-density-small'], ['mini', 'ui.compact', 'baseline-density-medium'], ['norm', 'ui.normal', 'baseline-density-large']];
const panels = [['compact', 'ui.merged'], ['rounded', 'ui.rounded'], ['square', 'ui.square']];
const prefix = 'naseqmen.ui.';
let callbacks = {}, initialized = false, connections = [];
const state = { theme: 'cream', density: 'nano', panelScheme: 'compact', tooltips: true, collapsed: false };
const $ = id => document.getElementById(id);
const element = (tag, className, text) => { const node = document.createElement(tag); if (className) node.className = className; if (text !== undefined) node.textContent = text; return node; };
const icon = name => { const node = element('i', 'ti ti-' + name); node.setAttribute('aria-hidden', 'true'); return node; };
function read(key) { try { return localStorage.getItem(prefix + key); } catch { return null; } }
function write(key, value) { try { localStorage.setItem(prefix + key, String(value)); } catch { /* Preferences are optional. */ } }

function apply() {
  const shell = $('shell');
  shell.dataset.theme = state.theme; shell.dataset.density = state.density;
  shell.dataset.panelScheme = state.panelScheme; shell.dataset.tooltips = String(state.tooltips);
  shell.classList.toggle('menu-collapsed', state.collapsed);
  $('shell-menu-toggle').setAttribute('aria-expanded', String(!state.collapsed));
  // Native dialog top layer may be outside shell; mirror tokens at the root.
  document.documentElement.dataset.theme = state.theme;
  document.documentElement.dataset.density = state.density;
  renderAppearance();
  document.dispatchEvent(new CustomEvent('shell-appearance', { detail: { ...state } }));
}

function renderAppearance() {
  $('appearance-label').textContent = t(themes.find(theme => theme[0] === state.theme)[1]) + ' · ' + state.density.toUpperCase();
  const menu = $('appearance-menu'); menu.replaceChildren();
  const group = (name, entries, key) => {
    menu.append(element('strong', '', t(name)));
    for (const [id, label, glyph] of entries) {
      const selected = state[key] === id;
      const button = element('button', selected ? 'selected' : ''); button.type = 'button';
      button.dataset.appearance = key; button.dataset.value = id;
      button.setAttribute('aria-label', t(label)); button.setAttribute('aria-pressed', String(selected));
      if (glyph) button.append(icon(glyph));
      else {
        const sample = element('span', 'panel-scheme-sample'); sample.dataset.sample = id; sample.setAttribute('aria-hidden', 'true');
        sample.append(element('span'), element('span')); button.append(sample);
      }
      button.append(document.createTextNode(' ' + t(label) + (selected ? ' ✓' : '')));
      button.addEventListener('click', () => {
        state[key] = id; write(key, id); apply();
        menu.querySelector(`[data-appearance="${key}"][data-value="${id}"]`)?.focus();
      });
      menu.append(button);
    }
  };
  group('ui.colour_scheme', themes, 'theme');
  group('ui.density', densities, 'density');
  group('ui.panel_borders', panels, 'panelScheme');
  menu.append(element('strong', '', t('ui.tooltips')));
  const tooltips = element('button', state.tooltips ? 'selected' : ''); tooltips.type = 'button'; tooltips.id = 'shell-tooltips';
  tooltips.setAttribute('role', 'switch'); tooltips.setAttribute('aria-label', t('ui.show_tooltips')); tooltips.setAttribute('aria-checked', String(state.tooltips));
  tooltips.append(icon(state.tooltips ? 'eye' : 'eye-off'), document.createTextNode(' ' + t('ui.show_tooltips')));
  tooltips.addEventListener('click', () => { state.tooltips = !state.tooltips; write('tooltips', state.tooltips); apply(); $('shell-tooltips').focus(); });
  menu.append(tooltips);
}

function translateShell() {
  bindTranslations($('shell'));
  $('shell-menu-toggle').dataset.tooltip = t('ui.collapse_expand_navigation');
  $('shell-menu-toggle').setAttribute('aria-label', t('ui.collapse_expand_navigation'));
  $('shell-navigation').setAttribute('aria-label', t('ui.sections'));
  $('shell-language').textContent = getLanguage().toUpperCase();
  renderAppearance(); renderConnections();
}

function selectPage(tab) {
  for (const link of document.querySelectorAll('.shell-sidebar [data-tab]')) {
    const active = link.dataset.tab === tab;
    link.classList.toggle('active', active); link.classList.toggle('selected', active);
    if (active) link.setAttribute('aria-current', 'page'); else link.removeAttribute('aria-current');
  }
  for (const link of document.querySelectorAll('[data-connection-id]')) { link.classList.remove('active'); link.removeAttribute('aria-current'); }
}

function renderConnections() {
  const host = $('settings-connections'); if (!host) return;
  const selected = host.querySelector('.active')?.dataset.connectionId;
  host.replaceChildren();
  for (const connection of connections) {
    const name = connection.name || connection.id;
    const button = element('button', 'nav-link' + (connection.id === selected ? ' active' : '')); button.type = 'button';
    button.dataset.connectionId = connection.id; button.dataset.tooltip = name;
    button.setAttribute('aria-label', t('ui.settings') + ': ' + name);
    button.append(icon('plug'), element('span', '', name));
    button.addEventListener('click', () => {
      selectPage('settings');
      const parent = document.querySelector('.shell-sidebar [data-tab="settings"]'); parent?.classList.remove('active', 'selected'); parent?.removeAttribute('aria-current');
      button.classList.add('active'); button.setAttribute('aria-current', 'page');
      callbacks.onNavigateConnection?.(connection.id);
      document.dispatchEvent(new CustomEvent('shell-select-connection', { detail: { id: connection.id } }));
    });
    host.append(button);
  }
}

export function updateConnections(value) { connections = Array.isArray(value) ? value : []; if (initialized) renderConnections(); }

export function selectSettingsConnection(id) {
  selectPage('settings');
  if (!id) return;
  const button = [...document.querySelectorAll('[data-connection-id]')].find(item => item.dataset.connectionId === id);
  if (!button) return;
  const parent = document.querySelector('.shell-sidebar [data-tab="settings"]');
  parent?.classList.remove('active', 'selected'); parent?.removeAttribute('aria-current');
  button.classList.add('active'); button.setAttribute('aria-current', 'page');
}

export function initializeShell(options = {}) {
  if (initialized) return;
  initialized = true; callbacks = options;
  for (const [key, entries] of [['theme', themes], ['density', densities], ['panelScheme', panels]]) {
    const saved = read(key); if (entries.some(entry => entry[0] === saved)) state[key] = saved;
  }
  const savedTooltips = read('tooltips'); if (savedTooltips === 'true' || savedTooltips === 'false') state.tooltips = savedTooltips === 'true';
  state.collapsed = read('menu') === 'collapsed';
  const language = read('language'); if (language === 'en' || language === 'ru') setLanguage(language);
  $('shell-menu-toggle').addEventListener('click', () => { state.collapsed = !state.collapsed; write('menu', state.collapsed ? 'collapsed' : 'expanded'); apply(); });
  $('shell-language').addEventListener('click', () => { const language = getLanguage() === 'ru' ? 'en' : 'ru'; write('language', language); setLanguage(language); });
  document.querySelectorAll('.shell-sidebar [data-tab]').forEach(button => button.addEventListener('click', () => { selectPage(button.dataset.tab); callbacks.onNavigate?.(button.dataset.tab); }));
  $('open-appearance').addEventListener('click', () => { $('appearance').open = true; $('appearance-summary').focus(); });
  document.addEventListener('click', event => { const path = event.composedPath(); if (!path.includes($('appearance')) && !path.includes($('open-appearance'))) $('appearance').open = false; });
  document.addEventListener('keydown', event => { if (event.key === 'Escape' && $('appearance').open && !event.defaultPrevented) { $('appearance').open = false; $('appearance-summary').focus(); } });
  document.addEventListener('naseqmen-config', event => updateConnections(event.detail?.connections || event.detail?.config?.connections || []));
  onLanguageChange(() => { translateShell(); document.dispatchEvent(new CustomEvent('shell-language', { detail: { language: getLanguage() } })); });
  apply(); translateShell();
}
