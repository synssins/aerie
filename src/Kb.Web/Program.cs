using Kb.Core.Access;
using Kb.Core.Audit;
using Kb.Core.Config;
using Kb.Core.Content;
using Kb.Core.Identity;
using Kb.Core.Notifications;
using Kb.Core.Plugins;
using Kb.Core.Spaces;
using Kb.Data;
using Kb.Data.Plugins;
using Kb.Plugins;
using Kb.Plugins.Actions;
using Kb.Web;
using Kb.Web.Pipeline;
using Kb.Web.Plugins;
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
    // One file, no SQL Server: its migrations are in their own assembly, as SQLite's tables differ from SQL Server's.
    DatabaseProvider.Sqlite => o.UseSqlite(kb.Database.ConnectionString, s => s.MigrationsAssembly(KbDbContext.SqliteMigrations)),
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
    .AddPasswordValidator<Kb.Web.Actions.CommonPasswordValidator>()
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
    // A session ends at once when its account is deleted or deactivated, or its password is set anew: each request reads the
    // account's stamp and compares it with the one the session was opened with. (The framework's own check runs every 30 minutes.)
    o.Events.OnValidatePrincipal = async ctx =>
    {
        var stamp = Guid.TryParse(ctx.Principal?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value, out var id)
            ? await ctx.HttpContext.RequestServices.GetRequiredService<KbDbContext>().Users.AsNoTracking()
                .Where(u => u.Id == id && u.DeactivatedAt == null).Select(u => u.SecurityStamp).FirstOrDefaultAsync(ctx.HttpContext.RequestAborted)
            : null;
        // A session also ends a fixed time after it was opened, however much it is used: its start travels in the cookie. A session
        // from before this rule gets its start now, so nobody is signed out by the update.
        var now = ctx.HttpContext.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow();
        var start = ctx.Properties.Items.TryGetValue(SessionAge.Key, out var text) && long.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, out var s) ? DateTimeOffset.FromUnixTimeSeconds(s) : (DateTimeOffset?)null;
        if (stamp is null || stamp != ctx.Principal!.FindFirst(ctx.HttpContext.RequestServices.GetRequiredService<IOptions<IdentityOptions>>().Value.ClaimsIdentity.SecurityStampClaimType)?.Value
            || now - start > kb.Auth.SessionMaxAge)
        {
            ctx.RejectPrincipal();
            await Microsoft.AspNetCore.Authentication.AuthenticationHttpContextExtensions.SignOutAsync(ctx.HttpContext, IdentityConstants.ApplicationScheme);
            return;
        }
        if (start is null)
        {
            ctx.Properties.Items[SessionAge.Key] = now.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
            ctx.ShouldRenew = true;
        }
        ctx.HttpContext.Items[SessionAge.Key] = start ?? now;
    };
});

// The pending sign-in between the password and the code: as strict as the session cookie, and short.
services.Configure<Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationOptions>(IdentityConstants.TwoFactorUserIdScheme, o =>
{
    o.Cookie.Name = "__Host-kb-pending";
    o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    o.Cookie.SameSite = SameSiteMode.Lax;
    o.Cookie.HttpOnly = true;
    o.ExpireTimeSpan = TimeSpan.FromMinutes(10);
});

// API tokens beside the sign-in cookie: a request with a Bearer header goes to the token handler, every other one to the cookie.
services.AddAuthentication()
    // HTTP Basic is read on the Data Center compatible surface only; aerie's own API ignores the header as it always did.
    .AddPolicyScheme(ApiTokenAuth.Selector, null, o => o.ForwardDefaultSelector = ctx => ApiTokenAuth.HasBearer(ctx.Request) ? ApiTokenAuth.Scheme
        : Kb.Web.DataCenter.DcRest.HasBasic(ctx.Request) && Kb.Web.DataCenter.DcRest.IsUnderRoot(ctx) ? Kb.Web.DataCenter.BasicAuthHandler.SchemeName
        : IdentityConstants.ApplicationScheme)
    .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, ApiTokenAuthHandler>(ApiTokenAuth.Scheme, null)
    .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, Kb.Web.DataCenter.BasicAuthHandler>(Kb.Web.DataCenter.BasicAuthHandler.SchemeName, null);
services.Configure<Microsoft.AspNetCore.Authentication.AuthenticationOptions>(o =>
    o.DefaultAuthenticateScheme = o.DefaultChallengeScheme = o.DefaultForbidScheme = ApiTokenAuth.Selector);
