# -*- coding: utf-8 -*-
"""
仓库自检。云端 CI 跑的就是这个（.github/workflows/ci.yml）。

只做**不需要 Survivalcraft 核心 DLL** 的检查 —— 因为核心 DLL 不能进公开仓库，
云端编译不了插件，能做的就是把"编译之前就该发现的问题"提前拦下来：

1. 逐字串引号配对：@"..." 里少写一个引号，会让后面几百行 HTML/JSON 被当成 C# 代码，
   一次报几百个错，而且报错位置**不在真正的错误行**，极难定位。这是踩过的大坑，
   所以放在 CI 第一关。
2. 白名单漏放行：.gitignore 是 /* 全忽略 + !/插件目录/ 放行。新增插件忘了加白名单，
   就会"本地有文件、仓库里没有、发布时静默少一个插件"。
3. 敏感文件混入：令牌、服务端目录、取证报告这类绝不能公开的东西被 track 进来。

退出码非 0 表示检查失败。
"""

import io
import json
import os
import re
import subprocess
import sys

REPO_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))

# 绝不能出现在版本库里的路径模式（大小写不敏感）
SENSITIVE = [
    r"scforge\.env",
    r"(^|/)server(/|$)",
    r"(^|/)server-test(/|$)",
    r"(^|/)server-test2(/|$)",
    r"\.env$",
    r"_env$",
    r"sshkey",
    r"id_ed25519",
    r"id_rsa",
    r"取证报告",
    r"(^|/)外挂(/|$)",
    r"(^|/)旧版备份(/|$)",
    r"AntiCheatEvidence",
    r"AntiCheatReport",
    r"publish-results",
    r"publish-plugins",
]


def strip_comments(text):
    """去掉 // 行注释与 /* */ 块注释，避免注释里的引号误报。
    字符串内的 // 会被误判，但这种写法在本仓库不存在，代价可接受。"""
    out = []
    i, n = 0, len(text)
    while i < n:
        c = text[i]
        if c == '/' and i + 1 < n and text[i + 1] == '/':
            j = text.find('\n', i)
            if j < 0:
                break
            out.append('\n')
            i = j + 1
            continue
        if c == '/' and i + 1 < n and text[i + 1] == '*':
            j = text.find('*/', i + 2)
            i = n if j < 0 else j + 2
            continue
        out.append(c)
        i += 1
    return ''.join(out)


def check_verbatim_quotes(rel_path):
    """返回 (行号, 说明) 列表。逐字串未闭合 = 致命。"""
    text, ok = read_text(os.path.join(REPO_ROOT, rel_path))
    if not ok:
        # 非 UTF-8（多半是 GBK 旧文件）。不算失败，但提醒一句——
        # 真要入库的话得先转码，否则 GitHub 上会显示成乱码。
        return [(1, '文件不是 UTF-8 编码，跳过引号检查（入库前需转码）')]
    text = strip_comments(text)
    problems = []
    i, n = 0, len(text)
    line = 1
    while i < n:
        c = text[i]
        if c == '\n':
            line += 1
            i += 1
            continue
        if c == '@' and i + 1 < n and text[i + 1] == '"':
            start_line = line
            i += 2
            closed = False
            while i < n:
                if text[i] == '"':
                    # 逐字串里 "" 表示一个字面引号
                    if i + 1 < n and text[i + 1] == '"':
                        i += 2
                        continue
                    i += 1
                    closed = True
                    # 少写一个引号的情况：字符串会"提前闭合"，语法上仍是配对的，
                    # 编译器不会报"未闭合"，而是把后面的 HTML/JSON 当 C# 代码，
                    # 于是一次报几百个错且位置全偏 —— 这才是当初最难查的地方。
                    # 判据：闭合引号后紧跟字母/数字/下划线，正常 C# 里不可能出现
                    # （正常只能是 ; , ) ] } + . 空白 等）。
                    if i < n and (text[i].isalnum() or text[i] == '_'):
                        problems.append((line, '逐字字符串在第 %d 行闭合后紧跟 "%s"，'
                                              '几乎肯定是里面少写了一个引号（应为 ""）'
                                        % (line, text[i])))
                    break
                if text[i] == '\n':
                    line += 1
                i += 1
            if not closed:
                problems.append((start_line, '逐字字符串 @" 从第 %d 行开始，到文件结束都没有闭合 '
                                             '（多半是里面某个引号少写了一个，应为 ""）' % start_line))
            continue
        i += 1
    return problems


def whitelisted_dirs():
    """.gitignore 里 !/xxx/ 放行的顶层目录 == 这个仓库真正要发布的范围。
    只扫这些目录，避免碰到工作区里同级的旧版备份 / 外挂源码（它们还是 GBK 编码）。"""
    path = os.path.join(REPO_ROOT, '.gitignore')
    dirs = []
    if os.path.exists(path):
        for line in io.open(path, encoding='utf-8'):
            line = line.strip()
            if line.startswith('!/') and line.endswith('/'):
                dirs.append(line[2:-1])
    return dirs


def cs_files():
    result = []
    for top in whitelisted_dirs():
        base = os.path.join(REPO_ROOT, top)
        if not os.path.isdir(base):
            continue
        for root, dirs, files in os.walk(base):
            dirs[:] = [d for d in dirs if not d.startswith('.')]
            for f in files:
                if f.endswith('.cs'):
                    rel = os.path.relpath(os.path.join(root, f), REPO_ROOT)
                    result.append(rel.replace('\\', '/'))
    return sorted(result)


