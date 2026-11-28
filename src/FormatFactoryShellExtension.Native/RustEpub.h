#pragma once
#include <cstddef>
#include <cstdint>

// The archive index owns its directory. Member reads borrow the original
// archive span, and all buffers returned to COM are allocated in the C++ DLL.
extern "C" {
int32_t ff_epub_open(const uint8_t* data, size_t size, void** index);
void ff_epub_close(void* index);
int32_t ff_epub_size(const void* index, const char* name, size_t length, size_t limit, size_t* size);
int32_t ff_epub_read(const void* index, const uint8_t* data, size_t size, const char* name, size_t length,
                     uint8_t* dest, size_t capacity);
int32_t ff_epub_path(const char* base, size_t baseLength, const char* href, size_t hrefLength,
                     char* dest, size_t capacity, size_t* size);
}
