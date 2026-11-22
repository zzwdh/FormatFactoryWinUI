#include "Native.h"
#include "ExplorerLocalization.h"
#include "miniz.h"
#include <iostream>
#include <array>

using namespace ff;
static unsigned passed=0;
static void Assert(bool condition,const char* message){if(!condition)throw std::runtime_error(message);}
template<class F> static void Test(const char* name,F action){action();++passed;std::cout<<"PASS "<<name<<'\n';}
template<class F> static void Reject(F action){try{action();}catch(const Error&){return;}throw std::runtime_error("Expected rejection");}
static std::vector<uint8_t> Png(){
    ComPtr<IWICImagingFactory> factory;Check(CoCreateInstance(CLSID_WICImagingFactory,nullptr,CLSCTX_INPROC_SERVER,IID_PPV_ARGS(&factory)));
    ComPtr<IStream> stream;Check(CreateStreamOnHGlobal(nullptr,TRUE,&stream));ComPtr<IWICBitmapEncoder> encoder;Check(factory->CreateEncoder(GUID_ContainerFormatPng,nullptr,&encoder));Check(encoder->Initialize(stream.Get(),WICBitmapEncoderNoCache));
    ComPtr<IWICBitmapFrameEncode> frame;Check(encoder->CreateNewFrame(&frame,nullptr));Check(frame->Initialize(nullptr));Check(frame->SetSize(2,2));auto format=GUID_WICPixelFormat24bppBGR;Check(frame->SetPixelFormat(&format));
    std::array<BYTE,12> pixels{30,60,90,30,60,90,30,60,90,30,60,90};Check(frame->WritePixels(2,6,static_cast<UINT>(pixels.size()),pixels.data()));Check(frame->Commit());Check(encoder->Commit());return ReadStream(stream.Get());
}
struct BookOptions {bool cover=true,epub2=false,duplicate=false;std::string href="cover.png",chapter="<!DOCTYPE html><html><body><h1>First chapter</h1><p>Readable text.</p><script>script text</script></body></html>";unsigned extras=0;};
static std::vector<uint8_t> Book(const BookOptions& options={}){
    mz_zip_archive archive{};Require(mz_zip_writer_init_heap(&archive,0,0)!=0);
    try{
        auto entry=[&](const char* path,const void* bytes,size_t count){Require(mz_zip_writer_add_mem(&archive,path,bytes,count,static_cast<mz_uint>(MZ_DEFAULT_COMPRESSION))!=0);};
        auto text=[&](const char* path,const std::string& value){entry(path,value.data(),value.size());};
        text("mimetype","application/epub+zip");text("META-INF/container.xml","<container xmlns='urn:oasis:names:tc:opendocument:xmlns:container'><rootfiles><rootfile full-path='OPS/book.opf'/></rootfiles></container>");
        std::string package="<package xmlns='http://www.idpf.org/2007/opf'><metadata xmlns:dc='http://purl.org/dc/elements/1.1/'><dc:title>A real book</dc:title><dc:creator>Test author</dc:creator>";
        if(options.epub2)package+="<meta name='cover' content='cover'/>";
        package+="</metadata><manifest>";if(options.cover)package+="<item id='cover' href='"+options.href+"' media-type='image/png' properties='"+(options.epub2?"":"cover-image")+"'/>";
        package+="<item id='chapter' href='chapter.xhtml' media-type='application/xhtml+xml'/></manifest><spine><itemref idref='chapter'/></spine></package>";text("OPS/book.opf",package);auto png=Png();entry("OPS/cover.png",png.data(),png.size());text("OPS/chapter.xhtml",options.chapter);
        if(options.duplicate)text("OPS/chapter.xhtml","duplicate");for(unsigned i=0;i<options.extras;++i)text(("extras/"+std::to_string(i)+".txt").c_str(),"extra");
        void* buffer=nullptr;size_t length=0;Require(mz_zip_writer_finalize_heap_archive(&archive,&buffer,&length)!=0);std::vector<uint8_t> bytes(static_cast<uint8_t*>(buffer),static_cast<uint8_t*>(buffer)+length);mz_free(buffer);mz_zip_writer_end(&archive);return bytes;
    }catch(...){mz_zip_writer_end(&archive);throw;}
}
struct NativeModule {
    HMODULE module;using FactoryFn=HRESULT(__stdcall*)(REFCLSID,REFIID,void**);using UnloadFn=HRESULT(__stdcall*)();FactoryFn factory;UnloadFn unload;
    explicit NativeModule(const wchar_t* path){module=LoadLibraryExW(path,nullptr,LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR|LOAD_LIBRARY_SEARCH_SYSTEM32);if(!module)throw Error{HRESULT_FROM_WIN32(GetLastError())};factory=reinterpret_cast<FactoryFn>(GetProcAddress(module,"DllGetClassObject"));unload=reinterpret_cast<UnloadFn>(GetProcAddress(module,"DllCanUnloadNow"));Assert(factory&&unload,"COM exports missing");}
    ~NativeModule(){if(module)FreeLibrary(module);}
    template<class T> ComPtr<T> Create(REFCLSID clsid){ComPtr<IClassFactory> f;Check(factory(clsid,IID_PPV_ARGS(&f)));ComPtr<T> result;Check(f->CreateInstance(nullptr,IID_PPV_ARGS(&result)));return result;}
};
static ComPtr<IStream> Stream(const std::vector<uint8_t>& bytes){ComPtr<IStream> stream;stream.Attach(SHCreateMemStream(bytes.data(),static_cast<UINT>(bytes.size())));Require(stream!=nullptr);return stream;}

