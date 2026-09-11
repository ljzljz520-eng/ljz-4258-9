// ============================================================================
// ButterBatch.Domain / Storage.fs
// 本地 LiteDB 持久化：阶段确认、材料事件、加盐、分块、取样位置与结果、
// 设备授权令牌与收到的读数。纯本地文件，不做云端同步。
// ============================================================================
namespace ButterBatch.Storage

open System
open LiteDB
open ButterBatch.Domain

// ---------- 持久化 DTO（扁平记录，便于 LiteDB 直接映射） ----------

type BatchDto() =
    [<LiteDB.BsonId>]
    member val Id: Guid = Guid.Empty with get, set
    member val CreamLot: string = "" with get, set
    member val StartedAt: DateTime = DateTime.MinValue with get, set
    member val OperatorName: string = "" with get, set
    member val CreamInKg: decimal = 0m with get, set
    member val CreamScaleReadingId: Guid = Guid.Empty with get, set
    member val CurrentStage: int = 0 with get, set
    member val Closed: bool = false with get, set

type ConfirmationDto() =
    [<LiteDB.BsonId>]
    member val Id: int = 0 with get, set
    member val BatchId: Guid = Guid.Empty with get, set
    member val Stage: int = 0 with get, set
    member val CreamLot: string = "" with get, set
    member val ConfirmedAt: DateTime = DateTime.MinValue with get, set
    member val ConfirmedBy: string = "" with get, set
    member val Note: string = null with get, set

type MaterialDto() =
    [<LiteDB.BsonId>]
    member val Id: Guid = Guid.Empty with get, set
    member val BatchId: Guid = Guid.Empty with get, set
    member val Stage: int = 0 with get, set
    member val At: DateTime = DateTime.MinValue with get, set
    member val Material: string = "" with get, set
    member val Lot: string = null with get, set
    member val Source: string = null with get, set
    member val Destination: string = null with get, set
    member val QuantityKg: decimal = 0m with get, set
    member val HasQuantity: bool = false with get, set
    member val ScaleReadingId: Guid = Guid.Empty with get, set
    member val By: string = "" with get, set

type SaltDto() =
    [<LiteDB.BsonId>]
    member val Id: Guid = Guid.Empty with get, set
    member val BatchId: Guid = Guid.Empty with get, set
    member val Ordinal: int = 0 with get, set
    member val SaltLot: string = "" with get, set
    member val QuantityKg: decimal = 0m with get, set
    member val ScaleReadingId: Guid = Guid.Empty with get, set
    member val At: DateTime = DateTime.MinValue with get, set
    member val By: string = "" with get, set

type BlockDto() =
    [<LiteDB.BsonId>]
    member val BlockNo: int = 0 with get, set
    member val BatchId: Guid = Guid.Empty with get, set
    member val WeightKg: decimal = 0m with get, set
    member val LabelId: string = "" with get, set
    member val ScaleReadingId: Guid = Guid.Empty with get, set
    member val At: DateTime = DateTime.MinValue with get, set
    member val By: string = "" with get, set

type SampleDto() =
    [<LiteDB.BsonId>]
    member val Id: Guid = Guid.Empty with get, set
    member val BatchId: Guid = Guid.Empty with get, set
    member val BlockNo: int = 0 with get, set
    member val Layer: int = 0 with get, set
    member val Radial: int = 0 with get, set
    member val TemperatureC: decimal = 0m with get, set
    member val SampledAt: DateTime = DateTime.MinValue with get, set
    member val SampledBy: string = "" with get, set
    member val MoisturePct: decimal = 0m with get, set
    member val HasMoisture: bool = false with get, set
    member val SaltPct: decimal = 0m with get, set
    member val HasSalt: bool = false with get, set
    member val MoistureReadingId: Guid = Guid.Empty with get, set
    member val SaltReadingId: Guid = Guid.Empty with get, set
    member val PublishedAt: DateTime = DateTime.MinValue with get, set
    member val HasPublish: bool = false with get, set
    member val PublishedBy: string = null with get, set

