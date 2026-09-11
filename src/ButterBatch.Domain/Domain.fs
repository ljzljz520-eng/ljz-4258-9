// ============================================================================
// ButterBatch.Domain / Domain.fs
// 黄油搅拌批次领域模型：阶段、材料、取样位置、设备读数与规则错误。
// 仅描述“发生了什么”，不包含任何配方或搅拌工艺参数。
// ============================================================================
namespace ButterBatch.Domain

open System

/// 生产阶段：奶油相转变 -> 排酪乳 -> 洗涤 -> 加盐(可分次) -> 捏合 -> 分块
[<Struct; StructuralEquality; StructuralComparison>]
type Stage =
    /// 奶油相转变（搅拌中）
    | PhaseTransition
    /// 排酪乳
    | ButtermilkDrain
    /// 洗涤
    | Washing
    /// 加盐（可分两次或多次）
    | Salting
    /// 捏合
    | Working
    /// 分块
    | Blocking

    override this.ToString() =
        match this with
        | PhaseTransition -> "奶油相转变"
        | ButtermilkDrain -> "排酪乳"
        | Washing -> "洗涤"
        | Salting -> "加盐"
        | Working -> "捏合"
        | Blocking -> "分块"

    static member All =
        [ PhaseTransition; ButtermilkDrain; Washing; Salting; Working; Blocking ]

/// 取样位置：层次 × 径向。冻结位置由质量人员选择，表面样不能代表整块。
[<Struct; StructuralEquality; StructuralComparison>]
type SampleLayer =
    /// 表层
    | Surface
    /// 中层（冻结取样必须覆盖）
    | Middle
    /// 芯层（冻结取样必须覆盖）
    | Core
    override this.ToString() =
        match this with Surface -> "表层" | Middle -> "中层" | Core -> "芯层"

[<Struct; StructuralEquality; StructuralComparison>]
type SampleRadial =
    /// 中心
    | Center
    /// 边缘
    | Edge
    override this.ToString() =
        match this with Center -> "中心" | Edge -> "边缘"

/// 冻结的取样点位（质量人员从冻结位置集合中选点）
type SamplePosition =
    { Layer: SampleLayer
      Radial: SampleRadial }
    override this.ToString() =
        sprintf "%s-%s" (string this.Layer) (string this.Radial)

/// 授权设备类型
[<Struct; StructuralEquality; StructuralComparison>]
type DeviceKind =
    /// 台秤
    | Scale
    /// 水分仪
    | MoistureMeter
    override this.ToString() =
        match this with Scale -> "台秤" | MoistureMeter -> "水分仪"

/// 经过授权桥接层收到的设备读数
[<Struct; StructuralEquality; StructuralComparison>]
type DeviceReading =
    { Kind: DeviceKind
      /// 读数数值（台秤 kg / 水分仪 质量分数 %）
      Value: decimal
      /// 台秤稳定标志；水分仪为完成标志
      Stable: bool
      /// 读数时刻
      At: DateTime
      /// 桥接层授权令牌（同令牌会产生多条读数，不唯一）
      Token: Guid
      /// 原始报文（仅留存审计）
      Raw: string
      /// 读数自身的稳定唯一标识（仓储按它建键，避免同令牌读数互相覆盖）
      ReadingId: Guid }

/// 材料事件（与阶段、来源、去向、设备读数一一对应）
type MaterialEvent =
    { EventId: Guid
      Stage: Stage
      At: DateTime
      /// 材料名称，例如：奶油 / 酪乳 / 洗涤水 / 盐
      Material: string
      /// 批次/批号（奶油批、盐袋批号等）
      Lot: string option
      /// 来源（容器/设备）
      Source: string option
      /// 去向（容器/设备）——酪乳去向必须登记
      Destination: string option
      /// 数量（kg）
      QuantityKg: decimal option
      /// 支撑该数量的台秤授权读数
      ScaleReading: DeviceReading option
      By: string }

/// 一次加盐投料（加盐可分两次；每次必须有盐材料批号与稳定台秤读数）
type SaltAddition =
    { AddId: Guid
      /// 第几次加盐（1、2…）
      Ordinal: int
      SaltLot: string
      QuantityKg: decimal
      ScaleReading: DeviceReading
      At: DateTime
      By: string }

