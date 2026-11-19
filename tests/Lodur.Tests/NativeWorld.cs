namespace Lodur.Tests;

/// <summary>
/// Test classes that build Ymir worlds share this collection so xunit runs them one at a time: two native
/// Box3D worlds alive on different threads crashed the test host (0xC000001D) on Windows/MinGW, 2026-11-19.
/// </summary>
[CollectionDefinition(Name)]
public sealed class NativeWorld
{
    public const string Name = "native Ymir world";
}