// shortcut: the limit is counted per server, so N servers allow N times as much; move it to the database if abuse appears.
services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    // Past a limit: 429 with Retry-After; on the Data Center compatible surface with its RestError body (identity.md, Failed Basic attempts).
    o.OnRejected = async (rejected, ct) =>
    {
        var http = rejected.HttpContext;
        var wait = rejected.Lease.TryGetMetadata(System.Threading.RateLimiting.MetadataName.RetryAfter, out var after) ? after : TimeSpan.FromMinutes(1);
        http.Response.Headers.RetryAfter = ((int)Math.Ceiling(wait.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (Kb.Web.DataCenter.DcRest.IsUnderRoot(http)) await Kb.Web.DataCenter.DcRest.Fail(429, "Too many attempts; try again later.").ExecuteAsync(http);
    };
    // Sign-in attempts are counted by the address they come from; a token is counted by itself. The route that matched decides, not
    // the text of the address (a trailing slash reaches the same route), and it decides first (a refused token does not buy a higher limit).
    // Guessing a password link, a one-time code or a recovery code is counted like guessing a password.
    o.GlobalLimiter = System.Threading.RateLimiting.PartitionedRateLimiter.Create<HttpContext, string>(http =>
        HttpMethods.IsPost(http.Request.Method) && (http.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText is "/api/v1/auth/local/signin" or "/api/v1/auth/local/password"
            or "/api/v1/auth/local/code" or "/api/v1/auth/codes" or "/api/v1/auth/codes/key"
        // A sign-in with a sign-in method, its start and its callback, is counted like a sign-in with a password.
        || HttpMethods.IsGet(http.Request.Method) && (http.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText is "/api/v1/auth/methods/{plugin}/{method}/start"
            or "/api/v1/auth/methods/{plugin}/{method}/callback"
            ? System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter("signin:" + http.Connection.RemoteIpAddress, _ => new() { PermitLimit = kb.Auth.SignInsPerMinutePerAddress, Window = TimeSpan.FromMinutes(1) })
        // OAuth 2.0 authorize requests are counted per address, like sign-ins (identity-plan.md, slice 5).
        : http.GetEndpoint()?.Metadata.GetMetadata<Kb.Web.DataCenter.DcOAuthRoute>() is not null && HttpMethods.IsGet(http.Request.Method)
            ? System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter("oauth.authorize:" + http.Connection.RemoteIpAddress, _ => new() { PermitLimit = kb.Auth.SignInsPerMinutePerAddress, Window = TimeSpan.FromMinutes(1) })
        // Refused Basic credentials on the Data Center compatible surface are limited before authentication instead (BasicThrottle.GateAsync).
        : http.User.Identity?.IsAuthenticated != true && Kb.Web.DataCenter.DcRest.HasBasic(http.Request) && Kb.Web.DataCenter.DcRest.IsUnderRoot(http)
            ? System.Threading.RateLimiting.RateLimitPartition.GetNoLimiter("")
        : ApiTokenAuth.PresentedId(http) is { } tokenId
            ? System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter("token:" + tokenId, _ => new() { PermitLimit = kb.Tokens.RequestsPerMinute, Window = TimeSpan.FromMinutes(1) })
        // Someone not signed in (reading Anyone spaces) is counted by address; the limiter runs after authentication, so a session is known.
        : http.User.Identity?.IsAuthenticated != true && (http.Request.Path.StartsWithSegments("/api") || Kb.Web.DataCenter.DcRest.IsUnderRoot(http))
            ? System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter("anon:" + http.Connection.RemoteIpAddress, _ => new() { PermitLimit = kb.Auth.AnonymousRequestsPerMinutePerAddress, Window = TimeSpan.FromMinutes(1) })
        : System.Threading.RateLimiting.RateLimitPartition.GetNoLimiter(""));
});

// Shared keys so any instance can read any instance's cookie (A/B patching, load balancing).
var dp = services.AddDataProtection().SetApplicationName("kb").PersistKeysToDbContext<KbDbContext>();
_ = dp;

// A browser that reached the site over https keeps to https for a year, its subdomains included (the framework's default is 30 days).
services.AddHsts(o => { o.MaxAge = TimeSpan.FromDays(365); o.IncludeSubDomains = true; });

services.AddAuthorizationBuilder()
    .SetFallbackPolicy(new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());

// ---- plugins ----
// Built-in plugins are the Kb.Plugin.*.dll files shipped in the app folder; installed ones are packages in the database.
// This migrates the database, then loads every enabled plugin. What plugins register is added last, by Seal, below.
var boot = await PluginBootstrap.RunAsync(services, config, kb, builder.Environment.ContentRootPath);
services.AddSingleton(typeof(ProviderSelector<>));

// ---- core services ----
services.AddSingleton(TimeProvider.System);
services.AddSingleton<IAppRestarter, HostRestarter>();
services.AddScoped<PluginInstaller>();
services.AddSingleton<PluginSettingsStore>();
services.AddSingleton<PluginFailures>();
services.AddSingleton<IPluginFailures>(sp => sp.GetRequiredService<PluginFailures>());
// How search, history and exports read the editor nodes plugins add. A renderer that fails is written to the log and the node is
// read as a placeholder. It is not counted as a failure of the plugin: what a page holds is its writer's choice, and a page
// must not be able to switch a plugin off.
services.AddSingleton(sp => new DocRenderers(sp.GetRequiredService<IRegistrations>().All<NodeRenderer>(),
    (plugin, e) => sp.GetRequiredService<ILoggerFactory>().CreateLogger("Kb.Plugins.EditorNodes").LogWarning(e, "Plugin {Plugin}: a renderer of an editor node failed. The node is read as a placeholder.", plugin)));
services.AddSingleton<PluginWatcher>();
services.AddHostedService(sp => sp.GetRequiredService<PluginWatcher>());
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
services.AddHostedService<Kb.Web.DataCenter.OAuthPruner>();
services.AddScoped<IAuditLog, SqlAuditLog>();
services.AddScoped<ISpaceAccessData, SpaceAccessData>();
services.AddScoped<SpacePermissionService>();
services.AddScoped<ISpacePermissionService, TokenScopedPermissions>();
services.AddScoped<IPluginPermissionData, Kb.Data.Plugins.PluginPermissionData>();
services.AddScoped<SpaceAccess>();
services.AddScoped<IAccess, Kb.Web.DataCenter.DcAccess>();
// ---- the action pipeline ----
services.AddSingleton<ActionCatalog>();
services.AddScoped<ActorAccessor>();
services.AddScoped<IActorAccessor>(sp => sp.GetRequiredService<ActorAccessor>());
services.AddScoped<IActionBus, ActionBus>();
services.AddSingleton<ActionQueueWorker>();
services.AddHostedService(sp => sp.GetRequiredService<ActionQueueWorker>());
services.AddSingleton<UiErrorLimit>();
services.AddSingleton<JobRunner>();
services.AddHostedService(sp => sp.GetRequiredService<JobRunner>());
services.AddScoped<IAccessData, AccessData>();
services.AddScoped<INotificationStore, NotificationStore>();
services.AddScoped<IUserDirectory, UserDirectory>();
services.AddScoped<INotifier, Notifier>();
services.AddScoped<AccessService>();
services.AddScoped<BreakGlassService>();
services.AddScoped<SetupService>();
services.AddScoped<Kb.Web.Actions.CodeSignIn>();
services.AddSingleton<SignInMethods>();
services.AddSingleton<OidcCache>();
services.AddScoped<SignInFlow>();
services.AddScoped<InventoryService>();
services.AddScoped<AiService>();
services.AddSingleton<AiRateLimiter>();
services.AddScoped<Kb.Web.Import.ImportService>();
services.AddSingleton<Kb.Web.Import.ImportContributors>();
services.AddScoped<Kb.Web.DataCenter.DcContentService>();
services.AddScoped<Kb.Web.DataCenter.DcAttachmentService>();
services.AddSingleton<Kb.Web.DataCenter.BasicThrottle>();
services.AddSingleton<Kb.Web.DataCenter.DcScopes>();
services.AddSingleton<Kb.Web.Actions.OAuthThrottle>();
services.AddSingleton<Kb.Web.Actions.CommentThrottle>();
services.AddSingleton<Kb.Web.Actions.RatingThrottle>();
services.AddHostedService<Kb.Web.Actions.CommentPurger>();
services.AddSingleton<Kb.Web.Import.ImportWorker>();
services.AddHostedService(sp => sp.GetRequiredService<Kb.Web.Import.ImportWorker>());
// The model server is the one an Admin names (often on the local network): no address guard; the time limit is per call.
// The AI's calls carry headers plugins add (an access gateway's secrets). They follow no redirect: .NET keeps such headers on a
// redirect to another host, so a redirect is answered as a refusal instead. No header value is ever written to a log.
services.AddHttpClient(AiService.ClientName, c => { c.Timeout = Timeout.InfiniteTimeSpan; c.MaxResponseContentBufferSize = 16 * 1024 * 1024; })
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false })
    .RedactLoggedHeaders(_ => true);