/// 产品块的生命周期状态（返捏合分支）
[<Struct; StructuralEquality; StructuralComparison>]
type BlockStatus =
    /// 独立可流通的产品块（默认）
    | Intact
    /// 已放行（部分放行后不再允许指定返工）
    | Released
    /// 质量人员已指定返工：原分块与其水盐结果即刻冻结，等待/正在返捏合
    | ReworkFrozen
    /// 已返捏合并出本批（整块或已登记的部分），原块记录仅作冻结留痕
    | ReworkedOut
    override this.ToString() =
        match this with
        | Intact -> "独立"
        | Released -> "已放行"
        | ReworkFrozen -> "返工冻结"
        | ReworkedOut -> "已返捏合出批"

/// 产品分块。每个块带唯一标签；标签与块号绑定，不允许互换。
type ProductBlock =
    { BlockNo: int
      WeightKg: decimal
      /// 随块标签号（物理标签的当前值；脱落换标见 LabelEvents）
      LabelId: string
      ScaleReading: DeviceReading option
      At: DateTime
      By: string
      /// 块状态（独立/已放行/返工冻结/已返捏合出批）
      Status: BlockStatus
      StatusChangedAt: DateTime option
      StatusChangedBy: string option
      StatusReason: string option
      /// 物理标签在软化中脱落、尚未补贴替代标签时为 true
      LabelMissing: bool
      /// 半块返捏合时，未返工的剩余部分以此块号另立为独立块
      SplitFromBlockNo: int option
      /// 产生该块（剩余块/重新分块）的返工工单号
      ReworkId: Guid option }

/// 标签事件：返捏合软化过程中物理标签可能脱落，质量人员核对身份后补贴替代标签并留痕
type BlockLabelEvent =
    { LabelEventId: Guid
      BlockNo: int
      At: DateTime
      By: string
      /// 脱落/污损的原标签
      OldLabelId: string option
      /// 补贴的替代标签（不补贴则为 None，块进入标签缺失状态）
      NewLabelId: string option
      Note: string option }

/// 多点取样记录（先按冻结位置登记取样，后由质量人员发布水分/盐分结果）
type Sample =
    { SampleId: Guid
      BlockNo: int
      Position: SamplePosition
      /// 取样时样品温度 ℃（样品受热软化则拒收）
      TemperatureC: decimal
      SampledAt: DateTime
      SampledBy: string
      /// 水分结果（质量分数 %）
      MoisturePct: decimal option
      /// 盐分结果（质量分数 %）
      SaltPct: decimal option
      MoistureReading: DeviceReading option
      SaltReading: DeviceReading option
      ResultPublishedAt: DateTime option
      ResultPublishedBy: string option }

/// 阶段确认记录（操作员确认奶油批和“实际”阶段）
type StageConfirmation =
    { Stage: Stage
      CreamLot: string
      ConfirmedAt: DateTime
      ConfirmedBy: string
      Note: string option }

/// 单个受影响分块的返工指定（由质量人员逐块指定）
type ReworkItem =
    { /// 被指定返工的源块号
      SourceBlockNo: int
      /// 整块返工 = 原块重；只返捏合半块 = 实际返出部分重量
      ReworkKg: decimal
      /// 未返工部分（半块返捏合）保留的原标签号；整块返工为 None
      RemainderLabelId: string option
      /// 未返工部分另立为独立块的块号（执行合并时生成）
      RemainderBlockNo: int option
      /// 该块指定返工时实际重量（解冻软化后复称）；缺省按切块重量
      FrozenWeightKg: decimal option
      /// 返出部分实际并入时的稳定台秤读数（按块留痕）
      MergeScaleReading: DeviceReading option
      /// 返出部分并入时刻
      MergedAt: DateTime option
      /// 返出部分并入去向工单（跨批时为目标批工单）
      IntoOrderId: Guid option }

/// 返捏合分支工单：记录“哪些块、并入哪批/哪工单、何时完成、产出哪些新块”
type ReworkOrder =
    { ReworkId: Guid
      /// 发起批（受影响块所在批）
      SourceBatchId: Guid
      /// 并入批：跨批返工为另一批；本批内返捏合等于 SourceBatchId
      TargetBatchId: Guid
      DesignatedAt: DateTime
      DesignatedBy: string
      Reason: string
      Items: ReworkItem list
      /// 返捏合完成（新分块取样齐备）的时刻
      CompletedAt: DateTime option
      CompletedBy: string option
      /// 重新分块在目标批中产出的块号
      OutputBlockNos: int list }

