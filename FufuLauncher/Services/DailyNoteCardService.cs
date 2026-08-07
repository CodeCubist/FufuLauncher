/*
Copyright (c) FufuLauncher Dev Team. All rights reserved.
Licensed under the MIT License.
*/
// Copyright (c) FufuLauncher Dev Team. All rights reserved.
// By kyxsan.
// Licensed under the MIT License.

using FufuLauncher.Contracts.Services;
using FufuLauncher.Models.MiHoYo.Identity;

namespace FufuLauncher.Services;

public class DailyNoteCardService
{
    private readonly IDailyNoteService _dailyNoteService;

    public DailyNoteCardService(IDailyNoteService dailyNoteService)
    {
        _dailyNoteService = dailyNoteService;
    }

    public async Task<DailyNoteCardData> LoadCardDataAsync(string roleId, string server, AccountContext ctx)
    {
        return await _dailyNoteService.GetDailyNoteAsync(ctx, roleId, server);
    }
}