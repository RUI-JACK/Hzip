#ifndef UNICODE
#define UNICODE
#endif
#define _UNICODE
#include <windows.h>
#include <shobjidl.h>
#include <shlobj.h>
#include <shlwapi.h>
#include <iostream>
#include <string>
#include <vector>
#include <stdexcept>

static void Verify(bool ok,const char* message){if(!ok)throw std::runtime_error(message);}
static IShellItemArray* Selection(const std::vector<std::wstring>& paths) {
    std::vector<PIDLIST_ABSOLUTE> ids;for(const auto& path:paths){PIDLIST_ABSOLUTE id=nullptr;Verify(SUCCEEDED(SHParseDisplayName(path.c_str(),nullptr,&id,0,nullptr)),"parse real shell selection");ids.push_back(id);}
    IShellItemArray* result=nullptr;HRESULT hr=SHCreateShellItemArrayFromIDLists(static_cast<UINT>(ids.size()),const_cast<PCIDLIST_ABSOLUTE*>(ids.data()),&result);
    for(auto id:ids)CoTaskMemFree(id);Verify(SUCCEEDED(hr),"create real shell selection array");return result;
}
int wmain(int argc,wchar_t** argv) {
    if(argc<5)return 2;
    CoInitializeEx(nullptr,COINIT_APARTMENTTHREADED);
    try {
        HMODULE dll=LoadLibraryW(argv[1]);Verify(dll!=nullptr,"load native DLL with system dependencies only");
        auto getClass=reinterpret_cast<HRESULT(__stdcall*)(REFCLSID,REFIID,void**)>(GetProcAddress(dll,"DllGetClassObject"));
        auto canUnload=reinterpret_cast<HRESULT(__stdcall*)()>(GetProcAddress(dll,"DllCanUnloadNow"));Verify(getClass&&canUnload,"COM exports");
        CLSID clsid;CLSIDFromString(L"{087FCBD1-33E8-4D8D-A256-7B09EC320681}",&clsid);
        IClassFactory* factory=nullptr;Verify(SUCCEEDED(getClass(clsid,IID_IClassFactory,reinterpret_cast<void**>(&factory))),"class factory");
        IExplorerCommand* root=nullptr;Verify(SUCCEEDED(factory->CreateInstance(nullptr,IID_IExplorerCommand,reinterpret_cast<void**>(&root))),"native command activation");factory->Release();
        EXPCMDFLAGS flags;Verify(SUCCEEDED(root->GetFlags(&flags))&&(flags&ECF_HASSUBCOMMANDS),"subcommands declared");
        IEnumExplorerCommand* enumerator=nullptr;Verify(SUCCEEDED(root->EnumSubCommands(&enumerator)),"enumeration");
        IExplorerCommand* commands[8]{};ULONG fetched=0;Verify(enumerator->Next(8,commands,&fetched)==S_OK&&fetched==8,"eight commands");enumerator->Release();
        auto zip=Selection({argv[2]}),text=Selection({argv[3]}),folder=Selection({argv[4]}),multiple=Selection({argv[2],argv[2]});
        for(int i=0;i<8;i++){
            PWSTR title=nullptr,icon=nullptr;Verify(SUCCEEDED(commands[i]->GetTitle(zip,&title))&&title&&*title,"command title");CoTaskMemFree(title);
            Verify(SUCCEEDED(commands[i]->GetIcon(zip,&icon))&&icon&&std::wstring(icon).find(L"Hzip.ico")!=std::wstring::npos,"command icon");CoTaskMemFree(icon);
            EXPCMDSTATE state;Verify(SUCCEEDED(commands[i]->GetState(zip,FALSE,&state))&&state==(i>=3?ECS_ENABLED:ECS_HIDDEN),"archive-only action set");
            Verify(SUCCEEDED(commands[i]->GetState(text,FALSE,&state))&&state==(i<=2||i==7?ECS_ENABLED:ECS_HIDDEN),"ordinary-file action filtering");
            Verify(SUCCEEDED(commands[i]->GetState(folder,FALSE,&state))&&state==(i<=2||i==7?ECS_ENABLED:ECS_HIDDEN),"folder action filtering");
            Verify(SUCCEEDED(commands[i]->GetState(multiple,FALSE,&state))&&state==(i>=3&&i<=6?ECS_ENABLED:ECS_HIDDEN),"multiple-selection action filtering");
        }
        for(int i=0;i<2;i++) {
            PWSTR title=nullptr;Verify(SUCCEEDED(commands[i]->GetTitle(folder,&title)),"folder title");
            std::wstring expected=L"压缩为“"+std::wstring(PathFindFileNameW(argv[4]))+(i==0?L".zip”":L".7z”");
            Verify(title&&std::wstring(title)==expected,"filename-aware folder title and format order");CoTaskMemFree(title);
        }
        PWSTR createTitle=nullptr;Verify(SUCCEEDED(commands[2]->GetTitle(folder,&createTitle))&&std::wstring(createTitle)==L"新建压缩包 (Hzip)…","create title/order");CoTaskMemFree(createTitle);
        PWSTR openTitle=nullptr;Verify(SUCCEEDED(commands[7]->GetTitle(folder,&openTitle))&&std::wstring(openTitle)==L"用 Hzip 打开…","open title/order");CoTaskMemFree(openTitle);
        PWSTR directTitle=nullptr;Verify(SUCCEEDED(commands[3]->GetTitle(zip,&directTitle))&&std::wstring(directTitle)==L"解压到此处","direct extraction order");CoTaskMemFree(directTitle);
        PWSTR smartTitle=nullptr;Verify(SUCCEEDED(commands[4]->GetTitle(zip,&smartTitle))&&std::wstring(smartTitle)==L"智能解压到此处","smart extraction order");CoTaskMemFree(smartTitle);
        PWSTR folderTitle=nullptr;Verify(SUCCEEDED(commands[5]->GetTitle(zip,&folderTitle))&&std::wstring(folderTitle)==L"解压到 "+std::wstring(PathFindFileNameW(argv[2])).substr(0,std::wstring(PathFindFileNameW(argv[2])).find_last_of(L'.'))+L"\\","dynamic extract folder");CoTaskMemFree(folderTitle);
        Verify(commands[0]->Invoke(zip,nullptr)==E_INVALIDARG,"hidden compression cannot invoke on archives");
        bool registered=argc>5&&std::wstring(argv[5]).rfind(L"--registered",0)==0;
        if(registered){
            IExplorerCommand* packaged=nullptr;HRESULT hr=CoCreateInstance(clsid,nullptr,CLSCTX_LOCAL_SERVER,IID_IExplorerCommand,reinterpret_cast<void**>(&packaged));
            if(FAILED(hr)){std::cerr<<"packaged COM activation HRESULT "<<std::hex<<static_cast<unsigned long>(hr)<<"\n";}
            Verify(SUCCEEDED(hr),"packaged COM surrogate activation");PWSTR title=nullptr;Verify(SUCCEEDED(packaged->GetTitle(zip,&title))&&title&&std::wstring(title)==L"Hzip","packaged COM marshaling");CoTaskMemFree(title);
            IEnumExplorerCommand* installedItems=nullptr;Verify(SUCCEEDED(packaged->EnumSubCommands(&installedItems)),"registered enumeration");
            IExplorerCommand* installed[8]{};ULONG count=0;Verify(installedItems->Next(8,installed,&count)==S_OK&&count==8,"registered eight commands");
            PWSTR label=nullptr;Verify(SUCCEEDED(installed[0]->GetTitle(folder,&label))&&label&&std::wstring(label)==L"压缩为“"+std::wstring(PathFindFileNameW(argv[4]))+L".zip”","registered dynamic label");CoTaskMemFree(label);
            for(int i=0;i<8;i++){EXPCMDSTATE state;Verify(SUCCEEDED(installed[i]->GetState(zip,FALSE,&state))&&state==(i>=3?ECS_ENABLED:ECS_HIDDEN),"registered archive-only menu");Verify(SUCCEEDED(installed[i]->GetState(folder,FALSE,&state))&&state==(i<=2||i==7?ECS_ENABLED:ECS_HIDDEN),"registered folder-only menu");}
            if(std::wstring(argv[5])!=L"--registered") {
                std::wstring flag=argv[5];int action=flag==L"--registered-invoke-zip"?0:flag==L"--registered-invoke-7z"?1:flag==L"--registered-invoke-create"?2:flag==L"--registered-invoke-direct"?3:flag==L"--registered-invoke-extract"?4:flag==L"--registered-invoke-subfolder"?5:flag==L"--registered-invoke-other"?6:7;
                auto selected=action<=1?folder:action==2?text:zip;
                if(flag==L"--registered-invoke-folder-open")selected=folder;
                Verify(SUCCEEDED(installed[action]->Invoke(selected,nullptr)),"invoke actual registered COM server command");
            }
            for(auto command:installed)command->Release();installedItems->Release();packaged->Release();
        }
        if(argc>5&&!registered){
            std::wstring flag=argv[5];int action=flag==L"--invoke-zip"||flag==L"--invoke-multi-zip"?0:flag==L"--invoke-7z"?1:flag==L"--invoke-create"?2:flag==L"--invoke-direct"?3:flag==L"--invoke-extract"?4:flag==L"--invoke-subfolder"?5:flag==L"--invoke-other"?6:7;
            IShellItemArray* selected=action<=1?folder:action==2?text:zip;
            IShellItemArray* mixed=nullptr;if(flag==L"--invoke-multi-zip"){mixed=Selection({argv[2],argv[3]});selected=mixed;}
            if(flag==L"--invoke-folder-open")selected=folder;
            Verify(SUCCEEDED(commands[action]->Invoke(selected,nullptr)),"real command invocation");if(mixed)mixed->Release();
        }
        zip->Release();text->Release();folder->Release();multiple->Release();for(auto command:commands)command->Release();root->Release();
        Verify(canUnload()==S_OK,"all COM objects released");FreeLibrary(dll);CoUninitialize();
        std::cout<<"{\"passed\":true,\"commands\":8,\"archive_visible\":5,\"regular_visible\":4,\"selection_cases\":4,\"dynamic_titles\":true,\"registered_activation\":"<<(registered?"true":"false")<<",\"invoked\":"<<(argc>5&&std::wstring(argv[5])!=L"--registered"?"true":"false")<<"}\n";return 0;
    }catch(const std::exception& error){std::cerr<<error.what()<<"\n";CoUninitialize();return 1;}
}
