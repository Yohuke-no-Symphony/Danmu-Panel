using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Themes.Fluent;
using Dock.Avalonia.Themes.Fluent;
using Yohuke.DanmuPanel.I18n;

[assembly: AvaloniaTestApplication(typeof(Yohuke.DanmuPanel.Tests.PanelTestApplication))]

namespace Yohuke.DanmuPanel.Tests;

public sealed class PanelTestApplication : Application
{
    public static AppBuilder BuildAvaloniaApp()
    {
        return AppBuilder.Configure<PanelTestApplication>().UseSkia().WithInterFont()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
    }

    public override void Initialize()
    {
        if (!Localization.IsInitialized)
        {
            Localization.Initialize(Path.Combine(AppContext.BaseDirectory, "i18n"));
        }
        Styles.Add(new FluentTheme());
        Styles.Add(new DockFluentTheme());
        Styles.Add(new StyleInclude(new Uri("avares://Yohuke.DanmuPanel/"))
        {
            Source = new("avares://Avalonia.Controls.DataGrid/Themes/Fluent.xaml")
        });
        Styles.Add(new StyleInclude(new Uri("avares://Yohuke.DanmuPanel/"))
        {
            Source = new("avares://Yohuke.DanmuPanel/Views/Styles/WorkbenchTheme.axaml")
        });
    }
}
