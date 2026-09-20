#include "ab.h"

#include <string.h>

namespace ab {
namespace {

const wchar_t kBackupRootName[] = L".we_backup";
const wchar_t kMarkerName[] = L".backup_ok";
constexpr int kErrorAlreadyExists = 183;

bool FileIdOf(const W& path, DWORD& vol, DWORD& hi, DWORD& lo) {
    vol = hi = lo = 0;
    const HANDLE h = CreateFileW(Ext(path).c_str(), GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE,
                                 nullptr, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS, nullptr);
    if (h == INVALID_HANDLE_VALUE || h == nullptr) return false;
    BY_HANDLE_FILE_INFORMATION info{};
    const bool ok = GetFileInformationByHandle(h, &info) != FALSE;
    if (ok) {
        vol = info.dwVolumeSerialNumber;
        hi = info.nFileIndexHigh;
        lo = info.nFileIndexLow;
    }
    CloseHandle(h);
    return ok;
}

bool IsSameFile(const W& a, const W& b) {
    DWORD va, ha, la, vb, hb, lb;
    if (!FileIdOf(a, va, ha, la)) return false;
    if (!FileIdOf(b, vb, hb, lb)) return false;
    return va == vb && ha == hb && la == lb;
}

// 复刻 .NET 的递归枚举顺序:先把本目录条目按索引序走一遍(文件立即产出、目录名暂存),
// 本目录走完再按暂存顺序递归。顺序错了日志行序就会对不上基线。
void EnumerateFiles(const W& dir, const W& relPrefix, std::vector<W>& relOut) {
    const W pattern = Ext(dir) + L"\\*";
    WIN32_FIND_DATAW fd{};
    const HANDLE h = FindFirstFileW(pattern.c_str(), &fd);
    if (h == INVALID_HANDLE_VALUE) return;
    std::vector<W> subDirs;
    do {
        const W name = fd.cFileName;
        if (name == L"." || name == L"..") continue;
        if (fd.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) {
            subDirs.push_back(name);
            continue;
        }
        relOut.push_back(relPrefix.empty() ? name : relPrefix + L"\\" + name);
    } while (FindNextFileW(h, &fd));
    FindClose(h);
    for (const W& sub : subDirs)
        EnumerateFiles(Combine(dir, sub), relPrefix.empty() ? sub : relPrefix + L"\\" + sub, relOut);
}

}  // namespace

W BackupRootOf(const W& contentPath) { return Combine(contentPath, kBackupRootName); }

W BackupDirOf(const W& contentPath, const W& id) { return Combine(BackupRootOf(contentPath), id); }

bool IsBackedUp(const W& contentPath, const W& id) {
    if (IsNullOrEmpty(id)) return false;
    const W dir = BackupDirOf(contentPath, id);
    return DirExists(dir) && FileExists(Combine(dir, kMarkerName));
}

BackupResult BackupFolder(const W& sourceDir, const W& contentPath, const W& id) {
    BackupResult r;
    if (sourceDir.empty() || !DirExists(sourceDir)) {
        r.error = W(A8("源目录不存在: ")) + sourceDir;
        return r;
    }
    if (id.empty()) {
        r.error = A8("缺少工坊 ID，无法备份");
        return r;
    }
    const W backupDir = BackupDirOf(contentPath, id);
    if (!EnsureDirTree(backupDir)) {
        const DWORD e = GetLastError();
        r.error = L"CreateDirectory: err=" + W(std::to_wstring(e));
        LogWriteErr(W(A8("创建备份目录失败: ")) + backupDir, r.error);
        return r;
    }

    std::vector<W> rels;
    EnumerateFiles(sourceDir, L"", rels);

    W firstError;
    for (const W& rel : rels) {
        const W file = Combine(sourceDir, rel);
        const W target = Combine(backupDir, rel);
        const W arrow = W(A8(" → "));
        if (!EnsureDirTree(DirName(target))) {
            firstError = W(A8("创建目标目录失败")) + rel;
            LogWriteErr(W(A8("备份单文件失败: ")) + file + arrow + target, firstError);
            continue;
        }
        if (FileExists(target)) {
            ++r.skipped;
            if (!IsSameFile(file, target)) {
                W line = W(A8("备份目标已存在且非同名硬链接,跳过: ")) + target;
                line += W(A8(" (来自 ")) + file + L')';
                LogWrite(line);
            }
            continue;
        }
        if (CreateHardLinkW(Ext(target).c_str(), Ext(file).c_str(), nullptr)) {
            ++r.linked;
            continue;
        }
        const DWORD err = GetLastError();
        if (err == kErrorAlreadyExists) {
            ++r.skipped;
            continue;
        }
        wchar_t hexbuf[16];
        swprintf(hexbuf, 16, L"0x%08X", err);
        const W detail = W(A8("创建硬链接失败(")) + hexbuf + W(L"): ") + file + arrow + target;
        if (firstError.empty()) firstError = detail;
        LogWrite(detail);
    }

    if (firstError.empty()) {
        if (!WriteFileUtf8(Combine(backupDir, kMarkerName), W(A8("created=")) + NowStamp() + L"\n"))
            LogWriteErr(W(A8("写入备份完成标记失败: ")) + backupDir, A8("写入失败"));
    } else {
        LogWrite(W(A8("备份 ")) + id + W(A8(" 不完整,不写完成标记(下次会重试): ")) + firstError);
    }
    r.error = firstError;
    return r;
}

}  // namespace ab
