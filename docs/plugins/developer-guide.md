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
    .Before<SavePage, MajorNeedsNote>()                        // refuse a core action
    .After<SavePage, PageDto, ClearOnMajor>()                  // react once a core action has succeeded
    .Job<PurgeJob>("purge")                                    // the schedule "purge" of the manifest
    .HealthCheck<TableCheck>("table")
    .EditorNode(Notice.Renderer)                               // how the server reads its editor block
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

/// <summary>Who acknowledged the page. For people who hold the plugin's permission "see-acknowledgements" in the page's space.</summary>
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

/** Called once, after sign-in, with the host: everything the script may use of the app. */
export default function (kb) {
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
- `kb.slots.add(slot, item)` adds to a named place of a screen. Today's slots: `page.tools` (buttons in the page's tool row: `id`, `label`, `onClick(ctx)`), `page.panel` (parts of the page's right panel: `id`, `title`, `render(ctx)`), both with the context `{ page }`, `editor.toolbar` (buttons of the editor's toolbar, with the context `{ editor }`), and `header.gear` (entries at the end of the gear menu in the header: `id`, `label`, `onClick()`; the way to a screen of the plugin's own). `order` places an item; `when(ctx)` shows it only when it answers true.
- `kb.api` calls the plugin's own routes (`/api/v1/plugins/<id>/...`); `kb.core` calls the app's API. The server answers what the signed-in person may see.
- `kb.me()` is the person signed in now. The script is started once per page load, so call `kb.me()` when you need it and do not keep its answer: another person may sign in without a reload.
- `kb.current.page()` and `kb.current.space()` are what the person is looking at; `kb.current.subscribe(fn)` tells when that changes. Either can be null.
- `kb.navigate(path)` goes to an address inside the app. Someone in the middle of an edit is asked first, as for a click on a link, and may stay.
- `kb.screens.add({ path, title, render(rest), panel })` adds a screen of the plugin's own at `/x/<plugin id>/<path>`, inside the app's three panels. `panel` names a slot that fills the right panel. Lead to it from the gear menu: `kb.slots.add("header.gear", { id: "reports", label: "Reports", onClick: () => kb.navigate("/x/<plugin id>/reports") })`.
- `kb.dialogs.confirm(...)` and `kb.dialogs.ask(...)` are the app's own dialogs. Never use a browser popup (`alert`, `confirm`, `prompt`).
- What a plugin draws sits inside an error boundary. A part that throws is left out and written to the server's log; the screen stays. Script errors are never counted as failures of the plugin.
- A script runs in every signed-in person's browser with that person's session. It can do in the browser whatever that person can.

The rules of the app's screens hold for a plugin's parts: no help text and no placeholder text; the right panel holds facts about the thing, not controls for it beyond what the app puts there; use the app's class names (`tb` for a button, `muted`, `small`).

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

## Page content

A page's content is the `ContentJson` of `page.create`, `page.save` and `PageDto`: a string holding the editor's document as JSON, in the editor's (TipTap 3, ProseMirror) document format.
A plugin that reads or changes a page works on that JSON.

- The top is one node of type `doc`. `page.create` and `page.save` refuse anything else ("Not an editor document."), and a string over 5,000,000 characters.
- A node is an object: `type` (its name), `attrs` (its attributes, optional), `content` (its child nodes, optional). A text node is `{ "type": "text", "text": "...", "marks": [...] }`; a mark is `{ "type": "bold" }` or, with attributes, `{ "type": "link", "attrs": { "href": "..." } }`.
- The server stores the string as it was sent and never rewrites it. A node of a type it does not know is kept, and is read as a plugin's node ([below](#editor-blocks)).
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

- An AI import drafts its pages with the same types: `heading`, `paragraph`, `bulletList`, `orderedList` (numbered steps), `listItem`, `codeBlock`, `blockquote` (notes and warnings), `table` with `tableHeader` and `tableCell`, `image`, `hardBreak`, and the marks `bold`, `italic` and `code`. What is stored is the content the importer reviewed and accepted.
- A page is stored as its writer sent it, by the editor or by anyone who calls the API. Read it with care: a node may lack `content`, hold `null`, or be of a type you do not expect. The core's own readers walk into the `content` of any node they do not know, so its text is never lost.
- There is no chart or diagram block in the core, and no format for one. A plugin that wants charts or diagrams brings its own: an [editor block](#editor-blocks) of its own type, the library that draws it in the browser (in its `ui/` folder), and a renderer for the server.

To make a page, send `page.create` with `Kind` set to `"Page"` or `"Folder"`, written exactly so: the names are case-sensitive, and any other value is refused ("Folder or Page."). A folder holds no content: its `ContentJson` is ignored. `ParentId` must be a folder of the same space, or null for the top of the space.

### Adding an image or an attachment

`image.add` and `attachment.add` take the file as an `UploadContent`. It is an abstract class: a plugin derives its own and passes it.

```csharp
public sealed class BytesFile(string name, byte[] data) : UploadContent
{
    public override string? FileName => name;
    public override long? Length => data.Length;
    // Every call starts at the first byte.
    public override Task<Stream> OpenReadAsync(CancellationToken ct) => Task.FromResult<Stream>(new MemoryStream(data, writable: false));
}

var image = await bus.SendAsync(new AddImage { SpaceId = spaceId, File = new BytesFile("chart.png", png) }, ct);
// image.Value.Url is "/api/v1/images/<id>": the src of an image node of a page in that space.
```

- Both actions record who added the file, so they need a person: they answer 401 to a plugin's own work (a job, an after handler, a health check). Send them where the actor is a person (a before hook, a handler, a route), with that person's rights: they need the right to create and edit pages in the space.
- `image.add` takes PNG, JPEG, GIF or WebP, 20 MB at most. An SVG is not an image: attach it, or draw it in the browser.
- `attachment.add` takes any file up to the size an Admin set (Administration > Uploads).
- A before hook on `page.create` that adds an image runs before the page is made. If the page is then refused, the image is stored and no page shows it.

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
- A script can load a stylesheet from its own `ui/` folder, as above. Use the app's colour variables (`--ink`, `--line`, `--warn-bg`, ...) so the block follows the theme.
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
- A `DocTable` a renderer answers is part of the Word and PDF export, but it is not one of the page's tables in the Export menu (CSV, Excel). A `DocImage` is drawn only for an image the page itself shows in an image of the editor.
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
- A script outside the app sends the same rows to `POST /api/v1/inventory/sync` with an API token: the token needs the Write scope, and its owner (or the token itself) must be allowed to edit the inventory. Administration > Security > Inventory access lists who may: everyone, a person, a group, the people with any role in a space, the people with a given role, or one named API token. A token is never an Admin, so an Admin's token needs a rule too. See `tools/Seed-Inventory.ps1`.
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

### What a plugin sees of an import

The import actions (`import.*`) are core-only: a plugin cannot hook them, send them or read their data.
**A plugin cannot read the uploaded file, the text the app read from it, or the AI's draft.** It sees only what the importer accepts, when the import sends the normal actions as the importer, and so runs the hooks on those:

| The importer accepts | Actions sent, in this order |
|---|---|
| A draft as new page(s) | `page.create` with `Kind` `"Folder"`, when the importer typed a new folder's name. Then, for each picture the page shows, `image.add` into the target space. Then `page.create` with `Kind` `"Page"`, for each page. |
| A draft into an existing page (`AcceptRequest.updatePageId`) | `image.add` for each picture, then `page.save` on that page: a major version (`Major` true), `BaseVersion` the page's current version, `Note` `"Imported from <file names>"`. |
| Either, with details | `page.details.save` (core-only) for each page. |
| Either, with "attach the originals" | `attachment.add` on the first page, once for each original file. |

- `ContentJson` is the draft as the importer accepted it ([page content](#page-content)); its pictures already point to the space's images (`/api/v1/images/<id>`). A before hook on `page.create` or `page.save` may change it, as for any caller.
- The uploaded file reaches a plugin only when the importer chooses to attach the originals: a before hook on `attachment.add` then gets it in `ctx.Action.File` and can read it (`OpenReadAsync`). That happens after the pages are made.
- Once an item is accepted or discarded, the app deletes its stored files and the text read from them.
- A hook cannot tell an import's `page.create` or `page.save` from a person's: [below](#where-the-call-comes-from).

## Who the caller is

Every action carries an actor (`ctx.Actor`). It is one of:

- **A person** in the browser: signed in with a password, then a code when the app asks for one (below). `UserId` is set.
- **A person through an API token**: `UserId` is the token's owner, `TokenId` is set. A token narrows what its owner may do: each scope is Read (Reader) or Write (Contributor) for one space or all, never more than its owner holds. A token is never an Admin, even an Admin's. A service token's owner is a service account that cannot sign in; its all-spaces scope does not reach a sensitive space. Tokens reach only the routes the [API reference](../api-reference.md) marks; plugin routes are closed to them.
- **A plugin** (`ActorKind.Plugin`), in a job or an after handler. It reads every space that is not sensitive, Private ones too, and nothing of a sensitive space. An action that records who did it (`page.create`, `page.save` and the like) answers 401 for it.
- **Nobody** (`ActorKind.Anonymous`): someone not signed in. They read only spaces open to Anyone that are not sensitive.

`IAccess` answers for all of them; ask it rather than checking the kind yourself.

### Where the call comes from

`ctx.Actor.Source` says how the action was started. The values:

| `Source` | `Kind` | When |
|---|---|---|
| `web` | `User` or `Anonymous` | Every action started by a request: the browser, an API token, a plugin's route, and every action the core sends while it handles a request (an import's `page.create`, for example). |
| `job` | `Plugin` | A plugin's job (`IKbJob`), and every action it sends. |
| `queue` | `Plugin` | A plugin's after handler, and every action it sends. |
| `health` | `Plugin` | A plugin's health check, and every action it sends. |

`ctx.Actor.PluginId` names the plugin for `job`, `queue` and `health`. `ctx.Name` is the action's own name (`page.create`), never the action that sent it.

**A hook cannot tell a page an import makes from a page a person makes.** The import sends `page.create` and `page.save` as the importer, with source `web`, inside the importer's request, exactly as the editor does.
Content does not tell them apart either: the app's editor stores nothing for a new page until its first save, which is a `page.create` with everything the person wrote.
The only mark an import leaves on the action is the `Note` of an import into an existing page (`"Imported from ..."`), and a person can type the same note.

### Spaces open to more people

A space is Private (its members), Signed in (every signed-in person reads it) or Anyone (read without signing in, as Reader). A sensitive space is always Private.
`IAccess.RoleAsync` and `VisibleSpacesAsync` already count this: a signed-in person has the Reader role in a Signed in space they are not a member of. A plugin route that should be read by people who are not signed in says `AllowAnonymous()`, and its handler still asks `IAccess`.

### The audit log

Every action's audit entries say who did it. A call made with an API token adds `token` (its name) and `tokenId` to the entry's data; Administration > Audit log shows it in the Via column (API or Web). Each call made with a token also writes a `token.use` entry with the method and the route.
What a plugin writes through `ctx.Audit(...)` gets the same.

### Sign-in codes

An Admin can make people use a code from an authenticator app at sign-in (Administration > Security > Sign-in). The password is checked first, then the code (`auth.signin`, then `auth.signin.code`).
A before hook on `auth.signin` runs before the code is asked. The code, the key behind it and the recovery codes are never in an action's JSON or text. Code actions are core-only.

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
- Five failures in ten minutes: a hook, a filter, an after handler, a job, an audit sink or an email sender that threw, or whose own time limit ran out. A job that runs past its limit counts too.

The plugin stays disabled until an Admin selects Enable. Uploading a new version does not enable it. Plugins that require it are disabled with it; an enforcing one among them is locked.

## Versions

- `version`: three numbers. An update must be higher than the installed version.
- `abstractionsVersion`: the version of `Kb.Plugins.Abstractions` the plugin was built against. The app refuses a plugin built for a newer one.
- `minKbVersion`: the lowest app version the plugin runs on.

The contract and the app are at 0.3.0, and can still change before 1.0.0. 0.3.0 added `IOutboundRequestDecorator`, the inventory sync action and its records, and page details on `PageDto`; a plugin that uses them says `"abstractionsVersion": "0.3.0"`. See [packaging](packaging.md#versions).

Editor blocks (`EditorNode`, `NodeRenderer` and the `Doc*` blocks) and the browser script (`ui`, `uiApiVersion` 1.0.0) were added while the contract was still numbered 0.2.0, so 0.2.0 does not say whether an app has them. A plugin that uses them says `"abstractionsVersion": "0.3.0"`: every app at 0.3.0 has them.

## Testing a plugin

A plugin's own tests run without the app, as an ordinary test project that references the plugin and the contract. The [outbound-headers example](example-outbound-headers.md) has one.

What a test can build and call directly:

- Extension points (`IMalwareScanner`, `IOutboundRequestDecorator`, `IEmailSender`, `IAuditSink`, ...): plain interfaces.
- After handlers: `AfterAsync(action, result, e, ct)` takes the action record, its result and an `ActionEvent`, which all have public constructors.
- Jobs (`RunAsync(ct)`), with a stand-in `IActionBus` that answers the actions the job sends. `Outcome<T>` converts from a value or a `Problem`.
- Renderers: call `Renderer.Plain`, `MarkdownLines` and `Blocks` with a node parsed by `JsonDocument.Parse(...).RootElement`.
- `Configure`, with a stand-in `IPluginApp` whose `Add<T>(T registration) where T : class` keeps what it is given; check the `BeforeHook`, `AfterHandler`, `ActionHandler` and `NodeRenderer` records.

What a test cannot build: `ActionContext<T>` and `Actor` have no public constructor. Only the app makes them, so a plugin cannot act as someone else. A before hook, a result filter or an action handler therefore cannot be called from a plugin's test as it is.
Keep such a class thin: put the decision in a method that takes the action (and what it needs) and returns the changed action or the `Problem`, test that method, and leave the hook to call it and set `ctx.Action`.

The whole plugin, hooks included, is tested in the app: install the package on a test instance, or run the repository's package test ([packaging](packaging.md#check-a-package-with-the-apps-tests)), which installs it in a throwaway database and checks that it loads.

## Planned, not built

- More slots in the app's screens (today: `page.tools`, `page.panel`, `editor.toolbar`, `header.gear`), the screen kit (tables, forms), wrapping or replacing parts of the app's screens.
- A build preset for scripts written in TypeScript or JSX; scripts on the sign-in screen; calling other hosts from the browser.
- Data attached to core rows (a count that comes with the page).
- Sign-in schemes, page kinds of a plugin's own, export formats.
- The extension points the contract declares and the core does not call yet: maps, an AI provider, record importers, page actions, export formats, DNS, a search engine.
- Scanning attachments and images added to pages directly.
- After handlers that survive a crash (a queue in the database).
- A plugin table save inside the core action's transaction.
