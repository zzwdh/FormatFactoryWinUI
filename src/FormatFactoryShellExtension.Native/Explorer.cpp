#include "Native.h"
#include "ExplorerLocalization.h"
#include "Formats.generated.h"
#include <shellapi.h>
#include <cwctype>
#include <mutex>

namespace ff {
std::wstring Extension(std::wstring_view path) {
    auto dot=path.rfind(L'.'),slash=path.find_last_of(L"/\\"); if(dot==std::wstring_view::npos||(slash!=std::wstring_view::npos&&dot<slash))return {};
    std::wstring extension(path.substr(dot));std::transform(extension.begin(),extension.end(),extension.begin(),[](wchar_t c){return wchar_t(towlower(c));});return extension;
}
std::wstring_view Kind(std::wstring_view extension) {
    for(const auto& format:Formats)if(extension==format.extension)return format.kind;
    return {};
}
static bool CanConvertPath(std::wstring_view path,std::wstring_view target) {
    auto extension=Extension(path);
    auto kind=Kind(extension);
    if(kind.empty())return false;
    if(target.empty())return true;
    if(target==L"pdf"||target==L"txt"||target==L"md"||target==L"epub"||target==L"mobi"||target==L"azw3")return kind==L"Document"||kind==L"Image";
    if(target==L"mp3")return kind==L"Video"||kind==L"Audio";
    if(target==L"iso")return kind==L"Disc"&&extension==L".cso";
    if(target==L"cso")return kind==L"Disc"&&extension==L".iso";
    return false;
}
bool CanConvert(const std::vector<std::wstring>& paths,std::wstring_view target) {
    if(target.empty())return true;
    return !paths.empty()&&std::all_of(paths.begin(),paths.end(),[&](const auto& path){return CanConvertPath(path,target);});
}
struct InsensitiveLess {bool operator()(const std::wstring& a,const std::wstring& b) const {return CompareStringOrdinal(a.data(),static_cast<int>(a.size()),b.data(),static_cast<int>(b.size()),TRUE)==CSTR_LESS_THAN;}};
using ShellPath=std::unique_ptr<wchar_t,decltype(&CoTaskMemFree)>;
static ShellPath FilePath(IShellItemArray* items,DWORD index) {
    ComPtr<IShellItem> item;
    PWSTR value=nullptr;
    auto hr=items->GetItemAt(index,&item);
    if(SUCCEEDED(hr)&&item)hr=item->GetDisplayName(SIGDN_FILESYSPATH,&value);
    ShellPath owned(value,&CoTaskMemFree);
    if(FAILED(hr))owned.reset();
    return owned;
}
// One classification serves the root and all subcommands. Fast UI queries never
// enumerate a provider: Explorer will retry E_PENDING with fOkToBeSlow=TRUE.
enum SelectionMask : unsigned { Supported=1, Document=2, Media=4, Iso=8, Cso=16 };
static unsigned ClassifySelection(IShellItemArray* items) {
    DWORD count=0;
    if(!items||FAILED(items->GetCount(&count))||!count)return 0;
    unsigned mask=Supported|Document|Media|Iso|Cso;
    for(DWORD i=0;i<count;++i) {
        auto path=FilePath(items,i);
        if(!path)return 0;
        auto extension=Extension(path.get());auto kind=Kind(extension);
        if(kind.empty())return 0;
        unsigned current=Supported;
        if(kind==L"Document"||kind==L"Image")current|=Document;
        if(kind==L"Video"||kind==L"Audio")current|=Media;
        if(extension==L".cso")current|=Iso;
        if(extension==L".iso")current|=Cso;
        mask&=current;
    }
    return mask;
}
class SelectionCache final {
    std::mutex mutex_;
    // Retain the array so an allocator cannot reuse its address for a different
    // selection while the cache is live. Different COM proxies safely miss.
    ComPtr<IShellItemArray> source_;
    unsigned mask_=0;
    std::chrono::steady_clock::time_point expires_{};
public:
    bool Read(IShellItemArray* items,unsigned& mask) {
        std::unique_lock lock(mutex_,std::try_to_lock);
        if(!lock||source_.Get()!=items||std::chrono::steady_clock::now()>=expires_)return false;
        mask=mask_;return true;
    }
    void Store(IShellItemArray* items,unsigned mask) {
        std::lock_guard lock(mutex_);source_=items;mask_=mask;
        expires_=std::chrono::steady_clock::now()+std::chrono::seconds(2);
    }
};
static unsigned MaskFor(std::wstring_view target) {
    if(target.empty())return Supported;
    if(target==L"mp3")return Media;
    if(target==L"iso")return Iso;
    if(target==L"cso")return Cso;
    return Document;
}
static std::vector<std::wstring> Paths(IShellItemArray* items,std::wstring_view target) {
    std::vector<std::wstring> paths;
    std::set<std::wstring,InsensitiveLess> seen;
    if(!items)return paths;
    DWORD count=0;Check(items->GetCount(&count));
    for(DWORD i=0;i<count;++i) {
        auto path=FilePath(items,i);
        if(!path||!CanConvertPath(path.get(),target)) {
            if(!target.empty())return {};
            continue;
        }
        if(seen.emplace(path.get()).second)paths.emplace_back(path.get());
    }
    return paths;
}
std::wstring SerializeBatch(const std::vector<std::wstring>& paths) {
    std::wstring result=L"[";bool first=true;constexpr wchar_t hex[]=L"0123456789abcdef";
    for(const auto& path:paths){if(!first)result+=L',';first=false;result+=L'"';for(wchar_t c:path){if(c==L'"'||c==L'\\'){result+=L'\\';result+=c;}else if(c<32){result+=L"\\u00";result+=hex[(c>>4)&15];result+=hex[c&15];}else result+=c;}result+=L'"';}return result+L"]";
}
HRESULT WriteBatchAndLaunch(const std::vector<std::wstring>& paths,std::wstring_view target) noexcept {
    return Guard([&]{
        wchar_t temp[32768];auto length=GetTempPathW(static_cast<DWORD>(std::size(temp)),temp);Require(length&&length<std::size(temp));
        auto directory=std::filesystem::path(temp)/L"FormatFactoryWinUI"/L"ShellBatches";std::filesystem::create_directories(directory);
        std::error_code error;const auto expiration=std::filesystem::file_time_type::clock::now()-std::chrono::hours(24);
        for(std::filesystem::directory_iterator it(directory,error),end;!error&&it!=end;it.increment(error)){if(it->path().extension()==L".json"&&it->last_write_time(error)<expiration)std::filesystem::remove(it->path(),error);}
        GUID id;Check(CoCreateGuid(&id));wchar_t guid[40];Require(StringFromGUID2(id,guid,40)>0);std::wstring batch;
        for(wchar_t c:std::wstring_view(guid))if(c!=L'{'&&c!=L'}'&&c!=L'-')batch+=wchar_t(towlower(c));
        auto file=directory/(batch+L".json"),staging=directory/(batch+L".tmp");auto bytes=Utf8(SerializeBatch(paths));
        HANDLE raw=CreateFileW(staging.c_str(),GENERIC_WRITE,0,nullptr,CREATE_NEW,FILE_ATTRIBUTE_TEMPORARY,nullptr);if(raw==INVALID_HANDLE_VALUE)throw Error{HRESULT_FROM_WIN32(GetLastError())};
        HRESULT writeResult=S_OK;DWORD written=0;if(!WriteFile(raw,bytes.data(),static_cast<DWORD>(bytes.size()),&written,nullptr)||written!=bytes.size())writeResult=HRESULT_FROM_WIN32(GetLastError()?GetLastError():ERROR_WRITE_FAULT);CloseHandle(raw);
        if(FAILED(writeResult)){DeleteFileW(staging.c_str());Check(writeResult);}if(!MoveFileExW(staging.c_str(),file.c_str(),MOVEFILE_WRITE_THROUGH)){auto hr=HRESULT_FROM_WIN32(GetLastError());DeleteFileW(staging.c_str());Check(hr);}
        auto uri=L"formatfactory://open/?batch="+batch;if(!target.empty())uri+=L"&target="+std::wstring(target);
        SHELLEXECUTEINFOW execute{sizeof(execute)};execute.fMask=SEE_MASK_FLAG_NO_UI;execute.lpVerb=L"open";execute.lpFile=uri.c_str();execute.nShow=SW_SHOWNORMAL;
        if(!ShellExecuteExW(&execute)){auto hr=HRESULT_FROM_WIN32(GetLastError());DeleteFileW(file.c_str());Check(hr);}
    });
}
static std::wstring_view TakeSubtag(std::wstring_view& tag) {
    auto delimiter = tag.find(L'-');
    auto subtag = tag.substr(0, delimiter);
    tag = delimiter == tag.npos ? std::wstring_view{} : tag.substr(delimiter + 1);
    return subtag;
}

static std::wstring_view CatalogCode(std::wstring_view tag) {
    auto language = TakeSubtag(tag);
    auto second = TakeSubtag(tag);
    auto region = second.size() == 4 ? TakeSubtag(tag) : second;

    if (language == L"zh") {
        if (second == L"yue") return L"yue-Hant-HK";
        if (second == L"hans" || second == L"chs") return L"zh-Hans";
        if (second == L"hant" || second == L"cht" || region == L"tw" || region == L"hk" || region == L"mo") return L"zh-Hant";
        return L"zh-Hans";
    }
    if (language == L"pnb") return L"pa-Arab-PK";
    if (language == L"pa") return second == L"arab" || (second != L"guru" && region == L"pk") ? L"pa-Arab-PK" : L"pa-IN";
    if (language == L"sr") return second == L"cyrl" ? L"sr-Cyrl-RS" : L"sr-Latn-RS";
    if (language == L"no") return L"nb-NO";
    if (language == L"pt") return region == L"br" ? L"pt-BR" : L"pt-PT";
    if (language == L"es") {
        constexpr std::wstring_view americas[]{
            L"019", L"419", L"ar", L"bo", L"br", L"bz", L"ca", L"cl", L"co", L"cr", L"cu", L"do", L"ec",
            L"gt", L"hn", L"mx", L"ni", L"pa", L"pe", L"pr", L"py", L"sv", L"us", L"uy", L"ve"
        };
        return std::find(std::begin(americas), std::end(americas), region) != std::end(americas) ? L"es-419" : L"es-ES";
    }
    return language;
}

const ExplorerLanguage& ResolveExplorerLanguage(std::wstring_view preferredLanguages) {
    while (!preferredLanguages.empty() && preferredLanguages.front() != L'\0') {
        auto end = preferredLanguages.find(L'\0');
        std::wstring tag(preferredLanguages.substr(0, end));
        for (auto& c : tag) {
            if (c >= L'A' && c <= L'Z') c = static_cast<wchar_t>(c + (L'a' - L'A'));
            else if (c == L'_') c = L'-';
        }
        auto code = CatalogCode(tag);
        for (const auto& entry : ExplorerLanguages) {
            auto entryTag = entry.code;
            if (entry.code == code || TakeSubtag(entryTag) == code) return entry;
        }
        if (end == preferredLanguages.npos) break;
        preferredLanguages.remove_prefix(end + 1);
    }
    return ExplorerLanguages[0];
}

static const ExplorerLanguage& ShellLanguage() {
    constexpr DWORD flags = MUI_LANGUAGE_NAME | MUI_MERGE_USER_FALLBACK;
    wchar_t common[256];
    ULONG count = 0, length = static_cast<ULONG>(std::size(common));
    if (GetThreadPreferredUILanguages(flags, &count, common, &length))
        return ResolveExplorerLanguage({common, length});
    if (GetLastError() == ERROR_INSUFFICIENT_BUFFER && length > std::size(common)) {
        std::vector<wchar_t> languages(length);
        if (GetThreadPreferredUILanguages(flags, &count, languages.data(), &length))
            return ResolveExplorerLanguage({languages.data(), length});
    }
    return ExplorerLanguages[0];
}

struct Definition { const wchar_t* format;const wchar_t* target;GUID id; };
static const Definition Targets[]{
    {L"PDF",L"pdf",{0x8c36b73d,0xd641,0x4dbe,{0x87,0x38,0xf7,0x0c,0x85,0x00,0x20,0x01}}},
    {L"TXT",L"txt",{0x8c36b73d,0xd641,0x4dbe,{0x87,0x38,0xf7,0x0c,0x85,0x00,0x20,0x07}}},
    {L"Markdown",L"md",{0x8c36b73d,0xd641,0x4dbe,{0x87,0x38,0xf7,0x0c,0x85,0x00,0x20,0x02}}},
    {L"EPUB",L"epub",{0x8c36b73d,0xd641,0x4dbe,{0x87,0x38,0xf7,0x0c,0x85,0x00,0x20,0x03}}},
    {L"MOBI",L"mobi",{0x8c36b73d,0xd641,0x4dbe,{0x87,0x38,0xf7,0x0c,0x85,0x00,0x20,0x08}}},
    {L"AZW3",L"azw3",{0x8c36b73d,0xd641,0x4dbe,{0x87,0x38,0xf7,0x0c,0x85,0x00,0x20,0x09}}},
    {L"MP3",L"mp3",{0x8c36b73d,0xd641,0x4dbe,{0x87,0x38,0xf7,0x0c,0x85,0x00,0x20,0x04}}},
    {L"ISO",L"iso",{0x8c36b73d,0xd641,0x4dbe,{0x87,0x38,0xf7,0x0c,0x85,0x00,0x20,0x05}}},
    {L"CSO",L"cso",{0x8c36b73d,0xd641,0x4dbe,{0x87,0x38,0xf7,0x0c,0x85,0x00,0x20,0x06}}}
};
using namespace Microsoft::WRL;
class Enumerator;
class Command final : public RuntimeClass<RuntimeClassFlags<ClassicCom>,IExplorerCommand>,private Lifetime {
    int index_=-1;
    std::shared_ptr<SelectionCache> cache_;
    HRESULT LocalizedText(bool tooltip, LPWSTR* value) const {
        if (!value) return E_POINTER;
        *value = nullptr;
        return Guard([&] {
            const auto& language = ShellLanguage();
            if (index_ < 0) {
                Check(SHStrDupW((tooltip ? language.openSelected : language.rootTitle).data(), value));
            } else {
                std::wstring title(language.targetTitle);
                title.replace(title.find(L"{}"), 2, Targets[index_].format);
                Check(SHStrDupW(title.c_str(), value));
            }
        });
    }
public:
    explicit Command(int index=-1,std::shared_ptr<SelectionCache> cache=std::make_shared<SelectionCache>()):index_(index),cache_(std::move(cache)){}
    HRESULT STDMETHODCALLTYPE GetTitle(IShellItemArray*,LPWSTR* value) override {return LocalizedText(false, value);}
    HRESULT STDMETHODCALLTYPE GetIcon(IShellItemArray*,LPWSTR* value) override {if(!value)return E_POINTER;*value=nullptr;return E_NOTIMPL;}
    HRESULT STDMETHODCALLTYPE GetToolTip(IShellItemArray*,LPWSTR* value) override {return LocalizedText(true, value);}
    HRESULT STDMETHODCALLTYPE GetCanonicalName(GUID* value) override {if(!value)return E_POINTER;*value=index_<0?CommandId:Targets[index_].id;return S_OK;}
    HRESULT STDMETHODCALLTYPE GetState(IShellItemArray* items,BOOL okToBeSlow,EXPCMDSTATE* state) override {
        if(!state)return E_POINTER;*state=index_<0?ECS_DISABLED:ECS_HIDDEN;
        if(!items)return S_OK;
        return Guard([&]{
            unsigned mask=0;
            if(!cache_->Read(items,mask)) {
                if(!okToBeSlow)throw Error{E_PENDING};
                mask=ClassifySelection(items);cache_->Store(items,mask);
            }
            if(mask&MaskFor(index_<0?L"":Targets[index_].target))*state=ECS_ENABLED;
        });
    }
    HRESULT STDMETHODCALLTYPE Invoke(IShellItemArray* items,IBindCtx*) override {return Guard([&]{auto target=index_<0?L"":Targets[index_].target;auto paths=Paths(items,target);Require(!paths.empty());Check(WriteBatchAndLaunch(paths,target));});}
    HRESULT STDMETHODCALLTYPE GetFlags(EXPCMDFLAGS* flags) override {if(!flags)return E_POINTER;*flags=index_<0?ECF_HASSUBCOMMANDS:ECF_DEFAULT;return S_OK;}
    HRESULT STDMETHODCALLTYPE EnumSubCommands(IEnumExplorerCommand** result) override;
};
class Enumerator final:public RuntimeClass<RuntimeClassFlags<ClassicCom>,IEnumExplorerCommand>,private Lifetime {
    ULONG index_=0;
    std::shared_ptr<SelectionCache> cache_;
public:
    explicit Enumerator(std::shared_ptr<SelectionCache> cache,ULONG index=0):index_(index),cache_(std::move(cache)){}
    HRESULT STDMETHODCALLTYPE Next(ULONG count,IExplorerCommand** commands,ULONG* fetched) override {
        if(!commands||(!fetched&&count!=1))return E_POINTER;ULONG total=0;if(fetched)*fetched=0;
        auto hr=Guard([&]{for(;total<count&&index_<std::size(Targets);++total,++index_){auto command=Make<Command>(static_cast<int>(index_),cache_);if(!command)throw Error{E_OUTOFMEMORY};Check(command.CopyTo(commands+total));}});
        if(fetched)*fetched=total;return FAILED(hr)?hr:(total==count?S_OK:S_FALSE);
    }
    HRESULT STDMETHODCALLTYPE Skip(ULONG count) override {auto remaining=static_cast<ULONG>(std::size(Targets))-index_;index_+=std::min(count,remaining);return count<=remaining?S_OK:S_FALSE;}
    HRESULT STDMETHODCALLTYPE Reset() override {index_=0;return S_OK;}
    HRESULT STDMETHODCALLTYPE Clone(IEnumExplorerCommand** result) override {if(!result)return E_POINTER;*result=nullptr;auto enumerator=Make<Enumerator>(cache_,index_);return enumerator?enumerator.CopyTo(result):E_OUTOFMEMORY;}
};
HRESULT Command::EnumSubCommands(IEnumExplorerCommand** result) {if(!result)return E_POINTER;*result=nullptr;if(index_>=0)return E_NOTIMPL;auto enumerator=Make<Enumerator>(cache_);return enumerator?enumerator.CopyTo(result):E_OUTOFMEMORY;}
HRESULT CreateCommand(REFIID iid,void** result) {auto command=Make<Command>();return command?command->QueryInterface(iid,result):E_OUTOFMEMORY;}
}
