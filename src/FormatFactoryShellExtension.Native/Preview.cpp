#include "Native.h"
#include <commctrl.h>

namespace ff {
using namespace Microsoft::WRL;
static HRESULT WindowError() noexcept {auto error=GetLastError();return error?HRESULT_FROM_WIN32(error):E_FAIL;}
class PreviewWindow final : private Lifetime,public std::enable_shared_from_this<PreviewWindow> {
    HWND window_=nullptr,cover_=nullptr,text_=nullptr;
    Bitmap bitmap_;ComPtr<IWICBitmap> decoded_;UINT lastWidth_=0,lastHeight_=0;
    static LRESULT CALLBACK Procedure(HWND window,UINT message,WPARAM wParam,LPARAM lParam,UINT_PTR id,DWORD_PTR data) noexcept {
        auto holder=reinterpret_cast<std::shared_ptr<PreviewWindow>*>(data);
        if(message==WM_DESTROY)(*holder)->ReleaseBitmap();
        if(message==WM_NCDESTROY){auto instance=*holder;RemoveWindowSubclass(window,Procedure,id);auto result=DefSubclassProc(window,message,wParam,lParam);instance->window_=instance->cover_=instance->text_=nullptr;instance->decoded_.Reset();delete holder;return result;}
        return DefSubclassProc(window,message,wParam,lParam);
    }
    void ReleaseBitmap() noexcept {
        auto attached=cover_&&IsWindow(cover_)?reinterpret_cast<HBITMAP>(SendMessageW(cover_,STM_SETIMAGE,IMAGE_BITMAP,0)):nullptr;
        if(attached&&attached!=bitmap_.value)DeleteObject(attached);bitmap_.Reset();
    }
public:
    ~PreviewWindow(){Close();}
    HWND Handle() const noexcept {return window_;}
    void Initialize(HWND parent,const RECT& rect,const Content& content) {
        // Allocate the subclass owner before creating an HWND so allocation
        // failure cannot strand a live child window.
        auto holder=std::make_unique<std::shared_ptr<PreviewWindow>>(shared_from_this());
        window_=CreateWindowExW(WS_EX_CONTROLPARENT,L"STATIC",L"",WS_CHILD|WS_VISIBLE|WS_CLIPCHILDREN,rect.left,rect.top,std::max<LONG>(1,rect.right-rect.left),std::max<LONG>(1,rect.bottom-rect.top),parent,nullptr,nullptr,nullptr);
        if(!window_)throw Error{WindowError()};
        if(!SetWindowSubclass(window_,Procedure,0,reinterpret_cast<DWORD_PTR>(holder.get()))){auto error=GetLastError();DestroyWindow(window_);window_=nullptr;throw Error{error?HRESULT_FROM_WIN32(error):E_FAIL};}holder.release();
        try {
            cover_=CreateWindowExW(0,L"STATIC",L"",WS_CHILD|WS_VISIBLE|SS_BITMAP,0,0,1,1,window_,nullptr,nullptr,nullptr);
            if(!cover_)throw Error{WindowError()};
            auto text=content.title+L"\r\n"+content.author+L"\r\n\r\n"+content.text;
            text_=CreateWindowExW(0,L"EDIT",text.c_str(),WS_CHILD|WS_VISIBLE|WS_VSCROLL|WS_TABSTOP|ES_MULTILINE|ES_READONLY|ES_AUTOVSCROLL,0,0,1,1,window_,nullptr,nullptr,nullptr);
            if(!text_)throw Error{WindowError()};SendMessageW(text_,WM_SETFONT,reinterpret_cast<WPARAM>(GetStockObject(DEFAULT_GUI_FONT)),TRUE);
            if(!content.cover.empty()) { try{decoded_=DecodeCover(content.cover);}catch(const Error&){} }
            Layout(rect);
        } catch(...){Close();throw;}
    }
    void Close() noexcept {if(window_)DestroyWindow(window_);}
    void Reparent(HWND parent,const RECT& rect){SetLastError(0);if(!SetParent(window_,parent)&&GetLastError())throw Error{HRESULT_FROM_WIN32(GetLastError())};Layout(rect);}
    void Layout(const RECT& rect) {
        if(!window_)return;UINT width=std::max<LONG>(1,rect.right-rect.left),height=std::max<LONG>(1,rect.bottom-rect.top);
        SetWindowPos(window_,nullptr,rect.left,rect.top,width,height,SWP_NOZORDER|SWP_NOACTIVATE);
        if(width==lastWidth_&&height==lastHeight_)return;lastWidth_=width;lastHeight_=height;
        UINT imageWidth=0,imageHeight=0;Bitmap next;
        if(decoded_&&width>32&&height>120) {try{next=ScaleCover(decoded_.Get(),std::min(width-16,512u),std::min(height/2,480u),imageWidth,imageHeight);}catch(const Error&){}}
        ReleaseBitmap();bitmap_=std::move(next);SendMessageW(cover_,STM_SETIMAGE,IMAGE_BITMAP,reinterpret_cast<LPARAM>(bitmap_.value));ShowWindow(cover_,bitmap_.value?SW_SHOW:SW_HIDE);
        SetWindowPos(cover_,nullptr,(width-imageWidth)/2,8,imageWidth,imageHeight,SWP_NOZORDER|SWP_NOACTIVATE);auto top=bitmap_.value?imageHeight+16:8;
        SetWindowPos(text_,nullptr,8,top,std::max(1,int(width)-16),std::max(1,int(height)-int(top)-8),SWP_NOZORDER|SWP_NOACTIVATE);
    }
    void Focus(){if(text_)::SetFocus(text_);}
};
static bool Valid(const RECT& r){auto w=int64_t(r.right)-r.left,h=int64_t(r.bottom)-r.top;return w>=0&&w<=32768&&h>=0&&h<=32768;}
class Thumbnail final:public RuntimeClass<RuntimeClassFlags<ClassicCom>,IInitializeWithStream,IThumbnailProvider>,private Lifetime {
    ComPtr<IStream> stream_;
    ComPtr<IWICBitmap> cover_;
public:
    HRESULT STDMETHODCALLTYPE Initialize(IStream* stream,DWORD mode) override {if(!stream||(mode&3))return E_INVALIDARG;if(stream_)return E_UNEXPECTED;stream_=stream;return S_OK;}
    HRESULT STDMETHODCALLTYPE GetThumbnail(UINT size,HBITMAP* bitmap,WTS_ALPHATYPE* alpha) override {
        if(!bitmap||!alpha)return E_POINTER;*bitmap=nullptr;*alpha=WTSAT_UNKNOWN;if(!size||size>4096)return E_INVALIDARG;if(!stream_)return E_UNEXPECTED;
        return Guard([&]{
            if(!cover_) {
                auto bytes=ReadStream(stream_.Get());
                auto content=ReadEpub(bytes,false);
                Require(!content.cover.empty());
                cover_=DecodeCover(content.cover);
            }
            UINT width=0,height=0;
            auto image=ScaleCover(cover_.Get(),size,size,width,height);
            *bitmap=image.Detach();*alpha=WTSAT_RGB;
        });
    }
};
class Preview final:public RuntimeClass<RuntimeClassFlags<ClassicCom>,IInitializeWithStream,IPreviewHandler,IObjectWithSite,IOleWindow>,private Lifetime {
    ComPtr<IStream> stream_;ComPtr<IUnknown> site_;std::shared_ptr<PreviewWindow> window_;HWND parent_=nullptr;RECT rect_{};
public:
    ~Preview(){Unload();}
    HRESULT STDMETHODCALLTYPE Initialize(IStream* stream,DWORD mode) override {if(!stream||(mode&3))return E_INVALIDARG;if(stream_)return E_UNEXPECTED;stream_=stream;return S_OK;}
    HRESULT STDMETHODCALLTYPE SetWindow(HWND parent,const RECT* rect) override {if(!rect||!parent||!IsWindow(parent)||!Valid(*rect))return E_INVALIDARG;return Guard([&]{parent_=parent;rect_=*rect;if(window_&&!window_->Handle())window_.reset();if(window_)window_->Reparent(parent_,rect_);});}
    HRESULT STDMETHODCALLTYPE SetRect(const RECT* rect) override {if(!rect||!Valid(*rect))return E_INVALIDARG;return Guard([&]{rect_=*rect;if(window_)window_->Layout(rect_);});}
    HRESULT STDMETHODCALLTYPE DoPreview() override {
        if(!stream_||!parent_||!IsWindow(parent_))return E_UNEXPECTED;if(window_&&window_->Handle())return S_OK;
        return Guard([&]{window_.reset();auto bytes=ReadStream(stream_.Get());auto content=ReadEpub(bytes);auto window=std::make_shared<PreviewWindow>();window->Initialize(parent_,rect_,content);window_=std::move(window);});
    }
    HRESULT STDMETHODCALLTYPE Unload() override {if(window_)window_->Close();window_.reset();stream_.Reset();parent_=nullptr;return S_OK;}
    HRESULT STDMETHODCALLTYPE SetFocus() override {if(!window_||!window_->Handle())return E_UNEXPECTED;window_->Focus();return S_OK;}
    HRESULT STDMETHODCALLTYPE QueryFocus(HWND* window) override {if(!window)return E_POINTER;*window=::GetFocus();return *window?S_OK:S_FALSE;}
    HRESULT STDMETHODCALLTYPE TranslateAccelerator(MSG* message) override {if(!message)return E_POINTER;ComPtr<IPreviewHandlerFrame> frame;return site_&&SUCCEEDED(site_.As(&frame))?frame->TranslateAccelerator(message):S_FALSE;}
    HRESULT STDMETHODCALLTYPE SetSite(IUnknown* site) override {site_=site;return S_OK;}
    HRESULT STDMETHODCALLTYPE GetSite(REFIID iid,void** result) override {if(!result)return E_POINTER;*result=nullptr;return site_?site_->QueryInterface(iid,result):E_NOINTERFACE;}
    HRESULT STDMETHODCALLTYPE GetWindow(HWND* window) override {if(!window)return E_POINTER;*window=window_?window_->Handle():nullptr;return *window?S_OK:E_FAIL;}
    HRESULT STDMETHODCALLTYPE ContextSensitiveHelp(BOOL) override {return E_NOTIMPL;}
};
HRESULT CreateCommand(REFIID iid,void** result);
HRESULT CreateShellObject(REFCLSID clsid,REFIID iid,void** result) noexcept {
    if(!result)return E_POINTER;*result=nullptr;return Guard([&]{if(clsid==CommandId)Check(CreateCommand(iid,result));else if(clsid==ThumbnailId){auto object=Make<Thumbnail>();if(!object)throw Error{E_OUTOFMEMORY};Check(object->QueryInterface(iid,result));}else if(clsid==PreviewId){auto object=Make<Preview>();if(!object)throw Error{E_OUTOFMEMORY};Check(object->QueryInterface(iid,result));}else throw Error{CLASS_E_CLASSNOTAVAILABLE};});
}
class Factory final:public RuntimeClass<RuntimeClassFlags<ClassicCom>,IClassFactory>,private Lifetime {
    CLSID id_;
public:
    explicit Factory(REFCLSID id):id_(id){}
    HRESULT STDMETHODCALLTYPE CreateInstance(IUnknown* outer,REFIID iid,void** result) override {if(!result)return E_POINTER;*result=nullptr;return outer?CLASS_E_NOAGGREGATION:CreateShellObject(id_,iid,result);}
    HRESULT STDMETHODCALLTYPE LockServer(BOOL lock) override {if(lock)++Objects;else --Objects;return S_OK;}
};
}
extern "C" HRESULT __stdcall DllGetClassObject(REFCLSID id,REFIID iid,void** result) {
    if(!result)return E_POINTER;*result=nullptr;if(id!=ff::CommandId&&id!=ff::ThumbnailId&&id!=ff::PreviewId)return CLASS_E_CLASSNOTAVAILABLE;
    return ff::Guard([&]{auto factory=Microsoft::WRL::Make<ff::Factory>(id);if(!factory)throw ff::Error{E_OUTOFMEMORY};ff::Check(factory->QueryInterface(iid,result));});
}
extern "C" HRESULT __stdcall DllCanUnloadNow() {return ff::Objects.load()==0?S_OK:S_FALSE;}
BOOL WINAPI DllMain(HINSTANCE instance,DWORD reason,LPVOID) {if(reason==DLL_PROCESS_ATTACH)DisableThreadLibraryCalls(instance);return TRUE;}
