# -*- coding: utf-8 -*-
"""
发布开源库的新版本：编译 -> 提交 -> 打 tag -> 推送 -> 建 Release -> 上传 DLL -> 发到 SCForge。

为什么这个脚本必须在本机跑，而不是放进 GitHub Actions：
插件要对着 Survivalcraft 服务端核心 DLL（Survivalcraft.dll / Engine.dll / EntitySystem.dll /
Newtonsoft.Json.dll / LiteNetLib.dll）编译。那是商业游戏的文件，既不能传进公开仓库，
也没有 NuGet 包可以装 —— 云端 runner 拿到源码也编译不出 DLL。
所以：云端 CI 只做不需要核心 DLL 的校验（.buildtools/check-sources.py + ci.yml），
真正的构建与 Release 附件上传走本脚本。

最后一步（发到 SCForge）是转手给 .buildtools/publish-to-scforge.py 做的：
它读同一份 release-manifest.json，把 DLL 传到 api.cldery.com 的资源平台。
没配 SCFORGE_TOKEN 时会跳过并提示，不会让整个发布失败。

用法（在本仓库根目录）：
    python .buildtools/publish-release.py v1.0.0
    python .buildtools/publish-release.py v1.0.0 --skip-build     # 已编译过，只发布
    python .buildtools/publish-release.py v1.0.0 --dry-run        # 只打印要做什么
    python .buildtools/publish-release.py v1.0.0 --scforge-from-url   # SCForge 侧走 Release 加速链接
    python .buildtools/publish-release.py v1.0.0 --no-scforge     # 只发 GitHub，不发平台

前置：
    - 编译仍由 .buildtools/build-plugin.ps1 负责（本脚本只调用它，不自己调 csc）
    - GitHub token 从 ~/.git-credentials 里 host=github.com 那条读（scope 需含 repo）
    - SCForge 上传需要环境变量 SCFORGE_TOKEN=scf_xxx（publish 作用域）；不设则自动跳过
"""

import argparse
import json
import os
import subprocess
import sys
import urllib.error
import urllib.parse
import urllib.request

REPO = "ClouderyStudio/sc-plugins"
API = "https://api.github.com"
REPO_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))


def log(msg):
    sys.stdout.write(msg + "\n")
    sys.stdout.flush()


def die(msg):
    sys.stderr.write("ERROR: " + msg + "\n")
    sys.exit(1)


def read_github_token():
    """从 ~/.git-credentials 取 github.com 的 token。"""
    path = os.path.expanduser("~/.git-credentials")
    if not os.path.exists(path):
        die("找不到 " + path)
    blocks = open(path, encoding="utf-8", errors="replace").read().split("\n\n")
    for block in blocks:
        fields = {}
        for line in block.strip().splitlines():
            if "=" in line:
                k, v = line.split("=", 1)
                fields[k.strip()] = v.strip()
        if fields.get("host") == "github.com" and fields.get("password"):
            return fields["password"]
    die("~/.git-credentials 里没有 host=github.com 的条目")


def git(args, capture=True, check=True):
    cmd = ["git"] + args
    if capture:
        p = subprocess.run(cmd, cwd=REPO_ROOT, stdout=subprocess.PIPE,
                           stderr=subprocess.STDOUT)
    else:
        p = subprocess.run(cmd, cwd=REPO_ROOT)
    out = (p.stdout or b"").decode("utf-8", "replace").strip() if capture else ""
    if check and p.returncode != 0:
        die("git " + " ".join(args) + " 失败：\n" + out)
    return out


def api(method, path, token, payload=None, raw=False, allow_404=False):
    url = API + path
    data = None
    headers = {
        "Authorization": "Bearer " + token,
        "Accept": "application/vnd.github+json",
        "User-Agent": "sc-plugins-publisher",
    }
    if payload is not None:
        data = json.dumps(payload, ensure_ascii=False).encode("utf-8")
        headers["Content-Type"] = "application/json"
    req = urllib.request.Request(url, data=data, headers=headers, method=method)
    try:
        with urllib.request.urlopen(req) as resp:
            body = resp.read()
            if raw:
                return body
            return json.loads(body.decode("utf-8")) if body else None
    except urllib.error.HTTPError as e:
        if allow_404 and e.code == 404:
            # "这个 tag 还没有 Release" 是正常情况，不是错误，交给调用方去创建
            return None
        body = e.read().decode("utf-8", "replace")
        die("GitHub API %s %s 返回 %d：%s" % (method, path, e.code, body[:800]))