/// 目标批收到的外来返工料（跨批返工的谱系边：在目标批侧可追溯来源）
type IncomingRework =
    { IncomingId: Guid
      ReworkId: Guid
      /// 来源批
      SourceBatchId: Guid
      /// 该批并入的返工料净重（各受影响块返出部分之和）
      QuantityKg: decimal
      /// 支撑并入重量的稳定台秤读数
      ScaleReading: DeviceReading
      At: DateTime
      By: string
      Note: string option }

/// 批次状态
type ButterBatch =
    { BatchId: Guid
      /// 操作员确认的奶油批
      CreamLot: string
      StartedAt: DateTime
      OperatorName: string
      /// 奶油投料净重（kg，来自稳定台秤读数）
      CreamInKg: decimal
      CreamScaleReading: DeviceReading
      /// 实际阶段确认（操作员可声明实际所处阶段）
      Confirmations: StageConfirmation list
      Materials: MaterialEvent list
      SaltAdditions: SaltAddition list
      Blocks: ProductBlock list
      Samples: Sample list
      /// 返捏合工单（源批与目标批都保留同一条；本批返捏合时也在本批）
      ReworkOrders: ReworkOrder list
      /// 收到的外来返工料（仅跨批返工的目标批）
      IncomingReworks: IncomingRework list
      /// 物理标签脱落/换标事件（质量人员核对身份后留痕）
      LabelEvents: BlockLabelEvent list
      /// 当前实际阶段
      CurrentStage: Stage
      Closed: bool }

module Positions =
    /// 冻结位置集合（质量人员从中取样）
    let frozen =
        [ for layer in [ Surface; Middle; Core ] do
              for radial in [ Center; Edge ] -> { Layer = layer; Radial = radial } ]

    let surface = frozen |> List.filter (fun p -> p.Layer = Surface)
    let interior = frozen |> List.filter (fun p -> p.Layer <> Surface)

