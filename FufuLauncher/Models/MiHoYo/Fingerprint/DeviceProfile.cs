/*
Copyright (c) FufuLauncher Dev Team. All rights reserved.
Licensed under the MIT License.
*/

namespace FufuLauncher.Models.MiHoYo.Fingerprint;


public sealed record DeviceProfile(
    string DeviceModel,
    string ProductName,
    string Board,
    string DeviceType,
    string OsVersion,
    string SdkVersion,
    string BuildId,
    string BuildDisplay,
    long BuildTime
);