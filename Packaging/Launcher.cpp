#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <shlobj.h>
#include <Xinput.h>

#include <array>
#include <cstring>
#include <filesystem>
#include <memory>
#include <optional>
#include <string>
#include <string_view>
#include <utility>
#include <vector>

#include "PayloadTag.h"

namespace
{
namespace fs = std::filesystem;
constexpr int PayloadResourceId = 101;

struct CloseHandleDeleter
{
    void operator()(HANDLE handle) const noexcept { CloseHandle(handle); }
};
using Handle = std::unique_ptr<void, CloseHandleDeleter>;

struct Payload
{
    const void* bytes;
    DWORD length;
};

struct InstalledPayload
{
    fs::path executable;
    fs::path extractionDirectory;
    fs::path nativeLibraryDirectory;
    Handle verifiedFile;
};

std::vector<fs::path> CacheDirectories(const fs::path& launcherDirectory)
{
    std::vector<fs::path> directories;
    PWSTR localAppData = nullptr;
    if (SUCCEEDED(SHGetKnownFolderPath(FOLDERID_LocalAppData, 0, nullptr, &localAppData)))
    {
        const std::unique_ptr<wchar_t, decltype(&CoTaskMemFree)> path(localAppData, CoTaskMemFree);
        directories.push_back(fs::path(path.get()) / L"FH6Telemetry" / L"Runtime");
    }

    std::array<wchar_t, MAX_PATH> temporaryDirectory{};
    const auto length = GetTempPathW(static_cast<DWORD>(temporaryDirectory.size()), temporaryDirectory.data());
    if (length > 0 && length < temporaryDirectory.size())
        directories.push_back(fs::path(temporaryDirectory.data()) / L"FH6TelemetryRuntime");

    directories.push_back(launcherDirectory / L".fh6-telemetry-runtime");
    return directories;
}

Handle OpenVerifiedPayload(const fs::path& path, const Payload& payload)
{
    // Keep a read-sharing handle until process creation to protect the verified image.
    const auto rawFile = CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING,
        FILE_ATTRIBUTE_NORMAL | FILE_FLAG_OPEN_REPARSE_POINT, nullptr);
    if (rawFile == INVALID_HANDLE_VALUE) return {};
    Handle file(rawFile);

    LARGE_INTEGER size{};
    BY_HANDLE_FILE_INFORMATION info{};
    if (!GetFileInformationByHandle(file.get(), &info) ||
        (info.dwFileAttributes & (FILE_ATTRIBUTE_DIRECTORY | FILE_ATTRIBUTE_REPARSE_POINT)) != 0 ||
        !GetFileSizeEx(file.get(), &size) || size.QuadPart != static_cast<LONGLONG>(payload.length))
        return {};

    Handle mapping(CreateFileMappingW(file.get(), nullptr, PAGE_READONLY, 0, 0, nullptr));
    if (!mapping) return {};
    const auto view = MapViewOfFile(mapping.get(), FILE_MAP_READ, 0, 0, 0);
    if (!view) return {};
    const auto matches = std::memcmp(view, payload.bytes, payload.length) == 0;
    UnmapViewOfFile(view);
    if (!matches) return {};
    return file;
}

void WritePayload(const fs::path& destination, const Payload& payload)
{
    GUID id{};
    std::array<wchar_t, 39> name{};
    if (FAILED(CoCreateGuid(&id)) || !StringFromGUID2(id, name.data(), static_cast<int>(name.size()))) return;
    const auto temporary = destination.parent_path() / (std::wstring(name.data()) + L".tmp");

    const auto rawFile = CreateFileW(temporary.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_NEW, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (rawFile == INVALID_HANDLE_VALUE) return;
    Handle file(rawFile);
    DWORD count = 0;
    const auto written = WriteFile(file.get(), payload.bytes, payload.length, &count, nullptr) && count == payload.length;
    file.reset();

    if (!written || !MoveFileExW(temporary.c_str(), destination.c_str(), MOVEFILE_REPLACE_EXISTING))
        DeleteFileW(temporary.c_str());
}

bool CanWriteTo(const fs::path& directory)
{
    std::array<wchar_t, MAX_PATH> probe{};
    if (!GetTempFileNameW(directory.c_str(), L"fh6", 0, probe.data())) return false;
    return DeleteFileW(probe.data()) != FALSE;
}

bool PrepareDirectory(const fs::path& directory)
{
    std::error_code error;
    fs::create_directories(directory, error);
    if (error) return false;
    const auto attributes = GetFileAttributesW(directory.c_str());
    return attributes != INVALID_FILE_ATTRIBUTES && (attributes & FILE_ATTRIBUTE_DIRECTORY) != 0 &&
        (attributes & FILE_ATTRIBUTE_REPARSE_POINT) == 0;
}

std::optional<InstalledPayload> InstallPayload(const fs::path& launcherDirectory, const Payload& payload)
{
    for (const auto& directory : CacheDirectories(launcherDirectory))
    {
        const auto build = directory / PayloadTag;
        const auto extraction = build / L"bundle";
        const auto nativeLibraries = build / L"native";
        if (!PrepareDirectory(directory) || !PrepareDirectory(build) ||
            !PrepareDirectory(extraction) || !CanWriteTo(extraction) ||
            !PrepareDirectory(nativeLibraries) || !CanWriteTo(nativeLibraries))
            continue;

        const auto executable = build / L"FH6Telemetry.App.exe";
        auto verified = OpenVerifiedPayload(executable, payload);
        if (!verified)
        {
            WritePayload(executable, payload);
            verified = OpenVerifiedPayload(executable, payload);
        }
        if (verified) return InstalledPayload{executable, extraction, nativeLibraries, std::move(verified)};
    }
    return std::nullopt;
}

int ShowError(const wchar_t* message)
{
    const auto stderrHandle = GetStdHandle(STD_ERROR_HANDLE);
    if (stderrHandle && stderrHandle != INVALID_HANDLE_VALUE)
    {
        std::array<char, 1024> utf8{};
        const auto length = WideCharToMultiByte(CP_UTF8, 0, message, -1, utf8.data(), static_cast<int>(utf8.size()), nullptr, nullptr);
        DWORD count = 0;
        if (length > 1) WriteFile(stderrHandle, utf8.data(), static_cast<DWORD>(length - 1), &count, nullptr);
    }

    std::array<wchar_t, 2> quiet{};
    if (GetEnvironmentVariableW(L"DOTNET_DISABLE_GUI_ERRORS", quiet.data(), static_cast<DWORD>(quiet.size())) != 1 || quiet[0] != L'1')
        MessageBoxW(nullptr, message, L"FH6 遥测助手 · 启动错误", MB_OK | MB_ICONERROR);
    return 1;
}
}

