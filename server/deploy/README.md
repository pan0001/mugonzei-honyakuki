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
