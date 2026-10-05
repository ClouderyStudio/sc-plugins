# -*- coding: utf-8 -*-
"""
把 sc-plugins 的 Release 产物上传到 SCForge 资源平台（api.cldery.com）。

背景与定位
----------
上游 .buildtools/publish-release.py 负责「编译 -> 提交 -> 打 tag -> 推 GitHub -> 建 Release -> 传附件」。
本脚本是它的下游：读同一份 .buildtools/release-manifest.json，把每个 DLL 顺带发到 SCForge。

为什么上传源是 GitHub Release 的加速链接
----------------------------------------
SCForge 的发布接口要的是**文件本体**（multipart），它自己不认「从 GitHub 拉」这种玩法 ——
平台没有代下载能力，所以必须先在本机把文件拿到，再传上去。
但对外暴露下载入口时，我们希望资源页上的「下载」指向 GitHub Release（省自己的带宽与存储）。
国内直连 GitHub Release 很慢，所以用加速前缀包一层：

    https://gh-proxy.com/https://github.com/ClouderyStudio/sc-plugins/releases/download/v1.0.0/web-panel.dll

本脚本两种模式都支持，且默认走「加速链接」是为了统一口径：

    --from-file   （默认）直接用本地已编译的 DLL 上传，最快，不依赖网络。
                  注意：SCForge 侧存的是文件副本，与 GitHub 上那份是两份独立存储。
    --from-url    先从加速链接把 DLL 下载到临时目录，再上传。
                  产出的内容与 GitHub Release 上那份**逐字节一致**，能保证平台上的包
                  与 Release 附件不会漂移。CI 里用这个模式（runner 上没编译产物）。

用法（本仓库根目录）：

    python .buildtools/publish-to-scforge.py v1.0.0                    # 用本地 DLL
    python .buildtools/publish-to-scforge.py v1.0.0 --from-url         # 走加速链接
    python .buildtools/publish-to-scforge.py v1.0.0 --dry-run          # 只打印计划
    python .buildtools/publish-to-scforge.py v1.0.0 --only peace-zone  # 只发一个插件
    python .buildtools/publish-to-scforge.py v1.0.0 --with-basic       # 连不开源的基础插件一起发

前置：
    - SCForge API Key 从环境变量 SCFORGE_TOKEN 读（形如 scf_xxx），需要 publish 作用域。
    - 资源已存在时，本脚本走「追加版本」而不是「重复创建」——它先按 slug 查自己的资源列表。
"""

import argparse
import json
import os
import sys
import urllib.error
import urllib.parse
import urllib.request
import uuid

API_BASE = "https://api.cldery.com"
SNAPSHOT_FILE = ".buildtools/_release-snapshot.json"
REPO_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))

# 加速前缀：留空即直连 GitHub。取 gh-proxy.com —— 它对
# /<owner>/<repo>/releases/download/... 有专门处理，且支持 Range 请求（大文件断点续传靠它）。
PROXY_PREFIX = "https://gh-proxy.com/"


def log(msg):
    sys.stdout.write(msg + "\n")
    sys.stdout.flush()


def die(msg):
    sys.stderr.write("ERROR: " + msg + "\n")
    sys.exit(1)


# ---------------------------------------------------------------- HTTP 薄封装

def multipart(fields, files):
    """手搓 multipart/form-data。

    不用 requests：本仓库的其它脚本一律只用标准库，少一个依赖就少一处 CI 上的意外。

    fields : [(name, value)]，value 为 None 时跳过（不提交该字段）
    files  : [(name, filename, bytes)]
    """
    boundary = "----scplugins" + uuid.uuid4().hex
    nl = b"\r\n"
    buf = []
    for name, value in fields:
        if value is None:
            continue
        if isinstance(value, (list, tuple)):
            # 数组字段：同名重复出现（OpenAPI 里 Tags / GameVersions 就是多值表单字段）
            for v in value:
                buf += [b"--" + boundary.encode(), nl,
                        b'Content-Disposition: form-data; name="%s"' % name.encode(), nl, nl,
                        str(v).encode("utf-8"), nl]
            continue
        buf += [b"--" + boundary.encode(), nl,
                b'Content-Disposition: form-data; name="%s"' % name.encode(), nl, nl,
                str(value).encode("utf-8"), nl]
    for name, filename, blob in files:
        buf += [b"--" + boundary.encode(), nl,
                b'Content-Disposition: form-data; name="%s"; filename="%s"'
                % (name.encode(), filename.encode("utf-8")), nl,
                b"Content-Type: application/octet-stream", nl, nl, blob, nl]
    buf += [b"--" + boundary.encode() + b"--", nl]
    return boundary, b"".join(buf)


