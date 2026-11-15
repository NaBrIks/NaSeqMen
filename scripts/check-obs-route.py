# SPDX-License-Identifier: MIT
"""Explicit read-only integration against the dedicated C:\\OBS test instance."""
import asyncio
import importlib.util
import json
import pathlib
import shutil
import subprocess
import time
import websockets
spec = importlib.util.spec_from_file_location('check_alpha', pathlib.Path(__file__).with_name('check-alpha.py'))
test = importlib.util.module_from_spec(spec)
spec.loader.exec_module(test)

test.WORK = test.ROOT / 'local' / ('obs-route-' + time.strftime('%Y%m%d-%H%M%S'))
test.PORT = 17965
test.BASE = f'http://127.0.0.1:{test.PORT}'
test.WORK.mkdir(parents=True)
settings = json.loads((test.ROOT / 'local/obs-experiment.json').read_text(encoding='utf-8-sig'))
modules = test.WORK / 'artifacts/alpha/modules'
modules.mkdir(parents=True)
for filename in ('NaSeqMen.Obs.dll', 'NaSeqMen.Contracts.dll'):
    shutil.copy2(test.ROOT / 'artifacts/alpha/modules' / filename, modules / filename)
config = {
    'version': 1, 'dryRun': False,
    'connections': [{'id': 'experiment', 'name': 'Explicit read-only OBS check', 'module': 'obs', 'enabled': True,
                     'settings': {'url': settings['address'], 'password': settings['password']}}],
    'nodes': [], 'edges': [], 'mappings': []}
for query in ('GetVersion', 'GetSceneList', 'GetRecordStatus'):
    config['nodes'] += [
        {'id': query + '-input', 'name': query, 'kind': 'source', 'module': 'test', 'settings': {'control': query}},
        {'id': query + '-request', 'name': query, 'kind': 'action', 'module': 'obs', 'connectionId': 'experiment', 'operation': 'request', 'settings': {'type': query, 'data': '{}'}},
        {'id': query + '-response', 'name': query, 'kind': 'source', 'module': 'obs', 'connectionId': 'experiment', 'settings': {'control': query, 'source': 'response'}},
        {'id': query + '-view', 'name': query, 'kind': 'transform', 'operation': 'text', 'settings': {'template': '{value}'}}]
    config['edges'] += [{'from': query + '-input', 'to': query + '-request'}, {'from': query + '-response', 'to': query + '-view'}]
(test.WORK / 'local').mkdir()
(test.WORK / 'local/config.json').write_text(json.dumps(config), encoding='utf-8')


async def run():
    async with websockets.connect(f'ws://127.0.0.1:{test.PORT}/api/diagnostics', additional_headers={'X-NaSeqMen-Token': test.TOKEN}) as ws:
        # Explicitly query through the graph, not directly through an OBS helper.
        for query in ('GetVersion', 'GetSceneList', 'GetRecordStatus'):
            status, _ = test.api('POST', '/api/inject', {'control': query, 'phase': 'down', 'valueType': 'text', 'value': ''})
            test.check(status == 200, query + ' entered graph')
            events = await test.trace_until(ws, lambda e: e['nodeId'] == query + '-view' and e['stage'] == 'transformed')
            test.check(any(e['nodeId'] == query + '-request' and e['stage'] == 'completed' for e in events), query + ' real request completed')
            response = events[-1]['signal']
            test.check(response['connectionId'] == 'experiment' and response['module'] == 'obs', query + ' response routed through same connection')
            if query == 'GetRecordStatus':
                test.check(json.loads(response['value'])['outputActive'] is False, 'dedicated OBS recording remains off')


with (test.WORK / 'executor.log').open('w', encoding='utf-8') as log:
    process = subprocess.Popen([str(test.ROOT / 'artifacts/alpha/executor/NaSeqMen.Executor.exe'), '--root', str(test.WORK), '--port', str(test.PORT), '--no-tray'], cwd=test.ROOT, stdout=log, stderr=subprocess.STDOUT, creationflags=subprocess.CREATE_NO_WINDOW)
    try:
        for _ in range(150):
            if process.poll() is not None:
                raise RuntimeError('Test executor exited')
            try:
                if test.api('GET', '/api/health', auth=False)[0] == 200:
                    break
            except OSError:
                pass
            time.sleep(.1)
        else:
            raise TimeoutError('Test executor unavailable')
        test.TOKEN = (test.WORK / 'local/api-token.txt').read_text().strip()
        asyncio.run(run())
    finally:
        process.terminate()
        process.wait(timeout=10)
        (test.WORK / 'results.json').write_text(json.dumps(test.RESULTS, ensure_ascii=False, indent=2), encoding='utf-8')
        print('Evidence: ' + str(test.WORK))
