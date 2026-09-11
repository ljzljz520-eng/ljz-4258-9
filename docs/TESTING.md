# 测试说明

## 运行
```bash
dotnet test ButterBatch.sln
```
当前：**35 个测试全部通过**。

## 返捏合分支场景（ReworkTests.fs）

1. **返工混入另一批（R1）**：整块 28kg 指定返工即刻 `ReworkFrozen`，原水盐结果不可再取样；
   合并后源块 `ReworkedOut`，目标批 `IncomingReworks` 记录 28kg 谱系边（来源批+工单+逐块并入读数）；
   在目标批重新分块 RW-1、新增 6 点取样并发布、`completeRework` 关单；源批 2/3 号块保持独立，两批各自物料闭合。
2. **只返捏合半块（R2）**：28kg 块指定返 12kg 必须登记保留标签；并入量必须等于指定量（10kg 被 `ReworkQtyInvalid` 拒）；
   原 1 号块冻结留痕，剩余 16kg 另立独立块（`SplitFromBlockNo=1`，保留 L-1），原冻结 6 点结果不继承，剩余块与返工新块都重新取样。
3. **标签在软化中脱落（R3）**：脱落未补贴时 `LabelMissing=true`，取样/放行/总检查全部被 `LabelDetached` 阻塞；
   复用脱落标签 L-2 判 `LabelReuseDetached`，占用在用标签判 `LabelMismatch`；补贴新标签 L-2R 后恢复，旧标签分块复用仍被拒。
4. **返工后重新分块（R4）**：新块重量之和必须等于并入量（14+13≠27.5 判 `ReworkOutputNotClosed`）；
   标签占用被拒；新块只有表层样不能关单（`SurfaceSamplesOnly`），覆盖齐备后关单并记录 `OutputBlockNos`。
5. **原批已有部分放行（R5）**：已发布结果的 1 号块先 `releaseBlock` 放行；已放行块再指定返工判 `BlockAlreadyReleased`；
   3 号块返工不影响 1（`Released`）、2（`Intact`）号块，整批仍闭合可发布。

另有 `StorageTests` 的返工 LiteDB 往返：工单/逐块并入明细/外来返工料/标签事件/块状态/半块剩余溯源均可重建，
且同一工单分别存入源批与目标批不产生主键冲突；读数改用稳定 `ReadingId` 建键，同授权令牌的多条读数不再互相覆盖。

## 指定的五个场景（ScenarioTests.fs）

1. **酪乳残留未排净**
   - `场景1-酪乳残留未排净时系统阻塞发布`：无排酪乳事件 → readyToRelease 返回含 `ButtermilkNotDrained` 的错误列表。
   - `场景1b-...没有去向也判未排净`：登记了数量但 Destination=None → 仍判未排净。
2. **加盐分两次**
   - `场景2-盐可分两次加入...`：第一次 SALT-A/0.9kg、第二次 SALT-B/0.6kg，次序 1、2，各自带独立稳定读数。
   - `场景2b-第二次加盐使用未稳定读数被拒`：`ReadingNotStable Scale`。
3. **产品分块后标签互换**
   - `场景3-...`：第 3 块误用第 1 块标签 L-A → `LabelMismatch(blockNo=3, label="L-A", firstBlockNo=1)`。
4. **样品受热软化**
   - `场景4-...`：芯部样品 14.8℃ > 拒收限 12.0℃ → `SampleTooWarm(1, 14.8, 12.0)`，样品不落库。
   - `场景4b-临界温度以内允许取样`：恰好 12.0℃ 可取样。
5. **台秤稳定标志丢失**
   - `场景5-...`：创建批、加盐、分块三个称重节点使用 Stable=false 读数全部被拒。

另有正常批总检查、物料闭合（含人为制造差异）、覆盖规则、离散统计、
LiteDB 往返与令牌撤销（StorageTests）、桥接 HTTP 冒烟（见下）。

## 桥接层冒烟（已手工验证）
临时控制台对 127.0.0.1:8799 的验证结果：
- GET /bridge 含令牌与 `requestPort`（用户手势授权）；
- 稳定台秤读数 → 200 并触发接收事件；
- 伪造令牌 → 403；水分仪令牌冒充台秤 → 403；撤销后重放 → 403；坏报文 → 400。

## 桌面 UI 冒烟（headless）
用 Avalonia Headless 平台实例化主窗口，断言控件树中存在
PlotView / DataGrid / TabControl（191 个控件），通过。
