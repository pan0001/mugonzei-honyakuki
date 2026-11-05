# Mugonzei Server

独立的 .NET 10 / ASP.NET Core 服务端，代码为本项目实现，没有复制 VRCLS、VoiceLinkServer 或其他 AGPL 项目。它提供软件账户、充值订单、积分账本及官方服务翻译任务。桌面客户端自带 API Key、Codex、Google 和本地服务模式不经过本账本。

**真实收款默认关闭。本地模拟通过不表示爱发电生产链路已经验证。** 上线前须由运营者完成平台创作者/商品准入、真实计划映射、HTTPS、备份和小额真实订单核验；本仓库不提供任何真实账户、密钥或预设收款人。

## 构建与运行

需要 .NET 10 SDK；不依赖 Windows、SteamVR、OCR 本机模型或桌面项目。

```sh
dotnet build server/Mugonzei.Server.csproj -c Release
dotnet run --project server/Mugonzei.Server.csproj -c Release
dotnet run --project server.tests/Mugonzei.Server.Tests.csproj -c Release
```

Linux x64 发行包为 framework-dependent，不包含 .NET 运行时。目标主机须先安装 **.NET 10 ASP.NET Core Runtime**（包含所需的 .NET Runtime），解压后使用 `dotnet Mugonzei.Server.dll` 启动；仅安装基础 .NET Runtime 不足以运行此服务。

使用 systemd 的单机部署见 [Linux 部署模板与步骤](deploy/README.md)，包含仅本机监听的配置、服务用户权限、SSH 隧道、备份和更新方式；模板默认关闭真实支付、模拟支付及 AI。

维护者可在仓库根目录用 PowerShell 7 打包既有 publish 目录；脚本不构建或启动服务：

```powershell
dotnet publish server/Mugonzei.Server.csproj -c Release -r linux-x64 --self-contained false -p:UseAppHost=false -o dist/Mugonzei-Server-linux-x64-v0.2.1-preview
./tools/package-server-release.ps1
```

脚本从 `server/Mugonzei.Server.csproj` 读取版本，默认读取 `dist/Mugonzei-Server-linux-x64-v<版本>/`，输出同名 `.zip` 和 `.zip.sha256`。可用 `-PublishDirectory` 指定仓库内的其他 publish 目录。打包前检查 Linux x64 / .NET 10 配置、服务依赖、本文档、配置示例与第三方许可；包内 `manifest.sha256` 记录每个文件的 SHA-256，并回读 ZIP 验证。数据库、数据目录、环境文件、密钥、调试符号和 SQLite 静态库不会打包。真实配置与持久化数据应保存在发行目录之外。

可选 Linux 容器：`docker build -t mugonzei-server ./server`，运行时将容器端口 5187 仅映射到主机 loopback，并持久挂载 `/data`；镜像以 `app` 非 root 用户运行，挂载目录须可写。Dockerfile 未替运营者启用收款、配置密钥或建立公网 HTTPS。

默认仅监听 `http://127.0.0.1:5187`，数据库为工作目录下 `data/mugonzei.db`。生产使用 `ASPNETCORE_URLS` 和 `MUGONZEI_DATABASE` 指定监听及持久化目录，在可信反向代理后提供 HTTPS。默认不读取 `X-Forwarded-For/Proto`。若代理与服务同机，设置 `MUGONZEI_TRUSTED_PROXIES=127.0.0.1,::1`，仅这些明确 IP 的一跳转发头被接受；不要把所有互联网来源设为可信代理。未配置时同一代理后的用户共享 IP 限流额度。

同机 Caddy 可用 `your-domain.example { reverse_proxy 127.0.0.1:5187 }`。同机 Nginx 的 HTTPS server 中使用以下 location；代理应覆盖外部传入的转发头，服务端口不要对公网开放。

