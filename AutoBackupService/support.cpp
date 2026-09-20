#include "ab.h"

#include <shlobj.h>
#include <stdio.h>

#include <cwchar>

namespace ab {
namespace {

W g_dataRoot;
CRITICAL_SECTION g_logLock;
bool g_logLockInit = false;

constexpr ULONGLONG kMaxLogSize = 5ull * 1024ull * 1024ull;

bool HasPrefix(const W& s, const W& p) { return s.size() >= p.size() && s.compare(0, p.size(), p) == 0; }

W LogPathOf() { return Combine(g_dataRoot, L"AutoBackupService.log"); }

}  // namespace

W FromU8N(const char* utf8, size_t len) {
    if (len == 0) return W();
    const int n = MultiByteToWideChar(CP_UTF8, 0, utf8, static_cast<int>(len), nullptr, 0);
    if (n <= 0) return W();                       // 非法 UTF-8:与 .NET 的替换解码同源,但这里宁可给空
    W out(static_cast<size_t>(n), L'\0');
    MultiByteToWideChar(CP_UTF8, 0, utf8, static_cast<int>(len), out.data(), n);
    return out;
}

W FromU8(const char* utf8) { return utf8 ? FromU8N(utf8, strlen(utf8)) : W(); }

std::string ToU8(const W& s) {
    if (s.empty()) return std::string();
    const int n = WideCharToMultiByte(CP_UTF8, 0, s.c_str(), static_cast<int>(s.size()), nullptr, 0,
                                      nullptr, nullptr);
    if (n <= 0) return std::string();
    std::string out(static_cast<size_t>(n), '\0');
    WideCharToMultiByte(CP_UTF8, 0, s.c_str(), static_cast<int>(s.size()), out.data(), n, nullptr, nullptr);
    return out;
}

W LowerAscii(W s) {
    for (auto& c : s)
        if (c >= L'A' && c <= L'Z') c = static_cast<wchar_t>(c - L'A' + L'a');
    return s;
}

W TrimLower(const W& s) {
    auto space = [](wchar_t c) {
        return c == L' ' || c == L'\t' || c == L'\n' || c == L'\r' || c == 0x000B || c == 0x000C ||
               c == 0x00A0 || c == 0x3000 || c == 0xFEFF;
    };
    size_t b = 0, e = s.size();
    while (b < e && space(s[b])) ++b;
    while (e > b && space(s[e - 1])) --e;
    return LowerAscii(s.substr(b, e - b));
}

bool IEquals(const W& a, const W& b) { return _wcsicmp(a.c_str(), b.c_str()) == 0; }

bool IsNullOrEmpty(const W& s) { return s.empty(); }

W NowStamp() {
    SYSTEMTIME st{};
    GetLocalTime(&st);
    wchar_t buf[32];
    swprintf(buf, 32, L"%04d-%02d-%02d %02d:%02d:%02d", st.wYear, st.wMonth, st.wDay, st.wHour,
             st.wMinute, st.wSecond);
    return buf;
}

W Ext(const W& p) {
    if (HasPrefix(p, L"\\\\?\\")) return p;
    if (HasPrefix(p, L"\\\\")) return L"\\\\?\\UNC\\" + p.substr(2);
    if (p.size() >= 2 && p[1] == L':') return L"\\\\?\\" + p;
    return p;
}

W Combine(const W& a, const W& b) {
    if (b.empty()) return a;
    if ((b.size() >= 2 && b[1] == L':') || b[0] == L'\\' || b[0] == L'/') return b;
    if (a.empty()) return b;
    const bool sep = a.back() == L'\\' || a.back() == L'/';
    return sep ? a + b : a + L"\\" + b;
}

W DirName(const W& p) {
    const size_t slash = p.find_last_of(L"\\/");
    if (slash == W::npos) return W();
    if (slash == 0) return W(1, p[0]);
    if (p[slash - 1] == L':') return p.substr(0, slash + 1);          // "C:\a" → "C:\"
    size_t end = slash;
    while (end > 1 && (p[end - 1] == L'\\' || p[end - 1] == L'/')) --end;
    return p.substr(0, end);
}

W BaseName(const W& p) {
    const size_t slash = p.find_last_of(L"\\/");
    return slash == W::npos ? p : p.substr(slash + 1);
}

W LocalAppData() {
    wchar_t buf[MAX_PATH]{};
    if (SUCCEEDED(SHGetFolderPathW(nullptr, CSIDL_LOCAL_APPDATA, nullptr, 0, buf))) return buf;
    const DWORD n = GetEnvironmentVariableW(L"LOCALAPPDATA", buf, MAX_PATH);
    return n > 0 ? W(buf, n) : W(L"C:\\Users\\Default\\AppData\\Local");
}

bool FileExists(const W& p) {
    const DWORD a = GetFileAttributesW(Ext(p).c_str());
    return a != INVALID_FILE_ATTRIBUTES && !(a & FILE_ATTRIBUTE_DIRECTORY);
}

bool DirExists(const W& p) {
    const DWORD a = GetFileAttributesW(Ext(p).c_str());
    return a != INVALID_FILE_ATTRIBUTES && (a & FILE_ATTRIBUTE_DIRECTORY) != 0;
}

bool EnsureDir(const W& p) {
    if (p.empty()) return true;
    if (CreateDirectoryW(Ext(p).c_str(), nullptr)) return true;
    const DWORD e = GetLastError();
    return e == ERROR_ALREADY_EXISTS || DirExists(p);
}

bool EnsureDirTree(const W& p) {
    if (p.empty() || DirExists(p)) return true;
    // 先切出根(盘符 "X:\" 或 UNC "\\server\share\")。根必然已存在,绝不能拿去 CreateDirectory:
    // \\?\C: 这种少尾斜杠的形态是非法参数(err=87),早先就是栽在这里导致整棵树建不起来。
    W cur;
    size_t i = 0;
    if (p.size() >= 2 && p[1] == L':') {
        cur = p.substr(0, 2) + L"\\";
        i = 3;
    } else if (HasPrefix(p, L"\\\\")) {
        const size_t second = p.find(L'\\', 2);
        const size_t share = second == W::npos ? W::npos : p.find(L'\\', second + 1);
        if (share == W::npos) return EnsureDir(p);
        cur = p.substr(0, share) + L"\\";
        i = share + 1;
    } else {
        return EnsureDir(p);                                   // 相对路径:交给单层创建,不猜根
    }
    if (i > p.size()) return DirExists(p);
    for (;;) {
        const size_t next = p.find_first_of(L"\\/", i);
        const size_t end = next == W::npos ? p.size() : next;
        if (end > i) {
            cur += p.substr(i, end - i);
            if (!DirExists(cur) && !EnsureDir(cur)) return false;
            cur += L'\\';
        }
        if (next == W::npos) break;
        i = next + 1;
    }
    return DirExists(p);
}

W ReadFileUtf8(const W& path, bool& ok) {
    ok = false;
    const HANDLE h = CreateFileW(Ext(path).c_str(), GENERIC_READ,
                                 FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr,
                                 OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (h == INVALID_HANDLE_VALUE) return W();
    std::string bytes;
    char buf[64 * 1024];
    for (;;) {
        DWORD got = 0;
        if (!ReadFile(h, buf, sizeof(buf), &got, nullptr) || got == 0) break;
        bytes.append(buf, got);
        if (got < sizeof(buf)) break;
    }
    CloseHandle(h);
    if (bytes.size() >= 3 && static_cast<unsigned char>(bytes[0]) == 0xEF &&
        static_cast<unsigned char>(bytes[1]) == 0xBB && static_cast<unsigned char>(bytes[2]) == 0xBF)
        bytes.erase(0, 3);                                            // .NET ReadAllText 剥 BOM
    ok = true;
    return FromU8N(bytes.data(), bytes.size());
}

bool WriteFileUtf8(const W& path, const W& content) {
    const std::string bytes = ToU8(content);
    const HANDLE h = CreateFileW(Ext(path).c_str(), GENERIC_WRITE, 0, nullptr, CREATE_ALWAYS,
                                 FILE_ATTRIBUTE_NORMAL, nullptr);
    if (h == INVALID_HANDLE_VALUE) return false;
    DWORD written = 0;
    const bool ok = bytes.empty() ||
                    (WriteFile(h, bytes.data(), static_cast<DWORD>(bytes.size()), &written, nullptr) &&
                     written == bytes.size());
    CloseHandle(h);
    return ok;
}

void WriteConsoleLine(const W& line) {
    const HANDLE h = GetStdHandle(STD_OUTPUT_HANDLE);
    if (h == nullptr || h == INVALID_HANDLE_VALUE) return;
    // 与 C# 侧新加的 Console.OutputEncoding=UTF8(无 BOM)对齐:stdout 必须与所在控制台的
    // 代码页无关,否则同一份基线换台机器就逐字节对不上。日志文件本来就是 UTF-8。
    const std::string bytes = ToU8(line) + "\r\n";                     // Console.WriteLine 的行尾
    DWORD written = 0;
    WriteFile(h, bytes.data(), static_cast<DWORD>(bytes.size()), &written, nullptr);
}

void InitLog(const W& dataRoot) {
    g_dataRoot = dataRoot;
    if (!g_logLockInit) {
        InitializeCriticalSection(&g_logLock);
        g_logLockInit = true;
    }
}

W DataRoot() { return g_dataRoot; }

namespace {
HANDLE g_stop = nullptr;     // 手动复位:所有线程都能观察到
HANDLE g_wake = nullptr;     // 自动复位:待处理队列有新东西时点亮
}  // namespace

void InitEvents() {
    if (!g_stop) g_stop = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    if (!g_wake) g_wake = CreateEventW(nullptr, FALSE, FALSE, nullptr);
}

HANDLE StopHandle() { return g_stop; }
HANDLE WakeHandle() { return g_wake; }

void SignalStop() {
    InitEvents();
    SetEvent(g_stop);
    SetEvent(g_wake);                                  // 别让轮询线程卡在唤醒等待上
}

void WakeLoop() {
    if (g_wake) SetEvent(g_wake);
}

bool Stopping() { return g_stop && WaitForSingleObject(g_stop, 0) == WAIT_OBJECT_0; }

bool WaitStop(DWORD ms) { return g_stop && WaitForSingleObject(g_stop, ms) == WAIT_OBJECT_0; }

void LogWrite(const W& msg) {
    const W stamped = L"[" + NowStamp() + L"] " + msg;
    EnterCriticalSection(&g_logLock);
    const W path = LogPathOf();
    EnsureDirTree(DirName(path));
    WIN32_FILE_ATTRIBUTE_DATA attr{};
    // 与 C# 侧同一个修法:先判存在再取长度,否则日志文件永远创建不了、第一行必丢
    if (GetFileAttributesExW(Ext(path).c_str(), GetFileExInfoStandard, &attr)) {
        const ULONGLONG size = (static_cast<ULONGLONG>(attr.nFileSizeHigh) << 32) | attr.nFileSizeLow;
        if (size > kMaxLogSize) WriteFileUtf8(path, L"");
    }
    const HANDLE h = CreateFileW(Ext(path).c_str(), FILE_APPEND_DATA, FILE_SHARE_READ | FILE_SHARE_WRITE,
                                 nullptr, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (h != INVALID_HANDLE_VALUE) {
        const std::string bytes = ToU8(stamped + L"\r\n");
        DWORD written = 0;
        WriteFile(h, bytes.data(), static_cast<DWORD>(bytes.size()), &written, nullptr);
        CloseHandle(h);
    }
    LeaveCriticalSection(&g_logLock);

    // C# 是 if (!Console.IsOutputRedirected) Console.WriteLine(line):重定向时不 echo,
    // 免得 --verify 的 stdout 被日志行污染(基线的 stdout 段就是靠这条保持干净的)
    if (GetFileType(GetStdHandle(STD_OUTPUT_HANDLE)) == FILE_TYPE_CHAR) WriteConsoleLine(stamped);
}

void LogWriteErr(const W& context, const W& detail) { LogWrite(context + L": " + detail); }

}  // namespace ab
