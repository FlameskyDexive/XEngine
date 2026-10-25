// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

namespace XEngine.Echo;

public interface ISerializable
{
    public void Serialize(ref EchoObject compound, SerializationContext ctx);
    public void Deserialize(EchoObject value, SerializationContext ctx);

}
