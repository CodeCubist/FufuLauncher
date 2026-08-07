/*
Copyright (c) FufuLauncher Dev Team. All rights reserved.
Licensed under the MIT License.
*/
using FufuLauncher.Models.MiHoYo.Identity;
using MihoyoBBS;

namespace FufuLauncher.Contracts.Services;

public interface IHoyoverseCheckinService
{
    Task<List<string>> GetBoundUidsAsync(Dictionary<string, string> cookies, string serverType);
    Task<(string status, string summary)> GetCheckinStatusAsync(string targetUid, Dictionary<string, string> cookies, string serverType);
    Task<(bool success, string message)> ExecuteCheckinAsync(string targetUid, Dictionary<string, string> cookies, string serverType);
    Task<CheckinCalendarData?> GetCalendarDataAsync(Dictionary<string, string> cookies, string serverType);

    // 新 ctx 入口：推荐使用
    Task<List<string>> GetBoundUidsAsync(AccountContext ctx, string serverType);
    Task<(string status, string summary)> GetCheckinStatusAsync(string targetUid, AccountContext ctx, string serverType);
    Task<(bool success, string message)> ExecuteCheckinAsync(string targetUid, AccountContext ctx, string serverType);
    Task<CheckinCalendarData?> GetCalendarDataAsync(AccountContext ctx, string serverType);
}