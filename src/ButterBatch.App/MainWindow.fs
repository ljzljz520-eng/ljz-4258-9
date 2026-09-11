// ============================================================================
// ButterBatch.App / MainWindow.fs
// 桌面工位（纯代码构建 Avalonia 界面，无 XAML 编译依赖）。
// 选项卡：1) 操作员（确认奶油批/实际阶段、材料、加盐、分块）
//         2) 质量（冻结位置取样、发布水盐结果、点位离散 OxyPlot）
//         3) 设备桥接（Web Serial 授权、最近读数）
// 系统只做规则检查与留痕，不提供任何配方或搅拌参数。
// ============================================================================
namespace ButterBatch.App

open System
open System.Collections.ObjectModel
open Avalonia
open Avalonia.Controls
open Avalonia.Data
open Avalonia.Layout
open Avalonia.Media
open Avalonia.Threading
open OxyPlot
open OxyPlot.Avalonia
open OxyPlot.Series
open OxyPlot.Axes
open ButterBatch.Domain
open ButterBatch.Storage

/// 样品下拉项（显示文本 + 样品 Id）
type SampleOption(text: string, id: Guid) =
    member _.Id = id
    override _.ToString() = text

/// 本地批次下拉项
type BatchOption(text: string, id: Guid) =
    member _.Id = id
    override _.ToString() = text

/// 应用会话状态（领域聚合 + 本地仓储 + 待绑定的授权读数缓存）
type AppState(repo: BatchRepository, closureTol: Rules.ClosureConfig, maxSampleTemp: decimal) =
    let mutable batch: ButterBatch option = None
    let readings = ResizeArray<DeviceReading>()
    let changed = Event<unit>()
    member _.Repo = repo
    member _.Tolerance = closureTol
    member _.MaxSampleTemp = maxSampleTemp
    member _.Batch = batch
    member _.SetBatch b = batch <- b; changed.Trigger()
    member _.Tokens = repo.ValidTokens
    member _.Readings = readings
    member _.LatestReading(kind: DeviceKind) =
        readings |> Seq.tryFindBack (fun r -> r.Kind = kind && r.Stable)
    member _.AddReading(r: DeviceReading) =
        readings.Add r
        changed.Trigger()
    member _.Apply(f: ButterBatch -> Result<ButterBatch, RuleError>) =
        match batch with
        | None -> Error(Conflict "尚未创建/打开批次")
        | Some b ->
            match f b with
            | Ok b' ->
                repo.Save b'
                batch <- Some b'
                changed.Trigger()
                Ok b'
            | Error e -> Error e
    member _.Refresh() = changed.Trigger()
    [<CLIEvent>] member _.Changed = changed.Publish

module Ui =
    let brush (hex: string) = SolidColorBrush(Color.Parse hex)

    let label text =
        TextBlock(Text = text, VerticalAlignment = VerticalAlignment.Center,
                  Margin = Thickness(0., 6., 6., 6.))

    let header text =
        TextBlock(Text = text, FontSize = 15.0, FontWeight = FontWeight.Medium,
                  Margin = Thickness(0., 10., 0., 6.), Foreground = brush "#2b6cb0")

    let row (children: Control list) =
        let sp = StackPanel(Orientation = Orientation.Horizontal, Spacing = 8.0)
        children |> List.iter sp.Children.Add
        sp

    let col (children: Control list) =
        let sp = StackPanel(Orientation = Orientation.Vertical, Spacing = 4.0)
        children |> List.iter sp.Children.Add
        sp

    let textBox (watermark: string) (width: float) =
        TextBox(Watermark = watermark, Width = width,
                VerticalContentAlignment = VerticalAlignment.Center)

    let btn text (handler: Button -> unit) =
        let b = Button(Content = text, Padding = Thickness(10., 6., 10., 6.))
        b.Click.Add(fun _ -> handler b)
        b

    let statusBar (tb: TextBlock) ok msg =
        tb.Text <- msg
        tb.Foreground <- if ok then brush "#2f855a" else brush "#c53030"

    let scroll content =
        ScrollViewer(Content = content,
                     HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
                     VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                     Padding = Thickness(14.))

