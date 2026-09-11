module ButterBatch.Tests.ReworkTests

open System
open Xunit
open ButterBatch.Domain
open ButterBatch.Tests.TestFixtures

/// 已分块（默认 3 块 28/28/27.5，物料闭合）但尚未取样的批次
let blockingBatch (w: World) : ButterBatch =
    let mutable b =
        Rules.createBatch "CR-777" "张操作" 100m w.ScaleStable w.Tokens DateTime.Now |> okOrFail
    for st in [ PhaseTransition; ButtermilkDrain; Washing; Salting; Working; Blocking ] do
        b <- Rules.confirmStage b st b.CreamLot "张操作" None DateTime.Now |> okOrFail
    b <- drainButtermilk w 18m (Some "酪乳回收罐 R-2") b
    b <- Rules.addSalt b "SALT-A" 0.9m { w.ScaleStable with Value = 0.9m; ReadingId = Guid.NewGuid() } w.Tokens "张操作" DateTime.Now |> okOrFail
    b <- Rules.addSalt b "SALT-B" 0.6m { w.ScaleStable with Value = 0.6m; ReadingId = Guid.NewGuid() } w.Tokens "张操作" DateTime.Now |> okOrFail
    b <- Rules.cutBlock b 28.0m "L-1" (Some { w.ScaleStable with Value = 28.0m; ReadingId = Guid.NewGuid() }) w.Tokens "张操作" DateTime.Now |> okOrFail
    b <- Rules.cutBlock b 28.0m "L-2" (Some { w.ScaleStable with Value = 28.0m; ReadingId = Guid.NewGuid() }) w.Tokens "张操作" DateTime.Now |> okOrFail
    b <- Rules.cutBlock b 27.5m "L-3" (Some { w.ScaleStable with Value = 27.5m; ReadingId = Guid.NewGuid() }) w.Tokens "张操作" DateTime.Now |> okOrFail
    b

/// 给指定块做齐 6 点取样并发布水盐结果（模拟“原分块多点水盐结果”）
let publishBlockResults (w: World) (b: ButterBatch) blockNo : ButterBatch =
    let mutable b = b
    for pos in Positions.frozen do
        b <- Rules.recordSample b blockNo pos 8.0m 12.0m "李质检" DateTime.Now |> okOrFail
    let ids = b.Samples |> List.filter (fun s -> s.BlockNo = blockNo) |> List.map (fun s -> s.SampleId)
    for sid in ids do
        b <- Rules.publishResult b sid 14.2m w.MeterStable 1.5m w.ScaleStable w.Tokens "李质检" DateTime.Now |> okOrFail
    b

/// 给返工后新块做齐新增取样并发布结果
let publishReworkResults (w: World) (b: ButterBatch) reworkId blockNo : ButterBatch =
    let mutable b = b
    for pos in Positions.frozen do
        b <- Rules.recordReworkSample b reworkId blockNo pos 8.0m 12.0m "李质检" DateTime.Now |> okOrFail
    let ids = b.Samples |> List.filter (fun s -> s.BlockNo = blockNo) |> List.map (fun s -> s.SampleId)
    for sid in ids do
        b <- Rules.publishResult b sid 14.0m w.MeterStable 1.6m w.ScaleStable w.Tokens "李质检" DateTime.Now |> okOrFail
    b

// ------------------------------------------------------------------
// 场景 R1：返工混入另一批（整块；原批冻结 + 目标批谱系 + 新增取样）
// ------------------------------------------------------------------

