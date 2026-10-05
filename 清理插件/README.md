# 清理插件

定期清理过多的掉落物与生物，防止长期运行后世界被垃圾淹没。

## 配置文件

位置：`Plugins/清理插件/Clear.json`

| 项 | 默认 | 说明 |
|---|---|---|
| `Frequency` | `5` | 巡检间隔（游戏分钟） |
| `PickablesWarnCount` | `100` | 掉落物超过此数打警告 |
| `PickablesClearCount` | `1000` | 掉落物超过此数触发清理 |
| `PickablesMaxAgeSeconds` | `300` | **掉落物存活上限**（游戏秒）。只有放置时间早于它的才会被清；`<=0` = 不按时间筛 |
| `CreaturesWarnCount` | `30` | 生物超过此数打警告 |
| `CreaturesClearCount` | `100` | 生物超过此数触发清理 |
| `CreaturesKeepRatio` | `0.5` | 清理时**保留**的比例（0~1）。`1` = 不清理任何生物 |
| `AddMessage` | `true` | 清理时在聊天栏提示 |
| `LogWarnings` | `true` | 每次巡检都打警告 |

## 与旧版的差异

| 项 | 旧版 | 本版 |
|---|---|---|
| 掉落物清理 | ⚠️ `m_pickables.Clear()` —— **一次性清空全服所有掉落物**，玩家刚捡/刚存的会凭空消失 | 只清理**放置时间超过 `PickablesMaxAgeSeconds`** 的掉落物，刚掉出来的一律保留 |
| 配置读取 | `Parameters["Frequency"]` 直接取值，缺一个键就每帧抛 `KeyNotFoundException` 刷爆日志 | 强类型配置类 + `Sanitize()` 拉回默认值 |
| 生物清理随机性 | `foreach` 里每次 `new Random()`（同一序列，实际比例偏离 50%） | 单个 `Random` 实例 + Fisher-Yates 打乱后取前 N 个，比例精确 |
| 掉落物删除方式 | 直接改 `m_pickables`（绕过核心） | 标记 `Pickable.ToRemove = true`，由核心自己移除 |

## 清理行为说明

### 掉落物

当数量超过 `PickablesClearCount` 时，从最新往最旧遍历，标记 `CreationTime` 早于 `当前游戏时间 - PickablesMaxAgeSeconds` 的那些为待删除，直到删够 `总数 - 阈值` 个为止。

**刚掉出来的东西不会被清掉**——这是与旧版最重要的区别。调小 `PickablesMaxAgeSeconds` 可以更激进地清理。

### 生物

当数量超过 `CreaturesClearCount` 时，把数量降到 `总数 × CreaturesKeepRatio`，随机挑选要移除的实体（**不含玩家实体**）。

## ⚠️ 互斥提醒

本插件与「无动物插件」、「动物生成条件修改」共用 `ICreatureSpawnEventHandle`：

- 若启用了**无动物**或**动物生成条件修改**并清空了生物刷新表，本插件统计到的生物数会**恒为 0**，生物清理这部分等于失效（掉落物清理不受影响）。
- 反过来，本插件只**移除已存在的实体**，不修改刷新表，与它们不直接冲突。

## 注意事项

- 巡检间隔是**游戏时间**，不是真实时间。游戏里所有人睡觉/离线时不会触发。
- 生物移除会打断 AI 状态；如果你的世界靠刷怪塔产资源，注意 `CreaturesKeepRatio` 别调太低。