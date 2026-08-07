/*
Copyright (c) FufuLauncher Dev Team. All rights reserved.
Licensed under the MIT License.
*/

using FufuLauncher.Models.MiHoYo.Fingerprint;

namespace FufuLauncher.Contracts.Services;

/// <summary>
/// 设备指纹 ext_fields 字段生成器。
/// 实现应保证同一调用方在一次"指纹注册窗口"内尽量稳定，
/// 不同窗口之间存在合理随机化（电量/内存/传感器等）。
/// </summary>
public interface IDeviceExtFieldsBuilder
{
    /// <summary>
    /// 基于设备档案构造一组 ext_fields。
    /// key 集合应与服务端 getExtList 接口返回的字段集合保持兼容。
    /// </summary>
    Dictionary<string, object> Build(DeviceProfile profile);
}