services.AddHealthChecks().AddCheck<ReadinessCheck>("ready", tags: ["ready"]);
services.AddProblemDetails();
services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));

// Forwarded headers are believed only on the app's own terms (below). The framework's switch (ASPNETCORE_FORWARDEDHEADERS_ENABLED)
// believes every caller; an install that still sets it gets nothing from it.
services.PostConfigure<ForwardedHeadersOptions>(o => o.ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.None);

PluginBootstrap.Seal(services, boot);
var app = builder.Build();

// A proxy named in Kb:Proxy:Trusted says who the client is and whether it came over https; nobody else does. Each address in the
// header is taken only while the one that passed it on is trusted, so a chain of named proxies works and a caller's own entry does not.
if (kb.Proxy.Trusted.Count > 0)
{
    var forwarded = new ForwardedHeadersOptions
    {
        ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor | Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto,
        ForwardLimit = null,
    };
    forwarded.KnownProxies.Clear();
    forwarded.KnownIPNetworks.Clear();
    foreach (var network in kb.Proxy.Networks()) forwarded.KnownIPNetworks.Add(network);
    app.UseForwardedHeaders(forwarded);
}
else
{
    // Said once: an install behind a proxy that is not named yet sees every caller as the proxy, and http where the client used https.
    var told = 0;
    app.Use((ctx, next) =>
    {
        if (ctx.Request.Headers.ContainsKey("X-Forwarded-For") && Interlocked.Exchange(ref told, 1) == 0)
            app.Logger.LogWarning("A request from {Address} carries X-Forwarded-For, which is ignored. If that address is your reverse proxy, name it in Kb:Proxy:Trusted.", ctx.Connection.RemoteIpAddress);
        return next(ctx);
    });
}

