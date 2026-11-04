using MailRouter.Web.Data;
using MailRouter.Web.Services;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

// Same number/date parsing everywhere (fr-CH/de-CH hosts would otherwise reject "0.7").
System.Globalization.CultureInfo.DefaultThreadCurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
System.Globalization.CultureInfo.DefaultThreadCurrentUICulture = System.Globalization.CultureInfo.InvariantCulture;

var builder = WebApplication.CreateBuilder(args);

var dataDir = builder.Configuration["DataDirectory"] ?? Path.Combine(AppContext.BaseDirectory, "data");
Directory.CreateDirectory(dataDir);

builder.Services.Configure<GraphOptions>(builder.Configuration.GetSection("Graph"));
builder.Services.PostConfigure<GraphOptions>(o =>
{
    if (string.IsNullOrWhiteSpace(o.TokenCachePath))
        o.TokenCachePath = Path.Combine(dataDir, "msal_cache.bin");
});
builder.Services.Configure<AiOptions>(builder.Configuration.GetSection("Anthropic"));
builder.Services.PostConfigure<AiOptions>(o =>
{
    if (string.IsNullOrWhiteSpace(o.ApiKey))
        o.ApiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY") ?? "";
});

builder.Services.AddDbContext<AppDbContext>(o => o.UseSqlite($"Data Source={Path.Combine(dataDir, "mailrouter.db")}"));
builder.Services.AddSingleton<GraphAuthService>();
builder.Services.AddSingleton<AiService>();
builder.Services.AddHttpClient<GraphClient>();
builder.Services.AddScoped<ProcessingService>();
builder.Services.AddHostedService<PollingWorker>();

// Keep login cookies valid across container restarts.
builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataDir, "keys")));

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o =>
    {
        o.LoginPath = "/Login";
        o.ExpireTimeSpan = TimeSpan.FromDays(7);
        o.SlidingExpiration = true;
    });
builder.Services.AddAuthorization();
builder.Services.AddRazorPages(o =>
{
    o.Conventions.AuthorizeFolder("/");
    o.Conventions.AllowAnonymousToPage("/Login");
    o.Conventions.AllowAnonymousToPage("/Error");
});

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.EnsureCreated();

    // One-time upgrade: replace the old English-only footer with the FR/DE/EN one, unless it was customised.
    var settings = db.GetSettingsAsync().GetAwaiter().GetResult();
    if (settings.ReplyFooter == AppSettings.LegacyFooter)
    {
        settings.ReplyFooter = AppSettings.DefaultFooter;
        db.SaveChanges();
    }
}

if (string.IsNullOrWhiteSpace(app.Configuration["Admin:Password"]))
    app.Logger.LogWarning("Admin:Password is not set — the dashboard cannot be logged into. Set ADMIN__PASSWORD.");

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
}

app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.MapRazorPages();
app.MapGet("/health", () => Results.Ok("ok")).AllowAnonymous();

app.Run();
