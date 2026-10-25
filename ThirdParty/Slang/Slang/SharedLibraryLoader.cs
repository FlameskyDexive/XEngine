// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Slang.Native;


namespace XEngine.Slang;


internal abstract class SharedLibraryLoader : ManagedComProxy<ISlangSharedLibraryLoader, SlangSharedLibraryLoaderThunks>, ISlangSharedLibraryLoader
{
    public abstract unsafe SlangResult LoadSharedLibrary(ConstU8Str path, out ISlangSharedLibrary* sharedLibraryOut);
}
