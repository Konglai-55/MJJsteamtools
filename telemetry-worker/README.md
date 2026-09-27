# MJJsteamtools 匿名使用统计

这个目录是一套独立的 Cloudflare Worker + D1 统计服务。它只接收：

- 客户端生成并哈希后的随机安装标识
- MJJsteamtools 版本号
- 每次启动生成的随机会话标识
- 服务端接收心跳的时间

不会接收 Steam 账号、游戏库、文件内容、文件路径或硬件信息，也不会把请求 IP 写入 D1。

## 第一次部署

需要 Node.js 18+ 和一个 Cloudflare 账号。在本目录执行：

```powershell
npm install
npx wrangler login
npx wrangler d1 create mjjsteamtools-telemetry
```

把命令返回的 `database_id` 填入 `wrangler.toml`，然后初始化数据库：

```powershell
npm run db:init
npx wrangler secret put ADMIN_TOKEN
npm run deploy
```

`ADMIN_TOKEN` 请使用密码管理器生成的长随机字符串。不要把它写进源码或提交到 Git。

部署成功后，Wrangler 会输出 Worker 地址。把心跳地址写入项目根目录的
`telemetry-endpoint.txt`，格式如下：

```text
https://你的-worker.workers.dev/api/heartbeat
```

客户端重新构建后，这个文件会复制到输出目录；安装包也需要包含它。

## 查看统计

浏览器打开 Worker 根地址或 `/admin`，输入 `ADMIN_TOKEN`。面板展示：

- 累计匿名设备数
- 最近 10 分钟在线数
- 24 小时和 30 天活跃设备数
- 最近 14 天趋势
- 客户端版本分布

管理员令牌仅保存在浏览器当前标签会话的 `sessionStorage`，不会放进网址。

需要轮换管理员令牌时，执行 `npm run admin:rotate`。脚本会生成新的加密随机令牌、
验证心跳和统计接口、清理测试数据，并打开一次性本机页面供复制令牌；令牌不会写入项目文件。

## 本地调试

初始化本地 D1 后运行：

```powershell
npx wrangler d1 execute mjjsteamtools-telemetry --local --file=./schema.sql
npm run dev
```

将系统环境变量 `MJJST_TELEMETRY_ENDPOINT` 临时设置为本地 `/api/heartbeat`
地址即可让 WPF 客户端向本地 Worker 发送心跳。正式构建只接受 HTTPS，HTTP 仅允许回环地址。
