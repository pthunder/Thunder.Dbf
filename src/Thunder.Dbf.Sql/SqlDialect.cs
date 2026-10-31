//      ______        ______           ProjectThunder :: Thunder.Dbf.Sql
//     | .dbf |  ->  | SQL  |          How dBASE types become SQL types, and back,
//     |______|  <-  |______|          for SQLite, SQL Server and PostgreSQL.
//
//   C(n)  -> varchar(n)    N(n,d) -> decimal(n,d)    D -> date      L -> boolean
//   M     -> text          I      -> int             Y -> money-ish  T -> timestamp

using System.Globalization;

namespace Thunder.Dbf.Sql;

/// <summary>
/// What differs between SQL databases: how to quote names, which column types to use, and how
/// values travel in and out. Use <see cref="Sqlite"/>, <see cref="SqlServer"/> or <see cref="PostgreSql"/>,
/// or derive your own.
/// </summary>
public abstract class SqlDialect
{
    /// <summary>SQLite.</summary>
    public static readonly SqlDialect Sqlite = new SqliteDialect();
    /// <summary>Microsoft SQL Server and Azure SQL.</summary>
    public static readonly SqlDialect SqlServer = new SqlServerDialect();
    /// <summary>PostgreSQL.</summary>
    public static readonly SqlDialect PostgreSql = new PostgreSqlDialect();

    /// <summary>The database's name, for messages.</summary>
    public abstract string Name { get; }

    /// <summary>A table or column name, quoted so it can't be read as SQL.</summary>
    public abstract string Quote(string identifier);

    /// <summary>The column definition for an auto-numbered primary key.</summary>
    public abstract string IdentityColumn(string name);

    /// <summary>The SQL type for a logical (L) field.</summary>
    public abstract string BooleanType { get; }

    /// <summary>Whether a UNIQUE column may hold many NULLs (true everywhere but SQL Server).</summary>
    public virtual bool UniqueAllowsManyNulls => true;

    /// <summary>The SQL type for a DBF field.</summary>
    public virtual string TypeFor(DbfField field) => field.Type switch
    {
        'C' => $"varchar({field.Length})",
        'N' when field.DecimalCount == 0 && field.Length <= 18 => "bigint",
        'N' => $"decimal({Math.Max(field.Length, field.DecimalCount + 1)},{field.DecimalCount})",
        'F' => "double precision",
        'B' when field.Length == 8 => "double precision",
        'D' => "date",
        'L' => BooleanType,
        'I' => "integer",
        'Y' => "decimal(19,4)",
        'T' => "timestamp",
        'M' when !field.IsBinary => TextType,
        _ => BinaryType,
    };

    /// <summary>The SQL type for a text memo field.</summary>
    public virtual string TextType => "text";
    /// <summary>The SQL type for binary memo, general and picture fields.</summary>
    public virtual string BinaryType => "bytea";

    /// <summary>A value as the database driver wants it.</summary>
    public virtual object ToDatabase(object? value) => value switch
    {
        null => DBNull.Value,
        DateOnly d => d.ToDateTime(TimeOnly.MinValue),
        _ => value,
    };

    /// <summary>A value read from the database, converted to what a DBF field of this type holds.</summary>
    public virtual object? FromDatabase(object? value, DbfField field)
    {
        if (value is null or DBNull)
            return null;

        return field.Type switch
        {
            'C' => System.Convert.ToString(value, CultureInfo.InvariantCulture),
            'M' when !field.IsBinary => System.Convert.ToString(value, CultureInfo.InvariantCulture),
            'N' or 'Y' => value is string s ? decimal.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture) : System.Convert.ToDecimal(value, CultureInfo.InvariantCulture),
            'F' => System.Convert.ToDouble(value, CultureInfo.InvariantCulture),
            'B' when field.Length == 8 => System.Convert.ToDouble(value, CultureInfo.InvariantCulture),
            'I' => System.Convert.ToInt32(value, CultureInfo.InvariantCulture),
            'L' => value is bool b ? b : System.Convert.ToInt64(value, CultureInfo.InvariantCulture) != 0,
            'D' => value switch
            {
                DateOnly d => d,
                DateTime dt => DateOnly.FromDateTime(dt),
                string s => DateOnly.Parse(s, CultureInfo.InvariantCulture),
                _ => throw new FormatException($"Can't read {value.GetType().Name} as a date for {field.Name}."),
            },
            'T' => value switch
            {
                DateTime dt => dt,
                string s => DateTime.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                _ => throw new FormatException($"Can't read {value.GetType().Name} as a datetime for {field.Name}."),
            },
            _ => value is byte[] bytes ? bytes : value,
        };
    }

    private sealed class SqliteDialect : SqlDialect
    {
        public override string Name => "SQLite";
        public override string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
        public override string IdentityColumn(string name) => $"{Quote(name)} integer primary key autoincrement";
        public override string BooleanType => "integer";
        public override string BinaryType => "blob";

        // SQLite has no date or exact decimal types: dates travel as ISO text, decimals as text,
        // so nothing is rounded through a double.
        public override object ToDatabase(object? value) => value switch
        {
            null => DBNull.Value,
            DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            DateTime dt => dt.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture),
            decimal m => m.ToString(CultureInfo.InvariantCulture),
            bool b => b ? 1 : 0,
            _ => value,
        };

        public override string TypeFor(DbfField field) => field.Type switch
        {
            // Anything that doesn't fit a 64-bit integer would come back as a rounded REAL.
            'N' when field.DecimalCount > 0 || field.Length > 18 => "text",
            'Y' => "text",
            'D' or 'T' => "text",
            _ => base.TypeFor(field),
        };
    }

    private sealed class SqlServerDialect : SqlDialect
    {
        public override string Name => "SQL Server";
        public override string Quote(string identifier) => "[" + identifier.Replace("]", "]]", StringComparison.Ordinal) + "]";
        public override string IdentityColumn(string name) => $"{Quote(name)} int identity(1,1) primary key";
        public override string BooleanType => "bit";
        public override bool UniqueAllowsManyNulls => false;
        public override string TextType => "nvarchar(max)";
        public override string BinaryType => "varbinary(max)";

        public override string TypeFor(DbfField field) => field.Type switch
        {
            'C' => $"nvarchar({field.Length})",
            'F' => "float",
            'B' when field.Length == 8 => "float",
            'T' => "datetime2",
            _ => base.TypeFor(field),
        };
    }

    private sealed class PostgreSqlDialect : SqlDialect
    {
        public override string Name => "PostgreSQL";
        public override string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
        public override string IdentityColumn(string name) => $"{Quote(name)} integer generated always as identity primary key";
        public override string BooleanType => "boolean";
    }
}