```nginx
location / {
    proxy_pass http://127.0.0.1:5187;
    proxy_set_header Host $host;
    proxy_set_header X-Forwarded-For $remote_addr;
    proxy_set_header X-Forwarded-Proto $scheme;
    client_max_body_size 17m;
}
```

**只支持一个服务进程使用一个数据库。** 启动持有 `<database>.lock` 的独占文件句柄；第二实例或退款命令会拒绝进入。不能通过不同路径、软链接或共享文件系统绕过此限制。进程退出释放锁，锁文件可以保留。任务队列仅存在该进程内，启动恢复会将中断任务标为失败并释放其积分预留。

配置见 [configuration.example.txt](configuration.example.txt)。环境变量示例中的值均为占位符。`Microsoft.Data.Sqlite` 固定 `10.0.0`；原生 SQLite 包固定 `SQLitePCLRaw.lib.e_sqlite3 3.53.3`，覆盖上游旧依赖中的 CVE-2025-6965，不屏蔽审计告警。

## 本机模拟

```sh
export ASPNETCORE_ENVIRONMENT=Development
export ASPNETCORE_URLS=http://127.0.0.1:5187
export MUGONZEI_DATABASE=./data/development.db
export MUGONZEI_MOCK_PAYMENTS=true
export MUGONZEI_LIVE_PAYMENTS=false
export MUGONZEI_PLANS_JSON='[{"id":"demo","name":"本机模拟积分","credits":10,"amountFen":500,"afdianPlanId":"demo-plan"}]'
export Translation__Mock=true
dotnet run --project server/Mugonzei.Server.csproj
```

模拟支付只允许 `Development`、显式开启、仅 loopback 监听且真实收款关闭；每次模拟页面访问还检查连接来源与 Host。通过 `/v1/orders` 创建订单，打开返回的 `payUrl`，**GET 只展示确认页**，提交页面按钮的 POST 才模拟到账。模拟到账使用相同核验/订单/账本事务，不调用爱发电。模拟翻译返回明确的测试结果，不调用模型。

## HTTP 接口

除注册、登录、计划列表、健康检查和平台回调外，接口使用 `Authorization: Bearer <token>`。密码使用随机 salt 的 PBKDF2-SHA256，校验采用固定时间比较；数据库仅保存会话 token 的 SHA-256 摘要，14 天过期，退出立即撤销当前会话。尚未提供找回密码、邮箱认证或账户删除界面。

| 方法 | 路径 | 用途 |
|---|---|---|
| POST | `/v1/auth/register`、`/v1/auth/login` | `{username,password}` → `{token,username,expiresAt}` |
| POST | `/v1/auth/logout` | 撤销当前会话 |
| GET | `/v1/account` | `{username,balance,reserved}`，可用积分 = balance − reserved |
| GET | `/v1/ledger` | 最近 200 条流水 |
| POST | `/v1/redemptions` | `{code}` → `{credits,balance,reserved,alreadyRedeemed,reference}`，兑换一次性积分码 |
| GET | `/v1/plans` | 服务端计划列表，金额单位为人民币分 |
| POST | `/v1/orders` | `{planId,requestId}`，requestId 为 UUID；相同请求只创建一次 |
| GET | `/v1/orders` | 最近 100 条账户订单 |
| GET | `/v1/orders/{id}?refresh=true` | 主动核验并返回订单状态 |
| POST | `/v1/payments/afdian/webhook` | 只读取通知中的爱发电订单号，再由服务器查单 |
| PUT | `/v1/translations/{requestId}` | `{mode,text?,imageBase64?,targetLanguage}` 创建/取回幂等任务 |
| GET | `/v1/translations/{requestId}` | 查询进度与已结算结果 |
| POST | `/v1/translations/{requestId}/cancel` | 取消尚未结算任务 |

