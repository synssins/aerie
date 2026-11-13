# Plugin developer guide

This guide covers what is built today. The last section lists what is planned and not built.
The examples come from the sample plugin in `plugins/Kb.Plugin.Sample`: readers acknowledge a page, and a Supervisor sees who has.

Related: [manifest reference](manifest-reference.md), [action reference](action-reference.md), [admin guide](admin-guide.md).

## What a plugin is

A plugin is a .NET assembly with a manifest (`kb-plugin.json`), packed as a `.kbplugin` file. An Admin uploads it on Administration > Plugins.
The app stores the package in its database, restarts itself, and loads the plugin when it starts. Every server that uses the same database loads the same package.

A plugin can:

- refuse or change a core action before it runs, shape the answer of a read, and react after an action has succeeded;
- define its own actions and its own routes under `/api/v1/plugins/<id>/`;
- keep its own tables in the app's database;
- run jobs on a schedule;
- have settings and secrets that an Admin sets in the app;
- supply an implementation of an extension point (an email sender, an audit sink, a storage kind, a log shipping kind);
- define extension points and actions of its own for other plugins.

### The security position

A plugin runs inside the app's process with the app's rights. It is trusted code. An Admin who installs a plugin trusts its publisher as much as the app itself.
The core does not sandbox a plugin and cannot stop a hostile one.

What the core does do:

- It runs its own permission check before any plugin code sees an action, and again when a hook changes the action.
- It answers for the caller. A plugin that reads core data through the bus gets what its caller may see, no more.
- It turns a plugin off when the plugin keeps failing, and says why on the Plugins screen.
- It lets a plugin's `HttpClient` call only the hosts the manifest lists.
- It records installs, updates, removals, state changes and setting changes in the audit log, with the package's SHA-256.

## A first plugin in ten steps

1. Create a class library for the app's target framework.
2. Reference `Kb.Plugins.Abstractions`. Reference `Kb.Plugins.Data` too when the plugin has tables. Reference nothing else of the app.
3. Add `kb-plugin.json` next to the project file. Give it an `id`, a `version` and a `publisher`. See the [manifest reference](manifest-reference.md).
4. Write a class that implements `IKbPlugin`, and name it in an assembly attribute: `[assembly: KbPlugin(typeof(MyPlugin))]`.
5. Register what the plugin adds in `Configure`.
6. Pack it: `tools\pack-plugin.ps1 <project folder>`. The package is written to `artifacts\plugins\<id>-<version>.kbplugin`.
7. Sign in as an Admin. Open Administration > Plugins. Select Upload and choose the package.
8. The screen shows "Restarting" and comes back by itself. The plugin is listed as Enabled.
9. Select the plugin to set its settings.
10. If the plugin is listed as "Disabled by core", the screen shows the reason. Correct the plugin, raise its version, and upload it again.

## The plugin class

Both methods run once at start, before the app is built. `ConfigureServices` adds the plugin's services to the container.
`Configure` registers what the plugin adds to the app.

<!-- sample:configure -->
```csharp
public void ConfigureServices(IServiceCollection services, IPluginContext context) =>
    services.AddSingleton(new SampleSettings(context));

public void Configure(IPluginApp app, IPluginContext context) => app
    .AddData<SampleData>()                                     // its own table
    .Handle<Acknowledge, AckCount, AcknowledgeHandler>()       // its own actions
    .Handle<CountAcks, AckCount, CountAcksHandler>()
    .Handle<ReadAcks, AckList, ReadAcksHandler>()
    .Before<SavePage, MajorNeedsNote>()                        // refuse a core action
    .After<SavePage, PageDto, ClearOnMajor>()                  // react once a core action has succeeded
    .Job<PurgeJob>("purge")                                    // the schedule "purge" of the manifest
    .HealthCheck<TableCheck>("table")
    .Routes(SampleRoutes.Map);                                 // under /api/v1/plugins/kb.sample/
```

Rules for both methods:

