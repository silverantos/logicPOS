using System.Text.Json;
using LogicPOS.ApiServer.Authentication;
using LogicPOS.ApiServer.Data;
using LogicPOS.ApiServer.Data.Entities;
using LogicPOS.ApiServer.DTOs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace LogicPOS.ApiServer.Services;

public sealed class DatabaseSeeder
{
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

        var countries = await LoadRequiredAsync<CountryResponse>(
            _databaseSettings.GetSeedFile("countries.json"),
            "shared countries",
            cancellationToken);

        var currencies = await LoadRequiredAsync<CurrencyResponse>(
            _databaseSettings.GetSeedFile("currencies.json"),
            "shared currencies",
            cancellationToken);

        var legacyUsers = await LoadRequiredAsync<LegacySeedUser>(
            _databaseSettings.GetSeedFile("modules", _databaseSettings.Module, "users.json"),
            $"module '{_databaseSettings.Module}' users",
            cancellationToken);

        var existingUsers = await _dbContext.ApiUsers
            .AsNoTracking()
            .Select(user => new { user.Id, user.Username })
            .ToListAsync(cancellationToken);

        var existingUserIds = existingUsers
            .Select(user => user.Id)
            .ToHashSet();

        var existingUsernames = existingUsers
            .Select(user => user.Username)
            .Where(username => string.IsNullOrWhiteSpace(username) == false)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var insertedUsers = 0;

        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);

        foreach (var legacyUser in legacyUsers.Where(user => user.IsDeleted == false && user.Id != Guid.Empty))
        {
            var username = GetUsername(legacyUser);
            if (string.IsNullOrWhiteSpace(username))
            {
                throw new InvalidOperationException(
                    $"Seed user '{legacyUser.Id}' in module '{_databaseSettings.Module}' does not define a usable Login or Name.");
            }

            if (existingUserIds.Contains(legacyUser.Id) || existingUsernames.Contains(username))
            {
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
                CreatedUtc = legacyUser.CreatedAt == default
                    ? DateTime.UtcNow
                    : legacyUser.CreatedAt.Kind == DateTimeKind.Unspecified
                        ? DateTime.SpecifyKind(legacyUser.CreatedAt, DateTimeKind.Utc)
                        : legacyUser.CreatedAt.ToUniversalTime()
            });

            existingUserIds.Add(legacyUser.Id);
            existingUsernames.Add(username);
            insertedUsers++;
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        _logger.LogInformation(
            "Processed legacy seed data from {SeedPath} for module {Module}. Loaded {CountryCount} countries, {CurrencyCount} currencies, inserted {InsertedUserCount} API users.",
            _databaseSettings.SeedPath,
            _databaseSettings.Module,
            countries.Count,
            currencies.Count,
            insertedUsers);
    }

    private (string Hash, string Salt, Guid TerminalId) CreateCredentials(string username)
    {
        if (_bootstrapUserSettings.IsConfigured &&
            string.Equals(username, _bootstrapUserSettings.Username, StringComparison.OrdinalIgnoreCase))
        {
            var bootstrapCredentials = _pinHasher.Hash(_bootstrapUserSettings.Pin);
            return (bootstrapCredentials.Hash, bootstrapCredentials.Salt, _bootstrapUserSettings.TerminalId);
        }

        var seedOnlyCredentials = _pinHasher.Hash(Guid.NewGuid().ToString("N"));
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
        public string? Name { get; set; }
        public string? Login { get; set; }
        public bool IsDeleted { get; set; }
        public DateTime CreatedAt { get; set; }
    }
}
