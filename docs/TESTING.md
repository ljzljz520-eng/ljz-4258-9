# 测试说明

## 运行
```bash
dotnet test ButterBatch.sln
```
当前：**29 个测试全部通过**。

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
