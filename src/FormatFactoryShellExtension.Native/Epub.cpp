#include "Native.h"
#include <xmllite.h>
#include "miniz.h"
#include <array>
#include <cctype>
#include <cwctype>
#include <utility>

namespace ff {
std::wstring Wide(std::string_view value) {
    if(value.empty()) return {};
    int count=MultiByteToWideChar(CP_UTF8,MB_ERR_INVALID_CHARS,value.data(),static_cast<int>(value.size()),nullptr,0);
    Require(count>0); std::wstring result(count,L'\0');
    Require(MultiByteToWideChar(CP_UTF8,MB_ERR_INVALID_CHARS,value.data(),static_cast<int>(value.size()),result.data(),count)==count);
    return result;
}
std::string Utf8(std::wstring_view value) {
    if(value.empty()) return {};
    int count=WideCharToMultiByte(CP_UTF8,WC_ERR_INVALID_CHARS,value.data(),static_cast<int>(value.size()),nullptr,0,nullptr,nullptr);
    Require(count>0); std::string result(count,'\0');
    Require(WideCharToMultiByte(CP_UTF8,WC_ERR_INVALID_CHARS,value.data(),static_cast<int>(value.size()),result.data(),count,nullptr,nullptr)==count);
    return result;
}
std::vector<uint8_t> ReadStream(IStream* stream) {
    Require(stream!=nullptr); Deadline deadline;
    LARGE_INTEGER zero{}; Check(stream->Seek(zero,STREAM_SEEK_SET,nullptr));
    std::vector<uint8_t> result; std::array<uint8_t,32768> buffer;
    STATSTG stat{};
    if(SUCCEEDED(stream->Stat(&stat,STATFLAG_NONAME))) {
        Require(stat.cbSize.QuadPart<=MaxArchive);
        result.reserve(static_cast<size_t>(stat.cbSize.QuadPart));
    }
    for(;;) {
        deadline.Check(); ULONG read=0;
        Check(stream->Read(buffer.data(),static_cast<ULONG>(buffer.size()),&read));
        Require(read<=buffer.size() && result.size()+read<=MaxArchive);
        if(!read) break;
        result.insert(result.end(),buffer.begin(),buffer.begin()+read);
    }
    deadline.Check(); return result;
}
static uint16_t U16(std::span<const uint8_t> bytes,size_t offset) {
    Require(offset+2<=bytes.size()); return uint16_t(bytes[offset]|uint16_t(bytes[offset+1])<<8);
}
static uint32_t U32(std::span<const uint8_t> bytes,size_t offset) {
    return uint32_t(U16(bytes,offset)) | uint32_t(U16(bytes,offset+2))<<16;
}
static void ValidateDirectory(std::span<const uint8_t> bytes,const Deadline& deadline) {
    Require(bytes.size()>=22 && bytes.size()<=MaxArchive);
    size_t end=SIZE_MAX,lower=bytes.size()>65557?bytes.size()-65557:0;
    for(size_t i=bytes.size()-22;;--i) {
        if(U32(bytes,i)==0x06054b50 && i+22+U16(bytes,i+20)==bytes.size()) { end=i; break; }
        if(i==lower) break;
    }
    Require(end!=SIZE_MAX);
    const auto entries=U16(bytes,end+10); const uint64_t start=U32(bytes,end+16),size=U32(bytes,end+12);
    Require(U32(bytes,end+4)==0 && U16(bytes,end+8)==entries && entries<=MaxEntries && start+size<=end);
    size_t position=static_cast<size_t>(start),count=0,finish=static_cast<size_t>(start+size);
    while(position<finish) {
        deadline.Check(); Require(++count<=MaxEntries && finish-position>=46 && U32(bytes,position)==0x02014b50);
        auto name=U16(bytes,position+28),extra=U16(bytes,position+30),comment=U16(bytes,position+32);
        Require(U16(bytes,position+34)==0 && !(U16(bytes,position+8)&1));
        Require(U32(bytes,position+20)!=UINT32_MAX && U32(bytes,position+24)!=UINT32_MAX && U32(bytes,position+42)!=UINT32_MAX);
        Require(position+46+name+extra+comment<=finish);
        size_t extraPos=position+46+name,extraEnd=extraPos+extra;
        while(extraPos<extraEnd) { Require(extraEnd-extraPos>=4); auto tag=U16(bytes,extraPos),length=U16(bytes,extraPos+2); Require(tag!=1 && extraPos+4+length<=extraEnd); extraPos+=4+length; }
        position+=46+name+extra+comment;
    }
    Require(count==entries);
}
std::string ResolvePath(std::string_view packagePath,std::string_view href) {
    href=href.substr(0,href.find_first_of("#?")); std::string decoded;
    for(size_t i=0;i<href.size();++i) {
        if(href[i]=='%') {
            Require(i+2<href.size()); auto hex=[](char c)->int { if(c>='0'&&c<='9')return c-'0'; if(c>='a'&&c<='f')return c-'a'+10; if(c>='A'&&c<='F')return c-'A'+10; return -1; };
            int high=hex(href[i+1]),low=hex(href[i+2]); Require(high>=0&&low>=0); decoded+=char(high*16+low); i+=2;
        } else decoded+=href[i];
    }
    Require(!decoded.empty() && decoded[0]!='/' && decoded.find_first_of("\\:")==std::string::npos && decoded.find('\0')==std::string::npos);
    Wide(decoded); // Reject malformed UTF-8 before looking up ZIP names.
    auto slash=packagePath.rfind('/'); std::string joined=slash==std::string_view::npos?decoded:std::string(packagePath.substr(0,slash+1))+decoded;
    std::vector<std::string> parts;
    for(size_t begin=0;begin<=joined.size();) {
        auto next=joined.find('/',begin); if(next==std::string::npos)next=joined.size(); auto part=joined.substr(begin,next-begin);
        if(part=="..") { Require(!parts.empty()); parts.pop_back(); } else if(!part.empty()&&part!=".")parts.push_back(part);
        if(next==joined.size())break; begin=next+1;
    }
    std::string result; for(const auto& part:parts) { if(!result.empty())result+='/'; result+=part; } Require(!result.empty()); return result;
}
struct XmlNode {
    std::wstring name,ns,text; std::map<std::wstring,std::wstring> attributes;
    std::vector<std::unique_ptr<XmlNode>> children;
    std::wstring Attr(const wchar_t* key) const { auto it=attributes.find(key); return it==attributes.end()?L"":it->second; }
};
static std::unique_ptr<XmlNode> Xml(const std::vector<uint8_t>& bytes,const Deadline& deadline) {
    ComPtr<IStream> stream; stream.Attach(SHCreateMemStream(bytes.data(),static_cast<UINT>(bytes.size()))); Require(stream!=nullptr);
    ComPtr<IXmlReader> reader; Check(CreateXmlReader(__uuidof(IXmlReader),reinterpret_cast<void**>(reader.GetAddressOf()),nullptr));
    Check(reader->SetProperty(XmlReaderProperty_DtdProcessing,DtdProcessing_Prohibit));
    Check(reader->SetProperty(XmlReaderProperty_MaxElementDepth,256));
    // EPUB XHTML commonly carries a harmless <!DOCTYPE html>. Strip only a plain
    // declaration; internal subsets and external identifiers remain prohibited.
    Check(reader->SetInput(stream.Get()));
    auto document=std::make_unique<XmlNode>(); std::vector<XmlNode*> stack{document.get()};
    XmlNodeType type; HRESULT hr; size_t nodes=0,chars=0;
    while((hr=reader->Read(&type))==S_OK) {
        deadline.Check(); Require(++nodes<=200000);
        if(type==XmlNodeType_Element) {
            auto node=std::make_unique<XmlNode>(); const wchar_t *name=nullptr,*ns=nullptr;
            Check(reader->GetLocalName(&name,nullptr)); Check(reader->GetNamespaceUri(&ns,nullptr)); node->name=name?name:L""; node->ns=ns?ns:L"";
            bool empty=reader->IsEmptyElement()!=FALSE;
            if(reader->MoveToFirstAttribute()==S_OK) {
                do { const wchar_t *key=nullptr,*value=nullptr; Check(reader->GetLocalName(&key,nullptr)); Check(reader->GetValue(&value,nullptr)); node->attributes[key?key:L""]=value?value:L""; } while(reader->MoveToNextAttribute()==S_OK);
                Check(reader->MoveToElement());
            }
            auto pointer=node.get(); stack.back()->children.push_back(std::move(node)); if(!empty)stack.push_back(pointer);
        } else if(type==XmlNodeType_EndElement) { Require(stack.size()>1); stack.pop_back(); }
        else if(type==XmlNodeType_Text || type==XmlNodeType_CDATA || type==XmlNodeType_Whitespace) {
            const wchar_t* value=nullptr; UINT length=0; Check(reader->GetValue(&value,&length)); Require((chars+=length)<=MaxXml);
            auto node=std::make_unique<XmlNode>(); node->name=L"#text"; node->text.assign(value,length); stack.back()->children.push_back(std::move(node));
        }
    }
    Check(hr); Require(stack.size()==1 && !document->children.empty()); return document;
}
static void Descendants(XmlNode& node,const std::function<void(XmlNode&)>& action) { for(auto& child:node.children) { action(*child); Descendants(*child,action); } }
static std::wstring Text(XmlNode& node) { std::wstring result=node.text; for(auto& child:node.children)result+=Text(*child); return result; }
static bool HasWord(std::wstring_view value,std::wstring_view word) {
    size_t begin=0; while(begin<value.size()) { auto end=value.find(L' ',begin); if(end==std::wstring_view::npos)end=value.size(); if(value.substr(begin,end-begin)==word)return true; begin=end+1; } return false;
}
static std::wstring Normalize(std::wstring_view value) {
    std::wstring result; bool space=false;
    for(wchar_t c:value) { if(iswspace(c))space=!result.empty(); else { if(space)result+=L' '; result+=c; space=false; } } return result;
}
struct Zip {
    mz_zip_archive archive{}; bool initialized=false; const Deadline& deadline;
    std::map<std::string,mz_uint> entries;
    Zip(std::span<const uint8_t> bytes,const Deadline& value):deadline(value) {
        ValidateDirectory(bytes,deadline); Require(mz_zip_reader_init_mem(&archive,bytes.data(),bytes.size(),0)!=0); initialized=true;
        try { auto count=mz_zip_reader_get_num_files(&archive); Require(count<=MaxEntries);
            for(mz_uint i=0;i<count;++i) { deadline.Check(); auto length=mz_zip_reader_get_filename(&archive,i,nullptr,0); Require(length>0&&length<=65536); std::string name(length,'\0'); Require(mz_zip_reader_get_filename(&archive,i,name.data(),length)==length); name.pop_back(); Require(name.find('\0')==std::string::npos && entries.emplace(name,i).second); }
        } catch(...) { mz_zip_reader_end(&archive); initialized=false; throw; }
    }
    ~Zip() { if(initialized)mz_zip_reader_end(&archive); }
    std::vector<uint8_t> Bytes(const std::string& name,size_t limit) {
        deadline.Check(); auto it=entries.find(name); Require(it!=entries.end()); mz_zip_archive_file_stat stat{}; Require(mz_zip_reader_file_stat(&archive,it->second,&stat)!=0);
        Require(stat.m_uncomp_size<=limit && !(stat.m_uncomp_size>1024*1024 && stat.m_uncomp_size>std::max<mz_uint64>(1,stat.m_comp_size)*200));
        struct Sink { std::vector<uint8_t> bytes; size_t limit; const Deadline* deadline; bool failed=false; } sink{{},limit,&deadline}; sink.bytes.reserve(static_cast<size_t>(stat.m_uncomp_size));
        auto callback=[](void* opaque,mz_uint64 offset,const void* data,size_t count)->size_t {
            auto& value=*static_cast<Sink*>(opaque);
            try { value.deadline->Check(); Require(offset==value.bytes.size() && count<=value.limit-value.bytes.size()); auto p=static_cast<const uint8_t*>(data); value.bytes.insert(value.bytes.end(),p,p+count); return count; } catch(...) { value.failed=true; return 0; }
        };
        Require(mz_zip_reader_extract_to_callback(&archive,it->second,callback,&sink,0)!=0 && !sink.failed && sink.bytes.size()==stat.m_uncomp_size); deadline.Check(); return sink.bytes;
    }
    std::unique_ptr<XmlNode> Document(const std::string& path) {
        auto bytes=Bytes(path,MaxXml);
        // Ignore a plain HTML DOCTYPE exactly as the managed reader does, without
        // enabling a resolver, entities, external DTDs or an internal subset.
        std::string text(reinterpret_cast<const char*>(bytes.data()),bytes.size()); auto pos=text.find("<!DOCTYPE");
        if(pos!=std::string::npos) { auto end=text.find('>',pos); Require(end!=std::string::npos); auto decl=text.substr(pos,end-pos+1); Require(decl=="<!DOCTYPE html>" || decl=="<!DOCTYPE HTML>"); text.erase(pos,end-pos+1); bytes.assign(text.begin(),text.end()); }
        return Xml(bytes,deadline);
    }
};
Content ReadEpub(std::span<const uint8_t> bytes,bool includeText) {
    Deadline deadline; Zip zip(bytes,deadline);
    auto mime=zip.Bytes("mimetype",64); Require(Normalize(Wide(std::string_view(reinterpret_cast<const char*>(mime.data()),mime.size())))==L"application/epub+zip");
    const std::wstring opf=L"http://www.idpf.org/2007/opf",dc=L"http://purl.org/dc/elements/1.1/";
    auto container=zip.Document("META-INF/container.xml"); std::string packagePath;
    Descendants(*container,[&](XmlNode& node){if(packagePath.empty() && node.name==L"rootfile" && node.ns==L"urn:oasis:names:tc:opendocument:xmlns:container" && !node.Attr(L"full-path").empty())packagePath=ResolvePath("",Utf8(node.Attr(L"full-path")));}); Require(!packagePath.empty());
    auto package=zip.Document(packagePath); Content content; content.title=L"EPUB"; bool title=false; std::wstring coverId;
    XmlNode* root=nullptr; for(auto& node:package->children)if(node->name==L"package"&&node->ns==opf){root=node.get();break;} Require(root!=nullptr);
    Descendants(*package,[&](XmlNode& node){
        if(node.ns==dc && node.name==L"title" && !title) {content.title=Text(node).substr(0,300);title=true;}
        if(node.ns==dc && node.name==L"creator" && content.author.size()<500) {if(!content.author.empty())content.author+=L", ";content.author+=Text(node);content.author.resize(std::min<size_t>(500,content.author.size()));}
        if(node.ns==opf && node.name==L"meta" && node.Attr(L"name")==L"cover" && coverId.empty())coverId=node.Attr(L"content");
    });
    std::vector<XmlNode*> manifest,spine; bool foundManifest=false;
    for(auto& node:root->children) {
        if(node->ns==opf && node->name==L"manifest") {foundManifest=true;for(auto& child:node->children)if(child->name==L"item"&&child->ns==opf)manifest.push_back(child.get());}
        if(node->ns==opf && node->name==L"spine")for(auto& child:node->children)if(child->name==L"itemref"&&child->ns==opf)spine.push_back(child.get());
    }
    Require(foundManifest); XmlNode* cover=nullptr;
    for(auto item:manifest)if(HasWord(item->Attr(L"properties"),L"cover-image")){cover=item;break;}
    if(!cover&&!coverId.empty())for(auto item:manifest)if(item->Attr(L"id")==coverId){cover=item;break;}
    if(cover && (cover->Attr(L"media-type")==L"image/png"||cover->Attr(L"media-type")==L"image/jpeg")) {
        content.cover=zip.Bytes(ResolvePath(packagePath,Utf8(cover->Attr(L"href"))),MaxCover);
        const auto& b=content.cover; constexpr uint8_t png[]{137,80,78,71,13,10,26,10};
        if(!((b.size()>=8&&std::equal(std::begin(png),std::end(png),b.begin()))||(b.size()>=3&&b[0]==255&&b[1]==216&&b[2]==255)))content.cover.clear();
    }
    for(size_t index=0;includeText&&index<std::min<size_t>(8,spine.size())&&content.text.size()<MaxText-2;++index) {
        deadline.Check(); auto reference=spine[index]; if(reference->Attr(L"linear")==L"no")continue; XmlNode* item=nullptr;
        for(auto entry:manifest)if(entry->Attr(L"id")==reference->Attr(L"idref")){item=entry;break;}
        if(!item||item->Attr(L"media-type")!=L"application/xhtml+xml")continue;
        auto chapter=zip.Document(ResolvePath(packagePath,Utf8(item->Attr(L"href")))); XmlNode* body=nullptr; Descendants(*chapter,[&](XmlNode& node){if(!body&&node.name==L"body")body=&node;}); if(!body)continue;
        std::function<void(XmlNode&)> append=[&](XmlNode& node){
            deadline.Check(); if(node.name==L"script"||node.name==L"style"||node.name==L"noscript"||node.name==L"svg"||content.text.size()>=MaxText-2)return;
            if(node.name==L"#text") {auto normalized=Normalize(node.text);if(!normalized.empty())content.text+=normalized.substr(0,MaxText-content.text.size()-2)+L"\r\n";}
            for(auto& child:node.children)append(*child);
        }; append(*body);
    }
    deadline.Check(); return content;
}
}
