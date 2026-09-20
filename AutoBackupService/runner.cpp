#include "ab.h"

#include <string.h>

namespace ab {
namespace {

// 与 .NET 的 Directory.EnumerateDirectories 同一底层流,故条目顺序一致(索引序)
std::vector<W> EnumerateDirs(const W& dir) {
    std::vector<W> out;
    WIN32_FIND_DATAW fd{};
    const HANDLE h = FindFirstFileW((Ext(dir) + L"\\*").c_str(), &fd);
    if (h == INVALID_HANDLE_VALUE) return out;
    do {
        const W name = fd.cFileName;
        if (name == L"." || name == L"..") continue;
        if (fd.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) out.push_back(name);
    } while (FindNextFileW(h, &fd));
    FindClose(h);
    return out;
}

}  // namespace

// C# BackupRunner.TryBackup —— 启动补齐与新增订阅轮询两条路径共用同一份文案
bool TryBackup(const W& contentPath, const W& id) {
    const W sourceDir = Combine(contentPath, id);
    if (!DirExists(sourceDir)) {
        LogWrite(W(A8("content/")) + id + W(A8(" 目录不存在,跳过备份")));
        return false;
    }
    const BackupResult r = BackupFolder(sourceDir, contentPath, id);
    wchar_t nums[32];
    if (r.Succeeded()) {
        swprintf(nums, 32, L"%d", r.linked);
        const W linked = nums;
        swprintf(nums, 32, L"%d", r.skipped);
        LogWrite(W(A8("已备份 ")) + id + W(A8(": 链接 ")) + linked +
                 W(A8(" 个,跳过 ")) + nums + W(A8(" 个")));
        return true;
    }
    LogWrite(W(A8("备份 ")) + id + W(A8(" 失败: ")) + r.error);
    return false;
}

int BackupAllMissing(const Cfg& c) {
    const W& ws = c.workshopPath;
    if (!DirExists(ws)) {
        LogWrite(W(A8("content 目录不存在: ")) + ws);
        return 0;
    }
    // 订阅集为空 = 不过滤(C# 里 subscribed.Count > 0 那条判断的原样语义)
    const std::vector<W> subscribed = VdfSubscribedIds(c.vdfPath);
    int backed = 0;
    for (const W& id : EnumerateDirs(ws)) {
        if (id == L".we_backup") continue;
        if (!subscribed.empty()) {
            bool hit = false;
            for (const W& s : subscribed)
                if (s == id) { hit = true; break; }
            if (!hit) continue;
        }
        if (IsBackedUp(ws, id)) continue;
        const W proj = Combine(Combine(ws, id), L"project.json");
        if (!FileExists(proj)) continue;
        if (!FilterMatches(c, ReadProjectMeta(proj))) continue;
        if (TryBackup(ws, id)) ++backed;
    }
    wchar_t buf[16];
    swprintf(buf, 16, L"%d", backed);
    LogWrite(W(A8("启动补齐完成: 新增备份 ")) + buf + W(A8(" 个")));
    return backed;
}

}  // namespace ab
