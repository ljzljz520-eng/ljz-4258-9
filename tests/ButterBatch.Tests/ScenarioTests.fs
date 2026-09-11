module ButterBatch.Tests.ScenarioTests

open System
open Xunit
open ButterBatch.Domain
open ButterBatch.Tests.TestFixtures

// ------------------------------------------------------------------
// 场景 1：酪乳残留未排净
// ------------------------------------------------------------------

[<Fact>]
let ``场景1-酪乳残留未排净时系统阻塞发布`` () =
    let w = world ()
    let mutable b = Rules.createBatch "CR-777" "张操作" 100m w.ScaleStable w.Tokens DateTime.Now |> okOrFail
    for st in [ PhaseTransition; ButtermilkDrain; Washing; Salting; Working; Blocking ] do
        b <- Rules.confirmStage b st b.CreamLot "张操作" None DateTime.Now |> okOrFail
    // 故意不登记排酪乳事件（残留）
    b <- Rules.addSalt b "SALT-A" 1.5m w.ScaleStable w.Tokens "张操作" DateTime.Now |> okOrFail
    b <- Rules.cutBlock b 80m "L-1" (Some w.ScaleStable) w.Tokens "张操作" DateTime.Now |> okOrFail
    b <- Rules.recordSample b 1 { Layer = Core; Radial = Center } 8m 12m "李质检" DateTime.Now |> okOrFail

    match Rules.readyToRelease b Rules.ClosureConfig.Default with
    | Error errs ->
        Assert.Contains(ButtermilkNotDrained, errs)
        // 阻塞信息明确告诉操作员“酪乳未排净”
        Assert.True(errs |> List.map string |> List.exists (fun m -> m.Contains "酪乳未排净"))
    | Ok () -> Assert.Fail "酪乳未排净不应通过"

[<Fact>]
let ``场景1b-登记了排酪乳但没有去向也判未排净`` () =
    let w = world ()
    let mutable b = Rules.createBatch "CR-777" "张操作" 100m w.ScaleStable w.Tokens DateTime.Now |> okOrFail
    for st in [ PhaseTransition; ButtermilkDrain; Washing; Salting; Working; Blocking ] do
        b <- Rules.confirmStage b st b.CreamLot "张操作" None DateTime.Now |> okOrFail
    let ev = { EventId = Guid.NewGuid(); Stage = ButtermilkDrain; At = DateTime.Now
               Material = "酪乳"; Lot = Some "BM"; Source = Some "搅拌缸"
               Destination = None; QuantityKg = Some 18m
               ScaleReading = Some { w.ScaleStable with Value = 18m; ReadingId = Guid.NewGuid() }; By = "张操作" }
    b <- Rules.logMaterial b ev w.Tokens |> okOrFail
    Assert.Equal(Error ButtermilkNotDrained, Rules.checkButtermilkDrained b)

// ------------------------------------------------------------------
// 场景 2：加盐分两次
// ------------------------------------------------------------------

[<Fact>]
let ``场景2-盐可分两次加入且两次材料批号与读数分别对应`` () =
    let w = world ()
    let mutable b = Rules.createBatch "CR-777" "张操作" 100m w.ScaleStable w.Tokens DateTime.Now |> okOrFail
    for st in [ ButtermilkDrain; Washing; Salting ] do
        b <- Rules.confirmStage b st b.CreamLot "张操作" None DateTime.Now |> okOrFail
    let r1 = { w.ScaleStable with Value = 0.9m; ReadingId = Guid.NewGuid() }
    b <- Rules.addSalt b "SALT-A" 0.9m r1 w.Tokens "张操作" DateTime.Now |> okOrFail
    let r2 = { w.ScaleStable with Value = 0.6m; ReadingId = Guid.NewGuid() }
    b <- Rules.addSalt b "SALT-B" 0.6m r2 w.Tokens "张操作" DateTime.Now |> okOrFail

    Assert.Equal(2, b.SaltAdditions.Length)
    Assert.Equal(1, b.SaltAdditions.[0].Ordinal)
    Assert.Equal(2, b.SaltAdditions.[1].Ordinal)
    // 每次加盐的数量必须由各自稳定台秤读数支撑
    Assert.Equal(0.9m, b.SaltAdditions.[0].ScaleReading.Value)
    Assert.Equal(0.6m, b.SaltAdditions.[1].ScaleReading.Value)
    Assert.True b.SaltAdditions.[0].ScaleReading.Stable
    Assert.True b.SaltAdditions.[1].ScaleReading.Stable

[<Fact>]
let ``场景2b-第二次加盐使用未稳定读数被拒`` () =
    let w = world ()
    let mutable b = Rules.createBatch "CR-777" "张操作" 100m w.ScaleStable w.Tokens DateTime.Now |> okOrFail
    for st in [ ButtermilkDrain; Washing; Salting ] do
        b <- Rules.confirmStage b st b.CreamLot "张操作" None DateTime.Now |> okOrFail
    b <- Rules.addSalt b "SALT-A" 0.9m { w.ScaleStable with Value = 0.9m; ReadingId = Guid.NewGuid() } w.Tokens "张操作" DateTime.Now |> okOrFail
    let r = Rules.addSalt b "SALT-B" 0.6m { w.ScaleUnstable with Value = 0.6m } w.Tokens "张操作" DateTime.Now
    Assert.True(match r with Error(ReadingNotStable Scale) -> true | _ -> false)

