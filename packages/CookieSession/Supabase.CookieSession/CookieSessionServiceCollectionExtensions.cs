using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Supabase.CookieSession;

/// <summary>Registers the refresh-cookie companion. The host still chooses a per-circuit <see cref="ICookieSession"/>.</summary>
public static class CookieSessionServiceCollectionExtensions
{
    public static IServiceCollection AddCookieSession(this IServiceCollection services, Action<CookieSessionOptions>? configure = null)
    {
        var options = new CookieSessionOptions();
        configure?.Invoke(options);
        services.AddSingleton(options);
        services.AddHttpContextAccessor();
        services.AddSupabaseBrowserSession(memory =>
        {
            memory.SupabaseUrl = options.SupabaseUrl;
            memory.AnonKey = options.AnonKey;
            memory.RefreshSkew = options.RefreshSkew;
        });
        services.AddSingleton<CookieJar>();
        services.AddSingleton<RefreshAuthority>();
        services.TryAddSingleton<IAuthTokenClient>(provider => new GotrueAuthTokenClient(provider.GetRequiredService<CookieSessionOptions>()));
        services.AddScoped<RequestSession>();
        services.AddScoped<PrerenderSessionBootstrap>();
        services.AddScoped<HybridCookieSession>();
        services.AddScoped<ICookieSession>(provider => provider.GetRequiredService<HybridCookieSession>());
        return services;
    }
}