def scforge(method, path, token, fields=None, files=None, allow_404=False, raise_http=False):
    """调 SCForge 接口。

    raise_http=True 时把 HTTP 错误码原样抛出（SCForgeHttpError）而不是退出进程 ——
    上层靠它做降级判断（比如"缺少读取作用域"就换一条路），不要用它做正常流程控制。
    """
    url = API_BASE + path
    headers = {
        "Authorization": "Bearer " + token,
        "Accept": "application/json",
        "User-Agent": "sc-plugins-publisher",
    }
    data = None
    if fields is not None or files is not None:
        boundary, data = multipart(fields or [], files or [])
        headers["Content-Type"] = "multipart/form-data; boundary=" + boundary
    req = urllib.request.Request(url, data=data, headers=headers, method=method)
    try:
        with urllib.request.urlopen(req) as resp:
            body = resp.read()
            return json.loads(body.decode("utf-8")) if body else None
    except urllib.error.HTTPError as e:
        body = e.read().decode("utf-8", "replace")
        if allow_404 and e.code == 404:
            return None
        # SCForge 的错误体固定是 {"detail": "..."}，直接把它抬出来，别让用户去猜 HTTP 码
        detail = body[:600]
        try:
            parsed = json.loads(body)
            if isinstance(parsed, dict) and "detail" in parsed:
                detail = str(parsed["detail"])
        except ValueError:
            pass
        if raise_http:
            raise SCForgeHttpError(e.code, detail)
        die("SCForge %s %s 返回 %d：%s" % (method, path, e.code, detail))


class SCForgeHttpError(Exception):
    def __init__(self, code, detail):
        Exception.__init__(self, "%d %s" % (code, detail))
        self.code = code
        self.detail = detail


def download(url, dest, attempts=5, delay=10):
    """下载到 dest。优先走加速前缀；失败退回直连（加速站偶发抽风，不该让发版卡死）。

    ⚠️ 带重试：打 tag 会同时触发 CI 的 publish-scforge 与本机的 Release 创建，
    两条流程是**并行**的 —— CI 常常跑得比 Release 附件上传更快，此时取附件会拿到 404。
    这种情况等几秒就好了，不能当硬失败（踩过：v1.0.2 的 CI 就是这样红的）。
    """
    candidates = []
    if PROXY_PREFIX and url.startswith("https://github.com/"):
        candidates.append(PROXY_PREFIX + url)
    candidates.append(url)

    import time
    last = None
    for attempt in range(1, attempts + 1):
        for u in candidates:
            try:
                req = urllib.request.Request(u, headers={"User-Agent": "sc-plugins-publisher"})
                with urllib.request.urlopen(req) as resp, open(dest, "wb") as out:
                    out.write(resp.read())
                if attempt > 1:
                    log("      （第 %d 次尝试成功）" % attempt)
                return u
            except Exception as exc:  # noqa: BLE001 - 逐个候选尝试，最后统一报错
                last = exc
        if attempt < attempts:
            log("      附件暂不可取（%s），%d 秒后重试 %d/%d"
                % (last, delay, attempt, attempts - 1))
            time.sleep(delay)
    die("下载失败：%s（重试 %d 次仍失败，最后一次错误：%s）\n"
        "  CI 里出现这个通常是因为 Release 附件还没传完；等一会儿重跑该作业即可。"
        % (url, attempts, last))


# ---------------------------------------------------------------- 清单读取

def load_manifest():
    path = os.path.join(REPO_ROOT, ".buildtools", "release-manifest.json")
    if not os.path.exists(path):
        die("找不到 " + path)
    with open(path, encoding="utf-8") as fh:
        return json.load(fh)