/// 规则违反（系统检查物料闭合与样品覆盖时给出的结论）
type RuleError =
    /// 阶段顺序错误
    | StageOutOfOrder of attempted: Stage * current: Stage
    /// 该操作不允许在当前阶段进行
    | OperationNotAllowedInStage of op: string * stage: Stage
    /// 酪乳未排净：排酪乳事件缺失或去向未登记
    | ButtermilkNotDrained
    /// 设备读数不稳定 / 稳定标志丢失
    | ReadingNotStable of DeviceKind
    /// 设备读数缺失
    | ReadingMissing of DeviceKind
    /// 授权令牌无效（桥接层未授权或重放）
    | UnauthorizedReading of DeviceKind
    /// 加盐材料（批号）缺失
    | SaltMaterialMissing
    /// 第二次加盐必须晚于第一次
    | SaltOrdinalConflict of ordinal: int
    /// 取样位置不在冻结位置集合中
    | PositionNotFrozen of SamplePosition
    /// 只取了表面样，不能代表整块均匀性
    | SurfaceSamplesOnly
    /// 样品覆盖不足：缺少某层（中/芯）或某径向
    | CoverageMissing of string
    /// 样品受热软化，超过拒收温度
    | SampleTooWarm of blockNo: int * temp: decimal * limit: decimal
    /// 水分/盐分结果未发布或读数不稳定
    | ResultNotReady of SamplePosition
    /// 产品块标签重复（分块后标签互换）
    | LabelMismatch of blockNo: int * labelId: string * firstBlockNo: int
    /// 物理标签已脱落，未由质量人员核对并补贴替代标签前不得取样/放行
    | LabelDetached of blockNo: int
    /// 标签号已被（当前块或脱落事件）占用，替代标签不得复用旧标签
    | LabelReuseDetached of labelId: string
    /// 块已被指定返工，原分块结果已冻结，不允许再取样/发布/改判
    | BlockReworkFrozen of blockNo: int
    /// 指定返工的块不存在
    | BlockNotFound of blockNo: int
    /// 只有“独立”状态的块可以指定返工（已放行/已返工/返工中均不可）
    | BlockNotReworkable of blockNo: int * status: BlockStatus
    /// 返工重量非法（非正或超过块重）
    | ReworkQtyInvalid of blockNo: int * reworkKg: decimal * blockKg: decimal
    /// 整块返工时不允许登记保留标签（保留标签仅用于部分返工）
    | ReworkRemainderNotAllowed of blockNo: int
    /// 部分返工必须登记未返工部分的保留标签
    | ReworkRemainderLabelMissing of blockNo: int
    /// 返工工单号在批中不存在
    | ReworkOrderNotFound of reworkId: Guid
    /// 返工工单尚未执行合并（受影响块仍冻结）
    | ReworkNotMerged of reworkId: Guid
    /// 返工工单已经完成
    | ReworkAlreadyCompleted of reworkId: Guid
    /// 重新分块重量之和与并入返工料重量不一致（返工分支不闭合）
    | ReworkOutputNotClosed of reworkId: Guid * mergedKg: decimal * outputKg: decimal
    /// 块已经放行（部分放行），不能再指定返工
    | BlockAlreadyReleased of blockNo: int
    /// 物料不闭合（输入-输出差异超过容差）
    | MaterialNotClosed of message: string
    /// 批次未完成前置阶段
    | BatchNotComplete of message: string
    /// 重复操作 / 数据不一致
    | Conflict of message: string

    override this.ToString() =
        match this with
        | StageOutOfOrder(a, c) -> sprintf "阶段顺序错误：试图进入「%s」，当前为「%s」" (string a) (string c)
        | OperationNotAllowedInStage(op, s) -> sprintf "操作「%s」不允许在「%s」阶段进行" op (string s)
        | ButtermilkNotDrained -> "酪乳未排净：缺少排酪乳记录或酪乳去向未登记"
        | ReadingNotStable k -> sprintf "%s读数不稳定（稳定标志丢失），不能采用" (string k)
        | ReadingMissing k -> sprintf "缺少%s授权读数" (string k)
        | UnauthorizedReading k -> sprintf "%s读数未经过授权（令牌无效或重放）" (string k)
        | SaltMaterialMissing -> "加盐材料批号缺失"
        | SaltOrdinalConflict i -> sprintf "第%d次加盐与已有投料次序冲突" i
        | PositionNotFrozen p -> sprintf "取样位置 %s 不在冻结位置集合中" (string p)
        | SurfaceSamplesOnly -> "仅有表层样品，不能代表整块均匀性"
        | CoverageMissing m -> sprintf "样品覆盖不足：%s" m
        | SampleTooWarm(b, t, l) -> sprintf "%d号块样品温度 %.2f℃ 超过拒收限 %.2f℃（样品受热软化）" b t l
        | ResultNotReady p -> sprintf "位置 %s 的水分/盐分结果未就绪" (string p)
        | LabelMismatch(b, l, f) -> sprintf "%d号块标签「%s」与%d号块重复（疑似标签互换）" b l f
        | LabelDetached b -> sprintf "%d号块物理标签在软化中脱落，未经质量人员核对并补贴替代标签前不得取样/放行" b
        | LabelReuseDetached l -> sprintf "替代标签「%s」是已脱落/占用标签，必须使用新标签，不得复用旧标签" l
        | BlockReworkFrozen b -> sprintf "%d号块已被指定返工，原分块结果已冻结" b
        | BlockNotFound b -> sprintf "%d号块不存在" b
        | BlockNotReworkable(b, st) -> sprintf "%d号块当前状态为「%s」，不能指定返工（只有独立块可返工）" b (string st)
        | ReworkQtyInvalid(b, r, w) -> sprintf "%d号块返工重量 %.3f kg 非法（须 > 0 且不超过块重 %.3f kg）" b r w
        | ReworkRemainderNotAllowed b -> sprintf "%d号块整块返工时登记了保留标签；保留标签仅用于只返捏合半块" b
        | ReworkRemainderLabelMissing b -> sprintf "%d号块只返捏合半块时，未返工部分必须登记保留标签" b
        | ReworkOrderNotFound id -> sprintf "返工工单 %O 不存在" id
        | ReworkNotMerged id -> sprintf "返工工单 %O 尚未执行返工合并" id
        | ReworkAlreadyCompleted id -> sprintf "返工工单 %O 已经完成" id
        | ReworkOutputNotClosed(id, m, o) -> sprintf "返工工单 %O 的新分块合计 %.3f kg 与并入返工料 %.3f kg 不一致" id o m
        | BlockAlreadyReleased b -> sprintf "%d号块已经放行，不能再指定返工（放行不可逆）" b
        | MaterialNotClosed m -> sprintf "物料不闭合：%s" m
        | BatchNotComplete m -> sprintf "批次不完整：%s" m
        | Conflict m -> sprintf "数据冲突：%s" m
