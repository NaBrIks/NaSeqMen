# SPDX-License-Identifier: MIT
"""Isolated web proxy checks against the existing executor; no config writes or injections."""
import asyncio
import json
import pathlib
import subprocess
import time
import urllib.error
import urllib.request

import websockets

ROOT = pathlib.Path(__file__).resolve().parents[1]
PORT = 17965
BASE = f'http://127.0.0.1:{PORT}'
RESULTS = []


def check(condition, name):
    RESULTS.append({'name': name, 'passed': bool(condition)})
    print(('PASS ' if condition else 'FAIL ') + name, flush=True)
    if not condition:
        raise AssertionError(name)


def request(path, method='GET', data=None, headers=None):
    request_headers = {'Origin': BASE, **(headers or {})}
    request_headers = {key: value for key, value in request_headers.items() if value is not None}
    if data is not None:
        request_headers.setdefault('Content-Type', 'application/json')
    req = urllib.request.Request(BASE + path, data=data, method=method, headers=request_headers)
    try:
        with urllib.request.urlopen(req, timeout=12) as response:
            return response.status, response.read(), response.headers
    except urllib.error.HTTPError as error:
        return error.code, error.read(), error.headers


async def websocket_checks():
    for origin in ['https://example.invalid', None]:
        try:
            async with websockets.connect(BASE.replace('http:', 'ws:') + '/api/diagnostics', origin=origin):
                check(False, 'websocket rejects foreign or missing origin')
        except websockets.exceptions.InvalidStatus as error:
            check(error.response.status_code == 403, 'websocket rejects foreign or missing origin')
    async with websockets.connect(BASE.replace('http:', 'ws:') + '/api/diagnostics', origin=BASE) as socket:
        # The executor diagnostics endpoint ignores client data; this is not api/inject.
        await socket.send('proxy transport check')
        await socket.close(code=1000, reason='read-only check complete')
        check(socket.close_code == 1000 and socket.close_reason == 'read-only check complete', 'websocket handshake and close code/reason survive proxy')
    async with websockets.connect(BASE.replace('http:', 'ws:') + '/api/diagnostics', origin=BASE) as socket:
        await socket.send('x' * (2 * 1024 * 1024 + 1))
        try:
            while True:
                await asyncio.wait_for(socket.recv(), timeout=8)
        except websockets.exceptions.ConnectionClosed as error:
            check(error.rcvd is not None and error.rcvd.code == 1009, 'oversized websocket message closes with 1009')


async def run():
    status, raw, _ = request('/web-health')
    check(status == 200 and json.loads(raw)['name'] == 'NaSeqMen.Editor.Web', 'web process identity')
    status, raw, headers = request('/api/health')
    check(status == 200 and json.loads(raw)['name'] == 'NaSeqMen.Executor', 'executor health relayed')
    check(headers.get('Cache-Control') == 'no-store' and "frame-ancestors 'none'" in headers.get('Content-Security-Policy', ''), 'browser response has no-store and frame policy')
    config_status, config, _ = request('/api/config')
    check(config_status == 200 and isinstance(json.loads(config)['nodes'], list), 'configuration read through server authentication')
    page_status, page, _ = request('/')
    check(page_status == 200 and b'<html' in page.lower(), 'HTML entry page served from wwwroot')
    token = (ROOT / 'local' / 'api-token.txt').read_bytes().strip()
    check(token not in config and token not in page and token not in raw, 'executor token absent from configuration, HTML and health responses')
    for path in ['/api/modules', '/api/devices']:
        status, data, _ = request(path)
        check(status == 200 and isinstance(json.loads(data), list), path + ' relayed')
    status, result, _ = request('/api/config/validate', 'POST', config)
    check(status == 200 and json.loads(result)['valid'], 'current configuration validates without applying it')
    check(request('/api/config', headers={'Origin': 'https://example.invalid'})[0] == 403, 'foreign Origin rejected')
    check(request('/api/config', headers={'Host': 'attacker.example:' + str(PORT)})[0] == 403, 'foreign Host rejected against DNS rebinding')
    check(request('/api/config', headers={'Sec-Fetch-Site': 'cross-site'})[0] == 403, 'cross-site browser metadata rejected')
    navigation = {'Origin': None, 'Sec-Fetch-Site': 'cross-site', 'Sec-Fetch-Mode': 'navigate', 'Sec-Fetch-Dest': 'document'}
    check(request('/', headers=navigation)[0] == 200, 'external top-level link can open editor entry')
    check(request('/index.html', headers=navigation)[0] == 200, 'external top-level link can open index.html')
    check(request('/api/config', headers=navigation)[0] == 403, 'entry navigation exception never exposes API')
    check(request('/', headers={**navigation, 'Sec-Fetch-Dest': 'iframe'})[0] == 403, 'cross-site iframe cannot use entry navigation exception')
    check(request('/', headers={**navigation, 'Origin': 'https://example.invalid'})[0] == 403, 'entry navigation keeps foreign Origin rejection')
    check(request('/api/config', 'PUT', b'{}', {'Origin': 'https://example.invalid'})[0] == 403, 'cross-origin mutation rejected before forwarding')
    check(request('/api/config/validate', 'POST', config, {'Content-Type': 'text/plain'})[0] == 415, 'non-JSON request rejected')
    check(request('/api/config/validate', 'POST', b' ' * (2 * 1024 * 1024 + 1))[0] == 413, 'HTTP request size bounded')
    check(request('/api/config?url=http://example.invalid')[0] == 400, 'destination query not accepted')
    check(request('/api/not-allowed')[0] == 404, 'unlisted API route not proxied')
    check(request('/local/api-token.txt')[0] == 404, 'local token file is outside static web root')
    await websocket_checks()
    check(request('/api/config')[1] == config, 'all checks leave executor configuration unchanged')


if __name__ == '__main__':
    output = ROOT / 'local' / ('web-backend-checks-' + time.strftime('%Y%m%d-%H%M%S'))
    output.mkdir(parents=True)
    binary = ROOT / 'src' / 'NaSeqMen.Editor.Web' / 'bin' / 'Release' / 'net11.0' / 'NaSeqMen.Editor.Web.dll'
    with (output / 'stdout.log').open('w', encoding='utf-8') as stdout, (output / 'stderr.log').open('w', encoding='utf-8') as stderr:
        process = subprocess.Popen(['dotnet', str(binary), '--root', str(ROOT), '--port', str(PORT)], cwd=ROOT, stdout=stdout, stderr=stderr, creationflags=subprocess.CREATE_NO_WINDOW)
        try:
            for _ in range(60):
                if process.poll() is not None:
                    raise RuntimeError('Test web process exited; see its local stderr.log')
                try:
                    if request('/web-health')[0] == 200:
                        break
                except (OSError, urllib.error.URLError):
                    pass
                time.sleep(.2)
            else:
                raise RuntimeError('Test web process did not become ready')
            asyncio.run(run())
        finally:
            process.terminate()
            process.wait(timeout=10)
            (output / 'results.json').write_text(json.dumps(RESULTS, indent=2), encoding='utf-8')
    print('WEB_BACKEND_CHECKS_OK', len(RESULTS))
