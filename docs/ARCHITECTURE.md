# 架构说明：黄油搅拌批次与样品规则系统

## 范围边界（刻意不做的事）
- **不提供配方**：没有任何原料配比、脂肪含量目标等。
- **不提供搅拌参数**：没有转速、时长、温度设定点等工艺参数。
系统只负责：批次/阶段对应、材料与去向留痕、多点取样覆盖、物料闭合与授权设备读数校验。

## 分层

```
ButterBatch.Domain (F#, net8.0, LiteDB)
├── Domain.fs   阶段/位置/读数/材料/加盐/分块/样品/批次 模型与规则错误
├── Rules.fs    纯函数规则引擎（无 UI、无 IO，可单元测试）
└── Storage.fs  LiteDB 本地持久化 + 设备授权令牌仓储

ButterBatch.App (F#, Avalonia 0.10 + OxyPlot)
├── BridgePage.fs   Web Serial API 授权页面（浏览器端）
├── SerialBridge.fs loopback HTTP 桥接服务（127.0.0.1）
├── MainWindow.fs   桌面工位（操作员 / 质量 / 设备桥接 三个选项卡）
└── App.fs          Avalonia 启动

ButterBatch.Tests (xUnit)
├── RulesTests.fs / ScenarioTests.fs / StorageTests.fs
```

## 核心规则（Rules.fs）

| 主题 | 规则 |
|---|---|
| 阶段 | 奶油相转变→排酪乳→洗涤→加盐(可重复)→捏合→分块，单调推进；确认时奶油批必须与批次一致 |
| 酪乳 | 排酪乳事件必须有阶段、数量（稳定台秤读数）与**去向**，否则判 `ButtermilkNotDrained` |
| 加盐 | 仅加盐阶段；盐批号必填；次序连续（支持分两次）；每次单独稳定台秤读数 |
| 分块 | 仅分块阶段；标签号批内唯一，重复即 `LabelMismatch`（标签互换） |
| 取样 | 位置必须在冻结集合（表/中/芯 × 中心/边缘）；样品温度超拒收限判受热软化 |
| 覆盖 | 不能只有表层；中层、芯层必须覆盖；内部点须含中心与边缘 |
| 结果 | 水分须来自稳定授权的水分仪；盐分须有稳定称量读数；两结果同时发布 |
| 物料闭合 | 奶油+盐 ≈ 产品块+酪乳（默认绝对容差 2 kg 或相对 1.5%，取大）；洗涤水不计入黄油质量 |
| 授权读数 | 令牌必须存在、未撤销、未过期；设备种类必须匹配；`Stable=false` 一律拒收 |

## Web Serial 授权桥接层

```
[台秤/水分仪] --串口--> [Chrome/Edge 页面 navigator.serial]
  requestPort() 必须由用户手势触发（授权点）
  --POST /reading {token,kind,value,stable,raw,at}--> [HttpListener 127.0.0.1:8765]
                                                        ├── 校验令牌（LiteDB auth_grants）
                                                        ├── 无效/错种类/撤销 → 403 + 拒收事件
                                                        └── 有效 → 读数留痕 + 推送 UI
```

- 令牌由本地仓储签发（台秤、水分仪各一个），页面只显示对应令牌。
- “释放授权”调用 `/revoke`，重放旧令牌返回 403。
- 桥接层只绑定 loopback，不监听局域网。

## 本地数据（LiteDB：butterbatch.db）

集合：`batches / confirmations / materials / salts / blocks / samples / readings / auth_grants`，
按 BatchId 建索引；保存采用整聚合覆盖写，读取重建 `ButterBatch`。
