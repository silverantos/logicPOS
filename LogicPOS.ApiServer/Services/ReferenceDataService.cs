using System.Text.Json;
using LogicPOS.ApiServer.Data;
using LogicPOS.ApiServer.DTOs;

namespace LogicPOS.ApiServer.Services;

public sealed class ReferenceDataService
{
    private static readonly Guid SystemUserId = Guid.Empty;
    private static readonly DateTime CatalogTimestamp = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly IReadOnlyList<CurrencyResponse> FallbackCurrencies =
    [
        new CurrencyResponse
        {
            Id = Guid.Parse("b9a1a510-7d3e-4c7a-902a-70c4c605aa01"),
            Order = 1,
            Code = "EUR",
            Designation = "Euro",
            Acronym = "EUR",
            Symbol = "€",
            Entity = "European Union",
            ExchangeRate = 1m,
            CreatedAt = CatalogTimestamp,
            UpdatedAt = CatalogTimestamp,
            UpdatedBy = SystemUserId
        },
        new CurrencyResponse
        {
            Id = Guid.Parse("b9a1a510-7d3e-4c7a-902a-70c4c605aa02"),
            Order = 2,
            Code = "AOA",
            Designation = "Kwanza",
            Acronym = "AOA",
            Symbol = "Kz",
            Entity = "Angola",
            ExchangeRate = 1m,
            CreatedAt = CatalogTimestamp,
            UpdatedAt = CatalogTimestamp,
            UpdatedBy = SystemUserId
        }
    ];

    private static readonly IReadOnlyList<CountryResponse> FallbackCountries =
    [
        new CountryResponse
        {
            Id = Guid.Parse("c4f6e75b-bb90-4a5b-bb91-fb1f899f0001"),
            Order = 1,
            Code = "PT",
            Designation = "Portugal",
            Code2 = "PT",
            Code3 = "PRT",
            Capital = "Lisbon",
            TLD = ".pt",
            Currency = "Euro",
            CurrencyCode = "EUR",
            FiscalNumberRegex = "^[0-9]{9,}$",
            ZipCodeRegex = @"^\d{4}-\d{3}$",
            CreatedAt = CatalogTimestamp,
            UpdatedAt = CatalogTimestamp,
            UpdatedBy = SystemUserId
        },
        new CountryResponse
        {
            Id = Guid.Parse("c4f6e75b-bb90-4a5b-bb91-fb1f899f0002"),
            Order = 2,
            Code = "AO",
            Designation = "Angola",
            Code2 = "AO",
            Code3 = "AGO",
            Capital = "Luanda",
            TLD = ".ao",
            Currency = "Kwanza",
            CurrencyCode = "AOA",
            FiscalNumberRegex = "^[0-9A-Z]{9,}$",
            ZipCodeRegex = string.Empty,
            CreatedAt = CatalogTimestamp,
            UpdatedAt = CatalogTimestamp,
            UpdatedBy = SystemUserId
        }
    ];

    private readonly IReadOnlyList<CountryResponse> _countries;
    private readonly IReadOnlyList<CurrencyResponse> _currencies;

    public ReferenceDataService(
        ResolvedDatabaseSettings databaseSettings,
        ILogger<ReferenceDataService> logger)
    {
        (_countries, _currencies) = LoadReferenceData(databaseSettings, logger);
    }

    public IReadOnlyList<CountryResponse> GetCountries()
    {
        return _countries;
    }

    public IReadOnlyList<CurrencyResponse> GetCurrencies()
    {
        return _currencies;
    }

    public CurrencyResponse? GetCurrencyByCode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return null;
        }

        return _currencies.FirstOrDefault(currency =>
                   string.Equals(currency.Code, code, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(currency.Acronym, code, StringComparison.OrdinalIgnoreCase));
    }

    private static (IReadOnlyList<CountryResponse> Countries, IReadOnlyList<CurrencyResponse> Currencies) LoadReferenceData(
        ResolvedDatabaseSettings databaseSettings,
        ILogger<ReferenceDataService> logger)
    {
        var countriesPath = databaseSettings.GetSeedFile("countries.json");
        var currenciesPath = databaseSettings.GetSeedFile("currencies.json");

        if (!File.Exists(countriesPath) || !File.Exists(currenciesPath))
        {
            if (databaseSettings.RequireSeedFiles)
            {
                throw new InvalidOperationException(
                   $"Reference seed files were expected under '{databaseSettings.SeedPath}' but countries.json and/or currencies.json were missing.");
            }

            return (FallbackCountries, FallbackCurrencies);
        }

        try
        {
            var countries = JsonSerializer.Deserialize<List<CountryResponse>>(File.ReadAllText(countriesPath), SerializerOptions);
            var currencies = JsonSerializer.Deserialize<List<CurrencyResponse>>(File.ReadAllText(currenciesPath), SerializerOptions);

            if (countries is null || currencies is null)
            {
                throw new InvalidOperationException("The reference seed files did not contain valid JSON arrays.");
            }

            logger.LogInformation(
                "Loaded {CountryCount} countries and {CurrencyCount} currencies from {SeedPath}.",
                countries.Count,
                currencies.Count,
                databaseSettings.SeedPath);

            return (countries, currencies);
        }
        catch (Exception exception) when (exception is IOException or JsonException or NotSupportedException)
        {
            if (databaseSettings.RequireSeedFiles)
            {
                throw new InvalidOperationException(
                   $"Failed to load reference data from '{databaseSettings.SeedPath}'.",
                   exception);
            }

            logger.LogWarning(
                exception,
                "Falling back to in-memory reference data because the deployed seed files under {SeedPath} could not be read.",
                databaseSettings.SeedPath);

            return (FallbackCountries, FallbackCurrencies);
        }
    }
}
