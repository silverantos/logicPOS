using LogicPOS.ApiServer.DTOs;
using LogicPOS.ApiServer.Data;
using Microsoft.Extensions.Options;

namespace LogicPOS.ApiServer.Services;

public sealed class ApiSystemInformationService
{
    private readonly SystemInformationResponse _systemInformation;
    private readonly DatabaseSettings _databaseSettings;

    public ApiSystemInformationService(
        IOptions<SystemInformationResponse> systemInformation,
        IOptions<DatabaseSettings> databaseSettings)
    {
        _systemInformation = systemInformation.Value;
        _databaseSettings = databaseSettings.Value;
    }

    public SystemInformationResponse GetSystemInformation()
    {
        return new SystemInformationResponse
        {
            Culture = _systemInformation.Culture,
            CountryCode2 = _systemInformation.CountryCode2,
            Module = string.IsNullOrWhiteSpace(_databaseSettings.Module)
                ? _systemInformation.Module
                : _databaseSettings.Module.Trim().ToLowerInvariant()
        };
    }
}
