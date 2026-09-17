using System.Text.Json;
using LogicPOS.ApiServer.Authentication;
using LogicPOS.ApiServer.Data;
using LogicPOS.ApiServer.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace LogicPOS.ApiServer.Services;

public sealed class DatabaseSeeder
{
    private static readonly string[] CommonSeedFiles =
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
        "tables.json"
    ];

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly ApplicationDbContext _dbContext;
    private readonly ResolvedDatabaseSettings _databaseSettings;
    private readonly BootstrapUserSettings _bootstrapUserSettings;
    private readonly PinHasher _pinHasher;
    private readonly ILogger<DatabaseSeeder> _logger;

    public DatabaseSeeder(
        ApplicationDbContext dbContext,
        ResolvedDatabaseSettings databaseSettings,
        IOptions<BootstrapUserSettings> bootstrapUserOptions,
        PinHasher pinHasher,
        ILogger<DatabaseSeeder> logger)
    {
        _dbContext = dbContext;
        _databaseSettings = databaseSettings;
        _bootstrapUserSettings = bootstrapUserOptions.Value;
        _pinHasher = pinHasher;
        _logger = logger;
    }

    public async Task SeedAsync(CancellationToken cancellationToken = default)
    {
        if (!_databaseSettings.UseSeed)
        {
            return;
        }

        var commonSeedRows = 0;
        foreach (var commonSeedFile in CommonSeedFiles)
        {
            commonSeedRows += await LoadRequiredCountAsync(
                _databaseSettings.GetSeedFile(commonSeedFile),
                $"shared seed '{commonSeedFile}'",
                cancellationToken);
        }

        var moduleSeedRows = 0;
        foreach (var moduleSeedFile in ModuleSeedFiles)
        {
            moduleSeedRows += await LoadRequiredCountAsync(
                _databaseSettings.GetSeedFile("modules", _databaseSettings.Module, moduleSeedFile),
                $"module '{_databaseSettings.Module}' seed '{moduleSeedFile}'",
                cancellationToken);
        }

        var legacyProfiles = await LoadRequiredAsync<LegacySeedUserProfile>(
            _databaseSettings.GetSeedFile("modules", _databaseSettings.Module, "userprofiles.json"),
            $"module '{_databaseSettings.Module}' user profiles",
            cancellationToken);
        var legacyUsers = await LoadRequiredAsync<LegacySeedUser>(
            _databaseSettings.GetSeedFile("modules", _databaseSettings.Module, "users.json"),
            $"module '{_databaseSettings.Module}' users",
            cancellationToken);

        ValidateUserProfileReferences(legacyUsers, legacyProfiles, _databaseSettings.Module);

        var existingUsers = await _dbContext.ApiUsers
            .ToDictionaryAsync(user => user.Id, cancellationToken);
        var existingUsersByUsername = existingUsers.Values
            .Where(user => string.IsNullOrWhiteSpace(user.Username) == false)
            .ToDictionary(user => user.Username, StringComparer.OrdinalIgnoreCase);

        var insertedUsers = 0;
        var updatedUsers = 0;

        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);

        foreach (var legacyUser in legacyUsers.Where(user => user.IsDeleted == false && user.Id != Guid.Empty))
        {
            var username = GetUsername(legacyUser);
            if (string.IsNullOrWhiteSpace(username))
            {
                throw new InvalidOperationException(
                    $"Seed user '{legacyUser.Id}' in module '{_databaseSettings.Module}' does not define a usable Login or Name.");
            }

            if (existingUsers.TryGetValue(legacyUser.Id, out var existingUser))
            {
                if (!string.Equals(existingUser.Username, username, StringComparison.OrdinalIgnoreCase) &&
                    existingUsersByUsername.TryGetValue(username, out var conflictingUser) &&
                    conflictingUser.Id != existingUser.Id)
                {
                    throw new InvalidOperationException(
                        $"Seed user '{legacyUser.Id}' attempted to use username '{username}', but it already belongs to '{conflictingUser.Id}'.");
                }

                var previousUsername = existingUser.Username;
                existingUser.Username = username;

                if (existingUser.CreatedUtc == default)
                {
                    existingUser.CreatedUtc = NormalizeDate(legacyUser.CreatedAt);
                }

                if (string.IsNullOrWhiteSpace(existingUser.PinHash) || string.IsNullOrWhiteSpace(existingUser.PinSalt))
                {
                    var (seedHash, seedSalt, seedTerminalId) = CreateCredentials(username);
                    existingUser.PinHash = seedHash;
                    existingUser.PinSalt = seedSalt;
                    existingUser.TerminalId = existingUser.TerminalId == Guid.Empty
                        ? seedTerminalId
                        : existingUser.TerminalId;
                }

                updatedUsers++;
                if (string.IsNullOrWhiteSpace(previousUsername) == false)
                {
                    existingUsersByUsername.Remove(previousUsername);
                }

                existingUsersByUsername[username] = existingUser;
                continue;
            }

            if (existingUsersByUsername.TryGetValue(username, out var existingUserByUsername))
            {
                if (existingUserByUsername.CreatedUtc == default)
                {
                    existingUserByUsername.CreatedUtc = NormalizeDate(legacyUser.CreatedAt);
                }

                if (string.IsNullOrWhiteSpace(existingUserByUsername.PinHash) || string.IsNullOrWhiteSpace(existingUserByUsername.PinSalt))
                {
                    var (seedHash, seedSalt, seedTerminalId) = CreateCredentials(username);
                    existingUserByUsername.PinHash = seedHash;
                    existingUserByUsername.PinSalt = seedSalt;
                    existingUserByUsername.TerminalId = existingUserByUsername.TerminalId == Guid.Empty
                        ? seedTerminalId
                        : existingUserByUsername.TerminalId;
                }

                updatedUsers++;
                continue;
            }

            var (hash, salt, terminalId) = CreateCredentials(username);

            _dbContext.ApiUsers.Add(new ApiUser
            {
                Id = legacyUser.Id,
                TerminalId = terminalId,
                Username = username,
                PinHash = hash,
                PinSalt = salt,
                CreatedUtc = NormalizeDate(legacyUser.CreatedAt)
            });

            existingUsers.Add(legacyUser.Id, new ApiUser
            {
                Id = legacyUser.Id
            });
            existingUsersByUsername[username] = new ApiUser
            {
                Id = legacyUser.Id,
                Username = username
            };
            insertedUsers++;
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        _logger.LogInformation(
            "Processed seed data from {SeedPath} for module {Module}. Validated {CommonRows} shared rows and {ModuleRows} module rows. Inserted {InsertedUserCount} API users and refreshed {UpdatedUserCount} existing users.",
            _databaseSettings.SeedPath,
            _databaseSettings.Module,
            commonSeedRows,
            moduleSeedRows + legacyProfiles.Count + legacyUsers.Count,
            insertedUsers,
            updatedUsers);
    }

    private (string Hash, string Salt, Guid TerminalId) CreateCredentials(string username)
    {
        if (_bootstrapUserSettings.IsConfigured &&
            string.Equals(username, _bootstrapUserSettings.Username, StringComparison.OrdinalIgnoreCase))
        {
            var bootstrapCredentials = _pinHasher.Hash(_bootstrapUserSettings.Pin);
            return (bootstrapCredentials.Hash, bootstrapCredentials.Salt, _bootstrapUserSettings.TerminalId);
        }

        var seedPinMaterial = Guid.NewGuid().ToString("N");
        var seedOnlyCredentials = _pinHasher.Hash(seedPinMaterial);
        return (seedOnlyCredentials.Hash, seedOnlyCredentials.Salt, Guid.Empty);
    }

    private static string GetUsername(LegacySeedUser legacyUser)
    {
        if (string.IsNullOrWhiteSpace(legacyUser.Login) == false)
        {
            return legacyUser.Login.Trim();
        }

        return legacyUser.Name?.Trim() ?? string.Empty;
    }

    private static DateTime NormalizeDate(DateTime value)
    {
        if (value == default)
        {
            return DateTime.UtcNow;
        }

        return value.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(value, DateTimeKind.Utc)
            : value.ToUniversalTime();
    }

    private static void ValidateUserProfileReferences(
        IReadOnlyList<LegacySeedUser> users,
        IReadOnlyList<LegacySeedUserProfile> profiles,
        string module)
    {
        var profileIds = profiles
            .Where(profile => profile.IsDeleted == false && profile.Id != Guid.Empty)
            .Select(profile => profile.Id)
            .ToHashSet();

        if (profileIds.Count == 0)
        {
            return;
        }

        foreach (var user in users.Where(item => item.IsDeleted == false && item.Id != Guid.Empty && item.ProfileId != Guid.Empty))
        {
            if (!profileIds.Contains(user.ProfileId))
            {
                throw new InvalidOperationException(
                    $"Seed user '{user.Id}' references missing profile '{user.ProfileId}' in module '{module}'.");
            }
        }
    }

    private static async Task<int> LoadRequiredCountAsync(string path, string description, CancellationToken cancellationToken)
    {
        var elements = await LoadRequiredAsync<JsonElement>(path, description, cancellationToken);
        return elements.Count;
    }

    private static async Task<IReadOnlyList<T>> LoadRequiredAsync<T>(string path, string description, CancellationToken cancellationToken)
    {
        try
        {
            var json = await File.ReadAllTextAsync(path, cancellationToken);
            var items = JsonSerializer.Deserialize<List<T>>(json, SerializerOptions);
            if (items is null)
            {
                throw new InvalidOperationException($"The seed file '{path}' did not contain a valid JSON array for {description}.");
            }

            return items;
        }
        catch (Exception exception) when (exception is IOException or JsonException or NotSupportedException)
        {
            throw new InvalidOperationException(
                $"Failed to load {description} from '{path}'. Check the seed file contents and deployment layout.",
                exception);
        }
    }

    private sealed class LegacySeedUser
    {
        public Guid Id { get; set; }
        public Guid ProfileId { get; set; }
        public string? Name { get; set; }
        public string? Login { get; set; }
        public bool IsDeleted { get; set; }
        public DateTime CreatedAt { get; set; }
    }

    private sealed class LegacySeedUserProfile
    {
        public Guid Id { get; set; }
        public bool IsDeleted { get; set; }
    }
}
