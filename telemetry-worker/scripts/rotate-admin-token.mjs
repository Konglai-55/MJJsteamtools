import { randomBytes } from 'node:crypto';
import { spawn, spawnSync } from 'node:child_process';
import { createServer } from 'node:http';
import { resolve } from 'node:path';
import { EnvHttpProxyAgent, setGlobalDispatcher } from 'undici';

setGlobalDispatcher(new EnvHttpProxyAgent());

const workerOrigin = 'https://mjjsteamtools-telemetry.konglai55.workers.dev';
const testInstallationId = 'f'.repeat(64);
const wrangler = resolve('node_modules', 'wrangler', 'bin', 'wrangler.js');
const adminToken = randomBytes(32).toString('base64url');

function runWrangler(args, { input, quiet = false } = {}) {
  const result = spawnSync(process.execPath, [wrangler, ...args], {
    cwd: process.cwd(),
    input,
    encoding: 'utf8',
    stdio: quiet ? ['pipe', 'pipe', 'pipe'] : ['pipe', 'inherit', 'inherit'],
  });

  if (result.error)
    throw result.error;
  if (result.status !== 0) {
    const detail = quiet ? String(result.stderr || result.stdout || '').trim() : '';
    throw new Error(`Wrangler failed (${result.status})${detail ? `: ${detail}` : ''}`);
  }
}

function wait(milliseconds) {
  return new Promise(resolvePromise => setTimeout(resolvePromise, milliseconds));
}

async function readStatsWithRetry() {
  for (let attempt = 1; attempt <= 12; attempt++) {
    const response = await fetch(`${workerOrigin}/api/stats`, {
      headers: { authorization: `Bearer ${adminToken}` },
      cache: 'no-store',
    });
    if (response.ok)
      return response.json();
    if (response.status !== 401)
      throw new Error(`Statistics API returned HTTP ${response.status}`);
    await wait(2500);
  }
  throw new Error('The new administrator token did not become active within 30 seconds');
}

function cleanTestData() {
  const sql = `DELETE FROM daily_activity WHERE id_hash = '${testInstallationId}'; ` +
    `DELETE FROM installations WHERE id_hash = '${testInstallationId}';`;
  runWrangler([
    'd1', 'execute', 'mjjsteamtools-telemetry', '--remote', '--command', sql,
  ], { quiet: true });
}

function showOneTimeTokenPage() {
  return new Promise((resolvePromise, reject) => {
    let served = false;
    const html = `<!doctype html>
<html lang="zh-CN"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>MJJST 管理员令牌</title><style>
body{margin:0;background:#101923;color:#f4f7fa;font:16px "Segoe UI","Microsoft YaHei",sans-serif}.box{max-width:620px;margin:12vh auto;padding:28px;background:#16202d;border:1px solid #30475b;border-radius:14px}h1{font-size:24px}p{color:#9fb1c1;line-height:1.6}input{box-sizing:border-box;width:100%;padding:12px;background:#0f1822;color:#fff;border:1px solid #30475b;border-radius:8px}button,a{display:inline-block;margin:14px 10px 0 0;padding:11px 18px;border:0;border-radius:8px;background:linear-gradient(100deg,#2d9cf0,#225cce);color:#fff;text-decoration:none;cursor:pointer}
</style></head><body><main class="box"><h1>部署完成</h1><p>这是统计面板管理员令牌。点击复制并保存到密码管理器；关闭本页后无法再次查看，只能重新生成。</p><input id="token" readonly value="${adminToken}"><button id="copy" onclick="navigator.clipboard.writeText(document.getElementById('token').value).then(() => this.textContent='已复制')">复制管理员令牌</button><a href="${workerOrigin}/admin" target="_blank">打开统计面板</a></main></body></html>`;

    const server = createServer((request, response) => {
      if (served) {
        response.writeHead(410).end('This one-time page has expired.');
        return;
      }
      served = true;
      response.writeHead(200, {
        'content-type': 'text/html; charset=utf-8',
        'cache-control': 'no-store',
        'x-content-type-options': 'nosniff',
      });
      response.end(html);
      setTimeout(() => server.close(() => resolvePromise()), 500);
    });

    server.on('error', reject);
    server.listen(0, '127.0.0.1', () => {
      const address = server.address();
      const url = `http://127.0.0.1:${address.port}/`;
      const browser = spawn('rundll32.exe', ['url.dll,FileProtocolHandler', url], {
        detached: true,
        stdio: 'ignore',
      });
      browser.unref();
    });

    setTimeout(() => {
      if (!served) {
        server.close();
        reject(new Error('The one-time token page was not opened within 30 seconds'));
      }
    }, 30_000).unref();
  });
}

async function main() {
  cleanTestData();
  runWrangler(['secret', 'put', 'ADMIN_TOKEN'], { input: `${adminToken}\n` });

  const heartbeatResponse = await fetch(`${workerOrigin}/api/heartbeat`, {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify({
      installationId: testInstallationId,
      appVersion: 'deployment-test',
      sessionId: 'ffffffff-ffff-ffff-ffff-ffffffffffff',
    }),
  });
  if (heartbeatResponse.status !== 204)
    throw new Error(`Heartbeat API returned HTTP ${heartbeatResponse.status}`);

  const stats = await readStatsWithRetry();
  cleanTestData();
  await showOneTimeTokenPage();
  console.log(`HEARTBEAT_STATUS=204 TOTAL_DURING_TEST=${stats.total} TOKEN_PAGE_OPENED=1 TEST_DATA_REMOVED=1`);
}

main().catch(error => {
  try { cleanTestData(); } catch { }
  console.error(error instanceof Error ? error.message : String(error));
  process.exitCode = 1;
});