- Do not read a secret here. Secrets are available only after the app has started.
- Do not register a hosted service. The app refuses the plugin. Use a job.
- An exception here disables the plugin, and the app starts without it.
- A service the plugin registers never replaces a service of the core or of the framework.

## Settings and secrets

The manifest lists the settings. An Admin sets them on the Plugins screen.

<!-- sample:settings -->
```csharp
/// <summary>The plugin's settings as an Admin set them in the app. They are read each time, so a change applies without a restart.</summary>
public sealed class SampleSettings(IPluginContext context)
{
    public bool RequireNoteOnMajor => context.GetSetting("RequireNoteOnMajor") == "true";
    public bool ResetOnMajor => context.GetSetting("ResetOnMajor") == "true";
    public int KeepDays => int.TryParse(context.GetSetting("KeepDays"), out var days) && days >= 0 ? days : 365;
}
```

- `GetSetting` returns the current value each time. A change applies without a restart, unless the setting says `"restart": true`.
- A secret (`"secret": true`) is stored protected. The app never sends it back to the browser and never writes it to a log.
  Read it with `await context.GetSecretAsync(key)`, after start only.

## Actions and the bus

Every operation of the core is one action: a record with a name such as `page.save`. A route builds the action and sends it through the bus.
The [action reference](action-reference.md) lists every action, its fields and its result.

The bus runs these steps, in this order:

1. It finds the one handler of the action.
2. The handler's `AuthorizeAsync` runs: the core's permission check. A refusal ends the action before any plugin code has run.
3. The lock: when an enforcing plugin that guards this action is disabled by the core, everyone but an Admin gets 423.
4. Before hooks run, in plugin load order. A hook may refuse the action or replace its payload.
5. When a hook replaced the payload, `AuthorizeAsync` runs again on the new payload.
6. The handler's `HandleAsync` does the work.
7. On a read, result filters run and may change the answer.
8. The audit entries of the action are written.
9. After handlers are queued. They run later.

### Read core data as the caller

A plugin does not query the core's tables. It sends a core action. The answer is what the caller may see:
a page the caller may not see answers 404, as if it did not exist.

<!-- sample:handler -->
```csharp
/// <summary>
/// A plugin reaches core data through the bus, as its caller. Sending page.read answers 404 for a page the caller may not see,
/// so this handler needs no permission logic of its own to stay inside what the caller is allowed.
/// </summary>
public sealed class AcknowledgeHandler(SampleData db, IActionBus bus) : IActionHandler<Acknowledge, AckCount>
{
    private PageDto? _page;

    public async Task<Problem?> AuthorizeAsync(ActionContext<Acknowledge> ctx)
    {
        if (ctx.Actor.UserId is null) return Problem.Unauthorized;
        var page = await bus.SendAsync(new ReadPage { PageId = ctx.Action.PageId }, ctx.Cancel);
        _page = page.Value;
        return page.Problem;
    }

    public async Task<Outcome<AckCount>> HandleAsync(ActionContext<Acknowledge> ctx)
    {
        var (page, me) = (_page!, ctx.Actor.UserId!.Value);
        if (!await db.Acks.AnyAsync(a => a.PageId == page.Id && a.UserId == me, ctx.Cancel))
        {
            var who = await bus.SendAsync(new ReadMe(), ctx.Cancel);
            db.Acks.Add(new Ack { PageId = page.Id, UserId = me, Name = who.Value?.DisplayName ?? "Unknown", Version = page.Version, AtUtc = DateTime.UtcNow });
            try { await db.SaveChangesAsync(); }
            catch (DbUpdateException) { } // the same person acknowledged twice at once; the unique index kept one
        }
        return new AckCount(await db.Acks.CountAsync(a => a.PageId == page.Id), Mine: true);
    }
}
```

A handler has two parts. `AuthorizeAsync` answers 404, 403 or 401 and may keep what it loaded for `HandleAsync`.
`HandleAsync` checks the input and does the work. Return a `Problem` to refuse, or the result.

