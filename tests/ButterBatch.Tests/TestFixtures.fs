module ButterBatch.Tests.TestFixtures

open System
open ButterBatch.Domain

let scale stable value token : DeviceReading =
    { Kind = Scale; Value = value; Stable = stable
      At = DateTime(2026, 9, 11, 9, 0, 0); Token = token; Raw = sprintf "ST,gs,%f" value }

let meter stable value token : DeviceReading =
    { Kind = MoistureMeter; Value = value; Stable = stable
      At = DateTime(2026, 9, 11, 9, 30, 0); Token = token; Raw = sprintf "OK,m,%f" value }

type World =
    { Tokens: Guid Set
      ScaleStable: DeviceReading
      ScaleUnstable: DeviceReading
      MeterStable: DeviceReading
      MeterUnstable: DeviceReading }

let world () =
    let ts = Guid.NewGuid()
    let tm = Guid.NewGuid()
    { Tokens = set [ ts; tm ]
      ScaleStable = scale true 100.0m ts
      ScaleUnstable = scale false 100.0m ts
      MeterStable = meter true 14.5m tm
      MeterUnstable = meter false 14.5m tm }

/// 走完 奶油相转变->排酪乳->洗涤->加盐->捏合->分块 前置阶段
let advanceToBlocking (w: World) (b: ButterBatch) : ButterBatch =
    let stages =
        [ PhaseTransition, ("奶油", None)
          ButtermilkDrain, ("酪乳", Some 18.0m)
          Washing, ("洗涤水", None)
          Salting, ("盐", Some 1.5m)
          Working, (null, None)
          Blocking, (null, None) ]
    (b, stages) ||> List.fold (fun b (st, _) ->
        match Rules.confirmStage b st b.CreamLot "张操作" None DateTime.Now with Ok b' -> b' | Error e -> failwith (string e))

/// 登记排酪乳事件（去向必填）
let drainButtermilk (w: World) (qty: decimal) (dest: string option) b =
    let ev =
        { EventId = Guid.NewGuid(); Stage = ButtermilkDrain; At = DateTime.Now
          Material = "酪乳"; Lot = Some "BM-1"; Source = Some "搅拌缸"
          Destination = dest
          QuantityKg = Some qty
          ScaleReading = Some { w.ScaleStable with Value = qty }
          By = "张操作" }
    match Rules.logMaterial b ev w.Tokens with Ok b' -> b' | Error e -> failwith (string e)

/// 构造一个走完各阶段、物料闭合、多点取样齐全的“正常批”
let closedBatch (w: World) : ButterBatch =
    let mutable b =
        Rules.createBatch "CR-777" "张操作" 100m w.ScaleStable w.Tokens DateTime.Now |> okOrFail
    // 确认阶段
    for st in [ PhaseTransition; ButtermilkDrain; Washing; Salting; Working; Blocking ] do
        b <- Rules.confirmStage b st b.CreamLot "张操作" None DateTime.Now |> okOrFail
    // 酪乳排净，去向 = 酪乳回收罐 R-2，18kg
    b <- drainButtermilk w 18m (Some "酪乳回收罐 R-2") b
    // 加盐两次
    b <- Rules.addSalt b "SALT-A" 0.9m { w.ScaleStable with Value = 0.9m } w.Tokens "张操作" DateTime.Now |> okOrFail
    b <- Rules.addSalt b "SALT-B" 0.6m { w.ScaleStable with Value = 0.6m } w.Tokens "张操作" DateTime.Now |> okOrFail
    // 分块：投入 100 + 1.5 = 101.5；产出 83.5 + 酪乳 18 = 101.5
    b <- Rules.cutBlock b 28.0m "L-1" (Some { w.ScaleStable with Value = 28.0m }) w.Tokens "张操作" DateTime.Now |> okOrFail
    b <- Rules.cutBlock b 28.0m "L-2" (Some { w.ScaleStable with Value = 28.0m }) w.Tokens "张操作" DateTime.Now |> okOrFail
    b <- Rules.cutBlock b 27.5m "L-3" (Some { w.ScaleStable with Value = 27.5m }) w.Tokens "张操作" DateTime.Now |> okOrFail
    // 每块 6 个冻结点位取样（温度合格），并发布水盐结果
    for block in b.Blocks do
        for pos in Positions.frozen do
            b <- Rules.recordSample b block.BlockNo pos 8.0m 12.0m "李质检" DateTime.Now |> okOrFail
        let ids = b.Samples |> List.filter (fun s -> s.BlockNo = block.BlockNo) |> List.map (fun s -> s.SampleId)
        for sid in ids do
            b <- Rules.publishResult b sid 14.2m w.MeterStable 1.5m w.ScaleStable w.Tokens "李质检" DateTime.Now |> okOrFail
    b

