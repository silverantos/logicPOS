using LogicPOS.ApiServer.Authentication;
using LogicPOS.ApiServer.Data;
using LogicPOS.ApiServer.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace LogicPOS.ApiServer.Services;

public sealed class BootstrapUserSeeder
{
    private readonly ApplicationDbContext _dbContext;
    private readonly BootstrapUserSettings _bootstrapUserSettings;
    private readonly PinHasher _pinHasher;

    public BootstrapUserSeeder(
        ApplicationDbContext dbContext,
        IOptions<BootstrapUserSettings> bootstrapUserOptions,
        PinHasher pinHasher)
    {
        _dbContext = dbContext;
        _bootstrapUserSettings = bootstrapUserOptions.Value;
        _pinHasher = pinHasher;
    }

    public async Task SeedAsync(CancellationToken cancellationToken = default)
    {
        if (!_bootstrapUserSettings.IsConfigured)
        {
            return;
        }

        var normalizedUsername = _bootstrapUserSettings.Username.Trim();
        var normalizedUsernameLower = normalizedUsername.ToLowerInvariant();
        var existingUser = await _dbContext.ApiUsers
            .Where(user => user.Id == _bootstrapUserSettings.UserId || user.Username.ToLower() == normalizedUsernameLower)
            .ToListAsync(cancellationToken);

        var targetUser = existingUser
            .OrderByDescending(user => user.Id == _bootstrapUserSettings.UserId)
            .FirstOrDefault();

        var (hash, salt) = _pinHasher.Hash(_bootstrapUserSettings.Pin);

        if (targetUser is not null)
        {
            targetUser.TerminalId = _bootstrapUserSettings.TerminalId;
            targetUser.Username = normalizedUsername;
            targetUser.PinHash = hash;
            targetUser.PinSalt = salt;

            if (targetUser.CreatedUtc == default)
            {
                targetUser.CreatedUtc = DateTime.UtcNow;
            }

            await _dbContext.SaveChangesAsync(cancellationToken);
            return;
        }

        _dbContext.ApiUsers.Add(new ApiUser
        {
            Id = _bootstrapUserSettings.UserId,
            TerminalId = _bootstrapUserSettings.TerminalId,
            Username = normalizedUsername,
            PinHash = hash,
            PinSalt = salt,
            CreatedUtc = DateTime.UtcNow
        });

        await _dbContext.SaveChangesAsync(cancellationToken);
    }
}
