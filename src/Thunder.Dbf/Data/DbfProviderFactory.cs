//        _____
//       |     |____        ProjectThunder :: Thunder.Dbf
//       | DBF |    |===>   DbProviderFactories.RegisterFactory("Thunder.Dbf", DbfProviderFactory.Instance);
//       |_____|____|       ...and any tool that speaks ADO.NET by provider name can open DBF folders.

using System.Data.Common;

namespace Thunder.Dbf.Data;

/// <summary>
/// Creates Thunder.Dbf's ADO.NET objects, for tools that work through <see cref="DbProviderFactories"/>.
/// Register it once with <c>DbProviderFactories.RegisterFactory("Thunder.Dbf", DbfProviderFactory.Instance)</c>.
/// </summary>
public sealed class DbfProviderFactory : DbProviderFactory
{
    /// <summary>The invariant name to register the factory under.</summary>
    public const string InvariantName = "Thunder.Dbf";

    /// <summary>The one instance, as <see cref="DbProviderFactories"/> expects.</summary>
    public static readonly DbfProviderFactory Instance = new();

    private DbfProviderFactory()
    {
    }

    /// <inheritdoc />
    public override DbConnection CreateConnection() => new DbfConnection();

    /// <inheritdoc />
    public override DbCommand CreateCommand() => new DbfCommand();

    /// <inheritdoc />
    public override DbParameter CreateParameter() => new DbfParameter();

    /// <inheritdoc />
    public override DbConnectionStringBuilder CreateConnectionStringBuilder() => new();

    /// <inheritdoc />
    public override bool CanCreateDataAdapter => false;

    /// <inheritdoc />
    public override bool CanCreateCommandBuilder => false;

    /// <inheritdoc />
    public override bool CanCreateDataSourceEnumerator => false;
}