### Your own actions

<!-- sample:actions -->
```csharp
/// <summary>The caller has read this page. Counted once per person.</summary>
[Action("sample.ack")]
public sealed record Acknowledge : IAction<AckCount>
{
    public required Guid PageId { get; init; }
}

[Action("sample.ack.count", Read = true)]
public sealed record CountAcks : IAction<AckCount>
{
    public required Guid PageId { get; init; }
}

/// <summary>Who acknowledged the page. For people who review the space (Supervisor and up).</summary>
[Action("sample.acks.read", Read = true)]
public sealed record ReadAcks : IAction<AckList>
{
    public required Guid PageId { get; init; }
}

public sealed record AckCount(int Count, bool Mine);
public sealed record AckEntry(string Name, string Version, DateTime AtUtc);
public sealed record AckList(IReadOnlyList<AckEntry> People);
```

- The name is `<your prefix>.<what>`. Mark an action that changes nothing with `Read = true`; result filters run only on reads.
- Put `[JsonIgnore]` on a member that holds a secret or a file.
- Register the handler with `app.Handle<TAction, TResult, THandler>()`.

### Your own routes

<!-- sample:routes -->
```csharp
public static class SampleRoutes
{
    /// <summary>Mapped under /api/v1/plugins/kb.sample/. A route binds the request, sends one action and answers.</summary>
    public static void Map(RouteGroupBuilder group)
    {
        group.MapPost("/pages/{id:guid}/ack", (Guid id, IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new Acknowledge { PageId = id }, ct).ToHttp()).WithAction<Acknowledge>();
        group.MapGet("/pages/{id:guid}/ack", (Guid id, IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new CountAcks { PageId = id }, ct).ToHttp()).WithAction<CountAcks>();
        group.MapGet("/pages/{id:guid}/acks", (Guid id, IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new ReadAcks { PageId = id }, ct).ToHttp()).WithAction<ReadAcks>();
    }
}
```

- The routes are mapped under `/api/v1/plugins/<plugin id>/`.
- They need sign-in, unless a route says `AllowAnonymous()`.
- They are closed to API tokens.
- `WithAction<T>()` names the action the route sends. The answer carries the name in the `X-Kb-Action` header.
  A route that names one action and sends another fails.

### Permissions

<!-- sample:permission -->
```csharp
public sealed class ReadAcksHandler(SampleData db, IActionBus bus, IAccess access) : IActionHandler<ReadAcks, AckList>
{
    public async Task<Problem?> AuthorizeAsync(ActionContext<ReadAcks> ctx)
    {
        var page = await bus.SendAsync(new ReadPage { PageId = ctx.Action.PageId }, ctx.Cancel);
        if (!page.Ok) return page.Problem; // 404: the caller may not even see the page
        // IAccess answers for the caller: a person, narrowed by an API token when the request came with one.
        return await access.CanAsync(ctx.Actor, page.Value!.SpaceId, SpaceAction.ReviewTeamHistory, ctx.Cancel) ? null : Problem.Forbidden;
    }

    public async Task<Outcome<AckList>> HandleAsync(ActionContext<ReadAcks> ctx)
    {
        var page = ctx.Action.PageId;
        return new AckList(await db.Acks.Where(a => a.PageId == page).OrderBy(a => a.Name).Select(a => new AckEntry(a.Name, a.Version, a.AtUtc)).ToListAsync(ctx.Cancel));
    }
}
```

`IAccess` answers for an actor: a person (narrowed by an API token when the request came with one), a plugin doing background work, or nobody.
Answer 404, not 403, when the caller has no role in a space: a space the caller cannot see must look like one that does not exist.

## Hooks

### Before an action

