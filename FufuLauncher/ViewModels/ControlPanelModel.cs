using CommunityToolkit.Mvvm.ComponentModel;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Diagnostics;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FufuLauncher.Models;

namespace FufuLauncher.ViewModels;

public partial class ControlPanelModel : ObservableObject
{
    private const string ServerIp = "127.0.0.1";
    private const int ServerPort = 12345;
    private const string TargetProcessName = "yuanshen";
    private const string TargetProcessNameAlt = "GenshinImpact";

    private UdpClient? _udpClient;
    private IPEndPoint? _remoteEndPoint;
    private readonly string _configPath;
    private bool _isLoaded;
    private CancellationTokenSource _cancellationTokenSource;
    private readonly SemaphoreSlim _socketLock = new(1, 1);
    private bool _isConnected;

    [ObservableProperty]
    private string _connectionStatus = "等待DLL连接...";

    public ControlPanelModel()
    {
        _configPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "fufu", "FufuConfig.cfg");
        _cancellationTokenSource = new CancellationTokenSource();
        
        try 
        {
            _udpClient = new UdpClient();
            _udpClient.Client.ReceiveTimeout = 3000;
            _remoteEndPoint = new IPEndPoint(IPAddress.Parse(ServerIp), ServerPort);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[UDP] Init Error: {ex.Message}");
        }

        _ = StartConnectionLoopAsync(_cancellationTokenSource.Token);
        LoadConfig();
    }

    [ObservableProperty]
    private bool _enableFpsOverride;

    partial void OnEnableFpsOverrideChanged(bool value)
    {
        SendCommand(value ? "enable_fps_override" : "disable_fps_override");
        SaveConfig();
    }

    [ObservableProperty]
    private int _targetFps = 60;

    partial void OnTargetFpsChanged(int value)
    {
        SendCommand($"set_fps {value}");
        SaveConfig();
    }

    [ObservableProperty]
    private bool _enableFovOverride;

    partial void OnEnableFovOverrideChanged(bool value)
    {
        SendCommand(value ? "enable_fov_override" : "disable_fov_override");
        SaveConfig();
    }

    [ObservableProperty]
    private float _targetFov = 45.0f;

    partial void OnTargetFovChanged(float value)
    {
        SendCommand($"set_fov {value}");
        SaveConfig();
    }

    [ObservableProperty]
    private bool _enableFogOverride;

    partial void OnEnableFogOverrideChanged(bool value)
    {
        SendCommand(value ? "enable_display_fog_override" : "disable_display_fog_override");
        SaveConfig();
    }

    [ObservableProperty]
    private bool _enablePerspectiveOverride;

    partial void OnEnablePerspectiveOverrideChanged(bool value)
    {
        SendCommand(value ? "enable_Perspective_override" : "disable_Perspective_override");
        SaveConfig();
    }

    private async void SendCommand(string command)
    {
        if (!_isConnected) return;
        await SendAndReceiveAsync(command);
    }

    private async Task<bool> SendAndReceiveAsync(string command, CancellationToken token = default)
    {
        if (_udpClient == null || _remoteEndPoint == null) return false;

        try
        {
            await _socketLock.WaitAsync(token);
            try
            {
                byte[] data = Encoding.UTF8.GetBytes(command);
                await _udpClient.SendAsync(data, data.Length, _remoteEndPoint);
                Debug.WriteLine($"[UDP] Sent: {command}");

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
                cts.CancelAfter(3000);

                var result = await _udpClient.ReceiveAsync(cts.Token);
                string response = Encoding.UTF8.GetString(result.Buffer);
                Debug.WriteLine($"[UDP] Received: {response}");
                return response == "OK" || response == "alive";
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[UDP] Error: {ex.Message}");
                return false;
            }
            finally
            {
                _socketLock.Release();
            }
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private (string Name, int Id)? FindTargetProcess()
    {
        var processes = Process.GetProcessesByName(TargetProcessName);
        if (processes.Length > 0) return (processes[0].ProcessName, processes[0].Id);

        processes = Process.GetProcessesByName(TargetProcessNameAlt);
        if (processes.Length > 0) return (processes[0].ProcessName, processes[0].Id);

        return null;
    }

    private async Task StartConnectionLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                bool alive = await SendAndReceiveAsync("heartbeat", token);

                if (alive)
                {
                    if (!_isConnected)
                    {
                        _isConnected = true;
                        var processInfo = FindTargetProcess();
                        App.MainWindow.DispatcherQueue.TryEnqueue(() =>
                        {
                            if (processInfo.HasValue)
                            {
                                ConnectionStatus = $"DLL已连接: {processInfo.Value.Name} [PID: {processInfo.Value.Id}]";
                            }
                            else
                            {
                                ConnectionStatus = "DLL已连接";
                            }
                        });
                        ApplyConfig();
                    }
                }
                else
                {
                    if (_isConnected)
                    {
                        _isConnected = false;
                        App.MainWindow.DispatcherQueue.TryEnqueue(() =>
                        {
                            ConnectionStatus = "连接断开";
                        });
                    }
                    else
                    {
                         App.MainWindow.DispatcherQueue.TryEnqueue(() =>
                        {
                            ConnectionStatus = "等待DLL连接...";
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[UDP] Loop Error: {ex.Message}");
                _isConnected = false;
            }

            await Task.Delay(1000, token);
        }
    }
    
    private void ApplyConfig()
    {
        SendCommand(EnableFpsOverride ? "enable_fps_override" : "disable_fps_override");
        SendCommand($"set_fps {TargetFps}");
        SendCommand(EnableFovOverride ? "enable_fov_override" : "disable_fov_override");
        SendCommand($"set_fov {TargetFov}");
        SendCommand(EnableFogOverride ? "enable_display_fog_override" : "disable_display_fog_override");
        SendCommand(EnablePerspectiveOverride ? "enable_Perspective_override" : "disable_Perspective_override");
    }

    private void LoadConfig()
    {
        try
        {
            if (File.Exists(_configPath))
            {
                var json = File.ReadAllText(_configPath);
                var config = JsonSerializer.Deserialize<ControlPanelConfig>(json);
                if (config != null)
                {
                    _isLoaded = false;
                    EnableFpsOverride = config.EnableFpsOverride;
                    TargetFps = config.TargetFps;
                    EnableFovOverride = config.EnableFovOverride;
                    TargetFov = config.TargetFov;
                    EnableFogOverride = config.EnableFogOverride;
                    EnablePerspectiveOverride = config.EnablePerspectiveOverride;
                    _isLoaded = true;
                    
                    if (_isConnected)
                    {
                        ApplyConfig();
                    }
                }
            }
            else
            {
                _isLoaded = true;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error loading config: {ex.Message}");
            _isLoaded = true;
        }
    }

    private async void SaveConfig()
    {
        if (!_isLoaded) return;
        try
        {
            var config = new ControlPanelConfig
            {
                EnableFpsOverride = EnableFpsOverride,
                TargetFps = TargetFps,
                EnableFovOverride = EnableFovOverride,
                TargetFov = TargetFov,
                EnableFogOverride = EnableFogOverride,
                EnablePerspectiveOverride = EnablePerspectiveOverride
            };

            var dir = Path.GetDirectoryName(_configPath);
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(config);
            await File.WriteAllTextAsync(_configPath, json);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error saving config: {ex.Message}");
        }
    }
}