认证错误返回 `{error,message}`，不返回密钥、上游响应正文或原文。注册/登录限每来源 IP 每分钟 10 次，订单操作和翻译提交各限每账户每分钟 20 次，Webhook 限每来源 IP 每分钟 60 次；超过返回 429。翻译上传全局最多 4 个并发，其余立即拒绝，可使用相同请求编号重试；结果轮询不消耗提交配额，取消使用独立的每账户 60 次/分钟配额。认证与支付请求体上限 32 KiB，图片任务另有有界 PNG/base64 限制。

## 积分兑换码

登录后可在网站账户中心或桌面「账号与积分」粘贴运营者发放的码。一个码只能归属一个账户；同一账户再次提交会返回 `alreadyRedeemed=true` 和当前余额，不重复加积分。响应丢失时应使用同一账户重试同一个码。兑换没有手续费，也不调用支付或 AI；积分到账不代表托管翻译已配置。

码为 `MU-` 加八组四位十六进制字符，使用 128 位密码学随机数。服务器接受大小写、空白和连字符差异，仅保存标准化码的 SHA-256。客户端不能指定积分、有效期或归属。无效、过期、撤销或被其他账户使用统一返回 `400 redemption_unavailable`；余额溢出返回 `409 credit_limit`，保留未使用的码。每账户每分钟最多 10 次兑换请求，超过返回 429，独立于翻译/订单配额。

认领码、增加余额和 `redemption` 流水在同一 SQLite 即时事务内完成；并发竞争仅一个账户成功，数据库失败会整体回滚。新增表自动创建，不改变现有账户、订单、余额或配置。已有兑换记录不会因到期变成可再次领取。

