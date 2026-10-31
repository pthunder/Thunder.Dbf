//      ______                    ______        ProjectThunder :: Thunder.Dbf.Sql
//     | .dbf |  <== changes ==> | SQL  |       Keeps a live DBF table and a SQL table in step,
//     |______|                  |______|       both ways, while the legacy app keeps running.
//          \                      /
//           '---- _thunder_sync -'             The sync table remembers each row's fingerprint
//                 recno | dbf | sql            on both sides at the last sync, so a sync moves
//                                              only what changed since, in either direction.
//
//   DBF changed only   -> copy to SQL          both changed  -> the conflict policy decides
//   SQL changed only   -> copy to DBF          new SQL row   -> appended to the DBF
//   SQL row removed    -> DBF delete mark      DBF record deleted -> _deleted set in SQL

using System.Data;
using System.Data.Common;

namespace Thunder.Dbf.Sql;

/// <summary>Which side wins when a row changed on both sides since the last sync.</summary>
public enum DbfConflictPolicy
{
    /// <summary>The legacy DBF is the system of record (the default).</summary>
    DbfWins,
    SqlWins,
}

/// <summary>What one sync did.</summary>
public sealed record DbfSyncResult(int ToSql, int ToDbf, int AddedToDbf, int DeletedInDbf, IReadOnlyList<DbfSyncConflict> Conflicts)
{
    public int Changes => ToSql + ToDbf + AddedToDbf + DeletedInDbf;
}

/// <summary>A row both sides changed; <see cref="Winner"/> says which change was kept.</summary>
public sealed record DbfSyncConflict(int RecordNumber, string Winner);

/// <summary>Two-way sync between one DBF table and its SQL mirror (as created by <see cref="DbfMigrator"/>).</summary>
public sealed class DbfBridge
{
    private const string SyncTable = "_thunder_sync";

    private readonly DbfTable _table;
    private readonly DbConnection _connection;
    private readonly SqlDialect _dialect;
    private readonly SqlTable _sql;

    /// <summary>Pairs a DBF table with its SQL mirror.</summary>
    /// <param name="table">The DBF table, opened writable (so SQL-side changes can be applied).</param>
    /// <param name="connection">An open connection to the SQL database.</param>
    /// <param name="dialect">The SQL database's dialect, for quoting and types.</param>
    /// <param name="sqlTableName">The mirror table's name; defaults to the DBF table's name.</param>
    public DbfBridge(DbfTable table, DbConnection connection, SqlDialect dialect, string sqlTableName)
    {
        _table = table;
        _connection = connection;
        _dialect = dialect;
        _sql = new SqlTable(connection, dialect, sqlTableName, table.Fields);
    }

    public DbfConflictPolicy ConflictPolicy { get; init; } = DbfConflictPolicy.DbfWins;