[<Fact>]
let ``返工场景1-整块返工混入另一批时原块冻结目标批留痕并新增取样`` () =
    let w = world ()
    let mutable src = blockingBatch w
    src <- publishBlockResults w src 1
    let mutable tgt = blockingBatch w

    // 质量人员指定源批 1 号块返工，并入另一批 tgt
    let src', rid =
        Rules.designateRework src tgt.BatchId [ (1, None, None) ] "盐花不均，返工混入后批" "李质检" DateTime.Now |> okOrFail
    src <- src'
    // 指定即冻结：原块状态、原水盐结果不可再改
    let frozenBlock = src.Blocks |> List.find (fun b -> b.BlockNo = 1)
    Assert.Equal(ReworkFrozen, frozenBlock.Status)
    let frozenSamples = src.Samples |> List.filter (fun s -> s.BlockNo = 1)
    Assert.All(frozenSamples, fun s -> Assert.True s.ResultPublishedAt.IsSome)

    // 冻结块不允许再取样（新增取样必须走返工分支、落在返工新块上）
    let r1 = Rules.recordSample src 1 { Layer = Core; Radial = Center } 8m 12m "李质检" DateTime.Now
    Assert.True(match r1 with Error(BlockReworkFrozen 1) -> true | _ -> false)

    // 执行返工合并：整块 28kg 并入目标批（逐块稳定台秤读数）
    let src'', tgt' =
        Rules.mergeRework src tgt rid [ 1, 28.0m, { w.ScaleStable with Value = 28.0m; ReadingId = Guid.NewGuid() } ] w.Tokens "张操作" DateTime.Now
        |> okOrFail
    src <- src''
    tgt <- tgt'
    let outBlock = src.Blocks |> List.find (fun b -> b.BlockNo = 1)
    Assert.Equal(ReworkedOut, outBlock.Status)
    // 目标批谱系边：外来返工料 28kg，可追溯来源批与工单
    Assert.Single(tgt.IncomingReworks) |> ignore
    let inc = tgt.IncomingReworks.Head
    Assert.Equal(28.0m, inc.QuantityKg)
    Assert.Equal(src.BatchId, inc.SourceBatchId)
    Assert.Equal(rid, inc.ReworkId)

    // 返工后在目标批重新分块（28kg 一块，新标签）
    tgt <- Rules.recutReworkBlocks tgt rid [ 28.0m, "RW-1", { w.ScaleStable with Value = 28.0m; ReadingId = Guid.NewGuid() } ]
                              w.Tokens "张操作" DateTime.Now |> okOrFail
    let newBlockNo = tgt.Blocks |> List.find (fun b -> b.LabelId = "RW-1") |> fun b -> b.BlockNo
    // 新增取样只能落在返工新块上；落在老块被拒
    let rBad = Rules.recordReworkSample tgt rid 1 { Layer = Core; Radial = Center } 8m 12m "李质检" DateTime.Now
    Assert.True(match rBad with Error(Conflict _) -> true | _ -> false)
    tgt <- publishReworkResults w tgt rid newBlockNo

    // 关闭返工分支
    tgt <- Rules.completeRework tgt rid "李质检" DateTime.Now |> okOrFail
    let orderT = tgt.ReworkOrders |> List.find (fun o -> o.ReworkId = rid)
    Assert.True orderT.CompletedAt.IsSome

    // 源批：未返工的 2、3 号块保持独立；物料闭合（28kg 作为跨批返工送出）
    let src2 = src.Blocks |> List.find (fun b -> b.BlockNo = 2)
    let src3 = src.Blocks |> List.find (fun b -> b.BlockNo = 3)
    Assert.Equal(Intact, src2.Status)
    Assert.Equal(Intact, src3.Status)
    Assert.Equal(Ok(), Rules.checkMaterialClosure src Rules.ClosureConfig.Default)
    // 源批总检查：2、3 号块补齐结果后可发布（返工工单对源批只要求全部并入）
    let mutable srcFinal = src
    srcFinal <- publishBlockResults w srcFinal 2
    srcFinal <- publishBlockResults w srcFinal 3
    match Rules.readyToRelease srcFinal Rules.ClosureConfig.Default with
    | Ok () -> ()
    | Error es -> Assert.Fail(String.Join("; ", es |> List.map string))

    // 目标批：返工新块已完成；补齐原 3 块结果后整批可发布，物料仍闭合
    let mutable tgtFinal = tgt
    for no in [ 1; 2; 3 ] do tgtFinal <- publishBlockResults w tgtFinal no
    Assert.Equal(Ok(), Rules.checkMaterialClosure tgtFinal Rules.ClosureConfig.Default)
    match Rules.readyToRelease tgtFinal Rules.ClosureConfig.Default with
    | Ok () -> ()
    | Error es -> Assert.Fail(String.Join("; ", es |> List.map string))

// ------------------------------------------------------------------
// 场景 R2：只返捏合半块（部分返工，未返工部分保持独立）
// ------------------------------------------------------------------

