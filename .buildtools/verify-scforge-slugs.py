# -*- coding: utf-8 -*-
"""
对账：把 release-manifest.json 里的 Scforge 配方，与 SCForge 平台上已有的资源并排比对。

存在的理由（踩过坑）：
    配 Scforge.Items 时如果**凭空造 slug**，脚本会探测不到、然后**新建一份资源**，
    于是平台上出现两个同名插件（一个旧 slug、一个新 slug）。
    本脚本就是给这一步兜底的：改完配方、发版之前跑一次，
    凡是「清单里的 slug 在平台上找不到、但平台上有另一个资源疑似同一个插件」的，
    都会列出来提醒你先确认，而不是等发完版才发现多了一份。

用法（在仓库根目录）：
    python .buildtools/verify-scforge-slugs.py

**不需要任何 Key** —— 它读的是公开目录 `GET /scforge/addons`（匿名可读）。
想额外区分"哪些是我发布的"，可以再设 SCFORGE_TOKEN（能读列表的 Key）。

输出三种标记：
    [有]   清单 slug 与平台资源对上了 —— 发版会走"追加版本"，符合预期
    [新]   清单 slug 平台上没有 —— 发版会**新建资源**。若它其实已存在（只是 slug 不同），
           就是上面说的重复 bug，脚本会把疑似对象指出来
    [缺]   平台上有资源，但清单里没有任何 slug 指向它 —— 可能是漏配，或是个旧资源
"""

import json
import os
import sys
import urllib.error
import urllib.parse
import urllib.request

API_BASE = "https://api.cldery.com"
REPO_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))


def load_manifest():
    path = os.path.join(REPO_ROOT, '.buildtools', 'release-manifest.json')
    with open(path, encoding='utf-8') as fh:
        return json.load(fh)


def scforge_api(path, token=None):
    headers = {'User-Agent': 'sc-plugins-verify'}
    if token:
        headers['Authorization'] = 'Bearer ' + token
    req = urllib.request.Request(API_BASE + path, headers=headers)
    try:
        with urllib.request.urlopen(req) as resp:
            body = resp.read().decode('utf-8')
            return resp.status, (json.loads(body) if body else None)
    except urllib.error.HTTPError as exc:
        return exc.code, None
    except Exception:  # noqa: BLE001 - 网络异常统一当"查不到"处理
        return 0, None


def fetch_catalog():
    """拉公开目录（匿名可读），翻页拿全量。

    这是本脚本"平台现状"的主来源 —— 不依赖 Key 作用域，谁都能跑。
    """
    out = []
    page = 1
    while True:
        code, body = scforge_api('/scforge/addons?page=%d&pageSize=100' % page)
        if code != 200 or not body:
            break
        items = body.get('items') or []
        out.extend(items)
        total_pages = body.get('totalPages') or 1
        if page >= total_pages or not items:
            break
        page += 1
    return out


def fetch_mine(token):
    """可选：拉"我发布的"列表，用于区分归属。没有 read 作用域时返回 None。"""
    if not token:
        return None
    code, body = scforge_api('/scforge/addons/mine', token)
    if code == 200 and body is not None:
        return body.get('items') or body.get('data') or []
    return None


def normalize(name):
    """把名字归一化，用于模糊匹配「疑似同一个插件」。
    去掉空格与常见后缀词，让 '个人进服密码插件' 与 '个人进服密码' 能撞上。"""
    if not name:
        return ''
    s = name.strip().lower()
    for suffix in ('插件', '模组', 'plugin', 'mod'):
        if s.endswith(suffix) and len(s) > len(suffix):
            s = s[: -len(suffix)]
    return s.replace(' ', '').replace('-', '').replace('_', '')


