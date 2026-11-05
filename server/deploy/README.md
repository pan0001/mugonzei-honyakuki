# Linux / systemd 部署

这是单机、小内存服务器的初始配置：仅监听 `127.0.0.1:5187`，真实支付、模拟支付和模拟 AI 均关闭，未配置真实 AI，也不展示充值套餐。可以先验证服务运行，再单独接入 HTTPS、爱发电与模型。不要将这一步描述为已开放收费翻译。

## 目录与运行时

需要 Linux x64、systemd，以及 **.NET 10 ASP.NET Core Runtime**；基础 .NET Runtime 不包含 ASP.NET Core。按照 [Microsoft 安装说明](https://learn.microsoft.com/en-us/dotnet/core/install/linux-scripted-manual) 将 ASP.NET Core Runtime 安装到 `/opt/mugonzei/dotnet`，检查本机依赖及运行时列表。程序无需 SDK、桌面 OCR 或 SteamVR。

| 路径 | 所属与权限 | 用途 |
|---|---|---|
| `/opt/mugonzei/dotnet/` | root 所有，服务用户只读/执行 | .NET 运行时 |
| `/opt/mugonzei/releases/<版本>/` | root 所有，目录 0755、程序文件 0644 | 解压后的服务发行包，保留 `licenses/` |
| `/opt/mugonzei/current` | root 管理的符号链接 | 指向当前发行目录 |
| `/etc/mugonzei/server.env` | root:root，0600 | 真实配置，只由 systemd 加载 |
| `/var/lib/mugonzei/` | mugonzei:mugonzei，0700 | 数据库、WAL、SHM 与进程锁 |

先核对发行 ZIP 的 `.sha256`，解压后在发行目录执行 `sha256sum -c manifest.sha256`。不要将程序或运行时放在 `/root` 下：unit 的 `ProtectHome=yes` 会禁止服务访问。数据库必须使用上述绝对路径，不能放入会被替换的发行目录。

**原生 SQLite 兼容性：** 当前 v0.2.0-preview 预编译发行包中的 `libe_sqlite3.so` 需要 glibc 2.34 或更高；这与 .NET 运行时能否启动是两项检查。上线前执行 `ldd --version` 和 `ldd /opt/mugonzei/current/libe_sqlite3.so`，确认没有缺失库或 `GLIBC_2.34 not found` 等符号版本错误。较旧系统应使用经维护者验证的同版本兼容构建，或采用满足依赖的新版容器用户空间；不要为此直接替换系统 glibc。仅复制系统的任意 SQLite 库不能保证版本与入口兼容。

### 旧版 glibc 的 SQLite 构建

仓库提供 [build-sqlite-linux.py](build-sqlite-linux.py)，在目标 Linux 上用 Python 3.6+ 和 GCC 构建应用私有的 SQLite **3.53.3**。脚本校验官方源码 ZIP 的固定 SHA-256，以及 [SQLite 官方发布页](https://www.sqlite.org/releaselog/3_53_3.html) 公布的 `sqlite3.c` SHA3-256；保留源码、来源声明、编译参数与输出哈希。单进程 `-O0` 适合小内存主机，代价是没有启用编译优化。它不修改系统库、数据库或运行中的服务。

```sh
install -d -o root -g root -m 0755 /opt/mugonzei/native-build
python3 build-sqlite-linux.py --output /opt/mugonzei/native-build/sqlite-3.53.3-verified
```

输出目录必须尚不存在；失败时保留记录，重试使用新目录。成功后检查 `build-manifest.json` 的 `status=validated`，以及 `ldd` 无缺失依赖。脚本验证实际加载的库、版本、导出接口、线程安全、内存事务和默认外键约束。只有完成这些检查后，才在停止服务的情况下部署：

```sh
systemctl stop mugonzei.service
# 仅首次替换时执行；不要覆盖已有原库备份。
if test ! -e /opt/mugonzei/current/libe_sqlite3.so.original; then
    cp -p /opt/mugonzei/current/libe_sqlite3.so /opt/mugonzei/current/libe_sqlite3.so.original || exit 1
fi
install -o root -g root -m 0755 /opt/mugonzei/native-build/sqlite-3.53.3-verified/libe_sqlite3.so /opt/mugonzei/current/libe_sqlite3.so
install -o root -g root -m 0644 /opt/mugonzei/native-build/sqlite-3.53.3-verified/build-manifest.json /opt/mugonzei/current/sqlite-compat-build.json
systemctl start mugonzei.service
```

原始发行清单仍描述原版 ZIP；替换后的 `.so` 应与独立的构建清单比对，不要改写原清单掩盖差异。服务器升级会带入新发行包的库，需重新检查兼容性。已在 Alibaba Cloud Linux 3（glibc 2.32、GCC 10）配合 ASP.NET Core Runtime 10.0.14 验证后台启动、注册/登录与重启后的会话恢复，并以同一兼容库通过服务端 229 项断言（临时数据库和模拟接口，无真实支付或模型调用）；此验证不代表所有 Linux 发行版均已测试。

## 首次安装

以下管理命令在服务器上以 root 执行；从包含本说明与两个模板的 `deploy` 目录开始。若服务用户已存在，跳过 `useradd`；已有配置不要用示例覆盖。先把已校验的发行目录放到 `/opt/mugonzei/releases/<版本>/`，并让 `/opt/mugonzei/current` 指向它。

```sh
useradd --system --user-group --no-create-home --home-dir /nonexistent --shell /sbin/nologin mugonzei
install -d -o root -g root -m 0755 /opt/mugonzei /opt/mugonzei/releases
install -d -o root -g root -m 0700 /etc/mugonzei
install -d -o mugonzei -g mugonzei -m 0700 /var/lib/mugonzei
install -o root -g root -m 0600 server.env.example /etc/mugonzei/server.env
install -o root -g root -m 0644 mugonzei.service /etc/systemd/system/mugonzei.service
/opt/mugonzei/dotnet/dotnet --list-runtimes
systemd-analyze verify /etc/systemd/system/mugonzei.service
systemctl daemon-reload
systemctl enable --now mugonzei.service
```

运行时列表须同时包含 `Microsoft.NETCore.App 10.0.x` 和 `Microsoft.AspNetCore.App 10.0.x`。按目标主机的 systemd 版本检查模板，不要忽略未知指令或启动错误。服务使用非 root 用户，只有数据目录和隔离的临时目录可写；数据库目录需要容纳 `.lock`、`-wal`、`-shm`，只授予 `.db` 文件写权限不够。不要添加 `MemoryDenyWriteExecute=yes`（与 JIT 冲突），或 `PrivateNetwork=yes`（会隔离 loopback）。[systemd 文档](https://www.freedesktop.org/software/systemd/man/latest/systemd.exec.html)

## 验证与本机连接

```sh
systemctl is-active mugonzei.service
systemctl show mugonzei.service -p User -p Group -p MainPID
ss -ltnp 'sport = :5187'
curl --fail --silent --show-error http://127.0.0.1:5187/health
curl --fail --silent --show-error http://127.0.0.1:5187/v1/plans
curl --silent --output /dev/null --write-out '%{http_code}\n' http://127.0.0.1:5187/v1/account
journalctl -u mugonzei.service -n 50 --no-pager
```

预期：服务 active、用户 mugonzei、只监听 `127.0.0.1:5187`；健康接口返回 `{"status":"ok"}`，套餐返回 `[]`，未登录账户接口返回 `401`。健康接口只证明进程能响应，不证明支付或 AI 可用。检查无原生库加载或数据库权限错误，并重启服务再次验证。不要对公网开放 5187，包括云安全组和主机防火墙。

暂无域名时，在自己的电脑运行以下命令，替换 SSH 用户和主机名；保持终端运行：

```sh
ssh -N -o ExitOnForwardFailure=yes -L 127.0.0.1:15187:127.0.0.1:5187 SSH_USER@YOUR_SERVER
```

桌面客户端的积分服务地址填写 `http://127.0.0.1:15187`。这只为运行隧道的电脑提供连接，不会启用支付或模型。账号会话按服务地址隔离，之后切换正式 HTTPS 地址需重新登录。公网服务应先配置域名、有效 HTTPS 证书与反向代理，再依照[服务端说明](../README.md)设置可信代理、爱发电计划与 AI。

## 公网 HTTPS（Caddy）

使用 [Caddyfile.example](Caddyfile.example) 与 [caddy.service](caddy.service) 为 API 子域名单独提供 HTTPS，不必占用根域名。模板的 `api.example.com` 是占位符，所有步骤都须替换成自己的域名。启用 HTTPS 不会开启爱发电或 AI；保留既有 `server.env` 中所有支付/模拟开关为 false、AI 地址和密钥为空、套餐为 `[]`。

1. 创建子域名的 A 记录，指向服务器公网 IPv4；等待权威 DNS 和公共解析器均能查到。仅在服务器 IPv6 确实可达时添加 AAAA。不要删除其他用途的根域名记录。
2. 放行云安全组及主机防火墙的 **TCP 80、443**，保持 **5187、2019 不对公网开放**。确保服务器能访问 DNS 和 ACME 服务。先让域名与端口就绪，再启动签发，避免反复失败触发 CA 限额。
3. 从 [Caddy 官方安装入口](https://caddyserver.com/docs/install#static-binaries) 下载 Linux amd64 静态发行包，核对官方校验文件和来源；例如 2.11.8 的校验文件使用 SHA-512，应使用对应算法。保留发行包中的许可证。将二进制安装为 `/opt/mugonzei/caddy/caddy`（root:root、0755）；运行 `file`、`ldd` 和 `caddy version` 检查架构及链接方式，静态文件的 `ldd` 显示非动态可执行文件属于预期结果。无需在服务器配置 DNS 提供商的 API 密钥。

首次安装时，以 root 从本 `deploy` 目录执行以下命令；已有用户或配置不要重复创建/覆盖。先安装二进制，复制配置后编辑占位域名，**验证成功后再启用服务**：

```sh
useradd --system --user-group --no-create-home --home-dir /var/lib/caddy --shell /sbin/nologin caddy
install -d -o root -g caddy -m 0750 /etc/caddy
install -d -o caddy -g caddy -m 0700 /var/lib/caddy
install -o root -g caddy -m 0640 Caddyfile.example /etc/caddy/Caddyfile
install -o root -g root -m 0644 caddy.service /etc/systemd/system/caddy.service
```

编辑 `/etc/caddy/Caddyfile` 的域名，并在应用的 `/etc/mugonzei/server.env` 中将 `MUGONZEI_TRUSTED_PROXIES` 改成 `127.0.0.1`，其余配置保留。Caddy 默认忽略客户端伪造的 `X-Forwarded-*`，自行写入真实连接的地址和协议；应用已有 `ForwardLimit=1`，与同机 IPv4 代理的一跳链路相符，不需要手写 `header_up`。当前没有前置 CDN，不要给 Caddy 添加宽泛的 `trusted_proxies`。[转发头说明](https://caddyserver.com/docs/caddyfile/directives/reverse_proxy#defaults)

```sh
chmod 0600 /etc/mugonzei/server.env
systemd-analyze verify /etc/systemd/system/caddy.service
runuser -u caddy -- /opt/mugonzei/caddy/caddy validate --config /etc/caddy/Caddyfile --adapter caddyfile
systemctl daemon-reload
systemctl restart mugonzei.service
systemctl enable --now caddy.service
```

Caddy 以独立用户运行，只授予绑定低端口所需的 `CAP_NET_BIND_SERVICE`；应用数据库和配置仍归原服务用户/root，不向 caddy 授权。证书、私钥和自动保存配置持久存放在 `/var/lib/caddy`，保留 0700 权限，不加入仓库或发行包。默认管理 API 只监听 loopback 的 2019，保留该设置以支持 reload。若 SELinux 开启，检查二进制和 unit 的标签与审计记录。[官方 systemd/SELinux 说明](https://caddyserver.com/docs/running)

域名配置会自动启用公信证书、续期及 HTTP→HTTPS；不使用 `tls internal` 或 `http://` 站点前缀。默认 HTTP/TLS-ALPN 挑战即可，不需要 DNS 插件。修改配置后先以 caddy 用户重新 validate，再 `systemctl reload caddy.service`。[自动 HTTPS 条件](https://caddyserver.com/docs/automatic-https)

从服务器之外验证，替换下列示例域名，不加 `-k` 跳过证书检查：

```sh
curl -sS -D - -o /dev/null http://api.example.com/health
curl -fsS https://api.example.com/health
curl -fsS https://api.example.com/v1/plans
curl -sS -o /dev/null -w '%{http_code}\n' https://api.example.com/v1/account
```

预期依次为 HTTPS 重定向、`{"status":"ok"}`、`[]`、`401`。服务器上检查 `systemctl is-active caddy.service mugonzei.service`、`journalctl -u caddy.service -n 50 --no-pager` 和 `ss -ltnp`，确认没有证书/代理错误且后台仍只监听 `127.0.0.1:5187`。客户端填写 `https://api.example.com` 并重新登录；健康检查成功不表示真实充值或模型已配置。

此模板已在 Alibaba Cloud Linux 3、Caddy 2.11.8 上验证：公信证书签发、HTTP 重定向、外部客户端的证书校验与注册/账号鉴权/退出均通过；测试未启用真实支付或 AI，测试账号已清理。

**回退：** 先停用代理 `systemctl disable --now caddy.service`，恢复应用 `MUGONZEI_TRUSTED_PROXIES=` 并重启 mugonzei，然后用前述 SSH 隧道连接。按原配置恢复 DNS/安全组规则，保留数据库和 `/var/lib/caddy`，不要删除私钥或账户数据。只回退 Caddyfile 时，恢复先前文件、validate 后 reload 即可；配置验证失败不要覆盖仍正常运行的服务。

## 注册邮件与账户存储

网站和桌面端向同一 API 提交注册。此模板把 SQLite 库放在 `/var/lib/mugonzei/mugonzei.db`，由 `mugonzei` 系统用户访问，不在网站静态目录中。数据库及备份包含账户、积分和新账户的邮箱，请限制读取权限。登录密码保存为加盐哈希。Windows 客户端只在 `%LOCALAPPDATA%\SteamVrTranslator\CreditSessions` 保存按服务地址区分、由当前 Windows 用户保护的会话，不保存注册密码或邮件验证码。

1. 准备一个可发送事务邮件的发件地址，按邮件服务商要求验证发信域名。注册邮件只用于验证新账户，使用发信专用 SMTP 凭据。使用阿里云 DirectMail 时，先在[发件地址设置](https://www.alibabacloud.com/help/en/direct-mail/user-guide/setup-sender-addresses)中设置 SMTP 密码，从[地域端点表](https://www.alibabacloud.com/help/en/direct-mail/smtp-endpoints)选择与发件地址一致的端点。该服务支持在 80 端口协商 STARTTLS；本项目不会回退到明文。其他服务请使用其明确支持 STARTTLS 的提交端口，通常是 587；465 的隐式 TLS 不是 STARTTLS。
2. 在服务器的 `/etc/mugonzei/server.env`（root 所有，权限 0600）填写 `MUGONZEI_SMTP_HOST`、`MUGONZEI_SMTP_PORT`、`MUGONZEI_SMTP_FROM`、`MUGONZEI_SMTP_USERNAME`、`MUGONZEI_SMTP_PASSWORD`。为 `MUGONZEI_EMAIL_CODE_KEY` 单独生成 32 个随机字节的 Base64 字符串，例如在私有终端使用 `openssl rand -base64 32`。不要把密钥放进客户端、Git、命令参数或聊天。更换验证码密钥会使已发出的验证码失效，不影响登录密码。
3. 先在隔离实例跑完整注册：用运营者自己的收件箱收信，用正确码新建账户，确认错误码、重复使用同一码、仅含用户名密码的直接 API 请求均不能建户。再使用升级前的账户登录，核对余额。联调通过后同步切换后台、网站和客户端。旧版客户端仍可登录已有账户，需要注册时前往更新后的官网。缺少发信配置时，新注册不可用，登录和已有余额不受影响。
4. 发布前备份数据库，保留旧程序。回滚到未实现邮箱验证的旧服务器前，先在反向代理临时关闭 `POST /v1/auth/register`；否则回滚会重新开放仅凭密码的注册。不要用旧数据库覆盖切换后新增的真实账户和流水。

## 开放付费充值前的验收

积分翻译与收款独立运行。已有余额可以翻译，不需要为了启用 AI 同时开启收款。从 v0.2.2-preview 起，可以预先写入两档套餐；真实和模拟支付都关闭时，`/v1/plans` 仍返回空数组，创建充值订单也会被拒绝。

| 检查项 | 通过条件 |
| --- | --- |
| 客户端首次使用 | 新配置选中账号积分服务及官方 HTTPS 地址；在向导打开账户，注册或登录、兑换，保存后可截图。重开/取消向导不会清空原配置。 |
| 托管翻译 | 用合成图片验证有字时成功一次扣 1 分、相同任务重试不重复扣、无字或失败不扣；检查取消后预留释放，下一张可入队。健康接口本身不能证明模型可用。 |
| 爱发电真实订单 | 使用隔离验证实例和软件账号发起一笔 5 元、一个月的 200 积分订单。付款前核对方案和金额；从签名查单结果确认订单关联标识与该账号创建的订单完全相同，回调和手动刷新均只能产生一条入账流水。10 元 / 500 积分方案也要核对价格和方案 ID。 |
| 通知和异常 | 验证爱发电后台通知地址确实指向本次验收实例。直接从赞助主页付款、修改月份导致金额不符、无关联标识等情形不得自动加分；为漏单、退款和无法登录的用户准备人工处理入口。 |
| 运维 | 发布前备份数据库，在隔离目录验证备份可读；保留上个程序版本和私有配置，核对 HTTPS、回环监听、服务重启和上游请求失败后的积分释放。按服务商实际账单核对一次完整识图加翻译的成本。 |

目前公开服务的托管翻译已验证，爱发电签名和查单权限已验证，**真实付款到账尚未验证**。验证中使用的一次性账号和订单应与真实用户分开；不得用模拟支付或手工改余额替代实付验收，也不要为了测试直接向所有用户开放套餐。

## 兑换码管理

生成、查询及撤销只通过服务器本地 CLI，不向网站用户开放管理员接口。使用当前发行版本与数据库，先确认没有进行中的翻译，再进入维护；后台未配置 AI 时兑换仍可入账，但无法完成托管翻译。

以 root 在维护 shell 中执行以下示例。导出目录位于数据库的受保护目录内，Caddy 无权限读取；每批使用新的文件名。`trap` 确保命令失败后也恢复服务。生成结果不会把明文码打印到终端。

```sh
(
  set -eu
  install -d -o mugonzei -g mugonzei -m 0700 /var/lib/mugonzei/admin
  trap 'systemctl start mugonzei.service' EXIT
  systemctl stop mugonzei.service
  runuser -u mugonzei -- env MUGONZEI_DATABASE=/var/lib/mugonzei/mugonzei.db \
    /opt/mugonzei/dotnet/dotnet /opt/mugonzei/current/Mugonzei.Server.dll \
    codes create --credits 100 --count 10 \
    --output /var/lib/mugonzei/admin/redemption-codes-batch-001.json --note launch
)
```

默认 30 天有效；指定到期时间使用 `--expires 2027-01-01T00:00:00Z`，永久码须显式 `--no-expiry`。一次1–1000个，每码1–1000000积分。将上例 `codes create ...` 换为 `codes list --limit 100` 可查看无明文元数据，或 `codes revoke --id UUID` 撤销未领取码；这两种命令也要维护停服。不要复制包含明文码的导出到官网、Git、发行目录或工单日志。已兑码不能用撤销功能扣回积分。

更新会自动新增兑换码表。先做一致性备份；回滚旧程序会关闭兑换入口，但已到账积分与流水仍在。网站和新版桌面客户端共用账户，不需要额外注册。用户提交过期/不可用码不会扣积分；同账户重试成功码不重复到账。

## 备份、更新与密钥

数据库只支持一个服务进程。首次安装之外，不要覆盖 `server.env`；编辑后保持 `chmod 600 /etc/mugonzei/server.env`，重启服务才会加载新值。此文件使用 systemd `EnvironmentFile` 语法，不写 `export`；不要将真实密钥提交 Git、放进发行 ZIP、命令参数或终端截图。

初期建议停服务做一致性备份，备份也含账户和近期原文/译文，须保护访问权限。下列命令备份整个数据目录，覆盖 WAL 场景；运行中不要只复制 `.db`。备份失败时先解决问题，不要继续升级。

```sh
systemctl stop mugonzei.service
install -d -o root -g root -m 0700 /var/backups/mugonzei
backup_file="/var/backups/mugonzei/data-$(date -u +%Y%m%dT%H%M%SZ).tar.gz"
umask 077
tar -czf "$backup_file" -C /var/lib/mugonzei . && systemctl start mugonzei.service
```

更新时先将新版本解压至新的发行目录并校验 SHA-256，保留旧版本、外置配置和数据库备份。停服务、备份数据，再以 root 把 `/opt/mugonzei/current` 指向新发行目录后启动并重复验证。只有 unit 修改时才需要 `systemctl daemon-reload`。若要回滚，先停服务并核对旧版本与当前数据库结构兼容；必要时恢复更新前的数据备份，恢复后确认目录属于 mugonzei，不能同时启动新旧实例。退款管理命令同样要求先停服务并指定同一个绝对数据库路径。
