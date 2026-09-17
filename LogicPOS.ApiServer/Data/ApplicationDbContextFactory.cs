using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;

namespace LogicPOS.ApiServer.Data;

public sealed class ApplicationDbContextFactory : IDesignTimeDbContextFactory<ApplicationDbContext>
{
    public ApplicationDbContext CreateDbContext(string[] args)
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile("databasesettings.json", optional: true)
            .AddEnvironmentVariables()
            .Build();

        var resolvedDatabaseSettings = DatabaseSettingsResolver.Resolve(configuration, Directory.GetCurrentDirectory());

        var optionsBuilder = new DbContextOptionsBuilder<ApplicationDbContext>();
        optionsBuilder.UseSqlite(resolvedDatabaseSettings.ConnectionString);

        return new ApplicationDbContext(optionsBuilder.Options);
    }
}
