"""校验 C# 逐字串（@"..."）是否被未转义的 ASCII 双引号提前截断。

背景：网页前端资源把整段 HTML/CSS/JS 塞进逐字串，任何一个写成单个 " 的
ASCII 引号都会让字符串在那里结束，后面的 JS 全被当成 C# 代码解析，
报出几百个位置完全对不上的语法错误（错误行号 ≠ 真正的错误行）。

用法：python check_verbatim.py <cs文件>
"""
import io
import sys

path = sys.argv[1]
src = io.open(path, encoding='utf-8').read()
lines = src.split('\n')

# 只找“整行以 sb.Append(@" 结尾”的起始标记，即真正开启逐字串的地方
starts = [i for i, l in enumerate(lines) if l.rstrip().endswith('sb.Append(@"')]

if not starts:
    print('no verbatim string found')
    sys.exit(0)

DQ = chr(34)
bad = False
for start in starts:
    pos = sum(len(x) + 1 for x in lines[:start]) + len(lines[start])
    i = pos
    n = len(src)
    end = None
    while i < n:
        c = src[i]
        if c == DQ:
            if i + 1 < n and src[i + 1] == DQ:
                i += 2
                continue
            end = i
            break
        i += 1
    if end is None:
        print('start line %d -> UNTERMINATED' % (start + 1))
        bad = True
        continue
    line = src.count('\n', 0, end) + 1
    tail = src[end:end + 14].replace('\n', '\\n')
    # 合法收尾：");  或  " + 变量
    good = tail.startswith(DQ + ');') or tail.startswith(DQ + ' +')
    flag = 'OK ' if good else 'BAD'
    if not good:
        bad = True
    print('%s start %-5d -> end line %-5d %r' % (flag, start + 1, line, tail))

print('ALL OK' if not bad else 'STILL BROKEN')
sys.exit(1 if bad else 0)