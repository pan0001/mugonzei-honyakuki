# 官网与账户页面

公开网站：<https://mugonzei.top>。账户页：<https://mugonzei.top/account.html>。

纯 HTML / CSS / JavaScript，无第三方前端依赖和构建步骤。首页包含产品介绍、示意交互、Windows 下载、使用说明与常见问题；账户页使用现有后台的注册、登录、积分、订单和流水接口。页面不调用 AI、不截取游戏画面，不新增收费能力。当前运营部署尚未配置爱发电方案和托管 AI，首页如实标记未开放；将来开放时应同步更新首页说明。

## 本地检查

在仓库根目录执行：

```powershell
node --check website/main.js
node --check website/account.js
node tests/website-account.cjs
python -m http.server 5288 --bind 127.0.0.1 --directory website
```

静态预览：<http://127.0.0.1:5288/>。Python 静态服务器不代理 API，账户页会显示后台暂不可达；真实账户联调使用下方 Caddy 同源配置。账户脚本测试使用内存模拟网络，覆盖付款地址白名单、幂等重试、到账后移除付款入口、旧请求覆盖与会话失效等情形，不访问真实支付平台。

产品行为发布前还须执行仓库完整桌面测试：`dotnet run --project tests/SteamVrTranslator.Tests.csproj`。

## 部署到已有 Linux 服务器

继续使用 [后台部署说明](../server/deploy/README.md) 的独立用户、回环监听和 Caddy。域名 `mugonzei.top` 与 `api.mugonzei.top` 均须有指向服务器的 A 记录，TCP 80/443 可达。Caddy 自动申请并续期证书。

1. 将下列公开文件打包上传到新的版本目录，例如 `/opt/mugonzei/website/releases/<commit>/`：
   `index.html`、`account.html`、`styles.css`、`main.js`、`account.js`、`favicon.svg`、`workspace.png`、`robots.txt`。
2. 版本目录及文件使用 `root:root`，目录 `0755`、文件 `0644`，Caddy 只需读取。**不要上传整个仓库、配置、数据库、测试、截图或本 README。** `workspace.png` 是仓库中已公开的固定示例界面。
3. 记录上传包 SHA-256 并在服务器校验。将 `/opt/mugonzei/website/current` 符号链接原子切换到新版本，保留旧版本用于回滚。上传目录应使用明确的新版本名，不覆盖现有版本。
4. 备份 `/etc/caddy/Caddyfile`，参考 [Caddyfile.example](Caddyfile.example) 合并官网段，保留原 API 配置及其他已有站点。若使用自定义域名，替换示例中的两个域名以及账户帮助中的服务地址。
5. 执行 `caddy validate --config /etc/caddy/Caddyfile --adapter caddyfile`，通过后 `systemctl reload caddy`，不会重启数据库后台。
6. 用正常证书校验访问首页、账户页和 `/v1/plans`，确认 HTTP 跳转 HTTPS、原 API 的 `/health` 仍正常、未登录 `/v1/account` 返回 401。检查桌面与窄屏布局、登录/退出和余额读取。

账户请求通过网站同源 `/v1/*` 转发到 `127.0.0.1:5187`，后台只信任已配置的 Caddy 回环代理。静态文件配置与 API 的 CSP 分开；账户 API 返回 401/404 时不会退回首页 HTML。网页登录令牌只保留当前页面内存，刷新或关闭后需再次登录；退出会请求服务端撤销，会话最长 14 天。请求使用 Bearer 认证，不依赖 Cookie，不写浏览器持久存储。不在访问日志中记录请求体或 Authorization。

官网独立于桌面发布版本：只改网页不重打 Windows 安装包。下载链接指向已验证的 `v0.2.0-preview` 公开资源；发布后续桌面版本时同步更新版本号、下载链接与文件大小。Git 按增量提交保留历史，不覆盖已有 Release。
