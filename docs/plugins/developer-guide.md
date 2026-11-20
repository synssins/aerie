# Plugin developer guide

This guide covers what is built today. The last section lists what is planned and not built.
The examples come from the sample plugin, [`plugins/Kb.Plugin.Sample`](https://github.com/synssins/aerie/tree/main/plugins/Kb.Plugin.Sample) in the public repository: readers acknowledge a page, and a Supervisor sees who has.

Related: [building and packaging](packaging.md) (project file, package layout, names, what an install checks), [manifest reference](manifest-reference.md), [action reference](action-reference.md) (every action, its fields, and every type with an example of its JSON), [page content](#page-content) (the format of `ContentJson`), [testing a plugin](#testing-a-plugin), [API reference](../api-reference.md), [example: an outbound-headers plugin](example-outbound-headers.md), [admin guide](admin-guide.md).

## What a plugin is

A plugin is a .NET assembly with a manifest (`kb-plugin.json`), packed as a `.kbplugin` file. An Admin uploads it on Administration > Plugins.
The app stores the package in its database, restarts itself, and loads the plugin when it starts. Every server that uses the same database loads the same package.

A plugin can:

- refuse or change a core action before it runs, shape the answer of a read, and react after an action has succeeded;
- define its own actions and its own routes under `/api/v1/plugins/<id>/`;
- keep its own tables in the app's database;
- run jobs on a schedule;
- have settings and secrets that an Admin sets in the app;
- supply an implementation of an extension point (an email sender, an audit sink, a storage kind, a log shipping kind, a virus scanner for imports, headers on the AI service's calls);
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

1. Create a class library for `net10.0`, the app's target framework.
2. Reference `Kb.Plugins.Abstractions` (a project or a DLL from the repository: [packaging](packaging.md#what-you-need)). Reference `Kb.Plugins.Data` too when the plugin has tables. Reference nothing else of the app.
3. Add `kb-plugin.json` next to the project file. Give it an `id`, a `version` and a `publisher`. See the [manifest reference](manifest-reference.md).
4. Write a class that implements `IKbPlugin`, and name it in an assembly attribute: `[assembly: KbPlugin(typeof(MyPlugin))]`.
5. Register its services in `ConfigureServices` and what it adds to the app in `Configure`.
6. Pack it: `tools\pack-plugin.ps1 <project folder>`, or by hand ([packaging](packaging.md#build-the-package)). The package is a zip with `kb-plugin.json` and the main assembly at the top.
7. Sign in as an Admin. Open Administration > Plugins. Select Upload and choose the package.
8. The screen shows "Restarting" and comes back by itself. The plugin is listed as Enabled.
9. Select the plugin to set its settings.
10. If the plugin is listed as "Disabled by core", the screen shows the reason. Correct the plugin, raise its version, upload it again, and select Enable: an update keeps the state the plugin had.

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
    .Handle<ReadAckTotals, AckTotals, ReadAckTotalsHandler>()
    .Before<SavePage, MajorNeedsNote>()                        // refuse a core action
    .After<SavePage, PageDto, ClearOnMajor>()                  // react once a core action has succeeded
    .Job<PurgeJob>("purge")                                    // the schedule "purge" of the manifest
    .HealthCheck<TableCheck>("table")
    .EditorNode(Notice.Renderer)                               // how the server reads its editor block
    .ImportContributor<NoticeFromFile>()                       // add to the drafts of AI imports
    .DraftContributor<NoticeFromDraft>()                       // ... from the text the AI drafted
    .DraftContributor<DiagramOutline>()                        // ... from the diagrams the AI read in its pictures
    .ImportFileKinds(".notice")                                // a kind the app does not read: drafted by this plugin
    .ImportDrawingTool<BoxesAndLines>(BoxesAndLines.Name, BoxesAndLines.Description, BoxesAndLines.Schema) // a tool the AI may call for a drawing (0.11.0 draft)
    .SignInMethod<SampleSignIn>("example", "Sign in with Example ID") // a button on the sign-in screen
    .SignInProviders<SampleProviderCheck>() // providers an Admin adds on this plugin's page, each with its own button
    .Routes(SampleRoutes.Map);                                 // under /api/v1/plugins/kb.sample/
```

Rules for both methods:

- Do not read a secret here. Secrets are available only after the app has started.
- Do not register a hosted service. The app refuses the plugin. Use a job.
- An exception here disables the plugin, and the app starts without it.
- A service the plugin registers never replaces a service of the core or of the framework: the core's registration of the same service wins. A plugin adds; it cannot replace.

## Settings and secrets

The manifest lists the settings. An Admin sets them on the Plugins screen.

<!-- sample:settings -->
```csharp
/// <summary>The plugin's settings as an Admin set them in the app. They are read each time, so a change applies without a restart.</summary>
public sealed class SampleSettings(IPluginContext context)
{
    public bool RequireNoteOnMajor => context.GetSetting("RequireNoteOnMajor") == "true";
    public bool ResetOnMajor => context.GetSetting("ResetOnMajor") == "true";
    // The manifest gives KeepDays a default (365) and bounds (0 to 3650): the app returns the default while none is saved and refuses a
    // value out of bounds, so the value is always a number here.
    public int KeepDays => int.Parse(context.GetSetting("KeepDays")!, System.Globalization.CultureInfo.InvariantCulture);
    public string? SignInDomain => context.GetSetting("SignInDomain")?.Trim() is { Length: > 0 } d ? d : null;
}
```

- `GetSetting` returns the current value each time, or the setting's `default` while nothing is saved. A change applies without a restart, unless the setting says `"restart": true`.
- A secret (`"secret": true`) is stored protected. The app never sends it back to the browser and never writes it to a log.
  Read it with `await context.GetSecretAsync(key)`, after start only.
- A setting can say more than its label ([the manifest's fields](manifest-reference.md#settings)). `help` is a sentence shown under the field. `default` is what `GetSetting` returns while nothing is saved, and the empty field shows it; the plugin no longer treats an empty value as "use my default". `unit` is shown after the field ("MB", "days"). For a number, `min` and `max` are checked when an Admin saves: a value out of bounds answers "At least 1 MB." or "At most 500 MB.", under the field. A select's `optionLabels` gives what the list shows for each stored value. Keep the label to the thing itself ("Largest file"), and put the unit, the default and the explanation in their own fields.
- A setting whose default does not fit its own type or bounds, a `min` above its `max`, bounds on a setting that is not a number, a default for a secret, or a label for an option that is not listed, is refused at install ("Setting KeepDays: the default is refused: At most 3650 days.").
- An Admin sets them on the plugin's page in Administration > Plugins: the gear left of its name, or a click on its row.

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

/// <summary>Who acknowledged the page. For people who hold the plugin's permission "see-acknowledgements" in the page's space.</summary>
[Action("sample.acks.read", Read = true)]
public sealed record ReadAcks : IAction<AckList>
{
    public required Guid PageId { get; init; }
}

/// <summary>How many acknowledgements are kept, on how many pages. For Admins: the plugin's panel on its page in Administration > Plugins.</summary>
[Action("sample.acks.totals", Read = true)]
public sealed record ReadAckTotals : IAction<AckTotals>;

public sealed record AckCount(int Count, bool Mine);
public sealed record AckTotals(int Acknowledgements, int Pages);
public sealed record AckEntry(string Name, string Version, DateTime AtUtc);
public sealed record AckList(IReadOnlyList<AckEntry> People);
```

- The name is `<your prefix>.<what>`. Mark an action that changes nothing with `Read = true`; result filters run only on reads.
- An action has one handler. A plugin that registers a handler for an action that already has one is not loaded.
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
        group.MapGet("/totals", (IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new ReadAckTotals(), ct).ToHttp()).WithAction<ReadAckTotals>();
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
        // The manifest declares the permission with a default role (Supervisor). An Admin can change that role on the Plugins screen.
        // IAccess answers for the caller: a person, narrowed by an API token when the request came with one.
        return await access.CanAsync(ctx.Actor, "kb.sample:see-acknowledgements", page.Value!.SpaceId, ctx.Cancel) ? null : Problem.Forbidden;
    }

    public async Task<Outcome<AckList>> HandleAsync(ActionContext<ReadAcks> ctx)
    {
        var page = ctx.Action.PageId;
        return new AckList(await db.Acks.Where(a => a.PageId == page).OrderBy(a => a.Name).Select(a => new AckEntry(a.Name, a.Version, a.AtUtc)).ToListAsync(ctx.Cancel));
    }
}
```

A plugin declares its permissions in the manifest and asks for one by name: `"<plugin id>:<key>"`.

- A `space` permission is held, in a space, by everyone whose role there is at least its minimum role. The manifest gives the default (`defaultRole`); an Admin can change it on the Plugins screen. Ask with the space id.
  The minimum is always a built-in role: `Reader`, `Contributor`, `Supervisor` or `Owner`. Admins can also make custom roles (Administration > Security > Roles): sets of what a person may do in a space. A person with a custom role holds the permission when their rights in the space include all of a built-in role's; they rank as the strongest built-in role so covered, and as a Reader at least.
- A `global` permission is held by the people an Admin gave it to (Administration > Users) and by every Admin. Ask without a space. `me.read` lists the global permissions its caller holds.
- A permission of a plugin that is not loaded is not held, whatever was set or granted.
- A request made with an API token holds no global permission. For a space permission the role the token has in the space counts.
- A deactivated account holds nothing.
- A manifest is refused at install when a permission could not work: a scope other than `space` or `global`, a `defaultRole` that is not a role's name (they are case-sensitive), a key listed twice, a key that is empty, longer than 100 characters or holds a colon.

`IAccess` answers for an actor: a person (narrowed by an API token when the request came with one), a plugin doing background work, or nobody.
It also answers the core's own questions: `RoleAsync`, `CanAsync(actor, spaceId, SpaceAction)`, `IsAdminAsync`.
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
- A plugin hooks the actions of the contract assembly and of other plugins. The core-only actions (how plugins, API tokens, storage, log shipping and invitations are administered) are closed: a plugin that registers on one is not loaded.
- The core sets no time limit on a hook, a filter or an after handler. One that never returns holds every request to that action; an after handler that never returns holds every later after handler of every plugin. Give every wait of your own a limit.
- A hook on `auth.signin` that refuses can lock everyone out, the break-glass account too. Only Safe Mode gets an Admin back in.
- An action's password, setup code or file is not in its JSON and not in its text (`ToString()`); do not write them to a log yourself.

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
- A plugin without EF Core migrations has its tables created the first time it starts, and never changed after that.
  The app notices only a table that is missing later (the plugin is then disabled with "Its tables changed. A plugin that changes its tables needs migrations.").
  It does not notice a changed column: the plugin loads and its queries fail.
- A plugin whose tables will ever change ships migrations from its first version. The app runs them at start, with their history in `plg_<id>__migrations`.
  Do not add migrations to a plugin that is already installed without them: its first migration would create tables that exist. Its data must be deleted first, or the first migration must tolerate existing tables.
- For `dotnet ef migrations add`, give the context a design-time factory that builds its options with `PluginDbOptions.ForDesign("<id>", o => o.UseSqlServer("..."))`.
- Keep the tables in the default schema. The prefix is their only namespace; a schema you set is removed.
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
- A run may last as long as its interval, five minutes at least. After that it is told to stop (its cancellation token), recorded as "Timed out." and counted as a failure.
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
It runs when an Admin opens the Plugins screen, as the plugin, with a limit of five seconds. One that throws or takes longer is shown as Unhealthy; that is not counted as a failure of the plugin.

## Screens

A plugin can ship a script for the browser. The manifest names it: `"uiApiVersion": "1.0.0"` and `"ui": { "script": "ui/index.js" }`. Both are needed: a script without `uiApiVersion` is refused at install.
Only an uploaded plugin's script is served. A plugin built into the app (shipped in its folder) cannot have one yet.
The script is an ES module in the package's `ui/` folder. Its default export is called once, after sign-in, with a host object.

<!-- sample:ui -->
```js
import { createElement as h, useEffect, useState } from "/shared/v1/react.js";

// The Acknowledge button tells the panel part to read again.
const listeners = new Set();
const changed = () => listeners.forEach((l) => l());

/** A part of the page's right panel: how many people acknowledged the page, and who, for those who may see that. */
function Acknowledged({ kb, page }) {
  const [count, setCount] = useState(null);
  const [people, setPeople] = useState(null);
  useEffect(() => {
    const load = () => {
      kb.api.get(`/pages/${page.id}/ack`).then(setCount).catch(() => setCount(null));
      // The plugin's route answers 403 to someone without the permission: they see the count and no names.
      kb.api.get(`/pages/${page.id}/acks`).then((r) => setPeople(r.people)).catch(() => setPeople(null));
    };
    load();
    listeners.add(load);
    return () => { listeners.delete(load); };
  }, [kb, page.id, page.version]);

  if (!count) return null;
  return h("div", null,
    h("div", null, count.count === 1 ? "1 person" : `${count.count} people`),
    (people ?? []).map((p) => h("div", { key: p.name, className: "muted small" }, `${p.name}, version ${p.version}`)));
}

/**
 * A panel on the plugin's own page in Administration > Plugins: what the plugin keeps. Only Admins see the page; the route checks too.
 * It uses the app's classes: "grid-table" for a table, "muted" for a quiet line.
 */
function Totals({ kb }) {
  const [totals, setTotals] = useState(null);
  const [failed, setFailed] = useState(false);
  useEffect(() => { kb.api.get("/totals").then(setTotals).catch(() => setFailed(true)); }, [kb]);
  if (failed) return h("p", { className: "form-error", role: "alert" }, "Not read.");
  if (!totals) return null;
  return h("table", { className: "grid-table" },
    h("tbody", null,
      h("tr", null, h("td", null, "Acknowledgements"), h("td", null, String(totals.acknowledgements))),
      h("tr", null, h("td", null, "Pages"), h("td", null, String(totals.pages)))));
}

/** Called once, after sign-in, with the host: everything the script may use of the app. */
export default function (kb) {
  kb.slots.add("plugin.settings", { id: "totals", title: "Acknowledgements kept", render: () => h(Totals, { kb }) });
  kb.slots.add("page.panel", { id: "acknowledged", title: "Acknowledged", render: (c) => h(Acknowledged, { kb, page: c.page }) });
  kb.slots.add("page.tools", {
    id: "acknowledge",
    label: "Acknowledge",
    onClick: (c) => kb.api.post(`/pages/${c.page.id}/ack`).then(changed).catch(() => undefined),
  });
  addNotice(kb); // the editor block, below
}
```

- Take React from `/shared/v1/react.js` (also `/shared/v1/react-jsx-runtime.js` and `/shared/v1/react-dom.js`), and the editor from `/shared/v1/tiptap-react.js`. They hand back the app's own copies. A script that brings its own React breaks hooks; a node made with another copy of the editor is not a node of this one. [What each one exports](#the-shared-modules).
- The app serves only the `ui/` folder of a loaded plugin, to people who are signed in, and only these file types: `.js`, `.css`, `.json`, `.map`, `.svg`, `.png`, `.jpg`, `.webp`, `.woff2`. Any other file of the folder answers 404: `.mjs`, `.cjs`, `.jpeg`, `.gif`, `.woff`, `.ttf`, `.html` and `.wasm` among them. The script the manifest names must end in `.js`.
- So a library that is published as a set of `.mjs` modules cannot be loaded as it is: the browser asks for files the app does not serve. Bundle it into your own `.js` files with a bundler, or use the library's single-file browser build (one `.js` file) and load it with a `<script>` element whose `src` is `new URL("./lib.js", import.meta.url).href`.
- The browser keeps the script itself for good: its address carries the package's hash. Every other file of the folder (a stylesheet, a module the script imports) is asked for again each time, so a new package is seen at once.
- The default export may return a promise. The app does not wait for it before it starts the next plugin's script; if it fails later, that is written to the server's log.
- `kb.slots.add(slot, item)` adds to a named place of a screen. Today's slots: `page.tools` (buttons in the page's tool row: `id`, `label`, `onClick(ctx)`), `page.panel` (parts of the page's right panel: `id`, `title`, `render(ctx)`), both with the context `{ page }`, `editor.toolbar` (buttons of the editor's toolbar, with the context `{ editor }`), and `header.gear` (entries at the end of the gear menu in the header: `id`, `label`, `onClick()`; the way to a screen of the plugin's own), and `plugin.settings` (panels on the plugin's own page in Administration > Plugins: `id`, `title`, `render(ctx)`, [below](#panels-on-the-plugins-own-page)). `order` places an item; `when(ctx)` shows it only when it answers true.
- `kb.api` calls the plugin's own routes (`/api/v1/plugins/<id>/...`), and `kb.api.upload` sends them a file ([below](#uploading-a-file-from-a-panel)); `kb.core` calls the app's API. The server answers what the signed-in person may see.
- `kb.me()` is the person signed in now. The script is started once per page load, so call `kb.me()` when you need it and do not keep its answer: another person may sign in without a reload.
- `kb.current.page()` and `kb.current.space()` are what the person is looking at; `kb.current.subscribe(fn)` tells when that changes. Either can be null.
- `kb.navigate(path)` goes to an address inside the app. Someone in the middle of an edit is asked first, as for a click on a link, and may stay.
- `kb.screens.add({ path, title, render(rest), panel })` adds a screen of the plugin's own at `/x/<plugin id>/<path>`, inside the app's three panels. `panel` names a slot that fills the right panel. Lead to it from the gear menu: `kb.slots.add("header.gear", { id: "reports", label: "Reports", onClick: () => kb.navigate("/x/<plugin id>/reports") })`. What only an Admin manages (lists the plugin keeps, files it was given) belongs in a [panel on its page](#panels-on-the-plugins-own-page), not in a screen of its own.
- `kb.dialogs.confirm(...)` and `kb.dialogs.ask(...)` are the app's own dialogs ([their options](#dialogs-and-a-surface-over-the-whole-window)). Never use a browser popup (`alert`, `confirm`, `prompt`).
- What a plugin draws sits inside an error boundary. A part that throws is left out and written to the server's log; the screen stays. Script errors are never counted as failures of the plugin.
- A script runs in every signed-in person's browser with that person's session. It can do in the browser whatever that person can.

The rules of the app's screens hold for a plugin's parts: no help text and no placeholder text (a setting's `help` and `default` are drawn by the app, under and in its field); the right panel holds facts about the thing, not controls for it beyond what the app puts there; use the app's class names (`tb` for a button, `muted`, `small`).

### Panels on the plugin's own page

Each plugin has a page in Administration > Plugins: the gear left of its name opens it. The app draws its description, its state, Disable or Enable and Remove, its settings and its permissions there. A plugin's script adds panels of its own below them, in the slot `plugin.settings`:

```js
kb.slots.add("plugin.settings", { id: "files", title: "Libraries", render: (c) => h(Libraries, { kb }) });
```

- The app shows a panel only on the page of the plugin that added it, and only Admins see that page. The panel's own calls still go to the plugin's routes, which check the caller themselves: the sample's `/totals` answers 403 to anyone who is not an Admin.
- `render(ctx)` gets `{ plugin: { id, version } }`. Use `kb` as anywhere else: `kb.api` for the plugin's routes, `kb.dialogs.confirm` and `kb.dialogs.ask` before a removal or a rename.
- Each panel sits in a boundary of its own. A panel that throws shows "This part of the plugin failed. The rest of the page works." in its place, and the error goes to the server's log; the app's parts of the page and the other panels stay.
- The page is drawn only for a plugin this server loaded: a disabled plugin's script does not run, so it has no panels.
- How a panel looks: the app gives it its title as a heading. Use the app's classes, so plugin pages do not each invent their own:

  | For | Use |
  |---|---|
  | A list of things (files, rows) | `<table className="grid-table">` with `<thead>` and `<th>`; actions in a last cell `<td className="actions">` |
  | A button | `className="tb"`; the main action of the panel `"tb primary"`, a removal `"tb danger"` |
  | A form | `<form className="form">`: a `<label className="lbl">` then the field, in pairs; `className="hint"` for a line under a field; `className="form-error"` with `role="alert"` for what went wrong |
  | A quiet line, a count, a date | `className="muted"`, `"small"` |
  | A row of buttons over a table | `<div className="ptools row">` |

  No icons of your own beside the app's, no colours of your own: the [theme's colours](#the-themes-colours) when a panel draws something.

### Uploading a file from a panel

`kb.api.upload(path, file, options)` posts a file to one of the plugin's routes as the raw request body: `Content-Type: application/octet-stream`, and `X-File-Name` the file's name, URL-encoded (a `File`'s own name, or `options.name`). `options.onProgress(sent, total)` is called as the bytes go out. It answers the route's JSON, and throws the same error as `kb.api` for a refusal (`e.status`, `e.body`).

```js
const input = Object.assign(document.createElement("input"), { type: "file" });
input.onchange = () => kb.api.upload("/libraries", input.files[0], { onProgress: (sent, total) => setDone(Math.round(100 * sent / total)) })
  .then(reload).catch((e) => setError(e.body?.detail ?? "Not uploaded."));
input.click();
```

The route reads the body as a stream. ASP.NET Core refuses a request body over 30,000,000 bytes unless the route raises its limit, before it reads the body; behind IIS, the site's `web.config` limit (`maxAllowedContentLength`, also 30,000,000 bytes unless set) applies first, and an Admin raises it there ([admin guide](admin-guide.md#the-upload-size-limit)). A file over either limit answers 413.

```csharp
group.MapPost("/libraries", async (HttpRequest request, IActionBus bus, CancellationToken ct) =>
{
    // Set before the body is read. 100 MB here; read your own setting for it.
    if (request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } cap) cap.MaxRequestBodySize = 100L * 1024 * 1024;
    var name = Path.GetFileName(Uri.UnescapeDataString(request.Headers["X-File-Name"].ToString()));
    var file = UploadContent.FromStream(name, _ => Task.FromResult(request.Body), request.ContentLength);
    return (await bus.SendAsync(new AddLibrary { File = file }, ct)).ToHttp();
}).WithAction<AddLibrary>();
```

- The body can be read once. The action that takes it (`[JsonIgnore] public required UploadContent File { get; init; }`) counts the bytes it reads and refuses more than its own limit.
- Nothing scans a file sent to a plugin's route. A plugin that keeps uploaded files checks what it reads.

### The shared modules

Each module under `/shared/v1/` re-exports every named export of one library, at the version the app is built with, and its default export when the library has one. They are written when the app is built, from the installed packages.

| Module | Library | What it has |
|---|---|---|
| `/shared/v1/react.js` | `react` 19 | Everything of React: `createElement`, `Fragment`, `useState`, `useEffect`, `useRef`, `useMemo`, `useCallback`, `useContext`, `createContext`, `memo`, `lazy`, `Suspense`, ... |
| `/shared/v1/react-jsx-runtime.js` | `react/jsx-runtime` | `jsx`, `jsxs`, `Fragment`: what a JSX compiler's output imports. |
| `/shared/v1/react-dom.js` | `react-dom` | `createPortal`, `flushSync`, ... |
| `/shared/v1/tiptap-react.js` | `@tiptap/react` 3.33.0 | The editor, with everything of `@tiptap/core`: `Node`, `Mark`, `Extension`, `mergeAttributes`, `InputRule`, `PasteRule`, `nodeInputRule`, `markInputRule`, and for a node drawn by React `ReactNodeViewRenderer`, `NodeViewWrapper`, `NodeViewContent`, `useEditor`, `EditorContent`. |

- A node's own `addNodeView()` is part of `Node.create(...)`, so it can return a node view that is a plain DOM element (`{ dom }`) or use `ReactNodeViewRenderer`.
- `@tiptap/pm` (ProseMirror) is not shared, so a plugin cannot import `Plugin` or `PluginKey` from it. A copy of your own of ProseMirror does not work with the app's editor.
- The editor's own extensions (StarterKit, tables, task lists, images, code blocks) are not shared. They are part of every editor already.
- The library versions are those of the app you run, and change with it.

### Dialogs, and a surface over the whole window

`kb.dialogs.confirm(options)` answers true or false. `kb.dialogs.ask(options)` answers the text typed, trimmed, or null when the person cancels.

| Call | Options |
|---|---|
| `confirm` | `title` (needed); `body`, a line of text under it; `ok`, the label of the button that answers true ("OK" when left out); `cancel` ("Cancel"); `danger`: true makes the OK button red; `focusCancel`: true makes Enter press Cancel, for what cannot be undone. |
| `ask` | `title` and `label` (the field's label), both needed; `initial`, the field's first value; `ok`; `validate(text)`, which answers a message to show under the field, or null to accept the text. An empty answer is refused ("Required."). The field takes 200 characters at most. |

```js
if (changed && !(await kb.dialogs.confirm({ title: "Discard your changes to this chart?", ok: "Discard", cancel: "Keep editing", danger: true }))) return;
const name = await kb.dialogs.ask({ title: "Rename the chart", label: "Name", initial: chart.name });
```

- One dialog at a time: a dialog opened while another is open answers the first as cancelled (false, or null).
- The app's dialogs are `<dialog>` elements opened with `showModal()`.

A surface over the whole window (an editor that needs the whole screen, opened from inside a page edit) is a `<dialog>` element of the script's own, opened with `showModal()`. There is no call of the app for it: the browser does what such a surface needs.

```js
const surface = document.createElement("dialog");
surface.className = "acme-surface"; // in the plugin's stylesheet, below
surface.setAttribute("aria-label", "Chart editor");
surface.addEventListener("close", () => surface.remove());
document.body.append(surface);
// Fill it with elements of your own. surface.close() closes it.
surface.showModal();
```

```css
.acme-surface { inset: 0; width: 100vw; height: 100vh; max-width: none; max-height: none; margin: 0; padding: 0; border: 0; background: var(--paper); color: var(--ink); }
```

- The browser puts it in its top layer, above everything of the app: menus, popups, notices.
- While it is open the rest of the page cannot be clicked or reached with Tab. Escape fires its `cancel` event and closes it. `preventDefault()` on that event keeps it open while you ask first; the browser may close it anyway on a second Escape, so save what the person did on `close`. When it closes, the browser gives focus back to where it was.
- A `kb.dialogs` call made while it is open shows above it, and the surface comes back when that dialog closes.
- Remove it when it closes, as above. A surface left open when the person leaves the page stays over the next screen.

### The theme's colours

Every colour of the app is a CSS variable, with a Day and a Night value. An Admin can change any of them (Administration > Branding > Theme colors), and a person switches between Day and Night, so read them with `var(...)` each time and never copy their values.

| Group | Variable | What it colours |
|---|---|---|
| Header | `--nav` | Header |
| Header | `--nav-fg` | Header text |
| Header | `--nav-ink` | Header text, quiet |
| Header | `--nav-hover` | Header button, hover |
| Header | `--nav-active` | Header button, on |
| Header | `--logo-bg` | Logo badge |
| Header | `--logo-ink` | Logo badge text |
| Header | `--search-bg` | Search box |
| Header | `--search-ink` | Search text |
| Header | `--search-ink2` | Search hint and icon |
| Header | `--search-line` | Search key outline |
| Header | `--avatar-bg` | Account badge |
| Header | `--avatar-ink` | Account badge text |
| Header | `--badge-bg` | Unread count |
| Header | `--badge-ink` | Unread count text |
| Surfaces | `--stage` | Behind the app |
| Surfaces | `--paper` | Page |
| Surfaces | `--rail` | Tree and side panel |
| Surfaces | `--line` | Lines and borders |
| Surfaces | `--hover` | Row, hover |
| Surfaces | `--toolbar-bg` | Editor toolbar |
| Surfaces | `--switch-knob` | Switch knob |
| Surfaces | `--scrollbar-thumb` | Scroll bar |
| Surfaces | `--scrollbar-track` | Scroll bar track |
| Text | `--ink` | Text |
| Text | `--ink2` | Text, quiet |
| Text | `--link` | Links |
| Text | `--folder` | Folder icons |
| Accent | `--accent` | Accent |
| Accent | `--accent-ink` | Text on accent |
| Accent | `--cur-bg` | Selected item |
| Accent | `--focus` | Focus ring |
| Accent | `--selection` | Selected text |
| Accent | `--btn-primary` | Primary button |
| Accent | `--btn-primary-ink` | Primary button text |
| Notices | `--warn-bg` | Notice |
| Notices | `--warn-line` | Notice outline |
| Notices | `--warn-ink` | Notice text |
| Notices | `--danger` | Danger and errors |
| Notices | `--danger-ink` | Text on danger buttons |
| Notices | `--success` | Added (history, diffs) |
| Notices | `--removed` | Removed (history, diffs) |
| Content | `--tag-bg` | Tags and pills |
| Content | `--tag-ink` | Tag text |
| Content | `--code-bg` | Code block |
| Content | `--code-ink` | Code text |
| Content | `--code-comment` | Code: comments |
| Content | `--code-keyword` | Code: keywords |
| Content | `--code-string` | Code: strings |
| Content | `--code-number` | Code: numbers |
| Content | `--code-variable` | Code: variables |
| Content | `--code-title` | Code: names |
| Content | `--code-deletion` | Code: deletions |
| Content | `--code-meta` | Code: other |

- There is no `--bg`: a surface like a page is `--paper`, what is behind it `--stage`, a side panel `--rail`.
- Quiet text, and marks that should be seen but not read (the dots of a grid), use `--ink2` or `--line`.
- The colours of the app's highlighted code (`--code-*`) are those of a dark code block in both themes.

## Page content

A page's content is the `ContentJson` of `page.create`, `page.save` and `PageDto`: a string holding the editor's document as JSON, in the editor's (TipTap 3, ProseMirror) document format.
A plugin that reads or changes a page works on that JSON.

- The top is one node of type `doc`. `page.create` and `page.save` refuse anything else ("Not an editor document."), and a string over 5,000,000 characters.
- A node is an object: `type` (its name), `attrs` (its attributes, optional), `content` (its child nodes, optional). A text node is `{ "type": "text", "text": "...", "marks": [...] }`; a mark is `{ "type": "bold" }` or, with attributes, `{ "type": "link", "attrs": { "href": "..." } }`.
- The server stores the string as it was sent and never rewrites it. A node of a type it does not know is kept, and is read as a plugin's node ([below](#editor-blocks)).
- An attribute may hold any JSON value, an object or an array too: the server keeps it as it is. In the browser, the editor keeps only the attributes a node's extension declares (`addAttributes`), so a plugin's node declares every attribute it stores.
- A page made with `ContentJson` null holds `{"type":"doc","content":[]}`.

The node types the editor has by itself. Any other type is a plugin's.

| Type | Holds | Attributes the server reads |
|---|---|---|
| `doc` | blocks | |
| `paragraph` | inline content | |
| `heading` | inline content | `level`: the editor writes 2, 3 or 4; the server reads 1 to 6, and 2 when it is missing |
| `bulletList`, `orderedList` | `listItem` | |
| `listItem` | blocks: a `paragraph` first, then more blocks or a nested list | |
| `taskList` | `taskItem` | |
| `taskItem` | like `listItem` | `checked`: true or false |
| `blockquote` | blocks | |
| `codeBlock` | text only, no marks | `language`: the language's name (`powershell`, `bash`, `json`, `plaintext`, ...) |
| `table` | `tableRow` | |
| `tableRow` | `tableHeader` or `tableCell` | |
| `tableHeader`, `tableCell` | blocks, usually one `paragraph` | |
| `horizontalRule` | nothing | |
| `image` | nothing | `src`: `/api/v1/images/<id>` for an image stored in the space; `alt`; `width` in pixels |
| `hardBreak` | nothing: a line break inside a paragraph | |
| `text` | `text`, `marks` | |

The marks the editor has by itself: `bold`, `italic`, `underline`, `strike`, `code`, `link` (`href`). The Markdown outputs drop `underline`; the Word and PDF exports keep every one.
The editor writes more attributes than the server reads (an ordered list's `start`, a cell's `colspan`, `rowspan` and `colwidth`, an image's `title` and `height`). They are stored and shown by the editor; search, Markdown and exports do not use them.

A small complete document: a heading, a paragraph with a bold word, a numbered list, a code block and a table.

```json
{
  "type": "doc",
  "content": [
    { "type": "heading", "attrs": { "level": 2 }, "content": [ { "type": "text", "text": "Restart the service" } ] },
    { "type": "paragraph", "content": [
      { "type": "text", "text": "Do this on " },
      { "type": "text", "marks": [ { "type": "bold" } ], "text": "each" },
      { "type": "text", "text": " server." } ] },
    { "type": "orderedList", "content": [
      { "type": "listItem", "content": [ { "type": "paragraph", "content": [ { "type": "text", "text": "Stop the service." } ] } ] },
      { "type": "listItem", "content": [ { "type": "paragraph", "content": [ { "type": "text", "text": "Start it again." } ] } ] } ] },
    { "type": "codeBlock", "attrs": { "language": "powershell" }, "content": [ { "type": "text", "text": "Restart-Service KbWorker" } ] },
    { "type": "table", "content": [
      { "type": "tableRow", "content": [
        { "type": "tableHeader", "content": [ { "type": "paragraph", "content": [ { "type": "text", "text": "Server" } ] } ] },
        { "type": "tableHeader", "content": [ { "type": "paragraph", "content": [ { "type": "text", "text": "Port" } ] } ] } ] },
      { "type": "tableRow", "content": [
        { "type": "tableCell", "content": [ { "type": "paragraph", "content": [ { "type": "text", "text": "app01" } ] } ] },
        { "type": "tableCell", "content": [ { "type": "paragraph", "content": [ { "type": "text", "text": "8080" } ] } ] } ] } ] }
  ]
}
```

- An AI import drafts its pages with the same types: `heading`, `paragraph`, `bulletList`, `orderedList` (numbered steps), `listItem`, `codeBlock`, `blockquote` (notes and warnings), `table` with `tableHeader` and `tableCell`, `image`, `hardBreak`, and the marks `bold`, `italic` and `code`, with the nodes [import contributors](#adding-to-the-draft-import-contributors) and [draft contributors](#adding-to-the-draft-from-its-text-draft-contributors) add. A `codeBlock` the AI drafts has the `language` the model gave it, in lower case, or `plaintext`: a fenced block of a Markdown file usually keeps its language, but the model decides. What is stored is the content the importer reviewed and accepted.
- A page is stored as its writer sent it, by the editor or by anyone who calls the API. Read it with care: a node may lack `content`, hold `null`, or be of a type you do not expect. The core's own readers walk into the `content` of any node they do not know, so its text is never lost.
- There is no chart or diagram block in the core, and no format for one. A plugin that wants charts or diagrams brings its own: an [editor block](#editor-blocks) of its own type, the library that draws it in the browser (in its `ui/` folder), and a renderer for the server.

To make a page, send `page.create` with `Kind` set to `"Page"` or `"Folder"`, written exactly so: the names are case-sensitive, and any other value is refused ("Folder or Page."). A folder holds no content: its `ContentJson` is ignored. `ParentId` must be a folder of the same space, or null for the top of the space.

### Adding an image or an attachment

`image.add` and `attachment.add` take the file as an `UploadContent`. Make one from bytes or from a stream:

```csharp
var image = await bus.SendAsync(new AddImage { SpaceId = spaceId, File = UploadContent.FromBytes("chart.png", png) }, ct);
// image.Value.Url is "/api/v1/images/<id>": the src of an image node of a page in that space.

var file = await bus.SendAsync(new AddAttachment { PageId = pageId, File = UploadContent.FromStream("report.csv", OpenReportAsync, length: null) }, ct);
```

- `FromStream` calls its `open` function for each read, and each stream must start at the first byte. The action closes the stream. `length` may be null: the action counts the bytes it reads, so a length that is wrong does not get a file past a limit.
- An `UploadContent` has no content type. `image.add` reads the type from the bytes; `attachment.add` serves every attachment as a download.
- On an app before 0.4.0, which has neither, derive a class of your own from `UploadContent` (`FileName`, `Length`, `OpenReadAsync`).

- Both actions record who added the file, so they need a person: they answer 401 to a plugin's own work (a job, an after handler, a health check). Send them where the actor is a person (a before hook, a handler, a route), with that person's rights: they need the right to create and edit pages in the space.
- `image.add` takes PNG, JPEG, GIF or WebP, 20 MB at most. An SVG is not an image: attach it, or draw it in the browser.
- `attachment.add` takes any file up to the size an Admin set (Administration > Uploads).
- Neither action scans the file, whoever sends it: only the files of an AI import are scanned ([below](#import-the-virus-scan-hook)).
- A before hook on `page.create` that adds an image runs before the page is made. If the page is then refused, the image is stored and no page shows it.

### Folders, and writes of the compatible API

A hook on `page.save` or `attachment.add`, and an enforcing plugin's lock on them, see every write to a page's or a folder's content and files, whichever API made it:

- `page.save` takes a folder too. Its `ContentJson` is saved as the folder's body, with a version, when it differs from the body the folder has; sent unchanged (a rename), the body is left as it is. Aerie's own screens show no folder body; the compatible API (`/rest/api`) writes one, through this action. A hook that reads `ContentJson` checks `PageDto.Kind` or loads the page when it treats folders apart.
- `attachment.add` takes a folder too: `PageId` may name one.
- A new version of an attachment, and an attachment renamed or moved to another page or folder, are made by the compatible API alone: no core action does them. Before writing, it runs `attachment.add`'s lock and before hooks with the file as it would arrive: `PageId` the page or folder it lands on, `File` the new file (or the stored one, under its new name). A refusal refuses the write. A hook that replaces the payload refuses it too (`422`): the compatible API cannot store what the hook made instead. `attachment.add`'s after handlers do not run for these: no attachment is added.
- The compatible API answers a refusal or a lock as its errors are shaped (`403` with the hook's message), as it does for pages.

## Editor blocks

A plugin can add its own kind of content to pages: a block, an inline node or a mark of the editor (TipTap 3).
It has two halves. Without the first nobody can write the content; without the second the server still keeps it, and reads it as a placeholder.

**In the browser.** The script adds TipTap extensions with `kb.editor.extend(...)` and a toolbar button in the slot `editor.toolbar`. The sample's notice block:

<!-- sample:editor -->
```js
import { Node } from "/shared/v1/tiptap-react.js";

/** A block of the plugin's own: a line of text shown as a notice. Its type in the stored page is "sampleNotice". */
const Notice = Node.create({
  name: "sampleNotice",
  group: "block",
  content: "inline*",
  // Before the editor's own rule for <p>, or a copied notice would be pasted as a plain paragraph.
  parseHTML: () => [{ tag: "p[data-sample-notice]", priority: 60 }],
  renderHTML: ({ HTMLAttributes }) => ["p", { ...HTMLAttributes, "data-sample-notice": "", class: "sample-notice" }, 0],
});

function addNotice(kb) {
  // A stylesheet of the plugin's own ui folder, beside this script.
  const css = document.createElement("link");
  css.rel = "stylesheet";
  css.href = new URL("./notice.css", import.meta.url).href;
  document.head.append(css);

  kb.editor.extend(Notice);
  // The button turns the line the cursor is in into a notice, and back.
  kb.slots.add("editor.toolbar", { id: "notice", label: "Notice", onClick: (c) => c.editor.chain().focus().toggleNode("sampleNotice", "paragraph").run() });
}
```

- The node's `name` is its type in the stored page. Choose one no other plugin will use.
- A script can load a stylesheet from its own `ui/` folder, as above. Use the app's colour variables (`--ink`, `--line`, `--warn-bg`, ...: [all of them](#the-themes-colours)) so the block follows the theme.
- Every editor made after the call has the extensions. A page that is open and not being edited is drawn again with them.
- Extensions passed in one call are checked together, so one may name another (a block and the title node it holds). Pass such a set in one call.
- A node that is drawn as a `<p>` needs `priority: 60` (or more) in its `parseHTML` rule, as above. Without it the editor's own rule for `<p>` wins and a copied block is pasted as a plain paragraph.
- An extension that takes a name of the editor's own (`paragraph`, `bold`, ...), or with which the editor cannot be built, is left out and written to the server's log. The editor stays.

**On the server.** Search, history, the Markdown an agent reads, and the Word and PDF exports read a page's content on the server. `app.EditorNode(Notice.Renderer)` in `Configure` tells them how to read the node:

<!-- sample:editornode -->
```csharp
/// <summary>
/// How the server reads the block for history, agents and exports.
/// It gives no Plain: search then finds the page by the text inside the block, which is what a notice needs.
/// </summary>
public static NodeRenderer Renderer { get; } = new()
{
    Type = "sampleNotice",
    MarkdownLines = node => ["> **Notice:** " + Text(node)],
    Blocks = node => [new DocParagraph([new DocRun("Notice: ", Bold: true), new DocRun(Text(node))], Quote: true)],
};

// The node is the stored JSON of the block: { "type": "sampleNotice", "content": [ { "type": "text", "text": "..." } ] }.
// A page is stored as its writer sent it, so read with care: a renderer must not throw on a node of an odd shape.
private static string Text(JsonElement node) =>
    node.TryGetProperty("content", out var kids) && kids.ValueKind == JsonValueKind.Array
        ? string.Concat(kids.EnumerateArray().Select(k => k.ValueKind == JsonValueKind.Object && k.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : ""))
        : "";
```

- Each part (`Plain`, `MarkdownLines`, `Blocks`) is optional. A part that is missing, answers null, answers something with a null in it, or throws gives the placeholder: the text inside the node, with `[sampleNotice]` before it in Markdown and exports. A renderer that fails is written to the server's log, at most once a minute for each part. It is not counted as a failure of the plugin: what a page holds is its writer's choice, and a page must not be able to switch a plugin off.
- The node is what a writer stored, not only what your editor part writes. Read it with care, as the sample does: `content` may be missing or of another shape.
- A renderer runs on every save, diff and export of a page that holds the node. It must be quick and must not call other systems.
- `Plain` given, the text inside the node is no longer indexed by itself: return it from `Plain` if search should find it.
- One renderer per node type. A plugin that registers one for a node of the editor itself, or for a type another plugin renders, is not loaded.
- Inside a table cell a plugin's node is read as its text only (`Plain`, or the text inside it): a cell is one line of text in Markdown and in exports.
- A `DocTable` a renderer answers is part of the Word and PDF export, but it is not one of the page's tables in the Export menu (CSV, Excel). A `DocImage` is drawn only for an image the page itself shows in an image of the editor; for a picture of the block's own, answer a `DocPicture`.
- Pages saved before the plugin was installed keep their old search words until they are saved again or an Admin selects Rebuild index (Administration > Search).

`MarkdownLines` is what the Markdown outputs show: history and its diffs, the AI assist and import diffs, and the page an agent reads (`page.markdown`). `Blocks` is what the Word and PDF exports show. Neither is used for the other: a node that should look like code in both answers a fenced block in `MarkdownLines` and a `DocCode` in `Blocks`.
The blocks a renderer can answer in `Blocks`:

| Block | Word | PDF |
|---|---|---|
| `DocParagraph(Runs, Heading = 0, Marker = null, Indent = 0, Quote = false)` | A paragraph. `Heading` 1 to 6 is a bold heading in a larger size; `Marker` is put before the text (a bullet, `1.`); `Indent` indents by list level; `Quote` makes it italic with a grey line at its left. | The same; a quote is italic and grey. |
| `DocRun(Text, Bold, Italic, Underline, Strike, Code, Href)` | One run of text in a paragraph. `Code` sets Consolas. `Href` is a link when it is an absolute `http`, `https` or `mailto` address, else blue underlined text. A line break in `Text` is a line break. | The same, with Courier New for `Code`. |
| `DocCode(Language, Text)` | Each line of `Text` as a line in Consolas, 9.5 pt, on a grey background, then an empty paragraph. | One paragraph in Courier New, 9 pt, on a light grey background; leading spaces are kept and a tab is four spaces. |
| `DocTable(Rows, HeaderRow)` | A table with a border; the first row bold when `HeaderRow`. | A table of equal columns across the text width. |
| `DocImage(ImageId, Alt, Width)` | The stored image with that id, when the page itself shows it in an `image` node. Otherwise the text `[image: <Alt>]`. | The same; a GIF is shown as its alt text. |
| `DocRule()` | A horizontal line. | A horizontal line. |
| `DocPicture(ContentType, Content, Alt, Width = null)` | A picture the renderer drew, when `ContentType` is `image/png` and `Content` is a PNG (its signature and header are checked) of at most 2 MB and 4000 pixels a side. `Width` is in CSS pixels, at most the text width; null draws it at its own width. Anything else is the text `[image: <Alt>]`. `Alt` is the picture's description in the file. | The same. |

- A `DocPicture` is drawn from PNG only. SVG is never drawn: a plugin's SVG can hold script and references to other hosts, which an export must not carry. Draw the picture as PNG on the server, or answer the `Alt` text and a table of what the picture shows.
- A renderer runs on every export of the page, so a picture drawn there is drawn each time: keep it quick, or keep the PNG in the node's attributes when the block is saved.
- Markdown does not have pictures: it shows what `MarkdownLines` gives.

- `DocCode.Language` is not used by the Word or PDF export: there is no syntax colouring. It must not be null (use `"plaintext"`); a block with a null in it gives the placeholder.
- The core's own `codeBlock` node gives a `DocCode` with its `language` attribute, and in Markdown a fenced code block with the language after the opening fence.

**Content is never lost.** The server stores a page as the editor sent it and never rewrites it. An editor that does not know a node (the plugin is disabled, removed, or its script failed) shows a box with the type's name. The box can be moved or deleted, not edited, and a save sends the content back as it was. When the plugin is back, so is the block.
Text with a mark the editor does not know shows as plain text and can be edited; the mark stays on it and is saved with it. Among several marks on one text its place may change, which changes nothing a reader sees.
The same holds for a page whose stored content is not what any editor would write (a null where a block should be): what cannot be shown is a box, or the whole page is one box, and a save keeps it.
Where a box cannot stand (first in a list item, inside a table row, a mark inside a code block), the nearest part that can be carried is carried whole: the list, the table or the code block becomes the box.

Limit: an attribute a plugin adds to one of the editor's own nodes (an alignment on `paragraph`) is not carried. Without the plugin's extension the editor drops it on the next save. Use a node or a mark of your own.

## Logging

A plugin logs with `ILogger<T>`, taken from the container like any service: put it in a constructor (a hook, a handler, a job, a decorator), or ask `IServiceProvider` for it. The lines go to the app's log, under the type's name.

```csharp
public sealed class SyncJob(ILogger<SyncJob> log, IActionBus bus) : IKbJob
{
    public async Task RunAsync(CancellationToken ct)
    {
        log.LogInformation("Sync started for {Source}.", "cmdb");
        // ...
    }
}
```

- Never log a secret, a setting marked secret, a header value, a token or a password, at any level. Log that a value is missing, never the value.
- Never log an `HttpRequestMessage` or its headers: `ToString()` prints every header with its value. Log the method, the host and the status.
- Exceptions of `HttpClient` can hold the address of the call; keep secrets out of addresses (query strings), and they stay out of logs.
- The app's own HTTP clients log header names with their values redacted.

## Calling other systems

List every host the plugin calls in `outboundHosts`. Take the plugin's client from `IHttpClientFactory` with the name `PluginHttp.ClientName("<id>")`.
That client refuses a host that is not listed, and it follows no redirect: the plugin gets the 3xx answer and may ask again with the new address, which is checked like any other. List the host a redirect leads to as well.
When you follow a redirect yourself, build a new request for the new host and add your own secret headers again only when that host is one you meant to send them to. Never copy the headers of the first request.

## Extension points

An extension point is an interface of the contract that the core calls. A plugin supplies one by registering a service in `ConfigureServices`.

| Interface | Register | The core uses it |
|---|---|---|
| `IEmailSender` | `services.AddSingleton<IEmailSender, MySender>()` | For every mail. One sender: the one whose `ProviderId` is `Kb:Email:Provider` in the app's configuration (case does not matter). A sender that throws is a failure of its plugin. |
| `IAuditSink` | `services.AddSingleton<IAuditSink, MySink>()` | Every sink gets each audit entry after it is written (a SIEM, for example). A sink that throws is a failure of its plugin; the entry stays written. |
| `IFileStoreKind` | `services.AddSingleton<IFileStoreKind, MyKind>()` | A kind of storage place (Administration > Document storage), chosen by its `Kind`. The plugin cannot be disabled while a storage place uses its kind. |
| `ILogShipperKind` | `services.AddSingleton<ILogShipperKind, MyKind>()` | A kind of log target (Administration > Log shipping). The plugin cannot be disabled while an enabled target uses its kind. |
| `IMalwareScanner` | `services.AddSingleton<IMalwareScanner, MyScanner>()` | Every scanner, for every file of an AI import. [Below](#import-the-virus-scan-hook). |
| `IOutboundRequestDecorator` | `services.AddSingleton<IOutboundRequestDecorator>(...)` | Every decorator, for each call of the AI service. [Below](#the-ai-service-and-outbound-headers). |

- An import contributor is registered in `Configure` with `app.ImportContributor<T>()`, not as a service, so the core knows which plugin it belongs to. [Below](#adding-to-the-draft-import-contributors).
- `ProviderId` is the plugin's id. The app does not check it, but uses it: to choose the email sender, and to count a failure against the right plugin. Use your manifest id.
- Declare what the plugin supplies in the manifest's `extensionPoints` (`email-sender`, `audit-sink`, ...). It is shown on the Plugins screen; the app does not act on it.
- Declared in the contract and not called by the core yet: `IMapProvider`, `IAiProvider`, `IRecordImporter`, `IPageAction`, `IExportFormat`, `IDnsProvider`, and `Kb.Plugins.ISearchProvider` (the app's search does not go through it). A plugin that registers one loads, and nothing calls it.
- `IFileStore` and the search engine are the core's own. A plugin's registration of them is ignored; supply a storage kind instead.

## The AI service and outbound headers

The app has one AI service, set on Administration > AI: an OpenAI-compatible server address, a model, an API key, limits, and the rules the model is held to. A plugin does not call the model and cannot see the API key.
What a plugin can do is add to the service's calls, for an access gateway in front of the model server:

```csharp
public void ConfigureServices(IServiceCollection services, IPluginContext context) =>
    services.AddSingleton<IOutboundRequestDecorator>(new GatewayHeaders(context));
```

- `DecorateAsync(OutboundRequest request, ct)` gets every call the service makes: `request.Purpose` is `OutboundPurposes.Ai` (`"ai"`) and `request.Message` the `HttpRequestMessage`, with its `Authorization` header already set.
- The calls are `POST <address>/v1/chat/completions` and `GET <address>/v1/models` (the Model list on Administration > AI).
- Every decorator runs, in plugin load order. One that throws refuses the call; it is not sent.
- `OutboundPurposes.Ai` is the only purpose today. Check it, so a kind of call added later does not get your headers unasked.
- Not set up, or not for this host: do nothing. Set up for this host but missing a value it needs: throw, so the call is refused with "A plugin that adds to the AI's requests failed" instead of reaching the gateway without its headers.
- No redirect is followed on these calls: .NET would carry added headers to the new host. A 3xx answer is a refusal ("The model server answered a redirect ...").
- The app logs no header value of these calls, at any level.
- The call goes through the app's HTTP client, so the manifest's `outboundHosts` does not apply. Check the request's host yourself.
- Reasoning is off unless the Admin turns it on: the request says `chat_template_kwargs.enable_thinking: false` and `reasoning_effort: "none"`. Leave the body alone.

The whole plugin, with settings for the host and two secret headers, a unit test and its packaging: [example: an outbound-headers plugin](example-outbound-headers.md).

## Inventory

The app keeps an inventory of applications and where they are installed (Inventory, in the tree). Pages say which applications they apply to (page details, below).
A plugin feeds it with the contract's action `inventory.installs.sync`, from a job that reads a CMDB, a scanner or a file share:

```csharp
public sealed class SyncJob(IActionBus bus, MySource source) : IKbJob
{
    public async Task RunAsync(CancellationToken ct)
    {
        var rows = (await source.ReadAsync(ct)).Select(r => new SyncRowDto(r.Application, r.Vendor, r.Server, r.Version)).ToList();
        var result = await bus.SendAsync(new SyncInstalls { Source = "cmdb", Rows = rows, RemoveMissing = false }, ct);
        if (result.Problem is { } p) throw new InvalidOperationException(p.Message);
    }
}
```

| Field | |
|---|---|
| `Source` | Who the data comes from (1 to 100 characters). The history of each install names it. |
| `Rows` | One `SyncRowDto(Application, Vendor, Server, Version)` per install. A new application is made by name; `Vendor` is set only on a new application, or one that has none. |
| `RemoveMissing` | True: an install of this source that is not in `Rows` is removed. |
| `Apply` | False: nothing is saved; the answer says what would change. Default true. |

The answer, `SyncResultDto`, counts what was added, changed, unchanged and removed, with each change. The [action reference](action-reference.md#inventoryinstallssync) has the types and an example of their JSON.

- A job runs as the plugin, and a plugin may always edit the inventory.
- A script outside the app sends the same rows to `POST /api/v1/inventory/sync` with an API token: the token needs the Write scope, and its owner (or the token itself) must be allowed to edit the inventory. Administration > Security > Inventory access lists who may: everyone, a person, a group, the people with any role in a space, the people with a given role, or one named API token. A new install has no rule there: only Admins view and edit the inventory until an Admin adds one. A token is never an Admin, so an Admin's token needs a rule too. See `tools/Seed-Inventory.ps1`.
- A plugin can hook the action like any other: a before hook to refuse rows, an after handler to react to a sync.

## Page details

A page has details: the applications (and servers) it applies to, its owner (a person or a group: who owns the process or the escalation path, not who wrote the page), and its review cycle in months.
They come with every page a plugin reads, in `PageDto.Details` (`PageDetailsDto(AppliesTo, Owner, ReviewMonths)`). Saving them is core-only (`page.details.save`): a plugin reads them and cannot hook their change.

## Import: the virus-scan hook

AI Import (Import inbox) reads files people upload and drafts pages from them. Every `IMalwareScanner` sees every file before it is read, zip entries and mail attachments too:

```csharp
public sealed class MyScanner : IMalwareScanner
{
    public string ProviderId => "acme.scanner";
    public async Task<ScanVerdict> ScanAsync(Stream content, string fileName, CancellationToken ct) => /* ask the engine */ ScanVerdict.Clean;
}
```

- `Infected`: the file is quarantined. It is not read, not sent to the model, its item says "Blocked", and the audit log has `import.quarantine` with the file's name and SHA-256.
- `Clean` and `Unknown`: the file is read.
- A scanner that throws blocks that file ("Not scanned: the virus scanner failed."), not the whole upload.
- Each scanner gets its own stream of the file's bytes.
- Attachments and images added to pages directly are not scanned today; only imports are.

### Adding to the draft: import contributors

A plugin can read each file of an import and add content of its own to the draft, before the importer reviews it. It registers an `IImportContributor` in `Configure` with `app.ImportContributor<T>()`. The sample turns a file named `*.notice.txt`, or `*.notice` ([a kind it claims](#file-kinds-the-app-does-not-read)), into its notice block:

<!-- sample:import -->
```csharp
/// <summary>
/// Adds to an AI import's draft: a file named "*.notice.txt" becomes a notice at the end of the page drafted from it. A "*.notice"
/// file is a kind the app does not read; the plugin claims it (<c>ImportFileKinds(".notice")</c>), so its page is drafted by this alone.
/// It gets the file only after every virus scanner passed it, as a read-only stream, and answers editor nodes as a page stores them.
/// </summary>
public sealed class NoticeFromFile : IImportContributor
{
    public async Task<ImportContribution?> ContributeAsync(ImportedFile file, CancellationToken ct)
    {
        if (!(file.FileName.EndsWith(".notice.txt", StringComparison.OrdinalIgnoreCase) || file.FileName.EndsWith(".notice", StringComparison.OrdinalIgnoreCase))
            || file.Length > 10_000) return null;
        await using var stream = await file.OpenReadAsync(ct);
        var line = (await new StreamReader(stream).ReadLineAsync(ct))?.Trim();
        if (string.IsNullOrEmpty(line)) return null;
        var node = JsonSerializer.SerializeToElement(new { type = "sampleNotice", content = new[] { new { type = "text", text = line } } });
        return new ImportContribution([node]);
    }
}
```

- **When.** Once for each file of an item, after every scanner has passed the file and the AI has drafted the item, before the importer sees the draft. Not for a blocked file, a zip itself (its entries are files of their own), a system file, or a picture inside a document; not for an item the AI could not draft. For a file of a [kind a plugin claims](#file-kinds-the-app-does-not-read), the AI drafts nothing: the contributors get an empty page to fill.
- **What it gets.** `ImportedFile(ImportItemId, Path, FileName, Length, OpenReadAsync)`. `Length` is the file's exact size in bytes. Each call of `OpenReadAsync` gives a new read-only stream of the file's bytes, so each contributor reads its own. The stream is a seekable stream over the whole file, which the core has read into memory: a zip (a `.docx`) can be opened on it directly, without a copy. There is no content type: read the name and the bytes.
- **How it runs.** Made new for each call, with the plugin's services, in a scope of its own, as its plugin: an actor of kind Plugin with source `import` and `ImportItemId` set. It reads what a plugin may read (no sensitive space), and an action that records a person (`page.create`, `attachment.add`) answers 401.
- **What it answers.** Null, or `ImportContribution(Nodes)`: block nodes as a page stores them ([page content](#page-content)), at most 50 of them and 512 KB of JSON ([how they are counted](#how-the-limits-are-counted)). Each must be an object whose `type` is a string other than `doc` and `text`. A node of the plugin's own type needs its [editor block](#editor-blocks) for the importer to see and edit it.
- **What the importer sees.** The nodes are added at the end of the first page of the draft, file by file. A file contributor cannot choose the page or the place: a plugin that should put its nodes elsewhere (a diagram next to the text that describes it, on the second page) adds them from a [draft contributor](#adding-to-the-draft-from-its-text-draft-contributors) instead, which says where. The draft's warnings name each plugin and file ("Charts added 2 blocks from flow.drawio at the end of the first page."). The importer reviews them with the rest, and can change or delete them before accepting.
- **What is checked.** The nodes are copied when the call returns. When the draft is accepted they are saved by `page.create` or `page.save` like any content: the same checks, the same hooks. Nothing of them is sent to the model.
- **Limits.** A call that throws, runs past 10 seconds, or answers what the core cannot use adds nothing for that file. It is written to the log and counted as a failure of the plugin ([five in ten minutes disable it](#what-the-core-disables-a-plugin-for)). At the limit its token is cancelled and the core stops waiting ([time](#how-the-limits-are-counted)). The import goes on.
- **Sensitive content.** Contributors are not called for an item in which the core finds a secret (a password, a key, a token: the same finder that marks the draft), or whose upload named a sensitive space as its destination. The draft then says "Plugins were not given these files: they hold a secret, or go to a sensitive space." An importer who chooses a sensitive space only when accepting has already let the contributors read the files: choose it at upload.
- **Audit.** Each contribution writes `import.contribute`, with the importer as the actor and the plugin's id, the file's path and SHA-256, and the number of nodes.
- **Files.** A contributor adds nodes, not files. A plugin that wants a picture on the page sends `image.add` ([above](#adding-an-image-or-an-attachment)) from a before hook on the `page.create` or `page.save` that accepting sends (source `import`), where the actor is the importer, and puts the image's address in the content.
- `IRecordImporter`, declared in the contract, is something else: records synced from another system. It is not called.

### File kinds the app does not read

An import contributor gets the files the app reads. A file of a kind the app has no reader for (a drawing in a format of its own) fails with "This kind of file is not read; it is kept as an attachment.", and no contributor sees it. A plugin that reads such a kind claims it, by extension, in `Configure`:

```csharp
app.ImportFileKinds(".notice")                                // a kind the app does not read: drafted by this plugin
```

The sample claims `.notice` and reads it with its import contributor ([above](#adding-to-the-draft-import-contributors)). An extension is a dot and 1 to 16 letters or digits, in any case; `ImportFileKinds` throws on anything else.

- **Which files.** A file whose extension the app has no reader for, claimed by a plugin this server loaded. The app decides by the extension: a claim on a kind it reads (`.txt`, `.docx`, `.pdf`, `.zip` and the others the Import inbox lists) changes nothing. A program or script (`.exe`, `.ps1` and the like) stays blocked, and a file whose bytes are a picture is read as a picture, whatever a plugin claims.
- **What happens to it.** The virus scanners read it first, as every file; a file a scanner flags stays blocked. Then it is kept whole: not unpacked (a format that is a zip inside stays one file), and not grouped with other documents (it is an item of its own; a mail's attachment stays with its mail). Its item gets a draft with one empty page, titled from the file name (`Network_Map.abc` gives "Network Map"). The AI is not called for it: there is no text to send. Then the import contributors run for the file and the draft contributors for the draft, exactly as for any item, with every rule above: the time limit, the node and size limits, sensitive spaces, the audit entries, and a failure that never breaks the import of the other files.
- **What the importer sees.** The page as the contributors filled it, with their warnings. "Drafted by" says "Plugin:" and the names of the plugins that claim the kind.
- **When nothing is added.** If the page is still empty after the contributors (none added anything, or all failed), the item fails: "Only <plugin> reads this kind of file, and it added nothing to the draft. The file is kept as an attachment." Bound for a sensitive space, the file is not given to any plugin, and the item fails: "Only a plugin reads this kind of file, and plugins are not given files that go to a sensitive space."
- **Two plugins.** Both may claim a kind. Every import contributor is called for every file, so each reads it; the page holds what each added.
- **A wrong extension.** The core does not check the file's first bytes. The contributor reads them, and answers null for a file that is not its kind; the item then fails as above.
- **A disabled plugin.** It is not loaded, so its claim does not count: the file is read as before (as text when its bytes are text, which goes to the AI; otherwise it fails as a kind the app does not read). A file read while a plugin claimed it, and drafted after the plugin was disabled, fails the same way.
- **Upload.** The Import inbox takes a file of any kind, within the attachment size limit; a claim changes neither.
- **Audit.** Each claimed file writes `import.claim`, with the importer as the actor and the file's name, its SHA-256 and the ids of the plugins that claim it (`plugins`). Each contribution then writes `import.contribute`, as for any file.
- **Plugins screen.** An Admin sees the kinds under "Reads for import" when selecting the plugin.

### Adding to the draft from its text: draft contributors

An import contributor sees a file, not what the AI drafted from it. A plugin that builds content from the draft's text (a chart of the procedure the page describes) registers an `IDraftContributor` with `app.DraftContributor<T>()`. It is called once for each item, with the draft's pages, and says where its nodes go. The sample repeats a paragraph that starts "Notice:" as its notice block, after the page's introduction:

<!-- sample:draft -->
```csharp
/// <summary>
/// Adds to an AI import's draft from its text: a paragraph that starts "Notice:" in a drafted page is repeated as a notice near the top
/// of that page, after its introduction, so the importer reviews it with the rest. It reads the draft and answers where its nodes go.
/// </summary>
public sealed class NoticeFromDraft : IDraftContributor
{
    public Task<DraftContribution?> ContributeAsync(ImportDraft draft, CancellationToken ct)
    {
        var additions = new List<DraftAddition>();
        for (var i = 0; i < draft.Pages.Count; i++)
        {
            using var doc = JsonDocument.Parse(draft.Pages[i].ContentJson);
            var blocks = doc.RootElement.TryGetProperty("content", out var c) ? c.EnumerateArray().ToList() : [];
            if (blocks.Any(b => b.GetProperty("type").GetString() == "sampleNotice")) continue; // a file already gave this page its notice
            var notice = blocks.Where(b => b.GetProperty("type").GetString() == "paragraph" && b.TryGetProperty("content", out _))
                .Select(b => string.Concat(b.GetProperty("content").EnumerateArray().Select(t => t.TryGetProperty("text", out var x) ? x.GetString() : "")))
                .FirstOrDefault(t => t.StartsWith("Notice:", StringComparison.Ordinal))?[7..].Trim();
            if (string.IsNullOrEmpty(notice)) continue;
            var node = JsonSerializer.SerializeToElement(new { type = "sampleNotice", content = new[] { new { type = "text", text = notice } } });
            additions.Add(new DraftAddition(i, DraftPlace.AfterIntroduction, [node]));
        }
        return Task.FromResult(additions.Count == 0 ? null : new DraftContribution(additions));
    }
}
```

- **When.** Once for each item the AI drafted, after every [import contributor](#adding-to-the-draft-import-contributors) has added its nodes, before the importer sees the draft. Draft contributors run one after another, in the order the plugins were loaded; each sees what the ones before it added. Not for an item the AI could not draft.
- **What it gets.** `ImportDraft(ImportItemId, Pages, Files)`. `Pages` are the draft's pages in order, each a `DraftPage(Title, ContentJson)`: the page's content as a page stores it ([page content](#page-content)), the AI's blocks with the import contributors' nodes already in them. The draft's pictures point to the import's own copies (`/api/v1/imports/files/<id>`) until the draft is accepted. `Files` are the paths of the item's files that were read. It is a copy: changing it changes nothing. It is not the text the app read from the files.
- **What it answers.** Null, or `DraftContribution(Additions)`: each a `DraftAddition(Page, Place, Nodes)`. `Page` is the page's index in `Pages` (0 is the first). `Place` is one of:

  | `DraftPlace` | The nodes go |
  |---|---|
  | `End` | After the page's last block. |
  | `Start` | Before the page's first block. |
  | `AfterIntroduction` | Before the first `heading` that has a block before it: after an opening paragraph, or, on a page that starts with a heading, after that first section. At the end when the page has no such heading. |
  | `AfterPicture` | 0.10.0. Right after the picture whose image node has `DraftAddition.Picture` as its `attrs.src` ([diagrams](#diagrams-in-the-pictures-of-an-import)). At the end when the page does not show it. |

  Additions to one place keep the order of the answer. The places are found on the page as it was before this contributor's additions. The nodes are checked as an import contributor's are, with the limits counted over all the additions of one answer together ([below](#how-the-limits-are-counted)).
- **What the importer sees.** The nodes in the draft where they were put, and a warning naming the plugin and the pages ("Charts added 1 block to "Restart the cluster"."). They review, change or delete them with the rest; accepting saves what they kept.
- **How it runs, sensitive content, failures, audit.** As for an import contributor: in its own scope, as its plugin (source `import`, `ImportItemId` set), within 10 seconds; never for an item with a secret or bound for a sensitive space. An answer that names a page the draft does not have, or a place that is not one of the four, is a failure like any other: nothing of it is added. Each contribution writes `import.contribute` with the plugin's id, the pages (`pages`, their indexes) and the number of nodes.
- **Retry.** Retry on an import item drafts it again from the files: the AI drafts new pages, and every contributor runs again on them. Nothing is kept from the earlier draft, so nothing is added twice.
- **After the draft is accepted.** The `page.create` or `page.save` that accepting sends has `ctx.Actor.Source` `import` and `ctx.Actor.ImportItemId` set ([where the call comes from](#where-the-call-comes-from)). A plugin that also builds its content in a before hook on those actions (for pages made by hand) skips a page that already holds it: look for its own node type in `ContentJson`, as the sample's contributor does on the draft. The importer may have deleted the node in review; a plugin that should not add it back then checks `Source` first, and leaves a page from an import as the importer accepted it.

### Diagrams in the pictures of an import

From 0.10.0 the app reads diagrams for draft contributors. When an import sends a picture to the AI (a picture imported on its own, or one inside a document), the app also asks the AI whether the picture is a diagram, and if so for its structure. A draft contributor gets it in `ImportDraft.Pictures`, one `DraftPicture` per picture sent, in that order:

- `Src`: the picture's image node's `attrs.src` in the draft. `Page`: the page that shows it (0 is the first). `FileName`.
- `Width` and `Height`: the picture's size in pixels, read from its file (0 when the app cannot tell). Areas are fractions of these sides, so a layout keeps the picture's proportions.
- `Diagram`: a `PictureDiagram`, or null when the picture is not a diagram or the AI could not read one.

A `PictureDiagram` holds:

| List | Each | |
|---|---|---|
| `Boxes` | `DiagramBox(Id, Label, Detail, Shape, Container, Area)` | `Detail` is a short second line, or null. `Shape` is `Box`, `Ellipse`, `Cylinder` or `Note` (`Box` when the AI named none of these). `Container` is the id of the container it sits in, or null. |
| `Containers` | `DiagramContainer(Id, Label, Container, Area)` | A frame around boxes and other containers. `Container` is the one it sits in, or null. |
| `Lines` | `DiagramLine(From, To, Label, Arrow, Dashed, Thick)` | `From` and `To` are ids of boxes or containers. `Arrow` is `None`, `Forward` (a head at `To`) or `Both`. |
| `Texts` | `DiagramText(Text, Container, Area)` with `Kind` and `Beside` | Text that belongs to no box. `Kind` is `Title`, `Remark` (a short comment written next to one box or container; `Beside` is that one's id, or null) or `Note` (anything else, such as a to-do line; also when the AI named no kind it knows). |

`PictureDiagram.Title` is the diagram's title as written on it, or null; it is also among `Texts`, as a text of kind `Title`.

`Area` is `DiagramArea(X, Y, Width, Height)` as fractions of the picture's width and height (0 to 1, from the top left), or null when the AI gave no position. A plugin can use it to lay a chart out like the original.

What the app guarantees, because the AI's answer is untrusted:

- Every id a box, container or line names resolves. Ids are unique. Containers nest without loops, at most `DiagramLimits.Depth` (5) deep. A line whose end does not resolve is dropped, as is a container or box that names a container that is not there (its `Container` becomes null).
- At most `DiagramLimits.Boxes` (150) boxes, `Containers` (40), `Lines` (200) and `Texts` (40). Labels and details are cut to 100 characters, texts to 300. What is over a count is dropped.
- An area inside the picture: values outside 0 to 1 are dropped (the area becomes null).
- A diagram whose text holds words addressed to an AI is not passed on, as for any source.
- The AI's reading is not the truth. A small model misreads handwriting; a plugin shows what it draws to the importer, who reviews it.

The app asks in two parts, each kept within the answer limit an Admin set (Max tokens in Administration > AI): the boxes and containers, then the lines and texts between them. Each part gets the whole picture and, when it can be cut, its four quarters enlarged and overlapping a little, so the text of a big diagram is read closer. A picture is cut only when its header says at most 24 million pixels and 12,000 a side, it is at most 4 MB, and it decodes whole; otherwise, or when the image library cannot load on the server, the whole picture goes alone and the import is otherwise the same. When the first part does not fit, it is asked once more without positions and second lines, and the draft says so; an answer cut off at the limit keeps its complete items. When the picture is not a diagram, `Diagram` is null and nothing is said. When it looks like one but the answer cannot be used (the AI failed, timed out or answered nothing readable), `Diagram` is null and the draft's warnings say the diagram could not be read. The draft itself is as it would be without this step.

To place a node right after the picture, use `DraftPlace.AfterPicture` with the picture's `Src` in `DraftAddition.Picture`. On a page that does not show that picture, the node goes at the end. The sample puts an outline of each diagram after its picture:

<!-- sample:diagram -->
```csharp
/// <summary>
/// Reads the diagrams the AI found in an import's pictures (0.10.0) and puts an outline of each right after its picture: one bullet per
/// container with what it holds, then one per line ("A to B: label"). A chart plugin would draw the same data instead.
/// </summary>
public sealed class DiagramOutline : IDraftContributor
{
    public Task<DraftContribution?> ContributeAsync(ImportDraft draft, CancellationToken ct)
    {
        var additions = new List<DraftAddition>();
        foreach (var picture in draft.Pictures)
        {
            if (picture.Diagram is not { } d) continue;
            var names = d.Boxes.ToDictionary(b => b.Id, b => b.Label);
            foreach (var c in d.Containers) names[c.Id] = c.Label;
            var items = new List<string>();
            foreach (var c in d.Containers)
                items.Add($"{c.Label}: {string.Join(", ", d.Boxes.Where(b => b.Container == c.Id).Select(b => b.Label).Concat(d.Containers.Where(x => x.Container == c.Id).Select(x => x.Label)))}");
            foreach (var l in d.Lines)
                items.Add($"{names[l.From]} to {names[l.To]}{(l.Label is { } label ? $": {label}" : "")}{(l.Dashed ? " (dashed)" : "")}");
            if (items.Count == 0) continue;
            var list = JsonSerializer.SerializeToElement(new
            {
                type = "bulletList",
                content = items.Select(t => new { type = "listItem", content = new[] { new { type = "paragraph", content = new[] { new { type = "text", text = t } } } } }),
            });
            additions.Add(new DraftAddition(picture.Page, DraftPlace.AfterPicture, [list]) { Picture = picture.Src });
        }
        return Task.FromResult(additions.Count == 0 ? null : new DraftContribution(additions));
    }
}
```

### How the limits are counted

For both kinds of contributor:

- **Nodes.** At most 50 nodes in one answer: the entries of `Nodes`, or of all the additions' `Nodes` together. A node's own content (a table's rows) is not counted.
- **Size.** At most 512 KB (524,288 bytes): the JSON of each node as the plugin gave it, counted in UTF-8 bytes and added up over the nodes of the answer. Write compact JSON; `JsonSerializer.SerializeToElement` does.
- **Over a limit.** The whole answer is dropped, not cut: nothing of it is added, and it counts as a failure of the plugin.
- **Time.** The limit is 10 seconds for each call. At the limit the core stops waiting, cancels the call's token, and goes on with the import; whatever the call answers later is dropped, and it counts as a failure. The call itself goes on running until it returns, so code that does not look at the token (a long parse) uses the processor until it ends. Check `ct` in long loops.


### Tools for the AI during an import (0.11.0, draft)

**This is a draft of the next contract, on the exploration branch `explore/plugin-tools` only.** It can change or be dropped.

A plugin can offer the AI tools while an import is drafted. The plugin is the toolset; the app is the only path between the model and it. The app drafts the page as before, then, in a conversation of its own, offers the model the tools of the loaded plugins over the drafted page. When the model calls a tool, the app checks the call, passes its input to the plugin, and places the nodes the plugin answers in the draft.

Register a tool with a name, a description and the JSON schema of its input:

```csharp
app.ImportTool<MyTool>(MyTool.Name, MyTool.Description, MyTool.Schema);
```

A tool that is for certain parts of a document, such as a drawing made of text characters, says which parts as data: a `ToolRegions`, line patterns the app checks, caps and runs on the source itself (see "What counts as a region" below). Register it with `ImportRegionTool`:

```csharp
app.ImportRegionTool<BoxesAndLines>(BoxesAndLines.Name, BoxesAndLines.Description, BoxesAndLines.Schema, ToolRegions.TextDrawing);
```

It sets `ImportToolReg.For`. `ToolRegions.TextDrawing` is a ready preset for drawings of boxes and lines; a plugin may pass it, or declare its own patterns. `ImportDrawingTool(...)` is kept as shorthand for the preset, and `ImportToolReg.Drawing` reads true when `For` is set. A region tool differs from other tools in these ways, all decided by the app from the source, never by the model or the plugin:

- **It is offered only for documents that hold one of its regions.** A tool without regions is offered for every document, as before.
- **It gets one result for each region.** A second call that would add something for the same region is refused, so a document with two drawings can get two results, even when both were drafted into one block. Which region a call is about is the one in which most of its text is written. A call whose text is in none of its regions is refused.
- **It is no longer offered once each of its regions has a result.** When no tool is left to offer, the conversation ends.
- **Its text is held to the layout of the region.** A label may be stacked over several lines of one box, and the text one object gives together must be written together. The exact rules are in the guards below.
- **The drafting keeps its regions as written.** See "The drafting keeps the regions" below.

The tool class implements `IImportTool`. It gets the input of one call as a `JsonElement`, already checked against its schema, and answers `ImportToolResult(Nodes)`, or null to add nothing:

<!-- sample:tool -->
```csharp
/// <summary>
/// A tool the AI may call while it drafts an import (0.11.0 draft): a drawing of boxes, groups and lines in a document (a network map
/// drawn in text) becomes an outline after the block that shows it. It is registered as a drawing tool (<c>ImportDrawingTool</c>): offered
/// only for documents that hold a drawing, one result for each drawing. The app checks the input against <see cref="Schema"/> before the
/// call, so the plugin gets only valid input; it never sees the document. Labels must be text of the document; ids, which the AI makes
/// up to tie boxes to groups and lines, are marked <c>"format": "identifier"</c>. A chart plugin would draw the same data instead.
/// </summary>
public sealed class BoxesAndLines : IImportTool
{
    public const string Name = "boxes_and_lines";
    public const string Description = "Draw a diagram of boxes, groups of boxes and lines between them, from a drawing in the documents such as a network map drawn in text.";
    public const string Schema = """
        {"type": "object", "required": ["boxes", "lines"], "additionalProperties": false, "properties": {
          "title": {"type": "string", "maxLength": 100},
          "groups": {"type": "array", "maxItems": 40, "items": {"type": "object", "required": ["id", "label"], "additionalProperties": false, "properties": {
            "id": {"type": "string", "format": "identifier"}, "label": {"type": "string", "maxLength": 100},
            "group": {"type": "string", "format": "identifier", "description": "The id of the group it sits in, if any."}}}},
          "boxes": {"type": "array", "minItems": 1, "maxItems": 150, "items": {"type": "object", "required": ["id", "label"], "additionalProperties": false, "properties": {
            "id": {"type": "string", "format": "identifier"}, "label": {"type": "string", "maxLength": 100},
            "group": {"type": "string", "format": "identifier", "description": "The id of the group it sits in, if any."}}}},
          "lines": {"type": "array", "maxItems": 200, "items": {"type": "object", "required": ["from", "to"], "additionalProperties": false, "properties": {
            "from": {"type": "string", "format": "identifier"}, "to": {"type": "string", "format": "identifier"}, "label": {"type": "string", "maxLength": 100}}}}}}
        """;

    public Task<ImportToolResult?> CallAsync(JsonElement input, CancellationToken ct)
    {
        static string Text(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
        var groups = input.TryGetProperty("groups", out var g) ? g.EnumerateArray().ToList() : [];
        var boxes = input.GetProperty("boxes").EnumerateArray().ToList();
        var names = groups.Concat(boxes).GroupBy(x => Text(x, "id")).ToDictionary(x => x.Key, x => Text(x.First(), "label"));
        object Item(string text, IEnumerable<object> children)
        {
            var list = children.ToList();
            var para = new { type = "paragraph", content = new[] { new { type = "text", text } } };
            return list.Count == 0 ? new { type = "listItem", content = new object[] { para } } : new { type = "listItem", content = new object[] { para, new { type = "bulletList", content = list } } };
        }
        // What sits in a group (or at the top), groups first. A group that names itself, or a loop, is shown once at the top.
        var seen = new HashSet<string>();
        IEnumerable<object> In(string group) =>
            groups.Where(x => Text(x, "group") == group && seen.Add(Text(x, "id"))).Select(x => Item(Text(x, "label"), In(Text(x, "id"))))
                .Concat(boxes.Where(x => Text(x, "group") == group).Select(x => Item(Text(x, "label"), [])));
        var top = In("").ToList();
        var lines = input.GetProperty("lines").EnumerateArray().Select(l => Item(
            $"{names.GetValueOrDefault(Text(l, "from"), Text(l, "from"))} to {names.GetValueOrDefault(Text(l, "to"), Text(l, "to"))}{(Text(l, "label") is { Length: > 0 } x ? ": " + x : "")}", [])).ToList();
        var nodes = new List<JsonElement>();
        if (Text(input, "title") is { Length: > 0 } title)
            nodes.Add(JsonSerializer.SerializeToElement(new { type = "paragraph", content = new[] { new { type = "text", text = title, marks = new[] { new { type = "bold" } } } } }));
        nodes.Add(JsonSerializer.SerializeToElement(new { type = "bulletList", content = top }));
        if (lines.Count > 0) nodes.Add(JsonSerializer.SerializeToElement(new { type = "bulletList", content = lines }));
        return Task.FromResult<ImportToolResult?>(new ImportToolResult(nodes));
    }
}
```

**What the model sees of a plugin.** Only the tool's name, description and input schema, in the request's list of tools. They are fixed for the version the plugin registers them in, and the Admin sees each tool on the plugin's page in Administration > Plugins, with the reason when the app does not offer it. The app offers a tool only when:

- its name is 3 to 40 lower-case letters, digits or underscores, starting with a letter, and no other loaded plugin's tool has it;
- its description is plain text (letters, digits, spaces and ordinary punctuation: no line breaks, markup, braces or quotes) of at most 300 characters;
- its schema is an object of at most 4,000 characters, nests at most 6 deep, and uses only `type` (object, array, string, integer, number, boolean), `properties`, `required`, `items`, `enum`, `minLength`, `maxLength`, `minItems`, `maxItems`, `additionalProperties` (false only), `description` and `format` (only `"identifier"`, on a string; see below). Property names are 1 to 40 letters, digits or underscores; a property's description is plain text of at most 200 characters; a choice of an `enum` is plain text of at most 60 characters, or a number. All this text together is at most 1,500 characters;
- none of that text is addressed to the AI (the check every source of the AI goes through);
- the schema does not use the name `after_block`, which the app keeps for itself.

**What the plugin sees.** Only the input of its own call, checked. Never the document, the draft, the prompt, the model's address or key, or the rest of the conversation. A call runs as the plugin (an actor of kind Plugin, source "import"), in its own scope, within 10 seconds, like a contributor's.

**Where the result goes.** The app adds an input `after_block` to every tool: the number of the draft block, as the model was shown the draft, that holds the content the call is about. The app removes it before the plugin gets the input, and puts the plugin's nodes right after that block. The source stays: a map drawn in text stays its code block, and the drawing goes after it. The importer reviews it with the rest.

**Guards on what the model sends.** After the schema, the app checks each call:

- **No repeats.** In one import item, a tool is not called again with the same input (its keys in any order), nor again after the same block. Only calls that added something count. A refused call may be made again, corrected. The reply is "Refused as a repeat: ...".
- **Only text of the source.** Every string in the input must appear in the item's documents or in its drafted page. The comparison ignores case, and every run of white space counts as one space. Three kinds of string are not checked:
  - a string shorter than 3 characters: it carries no text of its own;
  - a string the schema limits to an `enum`: the choices are the plugin's, fixed and checked when the tool is offered;
  - a string marked `"format": "identifier"`. This is for the ids the model makes up to tie items together, such as a box's id and the ends of a line. An identifier is not compared with the source, but it must be 1 to 32 letters, digits, dots, hyphens or underscores, so it cannot carry a sentence.

  - **Stacked text, for a region tool.** A text also counts as text of the source when it is written in one of the tool's regions over lines that follow each other, such as a name above an address in one box, or a line's label written over two lines. Each part must be on one line. Each part must start within 2 columns of the part above it, and one blank or letterless line may come between parts. Between two words the source may have spaces, brackets (`[storage] [KV]` matches "storage KV") or a slash, comma or semicolon; the model may give the line breaks as spaces, line breaks, " / " or ", " (so "DC01 (phys) / DC02 (vm)" matches the two lines). The words themselves must be as written, in order. Words further apart, side by side, or out of order are not joined.
    - **Inside a box,** the box is the bound: all parts sit between the box's top rule and its bottom rule, over as many lines as the box has. A text never crosses a box's rule. The box is found from the first part: the nearest rule above it that runs over the part, with a side of the box (a bar, a plus or a corner) on the part's line where that rule starts and ends, and the nearest rule below it that runs over the part.
    - **Outside any box,** such as a line's label beside the line, a text may be stacked over 2 to 4 lines, and none of its parts may sit in a box.

  A text that the model reworded, joined from two places, or made up is refused: "Refused: the input holds text that is not in the documents or the draft: "..."". Mark only real ids as identifiers. A string marked as one is still limited to 32 characters of that kind.

- **For a region tool, text given together is written together.** In each object of the input, the first text string in the schema's order is read as the label (such as a box's `label`), and the other text strings of that object as written with it (such as its `detail`), with the label's second and further lines. Choices and identifiers do not count.
  - Where the label is written in a box, each of the others must be written in that same box. A detail taken from beside the box, from another box, or from the prose is refused: "Refused: "..." is not written in the same box as "..." in the documents. Give each item only the text written with it."
  - Where the label is written in a region but in no box, such as a thing in brackets or a bare name, each of the others must be written stacked right under it, as one label would be (within 2 columns, at most 4 lines).
  - A label that is in none of the tool's regions is left to the source check.

  So put the label first in the schema of each object, and give the text a box holds in fields of that object.

**What counts as a region.** `ToolRegions(Starts, Joins, Weak, Alone, MinLines, MinStarts, MaxGap)` describes, as .NET regular expressions over single lines, the regions a tool is for. The app finds them in the documents line by line:

- A region starts at a line that matches a `Starts` (or `Alone`) pattern.
- It goes on over lines that match a `Starts`, `Alone` or `Joins` pattern. At most `MaxGap` other lines may come in a row, such as blank lines or labels.
- It counts when it has at least `MinLines` of those lines and at least `MinStarts` of them match `Starts` but no `Weak` pattern, or when one of its lines matches an `Alone` pattern (a region of one line).
- A Markdown table (rows between bars, with a rule row) is never part of a region, nor is the app's own file header line.

The app checks a declaration before it offers the tool: at most 16 patterns in each list, each at most 300 characters and a pattern the app can run without backtracking (no back-references or look-arounds); `MinLines` 1 to 20, `MinStarts` 0 to 20, `MaxGap` 0 to 5. Each pattern runs with a time limit. A tool whose declaration breaks a rule is not offered, and the Admin sees why.

**The preset `ToolRegions.TextDrawing`** is for drawings made of text characters:
- starts: a rule of three dashes, equals, tildes or underscores (or box-drawing characters); a plus where a rule meets it; or a thing in brackets with spaces inside, such as `[ HV01 ]`;
- joins: a line holding a bar, a plus, a slash or a box-drawing character;
- weak: a rule alone, such as a Markdown section break (`---`);
- alone: two things in brackets with spaces inside joined by a connector of two or more dashes, equals, tildes or dots, with or without arrow heads or a label inside (`[ A ] --- [ B ] --> [ C ]`); or two arrows or more on one line (`printer --587--> FW01 --> Exchange Online`);
- at least 3 lines, 2 of them starts, at most 2 other lines in a row.

With it, not drawings: one arrow in a sentence (`printer -> relay01`), a task list (`- [ ] check`), links (`[the guide](https://...)`), one thing in brackets in a sentence, Mermaid or other diagram code. Known misses: one arrow between two bare names (`A ---> B`). A line of bars alone (`| HV01 |`) joins a drawing but does not start one, so a table set with bars and no rule row, as an HTML table reads as text, is no drawing.

**The drafting keeps the regions.** Where the documents hold a region of a loaded tool, the drafting gets those regions as data under "keep", and its task says to copy each into one code block, exactly as written, where it stands. Documents without one are drafted exactly as before, word for word.

**What the model gets back.** Never the plugin's text. Each call gets one reply the app writes: "Done: added after block 2.", or "Refused:" and the app's reason (no tool of that name, not a JSON object, over 16 KB, no such block, which part of the schema the input breaks, a repeat, text that is not in the source, text not written together, a region that already has its result, an input about none of the tool's regions, or that the tool added nothing). A reply may quote, cut short, the model's own string that was not found; never anything of the plugin's.

**Refusing with a reason.** A tool that cannot make anything of an input answers `ImportToolResult.Refuse(why, at)` instead of null. `why` is one of the app's reasons, `ImportToolRefusal`:

| Reason | The app's reply to the model |
|---|---|
| `UnknownId` | Refused by the tool: `<at>` names an id that the input does not declare. Declare it, or leave it out. |
| `DuplicateId` | Refused by the tool: `<at>` declares an id that is declared before. Give each its own id. |
| `TooLittle` | Refused by the tool: the input holds too little to make anything of. |
| `TooMuch` | Refused by the tool: the input holds more than the tool can make. |

`at` is optional: a JSON path into the call's input, such as `$.boxes[3].group` (a dollar sign, then names after dots and numbers in brackets, at most 12 steps). The app names it only when it is of that form and names a part of the input; when it names a string of the identifier form, the reply quotes it, being the model's own text. Otherwise the reply says "the input". Nothing else of the plugin reaches the model. A null answer, or nodes that fail the checks, get "Refused: the tool added nothing."

**Limits, for each import item.** At most 8 tool calls that reach a plugin, and at most 6 calls the app refuses before that (the schema, the source, the layout, repeats), each counted apart, so a large drawing is not starved by retries; then the conversation ends, and the draft says so. It also ends when the model calls no tool, or when no tool is left to offer. One call's input is at most 16 KB. The plugin's answer is checked as a contribution's (block nodes, at most 50 nodes and 512 KB). Each call is in the audit log as `import.tool`: the plugin, the tool, the importer, the size of the input, the outcome and the number of nodes. Each model call is in it as an AI call, as the drafting is.

**When there are no tools.** With no plugin that offers a tool, or none the app accepts, no tool conversation is held, and the import is as it was. When the model server cannot call tools (it answers an error), the draft is as it would be without them, and its warnings say so. The tools are not offered for an item that holds a secret or that goes to a sensitive space, as contributors are not called for it.

### What a plugin sees of an import

The import actions (`import.*`) are core-only: a plugin cannot hook them, send them or read their data.
A plugin reads an import's files only through an [import contributor](#adding-to-the-draft-import-contributors) (including the [kinds it claims](#file-kinds-the-app-does-not-read)), and its draft only through a [draft contributor](#adding-to-the-draft-from-its-text-draft-contributors). It cannot read the text the app read from a file. Then it sees what the importer accepts, when the import sends the normal actions as the importer, and so runs the hooks on those:

| The importer accepts | Actions sent, in this order |
|---|---|
| A draft as new page(s) | `page.create` with `Kind` `"Folder"`, when the importer typed a new folder's name. Then, for each picture the page shows, `image.add` into the target space. Then `page.create` with `Kind` `"Page"`, for each page. |
| A draft into an existing page (`AcceptRequest.updatePageId`) | `image.add` for each picture, then `page.save` on that page: a major version (`Major` true), `BaseVersion` the page's current version, `Note` `"Imported from <file names>"`. |
| Either, with details | `page.details.save` (core-only) for each page. |
| Either, with "attach the originals" | `attachment.add` on the first page, once for each original file. |

Every one of these actions has `ctx.Actor.Source` `"import"` and `ctx.Actor.ImportItemId` the import item's id ([below](#where-the-call-comes-from)).

- `ContentJson` is the draft as the importer accepted it ([page content](#page-content)); its pictures already point to the space's images (`/api/v1/images/<id>`). A before hook on `page.create` or `page.save` may change it, as for any caller.
- The uploaded file reaches a plugin's hooks only when the importer chooses to attach the originals: a before hook on `attachment.add` then gets it in `ctx.Action.File` and can read it (`OpenReadAsync`). That happens after the pages are made.
- Once an item is accepted or discarded, the app deletes its stored files and the text read from them.
- A hook tells an import's `page.create` or `page.save` from a person's by `ctx.Actor.Source`: [below](#where-the-call-comes-from).

## Who the caller is

Every action carries an actor (`ctx.Actor`). It is one of:

- **A person** in the browser: signed in with a password or a [sign-in method](#sign-in-methods), then a code when the app asks for one (below). `UserId` is set.
- **A person through an API token**: `UserId` is the token's owner, `TokenId` is set. A token narrows what its owner may do: each scope is Read (Reader) or Write (Contributor) for one space or all, never more than its owner holds. A token is never an Admin, even an Admin's. A service token's owner is a service account that cannot sign in; its all-spaces scope does not reach a sensitive space. Tokens reach only the routes the [API reference](../api-reference.md) marks; plugin routes are closed to them.
- **A plugin** (`ActorKind.Plugin`), in a job, an after handler, a health check or an import contributor. It reads every space that is not sensitive, Private ones too, and nothing of a sensitive space. An action that records who did it (`page.create`, `page.save` and the like) answers 401 for it.
- **Nobody** (`ActorKind.Anonymous`): someone not signed in. They read only spaces open to Anyone that are not sensitive.

`IAccess` answers for all of them; ask it rather than checking the kind yourself.

### Where the call comes from

`ctx.Actor.Source` says how the action was started. The values, also constants of `ActorSources`:

| `Source` | `Kind` | When |
|---|---|---|
| `web` | `User` or `Anonymous` | Every action started by a request: the browser, an API token, a plugin's route, and every action the core sends while it handles a request, except those of an accepted import. |
| `job` | `Plugin` | A plugin's job (`IKbJob`), and every action it sends. |
| `queue` | `Plugin` | A plugin's after handler, and every action it sends. |
| `health` | `Plugin` | A plugin's health check, and every action it sends. |
| `import` | `User` | The actions sent when the importer accepts a draft ([which](#what-a-plugin-sees-of-an-import)), as the importer. |
| `import` | `Plugin` | An [import contributor](#adding-to-the-draft-import-contributors) or a [draft contributor](#adding-to-the-draft-from-its-text-draft-contributors), and every action it sends. |
| `signin` | `Plugin` | A [sign-in method](#sign-in-methods)'s provider description and check, and every action it sends. |

`ctx.Actor.PluginId` names the plugin for `job`, `queue`, `health`, `signin` and a contributor's `import`. `ctx.Actor.ImportItemId` is the import item's id when `Source` is `import`, and null otherwise. `ctx.Name` is the action's own name (`page.create`), never the action that sent it.

Only the core sets `Source` and `ImportItemId`. `Actor` has no public constructor, and nothing a caller sends (a field in a request's body, a header) changes them, so a hook can rely on them: a page whose `page.create` has source `import` was made by accepting that import item.
Before 0.4.0 an accepted import's actions had source `web`, and a hook could not tell them from a person's.

### Spaces open to more people

A space is Private (its members), Signed in (every signed-in person reads it) or Anyone (read without signing in, as Reader). A sensitive space is always Private.
`IAccess.RoleAsync` and `VisibleSpacesAsync` already count this: a signed-in person has the Reader role in a Signed in space they are not a member of.
A page's history is for members only: someone who reads a Signed in or Anyone space without a role there (directly, through a group, or as an Admin) gets `403` from `page.version.list`, `page.version.read` and `page.diff`, and a `PageDto` with an empty `History`, an empty `UpdatedBy` and `CanSeeHistory` false. A plugin's background work is Owner of every space that is not sensitive, so it keeps history. A plugin route that should be read by people who are not signed in says `AllowAnonymous()`, and its handler still asks `IAccess`.

### The audit log

Every action's audit entries say who did it. A call made with an API token adds `token` (its name) and `tokenId` to the entry's data; Administration > Audit log shows it in the Via column (API or Web). Each call made with a token also writes a `token.use` entry with the method and the route.
What a plugin writes through `ctx.Audit(...)` gets the same.

### Sign-in codes

An Admin can make people use a code from an authenticator app at sign-in (Administration > Security > Sign-in). The password is checked first, then the code (`auth.signin`, then `auth.signin.code`).
A before hook on `auth.signin` runs before the code is asked. The code, the key behind it and the recovery codes are never in an action's JSON or text. Code actions are core-only.

## Sign-in methods

A plugin can add a button to the sign-in screen that signs a person in through an OpenID Connect provider. The app runs the whole sign-in; the plugin says where the provider is, and may refuse a person on what the provider says about them. The app is only ever the relying party: it never issues identities to anyone.

<!-- sample:signin -->
```csharp
/// <summary>
/// A sign-in method: "Sign in with Example ID" on the sign-in screen. The app runs the whole OpenID Connect sign-in; this class says
/// where the provider is, and refuses people outside the domain an Admin set. Its hosts are in the manifest's outboundHosts.
/// </summary>
public sealed class SampleSignIn(SampleSettings settings) : ISignInMethod
{
    public OidcProvider Provider { get; } = new() { Issuer = "https://id.example.com" };

    public Task<string?> CheckAsync(SignInClaims claims, CancellationToken ct) =>
        Task.FromResult(settings.SignInDomain is { } domain && !(claims.EmailVerified && claims.Email!.EndsWith("@" + domain, StringComparison.OrdinalIgnoreCase))
            ? $"Sign in with your account at {domain}."
            : null);
}
```

Register it in `Configure`: `app.SignInMethod<SampleSignIn>("example", "Sign in with Example ID")`. The id is 1 to 32 lowercase letters, digits and hyphens, unique within the plugin; the name is the button's text, 1 to 60 characters. Anything else throws, and the plugin does not load. The method is made for each sign-in, with the plugin's services, and runs as the plugin (`ActorKind.Plugin`, source `signin`).

What the app does, for every method alike (OpenID Connect Core 1.0, authorization code flow):

- The button goes to `/api/v1/auth/methods/{plugin}/{id}/start`. The app reads the provider's discovery document, then sends the browser to the provider with `state`, `nonce` and a PKCE challenge (S256). These, the code verifier, the return path and the redirect address are kept in one encrypted cookie that lasts ten minutes and is read once.
- The provider sends the browser back to `/api/v1/auth/methods/{plugin}/{id}/callback`, the redirect address. The app checks the state, trades the code for the ID token at the token endpoint (with the client secret in HTTP Basic, or as a public client without one), and checks the ID token: its signature against the provider's published keys (RSA or EC only; an unsigned token or a shared-secret one is refused), its issuer, its audience (and its authorized party when it has several), its expiry, its not-before time when it has one, its issue time and its nonce. The provider's clock may be up to two minutes from the app's: an expiry that passed, a not-before time or an issue time more than two minutes ahead is refused. An old issue time is not refused on its own: the expiry bounds how long the token is good, and the nonce ties it to this one sign-in.
- After the sign-in, the browser goes to the path it came from. Only a path on this site is kept; an address elsewhere (`//host`, `https://...`) becomes `/`.
- Discovery, keys and the token request use the plugin's HTTP client, so every host they reach (the issuer's, the token endpoint's, the keys') must be in the manifest's `outboundHosts`. The sample's provider is at `id.example.com`.

What the plugin gives, in `Provider` (`OidcProvider`), asked at each sign-in so it may depend on the plugin's settings:

| Property | Default | |
|---|---|---|
| `Issuer` | (required) | Exactly as the provider's ID tokens give it: an https address with no query, at most 400 characters. |
| `DiscoveryAddress` | the issuer + `/.well-known/openid-configuration` | An https address. Its `issuer` must equal `Issuer`. |
| `Scopes` | `openid`, `email`, `profile` | Must hold `openid`. |
| `SubjectClaim` | `sub` | The claim that names the person for good. The account is linked by issuer plus this value. |
| `EmailClaim`, `EmailVerifiedClaim`, `NameClaim` | `email`, `email_verified`, `name` | Read from the ID token, and from the userinfo answer when `UserInfo` is on. |
| `UserInfo` | `false` | 0.9.0. Read the userinfo endpoint too ([below](#sign-in-providers-an-admin-adds)). |

A provider description the app cannot use (an issuer that is not https, scopes without `openid`) refuses the sign-in and counts as a failure of the plugin.

`CheckAsync` runs on the validated claims (`SignInClaims`: issuer, subject, email, whether the provider verified it, name, and every claim of the token) before the app looks for an account. Return null to let the person on, or a sentence that refuses them; the sign-in screen shows it once (at most 200 characters). A check that throws or runs past 10 seconds refuses too, with a general message. A domain allow-list, a tenant or a group claim belong here.

What the plugin cannot do: it never sees the authorization code, the ID token or any other token, it never opens a session, and it cannot act as the person. It cannot read the method's client id or client secret either: `GetSetting` and `GetSecretAsync` answer null for every key starting with `signIn.`, and a manifest that declares such a key is refused at install.

The app adds three settings to the plugin's page for each method: **Client ID**, **Client secret** (stored encrypted, never sent back) and **New accounts** (off unless an Admin turns it on). The page shows the redirect address to register with the provider. The button shows on the sign-in screen once the client id is set; the anonymous list behind it (`GET /api/v1/auth/methods`) holds each method's id and name, nothing else. No plugin script runs on the sign-in screen.

Who gets in:

1. An account already linked to this issuer and subject. A turned-off account is refused.
2. Otherwise, when the provider verified the email address, the one account with that address (never the break-glass account or a service account) is linked, and from then on found by the link alone. Linking removes its password and ends every password link made for it: a linked account is entered through its method only (password sign-in and Basic with a password answer as for a wrong password, and no password link can be made for it). Two accounts with the address, or an account already linked to another sign-in, are refused. An unverified address is refused.
3. Otherwise, when **New accounts** is on, a new account: never an Administrator, in no group and no space. Its user name is the email address (or `user-` and 8 hex digits when that is taken), and it has no password.

An Administrator who signs in with a method still gives their one-time code, as after a password. Password sign-in and the break-glass account are unchanged: a method cannot turn them off, and Safe Mode loads no plugin, so the break-glass account always gets in.

The audit log has `auth.method.start`, then `auth.signin` or `auth.signin.failed` (with the method and the reason, never a code or a token), and `user.signin.link` or `user.signin.create` when an account is linked or made.

## Sign-in providers an Admin adds

From 0.9.0 a plugin can leave the provider to the Admin. It says it offers providers, and its page under Administration > Plugins gets a **Sign-in providers** list with **Add new**. Each provider has a title (the text of its button), a gear that opens its settings, and its own redirect address. A plugin that does this needs no code for the provider at all: one generic OpenID Connect plugin covers a lab's test provider, a company's tenant at a cloud identity service, or any other, each as one more entry in the list.

<!-- sample:providers -->
```csharp
/// <summary>
/// The check on the providers an Admin adds on this plugin's page (0.9.0). The app reads each provider from its settings and checks its
/// required claim; this runs after, on every provider of the plugin, and is told which one. The sample refuses an account its provider
/// marks as a test account.
/// </summary>
public sealed class SampleProviderCheck : ISignInProviderCheck
{
    public Task<string?> CheckAsync(string providerId, SignInClaims claims, CancellationToken ct) =>
        Task.FromResult(claims.All.TryGetValue("test_account", out var v) && v.ValueKind == System.Text.Json.JsonValueKind.True
            ? "Test accounts cannot sign in here."
            : null);
}
```

Register in `Configure`: `app.SignInProviders()`, or `app.SignInProviders<TCheck>()` with a check of the plugin's own (`ISignInProviderCheck`: like `ISignInMethod.CheckAsync`, told which provider it is). Both may be used next to `app.SignInMethod<T>(id, name)`, which is unchanged.

What each provider is to the app: a sign-in method like any other. It has its own button, its own start and callback addresses (`/api/v1/auth/methods/{plugin}/{provider id}/start` and `/callback`), and its own sign-ins. Accounts are linked by issuer and subject, as before. The provider id is made by the app when the Admin adds it (a `p` and 10 hex digits). It is not the title, so renaming a provider keeps its redirect address. Adding, renaming and removing a provider apply at once, without a restart and without a new package, and are audited as `plugin.signin.provider.add`, `plugin.signin.provider.rename` and `plugin.signin.provider.remove`.

What the Admin sets for each provider, all read at the time of each sign-in:

| Setting | Default | |
|---|---|---|
| Issuer | (required for its button) | Exactly as the provider's ID tokens give it; an https address. |
| Discovery address | the issuer + `/.well-known/openid-configuration` | An https address. Its `issuer` must equal the issuer. |
| Client ID, Client secret | | From the provider. The secret is stored encrypted and never sent back. |
| Scopes | `openid email profile` | Separated by spaces; must hold `openid`. |
| Subject, Email, Email verified, Name claims | `sub`, `email`, `email_verified`, `name` | As `OidcProvider`'s properties. |
| Read userinfo | on | The app reads the provider's userinfo endpoint after the token request (below). |
| Required claim, Allowed values | none | The claim must equal one of the values, one per line; for a list claim, one of its items must. A domain (`hd`) or a tenant (`tid`) goes here. |
| New accounts | off | As for any method. |
| Provider on a private network | off | Below. |

The button shows once the issuer and the client ID are set. The plugin cannot read any of these settings: they are under `signIn.`, and the list itself is kept under `signIn.providers`.

To set a provider up from a script, these are the keys:

| Key | Type | Default | |
|---|---|---|---|
| `signIn.<providerId>.issuer` | text | none | The issuer, an https address. With the client ID, it puts the button on the sign-in screen. |
| `signIn.<providerId>.discovery` | text | the issuer + `/.well-known/openid-configuration` | An https address. |
| `signIn.<providerId>.clientId` | text | none | |
| `signIn.<providerId>.clientSecret` | secret | none | Write-only: reading the settings says only whether it is set. |
| `signIn.<providerId>.scopes` | text | `openid email profile` | Separated by spaces; must hold `openid`. |
| `signIn.<providerId>.subjectClaim` | text | `sub` | |
| `signIn.<providerId>.emailClaim` | text | `email` | |
| `signIn.<providerId>.emailVerifiedClaim` | text | `email_verified` | |
| `signIn.<providerId>.nameClaim` | text | `name` | |
| `signIn.<providerId>.userInfo` | toggle (`true`, `false`) | `true` | |
| `signIn.<providerId>.requiredClaim` | text | none | Empty: no claim is required. |
| `signIn.<providerId>.requiredValues` | multiline | none | One value per line (`\n` between them in JSON). |
| `signIn.<providerId>.newAccounts` | toggle | `false` | |
| `signIn.<providerId>.privateNetwork` | toggle | `false` | |

`<providerId>` is the `id` that adding the provider answered (`POST /api/v1/admin/plugins/{id}/signin-providers` with `{ "title": "..." }`; `PUT .../signin-providers/{providerId}` renames it, `DELETE` removes it). The values are saved with the plugin's other settings, by an Admin: `PUT /api/v1/admin/plugins/{id}/settings` with `{ "values": { "signIn.<providerId>.issuer": "https://...", ... } }`. A key left out is kept; an empty value clears it. A save applies at once, without a restart.

**Which hosts the app calls for a provider an Admin added.** Only these: the discovery address the Admin entered (or the issuer's), and the token endpoint, keys and userinfo endpoint named by the discovery document read from it. The manifest's `outboundHosts` do not apply. Every one of these must be https, and no redirect is followed. By default the app connects to them only at public internet addresses, the same guard the app uses for any address someone typed: a mistyped or hostile address cannot aim the server at its own network. **Provider on a private network** lifts the address check for that one provider, and only an Admin can turn it on. It exists for a provider on your own network, such as a test provider in a lab on an internal Docker network. Https stays required, and the provider's certificate must be one the server trusts: for a lab provider with its own certificate authority, add that authority to the trust store of the server (or its container). There is no switch that skips certificate checks.

**Userinfo.** `OidcProvider.UserInfo` (new in 0.9.0, off by default, so a 0.8.0 method behaves as before) and the **Read userinfo** setting (on by default) make the app call the userinfo endpoint with the access token when the discovery document names one. Its `sub` must equal the ID token's, or the sign-in is refused. Its claims fill those the ID token lacks; a claim the ID token has is kept as the ID token gives it. Only a JSON answer is read; a signed userinfo answer is refused. A method in code that sets `UserInfo` must list the userinfo endpoint's host in `outboundHosts`.

**Removing a provider.** Its button and its settings, secret included, are deleted. Accounts linked through it keep their link (issuer and subject). They have no password, so they cannot sign in until a provider with the same issuer is set up again, which finds them by the same link. The confirmation tells the Admin how many accounts are linked to the provider's issuer, and the audit entry records the number. To give such a person another way in, an Admin deletes the account and makes it again.

Everything else is as for any method: state, nonce, PKCE S256, the ID token's signature, issuer, audience, authorized party, expiry, issue time and nonce, the encrypted state cookie read once, the return path on this site only, the linking rules, and a linked account having no password.

## Library plugins

A plugin can serve other plugins.

- It lists the assemblies it shares in `exports`, by assembly name without `.dll`. A plugin that names it in `requires` sees those assemblies, and no other plugin's.
- It defines an extension point as a record. A dependent adds one with `app.Add(new TheRecord(...))`. The library reads them from `IRegistrations.All<TheRecord>()`.
  `IPluginApp.Add<T>(T registration)` and `All<T>()` require `T : class`: make the extension point a `record` or a `class`, not a `record struct`. A stand-in `IPluginApp` in a test must declare the same constraint.
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
- A hook, a handler or a job on an action that is not open to plugins, or a second handler for an action.
- Five failures in ten minutes: a hook, a filter, an after handler, a job, an import or draft contributor, an audit sink or an email sender that threw, or whose own time limit ran out. A job or a contributor that runs past its limit counts too, and so does a contributor's answer the core cannot use.

The plugin stays disabled until an Admin selects Enable. Uploading a new version does not enable it. Plugins that require it are disabled with it; an enforcing one among them is locked.

## Versions

- `version`: three numbers. An update must be higher than the installed version.
- `abstractionsVersion`: the version of `Kb.Plugins.Abstractions` the plugin was built against. The app refuses a plugin built for a newer one.
- `minKbVersion`: the lowest app version the plugin runs on.

The contract and the app are at 0.11.0, and can still change before 1.0.0. On the branch `explore/plugin-tools`, 0.11.0 is a draft.
0.11.0 (draft) adds [tools for the AI during an import](#tools-for-the-ai-during-an-import-0110-draft) (`app.ImportTool<T>(name, description, schema)`, `app.ImportRegionTool<T>(name, description, schema, regions)` with `ToolRegions` and its preset `ToolRegions.TextDrawing`, the shorthand `app.ImportDrawingTool<T>(name, description, schema)`, `IImportTool`, `ImportToolResult` with `ImportToolResult.Refuse(ImportToolRefusal, at)`, `ImportToolReg` with `For` and `Drawing`); a plugin that uses them says `"abstractionsVersion": "0.11.0"`. Nothing of 0.10.0 changed, and a plugin built for 0.10.0 or earlier loads as it did.
0.10.0 added [diagrams in the pictures of an import](#diagrams-in-the-pictures-of-an-import) (`ImportDraft.Pictures`, `DraftPicture`, `PictureDiagram`, `DiagramBox`, `DiagramContainer`, `DiagramLine`, `DiagramText`, `DiagramArea`, `DiagramShape`, `DiagramArrow`, `DiagramLimits`, `DraftPlace.AfterPicture`, `DraftAddition.Picture`); a plugin that uses them says `"abstractionsVersion": "0.10.0"`. Nothing of 0.9.0 changed, and a plugin built for 0.9.0 or earlier loads as it did.
0.9.0 added [sign-in providers an Admin adds](#sign-in-providers-an-admin-adds) (`app.SignInProviders()`, `app.SignInProviders<TCheck>()`, `ISignInProviderCheck`, `SignInProvidersReg`) and `OidcProvider.UserInfo`; a plugin that uses them says `"abstractionsVersion": "0.9.0"`. Nothing of 0.8.0 changed, and a plugin built for 0.8.0 or earlier loads as it did.
0.8.0 added [sign-in methods](#sign-in-methods) (`ISignInMethod`, `OidcProvider`, `SignInClaims`, `SignInMethodReg`, `app.SignInMethod<T>(id, name)`) and the actor source `signin`; a plugin that uses them says `"abstractionsVersion": "0.8.0"`. One thing changed that an earlier plugin can see: a setting key starting with `signIn.` is the app's, so a manifest that declares one is refused at install. Nothing else of 0.7.0 changed, and a plugin built for 0.7.0 or earlier loads as it did.
0.7.0 added a setting's `help`, `default`, `unit`, `min`, `max` and `optionLabels` ([settings](#settings-and-secrets)), and `GetSetting` returns the default while nothing is saved; a plugin that uses them says `"abstractionsVersion": "0.7.0"`. Its browser script API is 1.1.0: the slot `plugin.settings` ([panels](#panels-on-the-plugins-own-page)) and `kb.api.upload` ([uploading](#uploading-a-file-from-a-panel)); a script that uses them says `"uiApiVersion": "1.1.0"`. Nothing of 0.6.0 changed: a plugin built for 0.6.0 or earlier loads as it did, and its settings read and save as they did.
0.6.0 added [file kinds a plugin claims](#file-kinds-the-app-does-not-read) (`app.ImportFileKinds(...)`, `ImportFileKindReg`); a plugin that uses them says `"abstractionsVersion": "0.6.0"`. Nothing of 0.5.0 changed, and a plugin built for 0.5.0 or earlier loads as it did.
0.5.0 added [draft contributors](#adding-to-the-draft-from-its-text-draft-contributors) (`IDraftContributor`, `ImportDraft`, `DraftPage`, `DraftContribution`, `DraftAddition`, `DraftPlace`, `app.DraftContributor<T>()`); a plugin that uses them says `"abstractionsVersion": "0.5.0"`. It changed one thing a 0.4.0 contributor can see: the 512 KB limit is now counted in UTF-8 bytes, where 0.4.0 counted characters, so an answer of mostly non-ASCII text holds less. Everything else of 0.4.0 is unchanged, and a plugin built for 0.4.0 or earlier loads as it did.
0.4.0 added [import contributors](#adding-to-the-draft-import-contributors) (`IImportContributor`, `ImportedFile`, `ImportContribution`, `app.ImportContributor<T>()`), `DocPicture` for [exports](#editor-blocks), `UploadContent.FromBytes` and `FromStream` ([adding a file](#adding-an-image-or-an-attachment)), and `ActorSources` with `Actor.ImportItemId` ([where the call comes from](#where-the-call-comes-from)); a plugin that uses them says `"abstractionsVersion": "0.4.0"`. It changed one thing a 0.3.0 plugin can see: an accepted import's actions have source `import`, not `web`. Everything else of 0.3.0 is unchanged, and a plugin built for 0.3.0 or earlier loads as it did.
0.3.0 added `IOutboundRequestDecorator`, the inventory sync action and its records, and page details on `PageDto`; a plugin that uses them says `"abstractionsVersion": "0.3.0"`. See [packaging](packaging.md#versions).

Editor blocks (`EditorNode`, `NodeRenderer` and the `Doc*` blocks) and the browser script (`ui`, `uiApiVersion` 1.0.0) were added while the contract was still numbered 0.2.0, so 0.2.0 does not say whether an app has them. A plugin that uses them says `"abstractionsVersion": "0.3.0"`: every app at 0.3.0 has them.

## Testing a plugin

A plugin's own tests run without the app, as an ordinary test project that references the plugin and the contract. The [outbound-headers example](example-outbound-headers.md) has one.

What a test can build and call directly:

- Extension points (`IMalwareScanner`, `IOutboundRequestDecorator`, `IEmailSender`, `IAuditSink`, ...): plain interfaces.
- After handlers: `AfterAsync(action, result, e, ct)` takes the action record, its result and an `ActionEvent`, which all have public constructors.
- Jobs (`RunAsync(ct)`), with a stand-in `IActionBus` that answers the actions the job sends. `Outcome<T>` converts from a value or a `Problem`.
- Renderers: call `Renderer.Plain`, `MarkdownLines` and `Blocks` with a node parsed by `JsonDocument.Parse(...).RootElement`.
- Import contributors: call `ContributeAsync(new ImportedFile(...), ct)`; `ImportedFile` has a public constructor. The repository's `tests/Kb.Tests.Unit/SampleImportTests.cs` tests the sample's.
- `Configure`, with a stand-in `IPluginApp` whose `Add<T>(T registration) where T : class` keeps what it is given; check the `BeforeHook`, `AfterHandler`, `ActionHandler`, `NodeRenderer`, `ImportContributorReg` and `SignInMethodReg` records.
- A sign-in method's `CheckAsync`: `SignInClaims` has a public constructor. The whole sign-in runs in the app; the repository's `tests/Kb.Tests.Integration/Plugins/SignInMethodTests.cs` runs the sample's against a stand-in provider.

What a test cannot build: `ActionContext<T>` and `Actor` have no public constructor. Only the app makes them, so a plugin cannot act as someone else. A before hook, a result filter or an action handler therefore cannot be called from a plugin's test as it is.
Keep such a class thin: put the decision in a method that takes the action (and what it needs) and returns the changed action or the `Problem`, test that method, and leave the hook to call it and set `ctx.Action`.

The whole plugin, hooks included, is tested in the app: install the package on a test instance, or run the repository's package test ([packaging](packaging.md#check-a-package-with-the-apps-tests)), which installs it in a throwaway database and checks that it loads.

## Planned, not built

- More slots in the app's screens (today: `page.tools`, `page.panel`, `editor.toolbar`, `header.gear`), the screen kit (tables, forms), wrapping or replacing parts of the app's screens.
- A build preset for scripts written in TypeScript or JSX; scripts on the sign-in screen; calling other hosts from the browser.
- For sign-in methods: an issuer that differs per tenant (multi-tenant issuers), signing out at the provider, an Admin unlinking an account, a signed userinfo answer, client authentication other than a client secret in HTTP Basic, and setup help per provider.
- Data attached to core rows (a count that comes with the page).
- Page kinds of a plugin's own, export formats.
- The extension points the contract declares and the core does not call yet: maps, an AI provider, record importers, page actions, export formats, DNS, a search engine.
- Scanning attachments and images added to pages directly.
- After handlers that survive a crash (a queue in the database).
- A plugin table save inside the core action's transaction.
