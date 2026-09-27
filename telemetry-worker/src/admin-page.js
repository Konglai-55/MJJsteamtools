export const ADMIN_PAGE = String.raw`<!doctype html>
<html lang="zh-CN">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width,initial-scale=1">
  <title>MJJsteamtools 用户统计</title>
  <style>
    :root { color-scheme: dark; --bg:#101923; --panel:#16202d; --panel2:#1d2d3d; --line:#30475b; --text:#f4f7fa; --muted:#9fb1c1; --accent:#2d9cf0; --accent2:#225cce; --good:#72cf61; }
    * { box-sizing:border-box; }
    body { margin:0; min-height:100vh; background:linear-gradient(145deg,#101923,#18293a); color:var(--text); font-family:"Segoe UI","Microsoft YaHei",sans-serif; }
    button,input { font:inherit; }
    .shell { width:min(1120px,calc(100% - 32px)); margin:0 auto; padding:34px 0 56px; }
    header { display:flex; justify-content:space-between; align-items:flex-end; gap:20px; margin-bottom:20px; }
    h1 { margin:0; font-size:28px; letter-spacing:-.02em; }
    .subtitle,.updated { color:var(--muted); font-size:13px; margin-top:5px; }
    .panel { background:rgba(22,32,45,.94); border:1px solid var(--line); border-radius:12px; }
    .login { max-width:520px; margin:14vh auto 0; padding:24px; }
    .login h1 { font-size:24px; }
    .login p { color:var(--muted); line-height:1.65; }
    .row { display:flex; gap:10px; }
    input { min-width:0; flex:1; height:44px; border:1px solid var(--line); border-radius:8px; padding:0 12px; color:var(--text); background:#101a25; outline:none; }
    input:focus { border-color:var(--accent); box-shadow:0 0 0 3px rgba(45,156,240,.18); }
    button { min-height:44px; border:0; border-radius:8px; padding:0 18px; color:white; background:linear-gradient(100deg,var(--accent),var(--accent2)); cursor:pointer; transition:filter .18s ease,transform .18s ease; }
    button:hover { filter:brightness(1.1); } button:active { transform:translateY(1px); }
    .error { min-height:20px; color:#ff8e91; margin-top:10px; font-size:13px; }
    .dashboard[hidden],.login[hidden] { display:none; }
    .metrics { display:grid; grid-template-columns:repeat(4,1fr); gap:12px; }
    .metric { padding:17px 18px; }
    .metric-label { color:var(--muted); font-size:13px; }
    .metric-value { margin-top:7px; font-size:30px; font-weight:650; font-variant-numeric:tabular-nums; }
    .online .metric-value { color:var(--good); }
    .grid { display:grid; grid-template-columns:1.35fr 1fr; gap:12px; margin-top:12px; }
    .section { padding:20px; min-height:300px; }
    h2 { margin:0 0 18px; font-size:17px; }
    .chart { height:210px; display:flex; align-items:flex-end; gap:8px; padding-top:15px; border-bottom:1px solid var(--line); }
    .bar-wrap { height:100%; flex:1; display:flex; flex-direction:column; justify-content:flex-end; align-items:center; min-width:0; }
    .bar { width:min(30px,80%); min-height:2px; border-radius:4px 4px 0 0; background:linear-gradient(180deg,var(--accent),var(--accent2)); transition:height .25s ease; }
    .bar-value { color:var(--text); font-size:11px; margin-bottom:5px; }
    .bar-label { color:var(--muted); font-size:10px; margin-top:7px; white-space:nowrap; }
    .version { margin:0 0 14px; }
    .version-top { display:flex; justify-content:space-between; gap:12px; font-size:13px; margin-bottom:6px; }
    .track { height:7px; border-radius:99px; overflow:hidden; background:#0f1822; }
    .fill { height:100%; border-radius:inherit; background:linear-gradient(90deg,var(--accent),var(--accent2)); }
    .empty { color:var(--muted); font-size:13px; }
    @media (max-width:760px) { .metrics { grid-template-columns:repeat(2,1fr); } .grid { grid-template-columns:1fr; } header { align-items:flex-start; flex-direction:column; } }
    @media (max-width:430px) { .shell { width:min(100% - 20px,1120px); padding-top:20px; } .metrics { grid-template-columns:1fr; } .row { flex-direction:column; } }
    @media (prefers-reduced-motion:reduce) { *,*::before,*::after { transition:none!important; } }
  </style>
</head>
<body>
  <main class="shell">
    <section id="login" class="login panel">
      <h1>MJJsteamtools 统计</h1>
      <p>输入部署时设置的管理员令牌。令牌只保存在当前浏览器中，不会出现在网址里。</p>
      <form id="login-form" class="row">
        <input id="token" type="password" autocomplete="current-password" aria-label="管理员令牌" placeholder="管理员令牌">
        <button type="submit">进入面板</button>
      </form>
      <div id="login-error" class="error" role="alert"></div>
    </section>

    <section id="dashboard" class="dashboard" hidden>
      <header>
        <div><h1>用户概览</h1><div class="subtitle">匿名活跃设备统计 · 在线定义为最近 10 分钟内有心跳</div></div>
        <div><button id="refresh" type="button">刷新数据</button><div id="updated" class="updated"></div></div>
      </header>
      <div class="metrics">
        <article class="metric panel"><div class="metric-label">累计设备</div><div id="total" class="metric-value">—</div></article>
        <article class="metric panel online"><div class="metric-label">当前在线</div><div id="online" class="metric-value">—</div></article>
        <article class="metric panel"><div class="metric-label">24 小时活跃</div><div id="active24h" class="metric-value">—</div></article>
        <article class="metric panel"><div class="metric-label">30 天活跃</div><div id="active30d" class="metric-value">—</div></article>
      </div>
      <div class="grid">
        <article class="section panel"><h2>最近 14 天活跃设备</h2><div id="activity" class="chart"></div></article>
        <article class="section panel"><h2>版本分布</h2><div id="versions"></div></article>
      </div>
    </section>
  </main>
  <script>
    const login = document.getElementById('login');
    const dashboard = document.getElementById('dashboard');
    const tokenInput = document.getElementById('token');
    const loginError = document.getElementById('login-error');
    const savedToken = sessionStorage.getItem('mjjst-admin-token') || '';
    tokenInput.value = savedToken;

    document.getElementById('login-form').addEventListener('submit', function (event) {
      event.preventDefault();
      sessionStorage.setItem('mjjst-admin-token', tokenInput.value.trim());
      loadStats(true);
    });
    document.getElementById('refresh').addEventListener('click', function () { loadStats(false); });

    async function loadStats(fromLogin) {
      const token = sessionStorage.getItem('mjjst-admin-token') || '';
      if (!token) return;
      loginError.textContent = '';
      try {
        const response = await fetch('/api/stats', { headers: { authorization: 'Bearer ' + token }, cache: 'no-store' });
        if (response.status === 401) throw new Error('管理员令牌不正确');
        if (!response.ok) throw new Error('统计服务暂时不可用');
        const data = await response.json();
        render(data);
        login.hidden = true;
        dashboard.hidden = false;
      } catch (error) {
        if (fromLogin) loginError.textContent = error.message || '无法加载统计数据';
      }
    }

    function render(data) {
      ['total','online','active24h','active30d'].forEach(function (key) {
        document.getElementById(key).textContent = Number(data[key] || 0).toLocaleString('zh-CN');
      });
      document.getElementById('updated').textContent = '更新于 ' + new Date(data.generatedAt).toLocaleString('zh-CN');
      renderActivity(data.activity || []);
      renderVersions(data.versions || [], Number(data.total || 0));
    }

    function renderActivity(rows) {
      const host = document.getElementById('activity');
      host.replaceChildren();
      const map = new Map(rows.map(function (row) { return [row.date, Number(row.users || 0)]; }));
      const days = [];
      for (let offset = 13; offset >= 0; offset--) {
        const date = new Date(Date.now() - offset * 86400000);
        const key = date.toISOString().slice(0, 10);
        days.push({ key: key, value: map.get(key) || 0 });
      }
      const max = Math.max(1, ...days.map(function (item) { return item.value; }));
      days.forEach(function (item) {
        const wrap = document.createElement('div'); wrap.className = 'bar-wrap';
        const value = document.createElement('div'); value.className = 'bar-value'; value.textContent = item.value;
        const bar = document.createElement('div'); bar.className = 'bar'; bar.style.height = Math.max(2, item.value / max * 160) + 'px';
        const label = document.createElement('div'); label.className = 'bar-label'; label.textContent = item.key.slice(5).replace('-', '/');
        wrap.append(value, bar, label); host.append(wrap);
      });
    }

    function renderVersions(rows, total) {
      const host = document.getElementById('versions'); host.replaceChildren();
      if (!rows.length) { const empty = document.createElement('div'); empty.className = 'empty'; empty.textContent = '还没有统计数据'; host.append(empty); return; }
      rows.forEach(function (row) {
        const users = Number(row.users || 0); const percent = total ? users / total * 100 : 0;
        const item = document.createElement('div'); item.className = 'version';
        const top = document.createElement('div'); top.className = 'version-top';
        const name = document.createElement('span'); name.textContent = 'v' + row.version;
        const count = document.createElement('span'); count.textContent = users + ' 台 · ' + percent.toFixed(1) + '%';
        top.append(name, count);
        const track = document.createElement('div'); track.className = 'track';
        const fill = document.createElement('div'); fill.className = 'fill'; fill.style.width = percent + '%'; track.append(fill);
        item.append(top, track); host.append(item);
      });
    }

    if (savedToken) loadStats(false);
    setInterval(function () { if (!dashboard.hidden) loadStats(false); }, 30000);
  </script>
</body>
</html>`;
