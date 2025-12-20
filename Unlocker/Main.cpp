/* 致亲爱的旅行者：
 * 以下偏移均为虚妄之象，如欲寻得真章，还请亲自动手，丰衣足食
 * （当然，您也可以尝试对着屏幕许愿——只是效果尚未被科学证实）
 */

#define _CRT_SECURE_NO_WARNINGS
#define WIN32_LEAN_AND_MEAN

#include <Windows.h>
#include <winsock2.h>
#include <pathcch.h>
#include <iostream>
#include <string>
#include <vector>
#include <sstream>
#include <thread>

#pragma comment(lib, "Pathcch.lib")
#pragma comment(lib, "ws2_32.lib")

const int UDP_PORT = 8888;
const wchar_t* CONFIG_FILENAME_W = L"config_lite.dat";
const wchar_t* MAPPING_NAME = L"4F3E8543-40F7-4808-82DC-21E48A6037A7";

bool g_isDebug = false;

typedef struct ToolboxConfig
{
    char  gamePath[MAX_PATH];
    BOOL  HideQuestBanner;
    BOOL  DisableShowDamageText;
    BOOL  UsingTouchScreen;
} Config, * pConfig;

struct HookFunctionOffsets
{
    DWORD Hook_GameManagerAwake;
    DWORD Hook_MainEntryPoint;
    DWORD Hook_MainEntryPartner1;
    DWORD Hook_MainEntryPartner2;
    DWORD Hook_SetUid;
    DWORD Hook_SetFov;
    DWORD Hook_SetFog;
    DWORD Hook_GetFps;
    DWORD Hook_SetFps;
    DWORD Hook_OpenTeam;
    DWORD Hook_OpenTeamAdvanced;
    DWORD Hook_CheckEnter;
    DWORD Hook_QuestBanner;
    DWORD Hook_FindObject;
    DWORD Hook_ObjectActive;
    DWORD Hook_CameraMove;
    DWORD Hook_DamageText;
    DWORD Hook_TouchInput;
    DWORD Hook_CombineEntry;
    DWORD Hook_CombineEntryPartner;
    DWORD Hook_SetupResin;
    DWORD Hook_ResinList;
    DWORD Hook_ResinCount;
    DWORD Hook_ResinItem;
    DWORD Hook_ResinRemove;
};

struct HookEnvironment
{
    DWORD Size;
    DWORD State;
    DWORD LastError;
    DWORD Uid;
    HookFunctionOffsets Offsets;
    BOOL  EnableSetFov;
    FLOAT FieldOfView;
    BOOL  FixLowFov;
    BOOL  DisableFog;
    BOOL  EnableSetFps;
    DWORD TargetFps;
    BOOL  RemoveTeamProgress;
    BOOL  HideQuestBanner;
    BOOL  DisableCameraMove;
    BOOL  DisableDamageText;
    BOOL  TouchMode;
    BOOL  RedirectCombine;
    BOOL  ResinItem000106;
    BOOL  ResinItem000201;
    BOOL  ResinItem107009;
    BOOL  ResinItem107012;
    BOOL  ResinItem220007;
};

HookEnvironment* penv = nullptr;
Config g_config = {};

void Log(const std::string& msg) {
    if (g_isDebug) {
        std::cout << msg << std::endl;
    }
}

void LogError(const std::string& msg) {
    if (g_isDebug) {
        std::cerr << msg << std::endl;
    }
}

void InitializeEnv()
{
    HANDLE h = OpenFileMapping(FILE_MAP_READ | FILE_MAP_WRITE, FALSE, MAPPING_NAME);
    if (h == NULL) h = CreateFileMapping(INVALID_HANDLE_VALUE, NULL, PAGE_EXECUTE_READWRITE, 0, sizeof(HookEnvironment), MAPPING_NAME);

    if (h == NULL) {
        LogError("[Error] Failed to create mapping.");
        return;
    }

    penv = (HookEnvironment*)MapViewOfFile(h, FILE_MAP_READ | FILE_MAP_WRITE, 0, 0, 0);

    if (penv) {
        penv->Offsets.Hook_MainEntryPoint = 0x00000000;
        penv->Offsets.Hook_MainEntryPartner1 = 0xDEADBEEF;
        penv->Offsets.Hook_MainEntryPartner2 = 0xDEADBEEF;
        penv->Offsets.Hook_SetFov = 0x00000000;
        penv->Offsets.Hook_SetFog = 0xDEADBEEF;
        penv->Offsets.Hook_SetFps = 0x00000000;
        penv->Offsets.Hook_OpenTeam = 0xDEADBEEF;
        penv->Offsets.Hook_OpenTeamAdvanced = 0x00000000;
        penv->Offsets.Hook_CheckEnter = 0xDEADBEEF;
        penv->Offsets.Hook_QuestBanner = 0x00000000;
        penv->Offsets.Hook_FindObject = 0xDEADBEEF;
        penv->Offsets.Hook_ObjectActive = 0x00000000;
        penv->Offsets.Hook_CameraMove = 0xDEADBEEF;
        penv->Offsets.Hook_DamageText = 0x00000000;
        penv->Offsets.Hook_TouchInput = 0xDEADBEEF;
        penv->Offsets.Hook_CombineEntry = 0x00000000;
        penv->Offsets.Hook_CombineEntryPartner = 0xDEADBEEF;
        penv->Offsets.Hook_GetFps = 0xDEADBEEF;
        penv->Offsets.Hook_GameManagerAwake = 0x00000000;
        Log("[System] Environment initialized.");
    }
}

