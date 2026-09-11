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
      /// 桥接层授权令牌
      Token: Guid
      /// 原始报文（仅留存审计）
      Raw: string }

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

/// 产品分块。每个块带唯一标签；标签与块号绑定，不允许互换。
type ProductBlock =
    { BlockNo: int
      WeightKg: decimal
      /// 随块标签号（物理标签）
      LabelId: string
      ScaleReading: DeviceReading option
      At: DateTime
      By: string }

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
        | MaterialNotClosed m -> sprintf "物料不闭合：%s" m
        | BatchNotComplete m -> sprintf "批次不完整：%s" m
        | Conflict m -> sprintf "数据冲突：%s" m
