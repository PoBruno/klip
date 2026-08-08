using Wpf.Ui.Abstractions;

namespace Klip.App.Services;

/// <summary>ADR-S.03/S.06: resolve paginas do NavigationView pelo container de DI.</summary>
public sealed class NavigationPageProvider(IServiceProvider services) : INavigationViewPageProvider
{
    public object? GetPage(Type pageType) => services.GetService(pageType);
}