def asset_entries(manifest):
    """返回 [(本地路径, 发布用的文件名, 插件目录名)]。
    兼容两种写法：字符串（用 basename 当文件名）或 {File, Name, Plugin}。"""
    out = []
    for item in manifest["Assets"]:
        if isinstance(item, str):
            out.append((item, os.path.basename(item), item.split('/')[0]))
        else:
            out.append((item["File"], item.get("Name") or os.path.basename(item["File"]),
                        item.get("Plugin") or item["File"].split('/')[0]))
    return out


def upload_asset(token, release_id, file_path, name):
    """上传一个 Release 附件。

    ⚠️ name 必须是 ASCII：GitHub 不支持中文附件名 —— 中文名会被 sanitize 成
    `default.dll`，于是第二个附件开始全部报 already_exists。文件名不影响插件加载
    （核心按目录扫描 Plugins/*.dll），所以统一用英文名，映射表写进 Release 说明。
    """
    url = ("https://uploads.github.com/repos/%s/releases/%d/assets?name=%s"
           % (REPO, release_id, urllib.parse.quote(name)))
    with open(file_path, "rb") as fh:
        blob = fh.read()
    req = urllib.request.Request(url, data=blob, method="POST", headers={
        "Authorization": "Bearer " + token,
        "Accept": "application/vnd.github+json",
        "Content-Type": "application/octet-stream",
        "User-Agent": "sc-plugins-publisher",
    })
    try:
        with urllib.request.urlopen(req) as resp:
            return json.loads(resp.read().decode("utf-8"))
    except urllib.error.HTTPError as e:
        body = e.read().decode("utf-8", "replace")
        die("上传 %s 失败（HTTP %d）：%s" % (name, e.code, body[:500]))


def build_notes_since_last_tag():
    last = git(["describe", "--tags", "--abbrev=0"], check=False)
    rng = (last + "..HEAD") if last else "HEAD"
    notes = git(["log", rng, "--pretty=format:- %s"], check=False)
    return notes if notes else "- （首个版本）"


