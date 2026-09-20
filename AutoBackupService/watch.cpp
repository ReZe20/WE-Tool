// 常驻监听:对应 C# BackupRunner 的 FileSystemWatcher + 待处理轮询循环。
// 语义要点(全部从 C# 抄来,不是设计选择):
//   * VDF 变化后先等 2 秒(给 Steam 写完),再重解析,只把「新增的 ID」入队
//   * 队列空时线程阻塞在事件上 → 空闲期零 CPU;非空时每 5 秒扫一轮
//   * downloads 目录只是日志参考信号,不参与决策
#include "ab.h"

#include <string.h>
#include <thread>
#include <vector>

namespace ab {
namespace {

CRITICAL_SECTION g_lock;
bool g_lockInit = false;

std::vector<W> g_known;
std::vector<W> g_pending;

HANDLE g_vdfDir = nullptr;        // 用于 CancelIoEx 解开阻塞的 ReadDirectoryChangesW
HANDLE g_downloadsDir = nullptr;

std::vector<std::thread> g_threads;

void Lock() { EnterCriticalSection(&g_lock); }
void Unlock() { LeaveCriticalSection(&g_lock); }

bool Has(const std::vector<W>& v, const W& s) {
    for (const W& x : v)
        if (x == s) return true;
    return false;
}

void Erase(std::vector<W>& v, const W& s) {
    for (size_t i = 0; i < v.size(); ++i)
        if (v[i] == s) { v.erase(v.begin() + i); return; }
}

// C# BackupRunner.RefreshKnownIds
void RefreshKnownIds(const Cfg& c) {
    const std::vector<W> fresh = VdfSubscribedIds(c.vdfPath);
    Lock();
    std::vector<W> added;
    for (const W& id : fresh)
        if (!Has(g_known, id)) added.push_back(id);
    g_known = fresh;
    if (!added.empty()) {
        W joined;
        for (size_t i = 0; i < added.size(); ++i) {
            if (i) joined += L',';
            joined += added[i];
        }
        wchar_t buf[16];
        swprintf(buf, 16, L"%zu", added.size());
        LogWrite(W(A8("发现新增订阅 ")) + buf + W(A8(" 个: ")) + joined);
        for (const W& id : added) g_pending.push_back(id);
        Unlock();
        WakeLoop();                                       // C#: _hasPending.Release()
        return;
    }
    Unlock();
}

// C# BackupRunner.ProcessPending
void ProcessPending(const Cfg& c) {
    std::vector<W> snapshot;
    Lock();
    snapshot = g_pending;
    Unlock();

    for (const W& id : snapshot) {
        if (Stopping()) return;
        if (IsBackedUp(c.workshopPath, id)) {
            Lock();
            Erase(g_pending, id);
            Unlock();
            continue;
        }
        const W proj = Combine(Combine(c.workshopPath, id), L"project.json");
        if (!FileExists(proj)) continue;                  // 还在下载,留在队列下轮再查
        if (FilterMatches(c, ReadProjectMeta(proj))) TryBackup(c.workshopPath, id);
        Lock();
        Erase(g_pending, id);
        Unlock();
    }
}

size_t PendingCount() {
    Lock();
    const size_t n = g_pending.size();
    Unlock();
    return n;
}

// C# RunPendingLoop:空队列时零 CPU 阻塞,非空时 5 秒一轮
void PendingLoop(const Cfg c) {
    while (!Stopping()) {
        if (PendingCount() > 0) {
            if (WaitStop(5000)) return;                   // Task.Delay(5000, ct)
            ProcessPending(c);
        } else {
            const HANDLE handles[2] = { WakeHandle(), StopHandle() };
            const DWORD r = WaitForMultipleObjects(2, handles, FALSE, INFINITE);
            if (r == WAIT_OBJECT_0 + 1) return;
        }
    }
}

HANDLE OpenDirToWatch(const W& dir) {
    return CreateFileW(Ext(dir).c_str(), FILE_LIST_DIRECTORY,
                       FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr, OPEN_EXISTING,
                       FILE_FLAG_BACKUP_SEMANTICS, nullptr);
}

// .NET 的 FileSystemWatcher 会为**每一条**通知记录排一次回调,而回调第一件事是睡 2 秒再刷新。
// 一次 File.WriteAllText 常产生 SIZE + LAST_WRITE 两条记录 → 基线里就有两行「VDF 解析完成」。
// 所以这里必须逐条刷新(而不是整批合并一次),否则日志行数对不上。
void VdfLoop(const Cfg c, const HANDLE h, const W watchName) {
    alignas(UINT64) char buf[8 * 1024];
    while (!Stopping()) {
        DWORD ret = 0;
        const BOOL ok = ReadDirectoryChangesW(h, buf, sizeof(buf), FALSE,
                                              FILE_NOTIFY_CHANGE_LAST_WRITE | FILE_NOTIFY_CHANGE_SIZE |
                                                  FILE_NOTIFY_CHANGE_CREATION,
                                              &ret, nullptr, nullptr);
        if (Stopping()) break;
        if (!ok || ret == 0) break;                       // 目录被删/缓冲区溢出:C# 的 watcher 同样静默失聪
        DWORD off = 0;
        for (;;) {
            auto* info = reinterpret_cast<FILE_NOTIFY_INFORMATION*>(buf + off);
            const int chars = static_cast<int>(info->FileNameLength / sizeof(wchar_t));
            const W name(info->FileName, static_cast<size_t>(chars));
            const bool interesting = info->Action == FILE_ACTION_MODIFIED ||
                                     info->Action == FILE_ACTION_ADDED ||
                                     info->Action == FILE_ACTION_RENAMED_NEW_NAME;
            if (interesting && _wcsicmp(name.c_str(), watchName.c_str()) == 0) {
                if (WaitStop(2000)) break;                // C#: Thread.Sleep(2000) 防抖,但要能被打断
                RefreshKnownIds(c);
            }
            if (info->NextEntryOffset == 0) break;
            off += info->NextEntryOffset;
        }
    }
}

void DownloadsLoop(const W dir, const HANDLE h) {
    (void)dir;
    alignas(UINT64) char buf[8 * 1024];
    while (!Stopping()) {
        DWORD ret = 0;
        const BOOL ok = ReadDirectoryChangesW(h, buf, sizeof(buf), FALSE,
                                              FILE_NOTIFY_CHANGE_DIR_NAME | FILE_NOTIFY_CHANGE_CREATION,
                                              &ret, nullptr, nullptr);
        if (Stopping()) break;
        if (!ok || ret == 0) break;
        DWORD off = 0;
        for (;;) {
            auto* info = reinterpret_cast<FILE_NOTIFY_INFORMATION*>(buf + off);
            if (info->Action == FILE_ACTION_ADDED) {
                const int chars = static_cast<int>(info->FileNameLength / sizeof(wchar_t));
                LogWrite(W(A8("downloads 缓存出现新目录: ")) + W(info->FileName, chars) +
                         W(A8("(下载中,等待移入 content)")));
            }
            if (info->NextEntryOffset == 0) break;
            off += info->NextEntryOffset;
        }
    }
}

W DownloadsOf(const W& workshopPath) {
    const W parent = DirName(workshopPath);                    // ...\workshop\content
    const W root = DirName(parent);                           // ...\workshop
    return Combine(Combine(root, L"downloads"), BaseName(workshopPath));
}

}  // namespace

void StartWatch(const Cfg& c) {
    if (!g_lockInit) {
        InitializeCriticalSection(&g_lock);
        g_lockInit = true;
    }
    g_known.clear();
    g_pending.clear();

    g_known = VdfSubscribedIds(c.vdfPath);
    wchar_t buf[16];
    swprintf(buf, 16, L"%zu", g_known.size());
    LogWrite(W(A8("服务已启动: VDF=")) + c.vdfPath + W(A8(", 当前订阅 ")) + buf + W(A8(" 个")));

    // 与 C# 同一判法:VDF 的父目录不存在就不监听,并留一行同文案日志(R04 把它钉进基线)。
    // C# 那边这不是防御式编程,是修崩溃:FileSystemWatcher.Path 的 setter 会抛 ArgumentException。
    const W vdfDir = DirName(c.vdfPath);
    const W vdfName = BaseName(c.vdfPath);
    if (vdfDir.empty() || !DirExists(vdfDir)) {
        LogWrite(W(A8("监听目录不存在,跳过 VDF 监听: ")) + vdfDir);
    } else if (!vdfName.empty()) {
        const HANDLE h = OpenDirToWatch(vdfDir);
        if (h != INVALID_HANDLE_VALUE) {
            g_vdfDir = h;
            g_threads.emplace_back(VdfLoop, c, h, vdfName);
        }
    }

    const W downloads = DownloadsOf(c.workshopPath);
    if (DirExists(downloads)) {
        const HANDLE h = OpenDirToWatch(downloads);
        if (h != INVALID_HANDLE_VALUE) {
            g_downloadsDir = h;
            g_threads.emplace_back(DownloadsLoop, downloads, h);
        }
    }

    g_threads.emplace_back(PendingLoop, c);
}

void StopWatch() {
    SignalStop();
    if (g_vdfDir) CancelIoEx(g_vdfDir, nullptr);
    if (g_downloadsDir) CancelIoEx(g_downloadsDir, nullptr);
    for (auto& t : g_threads)
        if (t.joinable()) t.join();
    g_threads.clear();
    if (g_vdfDir) CloseHandle(g_vdfDir);
    if (g_downloadsDir) CloseHandle(g_downloadsDir);
    g_vdfDir = nullptr;
    g_downloadsDir = nullptr;
}

}  // namespace ab
