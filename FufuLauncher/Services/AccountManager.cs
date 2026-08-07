/*
Copyright (c) FufuLauncher Dev Team. All rights reserved.
Licensed under the MIT License.
*/
using System.Diagnostics;
using System.Text.Encodings.Web;
using System.Text.Json;
using FufuLauncher.Contracts.Services;
using FufuLauncher.Models;
using FufuLauncher.Models.MiHoYo.Fingerprint;
using Microsoft.Extensions.DependencyInjection;
using MihoyoBBS;

namespace FufuLauncher.Services;

public class AccountManager
{

    private string DataDir => Helpers.AppPaths.DataDir;
    private string CookiesDir => Path.Combine(DataDir, "cookies");
    private string AccountsFilePath => Path.Combine(DataDir, "accounts.json");
    private readonly SemaphoreSlim _lock = new(1, 1);
    private static readonly JsonSerializerOptions _cookieJsonOptions = new()
    {
        WriteIndented = true,
        // ext_fields 是嵌套 JSON 字符串，默认 encoder 会把内部 " 转成 "，导致重启后发出去的请求体里嵌着转义引号
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    // cookie 文件结构化存储：cookies + 登录时注册的设备指纹（完整 getFp 请求体）；Fingerprint=原生体系、FingerprintWeb=WebView 体系（DEVICEFP 系）
    private sealed class CookieFile
    {
        public Dictionary<string, string>? Cookies { get; set; }
        public DeviceFpRequest? Fingerprint { get; set; }
        public DeviceFpRequest? FingerprintWeb { get; set; }
    }

    private AccountList _accountList;
    private string? _activeAccountId;
    public string? ActiveAccountId => _activeAccountId;
    public AccountManager()
    {
        try
        {
            Directory.CreateDirectory(CookiesDir);
        }
        catch (UnauthorizedAccessException ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AccountManager] 无法创建 cookies 目录: {ex.Message}");
        }
        catch (IOException ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AccountManager] 创建 cookies 目录时发生IO异常: {ex.Message}");
        }
        _accountList = new AccountList();
    }

    public async Task InitializeAsync()
    {
        await LoadAccountListAsync();
        
        if (HasLegacyAccounts())
        {
            await MigrateLegacyAccountsAsync();
        }
    }



    public AccountEntry GetActiveAccountEntry() =>
        _accountList.Accounts.FirstOrDefault(a => a.Id == _activeAccountId);

    public List<AccountEntry> GetAllAccounts() => _accountList.Accounts;

  
    private async Task LoadAccountListAsync()
    {
        if (File.Exists(AccountsFilePath))
        {
            try
            {
                var json = await File.ReadAllTextAsync(AccountsFilePath);
                _accountList = JsonSerializer.Deserialize<AccountList>(json) ?? new AccountList();
            }
            catch (JsonException ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[AccountManager] accounts.json 解析失败，将重置账号列表: {ex.Message}");
                try
                {
                    var backupPath = AccountsFilePath + $".corrupt.{DateTime.Now:yyyyMMddHHmmss}.bak";
                    File.Copy(AccountsFilePath, backupPath, overwrite: true);
                }
                catch { }
                _accountList = new AccountList();
            }
        }
        else
        {
            _accountList = new AccountList();
        }

        var normalizedAccounts = _accountList.Accounts
            .Where(a => !string.IsNullOrWhiteSpace(a?.Id))
            .GroupBy(a => a.Id)
            .Select(g => g.Last())
            .ToList();

        if (normalizedAccounts.Count != _accountList.Accounts.Count)
        {
            _accountList.Accounts = normalizedAccounts;
            await SaveAccountListAsync();
        }
        else
        {
            _accountList.Accounts = normalizedAccounts;
        }

        var settings = App.GetService<ILocalSettingsService>();
        string? savedId = null;
        try
        {
            var savedObj = await settings.ReadSettingAsync("ActiveAccountId");
            savedId = savedObj as string;
        }
        catch
        {
            // ignore
        }
        // 走 SetActiveAccountIdAsync 触发 ActiveAccountChanged 事件，让订阅方（App 启动 fp 注册/更新）有机会生效
        await SetActiveAccountIdAsync(savedId ?? _accountList.Accounts.FirstOrDefault()?.Id);
    }
    /// <summary>
    /// 活跃账号变更事件：仅在切换到非空账号时触发（启动加载 / 主动切号 / 删除后选下一个账号）；
    /// App 订阅此事件以触发 <see cref="Contracts.Services.IDeviceFingerprintService"/> 注册/更新指纹。
    /// </summary>
    public event Func<string, Task>? ActiveAccountChanged;

    public async Task SetActiveAccountIdAsync(string? accountId)
    {
        var previousId = _activeAccountId;
        _activeAccountId = accountId;
        var settings = App.GetService<ILocalSettingsService>();
        if (settings != null)
            await settings.SaveSettingAsync("ActiveAccountId", accountId ?? string.Empty);

        // 仅在切换到非空账号时通知订阅方；fire-and-forget，避免阻塞设置 / 切号路径
        if (!string.IsNullOrEmpty(accountId) && accountId != previousId)
        {
            var handler = ActiveAccountChanged;
            if (handler != null)
            {
                _ = Task.Run(async () =>
                {
                    try { await handler(accountId); }
                    catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[AccountManager] ActiveAccountChanged 处理器异常: {ex.Message}"); }
                });
            }
        }
    }
    public async Task LogoutAsync()
    {
        await SetActiveAccountIdAsync(null);
    }
    private async Task SaveAccountListAsync()
    {
        var json = JsonSerializer.Serialize(_accountList, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(AccountsFilePath, json);
    }


    public async Task<AccountEntry> AddAccountAsync(
        Dictionary<string, string> cookies, string serverType, string nickname = "",
        DeviceFpRequest? fingerprint = null, DeviceFpRequest? webFingerprint = null)
    {
        await _lock.WaitAsync();
        try
        {
            string stuid = ExtractStuid(cookies, serverType);
            string id = $"{serverType}_{stuid}";

            var existingEntry = _accountList.Accounts.FirstOrDefault(a => a.Id == id);
            if (existingEntry != null)
            {
                string existingCookiePath = Path.Combine(CookiesDir, existingEntry.CookieFilePath);
                // 未传新指纹时保留原有 Fingerprint / FingerprintWeb，避免覆盖清空；
                // 旧文件缺失或损坏时不中断登录，按无指纹保留并记录日志
                CookieFile? existingFile = null;
                if (File.Exists(existingCookiePath))
                {
                    try
                    {
                        existingFile = JsonSerializer.Deserialize<CookieFile>(await File.ReadAllTextAsync(existingCookiePath));
                    }
                    catch (JsonException ex)
                    {
                        System.Diagnostics.Debug.WriteLine(
                            $"[AccountManager] 旧 cookie 文件解析失败 ({existingEntry.CookieFilePath})，按无指纹保留: {ex.Message}");
                    }
                }
                var existingCookieJson = JsonSerializer.Serialize(new CookieFile
                {
                    Cookies = cookies,
                    Fingerprint = fingerprint ?? existingFile?.Fingerprint,
                    FingerprintWeb = webFingerprint ?? existingFile?.FingerprintWeb
                }, _cookieJsonOptions);
                await File.WriteAllTextAsync(existingCookiePath, existingCookieJson);
                existingEntry.LastLoginTime = DateTime.Now;
                if (!string.IsNullOrWhiteSpace(nickname))
                    existingEntry.Nickname = nickname;
                await SaveAccountListAsync();
                return existingEntry;
            }

            string cookieFileName = $"{id}.json";
            string cookiePath = Path.Combine(CookiesDir, cookieFileName);
            var cookieJson = JsonSerializer.Serialize(new CookieFile { Cookies = cookies, Fingerprint = fingerprint, FingerprintWeb = webFingerprint }, _cookieJsonOptions);
            await File.WriteAllTextAsync(cookiePath, cookieJson);

            var entry = new AccountEntry
            {
                Id = id,
                Stuid = stuid,
                Nickname = nickname,
                ServerType = serverType,
                CookieFilePath = cookieFileName,
                LastLoginTime = DateTime.Now
            };

            _accountList.Accounts.Add(entry);
            await SaveAccountListAsync();
            return entry;
        }
        finally
        {
            _lock.Release();
        }
    }


    public async Task<Dictionary<string, string>> LoadCookiesAsync(string accountId)
    {
        var entry = _accountList.Accounts.FirstOrDefault(a => a.Id == accountId);
        if (entry == null) return null;

        string path = Path.Combine(CookiesDir, entry.CookieFilePath);
        if (!File.Exists(path)) return null;

        try
        {
            var json = await File.ReadAllTextAsync(path);
            // 新结构：{ cookies: {...}, fingerprint: {...} }；旧结构：纯 cookies 字典
            var cookieFile = JsonSerializer.Deserialize<CookieFile>(json);
            if (cookieFile?.Cookies != null)
                return cookieFile.Cookies;
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json);
        }
        catch (JsonException ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[AccountManager] Cookie 文件解析失败 ({entry.CookieFilePath}): {ex.Message}");
            return null;
        }
    }

    // 读取登录时持久化的设备指纹（完整 getFp 请求体）；旧格式（纯 cookies 字典）无指纹，返回 null
    public async Task<DeviceFpRequest?> LoadFingerprintAsync(string accountId)
    {
        var entry = _accountList.Accounts.FirstOrDefault(a => a.Id == accountId);
        if (entry == null) return null;

        string path = Path.Combine(CookiesDir, entry.CookieFilePath);
        if (!File.Exists(path)) return null;

        try
        {
            var json = await File.ReadAllTextAsync(path);
            var cookieFile = JsonSerializer.Deserialize<CookieFile>(json);
            return cookieFile?.Fingerprint;
        }
        catch (JsonException ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[AccountManager] 指纹解析失败 ({entry.CookieFilePath}): {ex.Message}");
            return null;
        }
    }

    // 仅更新 cookie 文件中的 fingerprint 段（指纹更新后写回），保留原有 cookies
    public async Task SaveFingerprintAsync(string accountId, DeviceFpRequest fingerprint)
    {
        await _lock.WaitAsync();
        try
        {
            var entry = _accountList.Accounts.FirstOrDefault(a => a.Id == accountId);
            if (entry == null) return;

            string path = Path.Combine(CookiesDir, entry.CookieFilePath);
            Dictionary<string, string>? cookies = null;
            DeviceFpRequest? fingerprintWeb = null;
            if (File.Exists(path))
            {
                try
                {
                    var json = await File.ReadAllTextAsync(path);
                    var cookieFile = JsonSerializer.Deserialize<CookieFile>(json);
                    cookies = cookieFile?.Cookies ?? JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                    fingerprintWeb = cookieFile?.FingerprintWeb;
                }
                catch (JsonException ex)
                {
                    // 解析失败不清空原文件：中止写入，保留现场供排查
                    Debug.WriteLine($"[AccountManager] Cookie 文件解析失败 ({entry.CookieFilePath})，中止指纹写入: {ex.Message}");
                    return;
                }
            }
            var newJson = JsonSerializer.Serialize(new CookieFile { Cookies = cookies, Fingerprint = fingerprint, FingerprintWeb = fingerprintWeb }, _cookieJsonOptions);
            await File.WriteAllTextAsync(path, newJson);
        }
        finally
        {
            _lock.Release();
        }
    }

    // 仅更新 cookie 文件中的 WebView 指纹段（DEVICEFP 系），保留原有 cookies 与原生 Fingerprint
    public async Task SaveWebFingerprintAsync(string accountId, DeviceFpRequest fingerprint)
    {
        await _lock.WaitAsync();
        try
        {
            var entry = _accountList.Accounts.FirstOrDefault(a => a.Id == accountId);
            if (entry == null) return;

            string path = Path.Combine(CookiesDir, entry.CookieFilePath);
            Dictionary<string, string>? cookies = null;
            DeviceFpRequest? fingerprintNative = null;
            if (File.Exists(path))
            {
                try
                {
                    var json = await File.ReadAllTextAsync(path);
                    var cookieFile = JsonSerializer.Deserialize<CookieFile>(json);
                    cookies = cookieFile?.Cookies ?? JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                    fingerprintNative = cookieFile?.Fingerprint;
                }
                catch (JsonException ex)
                {
                    // 解析失败不清空原文件：中止写入，保留现场供排查
                    Debug.WriteLine($"[AccountManager] Cookie 文件解析失败 ({entry.CookieFilePath})，中止 Web 指纹写入: {ex.Message}");
                    return;
                }
            }
            var newJson = JsonSerializer.Serialize(new CookieFile { Cookies = cookies, Fingerprint = fingerprintNative, FingerprintWeb = fingerprint }, _cookieJsonOptions);
            await File.WriteAllTextAsync(path, newJson);
        }
        finally
        {
            _lock.Release();
        }
    }

    // 读取 cookie 文件中的 WebView 指纹段（DEVICEFP 系）；无则返回 null
    public async Task<DeviceFpRequest?> LoadWebFingerprintAsync(string accountId)
    {
        var entry = _accountList.Accounts.FirstOrDefault(a => a.Id == accountId);
        if (entry == null) return null;

        string path = Path.Combine(CookiesDir, entry.CookieFilePath);
        if (!File.Exists(path)) return null;
        try
        {
            var json = await File.ReadAllTextAsync(path);
            var cookieFile = JsonSerializer.Deserialize<CookieFile>(json);
            return cookieFile?.FingerprintWeb;
        }
        catch (JsonException ex)
        {
            Debug.WriteLine($"[AccountManager] WebView 指纹解析失败 ({entry.CookieFilePath}): {ex.Message}");
            return null;
        }
    }


    public async Task DeleteAccountAsync(string accountId)
    {
        await _lock.WaitAsync();
        try
        {
            var entry = _accountList.Accounts.FirstOrDefault(a => a.Id == accountId);
            if (entry == null) return;

            string path = Path.Combine(CookiesDir, entry.CookieFilePath);
            if (File.Exists(path)) File.Delete(path);

            _accountList.Accounts.Remove(entry);
            await SaveAccountListAsync();

            if (_activeAccountId == accountId)
            {
                var next = _accountList.Accounts.FirstOrDefault();
                await SetActiveAccountIdAsync(next?.Id);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

   
    public async Task<bool> SwitchAccountAsync(string accountId)
    {
        if (_accountList.Accounts.All(a => a.Id != accountId)) return false;
        await SetActiveAccountIdAsync(accountId);

        var entry = GetActiveAccountEntry();
        if (entry != null)
        {
            entry.LastLoginTime = DateTime.Now;
            await SaveAccountListAsync();
        }
        return true;
    }

   
    public async Task UpdateAccountMetaAsync(string accountId, string nickname, string avatarUrl, string gameUid = "")
    {
        await _lock.WaitAsync();
        try
        {
            var entry = _accountList.Accounts.FirstOrDefault(a => a.Id == accountId);
            if (entry != null)
            {
                entry.Nickname = nickname;
                entry.AvatarUrl = avatarUrl;
                if (!string.IsNullOrEmpty(gameUid))
                    entry.GameUid = gameUid;
                await SaveAccountListAsync();
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    
    private string ExtractStuid(Dictionary<string, string> cookies, string serverType)
    {
        if (serverType == "cn")
        {
            if (cookies.TryGetValue("ltuid", out var ltuid)) return ltuid;
            if (cookies.TryGetValue("stuid", out var stuid)) return stuid;
        }
        else
        {
            if (cookies.TryGetValue("ltuid_v2", out var ltuidV2)) return ltuidV2;
        }
        throw new ArgumentException("无法提取账户 ID");
    }
    public async Task UpdateCookiesAsync(string accountId, Dictionary<string, string> newCookies)
    {
        await _lock.WaitAsync();
        try
        {
            var entry = _accountList.Accounts.FirstOrDefault(a => a.Id == accountId);
            if (entry == null) return;

            string cookiePath = Path.Combine(CookiesDir, entry.CookieFilePath);
            // 保留原有 fingerprint 与 FingerprintWeb，只更新 cookies 段
            DeviceFpRequest? fingerprint = null;
            DeviceFpRequest? fingerprintWeb = null;
            if (File.Exists(cookiePath))
            {
                try
                {
                    var existing = JsonSerializer.Deserialize<CookieFile>(await File.ReadAllTextAsync(cookiePath));
                    fingerprint = existing?.Fingerprint;
                    fingerprintWeb = existing?.FingerprintWeb;
                }
                catch (JsonException ex)
                {
                    // 解析失败不清空原文件：中止写入，保留现场供排查
                    Debug.WriteLine($"[AccountManager] Cookie 文件解析失败 ({entry.CookieFilePath})，中止 cookie 更新: {ex.Message}");
                    return;
                }
            }
            var json = JsonSerializer.Serialize(new CookieFile { Cookies = newCookies, Fingerprint = fingerprint, FingerprintWeb = fingerprintWeb }, _cookieJsonOptions);
            await File.WriteAllTextAsync(cookiePath, json);
        }
        finally
        {
            _lock.Release();
        }
    }

    #region 旧账号数据迁移

    private static Dictionary<string, string> ParseCookieString(string cookieString)
    {
        var cookieDict = new Dictionary<string, string>();
        if (string.IsNullOrWhiteSpace(cookieString))
            return cookieDict;

        var parts = cookieString.Split(';');
        foreach (var part in parts)
        {
            var trimmed = part.Trim();
            if (string.IsNullOrEmpty(trimmed))
                continue;

            var separatorIndex = trimmed.IndexOf('=');
            if (separatorIndex > 0)
            {
                var key = trimmed.Substring(0, separatorIndex).Trim();
                var value = trimmed.Substring(separatorIndex + 1).Trim();
                if (!string.IsNullOrEmpty(key))
                    cookieDict[key] = value;
            }
        }
        return cookieDict;
    }

    private bool HasLegacyAccounts()
    {
        if (!Directory.Exists(DataDir))
            return false;

        var configFiles = Directory.GetFiles(DataDir, "config*.json")
            .Where(f => !Path.GetFileName(f).Equals("accounts.json", StringComparison.OrdinalIgnoreCase))
            .ToList();

        return configFiles.Count > 0;
    }

    private static string DetermineServerTypeByFileName(string fileName)
    {
        return fileName.Contains(".lab", StringComparison.OrdinalIgnoreCase) ? "os" : "cn";
    }

    private async Task MigrateLegacyAccountsAsync()
    {
        System.Diagnostics.Debug.WriteLine("[AccountManager] 开始迁移旧账号数据...");

        try
        {
            var subAccountFiles = new List<string>();
            if (Directory.Exists(DataDir))
            {
                subAccountFiles.AddRange(
                    Directory.GetFiles(DataDir, "config*.json")
                        .Where(f =>
                        {
                            var name = Path.GetFileName(f);
                            return !name.Equals("config.json", StringComparison.OrdinalIgnoreCase) &&
                                   !name.Equals("config.lab.json", StringComparison.OrdinalIgnoreCase) &&
                                   !name.Equals("accounts.json", StringComparison.OrdinalIgnoreCase);
                        })
                );
            }

            bool hasOnlyMainConfig = subAccountFiles.Count == 0;

            var processed = new HashSet<string>();
            var migratedConfigFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int migratedCount = 0;

            foreach (var configFile in subAccountFiles)
            {
                try
                {
                    var fileName = Path.GetFileName(configFile);
                    var json = await File.ReadAllTextAsync(configFile);
                    var config = JsonSerializer.Deserialize<Config>(json,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                    if (config?.Account == null || string.IsNullOrWhiteSpace(config.Account.Cookie))
                        continue;

                    var cookieDict = ParseCookieString(config.Account.Cookie);
                    if (cookieDict.Count == 0)
                        continue;

                    string stuid = config.Account.Stuid;
                    if (string.IsNullOrWhiteSpace(stuid))
                    {
                        if (cookieDict.TryGetValue("ltuid", out var ltuid))
                            stuid = ltuid;
                        else if (cookieDict.TryGetValue("ltuid_v2", out var ltuidV2))
                            stuid = ltuidV2;
                    }

                    if (string.IsNullOrWhiteSpace(stuid))
                        continue;

                    if (processed.Contains(stuid))
                    {
                        migratedConfigFiles.Add(configFile);
                        continue;
                    }

                    string serverType = DetermineServerTypeByFileName(fileName);
                    string accountId = $"{serverType}_{stuid}";

                    if (_accountList.Accounts.Any(a => a.Id == accountId))
                    {
                        System.Diagnostics.Debug.WriteLine($"[AccountManager] 账号 {accountId} 已存在，确认迁移完成");
                        processed.Add(stuid);
                        migratedConfigFiles.Add(configFile);
                        continue;
                    }

                    string cookieFileName = $"{accountId}.json";
                    string cookiePath = Path.Combine(CookiesDir, cookieFileName);
                    var cookieJson = JsonSerializer.Serialize(cookieDict);
                    await File.WriteAllTextAsync(cookiePath, cookieJson);

                    var entry = new AccountEntry
                    {
                        Id = accountId,
                        Stuid = stuid,
                        ServerType = serverType,
                        CookieFilePath = cookieFileName,
                        Nickname = config.Display?.Nickname ?? "",
                        AvatarUrl = config.Display?.AvatarUrl ?? "",
                        GameUid = config.Display?.GameUid ?? "",
                        LastLoginTime = DateTime.Now
                    };

                    _accountList.Accounts.Add(entry);
                    processed.Add(stuid);
                    migratedConfigFiles.Add(configFile);
                    migratedCount++;

                    // 迁移云游戏凭证到 LocalSettings
                    var cloudToken = config.Account.CloudComboToken;
                    if (!string.IsNullOrWhiteSpace(cloudToken))
                    {
                        try
                        {
                            var settings = App.GetService<ILocalSettingsService>();
                            await settings.SaveSettingAsync($"CloudComboToken_{stuid}", cloudToken);
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine(
                                $"[AccountManager] 迁移云游戏凭证失败: {ex.Message}");
                        }
                    }

                    System.Diagnostics.Debug.WriteLine(
                        $"[AccountManager] 已迁移账号: {accountId} ({entry.Nickname}) [{serverType}]");
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[AccountManager] 迁移文件 {configFile} 失败: {ex.Message}");
                }
            }

            if (migratedCount > 0)
            {
                await SaveAccountListAsync();
                System.Diagnostics.Debug.WriteLine(
                    $"[AccountManager] 迁移完成，共迁移 {migratedCount} 个账号");
            }

            if (migratedCount > 0 || migratedConfigFiles.Count > 0)
            {
                var activeConfigFile = await MigrateActiveAccountAsync();
                if (activeConfigFile != null)
                    migratedConfigFiles.Add(activeConfigFile);
            }
            else if (hasOnlyMainConfig)
            {
                // 没有子账号文件，尝试从主 config.json / config.lab.json 迁移唯一账号
                var settings = App.GetService<ILocalSettingsService>();
                bool isInternationalAccount = false;
                try
                {
                    var isOsObj = await settings.ReadSettingAsync("IsInternationalAccount");
                    isInternationalAccount = isOsObj is bool b && b;
                }
                catch { }

                string mainConfigPath = isInternationalAccount
                    ? Path.Combine(DataDir, "config.lab.json")
                    : Path.Combine(DataDir, "config.json");

                if (File.Exists(mainConfigPath))
                {
                    try
                    {
                        var json = await File.ReadAllTextAsync(mainConfigPath);
                        var config = JsonSerializer.Deserialize<Config>(json,
                            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                        if (config?.Account != null && !string.IsNullOrWhiteSpace(config.Account.Cookie))
                        {
                            var cookieDict = ParseCookieString(config.Account.Cookie);
                            string stuid = config.Account.Stuid;
                            if (string.IsNullOrWhiteSpace(stuid))
                            {
                                if (cookieDict.TryGetValue("ltuid", out var ltuid))
                                    stuid = ltuid;
                                else if (cookieDict.TryGetValue("ltuid_v2", out var ltuidV2))
                                    stuid = ltuidV2;
                            }

                            if (!string.IsNullOrWhiteSpace(stuid) && cookieDict.Count > 0)
                            {
                                string serverType = isInternationalAccount ? "os" : "cn";
                                string accountId = $"{serverType}_{stuid}";

                                string cookieFileName = $"{accountId}.json";
                                string cookiePath = Path.Combine(CookiesDir, cookieFileName);
                                await File.WriteAllTextAsync(cookiePath, JsonSerializer.Serialize(cookieDict));

                                var entry = new AccountEntry
                                {
                                    Id = accountId,
                                    Stuid = stuid,
                                    ServerType = serverType,
                                    CookieFilePath = cookieFileName,
                                    Nickname = config.Display?.Nickname ?? "",
                                    AvatarUrl = config.Display?.AvatarUrl ?? "",
                                    GameUid = config.Display?.GameUid ?? "",
                                    LastLoginTime = DateTime.Now
                                };

                                _accountList.Accounts.Add(entry);
                                await SaveAccountListAsync();
                                await SetActiveAccountIdAsync(accountId);
                                migratedConfigFiles.Add(mainConfigPath);
                                migratedCount = 1;

                                // 迁移云游戏凭证
                                var cloudToken = config.Account.CloudComboToken;
                                if (!string.IsNullOrWhiteSpace(cloudToken))
                                {
                                    try
                                    {
                                        await settings.SaveSettingAsync($"CloudComboToken_{stuid}", cloudToken);
                                    }
                                    catch (Exception ex)
                                    {
                                        System.Diagnostics.Debug.WriteLine(
                                            $"[AccountManager] 迁移云游戏凭证失败: {ex.Message}");
                                    }
                                }

                                System.Diagnostics.Debug.WriteLine(
                                    $"[AccountManager] 已从主配置迁移唯一账号: {accountId} ({entry.Nickname}) [{serverType}]");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine(
                            $"[AccountManager] 从主配置迁移账号失败: {ex.Message}");
                    }
                }

                if (migratedCount == 0)
                    System.Diagnostics.Debug.WriteLine("[AccountManager] 未找到需要迁移的账号");
            }
            else
            {
                System.Diagnostics.Debug.WriteLine("[AccountManager] 未找到需要迁移的账号");
            }

            foreach (var file in migratedConfigFiles)
            {
                try
                {
                    File.Delete(file);
                    System.Diagnostics.Debug.WriteLine(
                        $"[AccountManager] 已清除已迁移的旧配置: {Path.GetFileName(file)}");
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[AccountManager] 清除旧配置 {file} 失败: {ex.Message}");
                }
            }

            System.Diagnostics.Debug.WriteLine(
                $"[AccountManager] 迁移流程结束，共迁移 {migratedCount} 个账号");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AccountManager] 迁移过程发生错误: {ex.Message}");
        }
    }

    private async Task<string?> MigrateActiveAccountAsync()
    {
        try
        {
            var settings = App.GetService<ILocalSettingsService>();

            bool isInternationalAccount = false;
            try
            {
                var isOsObj = await settings.ReadSettingAsync("IsInternationalAccount");
                isInternationalAccount = isOsObj is bool b && b;
            }
            catch { }

            string mainConfigPath = isInternationalAccount
                ? Path.Combine(DataDir, "config.lab.json")
                : Path.Combine(DataDir, "config.json");

            if (!File.Exists(mainConfigPath))
                return null;

            var json = await File.ReadAllTextAsync(mainConfigPath);
            var config = JsonSerializer.Deserialize<Config>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (config?.Account == null || string.IsNullOrWhiteSpace(config.Account.Stuid))
                return null;

            string stuid = config.Account.Stuid;
            string serverType = isInternationalAccount ? "os" : "cn";
            string accountId = $"{serverType}_{stuid}";

            if (_accountList.Accounts.Any(a => a.Id == accountId))
            {
                await SetActiveAccountIdAsync(accountId);
                System.Diagnostics.Debug.WriteLine(
                    $"[AccountManager] 已迁移活跃账号: {accountId}");
                return mainConfigPath;
            }

            System.Diagnostics.Debug.WriteLine(
                $"[AccountManager] 旧活跃账号 {accountId} 不在迁移列表中，使用默认账号");
            return null;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[AccountManager] 迁移活跃账号失败: {ex.Message}");
            return null;
        }
    }

    #endregion

}
