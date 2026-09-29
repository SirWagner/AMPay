using System.Data;
using System.Data.Common;
using System.Text;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging;

namespace AMPay.Infrastructure.Data;

/// <summary>
/// Brings the database to the current schema on startup without tripping over a database
/// that is already there.
/// <para>
/// A bare <c>MigrateAsync()</c> assumes the migration history table is the truth. Two
/// situations break that assumption in practice, and both have happened on this project:
/// </para>
/// <list type="number">
/// <item><b>LocalDB forgets the database but keeps its files.</b> The instance restarts or
/// is recreated, <c>AMPay.mdf</c> stays on disk, and EF - seeing no database - issues
/// <c>CREATE DATABASE</c>, which SQL Server refuses because the file already exists. Handled
/// by re-attaching the existing files, which also keeps every record in them.</item>
/// <item><b>The tables exist but the history does not say so.</b> The schema was created
/// some other way, or the history rows were lost. EF tries to run a migration whose tables
/// are already there and fails with "There is already an object named ...". Handled by
/// recording those migrations as applied instead of running them.</item>
/// </list>
/// <para>
/// Anything that cannot be positively confirmed as already present is migrated normally.
/// </para>
/// </summary>
public static class DatabaseInitializer
{
    /// <summary>
    /// For each migration that creates tables, one table that exists only once it has run.
    /// <para>
    /// This is how "already applied" is proven without trusting the history table. A
    /// migration missing from this map is never skipped - it simply runs - so forgetting to
    /// add an entry is safe. Add one for each future migration that creates a table.
    /// Data-only migrations such as ClientLifecycleStatuses are deliberately absent: they
    /// are written to be safe to run twice.
    /// </para>
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> SchemaMarkers =
        new Dictionary<string, string>
        {
            ["20260914194411_InitialSchema"] = "Tenants",
            ["20260916194822_CreditOrigination"] = "CreditPackages"
        };

    public static async Task InitialiseAsync(
        AppDbContext db, ILogger log, CancellationToken ct = default)
    {
        try
        {
            await AttachOrphanedLocalDbFilesAsync(db, log, ct);
        }
        catch (Exception ex) when (ex is DbException or InvalidOperationException or TimeoutException)
        {
            // The attach step is a recovery aid for one specific LocalDB failure. It must
            // never be the thing that stops the application starting: if master cannot be
            // reached - LocalDB still spinning up, an instance restart in progress - carry
            // on. The steps below connect to the database itself, and if it is genuinely
            // unreachable EF reports that with its own, clearer error.
            log.LogWarning(ex,
                "Could not check LocalDB for an orphaned database file: {Message} " +
                "Continuing with the normal startup.", ex.Message);
        }

        await BaselineExistingSchemaAsync(db, log, ct);

        var pending = (await db.Database.GetPendingMigrationsAsync(ct)).ToList();

        if (pending.Count == 0)
        {
            log.LogInformation("Database schema is up to date; no migrations to apply.");
            return;
        }

        log.LogInformation(
            "Applying {Count} migration(s): {Migrations}.", pending.Count, string.Join(", ", pending));

        await db.Database.MigrateAsync(ct);
    }

    // ------------------------------------------------------------------ orphaned files

    /// <summary>
    /// Re-attaches a LocalDB database whose files exist but which the instance no longer
    /// knows about.
    /// <para>
    /// LocalDB only. On a real SQL Server the web server cannot see the database server's
    /// disk, and attaching data files is a decision for whoever runs that server - not
    /// something an application should do to itself on startup.
    /// </para>
    /// </summary>
    private static async Task AttachOrphanedLocalDbFilesAsync(
        AppDbContext db, ILogger log, CancellationToken ct)
    {
        var connectionString = db.Database.GetConnectionString();
        if (string.IsNullOrWhiteSpace(connectionString)) return;

        var builder = new SqlConnectionStringBuilder(connectionString);

        if (!builder.DataSource.StartsWith("(localdb)", StringComparison.OrdinalIgnoreCase))
            return;

        var databaseName = builder.InitialCatalog;
        if (string.IsNullOrWhiteSpace(databaseName)) return;

        // The database is not attached, so connect to master to ask about it.
        builder.InitialCatalog = "master";

        // LocalDB shuts itself down when idle and starts again on the next connection. A cold
        // start can outlast SqlClient's default 15 seconds, so allow it longer here.
        builder.ConnectTimeout = Math.Max(builder.ConnectTimeout, 60);

        await using var master = new SqlConnection(builder.ConnectionString);
        await master.OpenAsync(ct);

        var existing = await ScalarAsync(master, "SELECT DB_ID(@name)", ct, ("@name", databaseName));
        if (existing is not null and not DBNull) return;

        var dataPath = await ScalarAsync(master,
            "SELECT CAST(SERVERPROPERTY('InstanceDefaultDataPath') AS nvarchar(4000))", ct) as string;
        var logPath = await ScalarAsync(master,
            "SELECT CAST(SERVERPROPERTY('InstanceDefaultLogPath') AS nvarchar(4000))", ct) as string;

        if (string.IsNullOrWhiteSpace(dataPath)) return;

        var mdf = Path.Combine(dataPath, databaseName + ".mdf");
        var ldf = Path.Combine(string.IsNullOrWhiteSpace(logPath) ? dataPath : logPath,
            databaseName + "_log.ldf");

        // No files either: a genuinely new database. MigrateAsync will create it.
        if (!File.Exists(mdf)) return;

        log.LogWarning(
            "Database {Database} is not registered with LocalDB, but its data file exists at {Path}. " +
            "Re-attaching the existing file instead of creating a new database.",
            databaseName, mdf);

        // CREATE DATABASE does not accept parameters for FILENAME, so the values are quoted
        // as literals. Both come from the connection string and the server itself, not from
        // a request, but they are escaped regardless.
        var sql = File.Exists(ldf)
            ? $"CREATE DATABASE {QuoteName(databaseName)} ON " +
              $"(FILENAME = {QuoteLiteral(mdf)}), (FILENAME = {QuoteLiteral(ldf)}) FOR ATTACH;"
            : $"CREATE DATABASE {QuoteName(databaseName)} ON " +
              $"(FILENAME = {QuoteLiteral(mdf)}) FOR ATTACH_REBUILD_LOG;";

        await using (var command = master.CreateCommand())
        {
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync(ct);
        }

        // Connections pooled before the attach may remember the database as missing.
        SqlConnection.ClearAllPools();

        log.LogInformation("Re-attached {Database} from {Path}.", databaseName, mdf);
    }

