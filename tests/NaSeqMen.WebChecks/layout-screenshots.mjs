// SPDX-License-Identifier: MIT
import assert from 'node:assert/strict';
import { chromium } from 'playwright';
import { existsSync, mkdirSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import path from 'node:path';
import { ownerFixture, branchFixture } from './layout-test.mjs';
const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');
const output = path.join(root, 'local'); mkdirSync(output, { recursive: true });
const executablePath = ['C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe', 'C:\\Program Files (x86)\\Google\\Chrome\\Application\\chrome.exe'].find(existsSync);
const browser = await chromium.launch({ executablePath, headless: true });
const errors = [];
try {
  for (const [name, fixture] of [['chains', ownerFixture], ['branches', branchFixture]]) {
    const context = await browser.newContext({ viewport: { width: 1280, height: 820 } });
    await context.addInitScript(() => {
      localStorage.setItem('naseqmen.ui.language', 'ru');
      window.WebSocket = class extends EventTarget {
        constructor() { super(); this.readyState = 0; setTimeout(() => { this.readyState = 1; this.dispatchEvent(new Event('open')); }); }
        close() { this.readyState = 3; } send() { throw new Error('Unexpected socket mutation'); }
      };
    });
    const names = { demo: 'Демонстрационный вход', condition: 'Только нажатие', scale: 'Преобразовать шкалу', copy: 'Копировать', 'script-in': 'Вход для C#', script: 'C#: изменить текст', enter: 'Нажать Enter', 'obs-info': 'Получить сведения OBS', request: 'OBS: GetVersion', 'obs-response': 'Ответ OBS', text: 'Сформировать текст', source: 'Общий источник', a: 'Условие A', b: 'Условие B', ta: 'Обработка A', tb: 'Обработка B', merge: 'Общее действие', independent: 'Отдельный источник', 'independent-out': 'Отдельное действие' };
    const configuration = { version: 1, dryRun: true, connections: [], mappings: [], ...fixture, nodes: fixture.nodes.map(node => ({ ...node, name: names[node.id], module: '', operation: '', settings: {}, enabled: true, inputType: 'any', outputType: 'any' })) };
    await context.route('**/api/**', async route => {
      assert.equal(route.request().method(), 'GET', 'Screenshot must not mutate executor');
      const url = new URL(route.request().url());
      const body = url.pathname === '/api/config' ? configuration : url.pathname === '/api/localization' ? { en: {}, ru: {} } : [];
      await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });
    });
    const page = await context.newPage(); page.on('pageerror', error => errors.push(error.message));
    await page.goto(process.env.NASEQMEN_TEST_URL || 'http://127.0.0.1:17965');
    await page.locator('.graph-node').nth(configuration.nodes.length - 1).waitFor();
    if (name === 'branches') {
      const ports = await page.locator('[data-node-id="source"] [data-port="out"] .graph-port-dot').evaluateAll(dots =>
        dots.map(dot => Number(dot.getAttribute('cy'))));
      assert.equal(ports.length, 2);
      assert(ports[1] - ports[0] > 8, 'shared output renders two visibly separated contacts');
      const branch = await page.locator('.graph-edge[aria-label="Общий источник → Условие B"] .graph-wire').getAttribute('d');
      assert.match(branch, /^M[^C]+ C[^LQ]+$/, 'offset branch is a single continuous curve');
    }
    const localized = await page.evaluate(async () => {
      const { GraphView, portType } = await import('/graph.js');
      const detail = node => GraphView.prototype.detail.call({ configuration: {} }, node);
      return {
        role: detail({ kind: 'condition' }),
        phase: detail({ kind: 'source', settings: { control: 'EXTERNAL_CONTROL', phase: 'down' } }),
        custom: detail({ kind: 'source', settings: { control: 'EXTERNAL_CONTROL', phase: 'CUSTOM_PHASE' } }),
        script: detail({ kind: 'script', inputType: 'bool', outputType: 'text' }),
        protocol: portType({ kind: 'script', inputType: 'bool' }, 'in')
      };
    });
    assert.equal(localized.role, 'Условие');
    assert.equal(localized.phase, 'EXTERNAL_CONTROL · нажатие');
    assert.equal(localized.custom, 'EXTERNAL_CONTROL · CUSTOM_PHASE');
    assert.equal(localized.script, 'C# · логическое → текст');
    assert.equal(localized.protocol, 'bool');
    await page.screenshot({ path: path.join(output, `graph-layout-${name}.png`) });
    await context.close();
  }
  assert.deepEqual(errors, []);
  console.log('PASS two mocked graph screenshots; no executor actions or browser errors');
} finally { await browser.close(); }