// ------------------------------------------------------------------
// 场景 3：产品分块后标签互换
// ------------------------------------------------------------------

[<Fact>]
let ``场景3-分块标签互换被系统识别`` () =
    let w = world ()
    let mutable b = Rules.createBatch "CR-777" "张操作" 100m w.ScaleStable w.Tokens DateTime.Now |> okOrFail
    b <- advanceToBlocking w b
    b <- Rules.cutBlock b 25m "L-A" (Some w.ScaleStable) w.Tokens "张操作" DateTime.Now |> okOrFail
    b <- Rules.cutBlock b 25m "L-B" (Some w.ScaleStable) w.Tokens "张操作" DateTime.Now |> okOrFail
    // 第 3 块误用已绑给第 1 块的标签 L-A（互换/重复）
    let r = Rules.cutBlock b 25m "L-A" (Some w.ScaleStable) w.Tokens "张操作" DateTime.Now
    match r with
    | Error(LabelMismatch(blockNo, label, firstBlockNo)) ->
        Assert.Equal(3, blockNo)
        Assert.Equal("L-A", label)
        Assert.Equal(1, firstBlockNo)
    | _ -> Assert.Fail "标签互换必须被识别"

// ------------------------------------------------------------------
// 场景 4：样品受热软化
// ------------------------------------------------------------------

[<Fact>]
let ``场景4-样品受热超过拒收温度不能登记与发布`` () =
    let w = world ()
    let mutable b = Rules.createBatch "CR-777" "张操作" 100m w.ScaleStable w.Tokens DateTime.Now |> okOrFail
    b <- advanceToBlocking w b
    b <- Rules.cutBlock b 25m "L-1" (Some w.ScaleStable) w.Tokens "张操作" DateTime.Now |> okOrFail
    let pos = { Layer = Core; Radial = Center }
    let r = Rules.recordSample b 1 pos 14.8m 12.0m "李质检" DateTime.Now
    match r with
    | Error(SampleTooWarm(blockNo, temp, limit)) ->
        Assert.Equal(1, blockNo); Assert.Equal(14.8m, temp); Assert.Equal(12.0m, limit)
    | _ -> Assert.Fail "受热软化样品必须拒收"
    // 拒收后该点样品不应存在
    Assert.Empty(b.Samples)

[<Fact>]
let ``场景4b-临界温度以内允许取样`` () =
    let w = world ()
    let mutable b = Rules.createBatch "CR-777" "张操作" 100m w.ScaleStable w.Tokens DateTime.Now |> okOrFail
    b <- advanceToBlocking w b
    b <- Rules.cutBlock b 25m "L-1" (Some w.ScaleStable) w.Tokens "张操作" DateTime.Now |> okOrFail
    let r = Rules.recordSample b 1 { Layer = Core; Radial = Center } 12.0m 12.0m "李质检" DateTime.Now
    Assert.True(match r with Ok _ -> true | _ -> false)

// ------------------------------------------------------------------
// 场景 5：台秤稳定标志丢失
// ------------------------------------------------------------------

[<Fact>]
let ``场景5-台秤未稳定读数在称重节点全部被拒`` () =
    let w = world ()
    // 创建批（奶油净重）
    let r0 = Rules.createBatch "CR-777" "张操作" 100m w.ScaleUnstable w.Tokens DateTime.Now
    Assert.True(match r0 with Error(ReadingNotStable Scale) -> true | _ -> false)

    let mutable b = Rules.createBatch "CR-777" "张操作" 100m w.ScaleStable w.Tokens DateTime.Now |> okOrFail
    for st in [ ButtermilkDrain; Washing; Salting ] do
        b <- Rules.confirmStage b st b.CreamLot "张操作" None DateTime.Now |> okOrFail
    // 加盐称重不稳定
    let r1 = Rules.addSalt b "SALT-A" 0.9m { w.ScaleUnstable with Value = 0.9m } w.Tokens "张操作" DateTime.Now
    Assert.True(match r1 with Error(ReadingNotStable Scale) -> true | _ -> false)

    b <- Rules.confirmStage b Working b.CreamLot "张操作" None DateTime.Now |> okOrFail
    b <- Rules.confirmStage b Blocking b.CreamLot "张操作" None DateTime.Now |> okOrFail
    // 分块称重不稳定
    let r2 = Rules.cutBlock b 25m "L-1" (Some { w.ScaleUnstable with Value = 25m }) w.Tokens "张操作" DateTime.Now
    Assert.True(match r2 with Error(ReadingNotStable Scale) -> true | _ -> false)

// ------------------------------------------------------------------
// 正常批应通过总检查
// ------------------------------------------------------------------

[<Fact>]
let ``正常批-物料闭合与样品覆盖全部通过`` () =
    let w = world ()
    let b = closedBatch w
    match Rules.readyToRelease b Rules.ClosureConfig.Default with
    | Ok () -> ()
    | Error es -> Assert.Fail(String.Join("; ", es |> List.map string))
