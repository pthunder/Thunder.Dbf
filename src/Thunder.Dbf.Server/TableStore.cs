//      +-------+  +-------+  +-------+
//      |CLIENT |  |PEOPLE |  | CAL   |      ProjectThunder :: Thunder.Dbf
//      | [##]  |  | [  ]  |  | [  ]  |      One turnstile per table: requests on the same
//      +---|---+  +---|---+  +---|---+      table take turns, different tables run side by side.
//          |          |          |
//        open       open       open         Tables are opened per request and closed straight
//        work       work       work         after, so the legacy app is never locked out for
//        close      close      close        longer than one request takes.

using System.Collections.Concurrent;
using Microsoft.Extensions.Options;

namespace Thunder.Dbf.Server;

/// <summary>
/// The data folder, as the service sees it. <see cref="DbfTable"/> isn't thread-safe, so every request
/// opens its own instance under the table's lock and closes it before the lock is released.
/// </summary>
public sealed class TableStore : IDisposable
{
    private static readonly string[] BackedUpExtensions = [".dbf", ".dbt", ".fpt", ".mdx", ".cdx"];

    private readonly ThunderOptions _options;
    private readonly ILogger<TableStore> _logger;
    private readonly DbfDatabase _reader;
    private readonly Lazy<DbfDatabase> _writer;
    private readonly Lazy<DbfDatabase> _unmaintainedWriter;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, bool> _backedUp = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _backupFolderLock = new();
    private string? _runBackupFolder;

    public TableStore(IOptions<ThunderOptions> options, IHostEnvironment environment, ILogger<TableStore> logger)
    {
        _options = options.Value;
        _logger = logger;

        var data = _options.Data;
        var databaseOptions = new DbfDatabaseOptions
        {
            ReadOnly = true,
            IgnoreMissingMemo = data.IgnoreMissingMemo,
            Encoding = data.CodePage is { } codePage ? DbfEncoding.GetEncoding(codePage) : null,
        };
        _reader = DbfDatabase.Open(data.Folder, databaseOptions);
        _writer = new(() => DbfDatabase.Open(_reader.Folder, databaseOptions with { ReadOnly = false }));
        _unmaintainedWriter = new(() => DbfDatabase.Open(_reader.Folder, databaseOptions with { ReadOnly = false, AllowUnmaintainedIndexes = true }));

        BackupFolder = Path.GetFullPath(_options.Backups.Folder, environment.ContentRootPath);
    }

    /// <summary>The data folder's full path. For logs only; it never goes into a response.</summary>
    public string Folder => _reader.Folder;

    public string BackupFolder { get; }

    /// <summary>
    /// The table's real name if <paramref name="requested"/> is exactly one of the folder's tables.
    /// Matching against the folder listing means a path, "..", or an encoded separator can never match.
    /// </summary>
    public string? Find(string requested)
    {
        var bare = requested.EndsWith(".dbf", StringComparison.OrdinalIgnoreCase) ? requested[..^4] : requested;
        return _reader.TableNames.FirstOrDefault(t => string.Equals(t, bare, StringComparison.OrdinalIgnoreCase));
    }

    public IReadOnlyList<string> TableNames => _reader.TableNames;

