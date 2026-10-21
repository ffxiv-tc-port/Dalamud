using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace Dalamud.Utility;

/// <summary>
/// Extension methods for enums.
/// </summary>
public static class EnumExtensions
{
    // Keyed by enum type, field name and attribute type. Collectible types are never added, so plugins still unload.
    private static readonly ConcurrentDictionary<(Type Enum, string Name, Type Attribute), Attribute?> SingleAttributes = new();

    /// <summary>
    /// Gets attributes on an enum.
    /// </summary>
    /// <typeparam name="TAttribute">The type of attribute to get.</typeparam>
    /// <param name="value">The enum value that has an attached attribute.</param>
    /// <returns>The enumerable of the attached attributes.</returns>
    public static IEnumerable<TAttribute> GetAttributes<TAttribute>(this Enum value)
        where TAttribute : Attribute
    {
        var type = value.GetType();
        var name = Enum.GetName(type, value);
        if (name.IsNullOrEmpty())
            return Array.Empty<TAttribute>();

        return type.GetField(name)?
                   .GetCustomAttributes(false)
                   .OfType<TAttribute>()
               ?? Array.Empty<TAttribute>();
    }

    /// <summary>
    /// Gets an attribute on an enum.
    /// </summary>
    /// <typeparam name="TAttribute">The type of attribute to get.</typeparam>
    /// <param name="value">The enum value that has an attached attribute.</param>
    /// <returns>The attached attribute, if any.</returns>
    /// <remarks>The attribute is created once per enum value and attribute type and then shared: do not change it.</remarks>
    public static TAttribute? GetAttribute<TAttribute>(this Enum value)
        where TAttribute : Attribute
    {
        var type = value.GetType();
        if (type.IsCollectible || typeof(TAttribute).IsCollectible || Enum.GetName(type, value) is not { Length: > 0 } name)
            return value.GetAttributes<TAttribute>().SingleOrDefault();

        var key = (type, name, typeof(TAttribute));
        if (SingleAttributes.TryGetValue(key, out var cached))
            return (TAttribute?)cached;

        // Several matching attributes throw here as before, and nothing is cached for them.
        var attribute = value.GetAttributes<TAttribute>().SingleOrDefault();
        SingleAttributes.TryAdd(key, attribute);
        return attribute;
    }

    /// <summary>
    /// Gets an indicator if enum has been flagged as obsolete (deprecated).
    /// </summary>
    /// <param name="value">The enum value that has an attached attribute.</param>
    /// <returns>Indicator if enum has been flagged as obsolete.</returns>
    public static bool IsObsolete(this Enum value)
    {
        return GetAttribute<ObsoleteAttribute>(value) != null;
    }
}
