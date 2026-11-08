using System;
using System.Collections;
using System.Collections.Generic;
using System.Numerics;
using System.Reflection;

namespace Poser.Application.Diagnostics;

/// <summary>
/// Copies a history value into a size-limited form for issue reports.
/// Reports must not recursively serialize live actor or camera objects, whose
/// relationships can expand into very large graphs. Capturing now also keeps
/// later edits from changing the recorded before/after values.
/// </summary>
public static class DiagnosticValue
{
    /// <summary>How deep records and collections are followed.</summary>
    public const int MaxDepth = 4;

    /// <summary>Items kept from one collection.</summary>
    public const int MaxItems = 32;

    /// <summary>Nodes one recorded value may produce in total.</summary>
    public const int MaxNodes = 256;

    /// <summary>Characters kept from one string.</summary>
    public const int MaxText = 512;

    public static object? From(object? value, Func<string, string> scrub)
    {
        int budget = MaxNodes;
        return Project(value, scrub, 0, ref budget);
    }

    private static object? Project(object? value, Func<string, string> scrub, int depth, ref int budget)
    {
        if (value is null)
            return null;
        if (--budget < 0)
            return "…";
        switch (value)
        {
            case string text:
                text = scrub(text);
                return text.Length <= MaxText ? text : text[..MaxText] + "…";
            case bool or byte or sbyte or short or ushort or int or uint or long or ulong
                or float or double or decimal or char or Guid or DateTime or TimeSpan:
                return value;
            case Enum:
                return value.ToString();
            case Vector2 or Vector3 or Vector4 or Quaternion:
                return value;
        }

        var type = value.GetType();
        if (depth >= MaxDepth)
            return $"<{type.Name}>";

        if (value is IEnumerable sequence)
        {
            var items = new List<object?>();
            foreach (var item in sequence)
            {
                if (items.Count == MaxItems)
                {
                    items.Add("…");
                    break;
                }
                items.Add(Project(item, scrub, depth + 1, ref budget));
                if (budget < 0)
                    break;
            }
            return items;
        }

        // Only inspect members of structs and records. Other objects are
        // represented by their type name; this avoids traversing native wrappers.
        if (!IsPlainData(type))
            return $"<{type.Name}>";
        var members = new SortedDictionary<string, object?>(StringComparer.Ordinal);
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetIndexParameters().Length > 0 || property.Name == "EqualityContract")
                continue;
            object? member;
            try
            {
                member = property.GetValue(value);
            }
            catch (Exception)
            {
                member = "<unreadable>";
            }
            members[property.Name] = Project(member, scrub, depth + 1, ref budget);
            if (budget < 0)
                break;
        }
        return members;
    }

    /// <summary>Allows structs and record-like classes for property inspection.
    /// This heuristic does not prove that their getters are free of side effects.</summary>
    private static bool IsPlainData(Type type) =>
        type.IsValueType
        || type.GetProperty("EqualityContract", BindingFlags.NonPublic | BindingFlags.Instance) is not null;
}
