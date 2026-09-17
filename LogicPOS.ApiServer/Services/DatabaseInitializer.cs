using LogicPOS.ApiServer.Data;
using Microsoft.EntityFrameworkCore;

namespace LogicPOS.ApiServer.Services;

public sealed class DatabaseInitializer
{
    private static readonly MigrationBaseline[] KnownBaselines =
    [
        new("20260915114207_InitialCreate", ["ApiUsers"]),
        new("20260915193506_AddLicensingAndTerminals", ["ApiLicenses", "ApiTerminals"]),
        new("20260915200858_AddCompanyInfo", ["ApiCompanyInfos"])
    ];

    private readonly ApplicationDbContext _dbContext;
    private readonly ResolvedDatabaseSettings _databaseSettings;
    private readonly ILogger<DatabaseInitializer> _logger;

    public DatabaseInitializer(
        ApplicationDbContext dbContext,
        ResolvedDatabaseSettings databaseSettings,
        ILogger<DatabaseInitializer> logger)
    {
        _dbContext = dbContext;
        _databaseSettings = databaseSettings;
        _logger = logger;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_databaseSettings.UseMigrations)
        {
            await BaselineEnsureCreatedDatabaseAsync(cancellationToken);
            await _dbContext.Database.MigrateAsync(cancellationToken);
            _logger.LogInformation("Applied EF Core migrations to the configured SQLite database.");
        }
        else
        {
            var created = await _dbContext.Database.EnsureCreatedAsync(cancellationToken);
            _logger.LogInformation(
                "DatabaseSettings.UseMigrations is disabled; ensured the SQLite schema exists (created={Created}).",
                created);
        }

        await EnsureRequiredTablesAsync(cancellationToken);
    }

    private async Task BaselineEnsureCreatedDatabaseAsync(CancellationToken cancellationToken)
    {
        if (KnownBaselines.Length == 0)
        {
            return;
        }

        var connection = _dbContext.Database.GetDbConnection();
        var shouldCloseConnection = connection.State != System.Data.ConnectionState.Open;
        if (shouldCloseConnection)
        {
            await connection.OpenAsync(cancellationToken);
        }

        try
        {
            var existingTables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var baseline in KnownBaselines)
            {
                foreach (var tableName in baseline.RequiredTables)
                {
                    if (await TableExistsAsync(connection, tableName, cancellationToken))
                    {
                        existingTables.Add(tableName);
                    }
                }
            }

            if (existingTables.Count == 0)
            {
                return;
            }

            await EnsureHistoryTableAsync(connection, cancellationToken);

            foreach (var baseline in KnownBaselines)
            {
                var tablesExist = baseline.RequiredTables.All(existingTables.Contains);
                var migrationExists = await MigrationExistsAsync(connection, baseline.MigrationId, cancellationToken);

                if (tablesExist && !migrationExists)
                {
                    await InsertMigrationHistoryAsync(connection, baseline.MigrationId, cancellationToken);
                    continue;
                }

                if (!tablesExist && migrationExists)
                {
                    await DeleteMigrationHistoryAsync(connection, baseline.MigrationId, cancellationToken);
                }
            }
        }
        finally
        {
            if (shouldCloseConnection)
            {
                await connection.CloseAsync();
            }
        }
    }

    private static async Task EnsureHistoryTableAsync(System.Data.Common.DbConnection connection, CancellationToken cancellationToken)
    {
        if (await TableExistsAsync(connection, "__EFMigrationsHistory", cancellationToken))
        {
            return;
        }

        await using var createHistoryCommand = connection.CreateCommand();
        createHistoryCommand.CommandText = @"
CREATE TABLE IF NOT EXISTS __EFMigrationsHistory (
    MigrationId TEXT NOT NULL CONSTRAINT PK___EFMigrationsHistory PRIMARY KEY,
    ProductVersion TEXT NOT NULL
);";
        await createHistoryCommand.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertMigrationHistoryAsync(System.Data.Common.DbConnection connection, string migrationId, CancellationToken cancellationToken)
    {
        await using var insertHistoryCommand = connection.CreateCommand();
        insertHistoryCommand.CommandText = @"
INSERT INTO __EFMigrationsHistory (MigrationId, ProductVersion)
VALUES ($migrationId, $productVersion);";

        var migrationIdParameter = insertHistoryCommand.CreateParameter();
        migrationIdParameter.ParameterName = "$migrationId";
        migrationIdParameter.Value = migrationId;
        insertHistoryCommand.Parameters.Add(migrationIdParameter);

        var productVersionParameter = insertHistoryCommand.CreateParameter();
        productVersionParameter.ParameterName = "$productVersion";
        productVersionParameter.Value = "9.0.9";
        insertHistoryCommand.Parameters.Add(productVersionParameter);

        await insertHistoryCommand.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task DeleteMigrationHistoryAsync(System.Data.Common.DbConnection connection, string migrationId, CancellationToken cancellationToken)
    {
        await using var deleteHistoryCommand = connection.CreateCommand();
        deleteHistoryCommand.CommandText = "DELETE FROM __EFMigrationsHistory WHERE MigrationId = $migrationId;";

        var migrationIdParameter = deleteHistoryCommand.CreateParameter();
        migrationIdParameter.ParameterName = "$migrationId";
        migrationIdParameter.Value = migrationId;
        deleteHistoryCommand.Parameters.Add(migrationIdParameter);

        await deleteHistoryCommand.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<bool> TableExistsAsync(System.Data.Common.DbConnection connection, string tableName, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $tableName;";

        var parameter = command.CreateParameter();
        parameter.ParameterName = "$tableName";
        parameter.Value = tableName;
        command.Parameters.Add(parameter);

        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is long count && count > 0;
    }

    private static async Task<bool> MigrationExistsAsync(System.Data.Common.DbConnection connection, string migrationId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM __EFMigrationsHistory WHERE MigrationId = $migrationId;";

        var parameter = command.CreateParameter();
        parameter.ParameterName = "$migrationId";
        parameter.Value = migrationId;
        command.Parameters.Add(parameter);

        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is long count && count > 0;
    }

    private async Task EnsureRequiredTablesAsync(CancellationToken cancellationToken)
    {
        var requiredTables = KnownBaselines
            .SelectMany(baseline => baseline.RequiredTables)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var connection = _dbContext.Database.GetDbConnection();
        var shouldCloseConnection = connection.State != System.Data.ConnectionState.Open;
        if (shouldCloseConnection)
        {
            await connection.OpenAsync(cancellationToken);
        }

        try
        {
            var missingTables = new List<string>();
            foreach (var tableName in requiredTables)
            {
                if (!await TableExistsAsync(connection, tableName, cancellationToken))
                {
                    missingTables.Add(tableName);
                }
            }

            if (missingTables.Count > 0)
            {
                throw new InvalidOperationException(
                    "The configured SQLite database does not contain the schema required by LogicPOS.ApiServer. " +
                    $"Missing tables: {string.Join(", ", missingTables)}. " +
                    "Enable DatabaseSettings.UseMigrations or recreate the database so the current EF Core model can be applied.");
            }
        }
        finally
        {
            if (shouldCloseConnection)
            {
                await connection.CloseAsync();
            }
        }
    }

    private sealed record MigrationBaseline(string MigrationId, string[] RequiredTables);
}
