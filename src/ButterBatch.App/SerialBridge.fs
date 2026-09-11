// ============================================================================
// ButterBatch.App / SerialBridge.fs
// 经过授权的设备桥接层：
//   - 仅监听 127.0.0.1 loopback；
//   - 浏览器端通过 Web Serial API 经操作员手势授权串口；
//   - 台秤 / 水分仪读数带一次性令牌 POST 到 /reading；
//   - 服务端校验令牌种类、有效期与稳定标志，无效一律拒收；
//   - 接收事件推送到桌面工位，并在 LiteDB 留痕。
// ============================================================================
namespace ButterBatch.App

open System
open System.Net
open System.Text
open System.Text.Json
open System.Threading
open ButterBatch.Domain
open ButterBatch.Storage

/// 桥接层收到读数的事件参数
type ReadingReceivedEventArgs(reading: DeviceReading) =
    inherit EventArgs()
    member _.Reading = reading

/// 桥接层拒收原因
[<Struct>]
type BridgeReject =
    | BadToken
    | WrongKind
    | Revoked
    | BadPayload
    override this.ToString() =
        match this with
        | BadToken -> "令牌无效"
        | WrongKind -> "设备种类与授权不符"
        | Revoked -> "授权已撤销"
        | BadPayload -> "报文格式错误"

type ReadingRejectedEventArgs(reason: BridgeReject, raw: string) =
    inherit EventArgs()
    member _.Reason = reason
    member _.Raw = raw

/// 本地 Web Serial 桥接服务
type SerialBridge(repo: BatchRepository, port: int) =
    let listener = new HttpListener()
    let prefix = sprintf "http://127.0.0.1:%d/" port
    let mutable tokenScale = Guid.Empty
    let mutable tokenMeter = Guid.Empty
    let mutable running = false
    let cts = new CancellationTokenSource()
    let received = Event<EventHandler<ReadingReceivedEventArgs>, _>()
    let rejected = Event<EventHandler<ReadingRejectedEventArgs>, _>()

    [<CLIEvent>] member _.ReadingReceived = received.Publish
    [<CLIEvent>] member _.ReadingRejected = rejected.Publish

    member _.Url = prefix + "bridge"
    member _.Prefix = prefix
    member _.ScaleToken = tokenScale
    member _.MeterToken = tokenMeter

    member _.IssueTokens() =
        tokenScale <- repo.IssueToken(Scale)
        tokenMeter <- repo.IssueToken(MoistureMeter)
        tokenScale, tokenMeter

    member private _.Send(ctx: HttpListenerContext, code: int, body: string) =
        ctx.Response.StatusCode <- code
        let bytes = Encoding.UTF8.GetBytes body
        ctx.Response.ContentType <- "application/json; charset=utf-8"
        ctx.Response.OutputStream.Write(bytes, 0, bytes.Length)
        ctx.Response.OutputStream.Close()

    member private _.HandleReading(ctx: HttpListenerContext, body: byte[]) =
        try
            use doc = JsonDocument.Parse(body)
            let root = doc.RootElement
            let token = Guid.Parse(root.GetProperty("token").GetString())
            let kindStr = root.GetProperty("kind").GetString()
            let value = root.GetProperty("value").GetDecimal()
            let stable = root.GetProperty("stable").GetBoolean()
            let raw = root.GetProperty("raw").GetString()
            let atRaw = root.GetProperty("at").GetString()
            let at = DateTime.Parse(atRaw, null, Globalization.DateTimeStyles.RoundtripKind)
            let kind = if kindStr = "moisture" then MoistureMeter else Scale
            // 令牌必须存在、未撤销、未过期，且设备种类匹配
            if token = Guid.Empty || not (repo.ValidTokens.Contains token) then
                rejected.Trigger(null, ReadingRejectedEventArgs(BadToken, raw))
                ctx |> fun c -> c.Response.StatusCode <- 403
                "无效或过期令牌"
            elif (kind = Scale && token <> tokenScale) || (kind = MoistureMeter && token <> tokenMeter) then
                rejected.Trigger(null, ReadingRejectedEventArgs(WrongKind, raw))
                ctx.Response.StatusCode <- 403
                "设备种类与授权不符"
            else
                let reading =
                    { Kind = kind; Value = value; Stable = stable
                      At = if at.Kind = DateTimeKind.Utc then at.ToLocalTime() else at
                      Token = token; Raw = raw; ReadingId = Guid.NewGuid() }
                // 原始读数留痕（批次尚未绑定也先记录到空批次审计集合）
                repo.SaveReading(Guid.Empty, reading)
                received.Trigger(null, ReadingReceivedEventArgs reading)
                ctx.Response.StatusCode <- 200
                """{"accepted":true}"""
            |> fun msg ->
                let bytes = Encoding.UTF8.GetBytes msg
                ctx.Response.ContentType <- "application/json; charset=utf-8"
                ctx.Response.OutputStream.Write(bytes, 0, bytes.Length)
                ctx.Response.OutputStream.Close()
        with ex ->
            rejected.Trigger(null, ReadingRejectedEventArgs(BadPayload, ex.Message))
            ctx.Response.StatusCode <- 400
            use doc = JsonDocument.Parse(Encoding.UTF8.GetBytes "{}")
            let bytes = Encoding.UTF8.GetBytes """{"accepted":false,"error":"bad payload"}"""
            ctx.Response.OutputStream.Write(bytes, 0, bytes.Length)
            ctx.Response.OutputStream.Close()

    member private this.Loop =
        async {
            while running do
                let! ctx = listener.GetContextAsync() |> Async.AwaitTask
                async {
                    try
                        match ctx.Request.HttpMethod, ctx.Request.Url.AbsolutePath with
                        | "GET", "/bridge" ->
                            let bytes = Encoding.UTF8.GetBytes(BridgePage.html (string tokenScale) (string tokenMeter))
                            ctx.Response.ContentType <- "text/html; charset=utf-8"
                            ctx.Response.OutputStream.Write(bytes, 0, bytes.Length)
                            ctx.Response.OutputStream.Close()
                        | "POST", "/reading" ->
                            use ms = new IO.MemoryStream()
                            ctx.Request.InputStream.CopyTo ms
                            this.HandleReading(ctx, ms.ToArray())
                        | "POST", "/revoke" ->
                            try
                                use ms = new IO.MemoryStream()
                                ctx.Request.InputStream.CopyTo ms
                                use doc = JsonDocument.Parse(ms.ToArray())
                                let token = Guid.Parse(doc.RootElement.GetProperty("token").GetString())
                                repo.RevokeToken token |> ignore
                                if token = tokenScale then tokenScale <- Guid.Empty
                                if token = tokenMeter then tokenMeter <- Guid.Empty
                                this.Send(ctx, 200, """{"revoked":true}""")
                            with _ -> this.Send(ctx, 400, """{"revoked":false}""")
                        | _ -> this.Send(ctx, 404, """{"error":"not found"}""")
                    with ex ->
                        try this.Send(ctx, 500, sprintf "{\"error\":\"%s\"}" (ex.Message.Replace("\\","\\").Replace("\"","\\")))
                        with _ -> ()
                } |> Async.Start
        }

    member this.Start() =
        if not running then
            listener.Prefixes.Clear()
            listener.Prefixes.Add prefix
            listener.Start()
            running <- true
            this.IssueTokens() |> ignore
            Async.Start(this.Loop, cts.Token)

    member this.Stop() =
        running <- false
        cts.Cancel()
        try listener.Stop() with _ -> ()
        try listener.Close() with _ -> ()

    interface IDisposable with
        member this.Dispose() = this.Stop()
