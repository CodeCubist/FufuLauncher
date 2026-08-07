/*
Copyright (c) FufuLauncher Dev Team. All rights reserved.
Licensed under the MIT License.
*/
namespace FufuLauncher.Contracts.Services;

public interface ITokenRefreshService
{
    Task<Dictionary<string, string>?> RefreshCookieAsync(Dictionary<string, string> currentCookies, bool isManual = false);
}