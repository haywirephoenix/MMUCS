using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace MMUCS.Runtime.Scripts;

/// <summary>
/// Copies the engine's state as a graph of objects, for the game's saves (<c>room save-game</c>, the F1 menu's
/// snapshot): <see cref="Clone"/> takes a deep copy that keeps which objects share which (a slot the cutscene
/// stack points at is the same slot as in the slot table), and <see cref="Restore"/> puts a copy back into the
/// live objects in place, so whoever holds the engine's actors, palette or state keeps holding the live ones.
/// <para>
/// What is copied: the runtime's own objects, arrays, and the collections holding them. What is shared as it
/// is: the game's resources (scripts and their code, rooms, costumes: anything from MMUCS.Core), the sound
/// players, strings and other immutable values. Delegates (events, the host's hooks) and fields marked
/// <see cref="NonSerializedAttribute"/> are left alone: a clone has none, and a restore keeps the live ones.
/// </para>
/// </summary>
public static class StateGraph
{
    private static readonly Assembly Runtime = typeof(StateGraph).Assembly;

    /// <summary>The runtime's types that are resources or services rather than state.</summary>
    private static readonly HashSet<Type> SharedTypes =
    [
        typeof(ScriptStore), typeof(ScriptCode), typeof(ObjectCode), typeof(Costume), typeof(EngineThread),
    ];

    private static readonly ConcurrentDictionary<Type, FieldInfo[]> Fields = new();
    private static readonly ConcurrentDictionary<Type, bool> Shared = new();

    /// <summary>A deep copy of <paramref name="root"/> and everything of the state it reaches.</summary>
    public static T Clone<T>(T root) where T : class
    {
        var map = new Dictionary<object, object>(ReferenceEqualityComparer.Instance);
        return (T)Translate(root, map, restoring: false);
    }

    /// <summary>
    /// Makes <paramref name="live"/> (and what it reaches) as <paramref name="saved"/> is, reusing the live
    /// objects where the two graphs line up and copying the rest. <paramref name="saved"/> is left as it was,
    /// so it can be restored again.
    /// </summary>
    public static void Restore<T>(T saved, T live) where T : class
    {
        ArgumentNullException.ThrowIfNull(saved);
        ArgumentNullException.ThrowIfNull(live);
        var map = new Dictionary<object, object>(ReferenceEqualityComparer.Instance);
        var targets = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var pairs = new List<(object Saved, object Live)>();
        Pair(saved, live, map, targets, pairs);
        foreach (var (s, l) in pairs) CopyInto(s, l, map, restoring: true);
    }

    /// <summary>Lines the saved graph up with the live one: each saved object gets the live object in the same place.</summary>
    private static void Pair(object saved, object live, Dictionary<object, object> map, HashSet<object> targets, List<(object, object)> pairs)
    {
        var stack = new Stack<(object, object)>();
        stack.Push((saved, live));
        while (stack.Count > 0)
        {
            var (s, l) = stack.Pop();
            if (s == null || l == null) continue;
            var type = s.GetType();
            if (type != l.GetType() || IsShared(type)) continue;
            if (type.IsValueType)
            {
                foreach (var f in FieldsOf(type)) stack.Push((f.GetValue(s), f.GetValue(l)));
                continue;
            }
            if (map.ContainsKey(s) || !targets.Add(l)) continue;
            if (s is Array sa)
            {
                var la = (Array)l;
                if (sa.Rank != 1 || sa.Length != la.Length) { targets.Remove(l); continue; }
                map[s] = l;
                pairs.Add((s, l));
                if (!IsShared(type.GetElementType()))
                    for (int i = 0; i < sa.Length; i++) stack.Push((sa.GetValue(i), la.GetValue(i)));
                continue;
            }
            map[s] = l;
            pairs.Add((s, l));
            foreach (var f in FieldsOf(type)) stack.Push((f.GetValue(s), f.GetValue(l)));
        }
    }

