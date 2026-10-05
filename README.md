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

它会依次：编译 → 提交源码 → 打 tag → 推送 → 创建 GitHub Release → 把 9 个 DLL 传成附件 → 发到 SCForge 平台。

常用开关：

| 开关 | 用途 |
|---|---|
| `--only <插件>` | **只发布指定插件**（详见下方），可重复或用逗号分隔 |
| `--skip-build` | 已经编译过了，只做发布 |
| `--dry-run` | 只打印要做什么，不动仓库也不上传 |
| `--no-push` | 不推 git，只建 Release 并传附件 |
| `--no-scforge` | 只发 GitHub，不上传 SCForge 平台 |
| `--scforge-from-url` | SCForge 侧从 Release 加速链接取文件（默认直接用本地 DLL） |

#### 只发布改动的插件（`--only`）

tag 是**仓库级**的，但插件各自独立演进 —— 不传 `--only` 时会把全部 9 个插件重新编一遍、
重新发一遍，没改动的插件在平台上的更新时间也会被无谓刷新。所以只想发一两个时：

```bat
python .buildtools/publish-release.py v1.0.4 --only 网页控制台插件
python .buildtools/publish-release.py v1.0.4 --only web-panel,daily-log
```

`--only` 接受三种写法，任选其一：

| 写法 | 例子 |
|---|---|
| 插件目录名 | `网页控制台插件` |
| 附件英文名（带不带 `.dll` 都行） | `web-panel` / `web-panel.dll` |
| Scforge slug | `peace-zone` |

名字写错会**直接报错并列出全部可用值**（而不是静默发个空的）。

> ⚠️ 打的是同一个仓库级 tag，所以"这次只发了哪个插件"只能看 Release 说明与附件清单 ——
> 附件表里列了谁，这次就发了谁。

要发布哪些 DLL 由 `.buildtools/release-manifest.json` 决定（刻意不含整合包与授权壳产物）。

> ⚠️ **为什么不在 GitHub Actions 里编译**：插件需要对着 Survivalcraft 服务端核心 DLL
> （`Survivalcraft.dll` 等）编译，那是商业游戏的文件，既不能传进公开仓库也没有 NuGet 包可装，
> 云端 runner 拿到源码也编不出 DLL。所以云端 CI（`.github/workflows/ci.yml`）只跑**不需要核心 DLL** 的检查：
> 逐字串引号配对、白名单是否覆盖每个待发布插件、有没有混入敏感文件、每个插件是否带 README、
> Scforge 配方是否完整（见 `.buildtools/check-sources.py`）。

### 发布到 SCForge 平台

`publish-release.py` 的最后一步会把 DLL 上传到 [SCForge](https://scforge.cldery.com)
（生存战争插件 / 模组资源平台）。这一步也可以单独跑：

```bat
set SCFORGE_TOKEN=scf_xxxx
python .buildtools/publish-to-scforge.py v1.0.0
```

需要一把 `publish` 作用域的 API Key（<https://scforge.cldery.com/api-keys> 自助签发）。
没设 `SCFORGE_TOKEN` 时这一步会**跳过并提示**，不会让整个发布失败。

每个插件在平台上的标题、slug、简介、标签写在 `release-manifest.json` 的 `Scforge.Items` 段里，
键是插件的中文名。资源已存在时脚本自动走"追加版本"，不会重复创建。
**slug 一经创建不可更改**（它是资源对外的固定地址）。

资源与版本提交后都进审核，通过前对外不可见。

### 让 CI 也自动发平台

`.github/workflows/ci.yml` 里有一个 `publish-scforge` 作业：**打 tag 时**自动把 Release 附件同步到平台。

它是从 Release 附件**下载**再上传的（云端没有编译产物），下载走 GitHub 加速前缀
（默认 `https://gh-proxy.com/`，失败自动退回直连）。要启用只需两步：

1. 在仓库 `Settings → Secrets and variables → Actions` 里加一个 `SCFORGE_TOKEN`（值就是 API Key）。
2. 打 tag。没配 Secret 时该作业打一行警告后跳过，CI 不会因此变红。

> 加速前缀在 `.buildtools/publish-to-scforge.py` 顶部的 `PROXY_PREFIX` 里改。
> 加速站属于第三方服务，不建议作为唯一依赖 —— 脚本本身有直连兜底，本地发布也不走它。

## 注意事项

- **命令白名单**：网页控制台插件可以在网页上执行服务端命令，而终端路径会跳过 AuthLevel 检查，所以务必配好 `Password` 与 `AllowedCommandPrefixes`，并尽量只绑 `127.0.0.1`。详见该插件的 README。
- **互斥**：部分插件抢同一个事件钩子（核心的事件管理器是"后注册者胜"），不要同时装功能重叠的插件。各插件 README 里写了互斥关系。
- 这些插件最初是从一套更老的 `.NET Framework` 插件迁移过来的，迁移时修掉的问题都记在各自 README 的"与旧版的差异"一节里。

## 许可

**GNU Affero General Public License v3.0（AGPL-3.0）**。详见 [LICENSE](LICENSE)。

这是**强 copyleft** 协议，并且带 AGPL 特有的**网络条款**（第 13 条）：

- 你可以自由使用、修改、分发这些插件，包括商用；
- 但**分发修改版（或基于它的衍生作品）时必须同样以 AGPL-3.0 开源**；
- 尤其注意：**哪怕你只是把修改过的版本部署成对外提供服务的网络应用**（比如架设一个
  面向玩家的服务器控制台面板），也必须向使用者提供完整对应源码 —— 这正是 AGPL 相比
  GPL 额外堵住的那个"网络服务不开源"的口子。

> 早于本次变更发布的版本曾以 MIT 授权，那些已分发的副本仍可按 MIT 使用。
