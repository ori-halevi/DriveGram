// DriveGram shell extension: Explorer overlay icons (is the file saved in Telegram yet, how much of it is on
// this PC) and the
// right-click menu ("prepare on this PC" / "free up from this PC") for files on DriveGram drives.
//
// It knows nothing by itself: it asks the DriveGram tray app over a named pipe of this Windows session
// (DriveGram.Shell.<session>), with a short timeout, and does nothing at all when the tray app is not
// running or the file is not on a DriveGram drive. Protocol (UTF-8, one request per call):
//   S \t path                → the file's state, one letter (see State below) or "-" (not ours)
//   M \t path \x1f path ...  → info \x1f prepare \x1f free   (an empty item: not offered)
//   P|F \t path \x1f ...     → prepare / free; answer "ok"
//
// Build (x64): cl /nologo /std:c++17 /utf-8 /O2 /EHsc /LD /MT DriveGramShell.cpp /link /DEF:DriveGramShell.def ole32.lib shell32.lib shlwapi.lib advapi32.lib user32.lib

#include <windows.h>
#include <shlobj.h>
#include <shlwapi.h>
#include <winnetwk.h>
#include <aclapi.h>
#include <new>
#include <string>
#include <vector>
#include <mutex>
#include <unordered_map>

// {3A8D5C75-0A14-4F04-AC1B-82799D1FF05A}  overlay: saved in Telegram, all of it on this PC (may be freed)
static const CLSID CLSID_OverlayFull = { 0x3a8d5c75, 0x0a14, 0x4f04, { 0xac, 0x1b, 0x82, 0x79, 0x9d, 0x1f, 0xf0, 0x5a } };
// {006DEF2B-A011-41AA-8849-4968A8C56AD6}  overlay: saved in Telegram, part of it on this PC
static const CLSID CLSID_OverlayPart = { 0x006def2b, 0xa011, 0x41aa, { 0x88, 0x49, 0x49, 0x68, 0xa8, 0xc5, 0x6a, 0xd6 } };
// {7A6202B0-1496-4AA2-93F9-32ADD9D26985}  overlay: saved in Telegram, prepared on this PC (kept until freed)
static const CLSID CLSID_OverlayKept = { 0x7a6202b0, 0x1496, 0x4aa2, { 0x93, 0xf9, 0x32, 0xad, 0xd9, 0xd2, 0x69, 0x85 } };
// {54A010A3-BBFE-429A-B67E-1625D8BCC7F0}  overlay: not in Telegram yet (waiting to upload, or uploading)
static const CLSID CLSID_OverlayUpload = { 0x54a010a3, 0xbbfe, 0x429a, { 0xb6, 0x7e, 0x16, 0x25, 0xd8, 0xbc, 0xc7, 0xf0 } };
// {DF25C5D1-CE6A-4208-82B8-711293B1114D}  overlay: not in Telegram yet, its upload is paused
static const CLSID CLSID_OverlayPaused = { 0xdf25c5d1, 0xce6a, 0x4208, { 0x82, 0xb8, 0x71, 0x12, 0x93, 0xb1, 0x11, 0x4d } };
// {3A0A7ED8-925A-4D76-AE2A-A88E6D8D83B8}  right-click menu
static const CLSID CLSID_Menu = { 0x3a0a7ed8, 0x925a, 0x4d76, { 0xae, 0x2a, 0xa8, 0x8e, 0x6d, 0x8d, 0x83, 0xb8 } };


static HINSTANCE g_inst = nullptr;
static long g_locks = 0, g_objects = 0;

// ── talking to the tray app ──────────────────────────────────────────────────
static std::wstring PipeName()
{
    DWORD session = 0;
    ProcessIdToSessionId(GetCurrentProcessId(), &session);
    return L"\\\\.\\pipe\\DriveGram.Shell." + std::to_wstring(session);
}

static std::string ToUtf8(const std::wstring& s)
{
    if (s.empty()) return {};
    int n = WideCharToMultiByte(CP_UTF8, 0, s.c_str(), (int)s.size(), nullptr, 0, nullptr, nullptr);
    std::string out(n, '\0');
    WideCharToMultiByte(CP_UTF8, 0, s.c_str(), (int)s.size(), out.data(), n, nullptr, nullptr);
    return out;
}