type MainWindow() as this =
    inherit Window()

    let repo = new BatchRepository("Filename=butterbatch.db;Connection=shared")
    let state =
        AppState(repo,
                 { AbsTolKg = 2.0m; RelTol = 0.015m },
                 maxSampleTemp = 12.0m)
    let bridge = new SerialBridge(repo, 8765)

    let status = TextBlock(TextWrapping = TextWrapping.Wrap, Margin = Thickness(8.))
    let setStatus = Ui.statusBar status

    // ---------- 控件 ----------
    let stageNames = ObservableCollection<string>(Stage.All |> List.map string)
    let layerNames = ObservableCollection<string>([ Surface; Middle; Core ] |> List.map string)
    let radialNames = ObservableCollection<string>([ Center; Edge ] |> List.map string)
    let blockNoItems = ObservableCollection<int>()
    let sampleItems = ObservableCollection<SampleOption>()
    let readingItems = ObservableCollection<string>()
    let batchItems = ObservableCollection<BatchOption>()
    let existingBatchBox = ComboBox(Width = 300.0, Items = batchItems)

    let tbCream = Ui.textBox "奶油批号" 150.0
    let tbOperator = Ui.textBox "操作员姓名" 120.0
    let tbCreamKg = Ui.textBox "奶油净重 kg" 110.0
    let stageBox = ComboBox(Width = 130.0)
    let tbStageNote = Ui.textBox "阶段备注（可选）" 200.0

    let matStage = ComboBox(Width = 120.0)
    let tbMatName = Ui.textBox "材料（如 酪乳/洗涤水）" 170.0
    let tbMatLot = Ui.textBox "批号（可选）" 120.0
    let tbMatSource = Ui.textBox "来源（可选）" 120.0
    let tbMatDest = Ui.textBox "去向（酪乳必填）" 140.0
    let tbMatQty = Ui.textBox "数量 kg（可选）" 120.0

    let tbSaltLot1 = Ui.textBox "盐批号（第一次）" 150.0
    let tbSaltKg1 = Ui.textBox "加盐量 kg" 100.0
    let tbSaltLot2 = Ui.textBox "盐批号（第二次）" 150.0
    let tbSaltKg2 = Ui.textBox "加盐量 kg" 100.0

    let tbBlockKg = Ui.textBox "块重 kg" 100.0
    let tbBlockLabel = Ui.textBox "标签号" 130.0

    let blockSample = ComboBox(Width = 110.0)
    let layerBox = ComboBox(Width = 100.0)
    let radialBox = ComboBox(Width = 100.0)
    let tbTemp = Ui.textBox "样品温度 ℃" 90.0
    let tbSampler = Ui.textBox "取样人" 100.0

    let sampleBox = ComboBox(Width = 260.0)
    let tbMoisture = Ui.textBox "水分 %" 80.0
    let tbSaltRes = Ui.textBox "盐分 %" 80.0
    let tbPublisher = Ui.textBox "发布人" 100.0

    let blockPlotBox = ComboBox(Width = 140.0)
    let plot = PlotView(Width = 760.0, Height = 360.0)
    let dispersionTb = TextBlock(TextWrapping = TextWrapping.Wrap, Margin = Thickness(0., 8., 0., 0.))

    let batchInfo = TextBlock(TextWrapping = TextWrapping.Wrap, Margin = Thickness(0., 0., 0., 8.))
    let materialGrid = DataGrid(Height = 150.0, IsReadOnly = true,
                                AutoGenerateColumns = false, CanUserSortColumns = false)
    let saltGrid = DataGrid(Height = 90.0, IsReadOnly = true, AutoGenerateColumns = false)
    let blockGrid = DataGrid(Height = 110.0, IsReadOnly = true, AutoGenerateColumns = false)
    let sampleGrid = DataGrid(Height = 220.0, IsReadOnly = true, AutoGenerateColumns = false)
    let readingList = ListBox(Height = 220.0)
    let bridgeInfo = TextBlock(TextWrapping = TextWrapping.Wrap)

    do
        this.Title <- "黄油搅拌批次与取样工位（规则检查 · 本地存储 · 授权设备）"
        this.Width <- 1180.0
        this.Height <- 820.0
        this.WindowStartupLocation <- WindowStartupLocation.CenterScreen
        stageBox.Items <- stageNames
        matStage.Items <- stageNames
        blockSample.Items <- blockNoItems
        layerBox.Items <- layerNames
        radialBox.Items <- radialNames
        sampleBox.Items <- sampleItems
        blockPlotBox.Items <- blockNoItems
        readingList.Items <- readingItems
        stageBox.SelectedIndex <- 0
        matStage.SelectedIndex <- 1
        layerBox.SelectedIndex <- 0
        radialBox.SelectedIndex <- 0

        this.Content <- this.BuildLayout()
        this.ConfigureGrids()
        bridge.Start()
        bridgeInfo.Text <-
            sprintf "桥接服务已启动（仅 127.0.0.1）。请在浏览器打开并分别授权台秤/水分仪：%s\n台秤令牌：%O\n水分仪令牌：%O"
                ("\n" + bridge.Url) bridge.ScaleToken bridge.MeterToken
        bridge.ReadingReceived.Add(fun args ->
            Dispatcher.UIThread.Post(fun () ->
                let r = args.Reading
                state.AddReading r
                readingItems.Insert(0,
                    sprintf "[%s] %s = %.3f 稳定=%s 令牌=%s"
                        (r.At.ToString "HH:mm:ss") (string r.Kind) r.Value
                        (if r.Stable then "是" else "否") ((string r.Token).Substring(0, 8)))
                setStatus true (sprintf "已接收%s读数 %.3f（%s）" (string r.Kind) r.Value
                                    (if r.Stable then "稳定" else "未稳定，规则将拒收"))
                this.RefreshAll()))
        bridge.ReadingRejected.Add(fun args ->
            Dispatcher.UIThread.Post(fun () ->
                setStatus false (sprintf "桥接拒收：%s（%s）" (string args.Reason) args.Raw)))
        state.Changed.Add(fun _ -> this.RefreshAll())
        this.Closing.Add(fun _ -> bridge.Stop(); (repo :> IDisposable).Dispose())
        this.LoadBatchList()
        this.RefreshAll()

    member private _.SelectedStage = Stage.All.[stageBox.SelectedIndex]
    member private _.SelectedMatStage = Stage.All.[matStage.SelectedIndex]
    member private _.SelectedLayer = [ Surface; Middle; Core ].[layerBox.SelectedIndex]
    member private _.SelectedRadial = [ Center; Edge ].[radialBox.SelectedIndex]

    member private this.BuildLayout() =
        let tabs = TabControl()
        tabs.Items <- [ this.OperatorTab(); this.QualityTab(); this.BridgeTab() ] :> Collections.IEnumerable
        let dock = DockPanel()
        let statusBorder = Border(BorderBrush = Brushes.LightGray, BorderThickness = Thickness(0., 1., 0., 0.),
                                 Child = status)
        DockPanel.SetDock(statusBorder, Dock.Bottom)
        dock.Children.Add statusBorder |> ignore
        dock.Children.Add tabs |> ignore
        dock

    // ---------- 操作员选项卡 ----------
    member private this.OperatorTab() =
        let create =
            Ui.col [
                Ui.header "① 操作员确认奶油批（创建批次）"
                Ui.row [ tbCream; tbOperator; tbCreamKg
                         Ui.btn "用最近台秤稳定读数填入净重" (fun _ -> this.FillCreamReading())
                         Ui.btn "创建批次" (fun _ -> this.CreateBatch()) ]
                TextBlock(Text = "奶油净重必须来自已授权台秤的稳定读数；台秤未稳定（稳定标志丢失）时不能创建。",
                          Foreground = Ui.brush "#666", TextWrapping = TextWrapping.Wrap)
                Ui.row [ Ui.label "打开本地批次："; existingBatchBox
                         Ui.btn "载入" (fun _ -> this.LoadBatch())
                         Ui.btn "刷新列表" (fun _ -> this.LoadBatchList()) ]
            ]
        let stage =
            Ui.col [
                Ui.header "② 确认实际阶段（奶油相转变 → 排酪乳 → 洗涤 → 加盐 → 捏合 → 分块）"
                Ui.row [ Ui.label "阶段："; stageBox; tbStageNote
                         Ui.btn "确认实际阶段" (fun _ -> this.ConfirmStage()) ]
            ]
        let material =
            Ui.col [
                Ui.header "③ 材料事件（温度/去向/读数与阶段对应；排酪乳必须登记去向）"
                Ui.row [ Ui.label "阶段"; matStage; tbMatName; tbMatLot; tbMatSource; tbMatDest; tbMatQty ]
                Ui.row [ Ui.btn "用最近台秤读数作为数量" (fun _ -> this.FillMatReading())
                         Ui.btn "登记材料事件" (fun _ -> this.LogMaterial()) ]
                materialGrid
            ]
        let salt =
            Ui.col [
                Ui.header "④ 加盐（可分两次；每次盐材料批号 + 稳定台秤读数）"
                Ui.row [ tbSaltLot1; tbSaltKg1; Ui.btn "第一次加盐" (fun _ -> this.AddSalt 1) ]
                Ui.row [ tbSaltLot2; tbSaltKg2; Ui.btn "第二次加盐" (fun _ -> this.AddSalt 2) ]
                saltGrid
            ]
        let blocks =
            Ui.col [
                Ui.header "⑤ 分块（每块唯一标签；重复标签视为标签互换）"
                Ui.row [ tbBlockKg; tbBlockLabel
                         Ui.btn "用最近台秤读数作为块重" (fun _ -> this.FillBlockReading())
                         Ui.btn "切下一块" (fun _ -> this.CutBlock()) ]
                blockGrid
            ]
        let item = TabItem(Header = "操作员")
        let panel = Ui.col [ batchInfo :> Control; create; stage; material; salt; blocks ]
        item.Content <- Ui.scroll panel
        item

    // ---------- 质量选项卡 ----------
    member private this.QualityTab() =
        let sampling =
            Ui.col [
                Ui.header "⑥ 质量人员在冻结位置取样（表面样不能代表整块均匀性）"
                Ui.row [ Ui.label "块号"; blockSample; Ui.label "位置：层"; layerBox; Ui.label "径向"; radialBox
                         tbTemp; tbSampler ]
                Ui.row [ Ui.btn "登记取样" (fun _ -> this.RecordSample()) ]
                TextBlock(Text = sprintf "样品温度超过 %.1f℃ 判为受热软化拒收；只能从冻结的 6 个点位取样。"
                                    state.MaxSampleTemp,
                          Foreground = Ui.brush "#666")
                sampleGrid
            ]
        let publish =
            Ui.col [
                Ui.header "⑦ 发布水盐结果（多点位；仪器读数须稳定授权）"
                Ui.row [ Ui.label "样品"; sampleBox; tbMoisture; tbSaltRes; tbPublisher
                         Ui.btn "发布该点结果" (fun _ -> this.PublishResult()) ]
            ]
        let chart =
            Ui.col [
                Ui.header "⑧ 批内点位离散（OxyPlot）"
                Ui.row [ Ui.label "块号"; blockPlotBox
                         Ui.btn "刷新图表" (fun _ -> this.UpdatePlot()) ]
                plot
                dispersionTb
                Ui.row [ Ui.btn "执行物料闭合 + 样品覆盖检查" (fun _ -> this.ReleaseCheck()) ]
            ]
        let item = TabItem(Header = "质量")
        item.Content <- Ui.scroll (Ui.col [ sampling; publish; chart ])
        item

    // ---------- 设备桥接选项卡 ----------
    member private _.BridgeTab() =
        let item = TabItem(Header = "设备桥接")
        let refreshInfo _ =
            bridgeInfo.Text <-
                sprintf "桥接服务已启动（仅 127.0.0.1）。请在浏览器打开并分别授权台秤/水分仪：%s\n台秤令牌：%O\n水分仪令牌：%O"
                    ("\n" + bridge.Url) bridge.ScaleToken bridge.MeterToken
        do refreshInfo ()
        let openBrowser =
            Ui.btn "打开浏览器授权页" (fun _ ->
                try
                    System.Diagnostics.Process.Start(
                        System.Diagnostics.ProcessStartInfo(bridge.Url, UseShellExecute = true)) |> ignore
                with ex -> setStatus false ("无法打开浏览器：" + ex.Message))
        let reissue =
            Ui.btn "重新签发令牌并刷新页面信息" (fun _ ->
                let ts, tm = bridge.IssueTokens()
                refreshInfo ()
                setStatus true (sprintf "已重新签发：台秤 %O / 水分仪 %O（旧令牌仍在有效期内可在仓储中撤销）" ts tm))
        let panel =
            Ui.col [
                Ui.header "Web Serial API 授权桥接（台秤 / 水分仪）"
                bridgeInfo
                openBrowser :> Control
                reissue :> Control
                TextBlock(Text = "操作流程：浏览器中点“选择串口”→ 选择设备完成授权 → 需要时点“发送当前稳定读数”。\n无效/过期令牌、设备种类不符、未稳定读数会被拒收并留痕。",
                          TextWrapping = TextWrapping.Wrap, Foreground = Ui.brush "#666")
                Ui.header "最近接收读数"
                readingList
            ]
        item.Content <- Ui.scroll panel
        item

    // ---------- 表格 ----------
    member private _.ConfigureGrids() =
        let col (binding: string) (header: string) (width: float) =
            DataGridTextColumn(Binding = Data.Binding binding, Header = header, Width = DataGridLength(width))
        materialGrid.Columns.Add(col "Stage" "阶段" 90.0) |> ignore
        materialGrid.Columns.Add(col "Material" "材料" 110.0) |> ignore
        materialGrid.Columns.Add(col "Lot" "批号" 100.0) |> ignore
        materialGrid.Columns.Add(col "Destination" "去向" 130.0) |> ignore
        materialGrid.Columns.Add(col "QtyText" "数量kg" 90.0) |> ignore
        materialGrid.Columns.Add(col "By" "登记人" 90.0) |> ignore
        saltGrid.Columns.Add(col "Ordinal" "次序" 50.0) |> ignore
        saltGrid.Columns.Add(col "SaltLot" "盐批号" 160.0) |> ignore
        saltGrid.Columns.Add(col "QuantityKg" "数量kg" 90.0) |> ignore
        blockGrid.Columns.Add(col "BlockNo" "块号" 50.0) |> ignore
        blockGrid.Columns.Add(col "LabelId" "标签号" 130.0) |> ignore
        blockGrid.Columns.Add(col "WeightKg" "重量kg" 90.0) |> ignore
        sampleGrid.Columns.Add(col "BlockNo" "块号" 50.0) |> ignore
        sampleGrid.Columns.Add(col "PositionText" "位置" 110.0) |> ignore
        sampleGrid.Columns.Add(col "TemperatureC" "温度℃" 70.0) |> ignore
        sampleGrid.Columns.Add(col "MoistureText" "水分%" 80.0) |> ignore
        sampleGrid.Columns.Add(col "SaltText" "盐分%" 80.0) |> ignore
        sampleGrid.Columns.Add(col "StatusText" "状态" 160.0) |> ignore

    // ---------- 数据刷新 ----------
    member private this.RefreshAll() =
        match state.Batch with
        | None ->
            batchInfo.Text <- "当前无打开批次。请先由操作员确认奶油批并创建。"
        | Some b ->
            batchInfo.Text <-
                sprintf "批次 %s ｜ 奶油批：%s ｜ 操作员：%s ｜ 当前实际阶段：%s ｜ 奶油净重 %.2f kg ｜ 加盐 %d 次 ｜ 产品块 %d ｜ 样品 %d"
                    ((string b.BatchId).Substring(0,8)) b.CreamLot b.OperatorName
                    (string b.CurrentStage) b.CreamInKg b.SaltAdditions.Length b.Blocks.Length b.Samples.Length
        let materialRows =
            state.Batch
            |> Option.map (fun b ->
                b.Materials |> List.map (fun m ->
                    {| Stage = string m.Stage; Material = m.Material
                       Lot = m.Lot |> Option.defaultValue ""
                       Destination = m.Destination |> Option.defaultValue ""
                       QtyText = m.QuantityKg |> Option.map string |> Option.defaultValue ""
                       By = m.By |}))
            |> Option.defaultValue []
        materialGrid.Items <- ObservableCollection(materialRows)
        let saltRows = state.Batch |> Option.map (fun b -> b.SaltAdditions) |> Option.defaultValue []
        saltGrid.Items <- ObservableCollection(saltRows)
        let blockRows = state.Batch |> Option.map (fun b -> b.Blocks) |> Option.defaultValue []
        blockGrid.Items <- ObservableCollection(blockRows)
        let sampleRows =
            state.Batch
            |> Option.map (fun b ->
                b.Samples |> List.map (fun s ->
                    {| BlockNo = s.BlockNo
                       PositionText = string s.Position
                       TemperatureC = s.TemperatureC
                       MoistureText = s.MoisturePct |> Option.map (sprintf "%.2f") |> Option.defaultValue "待发布"
                       SaltText = s.SaltPct |> Option.map (sprintf "%.2f") |> Option.defaultValue "待发布"
                       StatusText = if s.ResultPublishedAt.IsSome then "已发布" else "仅取样" |}))
            |> Option.defaultValue []
        sampleGrid.Items <- ObservableCollection(sampleRows)
        // 块号下拉
        blockNoItems.Clear()
        
        state.Batch |> Option.iter (fun b ->
            b.Blocks |> List.iter (fun x -> blockNoItems.Add x.BlockNo)
            if blockSample.ItemCount > 0 && blockSample.SelectedIndex < 0 then blockSample.SelectedIndex <- 0
            if blockPlotBox.ItemCount > 0 && blockPlotBox.SelectedIndex < 0 then blockPlotBox.SelectedIndex <- 0)
        // 样品下拉（未发布的）
        sampleItems.Clear()
        state.Batch |> Option.iter (fun b ->
            b.Samples |> List.iter (fun sm ->
                sampleItems.Add(SampleOption(sprintf "块%d %s 温度%.1f℃%s" sm.BlockNo (string sm.Position) sm.TemperatureC
                                        (if sm.ResultPublishedAt.IsSome then " (已发布)" else ""),
                                 sm.SampleId)) |> ignore))
        if sampleBox.ItemCount > 0 && sampleBox.SelectedIndex < 0 then sampleBox.SelectedIndex <- 0
        this.UpdatePlot()

    // ---------- 操作 ----------
    member private _.ReadingForKind kind = state.LatestReading kind

    member private this.LoadBatchList() =
        batchItems.Clear()
        repo.ListAll()
        |> List.iter (fun (id, cream, started, op) ->
            batchItems.Add(BatchOption(sprintf "%s ｜ %s ｜ %s ｜ %s"
                                        ((string id).Substring(0,8)) cream
                                        (started.ToString "MM-dd HH:mm") op, id)))
        if batchItems.Count > 0 then existingBatchBox.SelectedIndex <- 0

    member private this.LoadBatch() =
        if existingBatchBox.SelectedIndex < 0 then setStatus false "本地没有可打开的批次"
        else
            let opt = existingBatchBox.SelectedItem :?> BatchOption
            match repo.Load opt.Id with
            | Some b -> state.SetBatch (Some b); setStatus true (sprintf "已载入批次（奶油批 %s）" b.CreamLot)
            | None -> setStatus false "批次记录已损坏或不存在"

    member private this.FillCreamReading() =
        match this.ReadingForKind Scale with
        | Some r -> tbCreamKg.Text <- string r.Value; setStatus true "已填入最近台秤稳定净重"
        | None -> setStatus false "没有稳定台秤读数（稳定标志丢失或尚未授权）"

    member private this.CreateBatch() =
        let kgOk, kg = Decimal.TryParse tbCreamKg.Text
        let reading = state.LatestReading Scale
        if not kgOk then setStatus false "奶油净重不是有效数字"
        elif reading.IsNone then setStatus false "缺少稳定台秤读数，不能创建批次"
        else
            match Rules.createBatch tbCream.Text tbOperator.Text kg reading.Value
                                state.Tokens DateTime.Now with
            | Ok b -> state.SetBatch (Some b); repo.Save b
                      setStatus true (sprintf "批次已创建：奶油批 %s，当前阶段=奶油相转变" b.CreamLot)
            | Error e -> setStatus false (string e)

    member private this.ConfirmStage() =
        match state.Apply(fun b ->
            Rules.confirmStage b this.SelectedStage tbCream.Text tbOperator.Text
                (if String.IsNullOrWhiteSpace tbStageNote.Text then None else Some tbStageNote.Text)
                DateTime.Now) with
        | Ok b -> setStatus true (sprintf "已确认实际阶段：%s" (string b.CurrentStage))
        | Error e -> setStatus false (string e)

    member private _.FillMatReading() =
        match state.LatestReading Scale with
        | Some r -> tbMatQty.Text <- string r.Value
        | None -> setStatus false "没有稳定台秤读数"

    member private this.LogMaterial() =
        let opt (t: TextBox) = if String.IsNullOrWhiteSpace t.Text then None else Some t.Text
        let qty =
            match Decimal.TryParse tbMatQty.Text with
            | true, v -> Some v | false, _ -> None
        let reading =
            if qty.IsSome then state.LatestReading Scale else None
        let ev =
            { EventId = Guid.NewGuid(); Stage = this.SelectedMatStage; At = DateTime.Now
              Material = tbMatName.Text; Lot = opt tbMatLot
              Source = opt tbMatSource; Destination = opt tbMatDest
              QuantityKg = qty; ScaleReading = reading; By = tbOperator.Text }
        match state.Apply(fun b -> Rules.logMaterial b ev state.Tokens) with
        | Ok _ -> setStatus true "材料事件已登记并与阶段对应"
        | Error e -> setStatus false (string e)

    member private this.AddSalt ordinal =
        let lot, kgText =
            if ordinal = 1 then tbSaltLot1.Text, tbSaltKg1.Text
            else tbSaltLot2.Text, tbSaltKg2.Text
        let kgOk, kg = Decimal.TryParse kgText
        let reading = state.LatestReading Scale
        if not kgOk then setStatus false "加盐量不是有效数字"
        elif reading.IsNone then setStatus false "缺少稳定台秤读数（加盐必须称重）"
        else
            match state.Apply(fun b ->
                // 两次加盐必须顺序发生
                if ordinal <> b.SaltAdditions.Length + 1 then
                    Error(SaltOrdinalConflict ordinal)
                else Rules.addSalt b lot kg reading.Value state.Tokens
                            tbOperator.Text DateTime.Now) with
            | Ok _ -> setStatus true (sprintf "第 %d 次加盐已登记（批号 %s）" ordinal lot)
            | Error e -> setStatus false (string e)

    member private _.FillBlockReading() =
        match state.LatestReading Scale with
        | Some r -> tbBlockKg.Text <- string r.Value
        | None -> setStatus false "没有稳定台秤读数"

    member private this.CutBlock() =
        let kgOk, kg = Decimal.TryParse tbBlockKg.Text
        let reading = state.LatestReading Scale
        if not kgOk then setStatus false "块重不是有效数字"
        elif reading.IsNone then setStatus false "缺少稳定台秤读数（分块必须称重）"
        else
            match state.Apply(fun b ->
                Rules.cutBlock b kg tbBlockLabel.Text reading state.Tokens
                              tbOperator.Text DateTime.Now) with
            | Ok _ -> setStatus true "产品块已切分并绑定唯一标签"
            | Error e -> setStatus false (string e)

    member private this.RecordSample() =
        if blockSample.SelectedIndex < 0 then setStatus false "请先分块"
        else
            let blockNo = blockSample.SelectedItem :?> int
            match Decimal.TryParse tbTemp.Text with
            | false, _ -> setStatus false "样品温度不是有效数字"
            | true, temp ->
                let pos = { Layer = this.SelectedLayer; Radial = this.SelectedRadial }
                match state.Apply(fun b ->
                    Rules.recordSample b blockNo pos temp state.MaxSampleTemp
                        tbSampler.Text DateTime.Now) with
                | Ok _ -> setStatus true (sprintf "已在冻结位置 %s 取样" (string pos))
                | Error e -> setStatus false (string e)

    member private this.PublishResult() =
        if sampleBox.SelectedIndex < 0 then setStatus false "请选择样品"
        else
            let item = sampleBox.SelectedItem :?> SampleOption
            let sampleId = item.Id
            let mOk, moisture = Decimal.TryParse tbMoisture.Text
            let sOk, salt = Decimal.TryParse tbSaltRes.Text
            let mReading = state.LatestReading MoistureMeter
            let sReading = state.LatestReading Scale
            if not mOk then setStatus false "水分结果不是有效数字"
            elif not sOk then setStatus false "盐分结果不是有效数字"
            elif mReading.IsNone then setStatus false "缺少水分仪稳定/完成读数"
            elif sReading.IsNone then setStatus false "缺少台秤稳定读数（盐分称量）"
            else
                match state.Apply(fun b ->
                    // 水分来自水分仪；盐分结果也须有对应稳定称量读数
                    Rules.publishResult b sampleId moisture mReading.Value salt sReading.Value
                                        state.Tokens tbPublisher.Text DateTime.Now) with
                | Ok _ -> setStatus true "水盐结果已发布（多点）"
                | Error e -> setStatus false (string e)

    // ---------- 物料闭合 / 覆盖检查 ----------
    member private this.ReleaseCheck() =
        match state.Batch with
        | None -> setStatus false "无批次"
        | Some b ->
            match Rules.readyToRelease b state.Tolerance with
            | Ok () -> setStatus true "检查通过：酪乳已排净、标签无互换、样品覆盖充分、水盐结果齐全、物料闭合。"
            | Error es ->
                es |> List.iter (fun e -> printfn "阻塞项：%s" (string e))
                setStatus false ("不能发布，阻塞项：\n" + String.Join("\n", es |> List.map string))

    // ---------- 图表 ----------
    member private this.UpdatePlot() =
        let model = PlotModel()
        model.Title <- "批内多点水分/盐分离散"
        let categoryAxis = CategoryAxis(Position = AxisPosition.Bottom, Key = "Positions")
        model.Axes.Add categoryAxis
        let valueAxis = LinearAxis(Position = AxisPosition.Left, Title = "质量分数 %")
        model.Axes.Add valueAxis
        let samples =
            match state.Batch with
            | Some b ->
                if blockPlotBox.SelectedIndex >= 0 then
                    let no = blockPlotBox.SelectedItem :?> int
                    b.Samples |> List.filter (fun s -> s.BlockNo = no)
                else b.Samples
            | None -> []
        let labels = System.Collections.Generic.List<string>()
        samples |> List.iter (fun s -> labels.Add(string s.Position))
        categoryAxis.ItemsSource <- labels

        // 点位数据（匿名记录供 OxyPlot 反射绑定）
        let moisturePts =
            System.Collections.Generic.List(
                samples |> List.mapi (fun i s ->
                    s.MoisturePct
                    |> Option.map (fun v -> {| X = float i; Y = float v |})
                    |> Option.defaultValue {| X = float i; Y = 0.0 |}))
        let saltPts =
            System.Collections.Generic.List(
                samples |> List.mapi (fun i s ->
                    s.SaltPct
                    |> Option.map (fun v -> {| X = float i + 0.32; Y = float v |})
                    |> Option.defaultValue {| X = float i + 0.32; Y = 0.0 |}))

        // OxyPlot 2.1 使用 LinearBarSeries（旧版 ColumnSeries 的替代）
        let mkBar title color (pts: System.Collections.Generic.List<_>) =
            let bar = LinearBarSeries()
            bar.Title <- title
            bar.FillColor <- color
            bar.BarWidth <- 0.3
            bar.ItemsSource <- pts
            bar.DataFieldX <- "X"
            bar.DataFieldY <- "Y"
            bar
        let moisture = mkBar "水分 %" (OxyColor.FromRgb(43uy, 108uy, 176uy)) moisturePts
        let saltS = mkBar "盐分 %" (OxyColor.FromRgb(213uy, 128uy, 40uy)) saltPts
        model.Series.Add moisture
        model.Series.Add saltS
        model.IsLegendVisible <- true
        plot.Model <- model
        match Rules.moistureDispersion samples, Rules.saltDispersion samples with
        | Some md, Some sd ->
            dispersionTb.Text <-
                sprintf "水分：n=%d 均值 %.2f%% 极差 %.2f%% 标准差 %.3f　｜　盐分：n=%d 均值 %.2f%% 极差 %.2f%% 标准差 %.3f"
                    md.Count md.Mean md.Range md.StdDev
                    sd.Count sd.Mean sd.Range sd.StdDev
        | Some md, None ->
            dispersionTb.Text <-
                sprintf "水分：n=%d 均值 %.2f%% 极差 %.2f%% 标准差 %.3f（盐分结果未发布）"
                    md.Count md.Mean md.Range md.StdDev
        | _ -> dispersionTb.Text <- "尚无已发布的多点结果；表面样不能代表整块均匀性。"
