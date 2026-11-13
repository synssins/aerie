using System.Reflection;
using Kb.Core.Access;
using Kb.Core.Audit;
using Kb.Core.Config;
using Kb.Core.Identity;
using Kb.Core.Notifications;
using Kb.Core.Plugins;
using Kb.Core.Spaces;
using Kb.Data;
using Kb.Plugins;
using Kb.Web;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);
var services = builder.Services;
var config = builder.Configuration;

// ---- configuration (fail fast) ----
services.AddOptions<KbOptions>().Bind(config.GetSection(KbOptions.Section)).ValidateOnStart();
services.AddSingleton<IValidateOptions<KbOptions>, KbOptionsValidator>();
var kb = config.GetSection(KbOptions.Section).Get<KbOptions>() ?? new KbOptions();

// ---- database ----
services.AddDbContext<KbDbContext>(o => _ = kb.Database.Provider switch
{
    DatabaseProvider.Sqlite => o.UseSqlite(kb.Database.ConnectionString),
    _ => o.UseSqlServer(kb.Database.ConnectionString, s => s.EnableRetryOnFailure()),
});

// ---- identity (local accounts; Entra wired in Task 6) ----
services.AddIdentity<KbUser, KbRole>(o =>
    {
        // NIST SP 800-63B: length over composition rules.
        o.Password.RequiredLength = 15;
        o.Password.RequireDigit = o.Password.RequireLowercase = o.Password.RequireUppercase = o.Password.RequireNonAlphanumeric = false;
        o.Lockout.MaxFailedAccessAttempts = 5;
        o.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
        o.Lockout.AllowedForNewUsers = true;
        o.User.RequireUniqueEmail = false;
    })
    .AddEntityFrameworkStores<KbDbContext>()
    .AddDefaultTokenProviders();

services.ConfigureApplicationCookie(o =>
{
    o.Cookie.Name = "__Host-kb";
    o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    o.Cookie.SameSite = SameSiteMode.Lax;
    o.Cookie.HttpOnly = true;
    o.SlidingExpiration = true;
    o.ExpireTimeSpan = TimeSpan.FromHours(8);
    // API callers get status codes, never login redirects.
    o.Events.OnRedirectToLogin = ctx => { ctx.Response.StatusCode = StatusCodes.Status401Unauthorized; return Task.CompletedTask; };
    o.Events.OnRedirectToAccessDenied = ctx => { ctx.Response.StatusCode = StatusCodes.Status403Forbidden; return Task.CompletedTask; };
});

// API tokens beside the sign-in cookie: a request with a Bearer header goes to the token handler, every other one to the cookie.
services.AddAuthentication()
    .AddPolicyScheme(ApiTokenAuth.Selector, null, o => o.ForwardDefaultSelector = ctx => ApiTokenAuth.HasBearer(ctx.Request) ? ApiTokenAuth.Scheme : IdentityConstants.ApplicationScheme)
    .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, ApiTokenAuthHandler>(ApiTokenAuth.Scheme, null);
services.Configure<Microsoft.AspNetCore.Authentication.AuthenticationOptions>(o =>
    o.DefaultAuthenticateScheme = o.DefaultChallengeScheme = o.DefaultForbidScheme = ApiTokenAuth.Selector);
// shortcut: the limit is counted per server, so N servers allow N times as much; move it to the database if abuse appears.
services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    // A token is counted by itself; sign-in attempts are counted by the address they come from.
    o.GlobalLimiter = System.Threading.RateLimiting.PartitionedRateLimiter.Create<HttpContext, string>(http =>
        ApiTokenAuth.PresentedId(http) is { } tokenId
            ? System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter("token:" + tokenId, _ => new() { PermitLimit = kb.Tokens.RequestsPerMinute, Window = TimeSpan.FromMinutes(1) })
        : HttpMethods.IsPost(http.Request.Method) && http.Request.Path.Equals("/api/v1/auth/local/signin", StringComparison.OrdinalIgnoreCase)
            ? System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter("signin:" + http.Connection.RemoteIpAddress, _ => new() { PermitLimit = kb.Auth.SignInsPerMinutePerAddress, Window = TimeSpan.FromMinutes(1) })
        : System.Threading.RateLimiting.RateLimitPartition.GetNoLimiter(""));
});

// Shared keys so any instance can read any instance's cookie (A/B patching, load balancing).
var dp = services.AddDataProtection().SetApplicationName("kb").PersistKeysToDbContext<KbDbContext>();
_ = dp;

services.AddAuthorizationBuilder()
    .SetFallbackPolicy(new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());

// ---- plugins (native: core features ship as first-party plugins) ----
// Built-in plugins are the Kb.Plugin.*.dll files shipped in the app folder; drop-ins live under Kb:Plugins:Folder.
var builtIns = Directory.GetFiles(AppContext.BaseDirectory, "Kb.Plugin.*.dll").Select(Assembly.LoadFrom);
var registry = PluginLoader.Discover(builtIns, kb.Plugins, builder.Environment.ContentRootPath);
services.AddKbPlugins(config, registry);
services.AddSingleton(typeof(ProviderSelector<>));

