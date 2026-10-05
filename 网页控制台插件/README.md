# 网页控制台插件

在**服务端进程内**嵌一个 HTTP(S) 服务，浏览器直接开面板：看服务器状态、管玩家、看背包、敲命令、实时刷日志。

不依赖任何外部程序（不装 Nginx、不起第二个进程、不改系统证书库）——**关服即关站**。页面和接口全部由插件自己吐字符串，不落任何前端文件到磁盘，也就没有"忘了部署 dist"这类问题。

---

## 目录

- [功能](#功能)
- [HTTPS / 纯插件 TLS](#https--纯插件-tls)
- [安全设计](#安全设计)
- [部署](#部署)
- [配置](#配置)
- [⚠️ 安全边界](#️-安全边界最重要的一节)
- [与基础插件的关系](#与基础插件的关系)
- [已知限制](#已知限制)
- [线程模型（改代码前必读）](#线程模型改代码前必读)
- [常见问题](#常见问题)

---

## 功能

| 页签 | 内容 |
|---|---|
| 概览 | 在线人数 / 上限、TPS、MSPT、游戏内天数与时刻、存档大小、运行时长、内存 |
| 性能 | 自己采样的 TPS / MSPT 折线（核心没有现成的 TPS，只能自量） |
| 玩家 | 列表（名字、坐标、血量、等级、模式、管理员标记、延迟、**连接地址**）+ 踢人 / 回血 / 修复生存状态 / 击杀 / 切模式 / 清背包 / 无敌 / 送回重生点 / **封禁账号** / **封禁 IP**（手填 IP，封前自动查这个地址关联几个账号） |
| 背包 | 指定玩家的**生存背包（快捷栏+主背包、装备栏）在前**，创造背包折叠在后（默认收起）。模式与标题联动，服务端拿不到贴图，只能给"名字 + 数量 + 槽位" |
| 终端 | 敲服务端命令，带**命令白名单**闸门 |
| 日志 | 实时日志流（SSE 长连接，连不上自动退回 2 秒轮询） |
| 存档 | **只读**浏览服务端数据目录（存档 / 日志 / 玩家数据 / 插件配置），可点开文本文件预览 |
| 设置 | 改标题 / 监听地址 / 端口 / 会话时长 / 日志缓冲 / 命令白黑名单，**改口令**，**重载配置**（换端口或开关 TLS 会热重启监听） |

玩家页底部另有**封禁名单**卡片，分「账号封禁 / IP 封禁」两个页签，内容直接取自核心的
`/ban list` 与 `/ban ip list`，并带一个「手动封 IP」入口。

### 封禁：账号 vs IP

| 方式 | 面板入口 | 走的核心命令 | 可靠性 |
|---|---|---|---|
| **账号封禁**（首选） | 「封禁账号」按钮 | `/ban add <社区账号id>` | ⭐ 与人一一对应、与网络路径无关，**换 IP 也逃不掉** |
| 记录账号 IP | 「记录账号 IP 并封禁」按钮 | `/ban ip user <社区账号id>` | 由核心按账号现场关联 IP，不用管理员猜地址 |
| 手动封 IP | 「封禁 IP」按钮 | `/ban ip add <ip>` | ⚠ 中风险：需先确认这个地址**是不是多人共用** |
| IP 实名名单 | 「手动封 IP」/ 游戏内 | `/ban ip list` | 只增不减地累加，需定期清理 |

**关于"服务端看到的 IP 是不是真的"**：早期曾以为本服经 FRP 转发、服务端只看到代理机地址
（封一个 = 封全服）。**这个判断已被实测推翻** —— 24MB 的 `Game.log` 里出现了 229 个不同的
客户端 IP / 213 个 GUID，说明**服务端拿到的是真实客户端 IP**，不是中转地址。

所以正确的判据不是"能不能封 IP"，而是**"封之前先看这个 IP 上挂了几个人"**：

- 关联 **1 个**账号 → 独占地址，封了干净利落，不会误伤；
- 关联 **>1 个**账号 → 家庭网 / 校园网 / 网吧等多人共用，封它必然连坐。

面板据此做了 `/api/connections` 端点（扫内存日志 + 磁盘 `Bugs/Game.log` 尾部 8MB，
30 秒缓存），在前端封 IP 流程里自动查一遍并给出结论：

1. 对话框里预填该玩家的连接地址作为**候选**（不是直接提交）；
2. 若该地址关联 >1 个账号，明确列出这些账号名并警告"会一起挡掉"；
3. 管理员手填了**别的**地址时，再用最新的统计复检一次；
4. 提交时若命中共用地址，再弹一次确认。

服务端侧同样有防线：`banip` 要求必须显式传 `ip`，且过一道 `LooksLikeIp` 格式校验
（拒裸数字、拒带端口，只收 IPv4 / IPv6）；账号为 `-1`（离线/单机账号）时，
所有按账号的动作都会明确拒绝并提示改用游戏内控制台。

> 补充：核心连接时的 `IsBanIp` 检查只看 `BanIpList`，所以 `BanUserIpList` 那类"跟账号走的 IP 记录"
> 不参与连接拦截，只用来事后把 IP 补进 `BanIpList`。真正挡人的始终是 `BanIpList`。
> IP 名单是**只增不减**的，误封后记得用「解除账号 IP 记录」或游戏内 `/ban ip remove` 回退。

### 页面缓存

服务端对每个响应都带 `Cache-Control: no-store, no-cache, must-revalidate, max-age=0`、
`Pragma: no-cache`、`Expires: 0`，并且**每次请求都生成新的 `ETag`** —— 正常浏览器不会缓存面板。

如果仍看到旧界面（比如中间代理缓存、或浏览器顽固缓存），点顶栏的
**「强制刷新」**：它会给地址加一个时间戳参数再 `location.replace`，绕过一切缓存重新拉页面。
顶栏还有一个 `构建 yyyyMMdd-n` 标签，可直接看出当前页面是哪个版本。

---

## HTTPS / 纯插件 TLS

### 为什么需要

面板登录后能执行服务端命令，而命令走 `CmdManager.HandleMessage(..., isTerminal: true)`，
那条路**整个跳过 AuthLevel 检查**。所以走明文 HTTP 意味着：

- 登录口令**明文过网**；
- 会话令牌**明文过网**；
- 中间人（FRP 节点 / 运营商 / 被劫持的 WiFi / 任何链路上的旁观者）拿到的就是一个
  **能执行任意白名单命令的终端**。

口令泄露 ≠ "被看见日志"，而是**直接接管服务器**。所以**公网暴露必须开 TLS**。

### 为什么不用 `HttpListener` 的 https

`HttpListener` 在 Windows 上是转发到**内核态 HTTP.sys** 的，TLS 由内核终结。它只认
**系统证书库里、且被 `netsh http add sslcert` 绑到该端口**的证书 —— 写在 C# 里的证书对象一律无效。

也就是说想用它就得：管理员权限 + 把证书装进系统证书库 + `netsh` 绑端口。这跟"纯插件、丢 DLL 就跑"的目标直接冲突。

### 这里的做法：TLS 前置终结 + 明文回环转发

```
                    公网                      本机回环（外部碰不到）
   浏览器  ──HTTPS──▶  TcpListener   ──明文──▶   HttpListener
   (8443)             + SslStream              (127.0.0.1:随机端口)
                        终结 TLS                 真正的业务逻辑
                          │
                          └── 证书自签生成 / 或读你给的 PFX（不写系统证书库）
```

1. 自己起一个 `TcpListener` 绑公网端口，用 `SslStream` 终结 TLS；
2. 解密后的 HTTP 字节**原样透明转发**到绑在 `127.0.0.1` 的内部 `HttpListener`；
3. 响应字节再原样回传。

**好处：**

| | 说明 |
|---|---|
| **零系统依赖** | 自签证书直接从内存加载，不写系统证书库、不用 `netsh`、**不需要管理员权限** |
| **业务代码一行不改** | SSE 长连接、chunked 编码、keep-alive 全由 `HttpListener` 自己处理；转发层只是搬字节，不需要理解 HTTP 语义 |
| **内部端口永不外露** | 内部 `HttpListener` 强制绑 `127.0.0.1`，公网碰不到，也就不需要为它申请 URL ACL |
| **失败不降级** | 证书拿不到就**整体启动失败**并回滚，绝不退化成"只有明文"的半吊子状态 |

**代价**：多一次本机回环转发（内存拷贝，量级可忽略）。这也是转发层必须正确处理半关闭/断连的原因，
否则 SSE 长连接会泄漏线程。

### 证书

| `CertificateSource` | 行为 |
|---|---|
| `auto`（默认） | 首次启动自动生成**自签证书**（RSA 2048 / SHA256），落到 `Plugins/网页控制台插件/网页控制台自签证书.pfx`，之后一直复用 |
| `pfx` | 用 `CertificatePfxPath` 指定的 `.pfx` / `.p12`，`CertificatePassword` 是打开口令 |

- 自签证书会写入 **SAN**（现代浏览器只看 SAN，不看 CN），`CertificateHosts` 填你的公网域名/IP，
  多个用逗号分隔。留空会自动取 `BindHost` + 本机 FQDN。
- 证书落盘复用 ⇒ **重启后指纹不变**，不会每次重启都弹一次信任警告。
- 证书文件 ACL 会收紧到仅管理员可读。

> ⚠️ **自签证书浏览器会显示"不安全"警告**，必须点「高级 → 继续前往」才能进。
> 这不妨碍加密效果（通道仍是真加密），但**首次使用要跟使用者说清楚**。
> 想彻底无警告需要买域名 + CA 签发证书，把 PFX 路径填进来即可，**无需改代码**。

> 🔒 **证书与端口无关**。X.509 SAN 里只有主机名 / IP，**没有端口字段**，SNI 也不含端口，
> 所以 8443 和 443 用的是同一张证书，任意非标准端口都能直接走。

### ⚠️ 一个改了会炸的坑：不要写死 `SslProtocols`

握手用的是 **`SslProtocols.None`**，也就是"交给操作系统决定可用协议"。**不要改回去**。

原因：写死 `Tls12 | Tls13` 时，在不支持 TLS 1.3 的系统上握手会**直接失败**（alert 40 = handshake_failure），
**连 1.2 回退都不走** —— 因为协议列表不是"上限"而是"必须支持的集合"。

实测（本机 Windows Server 2022 / build 19045）：

| `SslProtocols` | 结果 |
|---|---|
| `None` | ✅ OK（2022 自动 1.3） |
| `Tls12` | ✅ OK |
| `Tls13` | ❌ FAIL |
| `Tls12 \| Tls13` | ❌ FAIL |

`None` 的好处是**向下兼容**：Windows Server 2022 自动落 TLS 1.3，
Windows Server 2019（更老，自动落 TLS 1.2）也能正常握手。

> 📌 实际影响：**Windows Server 2019 上这套只能跑 TLS 1.2**（套件 `ECDHE-RSA-AES256-GCM-SHA384`）。
> 这仍然是安全的（AES-256-GCM + 前向保密），只是没有 1.3。
> 想上 1.3 需要更高版本的 Windows Server，不是本插件能解决的。

### 抗扫描

- **握手超时**：`TlsHandshakeTimeoutSeconds`（默认 15s）。握手慢基本就是有人在扫端口，超时直接断开。
- **并发上限**：`MaxTlsConnections`（默认 64）。超了的新连接**立即关闭**，防止连接耗尽。

---

## 安全设计

面板的全部安全措施，按"防的是谁"排列：

| 措施 | 防什么 |
|---|---|
| **TLS**（`EnableTls`） | 中间人窃听 / 劫持会话 |
| **HttpOnly Cookie**（`UseHttpOnlyCookie`，默认开） | XSS 读走会话令牌；URL 里的令牌会流进浏览器历史、Referer、FRP/反代日志 |
| **禁止 URL 令牌**（`AllowTokenInQuery`，默认禁止） | 彻底关掉"令牌进日志"这条路 |
| **会话绝对超时**（`SessionAbsoluteTimeout`，默认开） | 会话被捡到后可无限续命 |
| **会话硬上限**（`SessionMaxLifetimeMinutes`，默认 240） | "一直点着页面"把一次登录无限延长 |
| **按 IP 限流**（`MaxLoginFailures` / `LockoutSeconds`） | 单 IP 撞库 |
| **全局熔断**（`GlobalMaxLoginFailures` / `GlobalLockoutSeconds`） | **代理池轮换 IP** 绕过按 IP 限流，无限次撞库 |
| **挑战值**（`RequireChallenge`） | 区分"真人浏览器"与"直接打 HTTP 的脚本" |
| **命令白名单 + 黑名单** | 命令执行面 |
| **响应头** | 点击劫持、类型嗅探、索引收录、Referer 泄漏 |

### 三个关键设计的说明

**① 为什么默认用 HttpOnly Cookie 而不是 `Authorization` 头？**
SSE 的 `EventSource` **不能自定义 header**。要支持 SSE 就只能走 Cookie，或把令牌塞进 URL。
塞 URL 的代价是它会出现在浏览器历史、Referer、以及**服务器 / 反代 / FRP 的访问日志**里 —— 等于半公开。
所以改成 Cookie 后：SSE 靠 Cookie 鉴权，`EventSource` 加 `withCredentials` 即可。

**② 会话超时的判定顺序**

```
请求进来
  ├─ HardExpiresUtc <= now ?  → 拒绝（绝对上限，永不延长）
  ├─ ExpiresUtc <= now ?→ 拒绝（空闲超时）
  └─ 都活着 → 通过，并（仅当 SessionAbsoluteTimeout=false）滑动续期
```

`SessionAbsoluteTimeout=true` 时，判定时会把 `ExpiresUtc` 压到不超过 `HardExpiresUtc`，
所以两个上限**都**生效，且以先到者为准。

**③ 挑战值机制**
`/api/challenge` 下发一个一次性值（TTL 120 秒，绑定 IP + UA），客户端登录时在
`X-Panel-Challenge` 头里回带。用于区分真人浏览器与脚本。

> ⚠️ 这个接口**不要口令**，所以必须有硬闸：`MaxPendingEntries = 4096`，
> 满了直接返 **503**。否则攻击者能在 120 秒内塞几十万条记录撑爆内存。
> 前端对应地在**第一段 `.then`** 就 throw，不会误报成"口令错误"。

### 响应头

```
Cache-Control: no-store, no-cache, must-revalidate, max-age=0
Pragma: no-cache
Expires: 0
ETag: "<每次都变>"
X-Content-Type-Options: nosniff
X-Frame-Options: DENY
X-Robots-Tag: noindex, nofollow
Access-Control-Allow-Origin: null
Referrer-Policy: no-referrer
```

---

## 部署

把 `网页控制台插件.dll` 丢进服务端的 `Plugins/` 目录，重启服务端。

首次启动会在 `Plugins/网页控制台插件/网页控制台配置.json` 生成一份默认配置（**默认关闭**）。要启用必须做两件事：

1. 填 `Password`
2. `Enabled` 改成 `true`

### 公网暴露（推荐姿势）

```jsonc
{
  "Enabled": true,
  "BindHost": "+",              // 所有网卡
  "Password": "sha256:<你的口令的 sha256 小写十六进制>",
  "EnableTls": true,            // ← 公网暴露必开
  "TlsPort": 8443,
  "CertificateHosts": "mc30.rhymc.com",   // ← 填你实际访问用的域名/IP
  "SecureCookie": true
}
```

启动后访问 `https://mc30.rhymc.com:8443/`。

- **不需要** `netsh http add urlacl`（内部 `HttpListener` 绑的是 `127.0.0.1`，不涉及 URL ACL）
- **不需要**装证书到系统证书库
- **不需要**管理员权限
- 首次访问会提示证书不受信任 → 「高级 → 继续前往」

想免掉证书警告：买域名 + CA 签发证书，把 PFX 路径填进 `CertificatePfxPath`，`CertificateSource` 改成 `pfx`。

### 局域网 / 本机（不开放公网）

保持默认 `BindHost: "127.0.0.1"`、`EnableTls: false` 即可。

### 实测结论

在 Windows Server 上真跑验证过（不是仅编译通过）：

| 项 | 结果 |
|---|---|
| 证书 SAN | ✅ 与 `CertificateHosts` 一致 |
| TLS 协商 | ✅ 1.2 / 强套件 |
| GET / POST / Cookie / 自定义头穿透 | ✅ 全部原样转发 |
| 10 并发连接 | ✅ 不串、不粘 |
| 重启后证书指纹 | ✅ 一致，不重复弹警告 |
| 端口隔离（`BindHost="+"`） | ✅ TLS 绑 `0.0.0.0`，内部绑 `127.0.0.1` |

---

## 配置

位置：`Plugins/网页控制台插件/网页控制台配置.json`

### 基础

| 项 | 默认 | 说明 |
|---|---|---|
| `Enabled` | `false` | 总开关 |
| `BindHost` | `127.0.0.1` | 监听地址。`+` / `*` = 所有网卡（外网可访问） |
| `Port` | `8080` | 监听端口（**明文模式**下对外的端口） |
| `Password` | 空 | 登录口令，留空则**拒绝启动**。支持明文或 `sha256:<64位hex>` |
| `Title` | `服务器控制台` | 面板标题 |

### TLS

| 项 | 默认 | 说明 |
|---|---|---|
| `EnableTls` | `false` | **公网暴露必开**。开启后 `Port` 变为内部端口，对外改用 `TlsPort` |
| `TlsPort` | `8443` | TLS 对外端口。`<1024` 会被自动纠正到 8443 |
| `CertificateSource` | `auto` | `auto` = 自签并落盘复用；`pfx` = 用你指定的证书 |
| `CertificatePfxPath` | 空 | PFX 路径（`CertificateSource=pfx` 时必填） |
| `CertificatePassword` | 空 | PFX 打开口令 |
| `CertificateHosts` | 空 | 写入 SAN 的主机名，逗号分隔。留空 = 自动取 `BindHost` + FQDN |
| `InternalPort` | `0` | 内部 `HttpListener` 端口，`0` = 自动挑空闲端口。绑 127.0.0.1，外网碰不到 |
| `MaxTlsConnections` | `64` | TLS 并发连接上限，超了立即关闭 |
| `TlsHandshakeTimeoutSeconds` | `15` | 握手超时，防端口扫描 |

### 会话与鉴权

| 项 | 默认 | 说明 |
|---|---|---|
| `SessionMinutes` | `30` | 空闲超时（分钟） |
| `SessionAbsoluteTimeout` | `true` | 会话**绝对**超时，不因任何请求延长。公网建议开 |
| `SessionMaxLifetimeMinutes` | `240` | 单次登录的**绝对**存活上限，`0` = 不限 |
| `MaxLoginFailures` | `5` | 单 IP 连续失败几次就封这个 IP（`0` = 不封） |
| `LockoutSeconds` | `900` | 触发上限后封多久 |
| `GlobalMaxLoginFailures` | `30` | **全服**失败上限，绕过按 IP 限流。`0` = 关闭 |
| `GlobalLockoutSeconds` | `900` | 触发全局上限后暂停登录多久 |
| `UseHttpOnlyCookie` | `true` | 令牌放 HttpOnly Cookie。**公网关键开关** |
| `AllowTokenInQuery` | `false` | 是否允许 `?token=` 鉴权。默认禁止 |
| `SecureCookie` | `false` | 下发 `Secure` 标记 Cookie。开 TLS 时**自动纠正为 true**（除非你显式写过 `false`） |
| `RequireChallenge` | `false` | 是否要求回带挑战值 |

### 终端与日志

| 项 | 默认 | 说明 |
|---|---|---|
| `AllowedCommandPrefixes` | `help,list,who,base,shop,market,admin,tp,tp2,back,respawn,clear,time,kill,kick,give` | 网页终端命令白名单，**前缀整词匹配**；留空 = 一条都不许执行 |
| `DeniedCommandPrefixes` | `stop,ban` | 额外黑名单，优先级高于白名单 |
| `LogBufferLines` | `2000` | 日志环形缓冲行数 |
| `UseServerSentEvents` | `true` | SSE 实时推送；`false` = 前端 2 秒轮询 |
| `RequestTimeoutSeconds` | `30` | 单请求最长等待 |
| `LogActions` | `true` | 记录每次网页登录与命令执行 |

> 端口、口令、TLS 开关等改完可在**设置页点「重载配置」热生效**（会重启监听）；
> 其余项需要重启服务端。热重载失败会自动回退到上一份可用配置，不会把面板搞挂。

口令建议只放哈希：

```json
"Password": "sha256:<你的口令的 sha256 小写十六进制>"
```

---

## ⚠️ 安全边界（最重要的一节）

这个面板登录后**可以执行服务端命令**，而命令走的是 `CmdManager.HandleMessage(..., isTerminal: true)`，那条路会**整个跳过 AuthLevel 检查**（核心按"来自终端"处理）。

所以：

- **面板的登录口令 + `AllowedCommandPrefixes` 是唯一的闸门**，不是"多一层保险"。
- 因此 `BindHost` 默认只绑 `127.0.0.1`（只有本机能访问）。要让外网访问必须显式改成 `+`/`*`，并且**先设好强口令**。
- `Password` 留空时插件**拒绝启动**——宁可不开，也不开一个无鉴权的命令终端。
- 明文模式下，Windows 上用 `+` / `*` 前缀需要 URL ACL（管理员执行 `netsh http add urlacl url=http://+:8080/ user=Everyone`），否则启动会抛 `HttpListenerException`，日志里会写清楚。
  **开了 `EnableTls` 就绕开了这一条** —— 内部监听绑的是 `127.0.0.1`。

### 这一版仍然挡不住什么

诚实说明，避免误以为"开了 TLS 就万事大吉"：

- **不能挡主动中间人（Active MITM）**。自签证书没有 CA 背书，浏览器只是"被警告后仍可继续"，
  一个能改路由的中间人配合社工诱导点「继续前往」，照样能读明文。**对抗主动 MITM 只能靠真 CA 证书。**
- **不防服务端进程被提权**。面板的权限就是服务端进程的权限。
- **命令白名单是唯一的执行面闸门**。往里加 `stop` / `ban` 就是真的交出去了。

---

## 与基础插件的关系

2026-10-05 从「基础插件」整合包里**分离**出来，现在是独立插件、独立配置。

分离原因：它自带一个 HTTP 服务与命令执行通道，和"游戏逻辑整合包"的发布节奏、安全边界都不一样，绑在一起两边都难维护。

> ⚠️ **不要两边同时开**。旧版基础插件里那份已经在源码层面删掉了，但如果你手上还有更早的 `基础插件.dll`，它和本插件会抢同一个端口，后启动的那个会失败。

---

## 已知限制

- **背包只能显示"名字 + 数量 + 槽位"**：服务端拿不到方块贴图数据，没法画图标
- **延迟可能显示 `-`**：核心不暴露 ping，插件靠反射 LiteNetLib 的 `Peer.Ping`，拿不到就是空
- **没有"维度"列**：本版本服务端没有维度概念
- **TPS / MSPT 是自量的**，不是核心提供的读数
- **TLS 1.3 取决于操作系统**：Windows Server 2019 只能到 1.2（原因见 [上面那个坑](#️-一个改了会炸的坑不要写死-sslprotocols)）
- **自签证书有浏览器警告**：要彻底消除需要 CA 签发证书
- 独立运行时，页面上的"已验证管理员"标记退化为 `ServerManager`（拿不到基础插件 `/pw` 二次验证的内存态）。装了基础插件想加强判定，改 `网页数据接口.cs` 里那一处即可，源码里有注释指路

---

## 线程模型（改代码前必读）

`HttpListener` 的回调跑在**线程池**上，而 `Project` / `Subsystem` / `ComponentPlayer` 都是主线程每帧在改的对象。

- 任何碰游戏对象的读取，都**不能**直接在回调里做——会读到撕裂状态甚至崩服
- 做法是把读操作**排队到主线程**（`Update` 里取队列执行），回调这边用 `ManualResetEventSlim` 等结果，带超时
- 命令执行更严格：`CmdManager` 与地形/实体都是主线程的东西

同理，SSE 的日志推送**不在 `Log` 回调里直接写**（写日志的线程可能是存档线程/网络线程/主线程，慢客户端会把它们全堵住），而是回调只置信号，由专门的泵线程统一写。

### TLS 终结层的线程

每条 TLS 连接占一个线程（`MaxTlsConnections` 默认 64 封顶），连接内部再起一个上行线程做字节泵。
`IsBackground = true` 保证关服时不会被卡住。握手用 `BeginAuthenticateAsServer` + 超时等待，
避免扫描器用一个慢握手占满连接数。

---

## 常见问题

**Q：开了 TLS 还需要 `netsh http add urlacl` 吗？**
不需要。内部 `HttpListener` 强制绑 `127.0.0.1`，不涉及 URL ACL。只有**明文模式**下用 `BindHost: "+"` 才需要。

**Q：证书文件名改了会怎样？**
删掉 `Plugins/网页控制台插件/网页控制台自签证书.pfx` 即可重新生成，但**新的指纹会让浏览器重新弹一次信任警告**。

**Q：为什么端口选 8443 而不是 443？**
Windows 上 HTTP.sys 保留 `<1024` 端口，80/443 需要特权且容易撞系统服务。8443 及以上是"特权端口之外"，非管理员就能绑，也不用抢 443。

**Q：`RequireChallenge` 要开吗？**
它防的是"直接打 HTTP 的脚本"，不是撞库（撞库由限流和熔断管）。开了会更安全，代价是老前端缓存可能登不进去 —— 强制刷新一次即可。

**Q：改了 `SecureCookie: false` 但开 TLS 时又被自动改回 true？**
不会。插件会区分「你主动写了 `false`」与「老配置里根本没这个字段」：只有后者才自动纠正成 `true`。

**Q：面板能防住有权限的客户端作弊吗？**
不能，也不该指望它。客户端与服务端的信任边界在核心里；面板是管理面，不是反作弊。