    /// <summary>
    /// Brings both sides up to date. Safe to call on a timer: when nothing changed, nothing is written.
    /// The first sync creates the mirror and sync tables if they don't exist yet.
    /// </summary>
    /// <remarks>
    /// The SQL side is read and written inside one serializable transaction, so a SQL change made while
    /// a sync runs either waits or is seen. A DBF table that was packed (record numbers changed) since the
    /// last sync is refused rather than paired up wrongly.
    /// </remarks>
    public DbfSyncResult Sync()
    {
        EnsureTables();
        _table.Refresh();

        using var transaction = _connection.BeginTransaction(IsolationLevel.Serializable);

        var memory = ReadMemory(transaction);
        var sqlRows = _sql.ReadAll(transaction);
        var byRecno = sqlRows.Where(r => r.Recno is not null).ToDictionary(r => r.Recno!.Value);
        RefuseRenumbered(memory, byRecno);

        var conflicts = new List<DbfSyncConflict>();
        int toSql = 0, toDbf = 0, added = 0, deleted = 0;

        // A sync that appended SQL rows to the DBF and then failed before its SQL commit left those
        // records in the DBF with nothing pointing at them. Pair them with their SQL rows again by
        // content, so a retry doesn't append them twice or mirror them back as new rows.
        var newSqlRows = sqlRows.Where(r => r.Recno is null).ToList();
        var adopted = AdoptOrphans(newSqlRows, memory, byRecno, transaction);
        added += adopted.Count;

        foreach (var record in _table.ReadAll())
        {
            var recno = record.RecordNumber;
            if (adopted.Contains(recno))
                continue;
            var dbfValues = _sql.ValuesOf(record, _table);
            var dbfPrint = _sql.Fingerprint(record.IsDeleted, dbfValues);
            memory.TryGetValue(recno, out var last);
            byRecno.TryGetValue(recno, out var sqlRow);

            if (sqlRow is null)
            {
                if (last is null)
                {
                    // New in the DBF.
                    _sql.Insert(recno, record.IsDeleted, dbfValues, transaction);
                    Remember(recno, dbfPrint, dbfPrint, transaction);
                    toSql++;
                    continue;
                }

                var dbfChanged = last.Dbf != dbfPrint;
                if (last.Sql == Missing && !dbfChanged)
                    continue; // removed in SQL earlier and already marked deleted here

                // The SQL row is gone. If the DBF record also changed since (edited, or recalled after
                // the delete), that's a conflict like any other, and the policy decides.
                if (dbfChanged)
                {
                    var keepDbf = ConflictPolicy == DbfConflictPolicy.DbfWins;
                    conflicts.Add(new DbfSyncConflict(recno, keepDbf ? "DBF" : "SQL"));
                    if (keepDbf)
                    {
                        _sql.Insert(recno, record.IsDeleted, dbfValues, transaction);
                        Remember(recno, dbfPrint, dbfPrint, transaction);
                        toSql++;
                        continue;
                    }
                }
                if (!record.IsDeleted)
                {
                    _table.Delete(recno - 1);
                    deleted++;
                }
                Remember(recno, _sql.Fingerprint(true, dbfValues), Missing, transaction);
                continue;
            }

            var sqlPrint = _sql.Fingerprint(sqlRow.Deleted, sqlRow.Values);
            var dbfMoved = last is null || last.Dbf != dbfPrint;
            var sqlMoved = last is null || last.Sql != sqlPrint;
            if (dbfPrint == sqlPrint)
            {
                if (dbfMoved || sqlMoved)
                    Remember(recno, dbfPrint, sqlPrint, transaction);
                continue;
            }
            if (!dbfMoved && !sqlMoved)
                continue; // they differ, but neither moved since the last sync: nothing to carry

            var useDbf = (dbfMoved, sqlMoved) switch
            {
                (true, false) => true,
                (false, true) => false,
                _ => ConflictPolicy == DbfConflictPolicy.DbfWins,
            };
            if (dbfMoved && sqlMoved)
                conflicts.Add(new DbfSyncConflict(recno, useDbf ? "DBF" : "SQL"));

            if (useDbf)
            {
                _sql.UpdateByRecno(recno, record.IsDeleted, dbfValues, transaction);
                Remember(recno, dbfPrint, dbfPrint, transaction);
                toSql++;
            }
            else
            {
                ApplyToDbf(record.Index, record.IsDeleted, sqlRow);
                Converge(record.Index, transaction);
                toDbf++;
            }
        }

        // Rows added on the SQL side reach the DBF as new records and learn their record numbers.
        foreach (var row in newSqlRows.Where(r => !adopted.Ids.Contains(r.Id)))
        {
            var index = _table.Append(ValuesForDbf(row.Values));
            if (row.Deleted)
                _table.Delete(index);
            _sql.SetRecno(row.Id, index + 1, transaction);
            Converge(index, transaction);
            added++;
        }

        transaction.Commit();
        return new DbfSyncResult(toSql, toDbf, added, deleted, conflicts);
    }

    // PACK removes deleted records and renumbers the rest, which would pair every SQL row with the
    // wrong record. A record number the bridge remembers that's now past the end is the tell.
    private void RefuseRenumbered(Dictionary<int, Memory> memory, Dictionary<int, SqlRow> byRecno)
    {
        var count = _table.RecordCount;
        var highest = Math.Max(memory.Count == 0 ? 0 : memory.Keys.Max(), byRecno.Count == 0 ? 0 : byRecno.Keys.Max());
        if (highest > count)
            throw new DbfException(DbfErrorCode.IncompleteTransaction,
                $"The DBF table has {count} records, but the bridge last synced record {highest}. It looks packed or truncated, so record numbers no longer match.",
                "Re-run the migrator with ReplaceExisting to rebuild the SQL mirror, and clear this table's rows from _thunder_sync.");
    }

    private sealed record Adoption(HashSet<int> Recnos, HashSet<long> Ids)
    {
        public int Count => Ids.Count;
        public bool Contains(int recno) => Recnos.Contains(recno);
    }

    private Adoption AdoptOrphans(List<SqlRow> newSqlRows, Dictionary<int, Memory> memory, Dictionary<int, SqlRow> byRecno, DbTransaction transaction)
    {
        var adoption = new Adoption([], []);
        if (newSqlRows.Count == 0)
            return adoption;

        // Orphans are records past everything the bridge has seen, with no SQL row of their own.
        var known = Math.Max(memory.Count == 0 ? 0 : memory.Keys.Max(), byRecno.Count == 0 ? 0 : byRecno.Keys.Max());
        var waiting = newSqlRows.GroupBy(r => _sql.Fingerprint(r.Deleted, r.Values)).ToDictionary(g => g.Key, g => new Queue<SqlRow>(g));
        for (var index = known; index < _table.RecordCount; index++)
        {
            var record = _table.Read(index);
            if (!waiting.TryGetValue(_sql.Fingerprint(record.IsDeleted, _sql.ValuesOf(record, _table)), out var rows) || rows.Count == 0)
                continue;
            var row = rows.Dequeue();
            _sql.SetRecno(row.Id, record.RecordNumber, transaction);
            Converge(index, transaction);
            adoption.Recnos.Add(record.RecordNumber);
            adoption.Ids.Add(row.Id);
        }
        return adoption;
    }

