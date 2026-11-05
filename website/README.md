# 官网、账户与服务状态

[官网首页](https://mugonzei.top) · [账户中心](https://mugonzei.top/account.html) · [服务状态](https://mugonzei.top/status.html)

官网采用 HTML、CSS 和 JavaScript，无需构建。首页提供 Windows 下载与使用说明，账户页提供注册、登录、积分兑换、充值和流水查询。

积分服务每成功翻译一次消耗 1 积分。**5 元首充到账 300 积分，常规 200 积分。10 元首充到账 600 积分，常规 500 积分。** 两个档位共用每账户一次的首充奖励。登录后可查看资格。使用优惠码可以同时享受首充，多笔订单以最先核验到账的一笔为准。兑换码所得积分不影响首充资格，退款后已用资格不会恢复。

选择方案后从订单链接前往爱发电，选择 1 个月并核对金额。爱发电套餐名可能只显示常规积分，本站会在核验到账时加发首充赠分。付款后回到账户页核验订单，订单记录列出实付金额和实际到账的积分。

登录后输入运营者发放的兑换码即可将积分计入同一账户。该入口独立于爱发电充值；积分到账不会自动开放 AI。网络失败保留当前输入，用户主动重试不会重复入账；成功或退出时清空，不将码写入浏览器存储或 URL。管理员生成/失效操作见[后台兑换码说明](../server/README.md#积分兑换码)。

在账户页选择「创建账户」，填写用户名、密码和邮箱，点击「发送验证码」，再输入邮件里的 6 位数字完成注册。验证码在 10 分钟内有效，同一邮箱等待 60 秒可重发。新账号验证后与原有账号一样，均可用用户名和密码登录。邮件由后台配置的发信服务发送。浏览器只在提交注册的 HTTPS 请求体中传送密码和验证码，成功或离开注册页时清空验证码。

## 服务状态与下载

「最近翻译耗时」显示本次启动以来最近最多 32 笔已完成任务的分段平均值，包括排队、模型处理和结果入账。服务重启后从第一笔完成的任务重新统计。

状态页匿名读取同源 `GET /v1/status`。如果后台提供详细报告，可查看运行时间、账户数据库读检查、翻译与充值配置、队列。旧后台返回 404 时，只读取公开的 `GET /v1/plans` 验证套餐接口是否可达；未报告的项目显示「未提供」，不能据此判断付款或 AI 上游是否可用。页面不运行试译或支付，不访问个人数据。页面可见且在线时可选每 30 秒刷新，探测失败显示「当前状态未知」。

官网主按钮从本站 `/downloads/<tag>/` 下载 Windows x64 安装 EXE；本站与 GitHub Release 提供相同字节与 SHA-256。安装后从开始菜单启动，设置和密钥沿用原用户的数据目录。无法访问本站镜像时可使用 GitHub 备用下载；便携 ZIP 需要完整解压，GitHub 的 `Source code` 供自行构建。[v0.2.9-preview 发布页](https://github.com/pan0001/mugonzei-honyakuki/releases/tag/v0.2.9-preview) · [GitHub 版本记录](https://github.com/pan0001/mugonzei-honyakuki/releases)

v0.2.5-preview 及更早版本的用户需先从官网下载当前版本 并手动升级。从 v0.2.6-preview 起，用户可点击工作台左侧导航底部的「检查更新」（窄窗口位于顶部导航右侧），由客户端查询 GitHub 的公开版本，显示当前版本与最新版。「下载新版」在默认浏览器打开官方安装程序的下载链接；没有对应安装程序时显示「查看发布页」。下载完成后由用户退出并安装。官网只提供下载和版本记录链接。

## 本地检查

在仓库根目录执行：

```powershell
node --check website/main.js
node --check website/account.js
node --check website/status.js
node tests/website-account.cjs
node tests/website-status.cjs
python -m http.server 5288 --bind 127.0.0.1 --directory website
```

静态预览：<http://127.0.0.1:5288/>。Python 静态服务器不代理 API，账户和状态页需要下方 Caddy 同源配置才能取得真实数据。账户脚本测试使用内存模拟网络，覆盖验证码发送与重发、切换邮箱或登录页、注册错误与旧账户登录，以及付款地址白名单、幂等重试、到账后移除付款入口、旧请求覆盖与会话失效等情形。

产品行为发布前还须执行仓库完整桌面测试：`dotnet run --project tests/SteamVrTranslator.Tests.csproj`。

## 部署到已有 Linux 服务器

继续使用 [后台部署说明](../server/deploy/README.md) 的独立用户、回环监听和 Caddy。域名 `mugonzei.top` 与 `api.mugonzei.top` 均须有指向服务器的 A 记录，TCP 80/443 可达。Caddy 自动申请并续期证书。

1. 上传、验证并公开 GitHub Release 的 Windows EXE、便携 ZIP 和校验文件。根据网站提交的 Git blob 字节计算每个页面与静态资源的 SHA-256，并从已验证的发布包取得安装器 SHA-256。
2. 用 [发布脚本](../tools/deploy-website.py) 的 `PAGES` 清单准备 JSON：`commit`（网站提交）、`previous`（现网提交）、`tag`、`installerSha256`、`pages`（文件名 → 哈希）。在已有主机运行经哈希验证的脚本：`python3 deploy-website.py manifest.json`。它只下载清单内的公开页面与指定安装器，校验通过才原子切换 `website/current`。不同内容不得覆盖同版本下载。旧页面与下载保留，便于回滚。
3. 用正常证书校验首页、账户页、状态页、同源套餐 API、API 域名健康端点及未登录账户请求。验证官网 EXE、校验文件和 GitHub 资产摘要相同。单独更新静态官网无需改 Caddy 配置或重启账户服务；首次设置域名可参考 [Caddyfile.example](Caddyfile.example)。

账户和状态请求通过网站同源 `/v1/*` 转发到 `127.0.0.1:5187`，后台只信任已配置的 Caddy 回环代理。静态文件配置与 API 的 CSP 分开；账户 API 返回 401/404 时不会退回首页 HTML。网页登录令牌只保留当前页面内存，刷新或关闭后需再次登录；退出会请求服务端撤销，会话最长 14 天。请求使用 Bearer 认证，不依赖 Cookie，不写浏览器持久存储。不在访问日志中记录请求体或 Authorization。 模型和支付服务密钥只在后台使用，网页中的登录令牌用于访问本人账户。

官网可独立更新。发布新桌面版本时，在新 Release 上传并核验安装程序和便携 ZIP，再更新官网主按钮、便携版链接和三语 README，保留已有版本下载。