def read_text(path):
    """返回 (文本, 是否解码成功)。旧文件有 GBK 编码的，解不开就跳过而不是让整个检查崩掉。"""
    try:
        return io.open(path, encoding='utf-8').read(), True
    except UnicodeDecodeError:
        return None, False


def manifest_paths():
    """跑 manifest 里的本地路径。兼容字符串与 {File, Name, Plugin} 两种写法。"""
    path = os.path.join(REPO_ROOT, '.buildtools', 'release-manifest.json')
    if not os.path.exists(path):
        return []
    assets = json.load(io.open(path, encoding='utf-8'))['Assets']
    return [a if isinstance(a, str) else a['File'] for a in assets]


def manifest_plugins():
    """返回 [(插件名, Scforge 配方 or None)]，检查 Scforge 段与 Assets 是否对得上。

    为什么值得单独查：Scforge 段是 publish-to-scforge.py 的输入，用插件**中文名**做键。
    新增插件时只往 Assets 加一行、忘了补 Scforge 配方，脚本会静默跳过那个插件 ——
    表现是「CI 全绿但平台上少了一个资源」，最难查。这里提前拦下。
    """
    path = os.path.join(REPO_ROOT, '.buildtools', 'release-manifest.json')
    if not os.path.exists(path):
        return []
    data = json.load(io.open(path, encoding='utf-8'))
    recipes = (data.get('Scforge') or {}).get('Items') or {}
    out = []
    for a in data.get('Assets', []):
        if isinstance(a, str):
            continue
        plugin = a.get('Plugin') or a['File'].split('/')[0]
        out.append((plugin, recipes.get(plugin)))
    return out, recipes


def main():
    failures = 0

    # ---- 1. 逐字串引号配对 ----
    print('[1/5] 逐字字符串引号配对')
    skipped = 0
    for rel in cs_files():
        for lineno, msg in check_verbatim_quotes(rel):
            if '不是 UTF-8' in msg:
                print('  WARN %s  %s' % (rel, msg))
                skipped += 1
            else:
                print('  FAIL %s:%d  %s' % (rel, lineno, msg))
                failures += 1
    print('  检查了 %d 个 .cs 文件%s' % (len(cs_files()),
                                    ('，%d 个因编码跳过' % skipped) if skipped else ''))

    # ---- 2. 白名单是否放行了 release-manifest 里的每个插件 ----
    print('[2/5] .gitignore 白名单是否覆盖待发布插件')
    manifest_path = os.path.join(REPO_ROOT, '.buildtools', 'release-manifest.json')
    gitignore_path = os.path.join(REPO_ROOT, '.gitignore')
    if os.path.exists(manifest_path) and os.path.exists(gitignore_path):
        ignore = io.open(gitignore_path, encoding='utf-8').read()
        for a in manifest_paths():
            top = a.split('/')[0]
            if ('!/' + top + '/') not in ignore:
                print('  FAIL 插件 %s 没有在 .gitignore 里白名单放行（缺 !/%s/）' % (top, top))
                failures += 1
        print('  %d 个待发布插件目录' % len({a.split('/')[0] for a in manifest_paths()}))

    # ---- 3. 敏感文件 ----
    print('[3/5] 版本库内是否混入敏感文件')
    try:
        tracked = subprocess.run(['git', 'ls-files'], cwd=REPO_ROOT,
                                 stdout=subprocess.PIPE, check=True)
        files = tracked.stdout.decode('utf-8', 'replace').split()
    except Exception as e:
        print('  跳过（git 不可用：%s）' % e)
        files = []
    for f in files:
        for pat in SENSITIVE:
            if re.search(pat, f, re.IGNORECASE):
                print('  FAIL 敏感文件被版本库跟踪：%s（匹配 %s）' % (f, pat))
                failures += 1
                break
    print('  %d 个被跟踪文件' % len(files))

    # ---- 4. 对外开源每个插件都该有 README ----
    print('[4/5] 待发布插件是否都带 README')
    if os.path.exists(manifest_path):
        for top in sorted({a.split('/')[0] for a in manifest_paths()}):
            if not os.path.exists(os.path.join(REPO_ROOT, top, 'README.md')):
                print('  FAIL 插件 %s 缺少 README.md（对外开源至少要有功能与配置说明）' % top)
                failures += 1

    # ---- 5. Scforge 配方与 Assets 是否一一对应 ----
    print('[5/5] Scforge 发布配方完整性')
    plugins, recipes = manifest_plugins()
    if not recipes:
        print('  清单里没有 Scforge 段，跳过（只发 GitHub Release）')
    else:
        for plugin, recipe in plugins:
            if recipe is None:
                print('  FAIL 插件 %s 在 Assets 里但 Scforge.Items 里没有配方'
                      '（会被静默跳过，平台上少一个资源）' % plugin)
                failures += 1
                continue
            for key in ('Slug', 'Summary'):
                if not recipe.get(key):
                    print('  FAIL 插件 %s 的 Scforge 配方缺少 %s' % (plugin, key))
                    failures += 1
        slugs = [r.get('Slug') for _p, r in plugins if r and r.get('Slug')]
        for s in sorted(set(slugs)):
            if slugs.count(s) > 1:
                print('  FAIL slug 重复：%s（slug 是资源的固定地址，不能撞）' % s)
                failures += 1
        print('  %d 个插件有配方，%d 个 slug 唯一' % (len(plugins), len(set(slugs))))

    if failures:
        print('\n共 %d 项检查失败' % failures)
        sys.exit(1)
    print('\n全部检查通过')


if __name__ == '__main__':
    main()