    /// <summary>The value to put in a copy for <paramref name="value"/>: itself when shared, its copy otherwise.</summary>
    private static object Translate(object value, Dictionary<object, object> map, bool restoring)
    {
        if (value == null) return null;
        var type = value.GetType();
        if (IsShared(type)) return value;
        if (type.IsValueType)
        {
            // A boxed struct here is always a fresh box (from GetValue), so it can take the copied fields itself.
            foreach (var f in FieldsOf(type)) f.SetValue(value, Translate(f.GetValue(value), map, restoring));
            return value;
        }
        if (map.TryGetValue(value, out var done)) return done;
        object clone;
        if (value is Array array)
        {
            if (array.Rank != 1) throw new NotSupportedException($"saving a {array.Rank}-dimensional {type.GetElementType()} array");
            clone = Array.CreateInstance(type.GetElementType(), array.Length);
        }
        else clone = RuntimeHelpers.GetUninitializedObject(type);
        map[value] = clone;
        CopyInto(value, clone, map, restoring);
        return clone;
    }

    private static void CopyInto(object from, object to, Dictionary<object, object> map, bool restoring)
    {
        if (from is Array source)
        {
            var target = (Array)to;
            if (IsShared(source.GetType().GetElementType())) Array.Copy(source, target, source.Length);
            else for (int i = 0; i < source.Length; i++) target.SetValue(Translate(source.GetValue(i), map, restoring), i);
            return;
        }
        foreach (var f in FieldsOf(from.GetType()))
        {
            if (Skipped(f))
            {
                if (!restoring) f.SetValue(to, null); // a clone has none
                continue;
            }
            f.SetValue(to, Translate(f.GetValue(from), map, restoring));
        }
    }

    /// <summary>Delegates and <see cref="NonSerializedAttribute"/> fields aren't state; they stay with the live objects.</summary>
    private static bool Skipped(FieldInfo f) =>
        f.IsNotSerialized || typeof(Delegate).IsAssignableFrom(f.FieldType);

    /// <summary>Every instance field of the type and its base types; for a struct only those holding state (the rest come with the box).</summary>
    private static FieldInfo[] FieldsOf(Type type) => Fields.GetOrAdd(type, static t =>
    {
        var list = new List<FieldInfo>();
        for (var at = t; at != null && at != typeof(object) && at != typeof(ValueType); at = at.BaseType)
            list.AddRange(at.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly));
        if (t.IsValueType) list.RemoveAll(f => IsShared(f.FieldType) && !f.FieldType.IsInterface && f.FieldType != typeof(object));
        return [.. list];
    });

    /// <summary>Whether values of the type are shared rather than copied (immutable values, resources, services).</summary>
    private static bool IsShared(Type type) => Shared.GetOrAdd(type, static t =>
    {
        if (t.IsPrimitive || t.IsEnum || t.IsPointer || t == typeof(string) || t == typeof(decimal)) return true;
        if (typeof(Delegate).IsAssignableFrom(t) || typeof(MemberInfo).IsAssignableFrom(t)) return true;
        if (t.IsArray) return false;
        if (t.IsInterface || t == typeof(object)) return false; // decided by the value's own type
        if (t.Assembly == Runtime)
        {
            if (SharedTypes.Contains(t) || t.Namespace == "MMUCS.Runtime.Sound") return true;
            return t.IsValueType && !HasState(t);
        }
        if (t.IsGenericType && t.Namespace == "System" && t.Name.StartsWith("ValueTuple") || IsGeneric(t, typeof(KeyValuePair<,>)) || IsGeneric(t, typeof(Nullable<>)))
            return !HasState(t);
        if (t.Namespace == "System.Collections.Generic" && !t.Name.Contains("Comparer")) return false;
        if (t == typeof(BitArray)) return false;
        return true;
    });

    private static bool IsGeneric(Type t, Type definition) => t.IsGenericType && t.GetGenericTypeDefinition() == definition;

    /// <summary>Whether a value type holds anything that is copied (so it can't just be copied as it is).</summary>
    private static bool HasState(Type valueType)
    {
        for (var at = valueType; at != null && at != typeof(ValueType); at = at.BaseType)
            foreach (var f in at.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                var ft = f.FieldType;
                if (ft == valueType) continue;
                if (ft.IsInterface || ft == typeof(object) || !IsShared(ft)) return true;
            }
        return false;
    }
}