class ThreadLanguageScope {
    std::vector<wchar_t> previous_;
public:
    ThreadLanguageScope() {
        ULONG count = 0, length = 0;
        constexpr DWORD flags = MUI_LANGUAGE_NAME | MUI_THREAD_LANGUAGES;
        Assert(GetThreadPreferredUILanguages(flags, &count, nullptr, &length), "thread language size");
        previous_.resize(length);
        Assert(GetThreadPreferredUILanguages(flags, &count, previous_.data(), &length), "thread language snapshot");
    }
    ~ThreadLanguageScope() {
        SetThreadPreferredUILanguages(MUI_LANGUAGE_NAME, previous_.data(), nullptr);
    }
    void Set(PCZZWSTR preferences) const {
        ULONG count = 0;
        Assert(SetThreadPreferredUILanguages(MUI_LANGUAGE_NAME, preferences, &count) && count, "set thread language");
    }
};

static std::wstring CommandText(IExplorerCommand* command, bool tooltip = false) {
    PWSTR text = nullptr;
    Check(tooltip ? command->GetToolTip(nullptr, &text) : command->GetTitle(nullptr, &text));
    std::unique_ptr<wchar_t, decltype(&CoTaskMemFree)> owned(text, &CoTaskMemFree);
    Assert(text != nullptr, "missing menu text");
    return text;
}
class SelectionItem final:public Microsoft::WRL::RuntimeClass<Microsoft::WRL::RuntimeClassFlags<Microsoft::WRL::ClassicCom>,IShellItem> {
    std::wstring path_;
    DWORD delay_=0;
public:
    explicit SelectionItem(std::wstring path,DWORD delay=0):path_(std::move(path)),delay_(delay){}
    HRESULT STDMETHODCALLTYPE BindToHandler(IBindCtx*,REFGUID,REFIID,void**) override {return E_NOTIMPL;}
    HRESULT STDMETHODCALLTYPE GetParent(IShellItem**) override {return E_NOTIMPL;}
    HRESULT STDMETHODCALLTYPE GetDisplayName(SIGDN kind,PWSTR* value) override {
        if(!value)return E_POINTER;*value=nullptr;
        if(delay_)Sleep(delay_);
        return kind==SIGDN_FILESYSPATH&&!path_.empty()?SHStrDupW(path_.c_str(),value):E_FAIL;
    }
    HRESULT STDMETHODCALLTYPE GetAttributes(SFGAOF,SFGAOF*) override {return E_NOTIMPL;}
    HRESULT STDMETHODCALLTYPE Compare(IShellItem*,SICHINTF,int*) override {return E_NOTIMPL;}
};
class Selection final:public Microsoft::WRL::RuntimeClass<Microsoft::WRL::RuntimeClassFlags<Microsoft::WRL::ClassicCom>,IShellItemArray> {
    std::vector<std::wstring> paths_;
    DWORD delay_=0;
public:
    DWORD queried=0,countQueries=0;
    explicit Selection(std::vector<std::wstring> paths,DWORD delay=0):paths_(std::move(paths)),delay_(delay){}
    HRESULT STDMETHODCALLTYPE BindToHandler(IBindCtx*,REFGUID,REFIID,void**) override {return E_NOTIMPL;}
    HRESULT STDMETHODCALLTYPE GetPropertyStore(GETPROPERTYSTOREFLAGS,REFIID,void**) override {return E_NOTIMPL;}
    HRESULT STDMETHODCALLTYPE GetPropertyDescriptionList(REFPROPERTYKEY,REFIID,void**) override {return E_NOTIMPL;}
    HRESULT STDMETHODCALLTYPE GetAttributes(SIATTRIBFLAGS,SFGAOF,SFGAOF*) override {return E_NOTIMPL;}
    HRESULT STDMETHODCALLTYPE GetCount(DWORD* count) override {if(!count)return E_POINTER;++countQueries;*count=static_cast<DWORD>(paths_.size());return S_OK;}
    HRESULT STDMETHODCALLTYPE GetItemAt(DWORD index,IShellItem** result) override {
        if(!result)return E_POINTER;*result=nullptr;if(index>=paths_.size())return E_INVALIDARG;
        ++queried;auto item=Microsoft::WRL::Make<SelectionItem>(paths_[index],delay_);return item?item.CopyTo(result):E_OUTOFMEMORY;
    }
    HRESULT STDMETHODCALLTYPE EnumItems(IEnumShellItems**) override {return E_NOTIMPL;}
};
struct Window { HWND value=CreateWindowExW(0,L"STATIC",L"Native Shell test",0,0,0,600,600,nullptr,nullptr,nullptr,nullptr);~Window(){if(value&&IsWindow(value))DestroyWindow(value);} };
struct RejectChildWindow {
    inline static thread_local unsigned created=0, rejectAt=0;
    HHOOK hook=nullptr;
    static LRESULT CALLBACK Procedure(int code,WPARAM wParam,LPARAM lParam) {
        if(code==HCBT_CREATEWND) {
            auto creation=reinterpret_cast<CBT_CREATEWNDW*>(lParam);
            if((creation->lpcs->style&WS_CHILD)&&++created==rejectAt)return 1;
        }
        return CallNextHookEx(nullptr,code,wParam,lParam);
    }
    explicit RejectChildWindow(unsigned index) {
        created=0;rejectAt=index;hook=SetWindowsHookExW(WH_CBT,Procedure,nullptr,GetCurrentThreadId());
        Assert(hook!=nullptr,"window failure hook unavailable");
    }
    ~RejectChildWindow(){if(hook)UnhookWindowsHookEx(hook);}
};
int wmain(int argc,wchar_t** argv){
    HRESULT apartment=CoInitializeEx(nullptr,COINIT_APARTMENTTHREADED);if(FAILED(apartment))return 1;
    try{
        Assert(argc==2,"Expected native DLL path");NativeModule module(argv[1]);Assert(module.unload()==S_OK,"Fresh DLL has live objects");
        Test("EPUB 3 metadata, cover and safe text",[]{auto result=ReadEpub(Book());Assert(result.title==L"A real book"&&result.author==L"Test author","metadata mismatch");Assert(result.text.find(L"First chapter")!=std::wstring::npos&&result.text.find(L"script text")==std::wstring::npos&&!result.cover.empty(),"content mismatch");});
        Test("EPUB 2 cover metadata",[]{BookOptions o;o.epub2=true;Assert(!ReadEpub(Book(o)).cover.empty(),"EPUB2 cover missing");});
        Test("No cover keeps text",[]{BookOptions o;o.cover=false;auto result=ReadEpub(Book(o));Assert(result.cover.empty()&&!result.text.empty(),"no-cover mismatch");});
        Test("Thumbnail does not parse malformed chapters",[]{BookOptions o;o.chapter="<broken";Assert(!ReadEpub(Book(o),false).cover.empty(),"thumbnail parsed chapter");Reject([&]{ReadEpub(Book(o));});});
        Test("Archive-local paths and percent-decoded traversal",[]{Assert(ResolvePath("OPS/book.opf","images/../cover.png#fragment")=="OPS/cover.png","resolution mismatch");for(auto path:{"https://example.test/x","../../x","%2f%2fexample.test/x","https%3a//example.test/x","%00x","bad%Q0"})Reject([&]{ResolvePath("OPS/book.opf",path);});BookOptions o;o.href="../../cover.png";Reject([&]{ReadEpub(Book(o));});});
        Test("XML entities and external resources rejected",[]{BookOptions o;o.chapter="<!DOCTYPE html [<!ENTITY secret SYSTEM 'file:///C:/Windows/win.ini'>]><html><body>&secret;</body></html>";Reject([&]{ReadEpub(Book(o));});});
        Test("XML size and expansion budget",[]{BookOptions o;o.chapter=std::string(MaxXml+1,'a');Reject([&]{ReadEpub(Book(o));});});
        Test("Duplicate ZIP resources rejected",[]{BookOptions o;o.duplicate=true;Reject([&]{ReadEpub(Book(o));});});
        Test("Forged directory count and ZIP64 rejected",[]{auto bytes=Book();auto eocd=bytes.size()-22;bytes[eocd+8]=bytes[eocd+9]=bytes[eocd+10]=bytes[eocd+11]=0;Reject([&]{ReadEpub(bytes);});bytes=Book();eocd=bytes.size()-22;for(size_t i=0;i<4;++i)bytes[eocd+16+i]=255;Reject([&]{ReadEpub(bytes);});});
        Test("Entry count is bounded before materialization",[]{BookOptions o;o.extras=4096;Reject([&]{ReadEpub(Book(o));});});
        Test("Text limit preserves bounded preview",[]{BookOptions o;o.chapter="<html><body><p>"+std::string(60000,'a')+"</p></body></html>";Assert(ReadEpub(Book(o)).text.size()==MaxText,"text limit mismatch");});
        Test("Cover dimensions rejected before decoder allocation",[]{auto bytes=Png();bytes[16]=0;bytes[17]=0;bytes[18]=0xc3;bytes[19]=0x50;Reject([&]{DecodeCover(bytes);});});
        Test("All conversion target rules and JSON escaping",[]{Assert(CanConvert({L"a.DOCX",L"b.PNG"},L"pdf"),"document/image rule");Assert(!CanConvert({L"a.docx",L"b.mp3"},L"pdf"),"mixed rule");Assert(CanConvert({L"a.MP4",L"b.wav"},L"mp3"),"audio rule");Assert(CanConvert({L"a.CSO"},L"iso")&&!CanConvert({L"a.iso"},L"iso"),"ISO rule");Assert(CanConvert({L"a.iso"},L"cso")&&!CanConvert({},L"mp3"),"CSO/empty rule");for(auto target:{L"txt",L"md",L"epub",L"mobi",L"azw3"})Assert(CanConvert({L"a.pdf"},target),"document target");Assert(SerializeBatch({L"C:\\中文\\a\"b.pdf",L"x\ny"})==L"[\"C:\\\\中文\\\\a\\\"b.pdf\",\"x\\u000ay\"]","batch JSON escaping");});
        Test("COM class factory enumeration and ownership",[&]{
            auto command=module.Create<IExplorerCommand>(CommandId);Assert(module.unload()==S_FALSE,"live COM object uncounted");
            ComPtr<IEnumExplorerCommand> commands;Check(command->EnumSubCommands(&commands));ComPtr<IExplorerCommand> first;ULONG fetched=0;Check(commands->Next(1,&first,&fetched));Assert(fetched==1,"missing target");GUID id{};Check(first->GetCanonicalName(&id));Assert(id.Data1==0x8c36b73d,"canonical GUID");ComPtr<IEnumExplorerCommand> clone;Check(commands->Clone(&clone));Assert(clone->Skip(ULONG_MAX)==S_FALSE,"Skip overflow");Check(commands->Reset());for(unsigned i=0;i<9;++i){first.Reset();Assert(commands->Next(1,&first,&fetched)==S_OK&&fetched==1,"target count");}first.Reset();Assert(commands->Next(1,&first,&fetched)==S_FALSE&&fetched==0,"target end");
        });
        Test("Shell language catalogue and preferred language ordering", [] {
            std::set<std::wstring_view> codes;
            for (const auto& language : ExplorerLanguages) {
                Assert(!language.code.empty() && codes.insert(language.code).second, "duplicate or empty language code");
                Assert(ResolveExplorerLanguage(language.code).code == language.code, "canonical language mismatch");
                Assert(language.rootTitle.find(L"FluentFormatConverter") != language.rootTitle.npos, "missing menu brand");
                Assert(language.openSelected.find(L"FluentFormatConverter") != language.openSelected.npos, "missing tooltip brand");
                auto marker = language.targetTitle.find(L"{}");
                Assert(marker != language.targetTitle.npos && language.targetTitle.size() > 2 &&
                    language.targetTitle.find_first_of(L"{}") == marker &&
                    language.targetTitle.find_first_of(L"{}", marker + 2) == language.targetTitle.npos, "invalid menu template");
            }
            using namespace std::literals;
            Assert(ResolveExplorerLanguage(L"mi-NZ\0en-GB\0zh-CN"sv).code == L"en-US", "fallback priority");
            Assert(ResolveExplorerLanguage(L"mi-NZ\0eo"sv).code == L"en-US", "unknown language fallback");
            Assert(ResolveExplorerLanguage({}).code == L"en-US", "empty language fallback");
            constexpr std::pair<std::wstring_view, std::wstring_view> aliases[]{
                {L"zh-TW", L"zh-Hant"}, {L"zh-MO", L"zh-Hant"}, {L"zh-Hans-HK", L"zh-Hans"},
                {L"zh-yue-Hant-HK", L"yue-Hant-HK"}, {L"yue-HK", L"yue-Hant-HK"},
                {L"sr-Cyrl-BA", L"sr-Cyrl-RS"}, {L"sr-Latn-ME", L"sr-Latn-RS"},
                {L"pnb", L"pa-Arab-PK"}, {L"pa-PK", L"pa-Arab-PK"}, {L"PA_GURU_IN", L"pa-IN"},
                {L"es-Latn-MX", L"es-419"}, {L"es-ES", L"es-ES"}, {L"pt-AO", L"pt-PT"}, {L"pt-Latn-BR", L"pt-BR"}
            };
            for (const auto& [tag, expected] : aliases)
                Assert(ResolveExplorerLanguage(tag).code == expected, "script or region fallback");
        });
        Test("Explorer root and all subcommands follow the current thread UI language", [&] {
            ThreadLanguageScope preferences;
            auto root = module.Create<IExplorerCommand>(CommandId);
            ComPtr<IEnumExplorerCommand> commands;
            Check(root->EnumSubCommands(&commands));
            constexpr std::wstring_view formats[]{L"PDF", L"TXT", L"Markdown", L"EPUB", L"MOBI", L"AZW3", L"MP3", L"ISO", L"CSO"};
            struct MenuExample { PCZZWSTR preferences; std::wstring_view language; };
            constexpr MenuExample examples[]{
                {L"en-US\0", L"en-US"}, {L"zh-CN\0", L"zh-Hans"}, {L"zh-TW\0", L"zh-Hant"},
                {L"sr-Cyrl-RS\0", L"sr-Cyrl-RS"}, {L"pa-IN\0", L"pa-IN"}, {L"ur-PK\0", L"ur-PK"},
                {L"mi-NZ\0ja-JP\0", L"ja-JP"}
            };
            for (const auto& example : examples) {
                preferences.Set(example.preferences);
                const auto& expected = ResolveExplorerLanguage(example.language);
                Assert(CommandText(root.Get()) == expected.rootTitle, "root title does not follow Explorer language");
                Assert(CommandText(root.Get(), true) == expected.openSelected, "root tooltip does not follow Explorer language");
                Check(commands->Reset());
                for (auto format : formats) {
                    ComPtr<IExplorerCommand> child;
                    Assert(commands->Next(1, &child, nullptr) == S_OK, "missing translated subcommand");
                    auto title = std::wstring(expected.targetTitle);
                    title.replace(title.find(L"{}"), 2, format);
                    Assert(CommandText(child.Get()) == title, "incorrect localized target title");
                    Assert(CommandText(child.Get(), true) == title, "incorrect localized target tooltip");
                }
            }
        });
        Test("COM menu classification is shared and stops at unsupported files",[&]{
            auto root=module.Create<IExplorerCommand>(CommandId);
            ComPtr<IEnumExplorerCommand> commands;Check(root->EnumSubCommands(&commands));
            ComPtr<IExplorerCommand> pdf;Check(commands->Next(1,&pdf,nullptr));
            auto selection=Microsoft::WRL::Make<Selection>(std::vector<std::wstring>{L"a.MP3",L"b.PDF"});
            EXPCMDSTATE state=ECS_DISABLED;Check(root->GetState(selection.Get(),TRUE,&state));Assert(state==ECS_ENABLED,"mixed supported root disabled");
            selection->queried=0;Check(pdf->GetState(selection.Get(),FALSE,&state));Assert(state==ECS_HIDDEN&&selection->queried==0,"submenu did not reuse selection classification");
            selection=Microsoft::WRL::Make<Selection>(std::vector<std::wstring>{L"a.PNG",L"A.png",L"b.DOCX"});
            Check(pdf->GetState(selection.Get(),TRUE,&state));Assert(state==ECS_ENABLED,"document/image selection hidden");
            for(auto path:{L"a.unknown",L""}) {
                selection=Microsoft::WRL::Make<Selection>(std::vector<std::wstring>{path,L"a.pdf"});
                Check(root->GetState(selection.Get(),TRUE,&state));Assert(state==ECS_DISABLED&&selection->queried==1,"unsupported root state");
            }
            Check(root->GetState(nullptr,FALSE,&state));Assert(state==ECS_DISABLED,"empty root enabled");
            Check(pdf->GetState(nullptr,FALSE,&state));Assert(state==ECS_HIDDEN,"empty target visible");
        });
        Test("Fast menu queries never enumerate a slow Shell provider",[&]{
            auto root=module.Create<IExplorerCommand>(CommandId);
            auto selection=Microsoft::WRL::Make<Selection>(std::vector<std::wstring>{L"a.PNG",L"b.DOCX"},20);
            EXPCMDSTATE state=ECS_ENABLED;
            Assert(root->GetState(selection.Get(),FALSE,&state)==E_PENDING&&state==ECS_DISABLED,"uncached fast query was not pending");
            Assert(selection->queried==0&&selection->countQueries==0,"fast query contacted a slow provider");
            Check(root->GetState(selection.Get(),TRUE,&state));Assert(state==ECS_ENABLED&&selection->queried==2,"slow query did not classify selection");
            Check(root->GetState(selection.Get(),FALSE,&state));Assert(selection->queried==2,"cached fast query re-enumerated");
            auto changed=Microsoft::WRL::Make<Selection>(std::vector<std::wstring>{L"a.MP3"},20);
            Assert(root->GetState(changed.Get(),FALSE,&state)==E_PENDING&&changed->queried==0,"different selection reused stale state");
        });
        Test("Ten thousand selected files are scanned once for all subcommands",[&]{
            auto root=module.Create<IExplorerCommand>(CommandId);
            auto selection=Microsoft::WRL::Make<Selection>(std::vector<std::wstring>(10000,L"batch.DOCX"));
            EXPCMDSTATE state=ECS_DISABLED;
            Assert(root->GetState(selection.Get(),FALSE,&state)==E_PENDING&&selection->queried==0,"large fast query scanned");
            Check(root->GetState(selection.Get(),TRUE,&state));Assert(state==ECS_ENABLED&&selection->queried==10000,"large classification incomplete");
            ComPtr<IEnumExplorerCommand> commands;Check(root->EnumSubCommands(&commands));ComPtr<IEnumExplorerCommand> clone;Check(commands->Clone(&clone));
            for(unsigned i=0;i<9;++i){ComPtr<IExplorerCommand> command;Check(clone->Next(1,&command,nullptr));Check(command->GetState(selection.Get(),FALSE,&state));Assert(state==static_cast<EXPCMDSTATE>(i<6?ECS_ENABLED:ECS_HIDDEN),"cached target state mismatch");}
            Assert(selection->queried==10000&&selection->countQueries==1,"subcommands rescanned the large batch");
        });
        Test("Thumbnail reuses its decoded cover for different sizes",[&]{
            auto stream=Stream(Book());auto initializer=module.Create<IInitializeWithStream>(ThumbnailId);Check(initializer->Initialize(stream.Get(),STGM_READ));
            ComPtr<IThumbnailProvider> provider;Check(initializer.As(&provider));
            HBITMAP raw=nullptr;WTS_ALPHATYPE alpha=WTSAT_UNKNOWN;Check(provider->GetThumbnail(128,&raw,&alpha));Bitmap first(raw);
            LARGE_INTEGER zero{};Check(stream->Seek(zero,STREAM_SEEK_SET,nullptr));
            raw=nullptr;Check(provider->GetThumbnail(64,&raw,&alpha));Bitmap second(raw);
            ULARGE_INTEGER position{};Check(stream->Seek(zero,STREAM_SEEK_CUR,&position));
            Assert(position.QuadPart==0,"cached thumbnail reread the EPUB stream");
            Assert(first.value&&second.value&&first.value!=second.value,"thumbnail did not transfer independent bitmaps");
        });
        Test("Thumbnail COM stream rewinds and returns real opaque bitmap",[&]{auto bytes=Book();auto stream=Stream(bytes);LARGE_INTEGER position{};position.QuadPart=9;Check(stream->Seek(position,STREAM_SEEK_SET,nullptr));auto initializer=module.Create<IInitializeWithStream>(ThumbnailId);Assert(initializer->Initialize(stream.Get(),STGM_WRITE)==E_INVALIDARG,"write mode accepted");Check(initializer->Initialize(stream.Get(),STGM_READ));Assert(initializer->Initialize(stream.Get(),STGM_READ)==E_UNEXPECTED,"second initialization accepted");ComPtr<IThumbnailProvider> provider;Check(initializer.As(&provider));for(unsigned i=0;i<2;++i){HBITMAP raw=nullptr;WTS_ALPHATYPE alpha=WTSAT_UNKNOWN;Check(provider->GetThumbnail(128,&raw,&alpha));Bitmap image(raw);BITMAP info{};Assert(GetObjectW(raw,sizeof(info),&info)>0&&info.bmBitsPixel==32&&info.bmWidth<=128&&alpha==WTSAT_RGB,"invalid HBITMAP");}HBITMAP invalid=nullptr;WTS_ALPHATYPE alpha;Assert(FAILED(provider->GetThumbnail(0,&invalid,&alpha))&&!invalid,"invalid size accepted");provider.Reset();initializer.Reset();position.QuadPart=0;Check(stream->Seek(position,STREAM_SEEK_SET,nullptr));char signature[2];ULONG read=0;Check(stream->Read(signature,2,&read));Assert(read==2&&signature[0]=='P'&&signature[1]=='K',"caller stream released");});
        Test("No-cover thumbnail falls back to Explorer",[&]{BookOptions o;o.cover=false;auto stream=Stream(Book(o));auto initializer=module.Create<IInitializeWithStream>(ThumbnailId);Check(initializer->Initialize(stream.Get(),0));ComPtr<IThumbnailProvider> provider;Check(initializer.As(&provider));HBITMAP bitmap=nullptr;WTS_ALPHATYPE alpha;Assert(FAILED(provider->GetThumbnail(64,&bitmap,&alpha))&&!bitmap,"fake cover returned");});
        Test("Preview HWND resize reparent unload and parent destruction",[&]{auto before=GetGuiResources(GetCurrentProcess(),GR_GDIOBJECTS);for(unsigned i=0;i<30;++i){auto stream=Stream(Book());auto initializer=module.Create<IInitializeWithStream>(PreviewId);ComPtr<IPreviewHandler> handler;Check(initializer.As(&handler));Assert(FAILED(handler->DoPreview()),"uninitialized preview accepted");Check(initializer->Initialize(stream.Get(),0));Window parent,other;Assert(parent.value&&other.value,"test HWND missing");RECT rect{0,0,400,500};Check(handler->SetWindow(parent.value,&rect));Check(handler->DoPreview());ComPtr<IOleWindow> window;Check(handler.As(&window));HWND child=nullptr;Check(window->GetWindow(&child));Assert(IsWindow(child)&&GetParent(child)==parent.value,"child parent");rect.right=250;Check(handler->SetRect(&rect));Check(handler->SetWindow(other.value,&rect));Assert(GetParent(child)==other.value,"reparent");RECT bad{LONG_MIN,0,LONG_MAX,400};Assert(FAILED(handler->SetRect(&bad)),"overflow rect accepted");if(i%2==0){DestroyWindow(other.value);other.value=nullptr;Assert(FAILED(window->GetWindow(&child))&&!child,"destroyed child retained");Check(handler->SetWindow(parent.value,&rect));Check(handler->DoPreview());}Check(handler->Unload());Check(handler->Unload());}auto after=GetGuiResources(GetCurrentProcess(),GR_GDIOBJECTS);Assert(after<=before+2,"GDI leak");});
        Test("Preview creation failure releases windows and can retry",[&]{
            Window parent;Assert(parent.value!=nullptr,"test HWND missing");
            auto before=GetGuiResources(GetCurrentProcess(),GR_USEROBJECTS);
            for(unsigned rejected=1;rejected<=3;++rejected) {
                auto initializer=module.Create<IInitializeWithStream>(PreviewId);auto stream=Stream(Book());Check(initializer->Initialize(stream.Get(),0));
                ComPtr<IPreviewHandler> handler;Check(initializer.As(&handler));RECT rect{0,0,400,500};Check(handler->SetWindow(parent.value,&rect));
                {
                    RejectChildWindow failure(rejected);
                    Assert(FAILED(handler->DoPreview()),"failed window creation returned success");
                    Assert(failure.created==rejected,"window creation continued after failure");
                }
                ComPtr<IOleWindow> window;Check(handler.As(&window));HWND child=nullptr;
                Assert(FAILED(window->GetWindow(&child))&&!child,"failed preview retained a window");
                Assert(!GetWindow(parent.value,GW_CHILD),"failed preview leaked a child HWND");
                Check(handler->DoPreview());Check(window->GetWindow(&child));Assert(IsWindow(child),"preview retry failed");Check(handler->Unload());
            }
            Assert(GetGuiResources(GetCurrentProcess(),GR_USEROBJECTS)<=before+2,"preview failure leaked USER objects");
        });
        Test("Mutation corpus rejects corrupt input without memory faults",[]{auto original=Book();for(size_t i=0;i<100;++i){auto bytes=original;bytes[(i*7919)%bytes.size()]^=0xff;try{ReadEpub(bytes);}catch(const Error&){} }});
        Test("Native module unloads without CLR",[&]{Assert(module.unload()==S_OK,"COM/window object leaked");Assert(!GetModuleHandleW(L"coreclr.dll")&&!GetModuleHandleW(L"hostfxr.dll"),"native Shell loaded CLR");});
        std::cout<<"Native Shell tests: "<<passed<<" passed\n";CoUninitialize();return 0;
    }catch(const Error& error){std::cerr<<"FAILED HRESULT 0x"<<std::hex<<error.code<<'\n';}catch(const std::exception& error){std::cerr<<"FAILED "<<error.what()<<'\n';}
    CoUninitialize();return 1;
}
