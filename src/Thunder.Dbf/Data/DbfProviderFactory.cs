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

    public override DbConnection CreateConnection() => new DbfConnection();

    public override DbCommand CreateCommand() => new DbfCommand();

    public override DbParameter CreateParameter() => new DbfParameter();

    public override DbConnectionStringBuilder CreateConnectionStringBuilder() => new();

    public override bool CanCreateDataAdapter => false;

    public override bool CanCreateCommandBuilder => false;

    public override bool CanCreateDataSourceEnumerator => false;
}
