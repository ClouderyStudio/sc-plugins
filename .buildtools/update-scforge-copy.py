# -*- coding: utf-8 -*-
"""
更新 SCForge 上web-panel 资源的介绍文案。

背景：publish-to-scforge.py 发现"资源已存在"时只追加版本，**不会更新元数据**，
所以清单里改好的 Summary / Description / Tags 永远传不上去。这个脚本补上这一段。

⚠ PUT /scforge/addons/{id} 是 multipart 且**整份替换**语义：不传的字段会被清空。
   所以这里把线上现值逐字段读回并原样回填，只改介绍相关的字段。

用法：
    export SCFORGE_TOKEN=scf_xxx
    python .buildtools/update-scforge-copy.py --dry-run   # 只打印将要提交的内容
    python .buildtools/update-scforge-copy.py             # 真发
"""
import argparse
import io
import json
import os
import sys
import urllib.error
import urllib.parse
import urllib.request
import uuid

sys.stdout.reconfigure(encoding="utf-8")

API = "https://api.cldery.com"
MANIFEST = ".buildtools/release-manifest.json"
PLUGIN = "网页控制台插件"
UA = "Mozilla/5.0 (sc-plugins release tool)"

# readme 正文：把仓库 README 的核心内容搬过来（去掉 GitHub 专有的相对锚点链接）。
README_BODY = """# 网页控制台插件

在**服务端进程内**嵌一个 HTTP(S) 服务，浏览器直接开面板：看服务器状态、管玩家、看背包、敲命令、实时刷日志。

不依赖任何外部程序（不装 Nginx、不起第二个进程、不改系统证书库）—— **关服即关站**。页面和接口全部由插件自己吐字符串，不落任何前端文件到磁盘。

## 页签

| 页签 | 内容 |
| --- | --- |
| 概览 | 在线人数 / 上限、TPS、MSPT、游戏内天数与时刻、存档大小、运行时长、内存 |
| 性能 | 自己采样的 TPS / MSPT 折线（核心没有现成的 TPS，只能自量） |
| 玩家 | 列表（名字、坐标、血量、等级、模式、管理员标记、延迟、连接地址）+ 踢人 / 回血 / 修复生存状态 / 击杀 / 切模式 / 清背包 / 无敌 / 送回重生点 / 封禁账号 / 封禁 IP |
| 背包 | 指定玩家的生存背包在前，创造背包折叠在后（服务端拿不到贴图，只能给"名字 + 数量 + 槽位"） |
| 终端 | 敲服务端命令，带命令白名单闸门 |
| 日志 | 实时日志流（SSE 长连接，连不上自动退回 2 秒轮询） |
| 存档 | 只读浏览服务端数据目录，可点开文本文件预览 |
| 设置 | 改标题 / 端口 / 会话时长 / 命令白黑名单、改口令、热重载配置 |

玩家页底部另有**封禁名单**卡片，分「账号封禁 / IP 封禁」两个页签，内容取自核心的 `/ban list` 与 `/ban ip list`。

### 封禁：账号 vs IP

| 方式 | 面板入口 | 走的核心命令 | 可靠性 |
| --- | --- | --- | --- |
| **账号封禁**（首选） | 「封禁账号」 | `/ban add <社区账号id>` | 与人一一对应，换 IP 也逃不掉 |
| 记录账号 IP | 「记录账号 IP 并封禁」 | `/ban ip user <社区账号id>` | 由核心按账号现场关联 IP |
| 手动封 IP | 「封禁 IP」 | `/ban ip add <ip>` | 中风险：需先确认这个地址是不是多人共用 |

**封IP 前的判据不是"能不能封"，而是"这个 IP 上挂了几个人"**：
关联 1 个账号 = 独占地址，封了干净；关联 >1 个 = 家庭网 / 校园网 / 网吧，封它必然连坐。

面板做了 `/api/connections` 端点（扫内存日志 + 磁盘 `Bugs/Game.log` 尾部 8MB，30 秒缓存），封 IP 流程里自动查一遍：预填候选地址、>1 个账号时列出账号名并警告"会一起挡掉"、手填了别的地址再复检一次、命中共用地址再弹一次确认。

服务端侧同样有防线：`banip` 要求显式传 `ip` 且过 `LooksLikeIp` 格式校验（拒裸数字、拒带端口）；账号为 `-1`（离线/单机账号）时所有按账号的动作明确拒绝并提示改用游戏内控制台。

> 核心连接时的 `IsBanIp` 检查只看 `BanIpList`，所以 `BanUserIpList` 那类"跟账号走的 IP 记录"不参与连接拦截，只用来事后把 IP 补进 `BanIpList`。真正挡人的始终是 `BanIpList`。

## HTTPS / 纯插件 TLS

面板登录后能执行服务端命令，而命令走 `CmdManager.HandleMessage(..., isTerminal: true)`，那条路**整个跳过 AuthLevel 检查**。所以走明文 HTTP 意味着**口令与会话令牌明文过网**，中间人拿到的就是一个能执行命令的终端。

`HttpListener` 的 https 在 Windows 上由**内核 HTTP.sys** 终结 TLS，只认系统证书库 + `netsh http add sslcert` 绑定的证书，写在 C# 里无效 —— 与"纯插件"冲突。

**本插件的做法**：自己起 `TcpListener` + `SslStream` 在公网端口终结 TLS，把解密后的字节**透明转发**到绑在 `127.0.0.1` 的内部 `HttpListener`。

- 真加密，且**零系统依赖、免管理员**，不需要 `netsh http add urlacl`
- **业务代码一行不改** —— SSE 长连接 / chunked / keep-alive 全由 `HttpListener` 处理，转发层只搬字节
- 内部端口永不外露
- 证书默认**自签并落盘复用**（重启后指纹不变，不重复弹警告），也可填自己的 CA 签发 PFX，**无需改代码**
- 握手超时 + 并发上限，防端口扫描与连接耗尽
- 证书拿不到就**整体启动失败**并回滚，绝不退化成明文

> ⚠ 自签证书浏览器会提示"不安全"，需点「高级 → 继续前往」—— 通道仍是真加密。想彻底无警告需要 CA 签发证书。

### 一个改了会炸的坑

握手用的是 `SslProtocols.None`（交给操作系统决定），**不要写死 `Tls12 | Tls13`**：在不支持 TLS 1.3 的系统上会直接 alert 40 失败，**连 1.2 回退都不走**（协议列表是"必须支持的集合"而不是上限）。

`None` 的效果是向下兼容：Windows Server 2022 自动落 TLS 1.3，Windows Server 2019 自动落 TLS 1.2（AES-256-GCM + 前向保密，仍安全）。

## 安全设计

| 措施 | 防什么 |
| --- | --- |
| 纯插件 TLS | 中间人窃听 / 劫持会话 |
| HttpOnly Cookie（SSE 靠它，EventSource 不能自定义 header） | XSS 读走令牌；URL 令牌会流进历史 / Referer / FRP 日志 |
| 会话绝对超时 + 单次登录硬上限 | 会话被捡到后无限续命 |
| 按IP 限流 **+ 全局熔断** | **代理池轮换 IP** 绕过按 IP 限流无限撞库 |
| 一次性挑战值 | 区分真人浏览器与直接打 HTTP 的脚本 |
| 命令白名单 + 黑名单 | 命令执行面 |
| `X-Frame-Options: DENY` / `nosniff` / `no-referrer` | 点击劫持、类型嗅探、Referer 泄漏 |

**会话超时判定顺序**：先看 `HardExpiresUtc`（绝对上限，永不延长），再看 `ExpiresUtc`（空闲超时），都活着才通过，且仅在 `SessionAbsoluteTimeout=false` 时滑动续期。

**响应头**：`Cache-Control: no-store...` + 每次都变的 `ETag` + `nosniff` + `DENY` + `noindex, nofollow` + `Access-Control-Allow-Origin: null` + `no-referrer`。

## ⚠️ 重要限制

**网页终端的命令走 `isTerminal:true`，整个跳过 AuthLevel 检查，所以口令 + 命令白名单是唯一的闸门**，不是"多一层保险"。请务必配好 `Password`（支持只放 `sha256:<hex>`）与 `AllowedCommandPrefixes`，公网暴露务必开 `EnableTls`。

这一版仍然挡不住：

- **主动中间人（Active MITM）**。自签证书没有 CA 背书，浏览器只是"被警告后仍可继续"，能改路由的中间人配合社工诱导点「继续前往」照样能读明文。**对抗主动 MITM 只能靠真 CA 证书。**
- **服务端进程被提权**。面板的权限就是服务端进程的权限。

其他已知限制：背包只能显示"名字 + 数量 + 槽位"（服务端拿不到方块贴图）；延迟可能显示 `-`（核心不暴露 ping）；没有"维度"列；TPS / MSPT 是自量的。

## 安装

把 `网页控制台插件.dll` 放进服务端 `Plugins/` 目录，重启服务端。首次启动会生成 `Plugins/网页控制台插件/网页控制台配置.json`（**默认关闭**）。

启用需改两处：`Enabled: true` 与 `Password`。

### 公网暴露配置示例

```json
{
  "Enabled": true,
  "BindHost": "+",
  "Password": "sha256:<你的口令的 sha256 小写十六进制>",
  "EnableTls": true,
  "TlsPort": 8443,
  "CertificateHosts": "你的公网域名",
  "SecureCookie": true
}
```

启动后访问 `https://你的公网域名:8443/`。

> 证书与端口无关：X.509 SAN 里只有主机名 / IP，**没有端口字段**，所以任意非标准端口都能直接走。Windows保留 `<1024` 端口，故默认 8443。

## 与其它插件的关系

2026-10-05 从「基础插件」整合包里分离出来，现在是独立插件、独立配置。

⚠️ **不要和更早版本的 `基础插件.dll` 同时开** —— 它会抢同一个端口，后启动的那个会失败（旧版已在源码层面删除该功能）。

## 线程模型（改代码前必读）

`HttpListener` 的回调跑在线程池上，而 `Project` / `Subsystem` / `ComponentPlayer` 都是主线程每帧在改的对象：

- 任何碰游戏对象的读取都**不能**在回调里直接做（会读到撕裂状态甚至崩服），做法是排队到主线程、在 `Update` 里执行，回调用 `ManualResetEventSlim` 等结果并带超时
- 命令执行更严格：`CmdManager` 与地形/实体都是主线程的东西
- SSE 的日志推送**不在 `Log` 回调里直接写**（慢客户端会堵住存档/网络/主线程），而是回调只置信号，由专门的泵线程统一写
- TLS 层每条连接占一个线程（`MaxTlsConnections` 默认 64 封顶），连接内部再起一个上行线程做字节泵，全部 `IsBackground = true`

## 兼容性

兼容 SurvivalCraft 2.4.0，AGPL-3.0 开源。"""