type ReadingDto() =
    [<LiteDB.BsonId>]
    member val Id: Guid = Guid.Empty with get, set
    member val BatchId: Guid = Guid.Empty with get, set
    member val Kind: int = 0 with get, set
    member val Value: decimal = 0m with get, set
    member val Stable: bool = false with get, set
    member val At: DateTime = DateTime.MinValue with get, set
    member val Token: Guid = Guid.Empty with get, set
    member val Raw: string = "" with get, set

/// 设备授权记录（Web Serial 桥接层每授权一台设备签发一个一次性令牌）
type AuthGrantDto() =
    [<LiteDB.BsonId>]
    member val Token: Guid = Guid.Empty with get, set
    member val Kind: int = 0 with get, set
    member val GrantedAt: DateTime = DateTime.MinValue with get, set
    member val ExpiresAt: DateTime = DateTime.MinValue with get, set
    member val Revoked: bool = false with get, set
    member val Note: string = null with get, set

module Mappings =
    let stageToInt = function
        | PhaseTransition -> 0 | ButtermilkDrain -> 1 | Washing -> 2
        | Salting -> 3 | Working -> 4 | Blocking -> 5
    let stageFromInt = function
        | 1 -> ButtermilkDrain | 2 -> Washing | 3 -> Salting
        | 4 -> Working | 5 -> Blocking | _ -> PhaseTransition
    let layerToInt = function Surface -> 0 | Middle -> 1 | Core -> 2
    let layerFromInt = function 1 -> Middle | 2 -> Core | _ -> Surface
    let radialToInt = function Center -> 0 | Edge -> 1
    let radialFromInt = function 1 -> Edge | _ -> Center
    let kindToInt = function Scale -> 0 | MoistureMeter -> 1
    let kindFromInt = function 1 -> MoistureMeter | _ -> Scale

    let toReadingDto (batchId: Guid) (r: DeviceReading) =
        ReadingDto(Id = Guid.NewGuid(), BatchId = batchId, Kind = kindToInt r.Kind,
                   Value = r.Value, Stable = r.Stable, At = r.At,
                   Token = r.Token, Raw = r.Raw)
    let ofReadingDto (d: ReadingDto) : DeviceReading =
        { Kind = kindFromInt d.Kind; Value = d.Value; Stable = d.Stable
          At = d.At; Token = d.Token; Raw = d.Raw }

