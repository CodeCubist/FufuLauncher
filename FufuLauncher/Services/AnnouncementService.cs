using System.Text.Json;
using FufuLauncher.Contracts.Services;
using FufuLauncher.Models;

namespace FufuLauncher.Services;

public class AnnouncementService : IAnnouncementService
{
    private const string ApiUrl = "https://philia093.cyou/announcement.json";
    private const string CacheFileName = "announcement_cache.txt";
    private readonly string _cacheFilePath;
    private readonly HttpClient _httpClient;

    public AnnouncementService()
    {
        _cacheFilePath = Path.Combine(AppContext.BaseDirectory, CacheFileName);
        _httpClient = new HttpClient();
        _httpClient.Timeout = TimeSpan.FromSeconds(10);
    }

    public async Task<string?> CheckForNewAnnouncementAsync()
    {
        try
        {
            var json = await _httpClient.GetStringAsync(ApiUrl);
            var data = JsonSerializer.Deserialize<AnnouncementData>(json);

            if (data == null || string.IsNullOrEmpty(data.Info))
            {
                return null;
            }

            var remoteUrl = data.Info;
            
            string localUrl = string.Empty;
            if (File.Exists(_cacheFilePath))
            {
                localUrl = await File.ReadAllTextAsync(_cacheFilePath);
            }
            
            if (!string.Equals(remoteUrl, localUrl, StringComparison.OrdinalIgnoreCase))
            {
                await File.WriteAllTextAsync(_cacheFilePath, remoteUrl);
                return remoteUrl;
            }

            return null;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AnnouncementService] Error: {ex.Message}");
            return null;
        }
    }
}