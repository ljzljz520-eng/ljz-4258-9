// ============================================================================
// ButterBatch.Domain / Rules.fs
// 批次与样品规则（纯函数、可单元测试）。
// 职责：阶段对应、酪乳去向、加盐材料、多点水分、物料闭合、样品覆盖。
// 刻意不提供任何配方与搅拌参数。
// ============================================================================
namespace ButterBatch.Domain

open System

[<RequireQualifiedAccess>]
module Rules =

    // ---------- 读数校验 ----------

    /// 校验经桥接层接收的设备读数：必须稳定且带有效授权令牌
    let validateReading (issuedTokens: Guid Set) (r: DeviceReading) : Result<DeviceReading, RuleError> =
        if r.Token = Guid.Empty || not (issuedTokens.Contains r.Token) then
            Error(UnauthorizedReading r.Kind)
        elif not r.Stable then
            Error(ReadingNotStable r.Kind)
        else Ok r

    // ---------- 批次创建 ----------

    /// 操作员确认奶油批并创建批次（奶油净重必须来自稳定台秤读数）
    let createBatch (creamLot: string) (operatorName: string)
                    (creamInKg: decimal) (reading: DeviceReading)
                    (issuedTokens: Guid Set) (at: DateTime)
                    : Result<ButterBatch, RuleError> =
        if String.IsNullOrWhiteSpace creamLot then Error(Conflict "奶油批号不能为空")
        elif String.IsNullOrWhiteSpace operatorName then Error(Conflict "操作员姓名不能为空")
        elif creamInKg <= 0m then Error(Conflict "奶油投料净重必须大于 0")
        elif reading.Kind <> Scale then Error(ReadingMissing Scale)
        else
            match validateReading issuedTokens reading with
            | Error e -> Error e
            | Ok r ->
                { BatchId = Guid.NewGuid()
                  CreamLot = creamLot.Trim()
                  StartedAt = at
                  OperatorName = operatorName.Trim()
                  CreamInKg = creamInKg
                  CreamScaleReading = r
                  Confirmations = []
                  Materials = []
                  SaltAdditions = []
                  Blocks = []
                  Samples = []
                  CurrentStage = PhaseTransition
                  Closed = false } |> Ok

    // ---------- 阶段确认 ----------

    /// 阶段顺序表（加盐可重复确认/投料，其余阶段单调推进）
    let private stageOrder =
        function
        | PhaseTransition -> 0
        | ButtermilkDrain -> 1
        | Washing -> 2
        | Salting -> 3
        | Working -> 4
        | Blocking -> 5

    /// 操作员确认“实际”阶段。阶段只能相邻推进或重复确认当前阶段
    /// （加盐可重复确认以支持分次加盐）；不允许跨阶段跳跃，以保证材料事件与阶段一一对应。
    let confirmStage (batch: ButterBatch) (stage: Stage) (creamLot: string)
                     (by: string) (note: string option) (at: DateTime)
                     : Result<ButterBatch, RuleError> =
        if batch.Closed then Error(Conflict "批次已关闭，不能再确认阶段")
        elif creamLot <> batch.CreamLot then
            Error(Conflict(sprintf "奶油批不一致：确认的是 %s，批次为 %s" creamLot batch.CreamLot))
        else
            let cur = stageOrder batch.CurrentStage
            let next = stageOrder stage
            if next < cur then
                Error(StageOutOfOrder(stage, batch.CurrentStage))
            elif next > cur + 1 then
                Error(Conflict(sprintf "不能从「%s」直接跳到「%s」，请按顺序确认中间阶段"
                                         (string batch.CurrentStage) (string stage)))
            else
                let conf =
                    { Stage = stage; CreamLot = creamLot; ConfirmedAt = at
                      ConfirmedBy = by; Note = note }
                // 进入新阶段时推进 CurrentStage；重复确认不改变阶段
                let newStage = if next > cur then stage else batch.CurrentStage
                Ok { batch with Confirmations = batch.Confirmations @ [ conf ]; CurrentStage = newStage }

    // ---------- 材料事件（温度/去向/读数对应） ----------

    /// 登记材料事件。阶段必须匹配；数量若提供必须有稳定台秤读数；
    /// 排酪乳必须登记去向；所有材料事件与“实际阶段”一一对应。
    let logMaterial (batch: ButterBatch) (ev: MaterialEvent)
                    (issuedTokens: Guid Set) : Result<ButterBatch, RuleError> =
        let stageMatches =
            batch.Confirmations |> List.exists (fun c -> c.Stage = ev.Stage)
            || batch.CurrentStage = ev.Stage
        if not stageMatches then
            Error(OperationNotAllowedInStage(sprintf "登记%s材料" ev.Material, ev.Stage))
        elif String.IsNullOrWhiteSpace ev.Material then Error(Conflict "材料名称不能为空")
        else
            match ev.QuantityKg, ev.ScaleReading with
            | Some q, _ when q <= 0m -> Error(Conflict "材料数量必须大于 0")
            | Some _, Some r ->
                match validateReading issuedTokens r with
                | Error e -> Error e
                | Ok _ -> Ok { batch with Materials = batch.Materials @ [ ev ] }
            | Some _, None -> Error(ReadingMissing Scale)
            | None, Some r ->
                match validateReading issuedTokens r with
                | Error e -> Error e
                | Ok _ -> Ok { batch with Materials = batch.Materials @ [ ev ] }
            | None, None -> Ok { batch with Materials = batch.Materials @ [ ev ] }

    // ---------- 排酪乳检查 ----------

    /// 酪乳必须已排净：存在排酪乳阶段的酪乳事件、有去向、有数量读数
    let checkButtermilkDrained (batch: ButterBatch) : Result<unit, RuleError> =
        let hasDrain =
            batch.Materials
            |> List.exists (fun m ->
                m.Stage = ButtermilkDrain
                && m.Material.Contains "酪乳"
                && m.Destination.IsSome
                && (m.Destination.Value |> String.IsNullOrWhiteSpace |> not)
                && m.QuantityKg.IsSome
                && m.ScaleReading.IsSome)
        if hasDrain then Ok() else Error ButtermilkNotDrained

    // ---------- 加盐（可分两次） ----------

    /// 加盐：当前必须为加盐阶段；盐材料批号必填；次序连续；每次稳定台秤读数
    let addSalt (batch: ButterBatch) (saltLot: string) (quantityKg: decimal)
                (reading: DeviceReading) (issuedTokens: Guid Set)
                (by: string) (at: DateTime) : Result<ButterBatch, RuleError> =
        if batch.CurrentStage <> Salting && not (batch.Confirmations |> List.exists (fun c -> c.Stage = Salting)) then
            Error(OperationNotAllowedInStage("加盐", batch.CurrentStage))
        elif String.IsNullOrWhiteSpace saltLot then Error SaltMaterialMissing
        elif quantityKg <= 0m then Error(Conflict "加盐量必须大于 0")
        elif reading.Kind <> Scale then Error(ReadingMissing Scale)
        else
            match validateReading issuedTokens reading with
            | Error e -> Error e
            | Ok r ->
                let ordinal = batch.SaltAdditions.Length + 1
                if batch.SaltAdditions |> List.exists (fun s -> s.Ordinal = ordinal) then
                    Error(SaltOrdinalConflict ordinal)
                else
                    let add =
                        { AddId = Guid.NewGuid(); Ordinal = ordinal; SaltLot = saltLot.Trim()
                          QuantityKg = quantityKg; ScaleReading = r; At = at; By = by }
                    Ok { batch with SaltAdditions = batch.SaltAdditions @ [ add ] }

    // ---------- 分块与标签 ----------

    /// 分块：必须进入分块阶段；标签号在批内唯一（互换检测在 checkLabels）
    let cutBlock (batch: ButterBatch) (weightKg: decimal) (labelId: string)
                 (reading: DeviceReading option) (issuedTokens: Guid Set)
                 (by: string) (at: DateTime) : Result<ButterBatch, RuleError> =
        if batch.CurrentStage <> Blocking then
            Error(OperationNotAllowedInStage("分块", batch.CurrentStage))
        elif weightKg <= 0m then Error(Conflict "块重必须大于 0")
        elif String.IsNullOrWhiteSpace labelId then Error(Conflict "标签号不能为空")
        elif batch.Blocks |> List.exists (fun b -> b.LabelId = labelId.Trim()) then
            let first = batch.Blocks |> List.find (fun b -> b.LabelId = labelId.Trim())
            Error(LabelMismatch(batch.Blocks.Length + 1, labelId.Trim(), first.BlockNo))
        else
            match reading with
            | Some r ->
                match validateReading issuedTokens r with
                | Error e -> Error e
                | Ok r -> Ok r
            | None -> Error(ReadingMissing Scale)
            |> function
                | Error e -> Error e
                | Ok r ->
                    let block =
                        { BlockNo = batch.Blocks.Length + 1; WeightKg = weightKg
                          LabelId = labelId.Trim(); ScaleReading = Some r; At = at; By = by }
                    Ok { batch with Blocks = batch.Blocks @ [ block ] }

    /// 标签互换检测：每个标签只能出现在首次绑定的块上
    let checkLabels (batch: ButterBatch) : Result<unit, RuleError> =
        let rec scan seen = function
            | [] -> Ok()
            | b :: rest when Map.containsKey b.LabelId seen ->
                Error(LabelMismatch(b.BlockNo, b.LabelId, Map.find b.LabelId seen))
            | b :: rest -> scan (Map.add b.LabelId b.BlockNo seen) rest
        scan Map.empty batch.Blocks

    // ---------- 取样与结果 ----------

    /// 质量人员在冻结位置取样。样品温度超拒收限即视为受热软化拒收。
    let recordSample (batch: ButterBatch) (blockNo: int) (position: SamplePosition)
                     (temperatureC: decimal) (maxSampleTempC: decimal)
                     (by: string) (at: DateTime) : Result<ButterBatch, RuleError> =
        if not (Positions.frozen |> List.contains position) then
            Error(PositionNotFrozen position)
        elif not (batch.Blocks |> List.exists (fun b -> b.BlockNo = blockNo)) then
            Error(Conflict(sprintf "%d号块不存在" blockNo))
        elif temperatureC > maxSampleTempC then
            Error(SampleTooWarm(blockNo, temperatureC, maxSampleTempC))
        else
            let sample =
                { SampleId = Guid.NewGuid(); BlockNo = blockNo; Position = position
                  TemperatureC = temperatureC; SampledAt = at; SampledBy = by
                  MoisturePct = None; SaltPct = None
                  MoistureReading = None; SaltReading = None
                  ResultPublishedAt = None; ResultPublishedBy = None }
            Ok { batch with Samples = batch.Samples @ [ sample ] }

    /// 单个块的覆盖规则：不能只有表层；中层、芯层必须覆盖；内部点中心/边缘均需有。
    let coverageFor (samples: Sample list) : Result<unit, RuleError> =
        let ps = samples |> List.map (fun s -> s.Position)
        if ps.IsEmpty then Error(CoverageMissing "该块没有任何取样记录")
        elif ps |> List.forall (fun p -> p.Layer = Surface) then Error SurfaceSamplesOnly
        else
            let layers = ps |> List.map (fun p -> p.Layer) |> set
            let interior = ps |> List.filter (fun p -> p.Layer <> Surface)
            let radials = interior |> List.map (fun p -> p.Radial) |> set
            let errs =
                [ if not (layers.Contains Middle) then CoverageMissing "缺少中层样品"
                  if not (layers.Contains Core) then CoverageMissing "缺少芯层样品"
                  if interior.Length < 2 then CoverageMissing "内部点位不足（至少中/芯各一点）"
                  if not (radials.Contains Center) then CoverageMissing "内部点缺少中心位置"
                  if not (radials.Contains Edge) then CoverageMissing "内部点缺少边缘位置" ]
            match errs with [] -> Ok() | e :: _ -> Error e

    /// 质量人员发布水盐结果：样品必须存在；水分仪读数必须稳定授权；水分、盐分都要发布。
    let publishResult (batch: ButterBatch) (sampleId: Guid)
                      (moisturePct: decimal) (moistureReading: DeviceReading)
                      (saltPct: decimal) (saltReading: DeviceReading)
                      (issuedTokens: Guid Set) (by: string) (at: DateTime)
                      : Result<ButterBatch, RuleError> =
        match batch.Samples |> List.tryFind (fun s -> s.SampleId = sampleId) with
        | None -> Error(Conflict "样品不存在")
        | Some s when s.ResultPublishedAt.IsSome -> Error(Conflict "该样品结果已发布")
        | Some _ ->
            let rangeChecks =
                [ if moisturePct < 0m || moisturePct > 100m then
                      Some(Conflict "水分结果超出 0–100%")
                  if saltPct < 0m || saltPct > 100m then
                      Some(Conflict "盐分结果超出 0–100%") ]
                |> List.choose id
            let readingChecks =
                [ if moistureReading.Kind <> MoistureMeter then Some(ReadingMissing MoistureMeter)
                  else match validateReading issuedTokens moistureReading with Error e -> Some e | Ok _ -> None
                  if saltReading.Kind <> Scale then Some(ReadingMissing Scale)
                  else match validateReading issuedTokens saltReading with Error e -> Some e | Ok _ -> None ]
                |> List.choose id
            match rangeChecks @ readingChecks with
            | c :: _ -> Error c
            | [] ->
                let samples =
                    batch.Samples
                    |> List.map (fun x ->
                        if x.SampleId = sampleId then
                            { x with MoisturePct = Some moisturePct
                                     SaltPct = Some saltPct
                                     MoistureReading = Some moistureReading
                                     SaltReading = Some saltReading
                                     ResultPublishedAt = Some at
                                     ResultPublishedBy = Some by }
                        else x)
                Ok { batch with Samples = samples }

    // ---------- 物料闭合 ----------

    type ClosureConfig =
        { /// 绝对容差 kg
          AbsTolKg: decimal
          /// 相对容差（占总投入比例）
          RelTol: decimal }
        static member Default = { AbsTolKg = 2.0m; RelTol = 0.015m }

    /// 物料闭合：奶油 + 盐（投入） ≈ 产品块总量 + 酪乳排出（产出/分出）。
    /// 洗涤水作为排出去的介质记录，不计入黄油质量闭合。
    let checkMaterialClosure (batch: ButterBatch) (cfg: ClosureConfig) : Result<unit, RuleError> =
        match checkButtermilkDrained batch with
        | Error e -> Error e
        | Ok () ->
            let buttermilkKg =
                batch.Materials
                |> List.filter (fun m -> m.Stage = ButtermilkDrain && m.Material.Contains "酪乳")
                |> List.sumBy (fun m -> defaultArg m.QuantityKg 0m)
            let saltKg = batch.SaltAdditions |> List.sumBy (fun s -> s.QuantityKg)
            let butterKg = batch.Blocks |> List.sumBy (fun b -> b.WeightKg)
            if batch.Blocks.IsEmpty then Error(BatchNotComplete "尚未分块，无法做物料闭合")
            else
                let input = batch.CreamInKg + saltKg
                let output = butterKg + buttermilkKg
                let diff = input - output
                let tol = max cfg.AbsTolKg (input * cfg.RelTol)
                if abs diff <= tol then Ok()
                else
                    Error(MaterialNotClosed(
                        sprintf "投入 %.2f kg（奶油 %.2f + 盐 %.2f），分出 %.2f kg（产品 %.2f + 酪乳 %.2f），差异 %.2f kg 超过容差 %.2f kg"
                            input batch.CreamInKg saltKg output butterKg buttermilkKg diff tol))

    // ---------- 批内点位离散 ----------

    type Dispersion =
        { Count: int; Mean: decimal; Min: decimal; Max: decimal
          Range: decimal; StdDev: decimal; Values: (SamplePosition * decimal) list }

    let private dispersion (pairs: (SamplePosition * decimal) list) : Dispersion option =
        match pairs with
        | [] -> None
        | xs ->
            let vals: decimal list = xs |> List.map snd
            let n = decimal vals.Length
            let sum = vals |> List.sum
            let mean = sum / n
            let variance = (vals |> List.sumBy (fun (v: decimal) -> let d = v - mean in d * d)) / n
            { Count = int n; Mean = mean
              Min = List.min vals; Max = List.max vals
              Range = List.max vals - List.min vals
              StdDev = sqrt (max 0.0 (float variance)) |> decimal
              Values = xs } |> Some

    /// 某块（或整批）水分点位离散，供 OxyPlot 展示
    let moistureDispersion (samples: Sample list) : Dispersion option =
        samples
        |> List.choose (fun s -> s.MoisturePct |> Option.map (fun m -> s.Position, m))
        |> dispersion

    /// 盐分点位离散
    let saltDispersion (samples: Sample list) : Dispersion option =
        samples
        |> List.choose (fun s -> s.SaltPct |> Option.map (fun v -> s.Position, v))
        |> dispersion

    // ---------- 发布前总检查 ----------

    /// 系统在质量人员发布水盐结果前执行的完整检查：
    /// 阶段走完、酪乳排净、分块完成、标签无互换、每块覆盖充分、结果齐全、物料闭合。
    let readyToRelease (batch: ButterBatch) (cfg: ClosureConfig) : Result<unit, RuleError list> =
        let stageErrs =
            [ if batch.CurrentStage <> Blocking then
                  BatchNotComplete(sprintf "尚未走完分块阶段（当前：%s）" (string batch.CurrentStage))
              if batch.Blocks.IsEmpty then BatchNotComplete "没有产品块" ]
        let drainErrs =
            match checkButtermilkDrained batch with Error e -> [ e ] | Ok _ -> []
        let labelErrs =
            match checkLabels batch with Error e -> [ e ] | Ok _ -> []
        let coverageErrs =
            [ for b in batch.Blocks do
                  let bs = batch.Samples |> List.filter (fun s -> s.BlockNo = b.BlockNo)
                  match coverageFor bs with
                  | Error e -> e
                  | Ok _ ->
                      for s in bs do
                          if s.MoisturePct.IsNone || s.SaltPct.IsNone then
                              ResultNotReady s.Position ]
        let closureErrs =
            match checkMaterialClosure batch cfg with Error e -> [ e ] | Ok _ -> []
        let errs = stageErrs @ drainErrs @ labelErrs @ coverageErrs @ closureErrs |> List.distinct
        match errs with [] -> Ok() | es -> Error es
