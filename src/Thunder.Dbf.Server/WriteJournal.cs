//     __________________
//    |  writes.jsonl    |      ProjectThunder :: Thunder.Dbf
//    |------------------|      Every write, one line each, appended and flushed:
//    | {"op":"insert"…} |      when, which key, which table and record, and the
//    | {"op":"update"…} |      values before and after. Never the key itself.
//    | {"op":"delete"…} |      Each write is two lines: its intent before the file
//    |__________________|      changes, and what happened after. Lines are only ever
//                              added, so `tail -f` is an audit log.

using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Thunder.Dbf.Server;

/// <summary>One line of the write journal.</summary>
public sealed record JournalEntry(
    DateTimeOffset Time,
    string KeyId,
    string Table,
    int Recno,
    string Operation,
    IReadOnlyDictionary<string, object?>? Before,
    IReadOnlyDictionary<string, object?>? After,
    string? RemoteIp,
    string Status = JournalStatus.Done);

/// <summary>
/// Whether a journal line was written before the change (<see cref="Intent"/>) or after it
/// succeeded (<see cref="Done"/>). An intent with no matching done line is a write that failed or
/// was interrupted: check that record in the table.
/// </summary>
public static class JournalStatus
{
    public const string Intent = "intent";
    public const string Done = "done";
}

/// <summary>The append-only JSON Lines file that records every write the service makes.</summary>
public sealed class WriteJournal
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.Create(System.Text.Unicode.UnicodeRanges.All),
    };

    private readonly Lock _lock = new();

    public WriteJournal(IOptions<ThunderOptions> options, IHostEnvironment environment)
    {
        Path = System.IO.Path.GetFullPath(options.Value.Journal.Path, environment.ContentRootPath);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
    }

    /// <summary>The journal's full path. For logs only.</summary>
    public string Path { get; }

    /// <summary>
    /// Appends one entry and flushes it to disk. Called after the table write succeeds and while the
    /// table's lock is still held, so the journal's order is the order the writes happened in.
    /// </summary>
    public void Append(JournalEntry entry)
    {
        var line = JsonSerializer.SerializeToUtf8Bytes(entry, JsonOptions);
        lock (_lock)
        {
            using var stream = new FileStream(Path, FileMode.Append, FileAccess.Write, FileShare.Read);
            stream.Write(line);
            stream.WriteByte((byte)'\n');
            stream.Flush(flushToDisk: true);
        }
    }

    /// <summary>Fails at startup, not on the first write, if the journal can't be written.</summary>
    public void EnsureWritable()
    {
        lock (_lock)
        {
            using var _ = new FileStream(Path, FileMode.Append, FileAccess.Write, FileShare.Read);
        }
    }
}
