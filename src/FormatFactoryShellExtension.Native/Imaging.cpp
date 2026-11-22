#include "Native.h"

namespace ff {
static uint32_t Big(std::span<const uint8_t> b,size_t p,size_t count) {
    Require(p+count<=b.size()); uint32_t n=0; for(size_t i=0;i<count;++i)n=(n<<8)|b[p+i];return n;
}
static bool ValidateDimensions(std::span<const uint8_t> b) {
    Require(b.size()>=3 && b.size()<=MaxCover); UINT width=0,height=0;
    constexpr uint8_t png[]{137,80,78,71,13,10,26,10};
    bool isPng=b.size()>=24&&std::equal(std::begin(png),std::end(png),b.begin());
    if(isPng) {Require(Big(b,12,4)==0x49484452);width=Big(b,16,4);height=Big(b,20,4);}
    else {
        Require(b[0]==255&&b[1]==216&&b[2]==255);
        for(size_t p=2;p+4<=b.size();) {
            Require(b[p++]==255); while(p<b.size()&&b[p]==255)++p; Require(p<b.size()); auto marker=b[p++];
            if(marker==0xd9||marker==0xda)break; if(marker==1||(marker>=0xd0&&marker<=0xd8))continue;
            auto length=Big(b,p,2);Require(length>=2&&p+length<=b.size());
            if(marker>=0xc0&&marker<=0xcf&&marker!=0xc4&&marker!=0xc8&&marker!=0xcc){Require(length>=8);height=Big(b,p+3,2);width=Big(b,p+5,2);break;}p+=length;
        }
    }
    Require(width&&height&&width<=16384&&height<=16384&&uint64_t(width)*height<=40000000);return isPng;
}
ComPtr<IWICBitmap> DecodeCover(std::span<const uint8_t> bytes) {
    bool png=ValidateDimensions(bytes); ComPtr<IWICImagingFactory> factory;
    Check(CoCreateInstance(CLSID_WICImagingFactory,nullptr,CLSCTX_INPROC_SERVER,IID_PPV_ARGS(&factory)));
    ComPtr<IWICStream> stream; Check(factory->CreateStream(&stream)); Check(stream->InitializeFromMemory(const_cast<BYTE*>(bytes.data()),static_cast<DWORD>(bytes.size())));
    // Explicit built-in decoder CLSIDs exclude installed third-party codecs.
    ComPtr<IWICBitmapDecoder> decoder;Check(CoCreateInstance(png?CLSID_WICPngDecoder:CLSID_WICJpegDecoder,nullptr,CLSCTX_INPROC_SERVER,IID_PPV_ARGS(&decoder)));
    Check(decoder->Initialize(stream.Get(),WICDecodeMetadataCacheOnLoad)); ComPtr<IWICBitmapFrameDecode> frame; Check(decoder->GetFrame(0,&frame));
    UINT width=0,height=0;Check(frame->GetSize(&width,&height)); Require(width&&height&&width<=16384&&height<=16384&&uint64_t(width)*height<=40000000);
    ComPtr<IWICBitmap> bitmap;Check(factory->CreateBitmapFromSource(frame.Get(),WICBitmapCacheOnLoad,&bitmap));return bitmap;
}
Bitmap ScaleCover(IWICBitmapSource* source,UINT maxWidth,UINT maxHeight,UINT& width,UINT& height) {
    Require(source&&maxWidth&&maxHeight&&maxWidth<=4096&&maxHeight<=4096); UINT sw=0,sh=0;Check(source->GetSize(&sw,&sh));Require(sw&&sh);
    double ratio=std::min({1.0,double(maxWidth)/sw,double(maxHeight)/sh});width=std::max(1u,UINT(sw*ratio));height=std::max(1u,UINT(sh*ratio));
    ComPtr<IWICImagingFactory> factory;Check(CoCreateInstance(CLSID_WICImagingFactory,nullptr,CLSCTX_INPROC_SERVER,IID_PPV_ARGS(&factory)));
    ComPtr<IWICBitmapScaler> scaler;Check(factory->CreateBitmapScaler(&scaler));Check(scaler->Initialize(source,width,height,WICBitmapInterpolationModeFant));
    ComPtr<IWICFormatConverter> converter;Check(factory->CreateFormatConverter(&converter));Check(converter->Initialize(scaler.Get(),GUID_WICPixelFormat32bppPBGRA,WICBitmapDitherTypeNone,nullptr,0,WICBitmapPaletteTypeCustom));
    BITMAPINFO info{};info.bmiHeader.biSize=sizeof(BITMAPINFOHEADER);info.bmiHeader.biWidth=width;info.bmiHeader.biHeight=-LONG(height);info.bmiHeader.biPlanes=1;info.bmiHeader.biBitCount=32;info.bmiHeader.biCompression=BI_RGB;
    void* raw=nullptr;Bitmap bitmap(CreateDIBSection(nullptr,&info,DIB_RGB_COLORS,&raw,nullptr,0));if(!bitmap.value||!raw)throw Error{E_OUTOFMEMORY};
    Check(converter->CopyPixels(nullptr,width*4,width*height*4,static_cast<BYTE*>(raw)));
    auto pixels=static_cast<uint8_t*>(raw);for(size_t i=0;i<size_t(width)*height*4;i+=4){auto white=255-pixels[i+3];pixels[i]=uint8_t(pixels[i]+white);pixels[i+1]=uint8_t(pixels[i+1]+white);pixels[i+2]=uint8_t(pixels[i+2]+white);pixels[i+3]=255;}
    return bitmap;
}
}