// ---- core services ----
services.AddSingleton(TimeProvider.System);
services.AddSingleton<IAppRestarter, HostRestarter>();
services.AddHttpContextAccessor();
services.AddSingleton<IFileStoreKind, FolderFileStoreKind>();
services.AddSingleton<ActiveFileStore>();
services.AddSingleton<IFileStore>(sp => sp.GetRequiredService<ActiveFileStore>());
services.AddSingleton<ILogShipperKind, FileLogShipperKind>();
services.AddSingleton<ILogShipperKind, SyslogShipperKind>();
services.AddSingleton<ILogShipperKind, HttpsLogShipperKind>();
services.AddHttpClient(HttpsLogShipperKind.ClientName, c => c.Timeout = TimeSpan.FromSeconds(15));
services.AddSingleton<LogShipperService>();
services.AddHostedService(sp => sp.GetRequiredService<LogShipperService>());
services.AddSingleton<IAuditSink, LogShipSignal>();
services.AddSingleton<StorageMover>();
services.AddHostedService(sp => sp.GetRequiredService<StorageMover>());
services.AddScoped<Kb.Core.Search.ISearchProvider, SqlSearchProvider>();
services.AddHttpClient(NetGuard.ClientName, c => { c.Timeout = TimeSpan.FromSeconds(15); c.MaxResponseContentBufferSize = ImageEndpoints.MaxImageBytes; })
    .ConfigurePrimaryHttpMessageHandler(NetGuard.Handler);
services.AddHostedService<ImageCleanupService>();
services.AddScoped<IAuditLog, SqlAuditLog>();
services.AddScoped<ISpaceAccessData, SpaceAccessData>();
services.AddScoped<SpacePermissionService>();
services.AddScoped<ISpacePermissionService, TokenScopedPermissions>();
services.AddScoped<IAccessData, AccessData>();
services.AddScoped<INotificationStore, NotificationStore>();
services.AddScoped<IUserDirectory, UserDirectory>();
services.AddScoped<INotifier, Notifier>();
services.AddScoped<AccessService>();
services.AddScoped<BreakGlassService>();
services.AddScoped<SetupService>();

services.AddHealthChecks().AddCheck<ReadinessCheck>("ready", tags: ["ready"]);
services.AddProblemDetails();
services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));

var app = builder.Build();

// ---- startup: migrate (dev/SQLite create), log plugin refusals, ensure break-glass ----
await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<KbDbContext>();
    if (db.Database.IsSqlite()) await db.Database.EnsureCreatedAsync();
    else await db.Database.MigrateAsync();

    var audit = scope.ServiceProvider.GetRequiredService<IAuditLog>();
    foreach (var r in registry.Refused)
        await audit.AppendAsync(new AuditEvent(AuditActions.PluginRefused, null, "plugin", r.Path, Data: new Dictionary<string, string> { ["reason"] = r.Reason }));

    await scope.ServiceProvider.GetRequiredService<BreakGlassService>().EnsureAccountAsync();
    await scope.ServiceProvider.GetRequiredService<SetupService>().OnStartupAsync();
    await SearchIndex.BackfillAsync(db); // pages saved before the search index existed
    await LogShipperService.EnsureFirstTargetAsync(db, kb.Logs.Folder, scope.ServiceProvider.GetRequiredService<TimeProvider>().GetUtcNow());
    await ActiveFileStore.EnsureFirstPlaceAsync(db, kb.Storage.Folder, scope.ServiceProvider.GetRequiredService<TimeProvider>().GetUtcNow());
}

app.UseExceptionHandler();
app.UseStatusCodePages();
if (!app.Environment.IsDevelopment()) app.UseHsts();
app.UseHttpsRedirection();
app.UseMiddleware<SecurityHeadersMiddleware>();
app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    // Static UI shell is served before auth so the sign-in page can load; all data comes from authenticated /api.
    OnPrepareResponse = ctx => ctx.Context.Response.Headers.CacheControl = "no-cache",
});
app.UseAuthentication();
app.UseRateLimiter();
app.Use(ApiTokenAuth.EnforceAsync);
app.UseAuthorization();

app.MapHealthChecks("/health/live", new() { Predicate = _ => false }).AllowAnonymous();
app.MapHealthChecks("/health/ready", new() { Predicate = c => c.Tags.Contains("ready") }).AllowAnonymous();

var api = app.MapGroup("/api/v1");
api.MapAuthEndpoints();
api.MapSetupEndpoints();
api.MapPageEndpoints();
api.MapUserSettingsEndpoints();
api.MapSpaceEndpoints();
api.MapAccessEndpoints();
api.MapNotificationEndpoints();
api.MapAdminEndpoints();
api.MapBrandingEndpoints();
api.MapImageEndpoints();
api.MapAttachmentEndpoints();
api.MapStorageEndpoints();
api.MapTokenEndpoints();
api.MapLogEndpoints();
api.MapSearchEndpoints();
api.MapHomeEndpoints();
api.MapExportEndpoints();
// Unknown API routes: authenticated callers get 404, anonymous get 401 (fallback policy).
api.MapFallback(() => Results.NotFound());

app.MapFallbackToFile("index.html").AllowAnonymous();

await app.RunAsync();

public partial class Program;
