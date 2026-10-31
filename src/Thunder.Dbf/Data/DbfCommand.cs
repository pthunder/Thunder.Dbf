//       .---------------------------.
//      /  SELECT NAME FROM CLIENT  /|      ProjectThunder :: Thunder.Dbf
//     /   WHERE CITY = @city      / |      One statement, one table, values as parameters.
//    '---------------------------'  |      Parse, bind, pick a plan, run.
//    |  >_                       |  /
//    |___________________________|/        command.Explain() says which plan it picked.

using System.ComponentModel;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;

namespace Thunder.Dbf.Data;

/// <summary>
/// A SQL statement against one table of a <see cref="DbfConnection"/>. The SQL is a deliberately
/// small subset: SELECT (with WHERE, ORDER BY, LIMIT, OFFSET), INSERT, UPDATE and DELETE. Values come
/// from <see cref="Parameters"/>, never from text pasted into the SQL.
/// </summary>
/// <remarks>
/// Deleted records are left out unless the WHERE mentions <c>_DELETED</c>. Every row also has the
/// pseudo-columns <c>_RECNO</c> (the 1-based record number) and <c>_DELETED</c> (the deletion mark);
/// <c>SET _DELETED = FALSE</c> recalls a record. Text equality is case-sensitive with trailing spaces
/// ignored, as in dBASE; <c>LIKE</c> is case-insensitive.
/// </remarks>
public sealed class DbfCommand : DbCommand
{
    private string _commandText = "";
    private DbfConnection? _connection;
    private readonly DbfParameterCollection _parameters = new();
    private DbfQuery? _running;

    public DbfCommand()
    {
    }

    public DbfCommand(string commandText, DbfConnection? connection = null)
    {
        _commandText = commandText ?? "";
        _connection = connection;
    }

    [AllowNull]
    public override string CommandText
    {
        get => _commandText;
        set => _commandText = value ?? "";
    }

    /// <summary>
    /// Seconds a statement may run before it stops with <see cref="DbfErrorCode.Timeout"/>; 0 means no limit.
    /// A reader's clock runs until the reader is closed.
    /// </summary>
    public override int CommandTimeout
    {
        get => _commandTimeout;
        set => _commandTimeout = value >= 0 ? value : throw new ArgumentOutOfRangeException(nameof(value), "CommandTimeout can't be negative.");
    }

    private int _commandTimeout = 30;

    /// <summary>Only <see cref="CommandType.Text"/>: there are no stored procedures or table-direct commands.</summary>
    public override CommandType CommandType
    {
        get => CommandType.Text;
        set
        {
            if (value != CommandType.Text)
                throw new NotSupportedException($"Thunder.Dbf only runs SQL text commands, not {value}.");
        }
    }

    [DefaultValue(true), DesignOnly(true), Browsable(false), EditorBrowsable(EditorBrowsableState.Never)]
    public override bool DesignTimeVisible { get; set; } = true;

    public override UpdateRowSource UpdatedRowSource { get; set; } = UpdateRowSource.None;

    public new DbfConnection? Connection
    {
        get => _connection;
        set => _connection = value;
    }

    protected override DbConnection? DbConnection
    {
        get => _connection;
        set => _connection = value switch
        {
            null => null,
            DbfConnection dbf => dbf,
            _ => throw new ArgumentException($"A DbfCommand needs a DbfConnection, not a {value.GetType().Name}.", nameof(value)),
        };
    }

    public new DbfParameterCollection Parameters => _parameters;

    protected override DbParameterCollection DbParameterCollection => _parameters;

    /// <summary>Always null: DBF tables have no transactions.</summary>
    protected override DbTransaction? DbTransaction
    {
        get => null;
        set
        {
            if (value is not null)
                throw new NotSupportedException("DBF tables have no transactions.");
        }
    }

    public new DbfParameter CreateParameter() => new();

    protected override DbParameter CreateDbParameter() => CreateParameter();

