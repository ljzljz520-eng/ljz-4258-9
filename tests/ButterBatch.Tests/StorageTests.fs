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
                   QuantityKg = Some 18m; ScaleReading = Some { rdScale with Value = 18m }; By = "张操作" }
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