def die(msg):
    print("错误：" + msg)
    sys.exit(1)


def request(method, path, token=None, fields=None, files=None, allow_404=False):
    url = API + path
    headers = {"User-Agent": UA}
    if token:
        headers["Authorization"] = "Bearer " + token
    body = None
    if fields is not None or files is not None:
        boundary = "----scforge" + uuid.uuid4().hex
        parts = []
        for k, v in (fields or []):
            parts.append(("--%s\r\nContent-Disposition: form-data; name=\"%s\"\r\n\r\n%s\r\n"
                          % (boundary, k, v)).encode("utf-8"))
        for k, filename, blob in (files or []):
            parts.append(("--%s\r\nContent-Disposition: form-data; name=\"%s\"; filename=\"%s\"\r\n"
                          "Content-Type: application/octet-stream\r\n\r\n" % (boundary, k, filename)).encode("utf-8"))
            parts.append(blob)
            parts.append(b"\r\n")
        parts.append(("--%s--\r\n" % boundary).encode("utf-8"))
        body = b"".join(parts)
        headers["Content-Type"] = "multipart/form-data; boundary=" + boundary
    req = urllib.request.Request(url, data=body, headers=headers, method=method)
    try:
        with urllib.request.urlopen(req, timeout=60) as resp:
            raw = resp.read().decode("utf-8", "replace")
            return resp.status, (json.loads(raw) if raw.strip() else None)
    except urllib.error.HTTPError as e:
        detail = e.read().decode("utf-8", "replace")
        if e.code == 404 and allow_404:
            return 404, None
        die("%s %s -> %d %s" % (method, path, e.code, detail[:400]))
    except urllib.error.URLError as e:
        die("网络错误 %s %s：%s" % (method, path, e))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--token", help="SCForge API Key（默认读环境变量 SCFORGE_TOKEN）")
    ap.add_argument("--dry-run", action="store_true", help="只打印将要提交的内容，不发请求")
    args = ap.parse_args()

    token = args.token or os.environ.get("SCFORGE_TOKEN")
    if not token:
        die("没有 SCForge API Key。设SCFORGE_TOKEN=scf_xxx（需 publish 作用域）。")

    manifest = json.load(io.open(MANIFEST, encoding="utf-8"))
    recipe = manifest["Scforge"]["Items"][PLUGIN]

    # ---- 读回线上现值（PUT 是整份替换，必须逐字段回填）----
    code, data = request("GET", "/scforge/addons/web-panel", token)
    cur = (data or {}).get("addon") or {}
    addon_id = cur.get("id")
    if not addon_id:
        die("拿不到 web-panel 的 id")
    print("当前资源 id=%s status=%s" % (addon_id, cur.get("status")))

    # ⚠ Tags 必须取自站点白名单，否则 400 "不支持的标签：xxx"。
    #   白名单共 20 个（从前端 index bundle 的 Vn 数组提取）：
    #   survival creative pvp pve multiplayer singleplayer adventure technical decoration
    #   magic technology food transport mining farming server client library chinese open-source
    #   ⚠ 没有 https / security / web 之类 —— 描述 HTTPS 的事只能靠文案，不能靠标签。
    #   上限 6 个。
    ALLOWED_TAGS = {
        "survival", "creative", "pvp", "pve", "multiplayer", "singleplayer",
        "adventure", "technical", "decoration", "magic", "technology", "food",
        "transport", "mining", "farming", "server", "client", "library",
        "chinese", "open-source",
    }
    tags = list(recipe.get("Tags") or cur.get("tags") or [])
    bad = [t for t in tags if t not in ALLOWED_TAGS]
    if bad:
        die("清单里有非法标签：%s\n合法值：%s" % ("、".join(bad), "、".join(sorted(ALLOWED_TAGS))))
    tags = tags[:6]

    # 顺序与前端 EditPluginPage 的 FormData 一致；tags 是重复字段。
    fields = [
        ("summary", recipe.get("Summary") or cur.get("summary") or ""),
        ("description", recipe.get("Description") or cur.get("description") or ""),
        ("readme", recipe.get("Readme") or README_BODY),
        ("category", recipe.get("Category") or cur.get("category") or "misc"),
        # ⚠ gameVersion 单数，且**沿用线上现值**：清单里的 GameVersion 是发布时的声明，
        #   直接覆盖会让页面上显示的兼容版本与实际不符。
        ("gameVersion", cur.get("gameVersion") or ""),
    ]
    for t in tags:
        fields.append(("tags", t))
    fields += [
        ("sourceUrl", cur.get("sourceUrl") or manifest["Scforge"].get("SourceUrl")
         or "https://github.com/ClouderyStudio/sc-plugins"),
        ("issuesUrl", cur.get("issuesUrl") or ""),
        # ⚠ 线上还写着 MIT，但仓库 2026-10-05 起已是 AGPL-3.0（见 git log 9d223ad）。
        #   这里跟随仓库，不跟随线上残留值。
        ("license", manifest["Scforge"].get("License") or "AGPL-3.0"),
        ("licenseUrl", cur.get("licenseUrl") or ""),
        ("donationUrl", cur.get("donationUrl") or ""),
        ("discordUrl", cur.get("discordUrl") or ""),
        ("accessMode", cur.get("accessMode") or "public"),
        ("accessHint", cur.get("accessHint") or ""),
    ]

    print("\n将要提交的字段（PUT 整份替换语义，未列出的字段会被清空）：")
    for k, v in fields:
        shown = v if len(v) <= 90 else (v[:87] + "...")
        print("  %-12s len=%-5d %s" % (k, len(v), shown.replace("\n", "\\n")))
    print("\n保持不变（未提交，因此不会被改动）：gallery=%d 项、accessMode=%s"
          % (len(cur.get("gallery") or []), cur.get("accessMode")))

    if args.dry_run:
        print("\n[dry-run] 未发任何请求。去掉 --dry-run 即执行。")
        return

    code, data = request("PUT", "/scforge/addons/" + urllib.parse.quote(addon_id),
                         token, fields=fields)
    ok = (data or {}).get("success")
    out = (data or {}).get("addon") or {}
    print("\nHTTP %d success=%s" % (code, ok))
    if out:
        print("回读 summary=%r" % out.get("summary"))
        print("回读 tags=%s" % out.get("tags"))
        print("回读 license=%r status=%r" % (out.get("license"), out.get("status")))
        print("回读 desc len=%d | readme len=%d" % (len(out.get("description") or ""),
                                                  len(out.get("readme") or "")))
    print("\n注意：编辑会把资源重新送审，通过前对外不可见。")


if __name__ == "__main__":
    main()