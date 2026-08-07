/*
Copyright (c) FufuLauncher Dev Team. All rights reserved.
Licensed under the MIT License.
*/
using FufuLauncher.Models.MiHoYo.Identity;

namespace FufuLauncher.Contracts.Services;

public interface IDailyNoteService
{
    Task<FufuLauncher.Services.DailyNoteCardData> GetDailyNoteAsync(string roleId, string server);
    Task<FufuLauncher.Services.DailyNoteCardData> GetDailyNoteAsync(AccountContext ctx, string roleId, string server);
}