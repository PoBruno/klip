using Microsoft.Extensions.DependencyInjection;
using Wpf.Ui.Abstractions;

namespace Klip.App.Services;

/// <summary>
/// Resolves settings pages from the DI container. A page that is not registered is
/// still built through the container (so its dependencies get injected) and kept, which
/// gives it the same singleton lifetime as the registered ones.
/// </summary>
public sealed class NavigationPageProvider(IServiceProvider services) : INavigationViewPageProvider
{
    private readonly Dictionary<Type, object> _unregistered = [];

    public object? GetPage(Type pageType)
    {
        var page = services.GetService(pageType);
        if (page is not null)
            return page;

        if (!_unregistered.TryGetValue(pageType, out page))
        {
            page = ActivatorUtilities.CreateInstance(services, pageType);
            _unregistered[pageType] = page;
        }

        return page;
    }
}
