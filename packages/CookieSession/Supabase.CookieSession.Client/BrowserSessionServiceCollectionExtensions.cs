using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Supabase.CookieSession;

/// <summary>Registers the in-memory SDK client used by WASM and, via the server companion, by Interactive Server.</summary>
public static class BrowserSessionServiceCollectionExtensions
{
    public static IServiceCollection AddSupabaseBrowserSession(this IServiceCollection services, Action<SupabaseMemoryOptions>? configure = null)
    {
        var options = new SupabaseMemoryOptions();
        configure?.Invoke(options);
        services.TryAddSingleton(options);
        services.TryAddScoped<MemoryAccessTokenStore>();
        services.TryAddScoped<BrowserCookieSession>();
        services.TryAddScoped<ICookieSession>(provider => provider.GetRequiredService<BrowserCookieSession>());
        return services;
    }
}
