# SPDX-License-Identifier: MIT
"""Isolated end-to-end checks. Uses only a test marker action, never real input/OBS."""
import asyncio
import copy
import json
import pathlib
import shutil
import subprocess
import sys
import time
import urllib.error
import urllib.request
import websockets

ROOT = pathlib.Path(__file__).resolve().parents[1]
WORK = ROOT / 'local' / ('checks-' + time.strftime('%Y%m%d-%H%M%S'))
PORT = 17964
BASE = f'http://127.0.0.1:{PORT}'
TOKEN = ''
RESULTS = []


def check(ok, name):
    RESULTS.append({'name': name, 'passed': bool(ok)})
    print(('PASS ' if ok else 'FAIL ') + name, flush=True)
    if not ok:
        raise AssertionError(name)


def api(method, path, data=None, auth=True, extra=None):
    headers = {'Content-Type': 'application/json'}
    if auth:
        headers['X-NaSeqMen-Token'] = TOKEN
    if extra:
        headers.update(extra)
    request = urllib.request.Request(BASE + path, data=None if data is None else json.dumps(data).encode(), method=method, headers=headers)
    try:
        with urllib.request.urlopen(request, timeout=15) as response:
            raw = response.read()
            return response.status, json.loads(raw) if raw else None
    except urllib.error.HTTPError as error:
        raw = error.read()
        return error.code, json.loads(raw) if raw else None


def config(dry=True):
    return {
        'version': 1, 'dryRun': dry,
        'connections': [{'id': 'probe', 'name': 'Probe', 'module': 'checks.probe', 'enabled': True, 'settings': {}}],
        'nodes': [
            {'id': 's', 'name': 'Input', 'kind': 'source', 'module': 'test', 'settings': {'control': 'demo'}},
            {'id': 'c', 'name': 'Down', 'kind': 'condition', 'operation': 'equals', 'settings': {'field': 'phase', 'value': 'down'}},
            {'id': 't', 'name': 'Scale', 'kind': 'transform', 'operation': 'scale', 'settings': {'factor': '2', 'offset': '1'}},
            {'id': 'a', 'name': 'Marker', 'kind': 'action', 'module': 'checks.probe', 'connectionId': 'probe', 'operation': 'mark', 'settings': {'path': str(WORK / 'marker.txt')}},
        ],
        'edges': [{'from': 's', 'to': 'c'}, {'from': 'c', 'to': 't'}, {'from': 't', 'to': 'a'}], 'mappings': []}


async def trace_until(ws, terminal, signal_id=None):
    events = []
    while True:
        event = json.loads(await asyncio.wait_for(ws.recv(), 10))
        if signal_id and event['signalId'] != signal_id:
            continue
        events.append(event)
        if event['stage'] == 'error' and not terminal(event):
            raise AssertionError('Unexpected runtime error: ' + event['message'])
        if terminal(event):
            return events


async def inject(ws, phase='down', value='3', control='demo', terminal='suppressed'):
    status, body = api('POST', '/api/inject', {'module': 'spoof-device', 'source': 'spoof', 'control': control, 'phase': phase, 'valueType': 'number', 'value': value})
    check(status == 200 and body['accepted'], 'event accepted ' + phase)
    return await trace_until(ws, lambda event: event['stage'] == terminal)


