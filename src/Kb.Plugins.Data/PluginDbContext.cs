using Microsoft.EntityFrameworkCore;

namespace Kb.Plugins.Data;

/// <summary>What the host gives a plugin's context: the app's database, and the prefix every table of this plugin carries.</summary>
public sealed class PluginDbOptions
{
    internal PluginDbOptions(string pluginId, DbContextOptions options)
    {
        (PluginId, TablePrefix, Options) = (pluginId, PluginTables.Prefix(pluginId), options);
    }

    public string PluginId { get; }
    public string TablePrefix { get; }
    public DbContextOptions Options { get; }

    /// <summary>For a plugin author's design-time factory, so <c>dotnet ef migrations add</c> names the tables as the app will.</summary>
    public static PluginDbOptions ForDesign(string pluginId, Action<DbContextOptionsBuilder> provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        var builder = new DbContextOptionsBuilder();
        provider(builder);
        return new(pluginId, builder.Options);
    }
}

/// <summary>
/// A plugin's own tables. They live in the app's database under the plugin's prefix (<c>plg_&lt;id&gt;__</c>), are created or migrated at start,
/// and are kept when the plugin is removed unless the Admin asks to delete its data. A table may hold the id of a core row as a value;
/// it cannot have a foreign key to a core table, because the core's tables are not in this model.
/// shortcut: a save here is on its own, not part of the action that caused it; join the action's transaction when a plugin needs both to commit together.
/// </summary>
public abstract class PluginDbContext : DbContext
{
    private readonly string _prefix;

    protected PluginDbContext(PluginDbOptions options) : base((options ?? throw new ArgumentNullException(nameof(options))).Options) => _prefix = options.TablePrefix;

    /// <summary>The plugin's model. Name tables plainly ("notes"); the prefix is added for you.</summary>
    protected abstract void Build(ModelBuilder model);

    protected sealed override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        Build(modelBuilder);
        foreach (var entity in modelBuilder.Model.GetEntityTypes())
        {
            if (entity.GetTableName() is { } table && !table.StartsWith(_prefix, StringComparison.Ordinal)) entity.SetTableName(_prefix + table);
            // The prefix is the only namespace a plugin's tables have: the host finds and drops them by name, in the default schema.
            entity.SetSchema(null);
        }
    }
}

/// <summary>The registration: this plugin has tables, described by this context.</summary>
public sealed record PluginData(Type Context);

public static class PluginDataExtensions
{
    /// <summary>Gives the plugin tables. The context is then available from the container, one per request.</summary>
    public static IPluginApp AddData<TContext>(this IPluginApp app) where TContext : PluginDbContext
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.Add(new PluginData(typeof(TContext)));
    }
}
