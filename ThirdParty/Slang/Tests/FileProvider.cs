// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

namespace XEngine.Slang.Test;


public class FileProvider : IFileProvider
{
    public Memory<byte>? LoadFile(string path)
    {
        if (!File.Exists(path))
            return null;

        return new Memory<byte>(File.ReadAllBytes(path));
    }
}