def plan(manifest, version, only=None, with_basic=False, present_assets=None):
    """把清单拍平成 [(plugin, name, dll_path, recipe)]。

    with_basic=True 时额外把不开源的基础插件也带上 —— 它不在 Assets 里（不进公开仓库），
    但有时确实想发到平台上，所以留一个显式开关，默认不带。

    present_assets: 只保留附件名出现在这个集合里的插件。
    ⚠️ CI 走 --from-url 时必须传它 —— Release 可能是 --only 发的（只有 1 个附件），
    而清单是全量 9 个；不筛的话会去下根本不存在的附件，直接 404 崩掉作业。
    """
    sc = manifest.get("Scforge") or {}
    items = sc.get("Items") or {}

    def matches(plugin, name, recipe):
        """--only 接受三种写法：插件中文名、附件英文名（带不带 .dll 都行）、slug。"""
        if not only:
            return True
        candidates = {plugin, name, name[:-4] if name.endswith(".dll") else name,
                      recipe.get("Slug")}
        return only in candidates

    out = []
    for asset in manifest.get("Assets", []):
        plugin = asset["Plugin"]
        if present_assets is not None and asset["Name"] not in present_assets:
            continue
        if plugin not in items:
            if not only or plugin == only or asset["Name"] == only:
                log("  跳过 %s（清单里没有 Scforge 配方）" % plugin)
            continue
        recipe = items[plugin]
        if not matches(plugin, asset["Name"], recipe):
            continue
        out.append((plugin, asset["Name"], asset["File"], recipe))
    if with_basic and (not only or only == "基础插件"):
        basic = os.path.join("基础插件", "基础插件.dll")
        out.append(("基础插件", "ScBase.dll", basic, {
            "Slug": "sc-base",
            "Summary": "云术服务器基础插件（整合包，不开源，仅二进制分发）",
            "Tags": ["core"],
        }))
    return out, sc


def release_asset_names(version):
    """问 GitHub：这个 Release 到底挂了哪几个附件。

    需要 GH_TOKEN / GITHUB_TOKEN（CI 里用内置的 GITHUB_TOKEN 即可）。
    拿不到就返回 None —— 调用方会退回"按清单全量"的老行为，不至于因为查不到就罢工。
    """
    token = os.environ.get("GH_TOKEN") or os.environ.get("GITHUB_TOKEN")
    repo = os.environ.get("GITHUB_REPOSITORY")
    if not token or not repo:
        return None
    url = "https://api.github.com/repos/%s/releases/tags/%s" % (repo, version)
    req = urllib.request.Request(url, headers={
        "Authorization": "Bearer " + token,
        "Accept": "application/vnd.github+json",
        "User-Agent": "publish-to-scforge",
    })
    try:
        with urllib.request.urlopen(req, timeout=30) as resp:
            data = json.load(resp)
    except Exception as exc:
        log("  ⚠ 查 Release 附件失败（%s），将按清单全量处理" % exc)
        return None
    return {a["name"] for a in (data.get("assets") or [])}


def release_url(version, name):
    return ("https://github.com/ClouderyStudio/sc-plugins/releases/download/"
            "%s/%s" % (version, name))


