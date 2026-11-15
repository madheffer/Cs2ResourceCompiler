using System.Reflection;
using ValveResourceFormat;

namespace Source2.Compiler;

/// <summary>
/// What the map compiler's VRF-based containers (Legacy/) still need that the resource library no longer
/// offers: VRF's <c>Resource.Version</c> has a private setter. Goes with the rest of Legacy/ when the map
/// compiler writes through ResourceFile (docs/MAP_SPLIT.md, "No VRF in either repo").
/// </summary>
public static class LegacyContainer
{
    private static readonly PropertyInfo ResourceVersionProp =
        typeof(Resource).GetProperty(nameof(Resource.Version), BindingFlags.Public | BindingFlags.Instance)
        ?? throw new InvalidOperationException("Resource.Version property missing - VRF API changed?");

    /// <summary>Set a VRF resource's container version.</summary>
    public static void SetResourceVersion(Resource resource, ushort version) => ResourceVersionProp.SetValue(resource, version);
}
