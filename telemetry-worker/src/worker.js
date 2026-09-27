import { ADMIN_PAGE } from './admin-page.js';

const JSON_HEADERS = {
  'content-type': 'application/json; charset=utf-8',
  'cache-control': 'no-store',
};

export default {
  async fetch(request, env) {
    const url = new URL(request.url);

    if (request.method === 'OPTIONS' && url.pathname === '/api/heartbeat') {
      return new Response(null, {
        status: 204,
        headers: {
          'access-control-allow-origin': '*',
          'access-control-allow-methods': 'POST, OPTIONS',
          'access-control-allow-headers': 'content-type',
          'access-control-max-age': '86400',
        },
      });
    }

    if (request.method === 'POST' && url.pathname === '/api/heartbeat')
      return recordHeartbeat(request, env);

    if (request.method === 'GET' && url.pathname === '/api/stats')
      return getStats(request, env);

    if (request.method === 'GET' && (url.pathname === '/' || url.pathname === '/admin')) {
      return new Response(ADMIN_PAGE, {
        headers: {
          'content-type': 'text/html; charset=utf-8',
          'cache-control': 'no-store',
          'x-content-type-options': 'nosniff',
          'x-frame-options': 'DENY',
          'referrer-policy': 'no-referrer',
          'content-security-policy': "default-src 'self'; style-src 'unsafe-inline'; script-src 'unsafe-inline'; connect-src 'self'; img-src 'self' data:; frame-ancestors 'none'",
        },
      });
    }

    return json({ error: 'Not found' }, 404);
  },
};

async function recordHeartbeat(request, env) {
  const contentLength = Number(request.headers.get('content-length') || '0');
  if (contentLength > 2048)
    return json({ error: 'Payload too large' }, 413, true);

  let payload;
  try {
    payload = await request.json();
  } catch {
    return json({ error: 'Invalid JSON' }, 400, true);
  }

  const installationId = String(payload.installationId || '').toLowerCase();
  const appVersion = String(payload.appVersion || '').trim();
  const sessionId = String(payload.sessionId || '').toLowerCase();

  if (!/^[a-f0-9]{64}$/.test(installationId) ||
      !/^[0-9a-zA-Z.+_-]{1,32}$/.test(appVersion) ||
      !/^[a-f0-9-]{36}$/.test(sessionId)) {
    return json({ error: 'Invalid heartbeat' }, 400, true);
  }

  const now = Math.floor(Date.now() / 1000);
  const day = new Date(now * 1000).toISOString().slice(0, 10);

  await env.DB.batch([
    env.DB.prepare(`
      INSERT INTO installations (id_hash, first_seen, last_seen, app_version, session_id)
      VALUES (?, ?, ?, ?, ?)
      ON CONFLICT(id_hash) DO UPDATE SET
        last_seen = excluded.last_seen,
        app_version = excluded.app_version,
        session_id = excluded.session_id
      WHERE installations.last_seen <= excluded.last_seen - 60
         OR installations.app_version <> excluded.app_version
         OR installations.session_id <> excluded.session_id
    `).bind(installationId, now, now, appVersion, sessionId),
    env.DB.prepare(`
      INSERT OR IGNORE INTO daily_activity (activity_date, id_hash)
      VALUES (?, ?)
    `).bind(day, installationId),
  ]);

  return new Response(null, {
    status: 204,
    headers: {
      'access-control-allow-origin': '*',
      'cache-control': 'no-store',
    },
  });
}

async function getStats(request, env) {
  if (!env.ADMIN_TOKEN || request.headers.get('authorization') !== `Bearer ${env.ADMIN_TOKEN}`)
    return json({ error: 'Unauthorized' }, 401);

  const now = Math.floor(Date.now() / 1000);
  const [summary, versions, activity] = await env.DB.batch([
    env.DB.prepare(`
      SELECT
        COUNT(*) AS total,
        SUM(CASE WHEN last_seen >= ? THEN 1 ELSE 0 END) AS online,
        SUM(CASE WHEN last_seen >= ? THEN 1 ELSE 0 END) AS active_24h,
        SUM(CASE WHEN last_seen >= ? THEN 1 ELSE 0 END) AS active_30d
      FROM installations
    `).bind(now - 10 * 60, now - 24 * 60 * 60, now - 30 * 24 * 60 * 60),
    env.DB.prepare(`
      SELECT app_version AS version, COUNT(*) AS users
      FROM installations
      GROUP BY app_version
      ORDER BY users DESC, app_version DESC
      LIMIT 12
    `),
    env.DB.prepare(`
      SELECT activity_date AS date, COUNT(*) AS users
      FROM daily_activity
      WHERE activity_date >= date('now', '-13 day')
      GROUP BY activity_date
      ORDER BY activity_date ASC
    `),
  ]);

  const row = summary.results?.[0] || {};
  return json({
    generatedAt: new Date().toISOString(),
    total: Number(row.total || 0),
    online: Number(row.online || 0),
    active24h: Number(row.active_24h || 0),
    active30d: Number(row.active_30d || 0),
    versions: versions.results || [],
    activity: activity.results || [],
  });
}

function json(value, status = 200, cors = false) {
  const headers = { ...JSON_HEADERS };
  if (cors)
    headers['access-control-allow-origin'] = '*';
  return new Response(JSON.stringify(value), { status, headers });
}