void SaveConfig()
{
    if (!penv) return;
    g_config.HideQuestBanner = penv->HideQuestBanner;
    g_config.DisableShowDamageText = penv->DisableDamageText;
    g_config.UsingTouchScreen = penv->TouchMode;

    HANDLE h = CreateFile(CONFIG_FILENAME_W, GENERIC_WRITE, 0, NULL, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
    if (h != INVALID_HANDLE_VALUE) {
        DWORD bytesWritten;
        WriteFile(h, &g_config, sizeof(Config), &bytesWritten, NULL);
        CloseHandle(h);
        Log("[Config] Saved.");
    }
}

void LoadConfig()
{
    ZeroMemory(&g_config, sizeof(Config));
    HANDLE h = CreateFile(CONFIG_FILENAME_W, GENERIC_READ, 0, NULL, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, NULL);
    if (h != INVALID_HANDLE_VALUE) {
        DWORD bytesRead;
        ReadFile(h, &g_config, sizeof(Config), &bytesRead, NULL);
        CloseHandle(h);
        if (penv) {
            penv->HideQuestBanner = g_config.HideQuestBanner;
            penv->DisableDamageText = g_config.DisableShowDamageText;
            penv->TouchMode = g_config.UsingTouchScreen;
        }
        Log("[Config] Loaded.");
    }
}

bool InjectOneDll(HANDLE hProcess, const wchar_t* dllName) {
    WCHAR appDir[MAX_PATH];
    GetModuleFileNameW(NULL, appDir, MAX_PATH);
    PathCchRemoveFileSpec(appDir, MAX_PATH);
    
    WCHAR dllPath[MAX_PATH];
    PathCchCombine(dllPath, MAX_PATH, appDir, dllName);

    if (GetFileAttributesW(dllPath) == INVALID_FILE_ATTRIBUTES) {
        std::string err = "[Error] DLL not found: ";
        char buf[256]; wcstombs(buf, dllName, 256);
        err += buf;
        LogError(err);
        return false;
    }

    size_t pathSize = (wcslen(dllPath) + 1) * sizeof(WCHAR);
    LPVOID ptr = VirtualAllocEx(hProcess, NULL, 4096, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
    
    if (!ptr) return false;

    WriteProcessMemory(hProcess, ptr, dllPath, pathSize, NULL);
    
    HMODULE hm = GetModuleHandleW(L"kernel32.dll");
    FARPROC hp = GetProcAddress(hm, "LoadLibraryW");
    
    HANDLE hThread = CreateRemoteThread(hProcess, NULL, NULL, (LPTHREAD_START_ROUTINE)hp, ptr, NULL, NULL);
    if (hThread) {
        WaitForSingleObject(hThread, INFINITE);
        CloseHandle(hThread);
        Log("[Inject] Injected.");
        VirtualFreeEx(hProcess, ptr, 0, MEM_RELEASE);
        return true;
    } else {
        LogError("[Inject] Remote thread failed.");
        VirtualFreeEx(hProcess, ptr, 0, MEM_RELEASE);
        return false;
    }
}

void LaunchGameImpl()
{
    if (strlen(g_config.gamePath) == 0) {
        LogError("[Error] Path not set.");
        return;
    }

    Log("[Launch] Starting game...");

    STARTUPINFOW si{};
    PROCESS_INFORMATION pi{};
    si.cb = sizeof(si);

    WCHAR workdir[MAX_PATH];
    int len = MultiByteToWideChar(CP_ACP, 0, g_config.gamePath, -1, NULL, 0);
    std::vector<wchar_t> wpathBuf(len);
    MultiByteToWideChar(CP_ACP, 0, g_config.gamePath, -1, wpathBuf.data(), len);
    std::wstring wpath(wpathBuf.data());

    wcsncpy_s(workdir, wpath.c_str(), MAX_PATH);
    PathCchRemoveFileSpec(workdir, MAX_PATH);

    BOOL started = CreateProcess(wpath.c_str(), NULL, NULL, NULL, FALSE, CREATE_SUSPENDED, NULL, workdir, &si, &pi);
    if (!started) {
        LogError("[Error] CreateProcess failed.");
        return;
    }

    Log("[Launch] Injecting nvhelper.dll...");
    InjectOneDll(pi.hProcess, L"nvhelper.dll");

    Log("[Launch] Injecting Genshin.dll...");
    InjectOneDll(pi.hProcess, L"Genshin.dll");

    ResumeThread(pi.hThread);
    CloseHandle(pi.hThread);
    CloseHandle(pi.hProcess);
    Log("[Launch] Done.");
}

void ProcessCommand(const std::string& line) {
    if (line.empty()) return;
    
    std::stringstream ss(line);
    std::string cmd;
    ss >> cmd;

    if (cmd == "launch") {
        LaunchGameImpl();
    }
    else if (cmd == "save") {
        SaveConfig();
    }
    else if (cmd == "exit" || cmd == "quit") {
        exit(0);
    }
    else if (cmd == "set_path") {
        std::string path;
        std::getline(ss, path);
        size_t first = path.find_first_not_of(" \t");
        if (std::string::npos != first) {
            path = path.substr(first);
            if (path.size() >= 2 && path.front() == '"' && path.back() == '"') {
                path = path.substr(1, path.size() - 2);
            }
            strcpy_s(g_config.gamePath, path.c_str());
            Log("Path updated.");
            SaveConfig();
        }
    }
    else if (cmd == "toggle_touch") {
        if (penv) {
            penv->TouchMode = !penv->TouchMode;
            Log(std::string("Touch Mode: ") + (penv->TouchMode ? "ON" : "OFF"));
        }
    }
    else if (cmd == "toggle_dmg") {
        if (penv) {
            penv->DisableDamageText = !penv->DisableDamageText;
            Log(std::string("No Damage Text: ") + (penv->DisableDamageText ? "ON" : "OFF"));
        }
    }
    else if (cmd == "toggle_quest") {
        if (penv) {
            penv->HideQuestBanner = !penv->HideQuestBanner;
            Log(std::string("Hide Quest Banner: ") + (penv->HideQuestBanner ? "ON" : "OFF"));
        }
    }
    else if (cmd == "status") {
        if (!penv) return;
        std::cout << "\n[Status]\n";
        std::cout << "Path: " << g_config.gamePath << "\n";
        std::cout << "Touch: " << penv->TouchMode << " | NoDmg: " << penv->DisableDamageText << " | NoQuest: " << penv->HideQuestBanner << "\n";
    }
    else {
        Log("Unknown command: " + cmd);
    }
}

void UdpServerThread() {
    WSADATA wsaData;
    if (WSAStartup(MAKEWORD(2, 2), &wsaData) != 0) {
        LogError("[UDP] WSAStartup failed.");
        return;
    }

    SOCKET sock = socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP);
    if (sock == INVALID_SOCKET) {
        LogError("[UDP] Socket creation failed.");
        WSACleanup();
        return;
    }

    sockaddr_in serverAddr{};
    serverAddr.sin_family = AF_INET;
    serverAddr.sin_port = htons(UDP_PORT);
    serverAddr.sin_addr.s_addr = INADDR_ANY;

    if (bind(sock, (sockaddr*)&serverAddr, sizeof(serverAddr)) == SOCKET_ERROR) {
        LogError("[UDP] Bind failed. Port might be in use.");
        closesocket(sock);
        WSACleanup();
        return;
    }

    Log("[UDP] Listening on port " + std::to_string(UDP_PORT));

    char buffer[1024];
    sockaddr_in clientAddr;
    int clientAddrLen = sizeof(clientAddr);

    while (true) {
        memset(buffer, 0, sizeof(buffer));
        int bytesReceived = recvfrom(sock, buffer, 1023, 0, (sockaddr*)&clientAddr, &clientAddrLen);
        
        if (bytesReceived > 0) {
            std::string receivedCmd(buffer);
            receivedCmd.erase(std::remove(receivedCmd.begin(), receivedCmd.end(), '\n'), receivedCmd.end());
            receivedCmd.erase(std::remove(receivedCmd.begin(), receivedCmd.end(), '\r'), receivedCmd.end());
            
            Log("[UDP] Recv: " + receivedCmd);
            ProcessCommand(receivedCmd);
        }
    }

    closesocket(sock);
    WSACleanup();
}

int main(int argc, char* argv[])
{
    for (int i = 1; i < argc; ++i) {
        if (std::string(argv[i]) == "-debug") {
            g_isDebug = true;
            break;
        }
    }

    if (!g_isDebug) {
        ShowWindow(GetConsoleWindow(), SW_HIDE);
    } else {
        SetConsoleTitle(L"Launcher Debug Console");
        std::cout << "=== Debug Mode Enabled ===\n";
    }

    InitializeEnv();
    LoadConfig();

    std::thread udpThread(UdpServerThread);
    udpThread.detach();

    if (g_isDebug) {
        std::cout << "Type 'help' for commands.\n";
        std::string input;
        while (true) {
            std::cout << "> ";
            if (!std::getline(std::cin, input)) break;
            ProcessCommand(input);
        }
    } else {
        while (true) {
            Sleep(1000); 
        }
    }

    return 0;
}