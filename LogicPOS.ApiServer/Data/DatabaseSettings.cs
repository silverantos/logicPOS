using Microsoft.Extensions.Configuration;

namespace LogicPOS.ApiServer.Data;

public sealed class DatabaseSettings
{
    public const string SectionName = "DatabaseSettings";

    public string DatabaseType { get; set; } = string.Empty;
    public string ConnectionString { get; set; } = string.Empty;
    public bool UseMigrations { get; set; } = true;
    public string SeedPath { get; set; } = string.Empty;
    public bool UseSeed { get; set; } = true;
    public string Module { get; set; } = string.Empty;
}

public sealed record ResolvedDatabaseSettings(
    string DatabaseType,
    string ConnectionString,
    bool UseMigrations,
    string SeedPath,
    bool UseSeed,
    string Module,
    bool RequireSeedFiles)
{
    public string GetSeedFile(params string[] relativeSegments)
    {
        var segments = new string[relativeSegments.Length + 1];
        segments[0] = SeedPath;
        Array.Copy(relativeSegments, 0, segments, 1, relativeSegments.Length);
        return Path.Combine(segments);
    }
}

public static class DatabaseSettingsResolver
{
    private static readonly string[] SharedSeedFiles =
    [
        "articleclasses.json",
        "countries.json",
        "currencies.json",
        "discountgroups.json",
        "inputreaders.json",
        "measurementunits.json",
        "movementtypes.json",
        "paymentconditions.json",
        "paymentmethods.json",
        "permissiongroups.json",
        "permissionitems.json",
        "permissionprofiles.json",
        "poledisplays.json",
        "printers.json",
        "printertypes.json",
        "sizeunits.json",
        "systemaudittypes.json",
        "systemnotificationtypes.json",
        "warehouselocations.json",
        "warehouses.json",
        "weighingmachines.json",
        "pt/customers.json",
        "pt/documenttypes.json",
        "pt/holidays.json",
        "pt/preferenceparameters.json",
        "pt/vatexemptionreasons.json",
        "pt/vatrates.json"
    ];

    private static readonly string[] ModuleSeedFiles =
    [
        "articlefamilies.json",
        "articles.json",
        "articlesubfamilies.json",
        "articletypes.json",
        "commissiongroups.json",
        "customertypes.json",
        "places.json",
        "pricetypes.json",
        "tables.json",
        "userprofiles.json",
        "users.json"
    ];

    public static ResolvedDatabaseSettings Resolve(IConfiguration configuration, string contentRootPath)
    {
        var settings = configuration.GetSection(DatabaseSettings.SectionName).Get<DatabaseSettings>() ?? new DatabaseSettings();
        var systemModule = configuration["SystemInformation:Module"];

        var databaseType = string.IsNullOrWhiteSpace(settings.DatabaseType)
            ? "Sqlite"
            : settings.DatabaseType.Trim();

        if (!string.Equals(databaseType, "Sqlite", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Unsupported DatabaseSettings.DatabaseType '{databaseType}'. LogicPOS.ApiServer currently supports only 'Sqlite'.");
        }

        var connectionString = string.IsNullOrWhiteSpace(settings.ConnectionString)
            ? configuration.GetConnectionString("DefaultConnection")
            : settings.ConnectionString.Trim();

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "No SQLite connection string was configured. Set DatabaseSettings.ConnectionString or ConnectionStrings:DefaultConnection.");
        }

        var seedPath = ResolveSeedPath(settings.SeedPath, contentRootPath);
        var configuredModule = string.IsNullOrWhiteSpace(settings.Module) == false
            ? settings.Module
            : systemModule;
        var module = string.IsNullOrWhiteSpace(configuredModule)
            ? "default"
            : configuredModule.Trim().ToLowerInvariant();
        var useSeed = settings.UseSeed || string.IsNullOrWhiteSpace(configuredModule) == false;

        var resolved = new ResolvedDatabaseSettings(
            DatabaseType: "Sqlite",
            ConnectionString: connectionString,
            UseMigrations: settings.UseMigrations,
            SeedPath: seedPath,
            UseSeed: useSeed,
            Module: module,
            RequireSeedFiles: useSeed ||
                              string.IsNullOrWhiteSpace(configuredModule) == false ||
                              string.IsNullOrWhiteSpace(settings.SeedPath) == false);

        if (resolved.RequireSeedFiles)
        {
            EnsureDirectoryExists(resolved.SeedPath, "seed root");
        }

        var shouldValidateModule = resolved.UseSeed || string.IsNullOrWhiteSpace(configuredModule) == false;
        if (shouldValidateModule)
        {
            var modulesPath = resolved.GetSeedFile("modules");
            EnsureDirectoryExists(modulesPath, "modules");

            var modulePath = resolved.GetSeedFile("modules", resolved.Module);
            if (!Directory.Exists(modulePath))
            {
                var availableModules = Directory.Exists(modulesPath)
                    ? string.Join(", ", Directory.GetDirectories(modulesPath).Select(Path.GetFileName).OrderBy(name => name))
                    : string.Empty;

                throw new InvalidOperationException(
                    $"DatabaseSettings.Module '{resolved.Module}' does not exist under '{modulesPath}'. " +
                    $"Available modules: {availableModules}.");
            }
        }

        if (resolved.UseSeed)
        {
            foreach (var sharedSeedFile in SharedSeedFiles)
            {
                EnsureFileExists(resolved.GetSeedFile(sharedSeedFile));
            }

            foreach (var moduleSeedFile in ModuleSeedFiles)
            {
                EnsureFileExists(resolved.GetSeedFile("modules", resolved.Module, moduleSeedFile));
            }
        }

        return resolved;
    }

    private static string ResolveSeedPath(string configuredSeedPath, string contentRootPath)
    {
        var seedPath = string.IsNullOrWhiteSpace(configuredSeedPath) ? "db_seeds" : configuredSeedPath.Trim();
        return Path.IsPathRooted(seedPath)
            ? Path.GetFullPath(seedPath)
            : Path.GetFullPath(Path.Combine(contentRootPath, seedPath));
    }

    private static void EnsureDirectoryExists(string path, string description)
    {
        if (!Directory.Exists(path))
        {
            throw new InvalidOperationException(
                $"The configured {description} directory '{path}' was not found. " +
                "Check DatabaseSettings.SeedPath and ensure the db_seeds content was deployed with the application.");
        }
    }

    private static void EnsureFileExists(string path)
    {
        if (!File.Exists(path))
        {
            throw new InvalidOperationException(
                $"The required seed file '{path}' was not found. " +
                "Check DatabaseSettings.SeedPath and the published db_seeds contents.");
        }
    }
}
