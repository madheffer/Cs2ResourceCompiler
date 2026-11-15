// Ported from ValveResourceFormat (MIT, https://github.com/ValveResourceFormat/ValveResourceFormat),
// Serialization/KeyValues/KVObjectDeepClone.cs.
using System.Globalization;
using ValveKeyValue;

namespace Source2.Compiler.Maps;

/// <summary>
/// A deep copy of a KV tree: collections, arrays, scalars and flags. Unlike
/// <see cref="PackageResourceBuilder.CloneKv"/> it does not carry the stored KV3 wire types, so the copy is
/// written with the writer's own type choice (the entity templates' copies were measured this way).
/// </summary>
public static class KVObjectDeepClone
{
    /// <summary>The copy.</summary>
    public static KVObject Clone(KVObject value)
    {
        ArgumentNullException.ThrowIfNull(value);
        KVObject clone;
        if (value.IsCollection)
        {
            clone = KVObject.ListCollection(value.Count);
            foreach (var (name, child) in value.Children)
                clone.Add(name, Clone(child));
        }
        else if (value.IsArray)
        {
            clone = KVObject.Array(value.Count);
            foreach (var element in value.Values)
                clone.Add(Clone(element));
        }
        else
            clone = value.ValueType switch
            {
                KVValueType.Null => KVObject.Null(),
                KVValueType.BinaryBlob => KVObject.Blob(value.AsBlob().ToArray()),
                KVValueType.Boolean => new KVObject(value.ToBoolean(CultureInfo.InvariantCulture)),
                KVValueType.String => new KVObject(value.ToString(CultureInfo.InvariantCulture)),
                KVValueType.Int16 => new KVObject(value.ToInt16(CultureInfo.InvariantCulture)),
                KVValueType.UInt16 => new KVObject(value.ToUInt16(CultureInfo.InvariantCulture)),
                KVValueType.Int32 => new KVObject(value.ToInt32(CultureInfo.InvariantCulture)),
                KVValueType.UInt32 => new KVObject(value.ToUInt32(CultureInfo.InvariantCulture)),
                KVValueType.Int64 => new KVObject(value.ToInt64(CultureInfo.InvariantCulture)),
                KVValueType.UInt64 => new KVObject(value.ToUInt64(CultureInfo.InvariantCulture)),
                KVValueType.FloatingPoint => new KVObject(value.ToSingle(CultureInfo.InvariantCulture)),
                KVValueType.FloatingPoint64 => new KVObject(value.ToDouble(CultureInfo.InvariantCulture)),
                KVValueType.Pointer => new KVObject((IntPtr)value),
                _ => throw new NotSupportedException($"Cannot clone a KV value of type {value.ValueType}"),
            };
        if (value.Flag != KVFlag.None)
            clone.Flag = value.Flag;
        return clone;
    }
}
