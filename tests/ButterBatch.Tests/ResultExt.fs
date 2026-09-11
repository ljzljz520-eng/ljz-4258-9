namespace ButterBatch.Tests

[<AutoOpen>]
module ResultHelpers =
    let okOrFail (r: Result<'T,'E>) : 'T =
        match r with
        | Ok v -> v
        | Error e -> failwithf "期望 Ok，实际 Error: %A" e
