#include "ab.h"

#include <shellapi.h>
#include <string.h>

namespace ab {
namespace {

BOOL WINAPI CtrlHandler(DWORD type) {
    switch (type) {
        case CTRL_C_EVENT:
        case CTRL_BREAK_EVENT:
        case CTRL_CLOSE_EVENT:
        case CTRL_LOGOFF_EVENT:
        case CTRL_SHUTDOWN_EVENT:
            SignalStop();
            return TRUE;
        default: return FALSE;
    }
}

W OnOff(bool v) { return v ? W(L"True") : W(L"False"); }

// C# 里 auto?.TypeScene 这类可空插值,缺失时打印空串而不是 False —— 与 ?? false 的两种形态要分开
W Opt(bool present, bool v) { return present ? OnOff(v) : W(); }

W Num(int n) {
    wchar_t buf[16];
    swprintf(buf, 16, L"%d", n);
    return buf;
}

W NulPath(bool hasPath, const W& v) { return hasPath ? v : W(A8("(空)")); }

int Verify(const Cfg& c) {
    const AutoBackupCfg* a = c.hasAutoBackup ? &c.auto_ : nullptr;
    WriteConsoleLine(W(A8("Enabled=")) + OnOff(a ? a->Enabled : false));
    WriteConsoleLine(W(A8("ServiceEnabled=")) + OnOff(a ? a->ServiceEnabled : false));
    WriteConsoleLine(W(A8("VdfPath=")) + NulPath(c.hasPath, c.vdfPath));
    WriteConsoleLine(W(A8("WorkshopPath=")) + NulPath(c.hasPath, c.workshopPath));
    WriteConsoleLine(W(A8("筛选: Scene=")) + Opt(a, a && a->TypeScene) + W(A8(" Video=")) +
                     Opt(a, a && a->TypeVideo) + W(A8(" Web=")) + Opt(a, a && a->TypeWeb) +
                     W(A8(" App=")) + Opt(a, a && a->TypeApplication) + W(A8(" Preset=")) +
                     Opt(a, a && a->TypePreset) + W(A8(" Unknown=")) + Opt(a, a && a->TypeUnknown));
    WriteConsoleLine(W(A8("      G=")) + Opt(a, a && a->RatingG) + W(A8(" Pg=")) +
                     Opt(a, a && a->RatingPg) + W(A8(" R=")) + Opt(a, a && a->RatingR));
    const bool active = ConfigActive(c);
    WriteConsoleLine(active ? W(L"STATUS=ACTIVE") : W(L"STATUS=INACTIVE"));
    return active ? 0 : 1;
}

int Once(const Cfg& c) {
    if (!ConfigActive(c)) {
        LogWrite(A8("自动备份未启用(Enabled/ServiceEnabled/路径任一缺失)"));
        return 1;
    }
    LogWrite(W(A8("补齐完成,新增备份 ")) + Num(BackupAllMissing(c)) + W(A8(" 个")));
    return 0;
}

int Run(const Cfg& c) {
    if (!ConfigActive(c)) {
        LogWrite(A8("自动备份未启用(Enabled/ServiceEnabled/路径任一缺失),服务退出"));
        return 1;
    }
    BackupAllMissing(c);
    StartWatch(c);
    LogWrite(A8("AutoBackupService 常驻运行中,按 Ctrl+C 退出"));
    WaitStop(INFINITE);
    StopWatch();
    LogWrite(A8("服务停止"));
    return 0;
}

}  // namespace

int MainEntry(int argc, wchar_t** argv) {
    std::vector<W> args;
    args.reserve(argc);
    for (int i = 0; i < argc; ++i) args.push_back(argv[i]);

    W dataRoot;
    for (size_t i = 0; i + 1 < args.size(); ++i) {
        if (_wcsicmp(args[i].c_str(), L"--data-dir") == 0) {
            dataRoot = args[i + 1];
            break;
        }
    }
    if (dataRoot.empty()) dataRoot = Combine(LocalAppData(), L"WE_Tool");
    InitLog(dataRoot);

    const W mode = !args.empty() ? LowerAscii(args[0]) : W(A8("--verify"));

    InitEvents();
    SetConsoleCtrlHandler(CtrlHandler, TRUE);

    Cfg cfg;
    if (!ConfigLoad(cfg)) {
        // C# 同序:非 --run 时静默退(日志已由 ConfigLoad 写过或写不出)
        if (mode != L"--run") return 1;
        LogWrite(A8("配置加载失败,服务退出"));
        return 1;
    }

    if (mode == L"--run") return Run(cfg);
    if (mode == L"--once") return Once(cfg);
    if (mode == L"--verify") return Verify(cfg);
    WriteConsoleLine(A8("未知参数。用法: AutoBackupService [--run|--once|--verify] [--data-dir <path>]"));
    return Verify(cfg);
}

}  // namespace ab

int APIENTRY wWinMain(HINSTANCE, HINSTANCE, LPWSTR, int) {
    int argc = 0;
    LPWSTR* argv = CommandLineToArgvW(GetCommandLineW(), &argc);
    if (!argv) return 1;
    // CommandLineToArgvW 的第 0 项是 exe 路径,.NET 的 args[] 不含它
    const int rc = ab::MainEntry(argc > 0 ? argc - 1 : 0, argv + 1);
    LocalFree(argv);
    return rc;
}
