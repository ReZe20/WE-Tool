#include "ab.h"

#include <string.h>

namespace ab {
namespace {

constexpr int kMaxDepth = 64;

struct P {
    const W& t;
    size_t i = 0;

    bool Eof() const { return i >= t.size(); }
    wchar_t Cur() const { return i < t.size() ? t[i] : L'\0'; }

    // ReadCommentHandling = Skip:.NET 两种注释形态都吃
    void SkipWs() {
        for (;;) {
            while (!Eof() && (Cur() == L' ' || Cur() == L'\t' || Cur() == L'\r' || Cur() == L'\n')) ++i;
            if (t.compare(i, 2, L"//") == 0) {
                const size_t nl = t.find(L'\n', i);
                i = nl == W::npos ? t.size() : nl;
                continue;
            }
            if (t.compare(i, 2, L"/*") == 0) {
                const size_t end = t.find(L"*/", i + 2);
                if (end == W::npos) return;                       // 未闭合:交给后面的语法判定报错
                i = end + 2;
                continue;
            }
            return;
        }
    }

    bool ParseString(W& out) {
        if (Cur() != L'"') return false;
        ++i;
        out.clear();
        while (!Eof()) {
            const wchar_t c = Cur();
            ++i;
            if (c == L'"') return true;
            if (c != L'\\') { out += c; continue; }
            if (Eof()) return false;
            const wchar_t e = Cur();
            ++i;
            switch (e) {
                case L'"': out += L'"'; break;
                case L'\\': out += L'\\'; break;
                case L'/': out += L'/'; break;
                case L'b': out += L'\b'; break;
                case L'f': out += 0x000C; break;
                case L'n': out += L'\n'; break;
                case L'r': out += L'\r'; break;
                case L't': out += L'\t'; break;
                case L'u': {
                    if (i + 4 > t.size()) return false;
                    wchar_t hi = 0;
                    for (int k = 0; k < 4; ++k) {
                        const wchar_t h = t[i + k];
                        const int v = (h >= L'0' && h <= L'9')   ? h - L'0'
                                    : (h >= L'a' && h <= L'f')   ? h - L'a' + 10
                                    : (h >= L'A' && h <= L'F')   ? h - L'A' + 10 : -1;
                        if (v < 0) return false;
                        hi = static_cast<wchar_t>(hi * 16 + v);
                    }
                    i += 4;
                    if (hi >= 0xD800 && hi <= 0xDBFF && t.compare(i, 2, L"\\u") == 0 && i + 6 <= t.size()) {
                        wchar_t lo = 0;
                        bool ok = true;
                        for (int k = 0; k < 4; ++k) {
                            const wchar_t h = t[i + 2 + k];
                            const int v = (h >= L'0' && h <= L'9') ? h - L'0'
                                        : (h >= L'a' && h <= L'f') ? h - L'a' + 10
                                        : (h >= L'A' && h <= L'F') ? h - L'A' + 10 : -1;
                            if (v < 0) { ok = false; break; }
                            lo = static_cast<wchar_t>(lo * 16 + v);
                        }
                        if (ok && lo >= 0xDC00 && lo <= 0xDFFF) {
                            out += hi; out += lo; i += 6;
                            break;
                        }
                    }
                    out += hi;
                    break;
                }
                default: return false;
            }
        }
        return false;
    }

    bool Parse(JVal& out, int depth) {
        if (depth > kMaxDepth) return false;
        SkipWs();
        if (Eof()) return false;
        const wchar_t c = Cur();
        if (c == L'{') {
            out.kind = JVal::JObj;
            ++i;
            SkipWs();
            if (Cur() == L'}') { ++i; return true; }
            for (;;) {
                SkipWs();
                W key;
                if (!ParseString(key)) return false;
                SkipWs();
                if (Cur() != L':') return false;
                ++i;
                JVal v;
                if (!Parse(v, depth + 1)) return false;
                out.keys.push_back(key);
                out.vals.push_back(v);
                SkipWs();                                       // AllowTrailingCommas:逗号后可直接 }
                if (Cur() == L',') { ++i; if (SkipToBrace()) return true; continue; }
                if (Cur() == L'}') { ++i; return true; }
                return false;
            }
        }
        if (c == L'[') {
            out.kind = JVal::JArr;
            ++i;
            SkipWs();
            if (Cur() == L']') { ++i; return true; }
            for (;;) {
                JVal v;
                if (!Parse(v, depth + 1)) return false;
                out.vals.push_back(v);
                SkipWs();
                if (Cur() == L',') { ++i; SkipWs(); if (Cur() == L']') { ++i; return true; } continue; }
                if (Cur() == L']') { ++i; return true; }
                return false;
            }
        }
        if (c == L'"') {
            out.kind = JVal::JStr;
            return ParseString(out.s);
        }
        if (t.compare(i, 4, L"true") == 0) { out.kind = JVal::JBool; out.b = true; i += 4; return true; }
        if (t.compare(i, 5, L"false") == 0) { out.kind = JVal::JBool; out.b = false; i += 5; return true; }
        if (t.compare(i, 4, L"null") == 0) { out.kind = JVal::JNull; i += 4; return true; }
        const size_t start = i;
        while (!Eof() && (Cur() == L'-' || Cur() == L'+' || Cur() == L'.' || Cur() == L'e' ||
                          Cur() == L'E' || (Cur() >= L'0' && Cur() <= L'9'))) ++i;
        if (i == start) return false;
        out.kind = JVal::JNum;
        out.s = t.substr(start, i - start);
        return true;
    }

    void SkipWsAfterComma() { SkipWs(); }

    bool SkipToBrace() {
        const size_t save = i;
        SkipWs();
        if (Cur() == L'}') { ++i; return true; }
        i = save;
        return false;
    }
};

}  // namespace

const JVal* JVal::Find(const W& name) const {
    if (kind != JObj) return nullptr;
    for (size_t k = keys.size(); k-- > 0;)                    // 重复键后者胜,与 System.Text.Json 一致
        if (IEquals(keys[k], name)) return &vals[k];
    return nullptr;
}

const W* JVal::Str(const W& name) const {
    const JVal* v = Find(name);
    return v && v->kind == JVal::JStr ? &v->s : nullptr;
}

bool JVal::Bool(const W& name, bool fallback) const {
    const JVal* v = Find(name);
    return v && v->kind == JVal::JBool ? v->b : fallback;
}

bool JsonParse(const W& text, JVal& out) {
    if (text.empty()) return false;
    P p{text};
    if (!p.Parse(out, 0)) return false;
    p.SkipWsAfterComma();
    return p.Eof();                                          // 尾部还有内容 = JSON 不合法
}

}  // namespace ab
