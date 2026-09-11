namespace ButterBatch.App

open System
open Avalonia
open Avalonia.Controls.ApplicationLifetimes
open Avalonia.Markup.Xaml.Styling
open Avalonia.Styling

type App() =
    inherit Application()
    override this.Initialize() =
        let inc = StyleInclude(baseUri = null, Source = Uri("avares://Avalonia.Themes.Fluent/FluentDark.xaml"))
        this.Styles.Add inc
        let acc = StyleInclude(baseUri = null, Source = Uri("avares://Avalonia.Themes.Fluent/Accents/FluentControlResourcesLight.xaml"))
        this.Styles.Add acc
    override this.OnFrameworkInitializationCompleted() =
        match this.ApplicationLifetime with
        | :? IClassicDesktopStyleApplicationLifetime as desktop ->
            desktop.MainWindow <- MainWindow()
        | _ -> ()
        base.OnFrameworkInitializationCompleted()

module Program =
    [<CompiledName "BuildAvaloniaApp">]
    let buildAvaloniaApp () =
        AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace()

    [<EntryPoint; System.STAThread>]
    let main argv =
        buildAvaloniaApp().StartWithClassicDesktopLifetime(argv)