def build_body(notes, entries):
    """Release 说明 = 变更记录 + 附件名与插件的对应表。
    附件名是英文的（GitHub 不支持中文附件名），必须给个对照，否则下载的人分不清谁是谁。"""
    lines = [notes, "", "## 附件与插件对应", "",
             "| 下载的文件名 | 对应插件 |", "|---|---|"]
    for path, name, plugin in entries:
        lines.append("| `%s` | %s |" % (name, plugin))
    lines += [
        "",
        "附件名用英文，是因为 GitHub 的 Release 附件不支持中文名 —— 中文名会被改成 "
        "`default.dll`，多个附件会互相撞名。",
        "",
        "**文件名不影响加载**：把 DLL 直接放进服务端 `Plugins/` 目录重启即可，不必改回中文名。",
        "每个插件的配置目录由代码写死（`Plugins/<插件名>/`），与 DLL 文件名无关。",
    ]
    return "\n".join(lines)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("version", help="版本号，形如 v1.0.0")
    ap.add_argument("--skip-build", action="store_true", help="跳过编译")
    ap.add_argument("--dry-run", action="store_true", help="只打印计划，不实际操作")
    ap.add_argument("--no-push", action="store_true", help="不推 git（只建 Release）")
    ap.add_argument("--no-scforge", action="store_true", help="跳过 SCForge 上传（只发 GitHub）")
    ap.add_argument("--scforge-from-url", action="store_true",
                    help="SCForge 侧从 Release 加速链接取文件（默认直接用本地 DLL）")
    args = ap.parse_args()

    version = args.version
    if not version.startswith("v"):
        version = "v" + version

    manifest = json.load(open(os.path.join(REPO_ROOT, ".buildtools", "release-manifest.json"),
                              encoding="utf-8"))
    entries = asset_entries(manifest)

    log("=== 发布 %s ===" % version)

    # ---- 1. 编译 ----
    if args.skip_build:
        log("[1/5] 跳过编译（--skip-build）")
    elif args.dry_run:
        log("[1/5] 会执行：.buildtools/build-plugin.ps1")
    else:
        log("[1/5] 编译插件...")
        ps = ["powershell", "-ExecutionPolicy", "Bypass", "-File",
              os.path.join(REPO_ROOT, ".buildtools", "build-plugin.ps1")]
        try:
            p = subprocess.run(ps, cwd=REPO_ROOT)
            rc = p.returncode
        except OSError as e:
            rc = -1
            log("      无法启动 PowerShell：" + str(e))
        if rc != 0:
            die("编译未执行成功（退出码 %d）。\n"
                "  常见原因：某些受限环境不允许从子进程启动 PowerShell。\n"
                "  这时请手动跑一次 .buildtools\\build-plugin.ps1（或直接在 IDE 里执行它），\n"
                "  确认 DLL 已生成后，本脚本加 --skip-build 重跑即可继续发布。" % rc)

    # ---- 2. 校验产物都在 ----
    missing = [e[0] for e in entries if not os.path.exists(os.path.join(REPO_ROOT, e[0]))]
    if missing:
        die("以下 DLL 不存在（先编译，或检查 release-manifest.json）：\n  "
            + "\n  ".join(missing))
    log("[2/5] %d 个 DLL 均已就绪" % len(entries))

    # ---- 3. git 提交 ----
    status = git(["status", "--porcelain"], check=False)
    if args.dry_run:
        log("[3/5] 工作区变更：\n" + (status or "  （无）"))
    else:
        if status:
            git(["add", "-A"], capture=True)
            msg_path = os.path.join(REPO_ROOT, ".buildtools", "_release-commit-msg.txt")
            open(msg_path, "w", encoding="utf-8").write(
                "发布 %s\n\n由 .buildtools/publish-release.py 自动提交。\n" % version)
            git(["commit", "-F", msg_path], check=False)
            os.remove(msg_path)
            log("[3/5] 已提交源码变更")
        else:
            log("[3/5] 无源码变更，跳过提交")

    # ---- 4. tag + push ----
    existing_tags = git(["tag", "--list"], check=False).split()
    if version in existing_tags:
        log("[4/5] tag %s 已存在，复用" % version)
    else:
        log("[4/5] 打 tag %s" % version)
        if not args.dry_run:
            git(["tag", "-a", version, "-m", "Release " + version], check=False)

    if args.no_push or args.dry_run:
        log("[4/5] 跳过推送")
    else:
        git(["push", "origin", "main"], check=False)
        git(["push", "origin", version], check=False)
        log("[4/5] 已推送 main 与 tag %s" % version)

    if args.dry_run:
        log("\n[dry-run] 计划上传的附件：")
        for path, name, plugin in entries:
            log("   %-22s <- %s  (%d bytes)"
                % (name, path, os.path.getsize(os.path.join(REPO_ROOT, path))))
        return

    # ---- 5. Release ----
    token = read_github_token()
    log("[5/5] 创建 / 获取 Release %s ..." % version)

    existing = api("GET", "/repos/%s/releases/tags/%s" % (REPO, version), token,
                   allow_404=True)

    if existing and existing.get("id"):
        release = existing
        log("      已存在，复用（id=%d）" % release["id"])
    else:
        body = build_body(build_notes_since_last_tag(), entries)
        release = api("POST", "/repos/%s/releases" % REPO, token, {
            "tag_name": version,
            "name": version,
            "body": body,
            "draft": False,
            "prerelease": False,
        })
        log("      已创建（id=%d）" % release["id"])

    # 同名附件先删（重复上传会 422 already_exists）
    had = {}
    for a in release.get("assets", []):
        had[a["name"]] = a["id"]
    for path, name, plugin in entries:
        if name in had:
            api("DELETE", "/repos/%s/releases/assets/%d" % (REPO, had[name]), token)
            log("      删除旧附件 " + name)

    for path, name, plugin in entries:
        info = upload_asset(token, release["id"],
                            os.path.join(REPO_ROOT, path), name)
        log("      上传 %-20s (%d bytes)" % (name, info.get("size", 0)))

    log("\n完成：https://github.com/%s/releases/tag/%s" % (REPO, version))

    # ---- 6. 发到 SCForge ----
    # 转手给子脚本：它读同一份清单，把每个 DLL 发到 api.cldery.com 的资源平台。
    # 失败不算发布失败 —— GitHub 那一半已经成了，平台侧可以单独重跑。
    if args.no_scforge:
        log("[6/6] 跳过 SCForge 上传（--no-scforge）")
        return

    if not os.environ.get("SCFORGE_TOKEN"):
        log("[6/6] 跳过 SCForge：未设 SCFORGE_TOKEN。")
        log("      要发平台的话，签发 Key（https://scforge.cldery.com/api-keys，需 publish 作用域）后：")
        log("        set SCFORGE_TOKEN=scf_xxx            （PowerShell）")
        log("        python .buildtools/publish-to-scforge.py %s" % version)
        return

    log("[6/6] 上传到 SCForge 资源平台...")
    child = [sys.executable, os.path.join(REPO_ROOT, ".buildtools", "publish-to-scforge.py"),
             version]
    if args.scforge_from_url:
        child.append("--from-url")
    p = subprocess.run(child, cwd=REPO_ROOT)
    if p.returncode != 0:
        log("      ✗ SCForge 上传未成功（退出码 %d）。GitHub Release 已发布，"
            "平台侧可单独重跑上面的命令。" % p.returncode)
    else:
        log("      ✓ SCForge 上传完成（资源与版本进审核，通过后对外可见）")


if __name__ == "__main__":
    main()
