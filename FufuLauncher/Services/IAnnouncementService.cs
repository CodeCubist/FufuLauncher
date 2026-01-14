namespace FufuLauncher.Contracts.Services;

public interface IAnnouncementService
{
    Task<string?> CheckForNewAnnouncementAsync();
}