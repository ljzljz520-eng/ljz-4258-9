module ButterBatch.Tests.RulesTests

open System
open Xunit
open ButterBatch.Domain
open ButterBatch.Tests.TestFixtures

// ---------- 批次创建与台秤稳定标志 ----------

[<Fact>]
let ``创建批次必须使用稳定且授权的台秤读数`` () =
    let w = world ()
    let r = Rules.createBatch "CR-001" "张操作" 100m w.ScaleStable w.Tokens DateTime.Now
    Assert.True(match r with Ok _ -> true | _ -> false)

[<Fact>]
let ``台秤稳定标志丢失时不能创建批次`` () =
    let w = world ()
    let r = Rules.createBatch "CR-001" "张操作" 100m w.ScaleUnstable w.Tokens DateTime.Now
    Assert.True(match r with Error(ReadingNotStable Scale) -> true | _ -> false)

[<Fact>]
let ``未授权读数（伪造令牌）被拒收`` () =
    let w = world ()
    let forged = { w.ScaleStable with Token = Guid.NewGuid() }
    let r = Rules.createBatch "CR-001" "张操作" 100m forged w.Tokens DateTime.Now
    Assert.True(match r with Error(UnauthorizedReading Scale) -> true | _ -> false)

// ---------- 阶段顺序 ----------

[<Fact>]
let ``阶段不能回退`` () =
    let w = world ()
    let b = Rules.createBatch "CR-001" "张操作" 100m w.ScaleStable w.Tokens DateTime.Now |> okOrFail
    let b = Rules.confirmStage b ButtermilkDrain b.CreamLot "张操作" None DateTime.Now |> okOrFail
    let r = Rules.confirmStage b PhaseTransition b.CreamLot "张操作" None DateTime.Now
    Assert.True(match r with Error(StageOutOfOrder _) -> true | _ -> false)

[<Fact>]
let ``奶油批不一致时不能确认阶段`` () =
    let w = world ()
    let b = Rules.createBatch "CR-001" "张操作" 100m w.ScaleStable w.Tokens DateTime.Now |> okOrFail
    let r = Rules.confirmStage b PhaseTransition "CR-OTHER" "张操作" None DateTime.Now
    Assert.True(match r with Error(Conflict _) -> true | _ -> false)

// ---------- 酪乳排净 ----------

[<Fact>]
let ``未排酪乳不能进入发布检查`` () =
    let w = world ()
    let b = Rules.createBatch "CR-001" "张操作" 100m w.ScaleStable w.Tokens DateTime.Now |> okOrFail
    let b = advanceToBlocking w b
    Assert.Equal(Error ButtermilkNotDrained, Rules.checkButtermilkDrained b)

[<Fact>]
let ``酪乳去向未登记视为未排净`` () =
    let w = world ()
    let b = Rules.createBatch "CR-001" "张操作" 100m w.ScaleStable w.Tokens DateTime.Now |> okOrFail
    let b = advanceToBlocking w b
    let b = drainButtermilk w 18m None b
    Assert.Equal(Error ButtermilkNotDrained, Rules.checkButtermilkDrained b)

// ---------- 加盐分两次 ----------

[<Fact>]
let ``加盐必须在加盐阶段且带盐批号`` () =
    let w = world ()
    let b = Rules.createBatch "CR-001" "张操作" 100m w.ScaleStable w.Tokens DateTime.Now |> okOrFail
    let r = Rules.addSalt b "" 1m w.ScaleStable w.Tokens "张操作" DateTime.Now
    Assert.True(match r with Error(OperationNotAllowedInStage("加盐", PhaseTransition)) -> true | _ -> false)

[<Fact>]
let ``两次加盐次序连续且各自带材料批号`` () =
    let w = world ()
    let mutable b = Rules.createBatch "CR-001" "张操作" 100m w.ScaleStable w.Tokens DateTime.Now |> okOrFail
    for st in [ ButtermilkDrain; Washing; Salting ] do
        b <- Rules.confirmStage b st b.CreamLot "张操作" None DateTime.Now |> okOrFail
    b <- Rules.addSalt b "SALT-A" 0.9m { w.ScaleStable with Value = 0.9m; ReadingId = Guid.NewGuid() } w.Tokens "张操作" DateTime.Now |> okOrFail
    b <- Rules.addSalt b "SALT-B" 0.6m { w.ScaleStable with Value = 0.6m; ReadingId = Guid.NewGuid() } w.Tokens "张操作" DateTime.Now |> okOrFail
    Assert.Equal(2, b.SaltAdditions.Length)
    Assert.Equal<string>(ResizeArray [ "SALT-A"; "SALT-B" ], ResizeArray (b.SaltAdditions |> List.map (fun s -> s.SaltLot)))

// ---------- 取样位置与覆盖 ----------

[<Fact>]
let ``只能在冻结位置集合内取样`` () =
    let w = world ()
    let b = Rules.createBatch "CR-001" "张操作" 100m w.ScaleStable w.Tokens DateTime.Now |> okOrFail
    // 冻结集合只有 表/中/芯 × 中心/边缘；任意“第4层”无法用类型构造，
    // 用集合成员校验直接验证
    Assert.DoesNotContain({ Layer = Middle; Radial = Center }, Positions.surface)
    Assert.Contains({ Layer = Core; Radial = Edge }, Positions.frozen)

