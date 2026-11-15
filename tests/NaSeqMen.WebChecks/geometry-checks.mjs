// SPDX-License-Identifier: MIT
import assert from 'node:assert/strict';
import { chromium } from 'playwright';
import { existsSync, mkdirSync, writeFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import path from 'node:path';
const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');
const output = path.join(root, 'local'); mkdirSync(output, { recursive: true });
const executablePath = ['C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe', 'C:\\Program Files (x86)\\Google\\Chrome\\Application\\chrome.exe'].find(existsSync);
const browser = await chromium.launch({ executablePath, headless: true });
const results = [], errors = [];
let currentCase = '';
const config = { version: 1, dryRun: true, nodes: [{ id: 'source', name: 'Тестовый источник с длинным названием', kind: 'source', settings: {} }, { id: 'action', name: 'Выход', kind: 'action', operation: 'chord', settings: {} }], edges: [{ from: 'source', to: 'action' }], connections: [], mappings: [] };
try {
  // Browser zoom changes CSS viewport and DPR together. This matrix emulates those
  // layout/raster metrics explicitly; it does not rely on pinch/pageScale zoom.
  for (const [physicalWidth, physicalHeight] of [[900, 600], [1280, 820], [2160, 1400]]) for (const zoom of [1, 1.25, 1.5]) for (const density of ['nano', 'norm']) {
    currentCase = `${physicalWidth}x${physicalHeight} / ${zoom} / ${density}`;
    const context = await browser.newContext({ viewport: { width: Math.round(physicalWidth / zoom), height: Math.round(physicalHeight / zoom) }, deviceScaleFactor: zoom });
    await context.addInitScript(({ density }) => {
      localStorage.setItem('naseqmen.ui.language', 'ru'); localStorage.setItem('naseqmen.ui.density', density);
      localStorage.setItem('naseqmen.ui.theme', density === 'nano' ? 'cream' : 'dark');
      window.__sockets = [];
      window.WebSocket = class extends EventTarget {
        constructor() { super(); window.__sockets.push(this); this.readyState = 0; setTimeout(() => { this.readyState = 1; this.dispatchEvent(new Event('open')); }); }
        close() { this.readyState = 3; } send() { throw new Error('Unexpected mutation'); }
      };
    }, { density });
    await context.route('**/api/**', async route => {
      assert.equal(route.request().method(), 'GET', 'Geometry checks must not mutate executor');
      const url = new URL(route.request().url());
      const body = url.pathname === '/api/config' ? config : url.pathname === '/api/localization' ? { en: {}, ru: {} } : url.pathname === '/api/devices' ? Array.from({ length: 20 }, (_, i) => ({ id: `mouse-${i}`, name: `Мышь ${i + 1} — очень длинное название устройства`, kind: 'mouse', hardwareId: `TEST/${i}`, canSuppress: false })) : [];
      await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });
    });
    const page = await context.newPage(); page.on('pageerror', error => errors.push(error.message));
    await page.goto(process.env.NASEQMEN_TEST_URL || 'http://127.0.0.1:17965');
    await page.locator('.graph-node').nth(1).waitFor();
    await page.locator('[data-tab="devices"]').click();
    for (let index = 0; index < 3; index++) await page.locator('#devices tbody tr').nth(index).locator('button').click();
    await page.evaluate(() => {
      for (let source = 0; source < 3; source++) for (let key = 1; key <= 8; key++) {
        const data = { time: new Date().toISOString(), stage: 'input', signalId: `${source}-${key}`, nodeId: '', message: 'TEST', signal: { source: `mouse-${source}`, control: `mouse:button${key}`, phase: 'down', value: '1', module: 'windows.input' } };
        window.__sockets.at(-1).dispatchEvent(new MessageEvent('message', { data: JSON.stringify(data) }));
      }
    });
    assert.equal(await page.locator('.input-group').count(), 3);
    for (const panelScheme of ['compact', 'rounded', 'square']) for (const collapsed of [false, true]) {
      await page.evaluate(value => { document.querySelector('#shell').dataset.panelScheme = value; }, panelScheme);
      await page.evaluate(value => document.querySelector('#shell').classList.toggle('menu-collapsed', value), collapsed);
      for (const tab of ['graph', 'devices', 'connections']) {
        await page.locator(`[data-tab="${tab}"]`).click();
        const measured = await page.evaluate(() => {
          const rect = node => { const r = node.getBoundingClientRect(); return { x: r.x, y: r.y, right: r.right, bottom: r.bottom, width: r.width, height: r.height }; };
          const page = document.querySelector('.page:not([hidden])'), bounds = rect(page);
          const panes = [...page.querySelectorAll('.workspace-pane')].map(node => ({ id: node.id, ...rect(node), scrollHeight: node.scrollHeight, clientHeight: node.clientHeight }));
          const log = document.querySelector('#logs'), header = log.querySelector('thead'), firstRow = log.querySelector('tbody tr');
          return { bounds, panes, logAvailable: log.clientHeight - header.getBoundingClientRect().height, logRowHeight: firstRow.getBoundingClientRect().height, pageScroll: { overflow: getComputedStyle(page).overflowY, height: page.scrollHeight, client: page.clientHeight }, footer: rect(document.querySelector('footer')), viewport: { width: innerWidth, height: innerHeight }, docWidth: document.documentElement.scrollWidth, docHeight: document.documentElement.scrollHeight };
        });
        const failures = [];
        if (measured.docWidth > measured.viewport.width + 1 || measured.docHeight > measured.viewport.height + 1) failures.push('document overflow');
        if (measured.footer.bottom > measured.viewport.height + 1) failures.push('footer outside viewport');
        if (measured.logAvailable < Math.min(20, measured.logRowHeight) - 1) failures.push('log header leaves no readable event row');
        for (const pane of measured.panes) {
          if (pane.height < 12) failures.push(`${pane.id} disappeared`);
          if (pane.right > measured.bounds.right + 1) failures.push(`${pane.id} exceeds page width`);
          if (pane.y < measured.bounds.y - 1 || pane.bottom > measured.bounds.bottom + 1) {
            if (measured.pageScroll.overflow !== 'auto' && measured.pageScroll.overflow !== 'scroll') failures.push(`${pane.id} clipped by page`);
            else {
              const reachable = await page.locator('#' + pane.id).evaluate(node => {
                node.scrollIntoView({ block: 'center' });
                const r = node.getBoundingClientRect(), p = node.closest('.page').getBoundingClientRect();
                return Math.min(r.bottom, p.bottom) - Math.max(r.top, p.top) >= Math.min(50, r.height, p.height) - 1;
              });
              if (!reachable) failures.push(`${pane.id} cannot be reached by scrolling`);
            }
          }
        }
        for (let i = 1; i < measured.panes.length; i++) if (measured.panes[i].y < measured.panes[i - 1].bottom - 1) failures.push('panes overlap');
        results.push({ physicalWidth, physicalHeight, zoom, density, panelScheme, collapsed, tab, failures, measured });
        await page.locator('.page:not([hidden])').evaluate(node => { node.scrollTop = 0; });
        if (tab === 'devices' && !collapsed && panelScheme !== 'square' && (physicalWidth === 900 && zoom === 1.5 || physicalWidth === 2160 && zoom === 1.5 || physicalWidth === 1280 && zoom === 1)) {
          await page.screenshot({ path: path.join(output, `geometry-${physicalWidth}-${Math.round(zoom * 100)}-${density}-${panelScheme}.png`) });
          if (physicalWidth === 900) {
            await page.locator('#inputs').evaluate(node => node.scrollIntoView({ block: 'start' }));
            await page.screenshot({ path: path.join(output, `geometry-${physicalWidth}-${Math.round(zoom * 100)}-${density}-${panelScheme}-scrolled.png`) });
          }
        }
      }
    }
    await context.close();
  }
} catch (error) {
  console.error(JSON.stringify({ currentCase, errors, completed: results.length }));
  throw error;
} finally { await browser.close(); }
writeFileSync(path.join(output, 'geometry-results.json'), JSON.stringify({ results, errors }, null, 2));
const failed = results.filter(row => row.failures.length);
for (const row of failed) console.log(`FAIL ${row.physicalWidth}x${row.physicalHeight} ${row.zoom} ${row.density} ${row.panelScheme} collapsed=${row.collapsed} ${row.tab}: ${row.failures.join(', ')}`);
assert.equal(errors.length, 0, errors.join('\n'));
assert.equal(failed.length, 0, `${failed.length}/${results.length} geometry cases failed; see local/geometry-results.json`);
console.log(`PASS ${results.length}/${results.length} geometry cases; no mutations or browser errors`);
