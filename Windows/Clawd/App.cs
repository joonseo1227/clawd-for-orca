using Clawd.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.XamlTypeInfo;
using Microsoft.Windows.AppLifecycle;
using Microsoft.Windows.AppNotifications;

namespace Clawd;

/// <summary>
/// The WinUI application. Written without App.xaml, so it supplies the XAML type information the
/// XAML compiler would otherwise generate: WinUI's own controls are all it needs.
/// </summary>
public sealed class App : Application, IXamlMetadataProvider
{
    private readonly XamlControlsXamlMetaDataProvider _provider = new();
    private AppController? _controller;

    public App()
    {
        if (TestHooks.Appearance is { } look) RequestedTheme = look == "dark" ? ApplicationTheme.Dark : ApplicationTheme.Light;
        // Clawd lives in the notification area and a Win32 pet window, which WinUI doesn't count:
        // by default closing the last WinUI window (welcome, settings) would quit the app. Only
        // "Clawd 종료" does (AppController.Quit).
        DispatcherShutdownMode = DispatcherShutdownMode.OnExplicitShutdown;
        UnhandledException += (_, e) =>
        {
            Log.Error($"unhandled: {e.Exception}");
            e.Handled = true;   // a pet that keeps running beats one that vanishes
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Resources.MergedDictionaries.Add(new XamlControlsResources());
        UI.Ui.Install(Resources);
        _controller = new AppController();
        _controller.Start();

        // Launched by a toast click: act on it once everything is up.
        var activation = AppInstance.GetCurrent().GetActivatedEventArgs();
        if (activation.Kind == ExtendedActivationKind.AppNotification && activation.Data is AppNotificationActivatedEventArgs toast)
            _controller.Notifier.Handle(toast);
        // A second launch was redirected here: show the chat, as clicking the icon would.
        AppInstance.GetCurrent().Activated += (_, e) => _controller.Dispatcher.TryEnqueue(() =>
        {
            if (e.Kind == ExtendedActivationKind.AppNotification && e.Data is AppNotificationActivatedEventArgs t) _controller.Notifier.Handle(t);
            else if (e.Kind == ExtendedActivationKind.Launch && e.Data is Windows.ApplicationModel.Activation.ILaunchActivatedEventArgs l && Program.IsQuit(l.Arguments)) _controller.Quit();
            else _controller.OpenChat(fromTray: true);
        });
    }

    public IXamlType GetXamlType(Type type) => _provider.GetXamlType(type);
    public IXamlType GetXamlType(string fullName) => _provider.GetXamlType(fullName);
    public XmlnsDefinition[] GetXmlnsDefinitions() => _provider.GetXmlnsDefinitions();
}