def main():
    manifest = load_manifest()
    sc = (manifest.get('Scforge') or {})
    items = (sc.get('Items') or {})
    if not items:
        sys.exit('清单里没有 Scforge.Items，无需对账。')

    token = os.environ.get('SCFORGE_TOKEN')
    print('=== SCForge slug 对账 ===\n')

    catalog = fetch_catalog()
    if not catalog:
        sys.exit('读不到公开目录（GET /scforge/addons）——检查网络或接口是否变更。')
    by_slug = {a.get('slug'): a for a in catalog}
    print('平台上现有 %d 个资源。\n' % len(catalog))

    mine = fetch_mine(token)
    if token and mine is None:
        print('（SCFORGE_TOKEN 没有「读取」作用域，跳过"归属"判断；'
              '对账本身不受影响）\n')

    # ---- 逐条比对清单里的 slug ----
    rows = []
    for plugin, recipe in items.items():
        slug = recipe.get('Slug')
        a = by_slug.get(slug)
        rows.append({'plugin': plugin, 'slug': slug, 'exists': bool(a),
                     'category': (a or {}).get('category'),
                     'latest': (a or {}).get('latestVersion'),
                     'onname': (a or {}).get('name')})

    claimed = {r['slug'] for r in rows if r['slug'] and r['exists']}
    new_ones = [r for r in rows if not r['exists']]

    print('%-14s %-18s %-6s %-12s %-10s %s'
          % ('插件', 'Slug', '状态', '分类', '最新版本', '说明'))
    print('-' * 100)
    for r in rows:
        if r['exists']:
            print('%-14s %-18s %-6s %-12s %-10s %s'
                  % (r['plugin'], r['slug'], '[有]', r['category'] or '-',
                     r['latest'] or '-', '发版会追加版本'))
        else:
            print('%-14s %-18s %-6s %-12s %-10s %s'
                  % (r['plugin'], r['slug'], '[新]', '-', '-', '发版会新建资源'))

    # ---- 关键告警：新 slug 疑似与已有资源是同一个插件 ----
    # 用名字归一化做模糊匹配：'个人进服密码插件' 与 '个人进服密码' 会撞上。
    unclaimed = [a for a in catalog if a.get('slug') not in claimed]
    by_norm = {}
    for a in unclaimed:
        by_norm.setdefault(normalize(a.get('name')), []).append(a)

    hits = []
    for r in new_ones:
        for cand in by_norm.get(normalize(r['plugin']), []):
            hits.append((r, cand))

    if hits:
        print('\n' + '!' * 78)
        print('!! 疑似重复：以下 slug 平台上没有，但存在「看起来是同一个插件」的其它资源。')
        print('!! 直接发版会**新建一份**，平台上就出现两个同名插件。')
        print('!! 若确属同一插件，请把配方里的 Slug 改成平台已有的那个。')
        print('!' * 78)
        for r, cand in hits:
            print('\n  %s' % r['plugin'])
            print('    配方里的 slug : %s      （平台上不存在 → 会新建）' % r['slug'])
            print('    平台已有      : %s  「%s」  最新 %s'
                  % (cand.get('slug'), cand.get('name'), cand.get('latestVersion')))
            print('    → 建议改成   : "Slug": "%s"' % cand.get('slug'))

    # ---- 平台上没被任何配方认领的资源 ----
    named = {cand.get('slug') for _r, cand in hits}
    orphan = [a for a in unclaimed if a.get('slug') not in named
              and a.get('slug') not in {r['slug'] for r in new_ones}]
    if orphan:
        print('\n平台上还有 %d 个资源未被清单认领（可能漏配，或是不再发布的旧资源）：'
              % len(orphan))
        for a in sorted(orphan, key=lambda x: x.get('slug') or ''):
            print('    %-18s 「%s」  最新 %s'
                  % (a.get('slug'), a.get('name'), a.get('latestVersion')))

    if hits:
        print('\n⚠️  有 %d 处疑似重复，发版前请先确认配方里的 Slug。' % len(hits))
    else:
        print('\n没发现重复隐患。')

    print('对账完成。')


if __name__ == '__main__':
    main()