<!-- sample:before -->
```csharp
/// <summary>Runs before the core saves a page. Returning a problem refuses the save; the person sees the message.</summary>
public sealed class MajorNeedsNote(SampleSettings settings) : IBeforeAction<SavePage>
{
    public Task<Problem?> BeforeAsync(ActionContext<SavePage> ctx) =>
        Task.FromResult(settings.RequireNoteOnMajor && ctx.Action.Major && string.IsNullOrWhiteSpace(ctx.Action.Note)
            ? Problem.Refused("A major version needs a note.")
            : null);
}
```

- `Problem.Refused(message)` answers 422. The person sees the message.
- To change the action, set `ctx.Action` to a changed copy. The core then checks the caller's permission again on the changed action.
- A hook that throws refuses the action with "<plugin name> failed.", and the failure is counted. Register it as optional
  (`app.Before<TAction, THook>(optional: true)`) when the action must go on without it.
- A hook runs for every caller, an Admin too.

### After an action

<!-- sample:after -->
```csharp
/// <summary>
/// Runs after a save has succeeded, later and as the plugin, not as the person who saved. It cannot undo the save.
/// It may run again for the same save, so it only does what is safe to repeat: deleting what is already gone changes nothing.
/// </summary>
public sealed class ClearOnMajor(SampleSettings settings, SampleData db) : IAfterAction<SavePage, PageDto>
{
    public async Task AfterAsync(SavePage action, PageDto result, ActionEvent e, CancellationToken ct)
    {
        if (!settings.ResetOnMajor || !action.Major) return;
        await db.Acks.Where(a => a.PageId == result.Id).ExecuteDeleteAsync(ct);
    }
}
```

- An after handler runs once the action has succeeded and its audit entries are written. It cannot undo or refuse the action.
- It runs as the plugin, not as the person. It gets the action as it was done and its result.
- The queue is in memory. A handler is lost when the process dies before it ran. A normal stop runs what is waiting.
- It may run again for the same action. Do only what is safe to repeat.
- It cannot read an upload: the request is over.

### Shaping a read

A result filter (`app.Filter<TAction, TResult, TFilter>()`) gets the result of a read and returns the result to answer.
It runs after the handler. A filter that throws refuses the read, unless it is registered as optional.

## Tables

<!-- sample:data -->
```csharp
public sealed class Ack
{
    public int Id { get; set; }
    /// <summary>The id of a core page, held as a value. A plugin table has no foreign key to a core table.</summary>
    public Guid PageId { get; set; }
    public Guid UserId { get; set; }
    public required string Name { get; set; }
    public required string Version { get; set; }
    public DateTime AtUtc { get; set; }
}

/// <summary>The plugin's table. In the database it is named plg_kb_sample__acks: the host adds the plugin's prefix.</summary>
public sealed class SampleData(PluginDbOptions options) : PluginDbContext(options)
{
    public DbSet<Ack> Acks => Set<Ack>();

    protected override void Build(ModelBuilder model)
    {
        var ack = model.Entity<Ack>().ToTable("acks");
        ack.HasIndex(a => new { a.PageId, a.UserId }).IsUnique();
        ack.Property(a => a.Name).HasMaxLength(200);
        ack.Property(a => a.Version).HasMaxLength(24);
    }
}
```

- Derive a context from `PluginDbContext` and register it with `app.AddData<TContext>()`. Take it from the container like any service; there is one per request.
- Every table gets the prefix `plg_<id>__` (dots and hyphens of the id become underscores). Name tables plainly.
- A table may hold the id of a core row as a value. It cannot have a foreign key to a core table.
- The tables are created the first time the plugin starts. When a later version changes them, the plugin needs EF Core migrations:
  the app then runs them at start, with their history in `plg_<id>__migrations`. A plugin without migrations whose tables no longer match its model is disabled, with
  "Its tables changed. A plugin that changes its tables needs migrations."
- For `dotnet ef migrations add`, give the context a design-time factory that builds its options with `PluginDbOptions.ForDesign("<id>", o => o.UseSqlServer("..."))`.
- A save is on its own. It is not part of the core action that caused it: when the action fails afterwards, the plugin's rows stay.
- Removing the plugin keeps its tables. An Admin deletes them with "Delete data".