[<Fact>]
let ``返工场景2-只返捏合半块时剩余部分另立独立块原结果冻结`` () =
    let w = world ()
    let mutable b = blockingBatch w
    b <- publishBlockResults w b 1
    // 1 号块 28kg 中只返 12kg，剩余 16kg 保留原标签 L-1
    let b', rid =
        Rules.designateRework b b.BatchId [ (1, Some 12.0m, Some "L-1") ] "边缘含水率偏高，半块返捏" "李质检" DateTime.Now
        |> okOrFail
    b <- b'
    Assert.Equal(ReworkFrozen, (b.Blocks |> List.find (fun x -> x.BlockNo = 1)).Status)

    // 本批内合并：实际并入量必须等于指定返工量，否则拒
    let bad =
        Rules.mergeRework b b rid [ 1, 10.0m, { w.ScaleStable with Value = 10.0m; ReadingId = Guid.NewGuid() } ] w.Tokens "张操作" DateTime.Now
    Assert.True(match bad with Error(ReworkQtyInvalid(1, 10.0m, 12.0m)) -> true | _ -> false)

    let b2, _ =
        Rules.mergeRework b b rid [ 1, 12.0m, { w.ScaleStable with Value = 12.0m; ReadingId = Guid.NewGuid() } ] w.Tokens "张操作" DateTime.Now
        |> okOrFail
    b <- b2
    let orig = b.Blocks |> List.find (fun x -> x.BlockNo = 1)
    Assert.Equal(ReworkedOut, orig.Status)
    // 未返工部分另立为新块号 4，16kg，保留原标签，记录溯源边
    let rem = b.Blocks |> List.find (fun x -> x.SplitFromBlockNo = Some 1)
    Assert.Equal(16.0m, rem.WeightKg)
    Assert.Equal("L-1", rem.LabelId)
    Assert.Equal(Intact, rem.Status)
    Assert.Equal(Some rid, rem.ReworkId)
    let order = b.ReworkOrders |> List.find (fun o -> o.ReworkId = rid)
    Assert.Equal(Some rem.BlockNo, order.Items.Head.RemainderBlockNo)
    // 原 1 号块样品结果保持冻结（记录仍在，不再参与检查）
    Assert.Equal(6, b.Samples |> List.filter (fun s -> s.BlockNo = 1) |> List.length)

    // 返工部分在本批重新分块（12kg 一块，新标签）
    b <- Rules.recutReworkBlocks b rid [ 12.0m, "RW-1", { w.ScaleStable with Value = 12.0m; ReadingId = Guid.NewGuid() } ]
                            w.Tokens "张操作" DateTime.Now |> okOrFail
    let rwNo = b.Blocks |> List.find (fun x -> x.LabelId = "RW-1") |> fun x -> x.BlockNo
    b <- publishReworkResults w b rid rwNo
    // 剩余独立块要按新块重新取样（原冻结结果不能继承代表剩余部分）
    b <- publishBlockResults w b rem.BlockNo
    b <- Rules.completeRework b rid "李质检" DateTime.Now |> okOrFail

    // 2、3 号老块补齐结果，整批闭合可发布
    let mutable bFinal = b
    bFinal <- publishBlockResults w bFinal 2
    bFinal <- publishBlockResults w bFinal 3
    Assert.Equal(Ok(), Rules.checkMaterialClosure bFinal Rules.ClosureConfig.Default)
    match Rules.readyToRelease bFinal Rules.ClosureConfig.Default with
    | Ok () -> ()
    | Error es -> Assert.Fail(String.Join("; ", es |> List.map string))

// ------------------------------------------------------------------
// 场景 R3：标签在软化中脱落
// ------------------------------------------------------------------