    // After a SQL change lands in the DBF, the DBF holds what it can store (text padded or trimmed,
    // numbers rounded to the field's decimals, blanks for SQL NULLs). Copy that back so both sides
    // agree and the next sync sees nothing to do.
    private void Converge(int index, DbTransaction transaction)
    {
        var written = _table.Read(index);
        var values = _sql.ValuesOf(written, _table);
        _sql.UpdateByRecno(index + 1, written.IsDeleted, values, transaction);
        var print = _sql.Fingerprint(written.IsDeleted, values);
        Remember(index + 1, print, print, transaction);
    }

    // Only the mirrored fields are written, under the record lock, so a field the legacy app changed
    // a moment ago that SQL doesn't mirror (or didn't change) keeps its new value.
    private void ApplyToDbf(int index, bool wasDeleted, SqlRow row)
    {
        var changes = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < _sql.Fields.Count; i++)
            changes[_sql.Fields[i].Name] = row.Values[i];
        _table.UpdateFields(index, changes);
        if (row.Deleted && !wasDeleted)
            _table.Delete(index);
        else if (!row.Deleted && wasDeleted)
            _table.Recall(index);
    }

    // SQL columns map onto DBF fields; fields that aren't mirrored (the hidden _NullFlags) start empty.
    private object?[] ValuesForDbf(object?[] sqlValues)
    {
        var values = new object?[_table.Fields.Count];
        for (var i = 0; i < _sql.Fields.Count; i++)
            values[SqlTable.IndexIn(_table, _sql.Fields[i])] = sqlValues[i];
        return values;
    }

    // ---- the sync table ---------------------------------------------------------------------

    private const string Missing = "missing";

    private sealed record Memory(string Dbf, string Sql);

    private void EnsureTables()
    {
        if (!_sql.Exists())
            _sql.Create();

        try
        {
            using var probe = Command($"select count(*) from {_dialect.Quote(SyncTable)} where 1 = 0", null);
            probe.ExecuteScalar();
        }
        catch (DbException)
        {
            using var create = Command(
                $"create table {_dialect.Quote(SyncTable)} ({_dialect.Quote("table_name")} varchar(128) not null, " +
                $"{_dialect.Quote("recno")} bigint not null, {_dialect.Quote("dbf_print")} varchar(64) not null, " +
                $"{_dialect.Quote("sql_print")} varchar(64) not null, primary key ({_dialect.Quote("table_name")}, {_dialect.Quote("recno")}))", null);
            create.ExecuteNonQuery();
        }
    }

    private Dictionary<int, Memory> ReadMemory(DbTransaction transaction)
    {
        using var command = Command($"select {_dialect.Quote("recno")}, {_dialect.Quote("dbf_print")}, {_dialect.Quote("sql_print")} " +
            $"from {_dialect.Quote(SyncTable)} where {_dialect.Quote("table_name")} = @table", transaction);
        AddParameter(command, "@table", _sql.Name);
        using var reader = command.ExecuteReader();
        var memory = new Dictionary<int, Memory>();
        while (reader.Read())
            memory[Convert.ToInt32(reader.GetValue(0))] = new Memory(reader.GetString(1), reader.GetString(2));
        return memory;
    }

    private void Remember(int recno, string dbfPrint, string sqlPrint, DbTransaction transaction)
    {
        using (var delete = Command($"delete from {_dialect.Quote(SyncTable)} where {_dialect.Quote("table_name")} = @table and {_dialect.Quote("recno")} = @recno", transaction))
        {
            AddParameter(delete, "@table", _sql.Name);
            AddParameter(delete, "@recno", (long)recno);
            delete.ExecuteNonQuery();
        }
        using var insert = Command($"insert into {_dialect.Quote(SyncTable)} ({_dialect.Quote("table_name")}, {_dialect.Quote("recno")}, " +
            $"{_dialect.Quote("dbf_print")}, {_dialect.Quote("sql_print")}) values (@table, @recno, @dbf, @sql)", transaction);
        AddParameter(insert, "@table", _sql.Name);
        AddParameter(insert, "@recno", (long)recno);
        AddParameter(insert, "@dbf", dbfPrint);
        AddParameter(insert, "@sql", sqlPrint);
        insert.ExecuteNonQuery();
    }

    private DbCommand Command(string sql, DbTransaction? transaction)
    {
        var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        return command;
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
