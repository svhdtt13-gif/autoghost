#define UNICODE
#define _UNICODE

#include <windows.h>
#include <sddl.h>
#include <processthreadsapi.h>
#include <string>
#include <vector>
#include <fstream>
#include <sstream>
#include <iostream>

struct WindowMatch {
    HWND hwnd{};
    DWORD pid{};
    std::wstring title;
};

static std::wstring GetWindowTitle(HWND hwnd) {
    const int length = GetWindowTextLengthW(hwnd);
    if (length <= 0) return L"";
    std::vector<wchar_t> buffer(static_cast<size_t>(length) + 1);
    GetWindowTextW(hwnd, buffer.data(), static_cast<int>(buffer.size()));
    return std::wstring(buffer.data());
}

static std::wstring GetUserObjectName(HANDLE object) {
    DWORD bytes = 0;
    GetUserObjectInformationW(object, UOI_NAME, nullptr, 0, &bytes);
    if (bytes == 0) return L"";
    std::vector<wchar_t> buffer((bytes / sizeof(wchar_t)) + 2);
    if (!GetUserObjectInformationW(object, UOI_NAME, buffer.data(),
                                   static_cast<DWORD>(buffer.size() * sizeof(wchar_t)), &bytes)) {
        return L"";
    }
    return std::wstring(buffer.data());
}

static std::wstring GetIntegrityLevel(HANDLE process, bool& elevated) {
    elevated = false;
    HANDLE token = nullptr;
    if (!OpenProcessToken(process, TOKEN_QUERY, &token)) return L"Unknown";

    TOKEN_ELEVATION elevation{};
    DWORD returned = 0;
    if (GetTokenInformation(token, TokenElevation, &elevation, sizeof(elevation), &returned)) {
        elevated = elevation.TokenIsElevated != 0;
    }

    DWORD bytes = 0;
    GetTokenInformation(token, TokenIntegrityLevel, nullptr, 0, &bytes);
    std::wstring result = L"Unknown";
    if (bytes > 0) {
        std::vector<BYTE> buffer(bytes);
        if (GetTokenInformation(token, TokenIntegrityLevel, buffer.data(), bytes, &returned)) {
            auto* label = reinterpret_cast<TOKEN_MANDATORY_LABEL*>(buffer.data());
            const DWORD count = *GetSidSubAuthorityCount(label->Label.Sid);
            const DWORD rid = *GetSidSubAuthority(label->Label.Sid, count - 1);
            if (rid >= SECURITY_MANDATORY_SYSTEM_RID) result = L"System";
            else if (rid >= SECURITY_MANDATORY_HIGH_RID) result = L"High";
            else if (rid >= SECURITY_MANDATORY_MEDIUM_RID) result = L"Medium";
            else if (rid >= SECURITY_MANDATORY_LOW_RID) result = L"Low";
            else result = L"RID-0x" + std::to_wstring(rid);
        }
    }

    CloseHandle(token);
    return result;
}

static std::wstring GetProcessName(DWORD pid) {
    HANDLE process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, pid);
    if (!process) return L"";
    wchar_t path[MAX_PATH]{};
    DWORD length = MAX_PATH;
    QueryFullProcessImageNameW(process, 0, path, &length);
    CloseHandle(process);
    std::wstring value(path, length);
    const auto slash = value.find_last_of(L"\\/");
    return slash == std::wstring::npos ? value : value.substr(slash + 1);
}

static bool IsNotepadWindow(HWND hwnd, DWORD& pid, std::wstring& title) {
    if (!IsWindowVisible(hwnd)) return false;
    title = GetWindowTitle(hwnd);
    GetWindowThreadProcessId(hwnd, &pid);
    const std::wstring processName = GetProcessName(pid);
    return title.find(L"Notepad") != std::wstring::npos ||
           processName == L"Notepad.exe" || processName == L"notepad.exe";
}

static BOOL CALLBACK FindNotepadCallback(HWND hwnd, LPARAM data) {
    auto* match = reinterpret_cast<WindowMatch*>(data);
    DWORD pid = 0;
    std::wstring title;
    if (IsNotepadWindow(hwnd, pid, title)) {
        match->hwnd = hwnd;
        match->pid = pid;
        match->title = title;
        return FALSE;
    }
    return TRUE;
}

static WindowMatch FindNotepadWindow() {
    WindowMatch match;
    EnumWindows(FindNotepadCallback, reinterpret_cast<LPARAM>(&match));
    return match;
}

static std::wstring GetThreadDesktopName(DWORD threadId) {
    HDESK desktop = GetThreadDesktop(threadId);
    return desktop ? GetUserObjectName(desktop) : L"";
}

static std::wstring GetInputDesktopName() {
    HDESK desktop = OpenInputDesktop(0, FALSE, DESKTOP_READOBJECTS);
    if (!desktop) return L"";
    const std::wstring name = GetUserObjectName(desktop);
    CloseDesktop(desktop);
    return name;
}

static std::wstring GetCurrentWindowStationName() {
    HWINSTA station = GetProcessWindowStation();
    return station ? GetUserObjectName(station) : L"";
}

static std::wstring GetCurrentThreadDesktopName() {
    return GetThreadDesktopName(GetCurrentThreadId());
}

static std::wstring BoolText(bool value) { return value ? L"true" : L"false"; }

static std::wstring HexPointer(const void* value) {
    std::wstringstream stream;
    stream << L"0x" << std::hex << reinterpret_cast<uintptr_t>(value);
    return stream.str();
}

