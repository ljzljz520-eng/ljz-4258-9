# 架构说明：黄油搅拌批次与样品规则系统

## 范围边界（刻意不做的事）
- **不提供配方**：没有任何原料配比、脂肪含量目标等。
- **不提供搅拌参数**：没有转速、时长、温度设定点等工艺参数。
系统只负责：批次/阶段对应、材料与去向留痕、多点取样覆盖、物料闭合与授权设备读数校验。

## 分层

```
ButterBatch.Domain (F#, net8.0, LiteDB)
├── Domain.fs   阶段/位置/读数/材料/加盐/分块/样品/返工/标签事件/批次 模型与规则错误
├── Rules.fs    纯函数规则引擎（无 UI、无 IO，可单元测试），含返捏合分支
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
| 返捏合 | 质量人员逐块指定受影响分块开立返工工单；指定即刻冻结原分块与其多点水盐结果（`ReworkFrozen`，拒绝再取样/发布）；执行合并时记录解冻复称并入读数；返工可混入另一批（目标批 `IncomingReworks` 谱系边）或在本批内返捏合 |
| 部分返工 | 只返捏合半块时，实际返工量 < 块重且必须登记未返工部分的保留标签；原块号冻结留痕（`ReworkedOut`），剩余部分另立独立块（`SplitFromBlockNo` 溯源）；冻结结果不能继承代表剩余部分，须重新取样 |
| 标签脱落 | 软化中物理标签脱落由质量人员登记 `BlockLabelEvent`；未补贴替代标签前块 `LabelMissing=true`，禁止取样/放行；替代标签必须全新，脱落标签进入禁用集合（`LabelReuseDetached`） |
| 返工重分块 | 返工料在目标批重新分块，标签唯一且新块重量之和必须等于并入量（`ReworkOutputNotClosed`）；新块独立新增取样，覆盖规则与常规块一致；`completeRework` 校验覆盖与结果齐备 |
| 部分放行 | 已发布结果且覆盖充分的独立块可逐块放行（`Released`，不可逆）；已放行块不能再指定返工（`BlockAlreadyReleased`），且不重复参与整批覆盖检查，但仍计入物料闭合 |
| 返工闭合 | 投入 = 奶油 + 盐 + 外来返工料；分出 = 批内产品（`ReworkedOut` 冻结原块不计，含半块剩余块）+ 酪乳 + 跨批返工送出；本批返捏合物料不出批，原块冻结与新块计入恰好抵消 |

## 返捏合分支状态机

```
质量人员指定受影响块          解冻软化→逐块复称并入            重新分块+新增取样+结果
Intact ──designateRework──▶ ReworkFrozen ──mergeRework──▶ ReworkedOut（原块冻结留痕）
                                  │                             └ 目标批：recutReworkBlocks → 新 Intact 块
                                  └ 半块：未返工部分另立 Intact 块（保留原标签，SplitFromBlockNo）
Intact ──releaseBlock──▶ Released（不可逆，不可再返工）
任意在制块 ──reportLabelDetached(None)──▶ LabelMissing（禁取样/放行）──补贴新标签──▶ 恢复
```

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

集合：`batches / confirmations / materials / salts / blocks / samples / readings / auth_grants
/ label_events / rework_orders / incoming_reworks`，
按 BatchId 建索引；返工工单的逐块并入明细以 JSON 内嵌存储，并入台秤读数仍走 `readings`
集合按令牌重建；保存采用整聚合覆盖写，读取重建 `ButterBatch`。
