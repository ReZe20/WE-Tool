// AutoBackupService C++ 版的公共声明。行为以 TestBackupContract/goldens 为唯一裁判,
// 这里的注释只说「为什么这样对齐 C#」,不复述代码在做什么。
#pragma once

#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>

#include <string>
#include <vector>

namespace ab {

using W = std::wstring;

// ---- support.cpp:文本、路径、日志 ----

W FromU8N(const char* utf8, size_t len);   // 长度明确:整文件字节流解码用这条
W FromU8(const char* utf8);                // NUL 结尾的字面量用这条
std::string ToU8(const W& s);
W LowerAscii(W s);
W TrimLower(const W& s);               // C#: Type.Trim().ToLowerInvariant() 的 ASCII 近似
bool IEquals(const W& a, const W& b);  // OrdinalIgnoreCase(ASCII)
bool IsNullOrEmpty(const W& s);
W NowStamp();                          // yyyy-MM-dd HH:mm:ss(本地时区,与 C# DateTime.Now 同)

// 只给裸 kernel32 调用用:.NET 的长路径规范化不吃 P/Invoke,不加前缀 >260 会 ERROR_PATH_NOT_FOUND
W Ext(const W& p);

W Combine(const W& a, const W& b);
W DirName(const W& p);
W BaseName(const W& p);
W LocalAppData();
bool FileExists(const W& p);           // 不加前缀(BCL 等价的普通路径语义)
bool DirExists(const W& p);
bool EnsureDir(const W& p);            // 单层;递归建目录用 EnsureDirTree
bool EnsureDirTree(const W& p);
W ReadFileUtf8(const W& path, bool& ok);
bool WriteFileUtf8(const W& path, const W& content);   // 无 BOM
void WriteConsoleLine(const W& line);                  // 等价 Console.WriteLine(CR LF)

void InitLog(const W& dataRoot);
W DataRoot();                                                  // --data-dir 或 %LOCALAPPDATA%\WE_Tool

// ---- 生命周期事件(watcher 线程与主线程共用) ----

void InitEvents();
HANDLE StopHandle();
void SignalStop();
bool Stopping();
bool WaitStop(DWORD ms);                                       // true = 等到停止信号
HANDLE WakeHandle();
void WakeLoop();                                               // 等价 SemaphoreSlim.Release()

void LogWrite(const W& msg);
void LogWriteErr(const W& context, const W& detail);   // 等价 C# Log.Write(ex, context):"{context}: {detail}"

#define A8(s) ::ab::FromU8(s)   // 源码里的中文消息常量都是 UTF-8 字面量,统一走这个

// ---- json.cpp ----

struct JVal {
    enum Kind { JNull, JBool, JNum, JStr, JArr, JObj } kind = JNull;
    bool b = false;
    W s;                                  // Num 存原文
    std::vector<W> keys;                   // Obj
    std::vector<JVal> vals;                // Obj 值 / Arr 元素

    const JVal* Find(const W& name) const;  // 大小写不敏感;重复键后者胜
    const W* Str(const W& name) const;
    bool Bool(const W& name, bool fallback) const;
};

// 严格解析,但容忍注释与尾逗号、剥 BOM —— 与 C# 源生成器的读盘选项一致
bool JsonParse(const W& text, JVal& out);

// ---- config.cpp:配置、VDF、筛选 ----

struct AutoBackupCfg {
    bool Enabled = false, ServiceEnabled = false;
    bool TypeScene = true, TypeVideo = true, TypeWeb = true;
    bool TypeApplication = true, TypePreset = true, TypeUnknown = true;
    bool RatingG = true, RatingPg = true, RatingR = true;
};

struct Cfg {
    bool hasAutoBackup = false;
    AutoBackupCfg auto_;
    bool hasPath = false;
    W vdfPath, workshopPath;
};

// 返回 false 表示 C# 里的 config == null(内部按同一顺序写日志)
bool ConfigLoad(Cfg& out);
bool ConfigActive(const Cfg& c);

struct ProjectMeta {
    bool ok = false;          // false = C# 的 null(解析失败/缺文件)
    W type, contentrating;
};

ProjectMeta ReadProjectMeta(const W& projectJsonPath);
bool FilterMatches(const Cfg& c, const ProjectMeta& m);

// 复刻 C# 那条合并正则的语义:publishedfileid 与 disabled_locally 必须同现且不跨 '}'
std::vector<W> VdfSubscribedIds(const W& vdfPath);

// ---- backup.cpp ----

struct BackupResult {
    int linked = 0, skipped = 0;
    W error;                               // 空 = C# 的 null
    bool Succeeded() const { return error.empty(); }
};

W BackupRootOf(const W& contentPath);
W BackupDirOf(const W& contentPath, const W& id);
bool IsBackedUp(const W& contentPath, const W& id);
BackupResult BackupFolder(const W& sourceDir, const W& contentPath, const W& id);

// ---- runner.cpp ----

int BackupAllMissing(const Cfg& c);
bool TryBackup(const W& contentPath, const W& id);   // 补齐与轮询共用同一份日志文案,别抄两遍

// ---- watch.cpp ----

// 起 VDF / downloads 监听线程 + 待处理轮询线程(对应 C# BackupRunner.StartWatch)
void StartWatch(const Cfg& c);
void StopWatch();                                              // 置停止事件 + CancelIoEx + join

// ---- main.cpp ----

// argv 已由 wWinMain 剥掉程序名,语义等价 C# 的 Main(string[] args)
int MainEntry(int argc, wchar_t** argv);

}  // namespace ab