管理员在服务器本地使用 `codes create/list/revoke`，没有公开的生成或管理 API。与退款管理相同，**必须先停止占用同一数据库的服务，完成后启动**。必须指定已有数据库；命令不会创建空账户库。Linux 权限及命令见[兑换码管理](deploy/README.md#兑换码管理)。

```sh
# 在受保护的维护环境执行，MUGONZEI_DATABASE 指向已有数据库。
dotnet Mugonzei.Server.dll codes create --credits 100 --count 10 --output /private/redemption-codes-batch.json --note launch
dotnet Mugonzei.Server.dll codes list --limit 100
dotnet Mugonzei.Server.dll codes revoke --id <生成记录的UUID>
```

每批 1–1000 个，每码 1–1000000 积分。默认 30 天后过期；可传 `--expires 2027-01-01T00:00:00Z` 指定未来十年内时间，或 `--no-expiry` 明确永久有效。备注最多 120 字。导出要求新的绝对 JSON 路径，不覆盖现有文件；Unix 以 0600 创建。明文只出现在该导出文件，标准输出和 `list` 只有元数据。导出失败会尝试撤销该批生成的码，请检查列表后换新文件重试。不可恢复遗失的明文码，可撤销未兑换记录再生成。

导出包含可直接领取积分的凭据，保存在网站目录、发行包和 Git 之外；不要贴进日志或公开仓库。撤销操作对已撤销的码幂等；已入账的码不能撤销或据此扣回用户积分。`list` 状态为 `active/expired/revoked/redeemed`，只列最近记录和内部 ID，不显示可兑换码或摘要。

## 爱发电与到账

真实收款需设置 `MUGONZEI_LIVE_PAYMENTS=true`、服务器专用 `AFDIAN_USER_ID` / `AFDIAN_TOKEN`、批准使用的 `AFDIAN_PAYMENT_BASE_URL` 与各套餐的 `afdianPlanId`。付款地址只接受 `afdian.com`、`ifdian.net`、`afdian.net` 的 HTTPS `/order/create`，禁止用户信息、额外查询串或非默认端口。

下单时固化金额、积分、方案及随机 `custom_order_id`，后者只关联发起订单的软件账户，不把凭据放入支付 URL。Webhook 不相信回调里的金额、账号或成功字段；通过服务器签名请求固定 `https://afdian.com/api/open/query-order`，以核实返回的 `status=2`、真实付款金额 `total_amount`、`plan_id` 和 `custom_order_id`。支付订单号唯一约束及同一 SQLite 事务保证重复通知、并发刷新不会重复充值。已付、退款冻结、已退款订单均不会再次入账。

当前刷新每次最多核对最近 3 页，补偿近期漏通知；更老的漏单需要平台重新通知或管理员核对，不应反复创建新订单补发积分。没有自动退款 API，没有从客户端接受金额或余额，也不依赖支付页回跳判定到账。当前版本未实现爱发电 OAuth 身份绑定；充值通过一次性订单标识定向给创建订单的账户。

参考接口：[官方开发者 API](https://guide.afdian.com/creator/developer)、[官方 OAuth2 说明](https://guide.afdian.com/creator/oauth2)。这些能力不代表具体商品/服务已经获得平台准入。

## 翻译计费与隐私

管理员通过 `Translation__BaseUrl`、`Translation__ApiKey`、`Translation__Model` 配置 OpenAI 兼容的 Chat Completions 上游。密钥只在服务器；客户端不能指定上游、模型或覆盖提示词。`Translation__TimeoutSeconds`、`Translation__Concurrency`、`Translation__QueueCapacity` 控制预算和队列。未配置时服务可启动，但不接受付费翻译任务。

每账户和 requestId 绑定内容摘要。相同 ID/相同内容返回原任务，不重新扣分；同 ID/不同内容返回 409。开始执行前原子预留 1 分，成功后扣 1 分；无可读文字、失败或成功之前的取消释放预留。识图/手写采用转写→翻译两个上游阶段，但只按一个成功任务扣费。已完成后取消、隐藏或重复读取结果不退费、不再收费。模型处理成本可能已产生，即使用户后来取消或模型失败，本版仍按上述规则释放积分。

取消与成功由数据库事务先提交者决定；明确取消若先于上传入库到达，也会保留该账户/请求编号的取消标记，迟到提交不会执行或扣分。网络断开不等于取消，客户端应以相同 requestId 查询结果。重启将排队/执行中的任务失败释放，用户可使用新 ID 再请求。图片只在有界内存队列中，不写数据库；文本/译文保存供失败重连取回，终态满 7 天后由小时维护任务删除内容，保留去重摘要、状态及账务流水。过期结果不会重新执行同一任务。

## 人工退款管理

本命令需要服务器数据库的本地访问权限，**先停服务**，使用同一 `MUGONZEI_DATABASE`。它不调用平台退款接口。每笔管理操作用新的 UUID，重试则复用同一个 UUID。

```sh
# 1. 先冻结这笔订单对应的全部可用积分。余额不足会拒绝，不变更账务。
dotnet server/bin/Release/net10.0/Mugonzei.Server.dll refund hold --order ORDER_UUID --request REFUND_UUID
# 2a. 运营者在爱发电实际完成资金退款之后，确认回收已冻结的积分。
dotnet server/bin/Release/net10.0/Mugonzei.Server.dll refund confirm --request REFUND_UUID --platform-refund-confirmed
# 2b. 若平台资金退款失败或决定取消，则释放冻结积分。
dotnet server/bin/Release/net10.0/Mugonzei.Server.dll refund release --request REFUND_UUID
```

冻结、确认和释放均幂等并留流水；确认后不能用 release 撤销。系统无法判断原订单的积分是否已被消费，规则仅要求当前可用积分足够回收整个套餐。余额不足时需人工协调部分退款/补偿，不能先退资金再假定积分一定能扣回。中断翻译恢复只释放翻译任务的预留，不会释放退款冻结。

## 验证范围

仓库测试以临时 SQLite、假爱发电查单和假翻译上游验证账户、幂等到账、金额拒绝、任务取消、并发结算及恢复，不进行真实收款或付费模型请求。生产启用前仍需要运营者独立完成真实小额订单与退款流程验证。
