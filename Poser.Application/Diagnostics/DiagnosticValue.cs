using System;
using System.Collections;
using System.Collections.Generic;
using System.Numerics;
using System.Reflection;

namespace Poser.Application.Diagnostics;

/// <summary>
/// Turns a recorded before/after value into a small, immutable copy the
/// issue report can serialize. Journal values are whatever a control wrote:
/// mostly numbers, vectors and Domain records, but sometimes live runtime
/// objects (a camera, an actor). Serializing those walks the native-backed
/// graph behind them; reference-loop handling only stops a cycle on the
/// current path, so a graph whose nodes are shared (bones and their parents)
/// expands combinatorially until memory runs out (#491). Copying at record
/// time also keeps the value as it was, not as the live object reads later.
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

        // Plain data is copied member by member; anything else is a live
        // object (a service, a native wrapper) and is named, never walked.
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

    /// <summary>Structs and records are value data; tuples too. Other
    /// classes are runtime objects whose graphs a report must not follow.</summary>
    private static bool IsPlainData(Type type) =>
        type.IsValueType
        || type.GetProperty("EqualityContract", BindingFlags.NonPublic | BindingFlags.Instance) is not null;
}
