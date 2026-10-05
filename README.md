# sc-plugins

Survivalcraft 服务端插件集合（SCNET 2.4.0 / .NET 10）。

每个插件一个子文件夹，**互不依赖**：可以只挑需要的那个编译、部署，不用整套一起装。

仓库里只有源码与说明，**不含编译好的 DLL** —— 请自行编译（见下方）。

## 插件一览

| 目录 | 作用 |
|---|---|
| [`网页控制台插件/`](网页控制台插件/) | 服务端内嵌 HTTP 面板：状态 / 性能 / 玩家管理 / 背包 / 终端 / 实时日志 |
| [`和平区域插件/`](和平区域插件/) | 指定区域内禁止玩家互伤，攻击者受轻微反噬 |
| [`清除低高空方块插件/`](清除低高空方块插件/) | 清理低于/高于阈值的方块，阈值可配 |
| [`火柴记录插件/`](火柴记录插件/) | 记录点火与蔓延，便于追查纵火 |
| [`个人进服密码插件/`](个人进服密码插件/) | 每个玩家单独的进服密码 |
| [`主城防破坏插件/`](主城防破坏插件/) | 主城区域防破坏 |
| [`清理插件/`](清理插件/) | 定时清理生物与掉落物，按比例保留 |
| [`聊天记录插件/`](聊天记录插件/) | 聊天归档，按天分文件 |
| [`无动物插件/`](无动物插件/) | 禁用动物生成 |

## 编译

插件跑在**服务端进程**里，所以要对着**服务端**核心程序集编译（客户端 mod 反之）。

需要的引用：

- 服务端核心（从你的服务端目录拷）：`Survivalcraft.dll`、`Engine.dll`、`EntitySystem.dll`、`Newtonsoft.Json.dll`、`LiteNetLib.dll`
  （`LiteNetLib.dll` 必须带上：Survivalcraft 的公开 API 暴露了它的类型，例如 `IBanEventHandle.IsBanIp` 收的是 `LiteNetLib.ConnectionRequest`）
- .NET 10 参考程序集（`Microsoft.NETCore.App.Ref` 的 `ref/net10.0`）
- Roslyn 编译器 `csc.dll`

命令行形态（`-target:library`，`net10.0`）：

```bat
dotnet exec "<path>\csc.dll" ^
  /nologo /target:library /unsafe /optimize+ /langversion:latest ^
  /out:网页控制台插件.dll ^
  /r:"<core>\Survivalcraft.dll" /r:"<core>\Engine.dll" ^
  /r:"<core>\EntitySystem.dll" /r:"<core>\Newtonsoft.Json.dll" /r:"<core>\LiteNetLib.dll" ^
  /r:"<refRoot>\System.Runtime.dll" ... ^
  网页控制台插件\源码\*.cs
```

单个插件一般只有一个 `.cs`；网页控制台插件是目录，把 `源码\*.cs` 全展开即可。

## 部署

编译出的 DLL 丢进服务端 `Plugins/` 目录，重启服务端。

每个插件的配置都写在自己名下：`Plugins/<插件名>/…`，互不影响。

## 发版

一条命令（本机）：

```bat
python .buildtools/publish-release.py v1.0.0
```

它会依次：编译 → 提交源码 → 打 tag → 推送 → 创建 GitHub Release → 把 9 个 DLL 传成附件。

常用开关：

| 开关 | 用途 |
|---|---|
| `--skip-build` | 已经编译过了，只做发布 |
| `--dry-run` | 只打印要做什么，不动仓库也不上传 |
| `--no-push` | 不推 git，只建 Release 并传附件 |

要发布哪些 DLL 由 `.buildtools/release-manifest.json` 决定（刻意不含整合包与授权壳产物）。

> ⚠️ **为什么不在 GitHub Actions 里编译**：插件需要对着 Survivalcraft 服务端核心 DLL
> （`Survivalcraft.dll` 等）编译，那是商业游戏的文件，既不能传进公开仓库也没有 NuGet 包可装，
> 云端 runner 拿到源码也编不出 DLL。所以云端 CI（`.github/workflows/ci.yml`）只跑**不需要核心 DLL** 的检查：
> 逐字串引号配对、白名单是否覆盖每个待发布插件、有没有混入敏感文件、每个插件是否带 README
> （见 `.buildtools/check-sources.py`）。构建与附件上传一律走本机的 `publish-release.py`。

## 注意事项

- **命令白名单**：网页控制台插件可以在网页上执行服务端命令，而终端路径会跳过 AuthLevel 检查，所以务必配好 `Password` 与 `AllowedCommandPrefixes`，并尽量只绑 `127.0.0.1`。详见该插件的 README。
- **互斥**：部分插件抢同一个事件钩子（核心的事件管理器是"后注册者胜"），不要同时装功能重叠的插件。各插件 README 里写了互斥关系。
- 这些插件最初是从一套更老的 `.NET Framework` 插件迁移过来的，迁移时修掉的问题都记在各自 README 的"与旧版的差异"一节里。

## 许可

MIT。详见 [LICENSE](LICENSE)。