static std::wstring FromUtf8(const char* p, int len)
{
    if (len <= 0) return {};
    int n = MultiByteToWideChar(CP_UTF8, 0, p, len, nullptr, 0);
    std::wstring out(n, L'\0');
    MultiByteToWideChar(CP_UTF8, 0, p, len, out.data(), n);
    return out;
}

// The user (SID) a process runs as; empty when it cannot be read.
static std::vector<BYTE> UserOf(HANDLE process)
{
    std::vector<BYTE> sid;
    HANDLE token = nullptr;
    if (!OpenProcessToken(process, TOKEN_QUERY, &token)) return sid;
    DWORD len = 0;
    GetTokenInformation(token, TokenUser, nullptr, 0, &len);
    std::vector<BYTE> buf(len);
    if (len && GetTokenInformation(token, TokenUser, buf.data(), len, &len))
    {
        PSID p = reinterpret_cast<TOKEN_USER*>(buf.data())->User.Sid;
        DWORD n = GetLengthSid(p);
        sid.assign(static_cast<BYTE*>(p), static_cast<BYTE*>(p) + n);
    }
    CloseHandle(token);
    return sid;
}

// Pipe names are shared by the whole machine: another account could create ours first. Only a pipe owned
// by this same Windows user, in this same session, is answered (the owner is the pipe's own, not a
// process id that could be reused); the server may never act as Explorer (identification-level only).
static bool ServerIsMine(HANDLE pipe)
{
    static std::vector<BYTE> me = UserOf(GetCurrentProcess());
    if (me.empty()) return false;
    ULONG session = 0;
    DWORD mine = 0;
    ProcessIdToSessionId(GetCurrentProcessId(), &mine);
    if (!GetNamedPipeServerSessionId(pipe, &session) || session != mine) return false;
    PSID owner = nullptr;
    PSECURITY_DESCRIPTOR sd = nullptr;
    if (GetSecurityInfo(pipe, SE_KERNEL_OBJECT, OWNER_SECURITY_INFORMATION, &owner, nullptr, nullptr, nullptr, &sd) != ERROR_SUCCESS)
        return false;
    bool ok = owner && IsValidSid(owner) && EqualSid(owner, reinterpret_cast<PSID>(me.data()));
    LocalFree(sd);
    return ok;
}

// One request and its answer, all within timeoutMs: Explorer's threads call this, so a busy tray app must
// never hold them (CallNamedPipe's timeout covers only waiting for a free pipe, not the answer).
static bool Ask(const std::wstring& request, std::wstring& answer, DWORD timeoutMs)
{
    std::string req = ToUtf8(request);
    std::wstring name = PipeName();
    ULONGLONG deadline = GetTickCount64() + timeoutMs;
    auto left = [&]() -> DWORD { ULONGLONG now = GetTickCount64(); return now >= deadline ? 0 : (DWORD)(deadline - now); };
    HANDLE pipe;
    for (;;)
    {
        pipe = CreateFileW(name.c_str(), GENERIC_READ | GENERIC_WRITE, 0, nullptr, OPEN_EXISTING,
            FILE_FLAG_OVERLAPPED | SECURITY_SQOS_PRESENT | SECURITY_IDENTIFICATION, nullptr);
        if (pipe != INVALID_HANDLE_VALUE) break;
        if (GetLastError() != ERROR_PIPE_BUSY || left() == 0 || !WaitNamedPipeW(name.c_str(), left())) return false;
    }
    if (!ServerIsMine(pipe)) { CloseHandle(pipe); return false; }
    DWORD mode = PIPE_READMODE_MESSAGE;
    SetNamedPipeHandleState(pipe, &mode, nullptr, nullptr);
    OVERLAPPED ov = {};
    ov.hEvent = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    // waits for one overlapped write or read until the deadline; on time-out it is cancelled
    auto finish = [&](BOOL started, DWORD& n) -> bool
    {
        if (!started && GetLastError() != ERROR_IO_PENDING) return false;
        if (WaitForSingleObject(ov.hEvent, left()) != WAIT_OBJECT_0)
        {
            CancelIoEx(pipe, &ov);
            GetOverlappedResult(pipe, &ov, &n, TRUE);
            return false;
        }
        return GetOverlappedResult(pipe, &ov, &n, FALSE) != FALSE;
    };
    bool ok = false;
    std::vector<char> buf(16 * 1024);
    DWORD n = 0;
    if (ov.hEvent && finish(WriteFile(pipe, req.data(), (DWORD)req.size(), nullptr, &ov), n))
    {
        ResetEvent(ov.hEvent);
        if (finish(ReadFile(pipe, buf.data(), (DWORD)buf.size(), nullptr, &ov), n))
        {
            answer = FromUtf8(buf.data(), (int)n);
            ok = true;
        }
    }
    if (ov.hEvent) CloseHandle(ov.hEvent);
    CloseHandle(pipe);
    return ok;
}

