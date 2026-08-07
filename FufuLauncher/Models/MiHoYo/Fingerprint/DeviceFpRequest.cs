/*
Copyright (c) FufuLauncher Dev Team. All rights reserved.
Licensed under the MIT License.
*/
using System.Text.Json.Serialization;

namespace FufuLauncher.Models.MiHoYo.Fingerprint;

// getFp 注册请求体：注册成功后整体返回，供各调用方统一使用 device_id/seed_id/seed_time/device_fp
// 属性顺序对应服务端 JSON 字段顺序（device_id / seed_id / seed_time / platform / device_fp / app_name / ext_fields / bbs_device_id），
// System.Text.Json 默认按声明顺序输出，更新指纹时复用同一顺序才能让服务端把请求识别成同一台设备
public sealed record DeviceFpRequest
{
    [JsonPropertyName("device_id")] public string DeviceId { get; set; } = "";
    [JsonPropertyName("seed_id")] public string SeedId { get; set; } = "";
    [JsonPropertyName("seed_time")] public string SeedTime { get; set; } = "";
    [JsonPropertyName("platform")] public string Platform { get; set; } = "";
    [JsonPropertyName("device_fp")] public string DeviceFp { get; set; } = "";
    [JsonPropertyName("app_name")] public string AppName { get; set; } = "";
    [JsonPropertyName("ext_fields")] public string ExtFields { get; set; } = "";
    // bbs_device_id 仅原生体系（platform=2）才有；WebView 体系（platform=5）请求体无此字段，
    // 传 null 时序列化自动忽略（WhenWritingNull），原生传值照常输出
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonPropertyName("bbs_device_id")] public string? BbsDeviceId { get; set; }
}