[<Fact>]
let ``返工场景3-标签软化脱落后未补贴禁止取样放行补贴新标签后方可继续`` () =
    let w = world ()
    let mutable b = blockingBatch w
    // 返捏合软化中 2 号块标签脱落，暂未补贴
    b <- Rules.reportLabelDetached b 2 None (Some "软化升温时胶标脱落") "李质检" DateTime.Now |> okOrFail
    let blk = b.Blocks |> List.find (fun x -> x.BlockNo = 2)
    Assert.True blk.LabelMissing
    Assert.Equal(Some "L-2", b.LabelEvents.Head.OldLabelId)
    Assert.True b.LabelEvents.Head.NewLabelId.IsNone

    // 标签缺失：取样与放行均被拒
    let r1 = Rules.recordSample b 2 { Layer = Core; Radial = Center } 8m 12m "李质检" DateTime.Now
    Assert.True(match r1 with Error(LabelDetached 2) -> true | _ -> false)
    let r2 = Rules.releaseBlock b 2 "李质检" DateTime.Now
    Assert.True(match r2 with Error(LabelDetached 2) -> true | _ -> false)
    // 总检查也被阻塞
    match Rules.readyToRelease b Rules.ClosureConfig.Default with
    | Error errs -> Assert.Contains(LabelDetached 2, errs)
    | Ok () -> Assert.Fail "标签脱落未处理不应通过"

    // 质量人员核对块身份后补贴新标签（不得复用脱落的 L-2）
    let rReuse = Rules.reportLabelDetached b 2 (Some "L-2") None "李质检" DateTime.Now
    Assert.True(match rReuse with Error(LabelReuseDetached "L-2") -> true | _ -> false)
    let rOther = Rules.reportLabelDetached b 2 (Some "L-3") None "李质检" DateTime.Now
    Assert.True(match rOther with Error(LabelMismatch _) -> true | _ -> false)
    b <- Rules.reportLabelDetached b 2 (Some "L-2R") None "李质检" DateTime.Now |> okOrFail
    let blk2 = b.Blocks |> List.find (fun x -> x.BlockNo = 2)
    Assert.False blk2.LabelMissing
    Assert.Equal("L-2R", blk2.LabelId)
    // 旧标签进入脱落集合，分块时复用旧标签被拒
    let rOld =
        Rules.cutBlock b 25m "L-2" (Some { w.ScaleStable with Value = 25m; ReadingId = Guid.NewGuid() }) w.Tokens "张操作" DateTime.Now
    Assert.True(match rOld with Error(LabelReuseDetached "L-2") -> true | _ -> false)
    // 补贴后可正常取样
    let r3 = Rules.recordSample b 2 { Layer = Core; Radial = Center } 8m 12m "李质检" DateTime.Now
    Assert.True(match r3 with Ok _ -> true | _ -> false)

// ------------------------------------------------------------------
// 场景 R4：返工后重新分块（重量闭合 + 覆盖 + 标签）
// ------------------------------------------------------------------

[<Fact>]
let ``返工场景4-返工后重新分块重量必须闭合且新块独立取样`` () =
    let w = world ()
    let mutable b = blockingBatch w
    let b', rid =
        Rules.designateRework b b.BatchId [ (3, None, None) ] "色泽不均返工" "李质检" DateTime.Now |> okOrFail
    b <- b'
    let b2, _ =
        Rules.mergeRework b b rid [ 3, 27.5m, { w.ScaleStable with Value = 27.5m; ReadingId = Guid.NewGuid() } ] w.Tokens "张操作" DateTime.Now
        |> okOrFail
    b <- b2
    // 新分块重量之和不等于并入量 -> 返工分支不闭合
    let bad =
        Rules.recutReworkBlocks b rid
            [ 14.0m, "RW-A", { w.ScaleStable with Value = 14.0m; ReadingId = Guid.NewGuid() }
              13.0m, "RW-B", { w.ScaleStable with Value = 13.0m; ReadingId = Guid.NewGuid() } ]
            w.Tokens "张操作" DateTime.Now
    Assert.True(match bad with Error(ReworkOutputNotClosed(_, 27.5m, 27.0m)) -> true | _ -> false)
    // 标签冲突也被拒（L-1 在老块上）
    let dup =
        Rules.recutReworkBlocks b rid [ 27.5m, "L-1", { w.ScaleStable with Value = 27.5m; ReadingId = Guid.NewGuid() } ]
            w.Tokens "张操作" DateTime.Now
    Assert.True(match dup with Error(LabelMismatch _) -> true | _ -> false)

    // 正确重新分两块（14 + 13.5 = 27.5）
    b <- Rules.recutReworkBlocks b rid
            [ 14.0m, "RW-A", { w.ScaleStable with Value = 14.0m; ReadingId = Guid.NewGuid() }
              13.5m, "RW-B", { w.ScaleStable with Value = 13.5m; ReadingId = Guid.NewGuid() } ]
            w.Tokens "张操作" DateTime.Now |> okOrFail
    let noA = b.Blocks |> List.find (fun x -> x.LabelId = "RW-A") |> fun x -> x.BlockNo
    let noB = b.Blocks |> List.find (fun x -> x.LabelId = "RW-B") |> fun x -> x.BlockNo
    // 新增取样：只取表层不能关闭分支（覆盖规则同样适用于返工新块）
    b <- Rules.recordReworkSample b rid noA { Layer = Surface; Radial = Center } 8m 12m "李质检" DateTime.Now |> okOrFail
    let rCov = Rules.completeRework b rid "李质检" DateTime.Now
    Assert.True(match rCov with Error(SurfaceSamplesOnly | CoverageMissing _) -> true | _ -> false)

    b <- publishReworkResults w b rid noA
    b <- publishReworkResults w b rid noB
    b <- Rules.completeRework b rid "李质检" DateTime.Now |> okOrFail
    let order = b.ReworkOrders |> List.find (fun o -> o.ReworkId = rid)
    Assert.Equal<int>(ResizeArray order.OutputBlockNos, ResizeArray [ noA; noB ])
    // 老 3 号块冻结留痕，标签记录不变；新块带工单溯源
    let old3 = b.Blocks |> List.find (fun x -> x.BlockNo = 3)
    Assert.Equal(ReworkedOut, old3.Status)
    Assert.Equal("L-3", old3.LabelId)

    // 补齐其余块结果，整批闭合可发布
    let mutable bFinal = b
    for no in [ 1; 2 ] do bFinal <- publishBlockResults w bFinal no
    Assert.Equal(Ok(), Rules.checkMaterialClosure bFinal Rules.ClosureConfig.Default)
    match Rules.readyToRelease bFinal Rules.ClosureConfig.Default with
    | Ok () -> ()
    | Error es -> Assert.Fail(String.Join("; ", es |> List.map string))