// ── is this a file on a DriveGram drive? (asked a lot: cached) ───────────────
static std::mutex g_gate;
static std::unordered_map<wchar_t, std::pair<bool, ULONGLONG>> g_volumes;           // drive letter → DriveGram?
static std::unordered_map<std::wstring, std::pair<wchar_t, ULONGLONG>> g_state;      // path → state

static bool OnDriveGramVolume(const wchar_t* path)
{
    if (!path || !path[0] || path[1] != L':' || path[2] != L'\\') return false;
    wchar_t letter = (wchar_t)towupper(path[0]);
    ULONGLONG now = GetTickCount64();
    {
        std::lock_guard<std::mutex> lock(g_gate);
        auto it = g_volumes.find(letter);
        if (it != g_volumes.end() && now - it->second.second < 15000) return it->second.first;
    }
    wchar_t root[4] = { letter, L':', L'\\', 0 };
    wchar_t fs[MAX_PATH + 1] = {};
    UINT type = GetDriveTypeW(root);
    bool yes;
    if (type == DRIVE_REMOTE)
    {
        // a network letter: asking the volume itself hangs on a disconnected share (an offline NAS), so only
        // its name is read (\\DriveGram\<drive> is ours; the name comes from Windows, not the network)
        wchar_t device[3] = { letter, L':', 0 };
        wchar_t remote[MAX_PATH] = {};
        DWORD len = MAX_PATH;
        yes = WNetGetConnectionW(device, remote, &len) == NO_ERROR && _wcsnicmp(remote, L"\\\\DriveGram\\", 12) == 0;
    }
    else
        yes = (type == DRIVE_FIXED || type == DRIVE_REMOVABLE)
            && GetVolumeInformationW(root, nullptr, 0, nullptr, nullptr, nullptr, fs, MAX_PATH)
            && _wcsicmp(fs, L"DriveGram") == 0;
    std::lock_guard<std::mutex> lock(g_gate);
    g_volumes[letter] = { yes, now };
    return yes;
}

// A file's state, one letter (the tray app decides; one overlay each, so they never compete):
//   P  not in Telegram yet, its upload is paused      U  not in Telegram yet (waiting, or uploading)
//   K  saved, prepared on this PC (kept)               F  saved, all of it on this PC (may be freed)
//   H  saved, part of it on this PC                    0  saved, none of it here      -  not ours
// Asked once for all five overlays of a file (each one asks): kept for 2 s. The tray app tells Explorer
// to look again when a file changes state, a little after the change, so this never hides it for long.
static wchar_t State(const wchar_t* path)
{
    if (!OnDriveGramVolume(path)) return L'-';
    ULONGLONG now = GetTickCount64();
    std::wstring key(path);
    {
        std::lock_guard<std::mutex> lock(g_gate);
        auto it = g_state.find(key);
        if (it != g_state.end() && now - it->second.second < 2000) return it->second.first;
        if (g_state.size() > 4000) g_state.clear();
    }
    wchar_t state = L'-';
    std::wstring answer;
    if (Ask(L"S\t" + key, answer, 150) && answer.size() == 1) state = answer[0];
    std::lock_guard<std::mutex> lock(g_gate);
    g_state[key] = { state, now };
    return state;
}

