//      .dbf .dbf .dbf                 ProjectThunder :: Thunder.Dbf.Sql
//        \   |   /                    One shot: every table in a folder, copied into a
//         \  |  /                     SQL database with proper types, in transactions.
//        [  SQL  ]                    The legacy files are only read, never changed.

using System.Data.Common;
using System.Diagnostics;

namespace Thunder.Dbf.Sql;

/// <summary>What to migrate, and how.</summary>
public sealed record DbfMigrationOptions
{
    /// <summary>Only these tables (by name, any case). Null means every table in the folder.</summary>
    public IReadOnlyCollection<string>? Tables { get; init; }

    /// <summary>Copy records marked deleted too (with _deleted set). Off by default.</summary>
    public bool IncludeDeleted { get; init; }

    /// <summary>Drop and recreate SQL tables that already exist. Off by default: existing tables are an error.</summary>
    public bool ReplaceExisting { get; init; }

    /// <summary>Rows per transaction. Large batches are faster; small ones hold locks for less time.</summary>
    public int BatchSize { get; init; } = 5_000;

    /// <summary>Prefix for SQL table names, for example "legacy_".</summary>
    public string TablePrefix { get; init; } = "";
}

/// <summary>What a migration did, table by table.</summary>
public sealed record DbfMigrationResult(IReadOnlyList<DbfTableMigration> Tables, TimeSpan Elapsed)
{
    /// <summary>Rows copied across every table.</summary>
    public int Rows => Tables.Sum(t => t.Rows);
}

/// <summary>What a migration did to one table.</summary>
/// <param name="Table">The DBF table's name.</param>
/// <param name="SqlTable">The SQL table it was copied to.</param>
/// <param name="Rows">Rows copied.</param>
/// <param name="SkippedDeleted">Records marked deleted in the DBF, and so not copied.</param>
public sealed record DbfTableMigration(string Table, string SqlTable, int Rows, int SkippedDeleted);

/// <summary>Copies DBF tables into a SQL database through any ADO.NET provider.</summary>
public static class DbfMigrator
{
    /// <summary>
    /// Copies the tables of <paramref name="database"/> into <paramref name="connection"/> (which must be
    /// open). Each SQL table gets the DBF fields as columns plus _id, _recno and _deleted, so it can later
    /// be kept in sync with <see cref="DbfBridge"/>.
    /// </summary>
    public static DbfMigrationResult Migrate(DbfDatabase database, DbConnection connection, SqlDialect dialect, DbfMigrationOptions? options = null)
    {
        options ??= new DbfMigrationOptions();
        ArgumentOutOfRangeException.ThrowIfLessThan(options.BatchSize, 1);
        var timer = Stopwatch.StartNew();

        var names = options.Tables is null
            ? database.TableNames
            : database.TableNames.Where(n => options.Tables.Contains(n, StringComparer.OrdinalIgnoreCase)).ToList();
        var missing = options.Tables?.Where(t => !names.Contains(t, StringComparer.OrdinalIgnoreCase)).ToList();
        if (missing is { Count: > 0 })
            throw new DbfException(DbfErrorCode.TableNotFound, $"There's no table called '{missing[0]}' to migrate.",
                "Check DbfMigrationOptions.Tables against DbfDatabase.TableNames.");

        var results = names.Select(name => MigrateTable(database, name, connection, dialect, options)).ToList();
        return new DbfMigrationResult(results, timer.Elapsed);
    }

    private static DbfTableMigration MigrateTable(DbfDatabase database, string name, DbConnection connection, SqlDialect dialect, DbfMigrationOptions options)
    {
        using var table = database.OpenTable(name);
        var sql = new SqlTable(connection, dialect, options.TablePrefix + name, table.Fields);

        using (var transaction = connection.BeginTransaction())
        {
            if (sql.Exists(transaction))
            {
                if (!options.ReplaceExisting)
                    throw new DbfException(DbfErrorCode.TableAlreadyExists, $"The SQL table '{sql.Name}' already exists.",
                        "Drop it first, pick a TablePrefix, or set ReplaceExisting to recreate it.");
                sql.Drop(transaction);
            }
            sql.Create(transaction);
            transaction.Commit();
        }

        var rows = 0;
        var skipped = 0;
        DbTransaction? batch = null;
        try
        {
            foreach (var record in table.ReadAll())
            {
                if (record.IsDeleted && !options.IncludeDeleted)
                {
                    skipped++;
                    continue;
                }

                batch ??= connection.BeginTransaction();
                sql.Insert(record.RecordNumber, record.IsDeleted, sql.ValuesOf(record, table), batch);
                if (++rows % options.BatchSize == 0)
                {
                    batch.Commit();
                    batch.Dispose();
                    batch = null;
                }
            }
            batch?.Commit();
        }
        finally
        {
            batch?.Dispose();
        }

        return new DbfTableMigration(name, sql.Name, rows, skipped);
    }
}
