#pragma once
#ifndef NOMINMAX
#define NOMINMAX
#endif
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <shobjidl.h>
#include <thumbcache.h>
#include <shlwapi.h>
#include <wincodec.h>
#include <wrl.h>
#include <algorithm>
#include <atomic>
#include <chrono>
#include <cstdint>
#include <filesystem>
#include <functional>
#include <map>
#include <memory>
#include <set>
#include <span>
#include <stdexcept>
#include <string>
#include <string_view>
#include <vector>
#include <utility>

namespace ff {
using Microsoft::WRL::ComPtr;
inline constexpr size_t MaxArchive = 64 * 1024 * 1024;
inline constexpr size_t MaxXml = 2 * 1024 * 1024;
inline constexpr size_t MaxCover = 8 * 1024 * 1024;
inline constexpr size_t MaxText = 40000;
inline constexpr size_t MaxEntries = 4096;
inline constexpr GUID CommandId{0xb23f9d12,0x9ed4,0x4a40,{0xb8,0x54,0x7a,0x2b,0x0c,0x7d,0x9a,0x14}};
inline constexpr GUID ThumbnailId{0x7657d52b,0x308a,0x4b9f,{0x82,0x1f,0x5d,0x83,0x9d,0xb7,0x4a,0x11}};
inline constexpr GUID PreviewId{0x34e71d22,0xebb7,0x49e1,{0xa7,0xb3,0xcb,0x91,0x86,0xcc,0x4d,0x20}};
inline std::atomic<long> Objects{0};
struct Lifetime { Lifetime() { ++Objects; } ~Lifetime() { --Objects; } };
struct Error { HRESULT code; };
inline void Check(HRESULT hr) { if (FAILED(hr)) throw Error{hr}; }
inline void Require(bool value) { if (!value) throw Error{HRESULT_FROM_WIN32(ERROR_INVALID_DATA)}; }
template<class F> HRESULT Guard(F&& action) noexcept {
    try { action(); return S_OK; }
    catch (const Error& error) { return error.code; }
    catch (const std::bad_alloc&) { return E_OUTOFMEMORY; }
    catch (...) { return E_FAIL; }
}
struct Deadline {
    std::chrono::steady_clock::time_point end = std::chrono::steady_clock::now() + std::chrono::seconds(3);
    void Check() const { if (std::chrono::steady_clock::now() > end) throw Error{HRESULT_FROM_WIN32(ERROR_TIMEOUT)}; }
};
struct Content { std::wstring title, author, text; std::vector<uint8_t> cover; };
struct Bitmap {
    HBITMAP value = nullptr;
    Bitmap() = default;
    explicit Bitmap(HBITMAP handle) : value(handle) {}
    Bitmap(const Bitmap&) = delete;
    Bitmap& operator=(const Bitmap&) = delete;
    Bitmap(Bitmap&& other) noexcept : value(std::exchange(other.value,nullptr)) {}
    Bitmap& operator=(Bitmap&& other) noexcept { if (this != &other) { Reset(); value=std::exchange(other.value,nullptr); } return *this; }
    ~Bitmap() { Reset(); }
    void Reset(HBITMAP next=nullptr) { if(value) DeleteObject(value); value=next; }
    HBITMAP Detach() { return std::exchange(value,nullptr); }
};
std::vector<uint8_t> ReadStream(IStream* stream);
Content ReadEpub(std::span<const uint8_t> bytes, bool includeText=true);
std::string ResolvePath(std::string_view packagePath, std::string_view href);
ComPtr<IWICBitmap> DecodeCover(std::span<const uint8_t> bytes);
Bitmap ScaleCover(IWICBitmapSource* source, UINT maxWidth, UINT maxHeight, UINT& width, UINT& height);
std::wstring Wide(std::string_view utf8);
std::string Utf8(std::wstring_view value);
std::wstring Extension(std::wstring_view path);
std::wstring_view Kind(std::wstring_view extension);
bool CanConvert(const std::vector<std::wstring>& paths, std::wstring_view target);
std::wstring SerializeBatch(const std::vector<std::wstring>& paths);
HRESULT WriteBatchAndLaunch(const std::vector<std::wstring>& paths, std::wstring_view target) noexcept;
HRESULT CreateShellObject(REFCLSID clsid, REFIID iid, void** result) noexcept;
}