static void Forget()
{
    std::lock_guard<std::mutex> lock(g_gate);
    g_state.clear();
}

static std::wstring ModuleDir()
{
    wchar_t p[MAX_PATH] = {};
    GetModuleFileNameW(g_inst, p, MAX_PATH);
    PathRemoveFileSpecW(p);
    return p;
}

// ── overlay icons ────────────────────────────────────────────────────────────
class Overlay : public IShellIconOverlayIdentifier
{
    long _refs = 1;
    wchar_t _state; // the State() letter this overlay stands for
public:
    explicit Overlay(wchar_t state) : _state(state) { InterlockedIncrement(&g_objects); }
    ~Overlay() { InterlockedDecrement(&g_objects); }

    IFACEMETHODIMP QueryInterface(REFIID riid, void** ppv) override
    {
        if (!ppv) return E_POINTER;
        if (riid == IID_IUnknown || riid == IID_IShellIconOverlayIdentifier) { *ppv = static_cast<IShellIconOverlayIdentifier*>(this); AddRef(); return S_OK; }
        *ppv = nullptr;
        return E_NOINTERFACE;
    }
    IFACEMETHODIMP_(ULONG) AddRef() override { return InterlockedIncrement(&_refs); }
    IFACEMETHODIMP_(ULONG) Release() override { long r = InterlockedDecrement(&_refs); if (r == 0) delete this; return r; }

    IFACEMETHODIMP IsMemberOf(LPCWSTR path, DWORD attrib) override
    {
        if (attrib & FILE_ATTRIBUTE_DIRECTORY) return S_FALSE;
        return State(path) == _state ? S_OK : S_FALSE;
    }
    IFACEMETHODIMP GetOverlayInfo(LPWSTR iconFile, int cch, int* index, DWORD* flags) override
    {
        // new file names in 1.3.1 (the "all here" look changed): Explorer's icon cache never shows the old one
        const wchar_t* name = _state == L'P' ? L"\\overlay-paused.ico" : _state == L'U' ? L"\\overlay-upload.ico"
            : _state == L'K' ? L"\\overlay-kept.ico" : _state == L'F' ? L"\\overlay-here.ico" : L"\\overlay-partial.ico";
        std::wstring file = ModuleDir() + name;
        if ((int)file.size() + 1 > cch) return E_FAIL;
        wcscpy_s(iconFile, cch, file.c_str());
        *index = 0;
        *flags = ISIOI_ICONFILE;
        return S_OK;
    }
    // "not in Telegram yet" first, should two ever match (they do not: one state per file)
    IFACEMETHODIMP GetPriority(int* priority) override { *priority = _state == L'P' ? 0 : _state == L'U' ? 10 : 50; return S_OK; }
};

// ── right-click menu ─────────────────────────────────────────────────────────
class Menu : public IShellExtInit, public IContextMenu
{
    long _refs = 1;
    std::vector<std::wstring> _paths;
    std::wstring _info, _prepare, _free;
    UINT _idPrepare = UINT_MAX, _idFree = UINT_MAX;
public:
    Menu() { InterlockedIncrement(&g_objects); }
    ~Menu() { InterlockedDecrement(&g_objects); }

    IFACEMETHODIMP QueryInterface(REFIID riid, void** ppv) override
    {
        if (!ppv) return E_POINTER;
        if (riid == IID_IUnknown || riid == IID_IShellExtInit) { *ppv = static_cast<IShellExtInit*>(this); AddRef(); return S_OK; }
        if (riid == IID_IContextMenu) { *ppv = static_cast<IContextMenu*>(this); AddRef(); return S_OK; }
        *ppv = nullptr;
        return E_NOINTERFACE;
    }
    IFACEMETHODIMP_(ULONG) AddRef() override { return InterlockedIncrement(&_refs); }
    IFACEMETHODIMP_(ULONG) Release() override { long r = InterlockedDecrement(&_refs); if (r == 0) delete this; return r; }