[<Fact>]
let ``表面样不能代表整块均匀性`` () =
    let surfaceOnly =
        [ { SampleId = Guid.NewGuid(); BlockNo = 1
            Position = { Layer = Surface; Radial = Center }
            TemperatureC = 8m; SampledAt = DateTime.Now; SampledBy = "李质检"
            MoisturePct = Some 14m; SaltPct = Some 1.5m
            MoistureReading = None; SaltReading = None
            ResultPublishedAt = Some DateTime.Now; ResultPublishedBy = Some "李质检" }
          { SampleId = Guid.NewGuid(); BlockNo = 1
            Position = { Layer = Surface; Radial = Edge }
            TemperatureC = 8m; SampledAt = DateTime.Now; SampledBy = "李质检"
            MoisturePct = Some 15m; SaltPct = Some 1.6m
            MoistureReading = None; SaltReading = None
            ResultPublishedAt = Some DateTime.Now; ResultPublishedBy = Some "李质检" } ]
    Assert.Equal(Error SurfaceSamplesOnly, Rules.coverageFor surfaceOnly)

[<Fact>]
let ``中芯层 + 中心/边缘覆盖通过`` () =
    let samples =
        [ for layer in [ Surface; Middle; Core ] do
              for radial in [ Center; Edge ] ->
                  { SampleId = Guid.NewGuid(); BlockNo = 1
                    Position = { Layer = layer; Radial = radial }
                    TemperatureC = 8m; SampledAt = DateTime.Now; SampledBy = "李质检"
                    MoisturePct = Some 14m; SaltPct = Some 1.5m
                    MoistureReading = None; SaltReading = None
                    ResultPublishedAt = Some DateTime.Now; ResultPublishedBy = Some "李质检" } ]
    Assert.Equal(Ok(), Rules.coverageFor samples)

// ---------- 样品受热软化 ----------

[<Fact>]
let ``样品温度超过拒收限判为受热软化`` () =
    let w = world ()
    let mutable b = Rules.createBatch "CR-001" "张操作" 100m w.ScaleStable w.Tokens DateTime.Now |> okOrFail
    b <- advanceToBlocking w b
    b <- Rules.cutBlock b 25m "L-1" (Some w.ScaleStable) w.Tokens "张操作" DateTime.Now |> okOrFail
    let r = Rules.recordSample b 1 { Layer = Core; Radial = Center } 15.0m 12.0m "李质检" DateTime.Now
    Assert.True(match r with Error(SampleTooWarm _) -> true | _ -> false)

// ---------- 标签互换 ----------

[<Fact>]
let ``分块标签重复即判标签互换`` () =
    let w = world ()
    let mutable b = Rules.createBatch "CR-001" "张操作" 100m w.ScaleStable w.Tokens DateTime.Now |> okOrFail
    b <- advanceToBlocking w b
    b <- Rules.cutBlock b 25m "L-1" (Some w.ScaleStable) w.Tokens "张操作" DateTime.Now |> okOrFail
    let r = Rules.cutBlock b 25m "L-1" (Some w.ScaleStable) w.Tokens "张操作" DateTime.Now
    Assert.True(match r with Error(LabelMismatch _) -> true | _ -> false)

// ---------- 物料闭合 ----------

[<Fact>]
let ``投入产出在容差内闭合`` () =
    let w = world ()
    let b = TestFixtures.closedBatch w
    Assert.Equal(Ok(), Rules.checkMaterialClosure b Rules.ClosureConfig.Default)

[<Fact>]
let ``差异超容差判物料不闭合`` () =
    let w = world ()
    let b = TestFixtures.closedBatch w
    // 人为把产品块总重改小（模拟丢失/未登记）
    let b' = { b with Blocks = b.Blocks |> List.map (fun x -> if x.BlockNo = 1 then { x with WeightKg = 1m } else x) }
    Assert.True(match Rules.checkMaterialClosure b' Rules.ClosureConfig.Default with
                | Error(MaterialNotClosed _) -> true | _ -> false)

// ---------- 离散统计 ----------

[<Fact>]
let ``多点水分离散（均值/极差/标准差）`` () =
    let samples =
        [ 14.0m; 14.5m; 15.0m; 13.5m ]
        |> List.mapi (fun i v ->
            { SampleId = Guid.NewGuid(); BlockNo = 1
              Position = Positions.frozen.[i]
              TemperatureC = 8m; SampledAt = DateTime.Now; SampledBy = "李质检"
              MoisturePct = Some v; SaltPct = Some 1.5m
              MoistureReading = None; SaltReading = None
              ResultPublishedAt = Some DateTime.Now; ResultPublishedBy = Some "李质检" })
    let d = Rules.moistureDispersion samples |> Option.get
    Assert.Equal(4, d.Count)
    Assert.Equal(14.25m, d.Mean)
    Assert.Equal(1.5m, d.Range)
    Assert.True(d.StdDev > 0.5m)

[<Fact>]
let ``阶段不允许跨阶段跳跃（保证材料与阶段对应）`` () =
    let w = world ()
    let b = Rules.createBatch "CR-001" "张操作" 100m w.ScaleStable w.Tokens DateTime.Now |> okOrFail
    // 从奶油相转变直接跳到加盐，跳过排酪乳/洗涤，必须拒绝
    let r = Rules.confirmStage b Salting b.CreamLot "张操作" None DateTime.Now
    Assert.True(match r with Error(Conflict _) -> true | _ -> false)
