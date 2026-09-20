#include "ab.h"

#include <string.h>

namespace ab {
namespace {

const W kAutoBackup = L"AutoBackup";
const W kPath = L"Path";
const W kVdfPath = L"VdfPath";
const W kWorkshopPath = L"WorkshopPath";

bool Eq(const wchar_t* a, const wchar_t* b) { return wcscmp(a, b) == 0; }

bool IsWs(wchar_t c) {
    return c == L' ' || c == L'\t' || c == L'\r' || c == L'\n' || c == 0x000B || c == 0x000C;
}

size_t SkipWsMin(const W& t, size_t i) {                       // 至少一个空白,等价 \s+
    size_t n = 0;
    while (i < t.size() && IsWs(t[i])) { ++i; ++n; }
    return n == 0 ? W::npos : i;
}

bool DigitsQuoted(const W& t, size_t i, W& out, size_t& end) {  // "(\d+)"
    if (i >= t.size() || t[i] != L'"') return false;
    ++i;
    const size_t start = i;
    while (i < t.size() && t[i] >= L'0' && t[i] <= L'9') ++i;
    if (i == start || i >= t.size() || t[i] != L'"') return false;
    out = t.substr(start, i - start);
    end = i + 1;
    return true;
}

// 在 [from, limit) 内找第一个 key + \s+ + "(\d+)"
bool FindKeyDigits(const W& t, const wchar_t* key, W& val, size_t& end) {
    const size_t keyLen = wcslen(key);
    for (size_t at = t.find(key); at != W::npos; at = t.find(key, at + 1)) {
        const size_t j = SkipWsMin(t, at + keyLen);
        size_t e = 0;
        if (j != W::npos && DigitsQuoted(t, j, val, e)) {
            end = e;
            return true;
        }
    }
    return false;
}

// 与主程序 WallpaperScanner 同一套两级匹配(旧版这里是一条合并正则,要求两个键同现且有序,
// 缺键或换序就整条静默丢掉):
//   叶子块 = "(\d+)" \s* { [^{}]* }        —— 块体不得含任何花括号
//   块体内独立找 publishedfileid / disabled_locally;后者缺失或值 != "1" 都算未停用
bool NextLeafBlock(const W& t, size_t from, W& body, size_t& resume) {
    for (size_t i = from; i < t.size(); ++i) {
        if (t[i] != L'"') continue;
        size_t j = i + 1;
        size_t digits = 0;
        while (j < t.size() && t[j] >= L'0' && t[j] <= L'9') { ++j; ++digits; }
        if (digits == 0 || j >= t.size() || t[j] != L'"') continue;
        ++j;                                                  // 跳过收尾引号
        while (j < t.size() && IsWs(t[j])) ++j;                // \s*
        if (j >= t.size() || t[j] != L'{') continue;
        const size_t bodyStart = j + 1;
        const size_t close = t.find(L'}', bodyStart);
        if (close == W::npos) return false;                   // 后面再没有闭合,继续找也没意义
        const size_t open = t.find(L'{', bodyStart);
        if (open != W::npos && open < close) continue;        // 含嵌套 = 不是叶子块
        body = t.substr(bodyStart, close - bodyStart);
        resume = close + 1;                                   // .NET 的 Matches 是非重叠的
        return true;
    }
    return false;
}

}  // namespace

bool ConfigLoad(Cfg& out) {
    const W path = Combine(DataRoot(), L"config.json");
    if (!FileExists(path)) {
        LogWrite(W(A8("config.json 不存在: ")) + path);
        return false;
    }
    bool ok = false;
    const W text = ReadFileUtf8(path, ok);
    if (!ok) {
        LogWriteErr(A8("配置解析失败"), A8("读取文件失败"));
        return false;
    }
    JVal root;
    if (!JsonParse(text, root)) {
        LogWriteErr(A8("配置解析失败"), A8("JSON 不合法"));
        return false;
    }
    if (root.kind == JVal::JNull) return false;                // C#: 反序列化出 null,静默返回
    if (root.kind != JVal::JObj) {
        LogWriteErr(A8("配置解析失败"), A8("根节点不是对象"));
        return false;
    }

    if (const JVal* ab = root.Find(kAutoBackup); ab && ab->kind == JVal::JObj) {
        out.hasAutoBackup = true;
        AutoBackupCfg& a = out.auto_;
        a.Enabled = ab->Bool(L"Enabled", a.Enabled);
        a.ServiceEnabled = ab->Bool(L"ServiceEnabled", a.ServiceEnabled);
        a.TypeScene = ab->Bool(L"TypeScene", a.TypeScene);
        a.TypeVideo = ab->Bool(L"TypeVideo", a.TypeVideo);
        a.TypeWeb = ab->Bool(L"TypeWeb", a.TypeWeb);
        a.TypeApplication = ab->Bool(L"TypeApplication", a.TypeApplication);
        a.TypePreset = ab->Bool(L"TypePreset", a.TypePreset);
        a.TypeUnknown = ab->Bool(L"TypeUnknown", a.TypeUnknown);
        a.RatingG = ab->Bool(L"RatingG", a.RatingG);
        a.RatingPg = ab->Bool(L"RatingPg", a.RatingPg);
        a.RatingR = ab->Bool(L"RatingR", a.RatingR);
    }
    if (const JVal* p = root.Find(kPath); p && p->kind == JVal::JObj) {
        out.hasPath = true;
        if (const W* v = p->Str(kVdfPath)) out.vdfPath = *v;
        if (const W* w = p->Str(kWorkshopPath)) out.workshopPath = *w;
    }

    LogWrite(W(A8("已读配置: AutoBackup.Enabled=")) + (out.auto_.Enabled ? L"True" : L"False") +
             W(A8(", ServiceEnabled=")) + (out.auto_.ServiceEnabled ? L"True" : L"False"));
    return true;
}

bool ConfigActive(const Cfg& c) {
    return c.hasAutoBackup && c.auto_.Enabled && c.auto_.ServiceEnabled && c.hasPath &&
           !IsNullOrEmpty(c.vdfPath) && !IsNullOrEmpty(c.workshopPath);
}

ProjectMeta ReadProjectMeta(const W& projectJsonPath) {
    ProjectMeta m;
    bool ok = false;
    const W text = ReadFileUtf8(projectJsonPath, ok);
    if (!ok) return m;
    JVal root;
    if (!JsonParse(text, root) || root.kind != JVal::JObj) return m;
    m.ok = true;                                               // 两键缺失也算成功:C# 会给 null 属性,归一化成 unknown/g
    if (const W* type = root.Str(L"type")) m.type = *type;
    if (const W* rating = root.Str(L"contentrating")) m.contentrating = *rating;
    return m;
}

bool FilterMatches(const Cfg& c, const ProjectMeta& m) {
    if (!c.hasAutoBackup || !m.ok) return false;
    const AutoBackupCfg& a = c.auto_;
    const bool typeOk = [&] {
        if (m.type.empty()) return a.TypeUnknown;
        const W t = TrimLower(m.type);
        if (Eq(t.c_str(), L"scene")) return a.TypeScene;
        if (Eq(t.c_str(), L"video")) return a.TypeVideo;
        if (Eq(t.c_str(), L"web")) return a.TypeWeb;
        if (Eq(t.c_str(), L"application")) return a.TypeApplication;
        if (Eq(t.c_str(), L"preset")) return a.TypePreset;
        return a.TypeUnknown;
    }();
    if (!typeOk) return false;
    const W r = m.contentrating.empty() ? L"g" : [&] {
        const W t = TrimLower(m.contentrating);
        if (Eq(t.c_str(), L"teen")) return L"pg";
        if (Eq(t.c_str(), L"mature")) return L"r";
        return L"g";
    }();
    if (r == L"pg") return a.RatingPg;
    if (r == L"r") return a.RatingR;
    return a.RatingG;
}

std::vector<W> VdfSubscribedIds(const W& vdfPath) {
    std::vector<W> result;
    if (IsNullOrEmpty(vdfPath) || !FileExists(vdfPath)) {
        LogWrite(W(A8("VDF 不存在: ")) + vdfPath);
        return result;
    }
    bool ok = false;
    const W content = ReadFileUtf8(vdfPath, ok);
    if (!ok) {
        LogWriteErr(W(A8("解析 VDF 失败: ")) + vdfPath, A8("读取文件失败"));
        return result;
    }
    static const wchar_t kPid[] = L"\"publishedfileid\"";
    static const wchar_t kDis[] = L"\"disabled_locally\"";
    size_t pos = 0;
    W body;
    size_t resume = 0;
    while (NextLeafBlock(content, pos, body, resume)) {
        pos = resume;
        W id;
        size_t e = 0;
        if (!FindKeyDigits(body, kPid, id, e)) continue;
        W dis;
        size_t e2 = 0;
        if (FindKeyDigits(body, kDis, dis, e2) && dis == L"1") continue;   // 显式停用才算排除
        bool dup = false;
        for (const W& s : result)
            if (s == id) { dup = true; break; }
        if (!dup) result.push_back(id);
    }
    wchar_t buf[32];
    swprintf(buf, 32, L"%zu", result.size());
    LogWrite(W(A8("VDF 解析完成: 有效订阅 ")) + buf + W(A8(" 个")));
    return result;
}

}  // namespace ab