    IFACEMETHODIMP Initialize(PCIDLIST_ABSOLUTE, IDataObject* data, HKEY) override
    {
        _paths.clear();
        if (!data) return E_INVALIDARG;
        FORMATETC fe = { CF_HDROP, nullptr, DVASPECT_CONTENT, -1, TYMED_HGLOBAL };
        STGMEDIUM stm = {};
        if (FAILED(data->GetData(&fe, &stm))) return E_INVALIDARG;
        HDROP drop = static_cast<HDROP>(GlobalLock(stm.hGlobal));
        if (drop)
        {
            UINT n = DragQueryFileW(drop, 0xFFFFFFFF, nullptr, 0);
            for (UINT i = 0; i < n && i < 200; i++)
            {
                wchar_t p[MAX_PATH * 4] = {};
                if (DragQueryFileW(drop, i, p, ARRAYSIZE(p)) == 0) continue;
                // ours first (cached, no disk access): other files, even on a slow share, are left alone
                if (!OnDriveGramVolume(p)) { _paths.clear(); break; }
                // no look at the drive itself here (Explorer's thread: a busy drive would freeze it): the tray
                // knows from its index which paths are files, and offers nothing for a folder
                _paths.push_back(p);
            }
            GlobalUnlock(stm.hGlobal);
        }
        ReleaseStgMedium(&stm);
        return _paths.empty() ? E_FAIL : S_OK;
    }

    std::wstring Joined() const
    {
        std::wstring all;
        for (size_t i = 0; i < _paths.size(); i++) { if (i) all += L'\x1f'; all += _paths[i]; }
        return all;
    }

    IFACEMETHODIMP QueryContextMenu(HMENU menu, UINT index, UINT first, UINT, UINT flags) override
    {
        _idPrepare = _idFree = UINT_MAX; // Explorer may ask again on the same object: no command from before
        if ((flags & CMF_DEFAULTONLY) || _paths.empty()) return MAKE_HRESULT(SEVERITY_SUCCESS, 0, 0);
        std::wstring answer;
        if (!Ask(L"M\t" + Joined(), answer, 400) || answer.empty()) return MAKE_HRESULT(SEVERITY_SUCCESS, 0, 0);
        std::vector<std::wstring> parts;
        size_t start = 0;
        for (size_t i = 0; i <= answer.size(); i++)
            if (i == answer.size() || answer[i] == L'\x1f') { parts.push_back(answer.substr(start, i - start)); start = i + 1; }
        while (parts.size() < 3) parts.push_back(L"");
        _info = parts[0]; _prepare = parts[1]; _free = parts[2];

        UINT pos = index, used = 0;
        InsertMenuW(menu, pos++, MF_BYPOSITION | MF_SEPARATOR, 0, nullptr);
        if (!_info.empty()) InsertMenuW(menu, pos++, MF_BYPOSITION | MF_STRING | MF_GRAYED, first + used++, _info.c_str());
        if (!_prepare.empty()) { _idPrepare = used; InsertMenuW(menu, pos++, MF_BYPOSITION | MF_STRING, first + used++, _prepare.c_str()); }
        if (!_free.empty()) { _idFree = used; InsertMenuW(menu, pos++, MF_BYPOSITION | MF_STRING, first + used++, _free.c_str()); }
        InsertMenuW(menu, pos++, MF_BYPOSITION | MF_SEPARATOR, 0, nullptr);
        return MAKE_HRESULT(SEVERITY_SUCCESS, 0, used);
    }

    IFACEMETHODIMP InvokeCommand(CMINVOKECOMMANDINFO* info) override
    {
        if (!info || HIWORD(info->lpVerb) != 0) return E_INVALIDARG; // no text verbs
        UINT id = LOWORD(info->lpVerb);
        wchar_t op = id == _idPrepare ? L'P' : id == _idFree ? L'F' : 0;
        if (!op) return E_INVALIDARG;
        std::wstring answer;
        Ask(std::wstring(1, op) + L"\t" + Joined(), answer, 2000);
        Forget();
        return S_OK;
    }

    IFACEMETHODIMP GetCommandString(UINT_PTR, UINT, UINT*, CHAR*, UINT) override { return E_NOTIMPL; }
};