    /// <summary>
    /// Stops the statement this command is running (or the reader it returned) at the next record,
    /// with an <see cref="OperationCanceledException"/>. Safe to call from another thread.
    /// </summary>
    public override void Cancel() => _running?.Cancel();

    /// <summary>Checks the SQL parses. Binding happens at execution, when parameter values are known.</summary>
    public override void Prepare() => SqlParser.Parse(_commandText);

    /// <summary>
    /// Describes how the command would find its rows, without changing anything: for example
    /// <c>scan CLIENT</c>, <c>read record 3 of CLIENT</c>, or <c>seek CHANGELOG index tag ID_TAG for ID = 2</c>.
    /// </summary>
    public string Explain()
    {
        using var query = Bind(write: false);
        return query.Plan;
    }

    // ---- execution ------------------------------------------------------------------------

    /// <summary>Runs an INSERT, UPDATE or DELETE and returns the number of records it touched (-1 for a SELECT).</summary>
    public override int ExecuteNonQuery()
    {
        var query = Bind(write: true);
        if (query.IsQuery)
        {
            using var reader = new DbfDataReader(query, CommandBehavior.Default, connection: null);
            while (reader.Read())
            {
                // Reading to the end runs the whole SELECT, as ADO.NET expects; the rows themselves aren't wanted.
            }
            return -1;
        }
        using (query)
            return query.Execute();
    }

    /// <summary>The first column of the first row of a SELECT, or null. Writes run and return null.</summary>
    public override object? ExecuteScalar()
    {
        var query = Bind(write: true);
        if (!query.IsQuery)
        {
            using (query)
                query.Execute();
            return null;
        }
        using var reader = new DbfDataReader(query, CommandBehavior.SingleRow, connection: null);
        return reader.Read() && reader.FieldCount > 0 ? reader.GetValue(0) : null;
    }

    public new DbfDataReader ExecuteReader() => ExecuteReader(CommandBehavior.Default);

    public new DbfDataReader ExecuteReader(CommandBehavior behavior) => (DbfDataReader)ExecuteDbDataReader(behavior);

    protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
    {
        // SchemaOnly asks for column information, so it must not change anything.
        var schemaOnly = behavior.HasFlag(CommandBehavior.SchemaOnly);
        var query = Bind(write: !schemaOnly);
        var closeWith = behavior.HasFlag(CommandBehavior.CloseConnection) ? _connection : null;
        if (query.IsQuery)
        {
            try
            {
                return new DbfDataReader(query, behavior, closeWith);
            }
            catch
            {
                query.Dispose(); // no reader took ownership, so nothing else would close the table
                throw;
            }
        }
        if (schemaOnly)
        {
            query.Dispose();
            return new DbfDataReader(-1, closeWith);
        }

        using (query)
            return new DbfDataReader(query.Execute(), closeWith);
    }

    private DbfQuery Bind(bool write)
    {
        if (_connection is null)
            throw new InvalidOperationException("The command has no connection. Set Connection to an open DbfConnection.");
        var database = _connection.RequireOpen();
        var statement = SqlParser.Parse(_commandText);

        if (write && statement is not SqlSelect && database.ReadOnly)
            throw new DbfException(DbfErrorCode.ReadOnlyConnection, "The connection is read-only, so INSERT, UPDATE and DELETE are refused.",
                "Connect with Read Only=false in the connection string to change data.");

        // A fresh table per command, so each command sees records other programs added since the last one.
        // DbfDatabase checks the name can't reach outside the folder.
        var table = database.OpenTable(statement.Table);

        // A table the library can't keep indexed may have a stale .mdx once this connection writes to it,
        // so only trust indexes when that can't happen.
        var trustIndexes = database.ReadOnly || !database.Options.AllowUnmaintainedIndexes;
        var query = DbfQuery.Bind(statement, table, statement.Table, _parameters.ValueFor, trustIndexes);
        query.StartClock(_commandTimeout);
        _running = query;
        return query;
    }
}
