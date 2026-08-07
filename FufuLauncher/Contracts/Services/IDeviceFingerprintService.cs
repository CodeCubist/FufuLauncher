using FufuLauncher.Models.MiHoYo.Fingerprint;

namespace FufuLauncher.Contracts.Services;

//获取指纹
public interface IDeviceFingerprintService
{
    // 获取或注册设备指纹，按 accountId 来；注册时 device_id/seed_id/seed_time 全部随机生成
    // 返回整个 getFp 请求体（含注册成功的 device_fp），便于调用方统一使用
    Task<DeviceFpRequest> GetOrRegisterFingerprintAsync(string accountId, Dictionary<string, string> cookies);

    // 获取当前活跃账号的完整请求体
    DeviceFpRequest? GetCurrentFingerprint(string accountId);

    // 登录窗口等场景的独立注册：只返回请求体，不写入/清空服务的内存缓存（不污染当前账号缓存）
    Task<DeviceFpRequest> RegisterStandaloneAsync();

    // 取最近一次注册的 WebView 体系（hk4e_cn）指纹；登录窗口预注册后随账号一起保存（照 bbs_cn 的 Fingerprint 段）
    DeviceFpRequest? GetCurrentWebFingerprint();

    //强制重新注册指纹
    Task ResetFingerprintAsync(string accountId);

    // 用账号已保存的指纹请求头发起更新（device_id/seed_id/seed_time/device_fp 复用，ext_fields 重新构造）
    // 无已保存指纹或更新失败返回 null；登录/激活账号成功后自动调用
    Task<DeviceFpRequest?> UpdateFingerprintAsync(string accountId);
}
