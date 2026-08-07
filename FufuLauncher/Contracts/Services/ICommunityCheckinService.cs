/*
Copyright (c) FufuLauncher Dev Team. All rights reserved.
Licensed under the MIT License.
*/
using FufuLauncher.Models;
using FufuLauncher.Models.MiHoYo.Identity;

namespace FufuLauncher.Contracts.Services;

public interface ICommunityCheckinService
{
    Task<CheckinTypeResult> ExecuteCheckinAsync(AccountCredentials account, bool signEnabled, bool readEnabled, bool likeEnabled, bool shareEnabled);
    Task<CheckinTypeResult> ExecuteCheckinAsync(AccountContext ctx, string uid, string nickname, bool signEnabled, bool readEnabled, bool likeEnabled, bool shareEnabled);
}
