//      ________
//     | @name  |=====>  WHERE NAME = @name        ProjectThunder :: Thunder.Dbf
//     | O'Hara |        ...compares with O'Hara,  Values travel beside the SQL, never inside it,
//     |________|        quote and all.            so a value can't become part of the command.

using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;

namespace Thunder.Dbf.Data;

/// <summary>
/// A named value for a <see cref="DbfCommand"/>. Names may be written with or without the leading
/// '@' (or ':'), so <c>@city</c> in the SQL matches a parameter named <c>city</c> or <c>@city</c>.
/// </summary>
public sealed class DbfParameter : DbParameter
{
    private DbType? _dbType;
    private string _name = "";
    private string _sourceColumn = "";

    /// <summary>A parameter with no name or value yet.</summary>
    public DbfParameter()
    {
    }

    /// <summary>A parameter called <paramref name="name"/> holding <paramref name="value"/>.</summary>
    public DbfParameter(string name, object? value)
    {
        ParameterName = name;
        Value = value;
    }

    /// <summary>The type implied by <see cref="Value"/>, unless set explicitly. Thunder.Dbf converts by the column's type either way.</summary>
    public override DbType DbType
    {
        get => _dbType ?? Infer(Value);
        set => _dbType = value;
    }

    /// <summary>Only <see cref="ParameterDirection.Input"/>: dBASE has no stored procedures to return values.</summary>
    public override ParameterDirection Direction
    {
        get => ParameterDirection.Input;
        set
        {
            if (value != ParameterDirection.Input)
                throw new NotSupportedException("Thunder.Dbf parameters are input-only; there are no stored procedures or output values.");
        }
    }

    /// <inheritdoc />
    public override bool IsNullable { get; set; } = true;

    /// <inheritdoc />
    [AllowNull]
    public override string ParameterName
    {
        get => _name;
        set => _name = value ?? "";
    }

    /// <inheritdoc />
    public override int Size { get; set; }

    /// <inheritdoc />
    [AllowNull]
    public override string SourceColumn
    {
        get => _sourceColumn;
        set => _sourceColumn = value ?? "";
    }

    /// <inheritdoc />
    public override bool SourceColumnNullMapping { get; set; }

    /// <inheritdoc />
    public override DataRowVersion SourceVersion { get; set; } = DataRowVersion.Current;

    /// <inheritdoc />
    public override object? Value { get; set; }

    /// <inheritdoc />
    public override void ResetDbType() => _dbType = null;

    /// <summary>The name without its '@' or ':' prefix, as the SQL refers to it.</summary>
    internal string BareName => Bare(_name);

    internal static string Bare(string name) => name.Length > 0 && name[0] is '@' or ':' ? name[1..] : name;

    private static DbType Infer(object? value) => value switch
    {
        null or DBNull => DbType.Object,
        string or char => DbType.String,
        bool => DbType.Boolean,
        byte => DbType.Byte,
        short => DbType.Int16,
        int => DbType.Int32,
        long => DbType.Int64,
        float => DbType.Single,
        double => DbType.Double,
        decimal => DbType.Decimal,
        DateOnly => DbType.Date,
        DateTime => DbType.DateTime,
        DateTimeOffset => DbType.DateTimeOffset,
        Guid => DbType.Guid,
        byte[] => DbType.Binary,
        _ => DbType.Object,
    };
}
