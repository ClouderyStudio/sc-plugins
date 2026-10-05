# 主城防破坏插件

主城范围内禁止破坏方块、放置方块、爆炸、放火、使用锤子、投射物品与修改告示牌。管理员默认豁免。

## 覆盖的 5 个事件

| 接口 | 拦截的行为 |
|---|---|
| `IPlayerBreakAndPlaceHandle` | 破坏方块、放置方块 |
| `IExplodeEventHandle` | 爆炸（威力归零） |
| `IFireEventHandle` | 放火、火焰蔓延 |
| `IPlayerInteractEventHandle` | 使用锤子、投射物品、改告示牌 |

## 配置文件

位置：`Plugins/主城防破坏插件/MajorCityAreas.json`

```json
{
  "Areas": [
    { "MinX": -50, "MinY": 0, "MinZ": -50, "MaxX": 50, "MaxY": 256, "MaxZ": 50 }
  ],
  "HammerBlockIds": [ 230 ],
  "AllowServerManager": true,
  "KickOnHammer": false,
  "NotifyPlayer": true
}
```

| 项 | 默认 | 说明 |
|---|---|---|
| `Areas` | `[]` | 保护区域列表，坐标写反会自动归一化 |
| `HammerBlockIds` | `[230]` | 视为"锤子"的方块 ID |
| `AllowServerManager` | `true` | 管理员豁免 |
| `KickOnHammer` | `false` | 在主城用锤子时是否**踢出**玩家 |
| `NotifyPlayer` | `true` | 拦截时是否给玩家屏幕提示 |

### 关于 HammerBlockIds

`230` 沿用旧版的硬编码值。**方块 ID 会随游戏版本与模组变化**，正式使用前建议先用「哨子获取信息插件」确认实际的锤子 ID。

### 关于 KickOnHammer

旧版发现玩家在主城用锤子**直接踢出**，惩罚过激。本版默认只警告 + 记日志，需要原来的行为请把 `KickOnHammer` 设为 `true`。

## 与旧版的差异

| 项 | 旧版 | 本版 |
|---|---|---|
| `Save()` | ⚠️ **整段被注释掉**，区域永远只能手改 JSON | 正常落盘（原子写入） |
| 聊天处理 | ⚠️ `ReceiveMessage` 空实现却把 `External` 置 `false`，静默干扰全服聊天处理流程 | **不实现该接口**，不碰聊天 |
| 锤子 | 硬编码 ID 230 + 无条件踢人 | ID 可配，踢人需显式打开 |
| 区域判定 | 依赖 `SubsystemGameInfo` 非空，首次进档会整体失效 | 去掉该依赖 |

## 注意事项

- **区域列表默认为空** —— 装上插件不配区域等于不生效，需要自己写 `MajorCityAreas.json`。
- 爆炸是**威力归零**而非"完全取消"，视觉上仍可能有爆炸表现。
- 与本仓库的「个人领地与玩家传送插件」（领地保护）、「多功能主城插件」在概念上相近，叠加时以最先拒绝的那一层为准（核心是 `foreach` + 短路）。