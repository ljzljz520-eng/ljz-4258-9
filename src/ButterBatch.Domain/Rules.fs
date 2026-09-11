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
                  ReworkOrders = []
                  IncomingReworks = []
                  LabelEvents = []
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

    let private findBlock (batch: ButterBatch) (blockNo: int) : ProductBlock option =
        batch.Blocks |> List.tryFind (fun b -> b.BlockNo = blockNo)

    let private mapBlock (batch: ButterBatch) (blockNo: int) (f: ProductBlock -> ProductBlock) : ButterBatch =
        { batch with Blocks = batch.Blocks |> List.map (fun b -> if b.BlockNo = blockNo then f b else b) }

    /// 已在软化中脱落的物理标签（不得重新流通）
    let detachedLabels (batch: ButterBatch) : string Set =
        batch.LabelEvents |> List.choose (fun e -> e.OldLabelId) |> set

    /// 当前仍在块上的标签
    let activeLabels (batch: ButterBatch) : string Set =
        batch.Blocks |> List.map (fun b -> b.LabelId) |> set

    /// 分块：必须进入分块阶段；标签号在批内唯一（互换检测在 checkLabels）；
    /// 已在软化中脱落的物理标签不得重新使用。
    let cutBlock (batch: ButterBatch) (weightKg: decimal) (labelId: string)
                 (reading: DeviceReading option) (issuedTokens: Guid Set)
                 (by: string) (at: DateTime) : Result<ButterBatch, RuleError> =
        if batch.CurrentStage <> Blocking then
            Error(OperationNotAllowedInStage("分块", batch.CurrentStage))
        elif weightKg <= 0m then Error(Conflict "块重必须大于 0")
        elif String.IsNullOrWhiteSpace labelId then Error(Conflict "标签号不能为空")
        elif detachedLabels batch |> Set.contains (labelId.Trim()) then
            Error(LabelReuseDetached(labelId.Trim()))
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
                          LabelId = labelId.Trim(); ScaleReading = Some r; At = at; By = by
                          Status = Intact; StatusChangedAt = None; StatusChangedBy = None
                          StatusReason = None; LabelMissing = false
                          SplitFromBlockNo = None; ReworkId = None }
                    Ok { batch with Blocks = batch.Blocks @ [ block ] }

    /// 标签互换检测：每个在用标签只能出现在首次绑定的块上
    /// （脱落缺标签的块、已返捏合出批的冻结留痕块不参与——物理标签已不在其上）
    let checkLabels (batch: ButterBatch) : Result<unit, RuleError> =
        let rec scan seen = function
            | [] -> Ok()
            | b :: rest when b.LabelMissing || b.Status = ReworkedOut -> scan seen rest
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
        else
            match findBlock batch blockNo with
            | None -> Error(Conflict(sprintf "%d号块不存在" blockNo))
            | Some b when b.LabelMissing -> Error(LabelDetached blockNo)
            | Some b when b.Status = ReworkFrozen || b.Status = ReworkedOut ->
                Error(BlockReworkFrozen blockNo)
            | Some _ when temperatureC > maxSampleTempC ->
                Error(SampleTooWarm(blockNo, temperatureC, maxSampleTempC))
            | Some _ ->
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
        | Some s ->
            match findBlock batch s.BlockNo with
            | Some b when b.Status = ReworkFrozen || b.Status = ReworkedOut ->
                Error(BlockReworkFrozen b.BlockNo)
            | Some b when b.LabelMissing -> Error(LabelDetached b.BlockNo)
            | _ ->
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

    // ---------- 返捏合分支（质量人员指定受影响分块） ----------

    /// 质量人员指定受影响分块，开立返捏合工单（此时即刻冻结原分块与其水盐结果）。
    /// items 为 (源块号, 返工kg option, 剩余保留标签 option)：
    /// 返工kg = None 表示整块返工；部分返工时返工kg < 块重，且必须登记保留标签。
    /// targetBatchId = 本批 Id 表示本批内返捏合；传入另一批 Id 表示返工混入另一批。
    let designateRework (source: ButterBatch) (targetBatchId: Guid)
                        (items: (int * decimal option * string option) list)
                        (reason: string) (by: string) (at: DateTime)
                        : Result<ButterBatch * Guid, RuleError> =
        if source.Closed then Error(Conflict "批次已关闭，不能开立返工工单")
        elif List.isEmpty items then Error(Conflict "必须指定至少一个受影响分块")
        elif String.IsNullOrWhiteSpace reason then Error(Conflict "返工原因必须填写")
        else
            let dup = items |> List.map (fun (n, _, _) -> n) |> List.countBy id |> List.tryFind (fun (_, c) -> c > 1)
            match dup with
            | Some(n, _) -> Error(Conflict(sprintf "%d号块被重复指定" n))
            | None ->
                let rec build (acc: ReworkItem list)
                              (xs: (int * decimal option * string option) list)
                              : Result<ReworkItem list, RuleError> =
                    match xs with
                    | [] -> Ok(List.rev acc)
                    | (blockNo, reworkOpt, remainderLabel) :: rest ->
                        match findBlock source blockNo with
                        | None -> Error(BlockNotFound blockNo)
                        | Some b when b.Status = Released -> Error(BlockAlreadyReleased blockNo)
                        | Some b when b.Status <> Intact -> Error(BlockNotReworkable(blockNo, b.Status))
                        | Some b when b.LabelMissing -> Error(LabelDetached blockNo)
                        | Some b ->
                            let reworkKg = defaultArg reworkOpt b.WeightKg
                            if reworkKg <= 0m || reworkKg > b.WeightKg then
                                Error(ReworkQtyInvalid(blockNo, reworkKg, b.WeightKg))
                            elif reworkKg = b.WeightKg && remainderLabel.IsSome then
                                Error(ReworkRemainderNotAllowed blockNo)
                            elif reworkKg < b.WeightKg
                                 && (remainderLabel.IsNone || String.IsNullOrWhiteSpace remainderLabel.Value) then
                                Error(ReworkRemainderLabelMissing blockNo)
                            elif reworkKg < b.WeightKg then
                                let keep = remainderLabel.Value.Trim()
                                let occupied =
                                    activeLabels source
                                    |> Set.remove b.LabelId
                                    |> Set.union (detachedLabels source)
                                if Set.contains keep occupied then
                                    Error(LabelMismatch(blockNo, keep, blockNo))
                                else
                                    let item =
                                        { SourceBlockNo = blockNo; ReworkKg = reworkKg
                                          RemainderLabelId = Some keep
                                          RemainderBlockNo = None
                                          FrozenWeightKg = None
                                          MergeScaleReading = None
                                          MergedAt = None; IntoOrderId = None }
                                    build (item :: acc) rest
                            else
                                let item =
                                    { SourceBlockNo = blockNo; ReworkKg = reworkKg
                                      RemainderLabelId = None
                                      RemainderBlockNo = None
                                      FrozenWeightKg = None
                                      MergeScaleReading = None
                                      MergedAt = None; IntoOrderId = None }
                                build (item :: acc) rest
                match build [] items with
                | Error e -> Error e
                | Ok reworkItems ->
                    let reworkId = Guid.NewGuid()
                    let frozen =
                        (source, reworkItems)
                        ||> List.fold (fun batch item ->
                            mapBlock batch item.SourceBlockNo (fun b ->
                                { b with Status = ReworkFrozen
                                         StatusChangedAt = Some at
                                         StatusChangedBy = Some by
                                         StatusReason = Some reason }))
                    let order =
                        { ReworkId = reworkId; SourceBatchId = source.BatchId
                          TargetBatchId = targetBatchId
                          DesignatedAt = at; DesignatedBy = by; Reason = reason
                          Items = reworkItems; CompletedAt = None; CompletedBy = None
                          OutputBlockNos = [] }
                    Ok({ frozen with ReworkOrders = frozen.ReworkOrders @ [ order ] }, reworkId)

    /// 在源批侧找到工单
    let private findOrder (batch: ButterBatch) reworkId =
        batch.ReworkOrders |> List.tryFind (fun o -> o.ReworkId = reworkId)

    let private mapOrder (batch: ButterBatch) reworkId f =
        { batch with ReworkOrders = batch.ReworkOrders |> List.map (fun o -> if o.ReworkId = reworkId then f o else o) }

    /// 执行返工合并：解冻软化后的受影响块逐块复称并入。
    /// source = 发起（源）批；target = 接收批（本批返捏合时与 source 是同一对象）；
    /// weights = 每块（实际并入kg, 稳定台秤读数）；实际并入量必须等于指定返工量。
    /// 只返捏合半块时，未返工部分以独立块另立（原块号冻结留痕），原标签保持在剩余部分上。
    let mergeRework (source: ButterBatch) (target: ButterBatch) (reworkId: Guid)
                    (weights: (int * decimal * DeviceReading) list)
                    (issuedTokens: Guid Set) (by: string) (at: DateTime)
                    : Result<ButterBatch * ButterBatch, RuleError> =
        match findOrder source reworkId with
        | None -> Error(ReworkOrderNotFound reworkId)
        | Some order when order.CompletedAt.IsSome -> Error(ReworkAlreadyCompleted reworkId)
        | Some order ->
            let weightMap : Map<int, decimal * DeviceReading> =
                weights |> List.fold (fun m (n, q, rd) -> Map.add n (q, rd) m) Map.empty
            if weights.Length <> order.Items.Length
               || weightMap.Count <> order.Items.Length then
                Error(Conflict "每个受影响分块都必须有唯一的解冻复称与并入读数")
            else
                let rec check (acc: ReworkItem list)
                              (xs: ReworkItem list)
                              : Result<ReworkItem list, RuleError> =
                    match xs with
                    | [] -> Ok(List.rev acc)
                    | item :: rest ->
                        match weightMap.TryFind item.SourceBlockNo with
                        | None -> Error(Conflict(sprintf "缺少%d号块的返出并入记录" item.SourceBlockNo))
                        | Some(qty, rd0) ->
                            if rd0.Kind <> Scale then Error(ReadingMissing Scale)
                            elif qty <> item.ReworkKg then
                                Error(ReworkQtyInvalid(item.SourceBlockNo, qty, item.ReworkKg))
                            else
                                match validateReading issuedTokens rd0 with
                                | Error e -> Error e
                                | Ok validReading ->
                                    let updated: ReworkItem =
                                        { item with
                                            FrozenWeightKg = Some qty
                                            MergeScaleReading = Some validReading
                                            MergedAt = Some at
                                            IntoOrderId = Some reworkId }
                                    check (updated :: acc) rest
                match check [] order.Items with
                | Error e -> Error e
                | Ok mergedItems ->
                    // 源批侧：整块 -> ReworkedOut；部分 -> 冻结原块并另立剩余独立块
                    let mutable source' = source
                    let mutable counter = (source.Blocks |> List.map (fun b -> b.BlockNo) |> List.max) + 1
                    let mutable remainders: (int * int) list = []
                    for item in mergedItems do
                        let orig =
                            source'.Blocks
                            |> List.tryFind (fun b -> b.BlockNo = item.SourceBlockNo)
                            |> Option.defaultWith (fun _ -> failwith "块丢失")
                        if item.ReworkKg >= orig.WeightKg then
                            source' <- mapBlock source' item.SourceBlockNo (fun b ->
                                { b with Status = ReworkedOut
                                         StatusChangedAt = Some at; StatusChangedBy = Some by
                                         StatusReason = Some(sprintf "整块返工并入工单 %O" reworkId) })
                        else
                            // 半块返捏合：原块记录冻结，剩余部分另立为独立块并保留原标签
                            let remainKg = orig.WeightKg - item.ReworkKg
                            let remainNo = counter
                            counter <- counter + 1
                            let keepLabel =
                                item.RemainderLabelId |> Option.defaultValue orig.LabelId
                            source' <- mapBlock source' item.SourceBlockNo (fun b ->
                                { b with Status = ReworkedOut
                                         StatusChangedAt = Some at; StatusChangedBy = Some by
                                         StatusReason = Some(sprintf "半块返工 %.3f kg 并入工单 %O" item.ReworkKg reworkId) })
                            let remainderBlock =
                                { BlockNo = remainNo; WeightKg = remainKg; LabelId = keepLabel
                                  ScaleReading = None; At = at; By = by
                                  Status = Intact; StatusChangedAt = Some at; StatusChangedBy = Some by
                                  StatusReason = Some(sprintf "半块返捏合：%d号块未返工的剩余部分" item.SourceBlockNo)
                                  LabelMissing = false
                                  SplitFromBlockNo = Some item.SourceBlockNo
                                  ReworkId = Some reworkId }
                            source' <- { source' with Blocks = source'.Blocks @ [ remainderBlock ] }
                            remainders <- (item.SourceBlockNo, remainNo) :: remainders
                    // 更新工单 items 的剩余块号
                    let items' =
                        mergedItems |> List.map (fun item ->
                            remainders
                            |> List.tryFind (fun (src, _) -> src = item.SourceBlockNo)
                            |> Option.map (fun (_, remainNo) -> { item with RemainderBlockNo = Some remainNo })
                            |> Option.defaultValue item)
                    source' <- mapOrder source' reworkId (fun o -> { o with Items = items' })
                    if source.BatchId = target.BatchId then
                        // 本批内返捏合：物料不出批，不登记外来返工料
                        Ok(source', source')
                    else
                        // 目标批侧：谱系边（外来返工料），目标批闭合时计入产出
                        let mergedKg = items' |> List.sumBy (fun i -> i.ReworkKg)
                        let totalReading =
                            items'
                            |> List.choose (fun i -> i.MergeScaleReading)
                            |> List.fold (fun acc r -> { acc with Value = acc.Value + r.Value })
                                   { Kind = Scale; Value = 0m; Stable = true
                                     At = at; Token = Guid.NewGuid()
                                     Raw = "(返工并入合计)"; ReadingId = Guid.NewGuid() }
                        let incoming =
                            { IncomingId = Guid.NewGuid(); ReworkId = reworkId
                              SourceBatchId = source.BatchId; QuantityKg = mergedKg
                              ScaleReading = totalReading; At = at; By = by
                              Note = Some(sprintf "返工混入；源批原因：%s" order.Reason) }
                        let target' =
                            { target with IncomingReworks = target.IncomingReworks @ [ incoming ]
                                          ReworkOrders = target.ReworkOrders
                                                         @ [ { order with Items = items' } ] }
                        Ok(source', target')

    /// 返捏合后重新分块：在目标批登记新块（标签唯一，物理脱落标签不得复用），
    /// 新块重量之和必须与该工单并入返工料一致（返工分支自身闭合）。
    let recutReworkBlocks (batch: ButterBatch) (reworkId: Guid)
                          (newBlocks: (decimal * string * DeviceReading) list)
                          (issuedTokens: Guid Set) (by: string) (at: DateTime)
                          : Result<ButterBatch, RuleError> =
        match findOrder batch reworkId with
        | None -> Error(ReworkOrderNotFound reworkId)
        | Some order when order.CompletedAt.IsSome -> Error(ReworkAlreadyCompleted reworkId)
        | Some order ->
            if List.isEmpty newBlocks then Error(Conflict "返工后至少要重新分一块")
            else
                let detached = detachedLabels batch
                let active = activeLabels batch
                let nextNo = (batch.Blocks |> List.map (fun b -> b.BlockNo) |> List.max)
                let rec build (acc: ProductBlock list)
                              (xs: (decimal * string * DeviceReading) list)
                              : Result<ProductBlock list, RuleError> =
                    match xs with
                    | [] -> Ok(List.rev acc)
                    | (weightKg, labelId, reading) :: rest ->
                        let label = labelId.Trim()
                        if weightKg <= 0m then Error(Conflict "块重必须大于 0")
                        elif String.IsNullOrWhiteSpace label then Error(Conflict "标签号不能为空")
                        elif Set.contains label detached then Error(LabelReuseDetached label)
                        elif acc |> List.exists (fun (b: ProductBlock) -> b.LabelId = label)
                             || Set.contains label active then
                            let prior =
                                batch.Blocks @ (acc |> List.rev)
                                |> List.pick (fun b -> if b.LabelId = label then Some b.BlockNo else None)
                            Error(LabelMismatch(nextNo + acc.Length + 1, label, prior))
                        elif reading.Kind <> Scale then Error(ReadingMissing Scale)
                        else
                            match validateReading issuedTokens reading with
                            | Error e -> Error e
                            | Ok r ->
                                let block =
                                    { BlockNo = nextNo + acc.Length + 1
                                      WeightKg = weightKg; LabelId = label
                                      ScaleReading = Some r; At = at; By = by
                                      Status = Intact; StatusChangedAt = Some at
                                      StatusChangedBy = Some by
                                      StatusReason = Some(sprintf "返工工单 %O 重新分块" reworkId)
                                      LabelMissing = false
                                      SplitFromBlockNo = None; ReworkId = Some reworkId }
                                build (block :: acc) rest
                match build [] newBlocks with
                | Error e -> Error e
                | Ok blocks ->
                    let outputKg = blocks |> List.sumBy (fun b -> b.WeightKg)
                    let mergedKg = order.Items |> List.sumBy (fun i -> defaultArg i.FrozenWeightKg i.ReworkKg)
                    if outputKg <> mergedKg then
                        Error(ReworkOutputNotClosed(reworkId, mergedKg, outputKg))
                    else
                        let batch' =
                            mapOrder
                                { batch with Blocks = batch.Blocks @ blocks }
                                reworkId
                                (fun o ->
                                    { o with OutputBlockNos = o.OutputBlockNos @ (blocks |> List.map (fun b -> b.BlockNo)) })
                        Ok batch'

    /// 新增取样（返工分支）：只能对返工后重新分块的新块登记新增样品；
    /// 与普通取样相同：冻结位置、温度拒收限、标签必须在位。
    let recordReworkSample (batch: ButterBatch) (reworkId: Guid) (blockNo: int)
                           (position: SamplePosition) (temperatureC: decimal)
                           (maxSampleTempC: decimal) (by: string) (at: DateTime)
                           : Result<ButterBatch, RuleError> =
        match findOrder batch reworkId with
        | None -> Error(ReworkOrderNotFound reworkId)
        | Some order when order.CompletedAt.IsSome -> Error(ReworkAlreadyCompleted reworkId)
        | Some order ->
            if not (order.OutputBlockNos |> List.contains blockNo) then
                Error(Conflict(sprintf "%d号块不是返工工单 %O 的重新分块，新增取样必须落在返工新块上" blockNo reworkId))
            else recordSample batch blockNo position temperatureC maxSampleTempC by at

    /// 关闭返捏合分支：新分块覆盖充分且水盐结果齐备后方可完成；
    /// 原分块结果保持冻结（冻结块不参与覆盖检查）。
    let completeRework (batch: ButterBatch) (reworkId: Guid)
                       (by: string) (at: DateTime) : Result<ButterBatch, RuleError> =
        match findOrder batch reworkId with
        | None -> Error(ReworkOrderNotFound reworkId)
        | Some order when order.CompletedAt.IsSome -> Error(ReworkAlreadyCompleted reworkId)
        | Some order when order.Items |> List.exists (fun i -> i.MergeScaleReading.IsNone) ->
            Error(ReworkNotMerged reworkId)
        | Some order when List.isEmpty order.OutputBlockNos ->
            Error(Conflict "返工后尚未重新分块")
        | Some order ->
            let errs =
                [ for n in order.OutputBlockNos do
                      let bs = batch.Samples |> List.filter (fun s -> s.BlockNo = n)
                      match coverageFor bs with
                      | Error e -> e
                      | Ok _ ->
                          for s in bs do
                              if s.MoisturePct.IsNone || s.SaltPct.IsNone then
                                  ResultNotReady s.Position ]
            match errs with
            | e :: _ -> Error e
            | [] ->
                Ok(mapOrder batch reworkId (fun o ->
                    { o with CompletedAt = Some at; CompletedBy = Some by }))

    /// 部分放行：已发布水盐结果、覆盖充分的独立块可逐块放行（放行不可逆）。
    /// 已放行块不参与后续整批发布覆盖检查，但仍是本批产品，计入物料闭合。
    let releaseBlock (batch: ButterBatch) (blockNo: int)
                     (by: string) (at: DateTime) : Result<ButterBatch, RuleError> =
        match findBlock batch blockNo with
        | None -> Error(BlockNotFound blockNo)
        | Some b when b.Status = Released -> Ok batch
        | Some b when b.Status <> Intact -> Error(BlockNotReworkable(blockNo, b.Status))
        | Some b when b.LabelMissing -> Error(LabelDetached blockNo)
        | Some b ->
            let bs = batch.Samples |> List.filter (fun s -> s.BlockNo = blockNo)
            match coverageFor bs with
            | Error e -> Error e
            | Ok _ ->
                let notReady =
                    bs |> List.tryFind (fun s -> s.MoisturePct.IsNone || s.SaltPct.IsNone)
                match notReady with
                | Some s -> Error(ResultNotReady s.Position)
                | None ->
                    Ok(mapBlock batch blockNo (fun x ->
                        { x with Status = Released
                                 StatusChangedAt = Some at; StatusChangedBy = Some by
                                 StatusReason = Some "质量人员部分放行" }))

    // ---------- 标签脱落（软化中） ----------

    /// 物理标签在返捏合软化中脱落：质量人员核对块身份后登记事件。
    /// newLabelId = None 表示暂未补贴（块进入标签缺失状态，禁止取样/放行）；
    /// 替代标签必须是全新标签，不得复用任何脱落标签，也不得与在用标签冲突。
    let reportLabelDetached (batch: ButterBatch) (blockNo: int)
                            (newLabelId: string option) (note: string option)
                            (by: string) (at: DateTime) : Result<ButterBatch, RuleError> =
        match findBlock batch blockNo with
        | None -> Error(BlockNotFound blockNo)
        | Some b when b.Status = ReworkedOut ->
            Error(BlockNotReworkable(blockNo, b.Status))
        | Some b ->
            let detached = detachedLabels batch
            let active = activeLabels batch |> Set.remove b.LabelId
            match newLabelId with
            | None ->
                let ev =
                    { LabelEventId = Guid.NewGuid(); BlockNo = blockNo; At = at; By = by
                      OldLabelId = Some b.LabelId; NewLabelId = None; Note = note }
                let batch' =
                    mapBlock batch blockNo (fun x -> { x with LabelMissing = true })
                Ok({ batch' with LabelEvents = batch'.LabelEvents @ [ ev ] })
            | Some raw when String.IsNullOrWhiteSpace raw ->
                Error(Conflict "替代标签号不能为空")
            | Some raw ->
                let nl = raw.Trim()
                if Set.contains nl detached then Error(LabelReuseDetached nl)
                elif Set.contains nl active then
                    Error(LabelMismatch(blockNo, nl, blockNo))
                else
                    let ev =
                        { LabelEventId = Guid.NewGuid(); BlockNo = blockNo; At = at; By = by
                          OldLabelId = Some b.LabelId; NewLabelId = Some nl; Note = note }
                    let batch' =
                        mapBlock batch blockNo (fun x ->
                            { x with LabelId = nl; LabelMissing = false })
                    Ok({ batch' with LabelEvents = batch'.LabelEvents @ [ ev ] })

    // ---------- 物料闭合 ----------

    type ClosureConfig =
        { /// 绝对容差 kg
          AbsTolKg: decimal
          /// 相对容差（占总投入比例）
          RelTol: decimal }
        static member Default = { AbsTolKg = 2.0m; RelTol = 0.015m }

    /// 物料闭合：奶油 + 盐 + 外来返工料（投入）
    /// ≈ 批内产品块（不含已返捏合出批的冻结原块）+ 酪乳 + 跨批返工送出（分出）。
    /// 本批内返捏合物料不出批：原块冻结不计、重新分块的新块计入，恰好抵消。
    /// 半块返捏合：原块冻结（整块重量不计），未返工部分另立的剩余块按产品计入，
    /// 返出部分计入跨批送出。
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
            let incomingKg = batch.IncomingReworks |> List.sumBy (fun i -> i.QuantityKg)
            // 已实际并入、跨批送出的返工料（合并记录存在才算出批）
            let outgoingKg =
                batch.ReworkOrders
                |> List.filter (fun o -> o.TargetBatchId <> batch.BatchId
                                         && o.SourceBatchId = batch.BatchId)
                |> List.sumBy (fun o ->
                    o.Items |> List.filter (fun i -> i.MergedAt.IsSome)
                    |> List.sumBy (fun i -> i.ReworkKg))
            // ReworkedOut 原块已出批（半块时其重量由剩余块承接返后剩余部分）；
            // ReworkFrozen 尚未合并，物料仍在批内，继续计入。
            let productBlocks =
                batch.Blocks |> List.filter (fun b -> b.Status <> ReworkedOut)
            let butterKg = productBlocks |> List.sumBy (fun b -> b.WeightKg)
            if batch.Blocks.IsEmpty then Error(BatchNotComplete "尚未分块，无法做物料闭合")
            else
                let input = batch.CreamInKg + saltKg + incomingKg
                let output = butterKg + buttermilkKg + outgoingKg
                let diff = input - output
                let tol = max cfg.AbsTolKg (input * cfg.RelTol)
                if abs diff <= tol then Ok()
                else
                    Error(MaterialNotClosed(
                        sprintf "投入 %.2f kg（奶油 %.2f + 盐 %.2f + 外来返工 %.2f），分出 %.2f kg（批内产品 %.2f + 酪乳 %.2f + 跨批返工送出 %.2f），差异 %.2f kg 超过容差 %.2f kg"
                            input batch.CreamInKg saltKg incomingKg output butterKg buttermilkKg outgoingKg diff tol))

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
                  // 冻结/已返出的原块结果保持冻结，不参与覆盖与发布检查
                  if b.Status = ReworkFrozen then
                      BlockReworkFrozen b.BlockNo
                  elif b.LabelMissing then
                      LabelDetached b.BlockNo
                  elif b.Status <> Released && b.Status <> ReworkedOut then
                      let bs = batch.Samples |> List.filter (fun s -> s.BlockNo = b.BlockNo)
                      match coverageFor bs with
                      | Error e -> e
                      | Ok _ ->
                          for s in bs do
                              if s.MoisturePct.IsNone || s.SaltPct.IsNone then
                                  ResultNotReady s.Position ]
        // 存在未完成的返工工单时阻塞整批发布：
        // 目标批（含本批返捏合）必须走到“重新分块+新增取样+关闭分支”；
        // 跨批送出的源批只负责到“全部受影响块并入完成”。
        let openOrderErrs =
            [ for o in batch.ReworkOrders do
                  // 本批只是跨批返工的来源：只负责到全部并入完成
                  if o.SourceBatchId = batch.BatchId && o.TargetBatchId <> batch.BatchId then
                      let mergedAll =
                          not (List.isEmpty o.Items)
                          && o.Items |> List.forall (fun i -> i.MergedAt.IsSome)
                      if not mergedAll then
                          Conflict(sprintf "返工工单 %O 的受影响块尚未全部返工并入" o.ReworkId)
                  // 目标批（含本批返捏合）必须关闭分支
                  elif o.TargetBatchId = batch.BatchId && o.CompletedAt.IsNone then
                      Conflict(sprintf "返工工单 %O 尚未完成（重新分块、新增取样并关闭分支）" o.ReworkId) ]
        let closureErrs =
            match checkMaterialClosure batch cfg with Error e -> [ e ] | Ok _ -> []
        let errs =
            stageErrs @ drainErrs @ labelErrs @ coverageErrs
            @ openOrderErrs @ closureErrs |> List.distinct
        match errs with [] -> Ok() | es -> Error es