    /// <summary>Opens the table read-only, runs <paramref name="work"/>, and closes it, holding the table's lock throughout.</summary>
    public async Task<T> ReadAsync<T>(string table, Func<DbfTable, T> work, CancellationToken cancellationToken)
    {
        var gate = await EnterAsync(table, cancellationToken);
        try
        {
            using var opened = _reader.OpenTable(table);
            return work(opened);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Opens the table for writing, backs it up if this is its first write since startup, runs
    /// <paramref name="work"/>, and closes it. Callers check permissions first.
    /// </summary>
    public async Task<T> WriteAsync<T>(string table, Func<DbfTable, T> work, CancellationToken cancellationToken)
    {
        if (!_options.TableAcceptsWrites(table))
            throw new InvalidOperationException($"Table {table} doesn't accept writes; the caller should have checked.");

        var gate = await EnterAsync(table, cancellationToken);
        try
        {
            var allowStaleIndexes = _options.AllowsUnmaintainedIndexes(table);
            var database = allowStaleIndexes ? _unmaintainedWriter.Value : _writer.Value;
            // Opening first means the library has already checked that the table and its companions
            // live in the folder (no links pointing elsewhere) before anything is copied.
            using var opened = database.OpenTable(table);

            // DbfTable would refuse this write anyway; refusing here skips a pointless backup. A
            // production .mdx next to the table is kept up to date, so only a missing .mdx or a
            // FoxPro .cdx (read-only for now) stands in the way.
            var staleIndex = opened.Header.HasStructuralCdx || (opened.Header.HasProductionMdx && !opened.MaintainsIndex);
            if (staleIndex && !allowStaleIndexes)
                throw new DbfException(DbfErrorCode.IndexWouldGoStale,
                    "The table has an index that can't be kept up to date (a FoxPro .cdx, or a production .mdx that's missing), so writing would leave it out of date.",
                    $"The server's administrator can allow it with Thunder:Tables:{table}:AllowUnmaintainedIndexes, if the legacy app rebuilds the index (REINDEX) before relying on it.");

            if (!_backedUp.ContainsKey(table))
            {
                BackUp(opened, table);
                _backedUp[table] = true;
            }
            return work(opened);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<SemaphoreSlim> EnterAsync(string table, CancellationToken cancellationToken)
    {
        var gate = _locks.GetOrAdd(table, _ => new SemaphoreSlim(1, 1));
        if (!await gate.WaitAsync(TimeSpan.FromSeconds(_options.LockTimeoutSeconds), cancellationToken))
            throw new TableBusyProblem();
        return gate;
    }

    // Copies the table and its memo and index files. The table lock (the one dBASE takes for a
    // whole-file operation) keeps the legacy app from writing mid-copy, so the .dbf, memo and index
    // copies agree with each other. Each file is copied under a .partial name and only renamed once
    // all of them are complete, so a backup folder never holds a half-copied table.
    private void BackUp(DbfTable opened, string table)
    {
        var target = RunBackupFolder();
        var files = Directory.EnumerateFiles(_reader.Folder)
            .Where(p => Path.GetFileNameWithoutExtension(p).Equals(table, StringComparison.OrdinalIgnoreCase)
                && BackedUpExtensions.Contains(Path.GetExtension(p), StringComparer.OrdinalIgnoreCase))
            .ToList();

        var staged = new List<(string Partial, string Final)>();
        try
        {
            using (opened.LockTable())
            {
                foreach (var source in files)
                {
                    var final = Path.Combine(target, Path.GetFileName(source));
                    var partial = final + ".partial";
                    staged.Add((partial, final));
                    using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    input.CopyTo(output);
                    output.Flush(flushToDisk: true);
                }
            }
            foreach (var (partial, final) in staged)
                File.Move(partial, final);
        }
        catch
        {
            foreach (var (partial, _) in staged)
                File.Delete(partial);
            throw;
        }
        _logger.LogInformation("Backed up {Table} ({Count} files) to {Folder} before its first write.", table, files.Count, target);
    }

    // One folder per run, named for when the first backup happened: backups/20261030-141500Z/.
    private string RunBackupFolder()
    {
        lock (_backupFolderLock)
        {
            if (_runBackupFolder is not null)
                return _runBackupFolder;

            var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss'Z'", System.Globalization.CultureInfo.InvariantCulture);
            var folder = Path.Combine(BackupFolder, stamp);
            for (var n = 2; Directory.Exists(folder); n++)
                folder = Path.Combine(BackupFolder, $"{stamp}-{n}");
            Directory.CreateDirectory(folder);
            return _runBackupFolder = folder;
        }
    }

    public void Dispose()
    {
        _reader.Dispose();
        if (_writer.IsValueCreated)
            _writer.Value.Dispose();
        if (_unmaintainedWriter.IsValueCreated)
            _unmaintainedWriter.Value.Dispose();
    }
}