# ---------------------------------------------------------------- 主流程

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("version", help="版本号，须与 GitHub Release 的 tag 一致，形如 v1.0.0")
    ap.add_argument("--from-url", action="store_true",
                    help="先把 Release 附件（经加速链接）下载下来再上传，保证与 Release 逐字节一致")
    ap.add_argument("--only", help="只处理一个插件（接受插件中文名 / 附件英文名 / slug）")
    ap.add_argument("--with-basic", action="store_true",
                    help="连基础插件（未开源）一起发")
    ap.add_argument("--dry-run", action="store_true", help="只打印计划，不发任何请求")
    ap.add_argument("--token", help="SCForge API Key（默认读环境变量 SCFORGE_TOKEN）")
    args = ap.parse_args()

    version = args.version if args.version.startswith("v") else "v" + args.version

    manifest = load_manifest()
    # --from-url（CI 走的路径）：先问 Release 挂了哪几个附件，只同步这几个。
    # 否则 --only 发的 Release（1 个附件）会被按清单全量处理，去下 8 个不存在的附件。
    present = release_asset_names(version) if args.from_url else None
    if present is not None:
        log("[0/2] Release %s 有 %d 个附件：%s" % (version, len(present), ", ".join(sorted(present))))
    entries, sc = plan(manifest, version, only=args.only,
                       with_basic=args.with_basic, present_assets=present)
    if not entries:
        die("没有要处理的插件。检查 release-manifest.json 与 --only。")

    kind = sc.get("Kind", "plugin")
    log("=== 上传到 SCForge：%s（kind=%s，共 %d 个）===" % (version, kind, len(entries)))

    if args.dry_run:
        for plugin, name, path, recipe in entries:
            src = "Release 加速链接" if args.from_url else path
            log("  %-10s slug=%-16s <- %s" % (name, recipe.get("Slug", "?"), src))
        log("  来源 URL 模板：%s" % (PROXY_PREFIX + release_url(version, "<name>.dll")))
        return

    token = args.token or os.environ.get("SCFORGE_TOKEN")
    if not token:
        die("没有 SCForge API Key。请设环境变量 SCFORGE_TOKEN=scf_xxx（需 publish 作用域）。\n"
            "  获取：登录 https://scforge.cldery.com/api-keys 签发。")

    # 判断"该新建资源还是该追加版本"，需要知道平台上已有哪些资源。
    # 首选 GET /scforge/addons/mine（一次拿全），但它要求 read 作用域 ——
    # 只勾了 publish 的 Key 会被 403 拦下。这时不报错，退化成"先试追加、失败再新建"：
    # 每次 POST /addons/{slug}/versions 用 slug 试，404 就说明这个资源还不存在。
    by_slug = {}
    try:
        mine = scforge("GET", "/scforge/addons/mine", token, raise_http=True) or {}
        for addon in (mine.get("items") or mine.get("data") or []):
            if addon.get("slug"):
                by_slug[addon["slug"]] = addon
        log("[1/2] 已取回我的资源 %d 个" % len(by_slug))
    except SCForgeHttpError as exc:
        if exc.code in (401, 403):
            log("[1/2] 这把 Key 没有「读取」作用域，改为逐个探测资源是否已存在")
            log("      （想一次拿全列表的话，重新签发时把 read 与 publish 一起勾上）")
        else:
            die("读取我的资源失败：%d %s" % (exc.code, exc.detail))

    tmpdir = os.path.join(REPO_ROOT, ".buildtools", "_scforge-staging")
    if args.from_url:
        os.makedirs(tmpdir, exist_ok=True)

    skipped = 0
    for plugin, name, path, recipe in entries:
        slug = recipe.get("Slug")
        if not slug:
            die("插件 %s 的 Scforge 配方缺少 Slug" % plugin)

        # ---- 取文件字节 ----
        if args.from_url:
            dest = os.path.join(tmpdir, name)
            used = download(release_url(version, name), dest)
            log("      ↓ %s（经 %s）" % (name, "加速链接" if used != release_url(version, name) else "直连"))
            blob = open(dest, "rb").read()
        else:
            full = os.path.join(REPO_ROOT, path)
            if not os.path.exists(full):
                die("找不到 %s（先跑 publish-release.py，或改用 --from-url）" % path)
            blob = open(full, "rb").read()

        existing = by_slug.get(slug)
        addon_id = existing.get("id") if existing else None

        # 没有列表（Key 缺 read 作用域）时，用详情端点按 slug 换 id：
        # GET /scforge/addons/{idOrSlug} 接受 slug 且匿名可读，是唯一能由 slug 拿 id 的接口。
        # ⚠️ 不能拿 slug 直接调 /addons/{id}/versions —— 那个路径参数声明为 uuid，不吃 slug。
        # ⚠️ 详情响应是 {"addon": {...}} 包了一层的，id 在 addon 里而不是顶层（实测确认）。
        if addon_id is None:
            try:
                detail = scforge("GET", "/scforge/addons/%s" % urllib.parse.quote(slug),
                                 token, allow_404=True, raise_http=True)
            except SCForgeHttpError as exc:
                die("按 slug %s 查资源失败：%d %s" % (slug, exc.code, exc.detail))
            addon = (detail or {}).get("addon") or {}
            addon_id = addon.get("id")
            if addon_id:
                by_slug[slug] = addon

        if addon_id:
            # 追加版本。GameVersion 缺省沿用资源的主游戏版本。
            fields = [
                ("Version", version),
                ("Channel", "release"),
                ("Changelog", recipe.get("Summary") or ("发布 %s" % version)),
            ]
            if recipe.get("GameVersion") or sc.get("GameVersion"):
                fields.append(("GameVersion", recipe.get("GameVersion") or sc["GameVersion"]))
            # ⚠️ 版本号唯一：这个版本已经传过了（比如本机 --only 传过、CI 又跑一遍，
            # 或者重跑同一个 tag 的 CI）时，API 会回 409。这不是错误，是"已是最新"，
            # 当成硬失败会让整个作业变红、还会连累后面还没传的插件，所以这里软跳过。
            try:
                scforge("POST", "/scforge/addons/%s/versions" % addon_id, token,
                        fields=fields, files=[("Package", name, blob)], raise_http=True)
                log("  ✓ %-16s 追加版本 %s（资源已存在）" % (slug, version))
            except SCForgeHttpError as exc:
                if exc.code == 409:
                    log("  = %-16s 版本 %s 已存在，跳过（%s）" % (slug, version, exc.detail.strip()[:60]))
                    skipped += 1
                else:
                    die("插件 %s 追加版本失败：%d %s" % (slug, exc.code, exc.detail))
        else:
            summary = recipe.get("Summary") or plugin
            # Description 是服务端必填项（缺了会 400 "请填写详细描述"）；
            # 优先用清单里显式写的，没有就用 Summary 兜底 —— 详情页的正文另有 Readme 撑。
            description = recipe.get("Description") or summary
            readme = recipe.get("Readme") or summary
            fields = [
                ("Kind", kind),
                ("Name", plugin),
                ("Slug", slug),
                ("Summary", summary),
                ("Description", description),
                ("Readme", readme),
                # ⚠️ 兜底值必须是 misc（合法的"其它"），不是 other —— 写 other 会被 400 拒绝。
                ("Category", recipe.get("Category") or sc.get("Category") or "misc"),
                ("GameVersion", recipe.get("GameVersion") or sc.get("GameVersion") or ""),
                ("Tags", recipe.get("Tags") or []),
                ("SourceUrl", sc.get("SourceUrl") or
                 "https://github.com/ClouderyStudio/sc-plugins"),
                # 协议以仓库根的 LICENSE（AGPL-3.0）为准；可用清单里的 License 覆盖。
                ("License", recipe.get("License") or sc.get("License") or "AGPL-3.0"),
                ("Version", version),
                ("Channel", "release"),
                ("Changelog", recipe.get("Summary") or ("首个版本 %s" % version)),
            ]
            scforge("POST", "/scforge/addons", token,
                    fields=fields, files=[("Package", name, blob)])
            log("  ✓ %-16s 新建资源并提交首个版本（进审核）" % slug)

    # 留一份快照，方便事后核对「这一版到底发了哪几个、来源是什么」
    snap = {
        "version": version,
        "source": "release-url(gh-proxy)" if args.from_url else "local-file",
        "kind": kind,
        "plugins": [{"plugin": p, "asset": n, "slug": r.get("Slug")}
                    for p, n, _path, r in entries],
    }
    with open(os.path.join(REPO_ROOT, SNAPSHOT_FILE), "w", encoding="utf-8") as fh:
        json.dump(snap, fh, ensure_ascii=False, indent=2)
    log("[2/2] 完成。快照写入 %s" % SNAPSHOT_FILE)
    if skipped:
        log("      其中 %d 个版本此前已传过，本次跳过（不是失败）。" % skipped)
    log("      资源与版本发布后都进审核，通过前对外不可见 —— 去后台点通过即可。")


if __name__ == "__main__":
    main()
