#ifndef Memory_h
#define Memory_h

#include <kern/assert.h>

kern_return_t Memory_Init();
kern_return_t Memory_Cleanup();

void* Memory_Alloc(uint32_t size);
void Memory_Free(void* buffer, uint32_t size);

template <typename T>
T* Memory_AllocArray(uint32_t arrayLength)
{
    size_t allocBytes = arrayLength * sizeof(T);
    if (allocBytes > UINT32_MAX)
    {
        return nullptr;
    }
    
    return static_cast<T*>(Memory_Alloc(static_cast<uint32_t>(allocBytes)));
}

template <typename T>
void Memory_FreeArray(T* array, uint32_t arrayLength)
{
    size_t arrayBytes = arrayLength * sizeof(T);
    assert(arrayBytes <= UINT32_MAX);
    Memory_Free(array, static_cast<uint32_t>(arrayBytes));
}

#endif /* Memory_h */
