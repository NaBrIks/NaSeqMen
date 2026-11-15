// SPDX-License-Identifier: MIT
import en from './locales/en.js';
import ru from './locales/ru.js';
import { extraEn, extraRu } from './translations-extra.js';
import { graphEn, graphRu } from './graph-translations.js';
import { eventEn, eventRu } from './input/event-translations.js';
const catalogs = { en: { ...en, ...extraEn, ...graphEn, ...eventEn }, ru: { ...ru, ...extraRu, ...graphRu, ...eventRu } };
let language = 'en';
try { if (localStorage.getItem('naseqmen.ui.language') === 'ru') language = 'ru'; } catch { }
const listeners = new Set();
export function getLanguage() { return language; }
export function hasTranslation(key) { return Object.hasOwn(catalogs.en, key); }
export function registerTranslations(value) {
  for (const lang of ['en', 'ru']) Object.assign(catalogs[lang], value?.[lang] || {});
}
export function t(key, values = {}) {
  const template = catalogs[language][key] ?? catalogs.en[key];
  if (template === undefined) return String(key);
  return template.replace(/\{(\w+)\}/g, (match, name) => values[name] === undefined ? match : formatArgument(values[name]));
}
function formatArgument(value) {
  return value && typeof value === 'object' && value.code ? localizeMessage(value) : String(value ?? '');
}
export function localizeMessage(value) {
  if (typeof value === 'string') return value;
  const code = value?.messageCode || value?.code;
  return code && hasTranslation(code) ? t(code, value.messageArgs || value.args || {}) : value?.message || value?.error || '';
}
export function displayName(value) { return value?.nameKey && hasTranslation(value.nameKey) ? t(value.nameKey) : value?.name || ''; }
export function bindTranslations(root = document) {
  for (const [attr, target] of [['data-i18n', null], ['data-i18n-tooltip', 'data-tooltip'], ['data-i18n-label', 'aria-label']]) {
    const nodes = [...root.querySelectorAll('[' + attr + ']')]; if (root.matches?.('[' + attr + ']')) nodes.unshift(root);
    for (const node of nodes) { const translated = t(node.getAttribute(attr)); if (target) node.setAttribute(target, translated); else node.textContent = translated; }
  }
}
export function setLanguage(value) {
  language = value === 'ru' ? 'ru' : 'en';
  if (typeof document !== 'undefined') document.documentElement.lang = language;
  try { localStorage.setItem('naseqmen.ui.language', language); } catch { }
  if (typeof document !== 'undefined') bindTranslations();
  for (const callback of listeners) callback(language);
}
export function onLanguageChange(callback) { listeners.add(callback); return () => listeners.delete(callback); }
if (typeof document !== 'undefined') document.documentElement.lang = language;
