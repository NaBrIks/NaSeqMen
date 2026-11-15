// SPDX-License-Identifier: MIT
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import en from '../../src/NaSeqMen.Editor.Web/wwwroot/locales/en.js';
import ru from '../../src/NaSeqMen.Editor.Web/wwwroot/locales/ru.js';
import { extraEn, extraRu } from '../../src/NaSeqMen.Editor.Web/wwwroot/translations-extra.js';
import { graphEn, graphRu } from '../../src/NaSeqMen.Editor.Web/wwwroot/graph-translations.js';
import { eventEn, eventRu } from '../../src/NaSeqMen.Editor.Web/wwwroot/input/event-translations.js';
import { getLanguage, setLanguage, t, registerTranslations, localizeMessage, displayName } from '../../src/NaSeqMen.Editor.Web/wwwroot/i18n.js';
let count = 0;
function check(value, label) { assert(value, label); console.log('PASS ' + label); count++; }
const english = { ...en, ...extraEn, ...graphEn, ...eventEn }, russian = { ...ru, ...extraRu, ...graphRu, ...eventRu };
assert.deepEqual(Object.keys(english).sort(), Object.keys(russian).sort());
check(Object.keys(english).length > 100, 'English and Russian catalog keys match');
for (const [key, text] of Object.entries(english)) {
  assert(!/[А-Яа-яЁё]/.test(key), key);
  assert(text.length > 0 && russian[key].length > 0, key);
  assert.deepEqual([...text.matchAll(/\{(\w+)\}/g)].map(x => x[1]).sort(), [...russian[key].matchAll(/\{(\w+)\}/g)].map(x => x[1]).sort(), key);
}
check(true, 'All translations are nonempty and preserve interpolation parameters');
for (const file of ['app.js', 'shell.js', 'graph.js', 'forms.js', 'connection-settings.js', 'index.html']) {
  const source = readFileSync(new URL('../../src/NaSeqMen.Editor.Web/wwwroot/' + file, import.meta.url), 'utf8');
  check(!/[А-Яа-яЁё]/.test(source), file + ' contains no embedded Russian UI copy');
  for (const match of source.matchAll(/data-i18n(?:-label|-tooltip)?="([^"]+)"|\bt\('([a-z][\w.]+)'/g)) {
    const key = match[1] || match[2]; if (!key.endsWith('.')) assert(Object.hasOwn(english, key), file + ': missing key ' + key);
  }
  for (const match of source.matchAll(/['"]((?:ui|forms|graph|settings)\.[\w.]+)['"]/g)) {
    if (!match[1].endsWith('.') && !/\.(js|css)$/.test(match[1])) assert(Object.hasOwn(english, match[1]), file + ': missing key ' + match[1]);
  }
}
check(getLanguage() === 'en', 'English is the default with no saved preference');
registerTranslations({ en: { 'test.outer': 'Failure: {detail}', 'test.inner': 'Missing {name}', 'test.device': 'Keyboard' }, ru: { 'test.outer': 'Ошибка: {detail}', 'test.inner': 'Нет {name}', 'test.device': 'Клавиатура' } });
const detail = { code: 'test.outer', args: { detail: { code: 'test.inner', args: { name: 'USB' } } }, message: 'fallback' };
check(localizeMessage(detail) === 'Failure: Missing USB', 'Structured backend messages resolve nested parameters in English');
setLanguage('ru');
check(localizeMessage(detail) === 'Ошибка: Нет USB', 'Same backend message resolves in Russian without translation by text');
check(displayName({ name: 'Keyboard', nameKey: 'test.device' }) === 'Клавиатура', 'Built-in descriptor names follow selected language');
check(displayName({ name: 'Мой OBS / My OBS' }) === 'Мой OBS / My OBS', 'User-defined names remain unchanged');
check(localizeMessage({ message: 'External detail' }) === 'External detail', 'Unknown external messages preserve original content');
setLanguage('unsupported');
check(getLanguage() === 'en' && t('ui.graph') === 'Graph', 'Unsupported language falls back to English');
console.log(`${count}/${count} localization checks passed`);