int WINAPI wWinMain(HINSTANCE instance, HINSTANCE, PWSTR arguments, int showWindow)
{
    if (arguments && std::wstring_view(arguments) == L"--probe-visible-gamepads")
    {
        unsigned int mask = 0;
        for (DWORD slot = 0; slot < XUSER_MAX_COUNT; ++slot)
        {
            XINPUT_STATE state{};
            if (XInputGetState(slot, &state) == ERROR_SUCCESS) mask |= 1u << slot;
        }
        return static_cast<int>(0x100u | mask);
    }

    try
    {
        const auto resource = FindResourceW(instance, MAKEINTRESOURCEW(PayloadResourceId), RT_RCDATA);
        const Payload payload{
            resource ? LockResource(LoadResource(instance, resource)) : nullptr,
            resource ? SizeofResource(instance, resource) : 0,
        };
        if (!payload.bytes || payload.length == 0) return ShowError(L"无法读取内置的遥测程序。请重新获取 EXE。");

        std::wstring modulePath(32768, L'\0');
        const auto length = GetModuleFileNameW(instance, modulePath.data(), static_cast<DWORD>(modulePath.size()));
        if (length == 0 || length >= modulePath.size()) return ShowError(L"无法确定程序所在的目录。");
        modulePath.resize(length);
        const auto launcherDirectory = fs::path(modulePath).parent_path();

        auto installed = InstallPayload(launcherDirectory, payload);
        if (!installed) return ShowError(L"无法准备运行缓存。请将 EXE 放到可写的目录后再打开。");
        // ViGEm's embedded native SDK uses GetTempPath rather than the .NET bundle directory.
        if (!SetEnvironmentVariableW(L"DOTNET_BUNDLE_EXTRACT_BASE_DIR", installed->extractionDirectory.c_str()) ||
            !SetEnvironmentVariableW(L"TEMP", installed->nativeLibraryDirectory.c_str()) ||
            !SetEnvironmentVariableW(L"TMP", installed->nativeLibraryDirectory.c_str()) ||
            !SetEnvironmentVariableW(L"FH6_TELEMETRY_LAUNCH_DIR", launcherDirectory.c_str()) ||
            !SetEnvironmentVariableW(L"FH6_TELEMETRY_LAUNCH_EXE", modulePath.c_str()))
            return ShowError(L"无法配置遥测程序的启动环境。");

        auto command = L"\"" + installed->executable.native() + L"\"";
        if (arguments && *arguments) command += std::wstring(L" ") + arguments;
        STARTUPINFOW startup{};
        startup.cb = sizeof(startup);
        startup.dwFlags = STARTF_USESHOWWINDOW;
        startup.wShowWindow = static_cast<WORD>(showWindow);
        PROCESS_INFORMATION process{};
        if (!CreateProcessW(installed->executable.c_str(), command.data(), nullptr, nullptr, FALSE, 0, nullptr,
            launcherDirectory.c_str(), &startup, &process))
            return ShowError(L"无法启动遥测程序。请检查运行缓存的访问权限。");

        Handle child(process.hProcess);
        CloseHandle(process.hThread);
        installed->verifiedFile.reset();
        DWORD exitCode = 1;
        if (WaitForSingleObject(child.get(), INFINITE) == WAIT_OBJECT_0) GetExitCodeProcess(child.get(), &exitCode);
        return static_cast<int>(exitCode);
    }
    catch (...)
    {
        return ShowError(L"遥测助手启动失败。请重新打开程序。");
    }
}
