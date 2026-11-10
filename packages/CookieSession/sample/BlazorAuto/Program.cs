using System.Text.Encodings.Web;
using System.Text.Json;
using BlazorAuto.Components;
using Microsoft.AspNetCore.DataProtection;
using Supabase.CookieSession;

var builder = WebApplication.CreateBuilder(args);

var keyRing = Path.Combine(builder.Environment.ContentRootPath, ".data-protection-keys");
Directory.CreateDirectory(keyRing);
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(keyRing))
    .SetApplicationName("Supabase.CookieSession.Sample");

builder.Services.AddCookieSession(options =>
{
    builder.Configuration.GetSection("CookieSession").Bind(options);
    var url = Environment.GetEnvironmentVariable("SUPABASE_URL");
    var key = Environment.GetEnvironmentVariable("SUPABASE_ANON_KEY");
    if (!string.IsNullOrWhiteSpace(url))
        options.SupabaseUrl = url;
    if (!string.IsNullOrWhiteSpace(key))
        options.AnonKey = key;
});
builder.Services.AddAntiforgery();
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents()
    .AddInteractiveWebAssemblyComponents();

var app = builder.Build();

if (app.Environment.IsDevelopment())
    app.UseWebAssemblyDebugging();
else
    app.UseExceptionHandler("/Error", createScopeForErrors: true);

// Re-executing /not-found keeps the original POST and its JSON content type. The Razor
// endpoint then answers 400 "The request has an incorrect Content-type." and hides the
// real /auth status, which the browser reports as "token request failed".
app.UseWhen(
    http => !http.Request.Path.StartsWithSegments("/auth"),
    branch => branch.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true));
app.UseAntiforgery();
app.UsePrerenderSession();
app.MapCookieSessionEndpoints();
app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .AddInteractiveWebAssemblyRenderMode()
    .AddAdditionalAssemblies(typeof(BlazorAuto.Client._Imports).Assembly);

app.Run();

/// <summary>Entry point marker so tests can host this app.</summary>
public partial class Program
{
}

/// <summary>JSON embedded in the host page. Public project coordinates only.</summary>
public static class PublicConfigJson
{
    public static string Serialize(CookieSessionOptions options) =>
        JsonSerializer.Serialize(new PublicProject { Url = options.SupabaseUrl, AnonKey = options.AnonKey },
            new JsonSerializerOptions { Encoder = JavaScriptEncoder.Default });
}
