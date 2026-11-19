namespace Lodur.Tests;

/// <summary>
/// Test classes that build Ymir worlds share this collection so xunit runs them one at a time: two native
/// Box3D worlds alive on different threads crashed the test host (0xC000001D) on Windows/MinGW, 2026-11-19.
/// This works around a Ymir defect (lodur:finding:cut-nervous-layers.s1.ymir-concurrent-world-create: world create and
/// destroy are not thread-safe). Remove this collection, and the attributes naming it, in the body commit after Ymir
/// merges its lock around world create/destroy.
/// </summary>
[CollectionDefinition(Name)]
public sealed class NativeWorld
{
    public const string Name = "native Ymir world";
}