    // ------------------------------------------------------------------ existing schema

    /// <summary>
    /// Records as applied any migration whose tables are already present but which the
    /// history table does not list.
    /// <para>
    /// Walks the migrations in order and stops at the first one it cannot prove is
    /// present. Skipping a later migration while an earlier one is still outstanding would
    /// leave the schema half-built, so from that point on everything runs normally.
    /// </para>
    /// </summary>
    private static async Task BaselineExistingSchemaAsync(
        AppDbContext db, ILogger log, CancellationToken ct)
    {
        // No database yet: nothing can already exist.
        if (!await db.Database.CanConnectAsync(ct)) return;

        var applied = (await db.Database.GetAppliedMigrationsAsync(ct)).ToHashSet();
        var toRecord = new List<string>();

        var connection = db.Database.GetDbConnection();
        var opened = connection.State != ConnectionState.Open;
        if (opened) await connection.OpenAsync(ct);

        try
        {
            foreach (var migration in db.Database.GetMigrations())
            {
                if (applied.Contains(migration)) continue;

                if (!SchemaMarkers.TryGetValue(migration, out var markerTable)) break;
                if (!await TableExistsAsync(connection, markerTable, ct)) break;

                toRecord.Add(migration);
            }
        }
        finally
        {
            if (opened) await connection.CloseAsync();
        }

        if (toRecord.Count == 0) return;

        log.LogWarning(
            "Tables for {Count} migration(s) already exist but are not in the migration history: " +
            "{Migrations}. Recording them as applied rather than running them again.",
            toRecord.Count, string.Join(", ", toRecord));

        var history = db.GetService<IHistoryRepository>();
        var version = ProductInfo.GetVersion();

        var script = new StringBuilder()
            .AppendLine(history.GetCreateIfNotExistsScript());

        foreach (var migration in toRecord)
            script.AppendLine(history.GetInsertScript(new HistoryRow(migration, version)));

        await ExecuteAsync(db, script.ToString(), ct);
    }

    // ------------------------------------------------------------------ helpers

    private static async Task<bool> TableExistsAsync(
        DbConnection connection, string table, CancellationToken ct)
    {
        var id = await ScalarAsync(connection,
            "SELECT OBJECT_ID(@name, N'U')", ct, ("@name", $"dbo.{table}"));

        return id is not null and not DBNull;
    }

    private static async Task<object?> ScalarAsync(
        DbConnection connection, string sql, CancellationToken ct,
        params (string Name, object Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        foreach (var (name, value) in parameters)
        {
            var p = command.CreateParameter();
            p.ParameterName = name;
            p.Value = value;
            command.Parameters.Add(p);
        }

        return await command.ExecuteScalarAsync(ct);
    }

    /// <summary>
    /// Runs a generated script through a plain command. Deliberately not ExecuteSqlRaw,
    /// which treats braces in the text as parameter placeholders.
    /// </summary>
    private static async Task ExecuteAsync(AppDbContext db, string sql, CancellationToken ct)
    {
        var connection = db.Database.GetDbConnection();
        var opened = connection.State != ConnectionState.Open;
        if (opened) await connection.OpenAsync(ct);

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            if (opened) await connection.CloseAsync();
        }
    }

    private static string QuoteName(string name) => "[" + name.Replace("]", "]]") + "]";

    private static string QuoteLiteral(string value) => "N'" + value.Replace("'", "''") + "'";
}
