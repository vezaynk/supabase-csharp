using Microsoft.AspNetCore.Components.Infrastructure;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Supabase.CookieSession;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.Services.AddSupplyValueFromPersistentComponentStateProvider();

// The URL and anon key are read from the host page (cookieSession.publicConfig) when these are empty.
builder.Services.AddSupabaseBrowserSession(options =>
{
    options.SupabaseUrl = builder.Configuration["CookieSession:SupabaseUrl"] ?? "";
    options.AnonKey = builder.Configuration["CookieSession:AnonKey"] ?? "";
});

await builder.Build().RunAsync();