async def run():
    check(api('GET', '/api/config', auth=False)[0] == 401, 'configuration requires token')
    check(api('GET', '/api/config', extra={'Origin': 'https://example.invalid'})[0] == 403, 'foreign origin rejected')
    check(api('GET', '/api/modules')[1][0]['id'] == 'checks.probe', 'test plugin loaded independently')
    good = config()
    check(api('PUT', '/api/config', good)[0] == 200, 'configuration applied')
    async with websockets.connect(f'ws://127.0.0.1:{PORT}/api/diagnostics', additional_headers={'X-NaSeqMen-Token': TOKEN}) as ws:
        events = await inject(ws)
        check(any(e['stage'] == 'transformed' and e['signal']['value'] == '7' for e in events), 'numeric transformation reaches action')
        check(all(e.get('signal', {}).get('module') == 'test' for e in events if e.get('signal')), 'injection cannot impersonate physical module')
        check(not (WORK / 'marker.txt').exists(), 'dryrun never invokes plugin side effect')
        events = await inject(ws, phase='up', terminal='blocked')
        check(not any(e['stage'] == 'intended' for e in events), 'failed condition has no action')
        bad = copy.deepcopy(good)
        bad['edges'].append({'from': 'a', 'to': 'c'})
        check(api('PUT', '/api/config', bad)[0] == 400, 'cycle rejected')
        check(len(api('GET', '/api/config')[1]['edges']) == 3, 'invalid graph preserves current configuration')
        bad = copy.deepcopy(good)
        bad['mappings'] = [{'id': 'm', 'source': 'x', 'control': 'y', 'name': 'bad', 'policy': 'suppress'}]
        check(api('POST', '/api/config/validate', bad)[1]['valid'] is False, 'unsupported physical suppression rejected')
        bad = copy.deepcopy(good)
        bad['nodes'][1]['outputType'] = 'text'
        check(api('POST', '/api/config/validate', bad)[1]['valid'] is False, 'incompatible known port types rejected')
        mapped = copy.deepcopy(good)
        mapped['nodes'][0]['settings']['control'] = 'Named button'
        mapped['mappings'] = [{'id': 'm', 'source': 'synthetic', 'control': 'demo', 'name': 'Named button', 'policy': 'map'}]
        check(api('PUT', '/api/config', mapped)[0] == 200, 'named input mapping saved')
        events = await inject(ws)
        check(any(e['stage'] == 'input' and e['signal']['control'] == 'demo' for e in events) and any(e['nodeId'] == 's' and e['signal']['control'] == 'Named button' for e in events), 'raw learning input and mapped graph input remain distinct')
        check(any(e['stage'] == 'routed' and e.get('nextNodeId') == 'a' for e in events), 'wire diagnostics identify actual traversed edge')
        scripted = copy.deepcopy(good)
        scripted['nodes'][2] = {'id': 't', 'name': 'C#', 'kind': 'script', 'settings': {'code': 'Context.Trace("script-ran"); return Input with { Value = "42" };'}}
        check(api('PUT', '/api/config', scripted)[0] == 200, 'real C# compiles on apply')
        events = await inject(ws)
        check(any(e['stage'] == 'script' and e.get('signal', {}).get('value') == '42' for e in events), 'C# output flows to next node')
        broken = copy.deepcopy(scripted)
        broken['nodes'][2]['settings']['code'] = 'this is not valid C#'
        check(api('PUT', '/api/config', broken)[0] == 400, 'C# compile error rejected before activation')
        check(api('GET', '/api/config')[1]['nodes'][2]['settings']['code'] == scripted['nodes'][2]['settings']['code'], 'last valid script remains configured')
        host_script = copy.deepcopy(scripted)
        path_literal = json.dumps(str(WORK / 'marker.txt'))
        host_script['nodes'][2]['settings']['code'] = 'await Context.EmitAsync("checks.probe", "probe", "mark", new Dictionary<string,string> { ["path"] = ' + path_literal + ' }); return null;'
        check(api('PUT', '/api/config', host_script)[0] == 200, 'C# host action compiles')
        await inject(ws, terminal='blocked')
        check(not (WORK / 'marker.txt').exists(), 'C# host actions share dryrun gate')
        check(api('PUT', '/api/config', config(False))[0] == 200, 'test-only live action enabled')
        await inject(ws, terminal='completed')
        check((WORK / 'marker.txt').read_text().strip() == '7', 'live mode invokes only test plugin')
    # Without the editor/diagnostic client the executor still performs its work.
    api('POST', '/api/inject', {'control': 'demo', 'phase': 'down', 'valueType': 'number', 'value': '4'})
    for _ in range(40):
        if len((WORK / 'marker.txt').read_text().splitlines()) == 2:
            break
        await asyncio.sleep(.05)
    check((WORK / 'marker.txt').read_text().splitlines() == ['7', '9'], 'executor works without editor or trace subscriber')
    async with websockets.connect(f'ws://127.0.0.1:{PORT}/api/diagnostics', additional_headers={'X-NaSeqMen-Token': TOKEN}) as ws:
        try:
            await asyncio.wait_for(ws.recv(), .4)
            no_replay = False
        except asyncio.TimeoutError:
            no_replay = True
        check(no_replay, 'new diagnostic connection receives no replay')
    check(api('PUT', '/api/config', good)[0] == 200, 'test runtime restored to dryrun')


if __name__ == '__main__':
    WORK.mkdir(parents=True)
    plugin_dir = WORK / 'artifacts' / 'alpha' / 'modules'
    subprocess.run(['dotnet', 'publish', str(ROOT / 'tests/NaSeqMen.ProbeModule/NaSeqMen.ProbeModule.csproj'), '-c', 'Release', '-o', str(plugin_dir)], cwd=ROOT, check=True, stdout=subprocess.DEVNULL)
    executable = ROOT / 'artifacts/alpha/executor/NaSeqMen.Executor.exe'
    with (WORK / 'executor.log').open('w', encoding='utf-8') as log:
        process = subprocess.Popen([str(executable), '--root', str(WORK), '--port', str(PORT), '--no-tray'], cwd=ROOT, stdout=log, stderr=subprocess.STDOUT, creationflags=subprocess.CREATE_NO_WINDOW)
        try:
            for _ in range(100):
                if process.poll() is not None:
                    raise RuntimeError('Executor exited; see ' + str(WORK / 'executor.log'))
                try:
                    if api('GET', '/api/health', auth=False)[0] == 200:
                        break
                except OSError:
                    pass
                time.sleep(.1)
            else:
                raise TimeoutError('Executor did not become ready')
            TOKEN = (WORK / 'local/api-token.txt').read_text().strip()
            asyncio.run(run())
        finally:
            process.terminate()
            process.wait(timeout=10)
            (WORK / 'results.json').write_text(json.dumps(RESULTS, ensure_ascii=False, indent=2), encoding='utf-8')
            print('Evidence: ' + str(WORK))
