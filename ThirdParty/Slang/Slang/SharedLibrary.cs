// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Slang.Native;


namespace XEngine.Slang;


internal class SharedLibrary
{
    internal ISlangSharedLibrary _library;


    internal SharedLibrary(ISlangSharedLibrary library)
    {
        _library = library;
    }
}
