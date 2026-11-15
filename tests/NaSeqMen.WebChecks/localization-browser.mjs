// SPDX-License-Identifier: MIT
// Isolated source-server browser checks. API and diagnostics are synthetic only.
import assert from 'node:assert/strict';
import { createServer } from 'node:http';
import { readFileSync, existsSync, writeFileSync, mkdirSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import path from 'node:path';
import { chromium } from 'playwright';
const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');
const www = path.join(root, 'src/NaSeqMen.Editor.Web/wwwroot');
const output = path.join(root, 'local');
mkdirSync(output, { recursive: true });
let apiRequests = 0;
const server = createServer((req,res) => {
  const route = new URL(req.url, 'http://localhost').pathname;
  if (route.startsWith('/api/')) apiRequests++;
  if(route === '/forms-fixture') { res.setHeader('Content-Type','text/html'); res.end('<html><body><main class="lu-shell"></main></body></html>'); return; }
  const file=path.resolve(www,'.'+(route === '/' ? '/index.html' : route));
  if(!file.startsWith(www+path.sep)||!existsSync(file)){res.statusCode=404;res.end();return;}
  const types = { '.js': 'text/javascript', '.css': 'text/css', '.html': 'text/html', '.woff2': 'font/woff2', '.woff': 'font/woff', '.svg': 'image/svg+xml' };
  res.setHeader('Content-Type', types[path.extname(file)] || 'application/octet-stream'); res.end(readFileSync(file));
});
await new Promise(resolve=>server.listen(0,'127.0.0.1',resolve));
const executablePath=process.env.NASEQMEN_TEST_CHROME || ['C:/Program Files/Google/Chrome/Application/chrome.exe','C:/Program Files (x86)/Google/Chrome/Application/chrome.exe'].find(existsSync);
assert(executablePath, 'Installed Chrome is required; this test does not download a browser.');
const browser=await chromium.launch({executablePath,headless:true});
const checks=[];
try {
  const page=await browser.newPage();
  await page.goto('http://127.0.0.1:'+server.address().port+'/forms-fixture');
  await page.evaluate(async()=>{
    window.forms=await import('/forms.js');window.i18n=await import('/i18n.js');
    window.config={version:1,dryRun:true,nodes:[{id:'node',name:'Original user name',kind:'source',module:'',connectionId:'connection',operation:'',inputType:'any',outputType:'any',enabled:true,order:0,settings:{control:'Original control',phase:'',privateExtension:'preserve me'}}],connections:[{id:'connection',name:'Источник',module:'external',enabled:true,settings:{}}],edges:[],mappings:[]};
    window.original=JSON.stringify(config);
    window.completion=forms.editNode(config,[{id:'external',name:'Literal fallback',nameKey:'forms.role.action',hasInput:true,actions:[],conditions:[]}], 'node', async candidate=>{window.saved=candidate;});
  });
  assert.equal(await page.locator('label[for]').first().textContent(),'Name');checks.push('EN primary labels');
  await page.locator('input[name=name]').fill('Unsaved user edit');
  await page.locator('input[name=control]').fill('User control');
  await page.evaluate(()=>i18n.setLanguage('ru'));
  assert.equal(await page.locator('label[for]').first().textContent(),'Название');
  assert.equal(await page.locator('input[name=name]').inputValue(),'Unsaved user edit');
  assert.equal(await page.locator('input[name=control]').inputValue(),'User control');checks.push('RU switch preserves unsaved name and settings');
  assert.equal(await page.locator('select[name=plugin] option[value=external]').textContent(),'Действие');checks.push('Descriptor nameKey caption retranslates');
  await page.evaluate(()=>i18n.setLanguage('en'));
  assert.equal(await page.locator('label:has(input[name=connection]:checked) span').textContent(),'Источник');checks.push('User connection name is never translated');
  assert.equal(await page.locator('label:has(input[name=phase][value=down]) span').textContent(),'Pressed (down)');checks.push('Protocol values have translated captions');
  await page.locator('input[name=name]').fill('');await page.getByRole('button',{name:'Save',exact:true}).click();
  assert.equal(await page.locator('.form-status').textContent(),'Fill in the required fields');
  await page.evaluate(()=>i18n.setLanguage('ru'));
  assert.equal(await page.locator('.form-status').textContent(),'Заполните обязательные поля');checks.push('Validation error retranslates live');
  await page.locator('input[name=name]').fill('Saved user edit');await page.getByRole('button',{name:'Сохранить',exact:true}).click();
  assert.equal(await page.evaluate(()=>completion),true);
  assert.equal(await page.evaluate(()=>saved.nodes[0].name),'Saved user edit');
  assert.equal(await page.evaluate(()=>saved.nodes[0].settings.privateExtension),'preserve me');
  assert.equal(await page.evaluate(()=>JSON.stringify(config)===original),true);checks.push('Save clones config and preserves extension fields');
  await page.evaluate(()=>{i18n.setLanguage('en');window.injection=forms.injectEvent(async()=>{throw new Error('Do not send');});});
  assert.equal(await page.locator('label:has(input[name=inputType][value=boolean]) span').textContent(),'Boolean');
  assert.equal(await page.locator('dialog [title]').count(),0);
  assert.equal((await page.locator('dialog').innerText()).includes('forms.'),false);checks.push('No native tooltips or leaked localization keys');
  await page.getByRole('button',{name:'Cancel',exact:true}).click();
  assert.equal(await page.evaluate(()=>injection),false);
  assert.equal(apiRequests,0);checks.push('No real API requests or event injection');
  await checkSeed(page, checks);
  await checkSettings(page, checks);
  await checkApplication(browser, checks);
  assert.equal(apiRequests, 0, 'No API request may escape the mock route');
  writeFileSync(path.join(output, 'web-localization-browser-results.json'),JSON.stringify({passed:checks.length,checks},null,2));
  console.log(JSON.stringify({passed:checks.length,checks},null,2));
} finally { await browser.close();server.close(); }

async function checkSeed(page, checks) {
  await page.evaluate(() => {
    i18n.setLanguage('en'); window.seedSaves = 0;
    window.seed = { name: 'Discovered event', module: 'external', connectionId: 'connection', settings: { source: 'synthetic-device', control: 'mouse:button4', phase: '' } };
    window.seedSnapshot = JSON.stringify(seed);
    window.seedCompletion = forms.editNode(config, [{ id: 'external', name: 'External', actions: [], conditions: [] }], null, async candidate => { window.seedSaves++; window.seedSaved = candidate; }, seed);
  });
  assert.equal(await page.locator('input[name=source]').inputValue(), 'synthetic-device');
  assert.equal(await page.locator('input[name=control]').inputValue(), 'mouse:button4');
  assert.equal(await page.locator('input[name=phase]:checked').inputValue(), '');
  assert.equal(await page.evaluate(() => seedSaves), 0);
  await page.evaluate(() => i18n.setLanguage('ru'));
  assert.equal(await page.locator('input[name=name]').inputValue(), 'Discovered event');
  await page.getByRole('button', { name: 'Сохранить', exact: true }).click();
  assert.equal(await page.evaluate(() => seedCompletion), true);
  assert.equal(await page.evaluate(() => seedSaved.nodes.at(-1).settings.phase), '');
  assert.equal(await page.evaluate(() => JSON.stringify(seed) === seedSnapshot && JSON.stringify(config) === original && seedSaves === 1), true);
  checks.push('Discovered-event seed is a draft; save clones config and seed, preserving both phases');
}

async function dismiss(page) {
  await page.locator('dialog[open] .form-close').click();
  await page.waitForFunction(() => document.querySelectorAll('dialog').length === 0);
}

async function checkSettings(page, checks) {
  await page.evaluate(async () => {
    const { createConnectionSettings } = await import('/connection-settings.js');
    i18n.setLanguage('en');
    window.settingsHost = document.createElement('div'); document.querySelector('.lu-shell').append(settingsHost);
    window.settingsConfig = { nodes: [], edges: [], mappings: [], connections: [
      { id: 'private-internal-id', name: 'User OBS', module: 'obs', enabled: true, settings: { url: 'ws://test-user:URL_PASSWORD@example.invalid:4455?token=URL_TOKEN', password: 'TOP_SECRET', nested: { apiKey: 'NESTED_SECRET', ordinary: 'User data' } } },
      { id: 'second-private-id', name: 'Second user connection', module: 'obs', enabled: false, settings: {} }
    ] };
    window.settingsModules = [{ id: 'obs', name: 'OBS WebSocket', actions: [], conditions: [] }];
    window.settingsOriginal = JSON.stringify(settingsConfig);
    window.settingsEdits = 0; window.settingsSaves = 0;
    window.settingsView = createConnectionSettings(settingsHost, {
      onNavigate(id) { settingsView.show(id, settingsConfig, settingsModules); },
      onEdit(id) { settingsEdits++; forms.editConnection(settingsConfig, settingsModules, id, async () => { settingsSaves++; }); }
    });
    settingsView.show(null, settingsConfig, settingsModules);
  });
  assert.equal(await page.locator('dialog').count(), 0);
  assert.equal(await page.locator('[data-settings-overview] tbody tr').count(), 2);
  await page.locator('[data-settings-filter=name]').fill('User OBS');
  assert.equal(await page.locator('[data-settings-overview] tbody tr').count(), 1);
  await page.locator('[data-settings-overview] tbody button').click();
  assert.equal(await page.locator('dialog').count(), 0);
  assert.equal(await page.locator('[data-settings-detail]').count(), 1);
  const markup = await page.evaluate(() => settingsHost.outerHTML);
  for (const value of ['TOP_SECRET', 'NESTED_SECRET', 'URL_PASSWORD', 'URL_TOKEN', 'test-user', 'private-internal-id']) assert.equal(markup.includes(value), false, `Settings overview must not disclose ${value}`);
  assert.match(markup, /Configured · hidden/);
  await page.evaluate(() => i18n.setLanguage('ru'));
  assert.equal(await page.locator('[data-settings-detail]').count(), 1);
  assert.equal(await page.locator('[data-settings-edit]').textContent(), 'Изменить');
  assert.match(await page.locator('[data-settings-detail]').innerText(), /Задано · скрыто/);
  await page.locator('[data-settings-edit]').click();
  assert.equal(await page.locator('dialog[open]').count(), 1);
  assert.equal(await page.locator('dialog input[name=password]').getAttribute('type'), 'password');
  await dismiss(page);
  assert.equal(await page.evaluate(() => settingsEdits === 1 && settingsSaves === 0 && JSON.stringify(settingsConfig) === settingsOriginal), true);
  await page.evaluate(() => { settingsView.show(null, settingsConfig, settingsModules); i18n.setLanguage('en'); });
  assert.equal(await page.locator('[data-settings-filter=name]').inputValue(), 'User OBS');
  assert.equal(await page.locator('[data-settings-overview] tbody tr').count(), 1);
  checks.push('Settings navigation opens inline details only; Edit opens modal; secrets/IDs never appear in page markup; language and filters persist without config mutation');
}

async function assertNoKeys(page, scope, label) {
  const leaks = await page.locator(scope).evaluateAll(nodes => nodes.flatMap(node => {
    const elements = [node, ...node.querySelectorAll('*')];
    const values = [node.innerText || '', ...elements.flatMap(el => ['data-tooltip', 'aria-label', 'placeholder'].map(attr => el.getAttribute(attr) || ''))];
    return values.flatMap(value => value.match(/\b(?:ui|forms|graph|settings|test)\.[A-Za-z_][A-Za-z0-9_.]*/g) || []);
  }));
  assert.deepEqual(leaks, [], `${label}: no visible or accessible localization keys`);
  assert.equal(await page.locator(`${scope} [title], ${scope} svg title`).count(), 0, `${label}: no native tooltip titles`);
}

async function checkApplication(browser, checks) {
  const context = await browser.newContext({ viewport: { width: 1280, height: 850 }, locale: 'ru-RU' });
  const failures = [], requests = [];
  const config = { version: 1, dryRun: true, nodes: [
    { id: 'source', name: 'User source', kind: 'source', module: 'external', connectionId: 'connection', operation: '', enabled: true, order: 0, inputType: 'any', outputType: 'any', settings: { control: 'demo' } },
    { id: 'action', name: 'User action', kind: 'action', module: 'external', connectionId: 'connection', operation: 'do', enabled: true, order: 1, inputType: 'any', outputType: 'any', settings: { value: 'User setting' } }
  ], edges: [{ from: 'source', to: 'action' }], connections: [
    { id: 'connection', name: 'Источник', module: 'external', enabled: true, settings: {} },
    { id: 'input', name: 'Local input', module: 'windows.input', enabled: true, settings: {} }
  ], mappings: [{ id: 'mapping', name: 'User event', source: 'device', control: 'mouse:button4', policy: 'map' }] };
  const modules = [{ id: 'external', name: 'Canonical fallback module', nameKey: 'test.module', hasInput: true, actions: [{ id: 'do', name: 'Canonical fallback operation', nameKey: 'test.operation', parameters: ['value'] }], conditions: [] }];
  const catalog = {
    en: { 'test.module': 'Localized module', 'test.operation': 'Localized operation', 'test.device': 'Localized device', 'test.diagnostic': 'Received {count}: {reason}', 'test.reason': 'nested reason {value}', 'test.failure': 'Rejected: {reason}' },
    ru: { 'test.module': 'Модуль из каталога', 'test.operation': 'Операция из каталога', 'test.device': 'Устройство из каталога', 'test.diagnostic': 'Получено {count}: {reason}', 'test.reason': 'вложенная причина {value}', 'test.failure': 'Отклонено: {reason}' }
  };
  const error = { code: 'test.failure', args: { reason: { code: 'test.reason', args: { value: 'USER VALUE' } } }, message: 'English legacy fallback' };
  await context.route('**/api/**', async route => {
    const request = route.request(), url = new URL(request.url());
    if (url.pathname !== '/api/devices/capture') requests.push(`${request.method()} ${url.pathname}`);
    let body, status = 200;
    if (url.pathname === '/api/localization') body = catalog;
    else if (url.pathname === '/api/config' && request.method() === 'GET') body = config;
    else if (url.pathname === '/api/modules') body = modules;
    else if (url.pathname === '/api/devices') body = [{ id: 'device', name: 'Canonical fallback device', nameKey: 'test.device', kind: 'mouse', hardwareId: 'TEST/device', canSuppress: false }, { id: 'device-two', name: 'User device two', kind: 'keyboard', hardwareId: 'TEST/device-two', canSuppress: false }];
    else if (url.pathname === '/api/devices/capture' && request.method() === 'POST') body = [];
    else if (url.pathname === '/api/config' && request.method() === 'PUT') { status = 400; body = error; }
    else if (url.pathname === '/api/config/validate') body = { valid: false, errors: ['English legacy fallback'], issues: [error] };
    else { failures.push(`Unexpected API request ${request.method()} ${url.pathname}`); status = 404; body = { error: 'Unexpected synthetic route' }; }
    await route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) });
  });
  await context.addInitScript(() => {
    window.__sockets = [];
    class MockSocket extends EventTarget {
      constructor(url) { super(); this.url = url; this.readyState = 0; window.__sockets.push(this); setTimeout(() => { this.readyState = 1; this.dispatchEvent(new Event('open')); }, 0); }
      close() { this.readyState = 3; this.dispatchEvent(new Event('close')); }
      send() { throw new Error('Diagnostics must not send commands'); }
    }
    window.WebSocket = MockSocket;
    window.__emit = value => window.__sockets.at(-1).dispatchEvent(new MessageEvent('message', { data: JSON.stringify(value) }));
  });
  const page = await context.newPage();
  page.on('pageerror', error => failures.push(error.message));
  await page.goto('http://127.0.0.1:' + server.address().port);
  await page.locator('#connection-state').filter({ hasText: 'Connected' }).waitFor();
  assert.equal(await page.locator('html').getAttribute('lang'), 'en');
  await page.evaluate(async () => { window.forms = await import('/forms.js'); window.i18n = await import('/i18n.js'); });
  checks.push('Full editor defaults to English with a Russian browser locale');
  await page.locator('[data-tab="settings"]').click();
  assert.equal(await page.locator('#settings-overview').isVisible(), true);
  assert.equal(await page.locator('#connection-settings [data-settings-overview]').count(), 1);
  assert.equal(await page.locator('dialog').count(), 0);
  await page.locator('[data-connection-id="connection"]').click();
  assert.equal(await page.locator('#tab-settings').isVisible(), true);
  assert.equal(await page.locator('#settings-overview').isVisible(), false);
  assert.equal(await page.locator('#connection-settings [data-settings-detail]').count(), 1);
  assert.equal(await page.locator('dialog').count(), 0);
  assert.match(await page.locator('#connection-settings').innerText(), /Источник/);
  await page.screenshot({ path: path.join(output, 'web-settings-inline-en.png') });
  await page.locator('#shell-language').click();
  assert.equal(await page.locator('[data-settings-edit]').textContent(), 'Изменить');
  assert.match(await page.locator('#connection-settings').innerText(), /Модуль из каталога/);
  await page.locator('[data-settings-edit]').click();
  assert.equal(await page.locator('dialog input[name=name]').inputValue(), 'Источник');
  await dismiss(page);
  await page.locator('[data-tab="settings"]').click();
  assert.equal(await page.locator('#settings-overview').isVisible(), true);
  await page.locator('[data-settings-overview] tbody tr').filter({ hasText: 'Источник' }).locator('button').click();
  assert.equal(await page.locator('[data-settings-detail]').count(), 1);
  assert.equal(await page.locator('dialog').count(), 0);
  await page.locator('#shell-language').click();
  await page.locator('[data-tab="graph"]').click();
  checks.push('Real shell Settings root and connection child navigate inline; overview row opens page; only page Edit opens modal; selection survives language switch');
  await page.evaluate(() => window.__emit({ time: '2026-11-15T00:00:00Z', nodeId: 'action', stage: 'error', messageCode: 'test.diagnostic', messageArgs: { count: 2, reason: { code: 'test.reason', args: { value: 'USER VALUE' } } }, message: 'English legacy fallback' }));
  await page.locator('#logs tbody tr').filter({ hasText: 'Received 2: nested reason USER VALUE' }).waitFor();
  await page.locator('#validate').click();
  await page.locator('#status').filter({ hasText: 'Rejected: nested reason USER VALUE' }).waitFor();
  await page.locator('#shell-language').click();
  assert.match(await page.locator('#logs tbody').innerText(), /Получено 2: вложенная причина USER VALUE/);
  assert.equal(await page.locator('#status').textContent(), 'Отклонено: вложенная причина USER VALUE');
  await page.locator('#shell-language').click();
  assert.match(await page.locator('#logs tbody').innerText(), /Received 2: nested reason USER VALUE/);
  checks.push('Existing diagnostics and validation issues retranslate nested message arguments EN → RU → EN');

  // Exercise the real app api() Error.message getter through a failed, entirely mocked save.
  await page.locator('#add-node').click();
  await page.locator('dialog button[type=submit]').click();
  await page.locator('dialog input[name=name]').fill('Unsaved backend-error draft');
  await page.getByRole('button', { name: 'Save', exact: true }).click();
  await page.locator('.form-status').filter({ hasText: 'Rejected: nested reason USER VALUE' }).waitFor();
  await page.evaluate(() => i18n.setLanguage('ru'));
  assert.equal(await page.locator('.form-status').textContent(), 'Отклонено: вложенная причина USER VALUE');
  assert.equal(await page.locator('dialog input[name=name]').inputValue(), 'Unsaved backend-error draft');
  await page.evaluate(() => i18n.setLanguage('en'));
  assert.equal(await page.locator('.form-status').textContent(), 'Rejected: nested reason USER VALUE');
  await dismiss(page);
  checks.push('Real app API failure remains structured and retranslates in an open unsaved form');

  await page.locator('[data-tab="devices"]').click();
  await page.locator('#devices tbody tr').nth(0).locator('button').click();
  await page.locator('#devices tbody tr').nth(1).locator('button').first().click();
  for (const source of ['device', 'device-two', 'device']) await page.evaluate(source => window.__emit({
    time: '2026-11-15T00:00:01Z', nodeId: '', stage: 'input', message: 'External user message',
    signal: { id: source, module: 'external', connectionId: 'connection', source, control: 'mouse:button4', phase: 'down', value: '1', valueType: 'number' }
  }), source);
  assert.equal(await page.locator('#inputs .input-group').count(), 2);
  assert.equal(await page.locator('#inputs .input-item').count(), 2);
  assert.equal(await page.locator('.input-group[data-source="device"] .input-key').textContent(), 'User event');
  const beforeDraft = requests.length;
  await page.locator('.input-group[data-source="device"] .input-add').click();
  assert.equal(await page.locator('dialog').getAttribute('data-node-role'), 'event'); assert.equal(await page.locator('dialog [name=kind]').count(), 0);
  assert.equal(await page.locator('dialog input[name=control]').inputValue(), 'User event');
  assert.equal(await page.locator('dialog input[name=phase]:checked').inputValue(), '');
  assert.equal(await page.locator('dialog input[name=module]').count(), 0);
  assert.equal(await page.locator('dialog input[name=connection]:visible').count(), 0);
  await page.getByRole('button', { name: 'Cancel', exact: true }).click();
  await page.waitForFunction(() => !document.querySelector('dialog'));
  assert.equal(requests.length, beforeDraft);
  checks.push('Repeated input stays in its device group; plus opens mapped event draft with both phases; cancel makes no API request');

  await page.getByRole('button', { name: 'Add sequence…', exact: true }).click();
  await page.getByRole('button', { name: 'Record sequence', exact: true }).click();
  await page.evaluate(() => {
    const send = (control, phase, source = 'device-two') => window.__emit({
      stage: 'input', time: new Date().toISOString(), message: 'fixture',
      signal: { module: 'windows.input', connectionId: 'input', source, control, phase, value: '1' }
    });
    send('key:30', 'down', 'device');
    send('key:E0:5C', 'down');
    send('key:12', 'down');
    send('key:12', 'down');
    send('key:12', 'up');
    send('key:E0:5C', 'up');
  });
  assert.equal(await page.locator('dialog textarea[name=steps]').inputValue(),
    ['key:E0:5C down', 'key:12 down', 'key:12 up', 'key:E0:5C up'].join('\n'));
  await page.getByRole('button', { name: 'Cancel', exact: true }).click();
  await page.waitForFunction(() => !document.querySelector('dialog'));
  assert.equal(requests.length, beforeDraft);
  checks.push('Sequence recorder scopes to one interface and connection, ignores repeats and prepares an editable draft');

  for (const language of ['en', 'ru']) {
    await page.evaluate(language => i18n.setLanguage(language), language);
    for (const tab of ['graph', 'devices', 'connections', 'settings']) {
      await page.locator(`[data-tab="${tab}"]`).click();
      await assertNoKeys(page, '#shell', `${language} ${tab}`);
    }
    await page.locator('[data-tab="devices"]').click();
    assert.match(await page.locator('#devices tbody').innerText(), language === 'en' ? /Localized device/ : /Устройство из каталога/);
    for (const kind of ['source', 'event', 'condition', 'transform', 'script', 'action']) {
      await page.evaluate(({ config, modules, kind }) => { const fixture = structuredClone(config); fixture.nodes[0].kind = kind; fixture.nodes[0].operation = kind === 'action' ? 'do' : ''; window.modalCompletion = forms.editNode(fixture, modules, 'source', async () => {}); }, { config, modules, kind });
      assert.equal(await page.locator('dialog select:not([name=plugin]),dialog [name=kind]').count(), 0);
      assert.equal(await page.locator('dialog').getAttribute('data-node-role'), kind);
      if (['source', 'condition', 'action'].includes(kind)) assert.equal(await page.locator('select[name=plugin] option[value=external]').textContent(), language === 'en' ? 'Localized module' : 'Модуль из каталога');
      if (kind === 'action') {
        assert.equal(await page.locator('label:has(input[name=operation][value=do]) span').textContent(), language === 'en' ? 'Localized operation' : 'Операция из каталога');
        await page.locator('dialog input[name=value]').fill('Unsaved operation parameter');
        await page.evaluate(language => i18n.setLanguage(language === 'en' ? 'ru' : 'en'), language);
        assert.equal(await page.locator('label:has(input[name=operation][value=do]) span').textContent(), language === 'en' ? 'Операция из каталога' : 'Localized operation');
        assert.equal(await page.locator('select[name=plugin] option[value=external]').textContent(), language === 'en' ? 'Модуль из каталога' : 'Localized module');
        assert.equal(await page.locator('dialog input[name=value]').inputValue(), 'Unsaved operation parameter');
        assert.equal(await page.locator('input[name=operation]:checked').inputValue(), 'do');
        await page.evaluate(language => i18n.setLanguage(language), language);
      }
      await assertNoKeys(page, 'dialog', `${language} ${kind}`);
      await dismiss(page);
    }
    for (const type of ['connection', 'mapping', 'links', 'inject', 'details']) {
      await page.evaluate(({ type, config, modules }) => {
        if (type === 'connection') forms.editConnection(config, modules, 'connection', async () => {});
        if (type === 'mapping') forms.editMapping(config, 'mapping', { source: 'device', control: 'mouse:button4', phase: 'down', value: '1', time: '12:00' }, async () => {});
        if (type === 'links') forms.editLinks(config, async () => {});
        if (type === 'inject') forms.injectEvent(async () => {});
        if (type === 'details') forms.showDetails(i18n.t('ui.diagnostics'), 'USER TEXT');
      }, { type, config, modules });
      await assertNoKeys(page, 'dialog', `${language} ${type}`);
      await dismiss(page);
    }
    await page.locator('#logs tbody tr').first().dblclick();
    await assertNoKeys(page, 'dialog', `${language} actual diagnostic details`);
    await dismiss(page);
    checks.push(`${language.toUpperCase()}: all tabs, all node roles, connection/mapping/links/inject/details dialogs have no key leaks; descriptors use nameKey`);
  }
  assert.deepEqual(failures, []);
  assert.equal(requests.some(value => value.includes('/api/localization')), true);
  checks.push('All API traffic is fulfilled locally; no real executor writes, external event injection, or browser errors');
  await context.close();
}

