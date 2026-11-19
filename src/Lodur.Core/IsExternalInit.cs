// Lets C# 9 records use init accessors on netstandard2.1, which does not ship this marker type.
namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit
    {
    }
}
