// SPDX-License-Identifier: MIT
import assert from 'node:assert/strict';
import { chromium } from 'playwright';
import { existsSync, mkdirSync, writeFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import path from 'node:path';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');
const output = path.join(root, 'local');
mkdirSync(output, { recursive: true });
const executablePath = process.env.NASEQMEN_TEST_CHROME || [
  'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe',
  'C:\\Program Files (x86)\\Google\\Chrome\\Application\\chrome.exe'
].find(existsSync);
assert(executablePath, 'Installed Chrome executable is required; no browser is downloaded.');
const baseUrl = process.env.NASEQMEN_TEST_URL || 'http://127.0.0.1:17964';
const node = (id, name, kind, extra = {}) => ({ id, name, kind, module: '', connectionId: '', operation: '', enabled: true, order: 0, inputType: 'any', outputType: 'any', settings: {}, ...extra });
let config = {
  version: 1, dryRun: true,
  nodes: [
    node('source', 'Боковая кнопка', 'source', { settings: { control: 'demo' } }),
    node('condition', 'Только нажатие', 'condition', { operation: 'equals', settings: { field: 'phase', value: 'down' } }),
    node('script', 'Обработчик C#', 'script', { settings: { code: 'return Input;' } }),
    node('action', 'Копировать', 'action', { module: 'windows.desktop', operation: 'chord', connectionId: 'desktop', settings: { keys: 'Ctrl+C' } })
  ],
  edges: [{ from: 'source', to: 'condition' }, { from: 'condition', to: 'script' }, { from: 'script', to: 'action' }],
  connections: [{ id: 'desktop', name: 'Windows', module: 'windows.desktop', enabled: true, settings: {} }, { id: 'win-input', name: 'Input', module: 'windows.input', enabled: true, settings: {} }, { id: 'obs', name: 'OBS — пример', module: 'obs', enabled: true, settings: { url: 'ws://127.0.0.1:4455', password: 'TEST-ONLY-NOT-A-SECRET' } }],
  mappings: [{ id: 'mapping', source: 'mouse-a', control: 'mouse:button4', name: 'Копировать', policy: 'map' }]
};
const devices = [
  { id: 'mouse-a', name: 'Мышь — длинное имя составного устройства', kind: 'mouse', hardwareId: 'TEST/mouse-a', canSuppress: false },
  { id: 'keyboard-a', name: 'Клавиатурная часть мыши', kind: 'keyboard', hardwareId: 'TEST/keyboard-a', canSuppress: false },
  { id: 'mouse-b', name: 'Вторая мышь', kind: 'mouse', hardwareId: 'TEST/mouse-b', canSuppress: false },
  { id: 'hid-a', name: 'Служебный HID', kind: 'hid', hardwareId: 'TEST/hid-a', canSuppress: false }
];
const modules = [
  { id: 'windows.desktop', name: 'Windows', version: 'test', hasInput: false, canSuppress: false, actions: [{ id: 'chord', name: 'Сочетание клавиш', parameters: ['keys'], destinationNameKey: 'destination.activeWindow' }], conditions: [] },
  { id: 'windows.input', name: 'Ввод Windows', version: 'test', hasInput: true, canSuppress: false, actions: [], conditions: [] },
  { id: 'obs', name: 'OBS WebSocket', version: 'test', hasInput: true, canSuppress: false, actions: [{ id: 'request', name: 'Запрос OBS', parameters: ['type', 'data'] }], conditions: [] }
];
const results = [], consoleErrors = [], mutations = [];
const captures = [];
const check = (condition, name) => { assert(condition, name); results.push(name); console.log('PASS ' + name); };
let sequence = 0;
const browser = await chromium.launch({ executablePath, headless: true });
try {
  const context = await browser.newContext({ viewport: { width: 1280, height: 820 }, locale: 'ru-RU' });
  await context.route('**/api/**', async route => {
    const request = route.request(), url = new URL(request.url());
    let body, status = 200;
    if (request.method() !== 'GET' && url.pathname !== '/api/devices/capture')
      mutations.push({ method: request.method(), path: url.pathname });
    if (url.pathname === '/api/localization') body = {
      en: { 'test.input': 'Received {count}', 'destination.activeWindow': 'Active window' },
      ru: { 'test.input': 'Получено {count}', 'destination.activeWindow': 'Активное окно' }
    };
    else if (url.pathname === '/api/config' && request.method() === 'PUT') { config = request.postDataJSON(); body = { valid: true, errors: [] }; }
    else if (url.pathname === '/api/config') body = config;
    else if (url.pathname === '/api/modules') body = modules;
    else if (url.pathname === '/api/devices') body = devices;
    else if (url.pathname === '/api/devices/capture') {
      captures.push(request.postDataJSON());
      const interfaces = captures.at(-1).interfaces;
      body = captures.at(-1).allowEditing ? [] : interfaces.filter(id => devices.some(device => device.id === id && device.canSuppress));
      if (interfaces.length && !captures.at(-1).allowEditing) body.push('@discovery-keyboard');
    }
    else if (url.pathname === '/api/config/validate') body = { valid: true, errors: [] };
    else if (url.pathname === '/api/inject') body = { accepted: true };
    else { status = 404; body = { error: 'Unexpected mocked API route' }; }
    await route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) });
  });
  await context.addInitScript(() => {
    // Entirely local socket fixture: no handshake or diagnostic traffic reaches the executor.
    window.__testSockets = [];
    class MockWebSocket extends EventTarget {
      static CONNECTING = 0; static OPEN = 1; static CLOSING = 2; static CLOSED = 3;
      constructor(url) {
        super(); this.url = url; this.readyState = 0; window.__testSockets.push(this);
        setTimeout(() => { if (this.readyState === 0) { this.readyState = 1; this.dispatchEvent(new Event('open')); } }, 0);
      }
      close() { this.readyState = 3; this.dispatchEvent(new Event('close')); }
      send() { throw new Error('The editor must not send action data through diagnostics.'); }
    }
    window.WebSocket = MockWebSocket;
    window.__emit = data => {
      const socket = window.__testSockets.at(-1);
      if (socket?.readyState !== 1) throw new Error('Mock socket not connected');
      socket.dispatchEvent(new MessageEvent('message', { data: JSON.stringify(data) }));
    };
  });
  const page = await context.newPage();
  page.on('pageerror', error => consoleErrors.push(error.message));
  page.on('console', message => { if (message.type() === 'error') consoleErrors.push(message.text()); });
  await page.goto(baseUrl);
  await page.locator('#connection-state').filter({ hasText: 'Connected' }).waitFor();
  check(await page.locator('html').getAttribute('lang') === 'en' && await page.locator('[data-tab="devices"] span').textContent() === 'Devices and events', 'clean profile defaults to English');
  await page.locator('#shell-language').click();
  await page.locator('#connection-state').filter({ hasText: 'Подключено' }).waitFor();
  await page.locator('.graph-node').first().waitFor();
  check(await page.locator('.graph-node').count() === 4, 'four graph roles rendered');
  check(await page.locator('.graph-lane-label').last().textContent() === 'ПОЛУЧАТЕЛИ' &&
    await page.locator('.graph-destination').getAttribute('aria-label') === 'Активное окно',
    'the final column shows the plugin-declared keyboard destination');
  await page.screenshot({ path: path.join(output, 'web-checks-graph.png') });
  check(await page.locator('#shell').getAttribute('data-theme') === 'cream' && await page.locator('#shell').getAttribute('data-density') === 'nano', 'TechnicalShell defaults to Cream and Nano');
  check(await page.locator('#settings-connections [data-connection-id]').count() === config.connections.length && await page.locator('#shell-navigation > div:last-child').getAttribute('class') === 'settings-menu', 'settings is the final root and children come from configured connections');
  await page.locator('#appearance-summary').click();
  check((await page.locator('[data-appearance="theme"]').evaluateAll(nodes => nodes.map(node => node.dataset.value))).join(',') === 'light,cream,emerald,dark', 'appearance preserves all four themes in the prescribed order');
  check(await page.locator('[data-appearance="density"]').count() === 3 && await page.locator('[data-appearance="panelScheme"]').count() === 3 && await page.locator('#shell-tooltips').count() === 1, 'one appearance menu contains densities, panel variants and tooltip switch');
  await page.screenshot({ path: path.join(output, 'web-shell-appearance.png') });
  await page.locator('[data-appearance="theme"][data-value="dark"]').click();
  check(await page.locator('#shell').getAttribute('data-theme') === 'dark' && await page.locator('#appearance').getAttribute('open') !== null, 'theme updates while keeping the appearance menu open');
  await page.keyboard.press('Escape');
  await page.screenshot({ path: path.join(output, 'web-shell-dark.png') });
  await page.locator('[data-tab="devices"]').click();
  const nanoMetric = await page.locator('#devices tbody tr').first().evaluate(row => ({ height: row.getBoundingClientRect().height, font: getComputedStyle(row.querySelector('td')).fontSize }));
  await page.locator('#appearance-summary').click();
  await page.locator('[data-appearance="density"][data-value="norm"]').click();
  await page.keyboard.press('Escape');
  const normMetric = await page.locator('#devices tbody tr').first().evaluate(row => ({ height: row.getBoundingClientRect().height, font: getComputedStyle(row.querySelector('td')).fontSize }));
  check(normMetric.height > nanoMetric.height && normMetric.font === nanoMetric.font, 'density changes row padding while keeping text size readable and unchanged');
  await page.locator('#appearance-summary').click();
  await page.locator('[data-appearance="panelScheme"][data-value="rounded"]').click();
  check(await page.locator('#devices').evaluate(node => parseFloat(getComputedStyle(node).borderRadius)) === 8, 'rounded panel scheme uses the original 8px radius');
  await page.locator('#shell-tooltips').click();
  check(await page.locator('#shell').getAttribute('data-tooltips') === 'false', 'tooltip setting is applied on the shell');
  await page.keyboard.press('Escape');
  await page.locator('[data-connection-id="obs"]').click();
  await page.locator('[data-settings-edit]').click();
  await page.locator('dialog input[name="name"]').hover();
  await page.waitForTimeout(350);
  check(await page.locator('.lu-tooltip:not([hidden])').count() === 0, 'tooltip preference also disables help inside native dialogs');
  await page.getByRole('button', { name: 'Отмена', exact: true }).click();
  await page.locator('#shell-menu-toggle').click();
  check(await page.locator('.shell-sidebar').evaluate(node => node.getBoundingClientRect().width) === 48, 'navigation collapses to the original icon rail');
  check(await page.locator('[data-tab="devices"]').getAttribute('aria-label') === 'Устройства и события', 'collapsed navigation retains its accessible labels');
  await page.locator('#shell-language').click();
  check(await page.locator('[data-tab="devices"] span').textContent() === 'Devices and events' && await page.locator('html').getAttribute('lang') === 'en', 'language switch translates navigation and document language');
  await page.reload();
  await page.locator('#connection-state').filter({ hasText: 'Connected' }).waitFor();
  check(await page.locator('#selected-node').textContent() === 'Select a node', 'unselected graph hint is translated after English reload');
  check(await page.locator('#shell').getAttribute('data-theme') === 'dark' && await page.locator('#shell').getAttribute('data-density') === 'norm' && await page.locator('#shell').getAttribute('data-panel-scheme') === 'rounded' && await page.locator('#shell').getAttribute('data-tooltips') === 'false' && (await page.locator('#shell').getAttribute('class')).includes('menu-collapsed') && await page.locator('html').getAttribute('lang') === 'en', 'all shell preferences survive reload under the application prefix');
  await page.screenshot({ path: path.join(output, 'web-shell-en.png') });
  await page.locator('#shell-menu-toggle').click();
  await page.locator('#shell-language').click();
  await page.locator('#appearance-summary').click();
  await page.locator('[data-appearance="theme"][data-value="cream"]').click();
  await page.locator('[data-appearance="density"][data-value="nano"]').click();
  await page.locator('[data-appearance="panelScheme"][data-value="compact"]').click();
  await page.locator('#shell-tooltips').click();
  await page.keyboard.press('Escape');
  await page.keyboard.press('Tab');
  await page.locator('#shell-menu-toggle').focus();
  await page.locator('.lu-tooltip:not([hidden])').waitFor();
  check(await page.locator('#shell-menu-toggle').getAttribute('aria-describedby') !== null, 'keyboard tooltip is visible and linked through aria-describedby');
  await page.keyboard.press('Escape');
  check(await page.locator('.lu-tooltip:not([hidden])').count() === 0 && await page.locator('#shell-menu-toggle').getAttribute('aria-describedby') === null, 'Escape closes tooltip and removes its accessibility relation');
  check(await page.locator('[title],svg title').count() === 0, 'application uses no duplicate native HTML or SVG title tooltips');
  await page.locator('[data-tab="settings"]').click();
  check(await page.locator('#tab-settings').isVisible(), 'settings root opens interface settings page');
  await page.locator('[data-connection-id="obs"]').click();
  check(await page.locator('[data-settings-detail]').isVisible() && await page.locator('dialog[open]').count() === 0, 'connection tree opens an inline settings page without a modal');
  await page.locator('[data-settings-edit]').click();
  await page.locator('dialog[open]').waitFor();
  check(await page.locator('dialog input[name="name"]').inputValue() === 'OBS — пример', 'editing from the connection page opens its configuration modal');
  await page.getByRole('button', { name: 'Отмена', exact: true }).click();
  await page.locator('[data-tab="graph"]').click();

  const emit = async (source, control, phase = 'down', value = '1', stage = 'input', nodeId = '', modifiers = null) => {
    const id = ++sequence;
    await page.evaluate(d => window.__emit(d), { time: new Date(1700000000000 + id).toISOString(), signalId: 'signal-' + id, nodeId, stage, message: 'event-' + id,
      signal: { id: 'signal-' + id, source, control, phase, value, valueType: 'number', module: 'windows.input', modifiers } });
  };
  const emitBatch = async count => {
    const start = sequence + 1; sequence += count;
    await page.evaluate(({ start, count }) => {
      for (let index = start; index < start + count; index++) window.__emit({ time: new Date(1700000000000 + index).toISOString(), signalId: 'signal-' + index, nodeId: '', stage: 'input', message: 'event-' + index,
        signal: { id: 'signal-' + index, module: 'windows.input', source: 'unwatched', control: 'test', phase: 'change', valueType: 'number', value: String(index) } });
    }, { start, count });
    await page.waitForFunction(last => document.querySelector('#logs tbody tr:last-child')?.textContent.includes('event-' + last), sequence);
  };
  const actionNode = page.locator('.graph-node[data-node-id="action"]');
  const highlight = await page.evaluate(() => {
    const time = new Date().toISOString();
    window.__emit({ time, stage: 'routed', nodeId: 'source', nextNodeId: 'condition' });
    window.__emit({ time, stage: 'blocked', nodeId: 'condition' });
    const frame = document.querySelector('[data-node-id="condition"].graph-failed .graph-node-box');
    const lines = [...document.querySelectorAll('.graph-wire.graph-active')];
    return frame && { frame: getComputedStyle(frame).strokeWidth, wires: lines.map(line => getComputedStyle(line).strokeWidth) };
  });
  check(highlight?.frame === '3px' && highlight.wires.length === 1 && highlight.wires[0] === '4px',
    'blocked condition thickens its frame and only the traversed wire');
  await emit('test', 'demo', 'down', '1', 'intended', 'action');
  check(await actionNode.evaluate(node => !node.classList.contains('graph-active')), 'planned action is not highlighted as completed');
  await emit('test', 'demo', 'down', '1', 'suppressed', 'action');
  check(await actionNode.evaluate(node => node.classList.contains('graph-suppressed') && !node.classList.contains('graph-active')),
    'dry-run action has a distinct highlight');
  check(await page.locator('.graph-delivery .graph-active').count() === 0, 'test output leaves the destination link inactive');
  await emit('test', 'demo', 'down', '1', 'completed', 'action');
  check(await actionNode.evaluate(node => node.classList.contains('graph-active') && !node.classList.contains('graph-suppressed')),
    'completed action gets the success highlight');
  check(await page.locator('.graph-delivery .graph-active').count() === 1 && await page.locator('.graph-destination.graph-active').count() === 1,
    'successful delivery highlights the destination and its wire');
  const pulseColors = await page.evaluate(() => {
    const shell = document.querySelector('.lu-shell');
    const theme = shell.dataset.theme;
    const box = document.querySelector('[data-node-id="action"] > .graph-node-box');
    const read = () => ({
      fill: getComputedStyle(box).fill,
      normalFill: getComputedStyle(document.querySelector('[data-node-id="script"] > .graph-node-box')).fill,
      stroke: getComputedStyle(box).stroke,
      width: getComputedStyle(box).strokeWidth,
      x: box.getBoundingClientRect().x, y: box.getBoundingClientRect().y
    });
    const light = read();
    shell.dataset.theme = 'dark';
    const dark = read();
    shell.dataset.theme = theme;
    return { light, dark };
  });
  check(pulseColors.light.stroke === 'rgb(0, 128, 40)' && pulseColors.dark.stroke === 'rgb(85, 245, 117)' &&
    pulseColors.light.fill === pulseColors.light.normalFill && pulseColors.dark.fill === pulseColors.dark.normalFill &&
    pulseColors.light.width === '4px' && pulseColors.dark.width === '4px' &&
    pulseColors.light.x === pulseColors.dark.x && pulseColors.light.y === pulseColors.dark.y,
  'success changes only the node outline in light and dark themes without moving it');
  const atBottom = () => page.locator('#logs').evaluate(host => host.scrollHeight - host.clientHeight - host.scrollTop < 3);
  await emitBatch(100);
  check(await atBottom(), 'log follows new events while at bottom');
  await page.locator('#logs').evaluate(host => { host.scrollTop = 220; });
  const oldTop = await page.locator('#logs').evaluate(host => host.scrollTop);
  await emitBatch(10);
  check(Math.abs((await page.locator('#logs').evaluate(host => host.scrollTop)) - oldTop) < 2, 'log does not move while reading above the end');
  await page.locator('#logs').evaluate(host => { host.scrollTop = host.scrollHeight; });
  await emitBatch(10);
  check(await atBottom(), 'log resumes following after return to bottom');
  await emitBatch(300);
  check(await page.locator('#logs tbody tr').count() === 400 && await atBottom(), 'bounded log retains 400 rows and follows through trimming');
  const shellBefore = await page.locator('.shell-topbar').boundingBox();
  await page.locator('#logs').hover(); await page.mouse.wheel(0, 1500);
  const shellAfter = await page.locator('.shell-topbar').boundingBox();
  check(shellBefore.y === shellAfter.y && await page.evaluate(() => document.scrollingElement.scrollTop === 0), 'scrolling beyond a data-panel boundary leaves the shell and document fixed');
  const anchor = () => page.locator('#logs').evaluate(host => {
    const top = host.getBoundingClientRect().top + host.querySelector('thead').getBoundingClientRect().height;
    const row = [...host.querySelectorAll('tbody tr')].find(r => r.getBoundingClientRect().top >= top - 1);
    return { id: row.dataset.rowId, y: row.getBoundingClientRect().top };
  });
  await page.locator('#logs').evaluate(host => { host.scrollTop = 1700; });
  const oldAnchor = await anchor();
  await emitBatch(20);
  const newAnchor = await anchor();
  check(oldAnchor.id === newAnchor.id && Math.abs(oldAnchor.y - newAnchor.y) < 2, 'bounded trimming preserves the visible reading anchor');

  await page.locator('[data-tab="devices"]').click();
  await page.getByRole('button', { name: 'Начать обнаружение: Мышь — длинное имя составного устройства', exact: true }).click();
  await page.getByRole('button', { name: 'Начать обнаружение: Клавиатурная часть мыши', exact: true }).click();
  check(await page.getByRole('button', { name: 'Начать обнаружение: Служебный HID', exact: true }).isDisabled(), 'unsupported HID observation is disabled');
  await emit('mouse-a', 'mouse:button1');
  check(await page.locator('#inputs .input-key.pressed').count() === 1, 'first down creates a highlighted input');
  await emit('mouse-a', 'mouse:button1'); await emit('mouse-a', 'mouse:button1', 'up', '0');
  check(await page.locator('#inputs .input-key').count() === 1 && await page.locator('#inputs .pressed').count() === 0, 'repeat and up update the same input instead of duplicating');
  await emit('keyboard-a', 'mouse:button1');
  check(await page.locator('#inputs .input-key').count() === 2, 'identical control codes on different interfaces stay separate');
  await emit('mouse-a', 'mouse:button4');
  check(await page.locator('#inputs .input-key').filter({ hasText: 'Копировать' }).count() === 1, 'discovered input displays saved mapping name');
  await emit('mouse-b', 'mouse:button1');
  check(await page.locator('#inputs .input-key').count() === 3, 'unwatched interface does not create inputs');
  await emit('mouse-a', 'mouse:wheel', 'change', '120');
  await page.waitForFunction(() => !document.querySelector('#inputs .pulse'));
  check(await page.locator('#inputs .input-key').count() === 4, 'wheel pulse expires without another event');
  await page.getByRole('button', { name: 'Остановить обнаружение: Мышь — длинное имя составного устройства', exact: true }).click();
  check(await page.locator('#inputs .pressed').count() === 1, 'row stop clears only its source highlights');
  await page.getByRole('button', { name: 'Начать обнаружение: Мышь — длинное имя составного устройства', exact: true }).click();
  await emit('mouse-a', 'mouse:button4');
  await emit('keyboard-a', 'key:E0:5B');
  await emit('keyboard-a', 'key:12');
  check(mutations.length === 0, 'discovery and diagnostics made no API mutations');

  const mouseCard = page.locator('#inputs .input-key').filter({ hasText: 'Копировать' });
  await mouseCard.locator('.input-label').click();
  check(await page.locator('dialog[open]').count() === 0 && await mouseCard.locator('.input-add').count() === 1,
    'discovery card label is passive and its add control is inside the card');
  await page.locator('#add-discovery-mapping').click();
  await page.locator('dialog label.form-choice').filter({ hasText: 'mouse:button4' }).locator('input').check();
  await page.locator('dialog button[type=submit]').click();
  check(await page.locator('dialog input[name="name"]').inputValue() === 'Копировать', 'mapping table opens the separate event naming form');
  await page.getByRole('button', { name: 'Отмена', exact: true }).click();
  await mouseCard.locator('.input-add').click();
  await page.locator('dialog[data-node-role="event"]').waitFor();
  await page.locator('#discovery-guard[data-state="idle"]').waitFor();
  check(captures.at(-1).allowEditing && captures.at(-1).interfaces.length > 0,
    'opening the event form releases Windows capture but keeps the graph paused');
  const known = page.getByRole('combobox', { name: 'Известные события' });
  await known.selectOption('mouse:button5');
  check(await page.locator('dialog input[name="control"]').inputValue() === 'mouse:button5' &&
    await known.locator('option[value="Копировать"]').count() === 1 &&
    await known.locator('option[value="key:E0:5B"]').count() === 0,
  'mouse event presets include mapped names and mouse controls, and preserve a manual field');
  await page.screenshot({ path: path.join(output, 'web-checks-modal.png') });
  await page.getByRole('button', { name: 'Отмена', exact: true }).click();
  await page.locator('#discovery-guard[data-state="blocked"]').waitFor();
  await emit('@discovery-keyboard', 'key:E0:5B', 'down', '1', 'input', '', { sourceHeld: 64, globalHeld: 64 });
  await emit('@discovery-keyboard', 'key:12', 'down', '1', 'input', '', { sourceHeld: 64, globalHeld: 64 });
  const chord = page.locator('#inputs .input-key').filter({ hasText: 'Win+E' });
  check(await page.locator('#inputs .input-label').getByText('E', { exact: true }).count() > 0,
    'plain key and combination appear simultaneously');
  check(await page.locator('#discovery-modifiers').textContent() === 'Удерживаются · LWin' &&
    await chord.locator('code').textContent() === '12', 'keyboard cards retain scan codes and show detected named chords and held modifiers');
  await chord.locator('.input-add').click();
  await page.locator('dialog input[name="source"][value="keyboard-a"]').check();
  await page.locator('dialog button[type=submit]').click();
  check(await page.locator('dialog input[name="operation"][value="chord"]').isChecked() &&
    await page.locator('dialog input[name="modifiers"]').inputValue() === 'Win',
    'the combination card opens a ready-to-save chord');
  await page.locator('dialog input[name="operation"][value="sequence"]').check();
  await page.getByRole('button', { name: 'Записать последовательность' }).click();
  await page.getByText('Нажмите и отпустите клавиши на выбранном интерфейсе.').waitFor();
  check(!captures.at(-1).allowEditing, 'sequence recording resumes Windows blocking after the form released it');
  for (const [code, phase] of [['key:E0:5B', 'down'], ['key:12', 'down'], ['key:12', 'up'], ['key:E0:5B', 'up']])
    await emit('@discovery-keyboard', code, phase);
  check(await page.locator('dialog textarea[name="steps"]').inputValue() === 'key:E0:5B down\nkey:12 down\nkey:12 up\nkey:E0:5B up',
    'a captured Win chord fills all four sequence steps for the explicitly selected keyboard');
  await page.getByRole('button', { name: 'Отмена', exact: true }).click();
  await page.screenshot({ path: path.join(output, 'web-checks.png') });
  await page.setViewportSize({ width: 900, height: 600 });
  await page.screenshot({ path: path.join(output, 'web-checks-narrow.png') });
  const boxes = await page.locator('#inputs').evaluate(host => ({ area: host.getBoundingClientRect().toJSON(), first: host.querySelector('button').getBoundingClientRect().toJSON(), bodyWidth: document.body.scrollWidth, viewport: innerWidth }));
  check(boxes.area.height >= 70 && boxes.first.bottom <= boxes.area.bottom + 1 && boxes.bodyWidth === boxes.viewport, '900x600 retains discoverable buttons without page horizontal overflow');
  await page.locator('[data-tab="graph"]').click();
  await page.locator('#add-node').click(); await page.locator('dialog[open]').waitFor();
  check(await page.locator('dialog input[type="radio"][name="kind"]').count() === 6, 'role is selected before creating a node');
  check(await page.locator('dialog [data-field="kind"]').evaluate(group => getComputedStyle(group).flexDirection) === 'column',
    'global add lists roles in a column');
  await page.locator('dialog input[name="kind"][value="script"]').check();
  await page.locator('dialog button[type="submit"]').click();
  await page.locator('dialog[data-node-role="script"]').waitFor();
  check(await page.locator('dialog [name="kind"],dialog select,dialog [name="module"]').count() === 0 &&
        await page.locator('dialog input[type="radio"][name="inputType"]').count() === 4,
        'script editor has fixed role, relevant fields and radio port types');
  await page.getByRole('button', { name: 'Отмена', exact: true }).click();
  await page.locator('#add-node').click();
  await page.locator('dialog input[name="kind"][value="source"]').check();
  await page.locator('dialog button[type=submit]').click();
  const devicePicker = page.getByRole('combobox', { name: 'Обнаруженные интерфейсы' });
  await devicePicker.selectOption('keyboard-a');
  check(await page.locator('dialog input[name="source"]').inputValue() === 'keyboard-a' &&
    await page.locator('dialog select[name="plugin"]').inputValue() === 'windows.input' &&
    await devicePicker.locator('option[value="hid-a"]').count() === 0,
    'source presets fill the Windows input interface, exclude auxiliary HID, and keep manual editing available');
  await page.getByRole('button', { name: 'Отмена', exact: true }).click();
  await page.locator('[data-tab="connections"]').click();
  check(!(await page.locator('#connections').textContent()).includes('TEST-ONLY-NOT-A-SECRET'), 'connection table never exposes password');
  await page.locator('#add-connection').click(); await page.locator('dialog[open]').waitFor();
  check(await page.locator('dialog input[name="name"]').count() === 1, 'connection modal is available');
  await page.getByRole('button', { name: 'Отмена', exact: true }).click();
  const outputModes = page.locator('#output-modes');
  const outputSettings = page.locator('#output-settings');
  check(await outputModes.getAttribute('data-mode') === 'global', 'global test is explicitly marked in the toolbar');
  await outputModes.click();
  await page.waitForFunction(() => document.querySelector('#output-modes').getAttribute('aria-pressed') === 'false');
  check(config.dryRun === false && await page.locator('dialog').count() === 0, 'global Test toggles off directly with no dialog');
  await outputModes.click();
  await page.waitForFunction(() => document.querySelector('#output-modes').getAttribute('aria-pressed') === 'true');
  check(config.dryRun === true && await page.locator('dialog').count() === 0, 'global Test toggles on directly with no dialog');
  devices[0].physicalDeviceId = devices[1].physicalDeviceId = 'composite-mouse';
  devices[0].physicalDeviceName = devices[1].physicalDeviceName = 'Test receiver';
  config.nodes.push(
    node('mouse-source', 'Mouse source', 'source', { module: 'windows.input', settings: { source: 'mouse-a' } }),
    node('keyboard-source', 'Keyboard source', 'source', { module: 'windows.input', settings: { source: 'keyboard-a' } }),
    node('action-two', 'Paste', 'action', { module: 'windows.desktop', operation: 'chord', connectionId: 'desktop' })
  );
  await page.locator('#reload').click();
  await page.locator('[data-tab="graph"]').click();
  const section = page.getByRole('button', { name: 'Test receiver · Мышь + клавиатура', exact: true });
  await section.waitFor();
  await section.click();
  check(await section.getAttribute('aria-expanded') === 'false' &&
    await page.locator('.graph-node[data-node-id="mouse-source"], .graph-node[data-node-id="keyboard-source"]').count() === 0 &&
    await actionNode.isVisible(), 'collapsing the physical mouse hides both interfaces and keeps other chains available');
  await section.press('Enter');
  check(await page.locator('.graph-node[data-node-id="mouse-source"], .graph-node[data-node-id="keyboard-source"]').count() === 2,
    'keyboard activation expands both interfaces');
  const receiver = section.locator('..');
  for (const mode of ['pause', 'debug', 'play']) {
    const control = receiver.locator(`[data-section-control="${mode}"]`);
    await control.press('Enter');
    await receiver.locator(`[data-section-control="${mode}"][aria-pressed=true]`).waitFor();
    check(config.nodes.filter(node => ['mouse-source', 'keyboard-source'].includes(node.id))
      .every(node => node.enabled === (mode !== 'pause') && !!node.previewOnly === (mode === 'debug')) &&
      config.nodes.find(node => node.id === 'source').enabled && config.dryRun,
    'zone ' + mode + ' applies only to its two sources and preserves global Test');
  }
  await page.setViewportSize({ width: 1280, height: 900 });
  check(await page.locator('.graph-add .graph-add-dock').count() === 12,
    'each expanded section has six add controls with docking points');
  const dragToAdd = async lane => {
    const start = page.locator('[data-node-id="mouse-source"] .graph-port-out .graph-port-dot');
    const dock = page.locator(`.graph-add[data-add-lane="${lane}"] .graph-add-dock`).first();
    await dock.evaluate(element => element.scrollIntoView({ block: 'nearest', inline: 'nearest' }));
    await page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))));
    const a = await start.boundingBox(), b = await dock.boundingBox();
    await page.mouse.move(a.x + a.width / 2, a.y + a.height / 2);
    await page.mouse.down();
    await page.mouse.move(b.x + b.width / 2, b.y + b.height / 2, { steps: 8 });
    await page.mouse.up();
  };
  await dragToAdd(1);
  await page.locator('dialog[data-node-role="event"]').waitFor();
  await page.locator('dialog input[name="name"]').fill('Docked button');
  await page.locator('dialog input[name="control"]').fill('mouse:button4');
  await page.locator('dialog button[type="submit"]').click();
  await page.locator('dialog').waitFor({ state: 'detached' });
  const docked = config.nodes.find(node => node.name === 'Docked button');
  check(docked?.kind === 'event' && config.edges.some(edge => edge.from === 'mouse-source' && edge.to === docked.id),
    'dropping on the Events dock saves the new node and its link together');
  const beforeChoose = mutations.length;
  await dragToAdd(3);
  await page.locator('dialog input[name="kind"]').first().waitFor();
  check(await page.locator('dialog input[name="kind"]').evaluateAll(options => options.map(option => option.value).join(',')) === 'transform,script',
    'dropping on Processing offers only its two roles');
  await page.getByRole('button', { name: 'Отмена', exact: true }).click();
  check(mutations.length === beforeChoose, 'cancelling a docked role leaves the graph unchanged');
  await actionNode.locator('.graph-node-box').dblclick();
  const shortcutField = page.locator('dialog input[name="keys"]');
  const record = page.locator('dialog .shortcut-record');
  const presets = page.locator('dialog .shortcut-presets');
  await shortcutField.waitFor();
  await page.screenshot({ path: path.join(output, 'web-checks-shortcut.png') });
  await presets.selectOption('Win+E');
  check(await shortcutField.inputValue() === 'Win+E', 'a shortcut reserved by Windows can be chosen without pressing it');
  await record.click();
  const beforeRecording = mutations.length;
  await page.keyboard.press('Control+Shift+K');
  check(await shortcutField.inputValue() === 'Ctrl+Shift+K' && await record.getAttribute('aria-pressed') === 'false' &&
    mutations.length === beforeRecording, 'recording writes the chord to the field without saving or executing it');
  await record.click();
  await page.keyboard.press('Enter');
  check(await shortcutField.inputValue() === 'Enter' && await shortcutField.isVisible(),
    'capturing Enter does not submit the dialog');
  await record.click();
  await page.keyboard.press('Escape');
  check(await shortcutField.inputValue() === 'Enter' && await record.getAttribute('aria-pressed') === 'false' &&
    await shortcutField.isVisible(), 'Escape cancels recording and leaves the dialog and current value intact');
  await shortcutField.fill('LCtrl+SC:2E');
  await page.locator('dialog button[type="submit"]').click();
  await page.locator('dialog').waitFor({ state: 'detached' });
  check(config.nodes.find(node => node.id === 'action').settings.keys === 'LCtrl+SC:2E',
    'special cases still accept the original manual syntax');
  await outputModes.click();
  await page.waitForFunction(() => document.querySelector('#output-modes').getAttribute('aria-pressed') === 'false');
  await outputSettings.click();
  await page.getByRole('button', { name: 'Все действия в пробный' }).click();
  await page.locator('dialog .output-mode-table tbody tr').filter({ hasText: 'Paste' }).locator('input').uncheck();
  await page.locator('dialog button[type=submit]').click();
  await page.locator('dialog').waitFor({ state: 'detached' });
  check(config.dryRun === false && config.nodes.find(n => n.id === 'action').previewOnly &&
    !config.nodes.find(n => n.id === 'action-two').previewOnly &&
    await outputModes.getAttribute('data-mode') === 'scoped', 'bulk Test and an individual override can be saved together');
  check(await actionNode.evaluate(node => node.classList.contains('graph-preview-only')), 'test action is marked on the graph');
  const play = actionNode.locator('.graph-mode[data-action="play"]');
  const pause = actionNode.locator('.graph-mode[data-action="pause"]');
  check(await page.locator('.graph-node').evaluateAll(nodes => nodes.every(node => {
    const title = node.querySelector('.graph-node-title').getBBox();
    const play = node.querySelector('[data-action="play"] rect').getBBox();
    return title.x + title.width <= play.x - 2;
  })), 'node titles leave a gap before the Play and Pause controls');
  check(await play.getAttribute('aria-pressed') === 'true' && await pause.getAttribute('aria-pressed') === 'false',
    'both controls are present with Play selected in test mode');
  check(await play.evaluate(button => getComputedStyle(button.querySelector('rect')).fill === 'rgba(0, 0, 0, 0)' &&
    getComputedStyle(button.querySelector('path')).strokeWidth === '2.5px'),
    'selected Play uses a thicker icon stroke and a transparent hit area');
  await pause.press('Enter');
  await actionNode.locator('.graph-mode-controls[data-mode="off"]').waitFor();
  check(await pause.getAttribute('aria-pressed') === 'true' && await play.getAttribute('aria-pressed') === 'false' &&
    config.nodes.find(n => n.id === 'action').previewOnly, 'Pause disables the action and preserves test mode');
  await play.press('Space');
  await actionNode.locator('.graph-mode-controls[data-mode="test"]').waitFor();
  check(await play.getAttribute('aria-pressed') === 'true' && config.nodes.find(n => n.id === 'action').previewOnly,
    'Play resumes the action in the same test mode');
  await outputSettings.click();
  await page.getByRole('button', { name: 'Все действия в пробный' }).click();
  await page.locator('dialog button[type=submit]').click();
  await page.locator('dialog').waitFor({ state: 'detached' });
  check(await outputModes.getAttribute('data-mode') === 'allActions' && !config.dryRun,
    'all actions in Test has a distinct indicator from the global graph switch');
  await outputSettings.click();
  await page.getByRole('button', { name: 'Все действия в рабочий' }).click();
  await page.locator('dialog button[type=submit]').click();
  await page.locator('dialog').waitFor({ state: 'detached' });
  check(!config.nodes.some(n => n.kind === 'action' && n.previewOnly) &&
    await outputModes.getAttribute('data-mode') === 'live', 'bulk Live clears action test flags');
  for (const [control, mode] of [[pause, 'off'], [play, 'live']]) {
    await control.click();
    await actionNode.locator(`.graph-mode-controls[data-mode="${mode}"]`).waitFor();
    const action = config.nodes.find(n => n.id === 'action');
    check(action.enabled === (mode === 'live') && !action.previewOnly && await control.getAttribute('aria-pressed') === 'true',
      'Direct Tabler node control sets ' + mode);
  }
  devices[0].canSuppress = devices[1].canSuppress = true;
  await Promise.all([
    page.waitForResponse(response => response.url().endsWith('/api/devices')),
    page.locator('#reload').click()
  ]);
  await page.locator('[data-tab="devices"]').click();
  await page.waitForFunction(() => document.querySelectorAll('#devices .captured').length === 2);
  await page.locator('#discovery-guard[data-state="blocked"]').waitFor();
  check(captures.at(-1).interfaces.toSorted().join(',') === 'keyboard-a,mouse-a',
    'discovery captures both interfaces of the configured composite device');
  await page.locator('[data-tab="graph"]').click();
  await page.waitForFunction(() => !document.querySelector('#devices .captured'));
  check(captures.at(-1).interfaces.length === 0, 'leaving device discovery releases capture');
  await page.locator('[data-tab="devices"]').click();
  await page.evaluate(() => window.__testSockets.at(-1).close());
  check(await page.locator('#inputs .pressed').count() === 0 && (await page.locator('#connection-state').textContent()) === 'Нет связи', 'disconnect clears stale pressed indicators');
  await page.locator('[data-tab="graph"]').click();
  const priorEdges = config.edges.length, priorNodes = config.nodes.length;
  const wire = page.locator('.graph-edge').first();
  const wireName = await wire.getAttribute('aria-label');
  await wire.focus();
  await page.keyboard.press('Enter');
  check(await wire.getAttribute('aria-pressed') === 'true' && await page.locator('#delete-link').isEnabled(), 'wire can be selected and enables its Delete link action');
  await page.keyboard.press('Delete');
  await page.locator('.graph-edge').filter({ has: page.locator('.graph-wire-selected') }).waitFor({ state: 'detached' });
  await page.waitForFunction(name => ![...document.querySelectorAll('.graph-edge')].some(edge => edge.getAttribute('aria-label') === name), wireName);
  check(config.edges.length === priorEdges - 1 && config.nodes.length === priorNodes, 'Delete removes only the selected wire and keeps both nodes');
  const addEvent = async (name, operation) => {
    await page.locator('#add-node').click();
    await page.locator('dialog input[name="kind"][value="event"]').check();
    await page.locator('dialog button[type=submit]').click();
    await page.locator('dialog input[name="name"]').fill(name);
    await page.locator(`dialog input[name="operation"][value="${operation}"]`).check();
    await page.locator('dialog button[type=submit]').click();
    await page.locator('dialog[open]').waitFor({ state: 'detached' });
    return config.nodes.find(node => node.name === name).id;
  };
  const firstEvent = await addEvent('First Event', 'match');
  const defaultEvent = await addEvent('Default Event', 'default');
  await page.locator('#links').click();
  await page.locator(`dialog input[name="from"][value="${firstEvent}"]`).check();
  await page.locator(`dialog input[name="to"][value="${defaultEvent}"]`).check();
  check(await page.locator('dialog input[name="outcome"][value="miss"]').isChecked(),
    'a link to Default selects No match');
  await page.locator('dialog button[type=submit]').click();
  await page.locator('dialog[open]').waitFor({ state: 'detached' });
  check(config.edges.some(edge => edge.from === firstEvent && edge.to === defaultEvent && edge.outcome === 'miss') &&
    await page.locator('.graph-edge .graph-wire[stroke-dasharray="5 3"]').count() === 1,
    'No match is saved and rendered as a dashed wire');
  check(consoleErrors.length === 0, 'no browser console or page errors');
  writeFileSync(path.join(output, 'web-checks-results.json'), JSON.stringify({ passed: results.length, results, consoleErrors, mutations, baseUrl, executablePath }, null, 2));
  console.log(`PASS ${results.length}/${results.length}; screenshots and results in local/`);
} catch (error) {
  writeFileSync(path.join(output, 'web-checks-results.json'), JSON.stringify({ passed: results.length, results, consoleErrors, mutations, error: String(error.stack || error) }, null, 2));
  throw error;
} finally { await browser.close(); }