// ------------------------------------------------------------------
// 场景 R5：原批已有部分放行
// ------------------------------------------------------------------

[<Fact>]
let ``返工场景5-已部分放行时未放行块可返工已放行块不可再返工`` () =
    let w = world ()
    let mutable b = blockingBatch w
    b <- publishBlockResults w b 1
    b <- publishBlockResults w b 2
    b <- publishBlockResults w b 3
    // 质量人员先放行 1 号块（部分放行，不可逆）
    b <- Rules.releaseBlock b 1 "李质检" DateTime.Now |> okOrFail
    Assert.Equal(Released, (b.Blocks |> List.find (fun x -> x.BlockNo = 1)).Status)

    // 已放行块不能再指定返工
    let rRel =
        Rules.designateRework b b.BatchId [ (1, None, None) ] "放行后异常" "李质检" DateTime.Now
    Assert.True(match rRel with Error(BlockAlreadyReleased 1) -> true | _ -> false)

    // 未放行的 3 号块返工本批；2 号块保持独立，已放行的 1 号块不受影响
    let b', rid =
        Rules.designateRework b b.BatchId [ (3, None, None) ] "3 号块复检异常返工" "李质检" DateTime.Now |> okOrFail
    b <- b'
    let b2, _ =
        Rules.mergeRework b b rid [ 3, 27.5m, { w.ScaleStable with Value = 27.5m; ReadingId = Guid.NewGuid() } ] w.Tokens "张操作" DateTime.Now
        |> okOrFail
    b <- b2
    b <- Rules.recutReworkBlocks b rid [ 27.5m, "RW-1", { w.ScaleStable with Value = 27.5m; ReadingId = Guid.NewGuid() } ]
                            w.Tokens "张操作" DateTime.Now |> okOrFail
    let rwNo = b.Blocks |> List.find (fun x -> x.LabelId = "RW-1") |> fun x -> x.BlockNo
    b <- publishReworkResults w b rid rwNo
    b <- Rules.completeRework b rid "李质检" DateTime.Now |> okOrFail

    // 已放行块状态不变；其覆盖结果不再重复要求；物料闭合
    Assert.Equal(Released, (b.Blocks |> List.find (fun x -> x.BlockNo = 1)).Status)
    Assert.Equal(Intact, (b.Blocks |> List.find (fun x -> x.BlockNo = 2)).Status)
    Assert.Equal(Ok(), Rules.checkMaterialClosure b Rules.ClosureConfig.Default)
    match Rules.readyToRelease b Rules.ClosureConfig.Default with
    | Ok () -> ()
    | Error es -> Assert.Fail(String.Join("; ", es |> List.map string))