/// LiteDB 本地仓储
type BatchRepository(connectionString: string) =
    let db = new LiteDatabase(connectionString)
    let batches = db.GetCollection<BatchDto>("batches")
    let confs = db.GetCollection<ConfirmationDto>("confirmations")
    let mats = db.GetCollection<MaterialDto>("materials")
    let salts = db.GetCollection<SaltDto>("salts")
    let blocks = db.GetCollection<BlockDto>("blocks")
    let samples = db.GetCollection<SampleDto>("samples")
    let readings = db.GetCollection<ReadingDto>("readings")
    let grants = db.GetCollection<AuthGrantDto>("auth_grants")

    do
        batches.EnsureIndex("CreamLot") |> ignore
        confs.EnsureIndex("BatchId") |> ignore
        mats.EnsureIndex("BatchId") |> ignore
        salts.EnsureIndex("BatchId") |> ignore
        blocks.EnsureIndex("BatchId") |> ignore
        samples.EnsureIndex("BatchId") |> ignore
        readings.EnsureIndex("BatchId") |> ignore

    member _.ConnectionString = connectionString

    // ---------- 授权令牌 ----------

    member _.IssueToken(kind: DeviceKind, ?ttl: TimeSpan) =
        let ttl = defaultArg ttl (TimeSpan.FromHours 8.0)
        let token = Guid.NewGuid()
        let g = AuthGrantDto(Token = token, Kind = Mappings.kindToInt kind,
                             GrantedAt = DateTime.Now, ExpiresAt = DateTime.Now.Add ttl)
        grants.Insert g |> ignore
        token

    member _.IsTokenValid(token: Guid, kind: DeviceKind) : bool =
        match grants.FindById(BsonValue token) with
        | g when obj.ReferenceEquals(Unchecked.defaultof<AuthGrantDto>, g) -> false
        | g -> not g.Revoked
              && g.Kind = Mappings.kindToInt kind
              && g.ExpiresAt >= DateTime.Now

    member _.RevokeToken(token: Guid) =
        match grants.FindById(BsonValue token) with
        | g when obj.ReferenceEquals(Unchecked.defaultof<AuthGrantDto>, g) -> false
        | g -> g.Revoked <- true; grants.Update g

    member _.ValidTokens : Guid Set =
        grants.FindAll()
        |> Seq.filter (fun g -> not g.Revoked && g.ExpiresAt >= DateTime.Now)
        |> Seq.map (fun g -> g.Token)
        |> Set.ofSeq

    // ---------- 读数留痕 ----------

    member _.SaveReading(batchId: Guid, r: DeviceReading) =
        readings.Insert(Mappings.toReadingDto batchId r) |> ignore

    member private _.readingById batchId (id: Guid) : DeviceReading option =
        readings.Find(Query.And(Query.EQ("BatchId", BsonValue((batchId: Guid))),
                                Query.EQ("_id", BsonValue id)))
        |> Seq.tryHead |> Option.map Mappings.ofReadingDto

    // ---------- 批次保存（整聚合覆盖写） ----------

    member _.Save(b: ButterBatch) =
        // 读数先写（幂等：先删该批次读数再重建，保证可重复保存）
        let rd = readings.DeleteMany(Query.EQ("BatchId", BsonValue((b.BatchId: Guid))))
        let writeReading (r: DeviceReading) =
            readings.Insert(Mappings.toReadingDto b.BatchId r) |> ignore
        writeReading b.CreamScaleReading
        b.Materials |> List.choose (fun m -> m.ScaleReading) |> List.iter writeReading
        b.SaltAdditions |> List.iter (fun s -> writeReading s.ScaleReading)
        b.Blocks |> List.choose (fun x -> x.ScaleReading) |> List.iter writeReading
        b.Samples |> List.choose (fun s -> s.MoistureReading) |> List.iter writeReading
        b.Samples |> List.choose (fun s -> s.SaltReading) |> List.iter writeReading
        ignore rd

        batches.Upsert(BatchDto(
            Id = b.BatchId, CreamLot = b.CreamLot, StartedAt = b.StartedAt,
            OperatorName = b.OperatorName, CreamInKg = b.CreamInKg,
            CreamScaleReadingId = b.CreamScaleReading.Token,
            CurrentStage = Mappings.stageToInt b.CurrentStage, Closed = b.Closed)) |> ignore

        confs.DeleteMany(Query.EQ("BatchId", BsonValue((b.BatchId: Guid)))) |> ignore
        b.Confirmations |> List.iteri (fun i c ->
            confs.Insert(ConfirmationDto(
                Id = 0, BatchId = b.BatchId, Stage = Mappings.stageToInt c.Stage,
                CreamLot = c.CreamLot, ConfirmedAt = c.ConfirmedAt,
                ConfirmedBy = c.ConfirmedBy,
                Note = (match c.Note with Some n -> n | None -> null))) |> ignore)

        mats.DeleteMany(Query.EQ("BatchId", BsonValue((b.BatchId: Guid)))) |> ignore
        b.Materials |> List.iter (fun m ->
            mats.Insert(MaterialDto(
                Id = m.EventId, BatchId = b.BatchId,
                Stage = Mappings.stageToInt m.Stage, At = m.At, Material = m.Material,
                Lot = (match m.Lot with Some v -> v | None -> null),
                Source = (match m.Source with Some v -> v | None -> null),
                Destination = (match m.Destination with Some v -> v | None -> null),
                QuantityKg = defaultArg m.QuantityKg 0m, HasQuantity = m.QuantityKg.IsSome,
                ScaleReadingId = (match m.ScaleReading with Some r -> r.Token | None -> Guid.Empty),
                By = m.By)) |> ignore)

        salts.DeleteMany(Query.EQ("BatchId", BsonValue((b.BatchId: Guid)))) |> ignore
        b.SaltAdditions |> List.iter (fun s ->
            salts.Insert(SaltDto(
                Id = s.AddId, BatchId = b.BatchId, Ordinal = s.Ordinal,
                SaltLot = s.SaltLot, QuantityKg = s.QuantityKg,
                ScaleReadingId = s.ScaleReading.Token, At = s.At, By = s.By)) |> ignore)

        blocks.DeleteMany(Query.EQ("BatchId", BsonValue((b.BatchId: Guid)))) |> ignore
        b.Blocks |> List.iter (fun x ->
            blocks.Insert(BlockDto(
                BlockNo = x.BlockNo, BatchId = b.BatchId, WeightKg = x.WeightKg,
                LabelId = x.LabelId,
                ScaleReadingId = (match x.ScaleReading with Some r -> r.Token | None -> Guid.Empty),
                At = x.At, By = x.By)) |> ignore)

        samples.DeleteMany(Query.EQ("BatchId", BsonValue((b.BatchId: Guid)))) |> ignore
        b.Samples |> List.iter (fun s ->
            samples.Insert(SampleDto(
                Id = s.SampleId, BatchId = b.BatchId, BlockNo = s.BlockNo,
                Layer = Mappings.layerToInt s.Position.Layer,
                Radial = Mappings.radialToInt s.Position.Radial,
                TemperatureC = s.TemperatureC, SampledAt = s.SampledAt, SampledBy = s.SampledBy,
                MoisturePct = defaultArg s.MoisturePct 0m, HasMoisture = s.MoisturePct.IsSome,
                SaltPct = defaultArg s.SaltPct 0m, HasSalt = s.SaltPct.IsSome,
                MoistureReadingId = (match s.MoistureReading with Some r -> r.Token | None -> Guid.Empty),
                SaltReadingId = (match s.SaltReading with Some r -> r.Token | None -> Guid.Empty),
                PublishedAt = defaultArg s.ResultPublishedAt DateTime.MinValue,
                HasPublish = s.ResultPublishedAt.IsSome,
                PublishedBy = (match s.ResultPublishedBy with Some v -> v | None -> null))) |> ignore)

    // ---------- 批次读取（重建聚合） ----------

    member _.Load(batchId: Guid) : ButterBatch option =
        match batches.FindById(BsonValue batchId) with
        | bd when obj.ReferenceEquals(Unchecked.defaultof<BatchDto>, bd) -> None
        | bd ->
            let allReadings =
                readings.Find(Query.EQ("BatchId", BsonValue((batchId: Guid))))
                |> Seq.map (fun d -> d.Token, Mappings.ofReadingDto d) |> Map.ofSeq
            let findR token = Map.tryFind token allReadings
            let cs =
                confs.Find(Query.EQ("BatchId", BsonValue((batchId: Guid))))
                |> Seq.sortBy (fun c -> c.ConfirmedAt)
                |> Seq.map (fun c ->
                    { Stage = Mappings.stageFromInt c.Stage; CreamLot = c.CreamLot
                      ConfirmedAt = c.ConfirmedAt; ConfirmedBy = c.ConfirmedBy
                      Note = (if isNull c.Note then None else Some c.Note) })
                |> Seq.toList
            let ms =
                mats.Find(Query.EQ("BatchId", BsonValue((batchId: Guid))))
                |> Seq.sortBy (fun m -> m.At)
                |> Seq.map (fun m ->
                    { EventId = m.Id; Stage = Mappings.stageFromInt m.Stage; At = m.At
                      Material = m.Material
                      Lot = (if isNull m.Lot then None else Some m.Lot)
                      Source = (if isNull m.Source then None else Some m.Source)
                      Destination = (if isNull m.Destination then None else Some m.Destination)
                      QuantityKg = (if m.HasQuantity then Some m.QuantityKg else None)
                      ScaleReading = (if m.ScaleReadingId = Guid.Empty then None
                                      else findR m.ScaleReadingId)
                      By = m.By }) |> Seq.toList
            let ss =
                salts.Find(Query.EQ("BatchId", BsonValue((batchId: Guid))))
                |> Seq.sortBy (fun s -> s.Ordinal)
                |> Seq.map (fun s ->
                    { AddId = s.Id; Ordinal = s.Ordinal; SaltLot = s.SaltLot
                      QuantityKg = s.QuantityKg
                      ScaleReading = (findR s.ScaleReadingId
                                      |> Option.defaultValue
                                          { Kind = Scale; Value = s.QuantityKg; Stable = true
                                            At = s.At; Token = s.ScaleReadingId; Raw = "(重建)" })
                      At = s.At; By = s.By }) |> Seq.toList
            let bs =
                blocks.Find(Query.EQ("BatchId", BsonValue((batchId: Guid))))
                |> Seq.sortBy (fun b -> b.BlockNo)
                |> Seq.map (fun b ->
                    { BlockNo = b.BlockNo; WeightKg = b.WeightKg; LabelId = b.LabelId
                      ScaleReading = (if b.ScaleReadingId = Guid.Empty then None
                                      else findR b.ScaleReadingId)
                      At = b.At; By = b.By }) |> Seq.toList
            let sp =
                samples.Find(Query.EQ("BatchId", BsonValue((batchId: Guid))))
                |> Seq.sortBy (fun s -> s.SampledAt)
                |> Seq.map (fun s ->
                    { SampleId = s.Id; BlockNo = s.BlockNo
                      Position = { Layer = Mappings.layerFromInt s.Layer
                                   Radial = Mappings.radialFromInt s.Radial }
                      TemperatureC = s.TemperatureC; SampledAt = s.SampledAt; SampledBy = s.SampledBy
                      MoisturePct = (if s.HasMoisture then Some s.MoisturePct else None)
                      SaltPct = (if s.HasSalt then Some s.SaltPct else None)
                      MoistureReading = (if s.MoistureReadingId = Guid.Empty then None
                                         else findR s.MoistureReadingId)
                      SaltReading = (if s.SaltReadingId = Guid.Empty then None
                                     else findR s.SaltReadingId)
                      ResultPublishedAt = (if s.HasPublish then Some s.PublishedAt else None)
                      ResultPublishedBy = (if isNull s.PublishedBy then None else Some s.PublishedBy) })
                |> Seq.toList
            let creamReading =
                findR bd.CreamScaleReadingId
                |> Option.defaultValue
                    { Kind = Scale; Value = bd.CreamInKg; Stable = true
                      At = bd.StartedAt; Token = bd.CreamScaleReadingId; Raw = "(重建)" }
            Some { BatchId = bd.Id; CreamLot = bd.CreamLot; StartedAt = bd.StartedAt
                   OperatorName = bd.OperatorName; CreamInKg = bd.CreamInKg
                   CreamScaleReading = creamReading
                   Confirmations = cs; Materials = ms; SaltAdditions = ss
                   Blocks = bs; Samples = sp
                   CurrentStage = Mappings.stageFromInt bd.CurrentStage; Closed = bd.Closed }

    member _.ListAll() : (Guid * string * DateTime * string) list =
        batches.FindAll()
        |> Seq.sortByDescending (fun b -> b.StartedAt)
        |> Seq.map (fun b -> b.Id, b.CreamLot, b.StartedAt, b.OperatorName)
        |> Seq.toList

    member _.Dispose() = db.Dispose()
    interface IDisposable with member this.Dispose() = this.Dispose()