// ---- startup: record what the plugin start found, ensure break-glass ----
await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<KbDbContext>();
    var audit = scope.ServiceProvider.GetRequiredService<IAuditLog>();
    foreach (var e in boot.Events) await audit.AppendAsync(e);
    await app.Services.GetRequiredService<PluginSettingsStore>().ProtectStraysAsync(CancellationToken.None); // from here a plugin can read its secrets

    await scope.ServiceProvider.GetRequiredService<BreakGlassService>().EnsureAccountAsync();
    await scope.ServiceProvider.GetRequiredService<SetupService>().OnStartupAsync();
    await SearchIndex.BackfillAsync(db, app.Services.GetRequiredService<DocRenderers>()); // pages saved before the search index existed
    // Image grants no page needs any more (a page moved in and out again, or a grant written before moves checked what they pass on).
    // A failed clean-up must not stop the start: the grants stay one more run.
    try { foreach (var space in await db.SpaceImageGrants.Select(g => g.SpaceId).Distinct().ToListAsync()) await ImageEndpoints.PruneGrantsAsync(db, space); }
    catch (Exception e) when (e is not OperationCanceledException) { app.Logger.LogError(e, "Image grants were not cleaned up at start."); }
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
app.Use(Kb.Web.DataCenter.BasicThrottle.GateAsync); // before authentication: a limited address's credentials are not checked
app.UseAuthentication();
app.UseRateLimiter();
app.Use(CrossSiteGuard.InvokeAsync);
app.Use(Kb.Web.DataCenter.DcRest.GuardAsync);
app.Use(ApiTokenAuth.EnforceAsync);
app.UseAuthorization();

app.MapHealthChecks("/health/live", new() { Predicate = _ => false }).AllowAnonymous();
app.MapHealthChecks("/health/ready", new() { Predicate = c => c.Tags.Contains("ready") }).AllowAnonymous();

var api = app.MapGroup("/api/v1");
api.MapAuthEndpoints();
api.MapMyPassword();
api.MapSetupEndpoints();
api.MapPageEndpoints();
api.MapPageStructureEndpoints();
api.MapVersionEndpoints();
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
api.MapOAuthClientEndpoints();
api.MapFeedbackEndpoints();
api.MapOAuthConsentEndpoints();
api.MapLogEndpoints();
api.MapPluginAdminEndpoints();
api.MapSearchEndpoints();
api.MapHomeEndpoints();
api.MapExportEndpoints();
api.MapUiEndpoints();
api.MapInventoryEndpoints();
api.MapImportEndpoints();
app.MapPluginFiles();
Kb.Web.DataCenter.DcRoutes.MapDataCenterApi(app);
app.MapDcOAuth();
// Each loaded plugin's own routes, under /api/v1/plugins/<id>/. They need sign-in unless a route says AllowAnonymous, and are closed to API tokens.
// A server that runs an old set of plugins answers 503 there until it has restarted.
foreach (var routes in boot.Registrations.All<RouteMap>())
    routes.Value.Map(api.MapGroup($"/plugins/{routes.PluginId}").AddEndpointFilter(async (ctx, next) =>
        ctx.HttpContext.RequestServices.GetRequiredService<PluginWatcher>().Stale ? Results.StatusCode(StatusCodes.Status503ServiceUnavailable) : await next(ctx)));
// Unknown API routes: authenticated callers get 404, anonymous get 401 (fallback policy).
api.MapFallback(() => Results.NotFound());

app.MapFallbackToFile("index.html").AllowAnonymous();

await app.RunAsync();

public partial class Program;