static std::wstring HexHandle(HWND hwnd) {
    return HexPointer(hwnd);
}

static std::wstring RunProbe() {
    std::wstringstream report;
    report << L"probe=native-x64-sendinput\n";
    report << L"processArchitecture=x64\n";
    report << L"inputStructSize=" << sizeof(INPUT) << L"\n";
    report << L"expectedInputStructSize=40\n";
    report << L"keyboardInputSize=" << sizeof(KEYBDINPUT) << L"\n";
    report << L"windowStation=" << GetCurrentWindowStationName() << L"\n";
    report << L"threadDesktopBefore=" << GetCurrentThreadDesktopName() << L"\n";
    report << L"inputDesktopBefore=" << GetInputDesktopName() << L"\n";

    WindowMatch match = FindNotepadWindow();
    if (!match.hwnd) {
        HDESK desktop = OpenDesktopW(L"Default", 0, FALSE,
            DESKTOP_READOBJECTS | DESKTOP_WRITEOBJECTS | DESKTOP_ENUMERATE |
            DESKTOP_CREATEWINDOW | DESKTOP_SWITCHDESKTOP);
        const bool switched = desktop && SetThreadDesktop(desktop) != FALSE;
        report << L"setThreadDesktopDefault=" << BoolText(switched) << L"\n";
        if (switched) match = FindNotepadWindow();
        if (desktop) CloseDesktop(desktop);
    }

    report << L"threadDesktopAfter=" << GetCurrentThreadDesktopName() << L"\n";
    report << L"inputDesktopAfter=" << GetInputDesktopName() << L"\n";
    report << L"notepadWindow=" << HexHandle(match.hwnd) << L"\n";
    report << L"notepadPid=" << match.pid << L"\n";
    report << L"notepadTitle=" << match.title << L"\n";

    if (!match.hwnd) {
        report << L"result=NOT_RUN_NO_NOTEPAD_WINDOW\n";
        return report.str();
    }

    DWORD targetThreadId = GetWindowThreadProcessId(match.hwnd, &match.pid);
    report << L"targetThreadId=" << targetThreadId << L"\n";
    report << L"targetDesktop=" << GetThreadDesktopName(targetThreadId) << L"\n";
    DWORD targetSession = 0;
    ProcessIdToSessionId(match.pid, &targetSession);
    DWORD currentSession = 0;
    ProcessIdToSessionId(GetCurrentProcessId(), &currentSession);
    report << L"currentSession=" << currentSession << L"\n";
    report << L"targetSession=" << targetSession << L"\n";

    bool currentElevated = false;
    report << L"currentIntegrity=" << GetIntegrityLevel(GetCurrentProcess(), currentElevated) << L"\n";
    report << L"currentElevated=" << BoolText(currentElevated) << L"\n";

    HANDLE targetProcess = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, match.pid);
    bool targetElevated = false;
    if (targetProcess) {
        report << L"targetIntegrity=" << GetIntegrityLevel(targetProcess, targetElevated) << L"\n";
        CloseHandle(targetProcess);
    } else {
        report << L"targetIntegrity=Unknown\n";
    }
    report << L"targetElevated=" << BoolText(targetElevated) << L"\n";

    ShowWindowAsync(match.hwnd, SW_RESTORE);
    SetForegroundWindow(match.hwnd);
    Sleep(150);
    HWND foregroundBefore = GetForegroundWindow();
    DWORD foregroundPidBefore = 0;
    GetWindowThreadProcessId(foregroundBefore, &foregroundPidBefore);
    report << L"foregroundBefore=" << HexHandle(foregroundBefore) << L"\n";
    report << L"foregroundPidBefore=" << foregroundPidBefore << L"\n";
    report << L"foregroundTitleBefore=" << GetWindowTitle(foregroundBefore) << L"\n";
    report << L"foregroundMatchesTarget=" << BoolText(foregroundBefore == match.hwnd) << L"\n";

    INPUT inputs[2]{};
    inputs[0].type = INPUT_KEYBOARD;
    inputs[0].ki.wVk = VK_F13;
    inputs[1].type = INPUT_KEYBOARD;
    inputs[1].ki.wVk = VK_F13;
    inputs[1].ki.dwFlags = KEYEVENTF_KEYUP;
    SetLastError(ERROR_SUCCESS);
    const UINT sent = SendInput(2, inputs, sizeof(INPUT));
    const DWORD error = GetLastError();
    report << L"sendInputCount=" << sent << L"\n";
    report << L"sendInputLastError=" << error << L"\n";
    report << L"sendInputSucceeded=" << BoolText(sent == 2) << L"\n";

    HWND foregroundAfter = GetForegroundWindow();
    DWORD foregroundPidAfter = 0;
    GetWindowThreadProcessId(foregroundAfter, &foregroundPidAfter);
    report << L"foregroundAfter=" << HexHandle(foregroundAfter) << L"\n";
    report << L"foregroundPidAfter=" << foregroundPidAfter << L"\n";
    report << L"result=" << (sent == 2 ? L"PASS" : L"BLOCKED") << L"\n";
    return report.str();
}

int wmain(int argc, wchar_t** argv) {
    std::wstring output;
    for (int i = 1; i + 1 < argc; ++i) {
        if (std::wstring(argv[i]) == L"--output") output = argv[++i];
    }

    const std::wstring report = RunProbe();
    std::wcout << report;
    if (!output.empty()) {
        std::wofstream file(output);
        file << report;
    }
    return 0;
}