// ── class factory and DLL exports ────────────────────────────────────────────
static wchar_t OverlayState(REFCLSID clsid)
{
    return clsid == CLSID_OverlayPaused ? L'P' : clsid == CLSID_OverlayUpload ? L'U' : clsid == CLSID_OverlayKept ? L'K'
        : clsid == CLSID_OverlayFull ? L'F' : clsid == CLSID_OverlayPart ? L'H' : 0;
}

class Factory : public IClassFactory
{
    long _refs = 1;
    CLSID _clsid;
public:
    explicit Factory(REFCLSID clsid) : _clsid(clsid) { InterlockedIncrement(&g_objects); }
    ~Factory() { InterlockedDecrement(&g_objects); }
    IFACEMETHODIMP QueryInterface(REFIID riid, void** ppv) override
    {
        if (!ppv) return E_POINTER;
        if (riid == IID_IUnknown || riid == IID_IClassFactory) { *ppv = static_cast<IClassFactory*>(this); AddRef(); return S_OK; }
        *ppv = nullptr;
        return E_NOINTERFACE;
    }
    IFACEMETHODIMP_(ULONG) AddRef() override { return InterlockedIncrement(&_refs); }
    IFACEMETHODIMP_(ULONG) Release() override { long r = InterlockedDecrement(&_refs); if (r == 0) delete this; return r; }
    IFACEMETHODIMP CreateInstance(IUnknown* outer, REFIID riid, void** ppv) override
    {
        if (!ppv) return E_POINTER;
        *ppv = nullptr;
        if (outer) return CLASS_E_NOAGGREGATION;
        IUnknown* obj = nullptr;
        if (wchar_t state = OverlayState(_clsid)) obj = static_cast<IShellIconOverlayIdentifier*>(new (std::nothrow) Overlay(state));
        else if (_clsid == CLSID_Menu) obj = static_cast<IShellExtInit*>(new (std::nothrow) Menu());
        if (!obj) return E_OUTOFMEMORY;
        HRESULT hr = obj->QueryInterface(riid, ppv);
        obj->Release();
        return hr;
    }
    IFACEMETHODIMP LockServer(BOOL lock) override { if (lock) InterlockedIncrement(&g_locks); else InterlockedDecrement(&g_locks); return S_OK; }
};

BOOL APIENTRY DllMain(HMODULE module, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH) { g_inst = module; DisableThreadLibraryCalls(module); }
    return TRUE;
}

static bool Elevated()
{
    HANDLE token = nullptr;
    if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &token)) return true; // unknown: stay out
    TOKEN_ELEVATION e = {};
    DWORD len = 0;
    bool elevated = !GetTokenInformation(token, TokenElevation, &e, sizeof e, &len) || e.TokenIsElevated != 0;
    CloseHandle(token);
    return elevated;
}

STDAPI DllGetClassObject(REFCLSID clsid, REFIID riid, void** ppv)
{
    if (!ppv) return E_POINTER;
    *ppv = nullptr;
    if (!OverlayState(clsid) && clsid != CLSID_Menu) return CLASS_E_CLASSNOTAVAILABLE;
    if (Elevated()) return CLASS_E_CLASSNOTAVAILABLE; // never inside a program running as admin
    auto f = new (std::nothrow) Factory(clsid);
    if (!f) return E_OUTOFMEMORY;
    HRESULT hr = f->QueryInterface(riid, ppv);
    f->Release();
    return hr;
}

STDAPI DllCanUnloadNow() { return g_locks == 0 && g_objects == 0 ? S_OK : S_FALSE; }

// ── registration ─────────────────────────────────────────────────────────────
// Done by the installer, not by this DLL: the COM classes and the right-click menu PER USER (HKCU, no
// admin), and the five overlay NAMES machine-wide (Explorer reads them from HKLM only) with Windows' own
// reg.exe. An elevated program of this user CAN load this DLL (with UAC on, elevated processes still read
// the user's own COM registrations): DllGetClassObject refuses there (Elevated()), and DllMain does
// nothing, so no code of this file runs inside a program that runs as admin.