## Jobs and health checks

The manifest schedules a job by name. The plugin registers a job of that name with `app.Job<TJob>(name)`.

<!-- sample:job -->
```csharp
/// <summary>The job the manifest schedules as "purge", once a day: acknowledgements older than the KeepDays setting go.</summary>
public sealed class PurgeJob(SampleSettings settings, SampleData db) : IKbJob
{
    public async Task RunAsync(CancellationToken ct)
    {
        var before = DateTime.UtcNow.AddDays(-settings.KeepDays);
        await db.Acks.Where(a => a.AtUtc <= before).ExecuteDeleteAsync(ct);
    }
}
```

- A job runs in its own scope, on one server at a time. A job that is seen for the first time runs at once; after that it runs every `everyMinutes`, counted from the end of the last run.
- It runs as the plugin: an actor of kind `Plugin`, source `job`. Through the bus it reads every space that is not sensitive, and nothing of a sensitive space.
  An action that records who did it (`page.create`, `page.save` and the like) answers 401 for it today.
- A job that throws has its error recorded and counts as a failure of the plugin. It runs again at its next slot.
- A schedule without a job of that name, a job without a schedule, or `everyMinutes` under 1 disables the plugin at start.

<!-- sample:health -->
```csharp
public sealed class TableCheck(SampleData db) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            _ = await db.Acks.AnyAsync(cancellationToken);
            return HealthCheckResult.Healthy();
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return HealthCheckResult.Unhealthy("The acknowledgements table cannot be read.", e);
        }
    }
}
```

A health check is registered with `app.HealthCheck<TCheck>(name)`. It is not part of the app's own readiness check.

## Calling other systems

List every host the plugin calls in `outboundHosts`. Take the plugin's client from `IHttpClientFactory` with the name `PluginHttp.ClientName("<id>")`.
That client refuses a host that is not listed.

## Library plugins

A plugin can serve other plugins.

- It lists the assemblies it shares in `exports`. A plugin that names it in `requires` sees those assemblies, and no other plugin's.
- It defines an extension point as a record. A dependent adds one with `app.Add(new TheRecord(...))`. The library reads them from `IRegistrations.All<TheRecord>()`.
- It defines an action. A dependent hooks it exactly as it would hook a core action.
- A required plugin cannot be disabled or removed while a plugin that requires it is enabled.

## Enforcing plugins

A plugin that enforces a rule the organisation relies on says `"enforcing": true`. At each start the app records which actions the plugin has before hooks or filters on.
When the core has to disable the plugin, those actions answer 423 "<plugin name> is disabled." to everyone but an Admin, until an Admin releases the lock on the Plugins screen.
Sign-in, setup and plugin administration are never locked.

## What the core disables a plugin for

- An exception in `ConfigureServices` or `Configure`, or a hosted service.
- Tables that cannot be made, or that changed without a migration.
- A schedule and its job that do not match.
- A requirement that is missing, disabled or too old; a circular requirement.
- A plugin built for a newer plugin API or a newer app.
- Five failures in ten minutes: a hook, a filter, an after handler, a job, an audit sink or an email sender that threw or timed out.

The plugin stays disabled until an Admin enables it again or uploads a new version. Plugins that require it are disabled with it.

## Versions

- `version`: three numbers. An update must be higher than the installed version.
- `abstractionsVersion`: the version of `Kb.Plugins.Abstractions` the plugin was built against. The app refuses a plugin built for a newer one.
- `minKbVersion`: the lowest app version the plugin runs on.

The contract is at 0.2.0 and can still change.

## Planned, not built

- Screens: plugin scripts, slots in core screens, editor blocks, plugin screens.
- Data attached to core rows (a count that comes with the page).
- Permissions a plugin declares in its manifest.
- Sign-in schemes, page kinds, export formats.
- After handlers that survive a crash (a queue in the database).
- A plugin table save inside the core action's transaction.
