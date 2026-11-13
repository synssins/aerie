using System.Data.Common;
using Kb.Core.Config;
using Kb.Plugins;
using Kb.Plugins.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;

namespace Kb.Data.Plugins;

/// <summary>
/// The host's side of plugin tables: gives a plugin's context the app's database, makes its tables ready at start,
/// and drops them when an Admin deletes the plugin's data. Every table of a plugin starts with its prefix, and no prefix is the start of another.
/// </summary>
public static class PluginDataHost
{
    public static PluginDbOptions Options(string pluginId, KbOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var history = PluginTables.Prefix(pluginId) + "migrations";
        var builder = new DbContextOptionsBuilder();
        _ = options.Database.Provider switch
        {
            DatabaseProvider.Sqlite => builder.UseSqlite(options.Database.ConnectionString, s => s.MigrationsHistoryTable(history)),
            _ => builder.UseSqlServer(options.Database.ConnectionString, s => s.EnableRetryOnFailure().MigrationsHistoryTable(history)),
        };
        return new PluginDbOptions(pluginId, builder.Options);
    }

    public static PluginDbContext Create(Type context, PluginDbOptions options)
    {
        ArgumentNullException.ThrowIfNull(context);
        try { return (PluginDbContext)Activator.CreateInstance(context, options)!; }
        catch (MissingMethodException) { throw new InvalidOperationException($"{context.Name} needs a constructor that takes PluginDbOptions."); }
    }

    /// <summary>
    /// Makes the plugin's tables ready. A plugin with migrations is migrated (its history is in <c>&lt;prefix&gt;migrations</c>).
    /// One without has its tables created the first time; if they later differ from its model it needs migrations, and is refused.
    /// </summary>
    public static async Task ReadyAsync(Type context, PluginDbOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);
        await using var db = Create(context, options);
        if (db.Database.GetMigrations().Any())
        {
            await db.Database.MigrateAsync(ct);
            return;
        }
        var wanted = db.Model.GetEntityTypes().Select(e => e.GetTableName()).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var existing = await TablesAsync(db, options.TablePrefix, ct);
        var present = wanted.Count(t => existing.Contains(t, StringComparer.OrdinalIgnoreCase));
        if (present == wanted.Count) return;
        if (present > 0) throw new InvalidOperationException("Its tables changed. A plugin that changes its tables needs migrations.");
        await db.GetService<IRelationalDatabaseCreator>().CreateTablesAsync(ct);
    }

    /// <summary>The tables whose name starts with the prefix.</summary>
    public static async Task<List<string>> TablesAsync(DbContext db, string prefix, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(prefix);
        var sql = db.Database.IsSqlite()
            ? "SELECT name FROM sqlite_master WHERE type = 'table' AND substr(name, 1, @length) = @prefix"
            : "SELECT TABLE_NAME FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_TYPE = 'BASE TABLE' AND LEFT(TABLE_NAME, @length) = @prefix";
        return await ReadAsync(db, sql, ct, ("@length", prefix.Length), ("@prefix", prefix));
    }

    /// <summary>Drops every table of the plugin, its migration history included. Nothing of another plugin or of the core can match its prefix.</summary>
    public static async Task DropAsync(DbContext db, string pluginId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        var tables = await TablesAsync(db, PluginTables.Prefix(pluginId), ct);
        if (tables.Count == 0) return;
        var quote = db.GetService<ISqlGenerationHelper>();
        var commands = new List<string>();
        if (db.Database.IsSqlite())
        {
            // Foreign keys between the plugin's own tables would refuse the drops in the wrong order.
            commands.Add("PRAGMA foreign_keys = OFF");
            commands.AddRange(tables.Select(t => $"DROP TABLE {quote.DelimitIdentifier(t)}"));
            commands.Add("PRAGMA foreign_keys = ON");
        }
        else
        {
            foreach (var table in tables)
            {
                var keys = await ReadAsync(db, "SELECT name FROM sys.foreign_keys WHERE parent_object_id = OBJECT_ID(@table)", ct, ("@table", table));
                commands.AddRange(keys.Select(k => $"ALTER TABLE {quote.DelimitIdentifier(table)} DROP CONSTRAINT {quote.DelimitIdentifier(k)}"));
            }
            commands.AddRange(tables.Select(t => $"DROP TABLE {quote.DelimitIdentifier(t)}"));
        }
        foreach (var command in commands) await RunAsync(db, command, ct);
    }

    private static async Task<List<string>> ReadAsync(DbContext db, string sql, CancellationToken ct, params (string Name, object Value)[] parameters)
    {
        var connection = db.Database.GetDbConnection();
        var opened = connection.State != System.Data.ConnectionState.Open;
        if (opened) await connection.OpenAsync(ct);
        try
        {
            await using var command = Command(connection, sql, parameters);
            var rows = new List<string>();
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) rows.Add(reader.GetString(0));
            return rows;
        }
        finally { if (opened) await connection.CloseAsync(); }
    }

    // Table and key names cannot be parameters. They come from the database's own catalog and are delimited by the provider.
    private static async Task RunAsync(DbContext db, string sql, CancellationToken ct)
    {
        var connection = db.Database.GetDbConnection();
        var opened = connection.State != System.Data.ConnectionState.Open;
        if (opened) await connection.OpenAsync(ct);
        try
        {
            await using var command = Command(connection, sql);
            await command.ExecuteNonQueryAsync(ct);
        }
        finally { if (opened) await connection.CloseAsync(); }
    }

    private static DbCommand Command(DbConnection connection, string sql, params (string Name, object Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            var parameter = command.CreateParameter();
            (parameter.ParameterName, parameter.Value) = (name, value);
            command.Parameters.Add(parameter);
        }
        return command;
    }
}
