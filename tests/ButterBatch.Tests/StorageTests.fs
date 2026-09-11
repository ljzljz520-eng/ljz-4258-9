module ButterBatch.Tests.StorageTests

open System
open System.IO
open Xunit
open ButterBatch.Domain
open ButterBatch.Storage
open ButterBatch.Tests.TestFixtures

let tempDb () =
    let dir = Path.Combine(Path.GetTempPath(), "butter-tests-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory dir |> ignore
    Path.Combine(dir, "test.db"), dir

[<Fact>]
let ``LiteDB 往返保存阶段/材料/加盐/分块/样品`` () =
    let db, dir = tempDb ()
    try
        use repo = new BatchRepository(sprintf "Filename=%s;Connection=shared" db)
        let w = world ()
        // 用仓储签发的令牌才被仓储承认为有效
        let ts = repo.IssueToken(Scale)
        let tm = repo.IssueToken(MoistureMeter)
        let tokens = set [ ts; tm ]
        let rdScale = TestFixtures.scale true 100m ts
        let rdMeter = TestFixtures.meter true 14.2m tm

        let mutable b = Rules.createBatch "CR-STO-1" "张操作" 100m rdScale tokens DateTime.Now |> okOrFail
        for st in [ PhaseTransition; ButtermilkDrain; Washing; Salting; Working; Blocking ] do
            b <- Rules.confirmStage b st b.CreamLot "张操作" None DateTime.Now |> okOrFail
        let ev = { EventId = Guid.NewGuid(); Stage = ButtermilkDrain; At = DateTime.Now
                   Material = "酪乳"; Lot = Some "BM"; Source = Some "缸"; Destination = Some "R-2"
                   QuantityKg = Some 18m; ScaleReading = Some { rdScale with Value = 18m; ReadingId = Guid.NewGuid() }; By = "张操作" }
        b <- Rules.logMaterial b ev tokens |> okOrFail
        b <- Rules.addSalt b "S-A" 1.5m rdScale tokens "张操作" DateTime.Now |> okOrFail
        b <- Rules.cutBlock b 80m "L-1" (Some rdScale) tokens "张操作" DateTime.Now |> okOrFail
        b <- Rules.recordSample b 1 { Layer = Core; Radial = Center } 8m 12m "李质检" DateTime.Now |> okOrFail
        let sid = b.Samples.Head.SampleId
        b <- Rules.publishResult b sid 14.2m rdMeter 1.5m rdScale tokens "李质检" DateTime.Now |> okOrFail
        repo.Save b

        use repo2 = new BatchRepository(sprintf "Filename=%s;Connection=shared" db)
        let loaded = repo2.Load b.BatchId |> Option.get
        Assert.Equal("CR-STO-1", loaded.CreamLot)
        Assert.Equal(Blocking, loaded.CurrentStage)
        Assert.Single(loaded.Materials |> List.filter (fun m -> m.Material = "酪乳")) |> ignore
        Assert.Equal(Some "R-2", loaded.Materials.Head.Destination)
        Assert.Equal(1, loaded.SaltAdditions.Length)
        Assert.Equal("S-A", loaded.SaltAdditions.Head.SaltLot)
        Assert.Equal(1, loaded.Blocks.Length)
        Assert.Equal("L-1", loaded.Blocks.Head.LabelId)
        Assert.Equal(Some 14.2m, loaded.Samples.Head.MoisturePct)
        Assert.Equal(Some 1.5m, loaded.Samples.Head.SaltPct)
        Assert.True loaded.Samples.Head.ResultPublishedAt.IsSome
        // 重建后稳定读数仍可用作规则校验输入
        Assert.Equal(Ok(), Rules.checkLabels loaded)
    finally
        try Directory.Delete(dir, true) with _ -> ()

[<Fact>]
let ``仓储令牌：未授权/撤销令牌无效`` () =
    let db, dir = tempDb ()
    try
        use repo = new BatchRepository(sprintf "Filename=%s;Connection=shared" db)
        let t = repo.IssueToken(Scale)
        Assert.True(repo.IsTokenValid(t, Scale))
        Assert.False(repo.IsTokenValid(Guid.NewGuid(), Scale))
        Assert.True(repo.RevokeToken t)
        Assert.False(repo.IsTokenValid(t, Scale))
    finally
        try Directory.Delete(dir, true) with _ -> ()

[<Fact>]
let ``LiteDB 往返保存返工工单/外来返工料/标签脱落与块状态`` () =
    let db, dir = tempDb ()
    try
        use repo = new BatchRepository(sprintf "Filename=%s;Connection=shared" db)
        let w = world ()
        let ts = repo.IssueToken(Scale)
        let tm = repo.IssueToken(MoistureMeter)
        let tokens = set [ ts; tm ]
        let rdScale = TestFixtures.scale true 100m ts
        let rdMeter = TestFixtures.meter true 14.2m tm

        let mutable src = Rules.createBatch "CR-RW-S" "张操作" 100m rdScale tokens DateTime.Now |> okOrFail
        for st in [ PhaseTransition; ButtermilkDrain; Washing; Salting; Working; Blocking ] do
            src <- Rules.confirmStage src st src.CreamLot "张操作" None DateTime.Now |> okOrFail
        let ev = { EventId = Guid.NewGuid(); Stage = ButtermilkDrain; At = DateTime.Now
                   Material = "酪乳"; Lot = Some "BM"; Source = Some "缸"; Destination = Some "R-2"
                   QuantityKg = Some 18m; ScaleReading = Some { rdScale with Value = 18m; ReadingId = Guid.NewGuid() }; By = "张操作" }
        src <- Rules.logMaterial src ev tokens |> okOrFail
        src <- Rules.addSalt src "S-A" 1.5m rdScale tokens "张操作" DateTime.Now |> okOrFail
        src <- Rules.cutBlock src 28m "L-1" (Some { rdScale with Value = 28m; ReadingId = Guid.NewGuid() }) tokens "张操作" DateTime.Now |> okOrFail
        src <- Rules.cutBlock src 28m "L-2" (Some { rdScale with Value = 28m; ReadingId = Guid.NewGuid() }) tokens "张操作" DateTime.Now |> okOrFail
        src <- Rules.cutBlock src 27.5m "L-3" (Some { rdScale with Value = 27.5m; ReadingId = Guid.NewGuid() }) tokens "张操作" DateTime.Now |> okOrFail
        // 另一批（目标批），独立批次标识
        let mutable tgt = Rules.createBatch "CR-RW-T" "王操作" 100m rdScale tokens DateTime.Now |> okOrFail
        for st in [ PhaseTransition; ButtermilkDrain; Washing; Salting; Working; Blocking ] do
            tgt <- Rules.confirmStage tgt st tgt.CreamLot "王操作" None DateTime.Now |> okOrFail
        let ev2 = { ev with EventId = Guid.NewGuid() }
        tgt <- Rules.logMaterial tgt ev2 tokens |> okOrFail
        tgt <- Rules.addSalt tgt "S-A" 1.5m rdScale tokens "王操作" DateTime.Now |> okOrFail
        tgt <- Rules.cutBlock tgt 28m "T-1" (Some { rdScale with Value = 28m; ReadingId = Guid.NewGuid() }) tokens "王操作" DateTime.Now |> okOrFail
        tgt <- Rules.cutBlock tgt 28m "T-2" (Some { rdScale with Value = 28m; ReadingId = Guid.NewGuid() }) tokens "王操作" DateTime.Now |> okOrFail
        tgt <- Rules.cutBlock tgt 27.5m "T-3" (Some { rdScale with Value = 27.5m; ReadingId = Guid.NewGuid() }) tokens "王操作" DateTime.Now |> okOrFail
        // 指定源批 1 号块跨批返工并合并到目标批、在目标批重新分块
        let src', rid =
            Rules.designateRework src tgt.BatchId [ (1, None, None) ] "复检异常" "李质检" DateTime.Now |> okOrFail
        src <- src'
        let src'', tgt' =
            Rules.mergeRework src tgt rid [ 1, 28m, { rdScale with Value = 28m; ReadingId = Guid.NewGuid() } ] tokens "张操作" DateTime.Now
            |> okOrFail
        src <- src''
        tgt <- tgt'
        tgt <- Rules.recutReworkBlocks tgt rid [ 28m, "RW-1", { rdScale with Value = 28m; ReadingId = Guid.NewGuid() } ] tokens "张操作" DateTime.Now
               |> okOrFail
        // 软化中标签脱落再补贴
        src <- Rules.reportLabelDetached src 2 None None "李质检" DateTime.Now |> okOrFail
        src <- Rules.reportLabelDetached src 2 (Some "L-2R") None "李质检" DateTime.Now |> okOrFail
        repo.Save src
        repo.Save tgt

        use repo2 = new BatchRepository(sprintf "Filename=%s;Connection=shared" db)
        let loadedSrc = repo2.Load src.BatchId |> Option.get
        let loadedTgt = repo2.Load tgt.BatchId |> Option.get
        // 源批：冻结原块、标签事件、跨批送出工单
        Assert.Equal(ReworkedOut, (loadedSrc.Blocks |> List.find (fun b -> b.BlockNo = 1)).Status)
        Assert.Equal(2, loadedSrc.LabelEvents.Length)
        Assert.Equal("L-2R", (loadedSrc.Blocks |> List.find (fun b -> b.BlockNo = 2)).LabelId)
        let order = loadedSrc.ReworkOrders |> List.find (fun o -> o.ReworkId = rid)
        Assert.True(order.Items.Head.MergedAt.IsSome)
        Assert.Equal(28m, order.Items.Head.MergeScaleReading.Value.Value)
        // 目标批：外来返工料谱系边 + 重新分块溯源
        Assert.Equal(28m, loadedTgt.IncomingReworks.Head.QuantityKg)
        Assert.Equal(src.BatchId, loadedTgt.IncomingReworks.Head.SourceBatchId)
        let rw = loadedTgt.Blocks |> List.find (fun b -> b.LabelId = "RW-1")
        Assert.Equal(Some rid, rw.ReworkId)
        Assert.Equal(Ok(), Rules.checkLabels loadedSrc)
        Assert.Equal(Ok(), Rules.checkLabels loadedTgt)
    finally
        try Directory.Delete(dir, true) with _ -> ()
