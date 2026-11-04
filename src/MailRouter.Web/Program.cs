using MailRouter.Web.Data;
using MailRouter.Web.Services;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

// Same number/date parsing everywhere (fr-CH/de-CH hosts would otherwise reject "0.7").
System.Globalization.CultureInfo.DefaultThreadCurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
System.Globalization.CultureInfo.DefaultThreadCurrentUICulture = System.Globalization.CultureInfo.InvariantCulture;

var builder = WebApplication.CreateBuilder(args);

var dataDir = builder.Configuration["DataDirectory"] ?? Path.Combine(AppContext.BaseDirectory, "App_Data");
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

    // Lightweight schema upgrades for databases created by earlier versions (EnsureCreated doesn't alter tables).
    {
        var conn = db.Database.GetDbConnection();
        conn.Open();
        bool EnsureColumn(string table, string column, string ddl)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = '{column}'";
            if (Convert.ToInt32(cmd.ExecuteScalar()) != 0) return false;
            using var alter = conn.CreateCommand();
            alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {ddl}";
            alter.ExecuteNonQuery();
            return true;
        }
        void SetSetting(string column, string value)
        {
            using var set = conn.CreateCommand();
            set.CommandText = $"UPDATE Settings SET {column} = $v";
            var p = set.CreateParameter(); p.ParameterName = "$v"; p.Value = value;
            set.Parameters.Add(p);
            set.ExecuteNonQuery();
        }
        if (EnsureColumn("Settings", "RedirectNotice", "TEXT NULL")) SetSetting("RedirectNotice", AppSettings.DefaultRedirectNotice);
        if (EnsureColumn("Settings", "ForwardNotice", "TEXT NULL")) SetSetting("ForwardNotice", AppSettings.DefaultForwardNotice);
        EnsureColumn("Messages", "ForwardNote", "TEXT NULL");
        EnsureColumn("Messages", "ForwardSubject", "TEXT NULL");
        EnsureColumn("Messages", "ForwardBody", "TEXT NULL");
        EnsureColumn("Messages", "AiIsSensitive", "INTEGER NULL");
        EnsureColumn("Rules", "Exceptions", "TEXT NULL");
        EnsureColumn("Settings", "MarkEmailAsRead", "INTEGER NOT NULL DEFAULT 1");
        EnsureColumn("Settings", "MarkTeamsAsRead", "INTEGER NOT NULL DEFAULT 1");
        EnsureColumn("Settings", "HandledFolder", "TEXT NULL DEFAULT 'AI-Routed'");
        EnsureColumn("Rules", "MoveToFolder", "TEXT NULL");
    }

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
