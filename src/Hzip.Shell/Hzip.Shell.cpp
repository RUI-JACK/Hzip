// Hzip's native Windows 11 IExplorerCommand server. No CLR or compression in Explorer.
#define UNICODE
#define _UNICODE
#include <windows.h>
#include <shobjidl.h>
#include <shlwapi.h>
#include <atomic>
#include <string>
#include <vector>
#include <new>

static HMODULE module;
static std::atomic<long> objects{0};
static const CLSID CLSID_Hzip = {0x087fcbd1,0x33e8,0x4d8d,{0xa2,0x56,0x7b,0x09,0xec,0x32,0x06,0x81}};
static std::wstring AppDirectory() {
    std::vector<wchar_t> path(32768);
    DWORD n=GetModuleFileNameW(module,path.data(),static_cast<DWORD>(path.size()));
    if(!n || n>=path.size()) return {};
    std::wstring result(path.data(),n);
    result.resize(result.find_last_of(L"\\/")); // shell directory
    result.resize(result.find_last_of(L"\\/")); // application directory
    return result;
}
static HRESULT CopyText(const std::wstring& text, PWSTR* output) {
    if(!output) return E_POINTER;
    *output=static_cast<PWSTR>(CoTaskMemAlloc((text.size()+1)*sizeof(wchar_t)));
    if(!*output) return E_OUTOFMEMORY;
    memcpy(*output,text.c_str(),(text.size()+1)*sizeof(wchar_t)); return S_OK;
}
static std::wstring Quote(const std::wstring& text) {
    std::wstring result=L"\""; size_t slashes=0;
    for(wchar_t c:text) {
        if(c==L'\\') { ++slashes; continue; }
        if(c==L'\"') { result.append(slashes*2+1,L'\\'); result+=c; }
        else { result.append(slashes,L'\\'); result+=c; }
        slashes=0;
    }
    result.append(slashes*2,L'\\'); result+=L'\"'; return result;
}
static HRESULT Selected(IShellItemArray* items,std::vector<std::wstring>& paths,bool archiveOnly=false) {
    if(!items) return E_INVALIDARG;
    DWORD count=0; HRESULT hr=items->GetCount(&count);
    if(FAILED(hr)) return hr;
    if(!count || count>1024) return E_INVALIDARG;
    for(DWORD i=0;i<count;i++) {
        IShellItem* item=nullptr; hr=items->GetItemAt(i,&item); if(FAILED(hr)) return hr;
        // ZIP files are shell folders too; SFGAO_STREAM distinguishes them from directories.
        SFGAOF attributes=0; HRESULT attributeResult=item->GetAttributes(SFGAO_FOLDER|SFGAO_STREAM,&attributes);
        if(archiveOnly && SUCCEEDED(attributeResult) && (attributes&SFGAO_FOLDER) && !(attributes&SFGAO_STREAM)) {item->Release();return S_FALSE;}
        PWSTR path=nullptr; hr=item->GetDisplayName(SIGDN_FILESYSPATH,&path); item->Release();
        if(FAILED(hr)) return hr;
        std::wstring name(path); CoTaskMemFree(path);
        if(archiveOnly) {
            const wchar_t* ext=PathFindExtensionW(name.c_str());
            static const wchar_t* extensions[]={L".zip",L".zipx",L".7z",L".rar",L".tar",L".gz",L".tgz",L".xz",L".txz",L".bz2",L".tbz2",L".iso",L".cab",L".lzh",L".lha",L".wim",L".arj",L".dmg",L".001",L".zst",L".lzma",L".z01"};
            bool match=false; for(auto candidate:extensions) if(!_wcsicmp(ext,candidate)) {match=true;break;}
            if(!match) return S_FALSE;
        }
        paths.push_back(std::move(name));
    }
    return S_OK;
}
// Keep filename rules aligned with QuickArchivePlan; no compression runs in this server.
static std::wstring ParentPath(const std::wstring& path) {
    auto cut=path.find_last_of(L"\\/"); if(cut==std::wstring::npos)return {};
    return path.substr(0,cut==2&&path.size()>2&&path[1]==L':'?3:cut);
}
static std::wstring BaseName(std::wstring path) {
    while(path.size()>3&&(path.back()==L'\\'||path.back()==L'/'))path.pop_back();
    auto cut=path.find_last_of(L"\\/");return cut==std::wstring::npos?path:path.substr(cut+1);
}
static std::wstring ArchiveName(const std::vector<std::wstring>& paths,const wchar_t* extension) {
    std::wstring stem=BaseName(paths.size()==1?paths[0]:ParentPath(paths[0]));
    if(paths.size()==1) {
        DWORD attributes=GetFileAttributesW(paths[0].c_str());
        if(attributes!=INVALID_FILE_ATTRIBUTES&&!(attributes&FILE_ATTRIBUTE_DIRECTORY)) {
            auto dot=stem.find_last_of(L'.');if(dot!=std::wstring::npos&&dot>0)stem.resize(dot);
        }
    }
    if(stem.empty()||stem.find_first_of(L"<>:\"/\\|?*")!=std::wstring::npos)stem=L"归档";
    if(stem.size()>240){stem.resize(240);if(stem.back()>=0xd800&&stem.back()<=0xdbff)stem.pop_back();}
    return stem+extension;
}
static std::wstring ExtractFolderName(const std::vector<std::wstring>& paths) {
    if(paths.size()!=1)return L"各自同名文件夹";
    std::wstring name=BaseName(paths[0]);
    static const wchar_t* suffixes[]={L".tar.gz",L".tar.xz",L".tar.bz2",L".tar.zst",L".7z.001",L".zip.001"};
    for(auto suffix:suffixes){size_t n=wcslen(suffix);if(name.size()>=n&&!_wcsicmp(name.c_str()+name.size()-n,suffix))return name.substr(0,name.size()-n);}
    auto dot=name.find_last_of(L'.');return dot==std::wstring::npos?name:name.substr(0,dot);
}
static constexpr ULONG ActionCount=8;
static constexpr int ActionOrder[ActionCount]={4,5,3,6,2,7,8,1};
class Command;
class Commands final:public IEnumExplorerCommand {
    std::atomic<ULONG> refs{1}; ULONG position=0;
public:
    Commands(){++objects;} ~Commands(){--objects;}
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid,void** out) override {if(!out)return E_POINTER;*out=nullptr;if(iid==IID_IUnknown||iid==IID_IEnumExplorerCommand){*out=this;AddRef();return S_OK;}return E_NOINTERFACE;}
    ULONG STDMETHODCALLTYPE AddRef() override{return ++refs;}
    ULONG STDMETHODCALLTYPE Release() override{ULONG n=--refs;if(!n)delete this;return n;}
    HRESULT STDMETHODCALLTYPE Next(ULONG count,IExplorerCommand** out,ULONG* fetched) override;
    HRESULT STDMETHODCALLTYPE Skip(ULONG count) override {ULONG available=ActionCount-position;position+=count<available?count:available;return count<=available?S_OK:S_FALSE;}
    HRESULT STDMETHODCALLTYPE Reset() override {position=0;return S_OK;}
    HRESULT STDMETHODCALLTYPE Clone(IEnumExplorerCommand** out) override {if(!out)return E_POINTER;auto copy=new(std::nothrow) Commands();if(!copy)return E_OUTOFMEMORY;copy->position=position;*out=copy;return S_OK;}
};
class Command final:public IExplorerCommand {
    std::atomic<ULONG> refs{1}; int action;
public:
    explicit Command(int value=0):action(value){++objects;} ~Command(){--objects;}
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid,void** out) override {if(!out)return E_POINTER;*out=nullptr;if(iid==IID_IUnknown||iid==IID_IExplorerCommand){*out=this;AddRef();return S_OK;}return E_NOINTERFACE;}
    ULONG STDMETHODCALLTYPE AddRef() override{return ++refs;}
    ULONG STDMETHODCALLTYPE Release() override{ULONG n=--refs;if(!n)delete this;return n;}
    HRESULT STDMETHODCALLTYPE GetTitle(IShellItemArray* items,PWSTR* out) override {
        try {
            static const wchar_t* titles[]={L"Hzip",L"用 Hzip 打开…",L"智能解压到此处",L"新建压缩包 (Hzip)…",L"压缩为 ZIP (Hzip)",L"压缩为 7Z (Hzip)",L"解压到此处",L"解压到同名文件夹",L"解压到其他文件夹…"};
            if(action==4||action==5) {std::vector<std::wstring> paths;if(Selected(items,paths)==S_OK)return CopyText(L"压缩为“"+ArchiveName(paths,action==4?L".zip":L".7z")+L"”",out);}
            if(action==7){std::vector<std::wstring> paths;if(Selected(items,paths,true)==S_OK)return CopyText(L"解压到 "+ExtractFolderName(paths)+(paths.size()==1?L"\\":L""),out);}
            return CopyText(titles[action],out);
        }catch(...){return E_OUTOFMEMORY;}
    }
    HRESULT STDMETHODCALLTYPE GetIcon(IShellItemArray*,PWSTR* out) override {try{return CopyText(AppDirectory()+L"\\Hzip.ico,0",out);}catch(...){return E_OUTOFMEMORY;}}
    HRESULT STDMETHODCALLTYPE GetToolTip(IShellItemArray*,PWSTR* out) override {if(!out)return E_POINTER;*out=nullptr;return E_NOTIMPL;}
    HRESULT STDMETHODCALLTYPE GetCanonicalName(GUID* out) override {if(!out)return E_POINTER;*out=CLSID_Hzip;out->Data1+=action;return S_OK;}
    HRESULT STDMETHODCALLTYPE GetState(IShellItemArray* items,BOOL,EXPCMDSTATE* state) override {
        if(!state)return E_POINTER;*state=ECS_HIDDEN;
        try {
            std::vector<std::wstring> paths;HRESULT hr=Selected(items,paths);
            if(hr!=S_OK)return S_OK;
            std::vector<std::wstring> archives;bool archiveSelection=Selected(items,archives,true)==S_OK;
            bool compress=action==3||action==4||action==5,extract=action==2||action>=6;
            if((compress&&archiveSelection)||(extract&&!archiveSelection)||(action==1&&paths.size()!=1))return S_OK;
            if(action==4||action==5)for(const auto& path:paths)if(PathIsRootW(path.c_str()))return S_OK;
            *state=ECS_ENABLED;
            return S_OK;
        }catch(...){return E_OUTOFMEMORY;}
    }
    HRESULT STDMETHODCALLTYPE Invoke(IShellItemArray* items,IBindCtx*) override {
        if(!action)return E_NOTIMPL;
        try {
            EXPCMDSTATE state;HRESULT hr=GetState(items,FALSE,&state);if(FAILED(hr)||state!=ECS_ENABLED)return E_INVALIDARG;
            std::vector<std::wstring> paths;hr=Selected(items,paths);
            if(hr!=S_OK || (action==1&&paths.size()!=1))return E_INVALIDARG;
            std::wstring directory=AppDirectory(),exe=directory+L"\\Hzip.exe";
            std::wstring args=Quote(exe);
            if(action==4)args+=L" --quick-zip";
            else if(action==5)args+=L" --quick-7z";
            else if(action==3)args+=L" --create";
            else if(action==2)args+=L" --extract-many-here";
            else if(action==6)args+=L" --extract-direct";
            else if(action==7)args+=L" --extract-subfolder";
            else if(action==8)args+=L" --extract-to";
            else {std::vector<std::wstring> archives;if(Selected(items,archives,true)!=S_OK)args+=L" --open-from";}
            for(const auto& path:paths)args+=L" "+Quote(path);
            if(args.size()>30000)return HRESULT_FROM_WIN32(ERROR_FILENAME_EXCED_RANGE);
            STARTUPINFOW startup{sizeof(startup)};PROCESS_INFORMATION process{};
            if(!CreateProcessW(exe.c_str(),args.data(),nullptr,nullptr,FALSE,0,nullptr,directory.c_str(),&startup,&process))return HRESULT_FROM_WIN32(GetLastError());
            CloseHandle(process.hThread);CloseHandle(process.hProcess);return S_OK;
        }catch(...){return E_OUTOFMEMORY;}
    }
    HRESULT STDMETHODCALLTYPE GetFlags(EXPCMDFLAGS* out) override {if(!out)return E_POINTER;*out=action?ECF_DEFAULT:ECF_HASSUBCOMMANDS;return S_OK;}
    HRESULT STDMETHODCALLTYPE EnumSubCommands(IEnumExplorerCommand** out) override {if(!out)return E_POINTER;*out=nullptr;if(action)return E_NOTIMPL;*out=new(std::nothrow) Commands();return *out?S_OK:E_OUTOFMEMORY;}
};
HRESULT Commands::Next(ULONG count,IExplorerCommand** out,ULONG* fetched) {
    if(!out || (!fetched&&count!=1))return E_POINTER;
    if(fetched)*fetched=0;ULONG done=0;
    for(;done<count&&position<ActionCount;done++,position++) {
        out[done]=new(std::nothrow) Command(ActionOrder[position]);
        if(!out[done]) {for(ULONG i=0;i<done;i++)out[i]->Release();return E_OUTOFMEMORY;}
    }
    if(fetched)*fetched=done;return done==count?S_OK:S_FALSE;
}
class Factory final:public IClassFactory {
    std::atomic<ULONG> refs{1};
public:
    Factory(){++objects;}~Factory(){--objects;}
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid,void** out) override {if(!out)return E_POINTER;*out=nullptr;if(iid==IID_IUnknown||iid==IID_IClassFactory){*out=this;AddRef();return S_OK;}return E_NOINTERFACE;}
    ULONG STDMETHODCALLTYPE AddRef() override{return ++refs;}
    ULONG STDMETHODCALLTYPE Release() override{ULONG n=--refs;if(!n)delete this;return n;}
    HRESULT STDMETHODCALLTYPE CreateInstance(IUnknown* outer,REFIID iid,void** out) override {if(!out)return E_POINTER;*out=nullptr;if(outer)return CLASS_E_NOAGGREGATION;auto command=new(std::nothrow) Command();if(!command)return E_OUTOFMEMORY;HRESULT hr=command->QueryInterface(iid,out);command->Release();return hr;}
    HRESULT STDMETHODCALLTYPE LockServer(BOOL lock) override {if(lock)++objects;else --objects;return S_OK;}
};
extern "C" HRESULT __stdcall DllGetClassObject(REFCLSID clsid,REFIID iid,void** out) {if(!out)return E_POINTER;*out=nullptr;if(clsid!=CLSID_Hzip)return CLASS_E_CLASSNOTAVAILABLE;auto factory=new(std::nothrow) Factory();if(!factory)return E_OUTOFMEMORY;HRESULT hr=factory->QueryInterface(iid,out);factory->Release();return hr;}
extern "C" HRESULT __stdcall DllCanUnloadNow(){return objects==0?S_OK:S_FALSE;}
BOOL WINAPI DllMain(HINSTANCE instance,DWORD reason,LPVOID){if(reason==DLL_PROCESS_ATTACH){module=instance;DisableThreadLibraryCalls(instance);}return TRUE;}
