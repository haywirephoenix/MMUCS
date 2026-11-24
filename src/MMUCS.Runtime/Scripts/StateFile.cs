using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;

namespace MMUCS.Runtime.Scripts;

/// <summary>
/// Writes a graph of the engine's state to a file and reads it back, as <see cref="StateGraph"/> copies it in
/// memory: the same objects are written (the runtime's own objects, arrays and the collections holding them,
/// field by field), so nothing is written type by type. What <see cref="StateGraph"/> shares instead of copying
/// goes in by name:
/// <list type="bullet">
/// <item>the game's resources (<see cref="ScriptCode"/>, <see cref="ObjectCode"/>, <see cref="Costume"/>) by
/// kind, room and number, and found again in the live engine's <see cref="ScriptStore"/>;</item>
/// <item>the host's services (the script store, the sound players, the engine thread) by
/// type only: a read takes the live engine's own;</item>
/// <item>records made of plain values (a music cue, the music's save) are written out in full.</item>
/// </list>
/// Fields are matched by name, so a newer runtime still reads an older file: a field the file has that the type
/// no longer has is skipped, and one it lacks is left empty. Dictionaries and sets are written as their entries,
/// since their buckets hang on hash codes that differ from run to run.
/// <para>
/// A read gives a graph like <see cref="StateGraph.Clone{T}"/>'s, to put back with <see cref="ScriptVm.LoadState"/>.
/// </para>
/// </summary>
public static class StateFile
{
    private const string Magic = "MMUCS-STATE";
    private const int Version = 1;

    private enum Tag : byte { Null, Ref, Primitive, Enum, String, Struct, Script, Object, Costume, Service, Comparer }

    private enum Kind : byte { Fields, Array, Dictionary, Set }

    private static readonly ConcurrentDictionary<Type, FieldInfo[]> Fields = new();
    private static readonly ConcurrentDictionary<Type, bool> Data = new();
    private static readonly ConcurrentDictionary<string, Type> Types = new();

    /// <summary>What was written: how many objects, and the services left to the reader's engine, by type.</summary>
    public sealed record Summary(int Objects, long Bytes, IReadOnlyDictionary<string, int> ObjectsByType, IReadOnlyDictionary<string, int> Services);

    /// <summary>Writes <paramref name="root"/> and everything of the state it reaches.</summary>
    public static Summary Write(Stream stream, object root)
    {
        var w = new Writer();
        w.Run(root);
        long start = stream.CanSeek ? stream.Position : 0;
        using var output = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        output.Write(Magic);
        output.Write(Version);
        output.Write(w.TypeList.Count);
        foreach (var t in w.TypeList)
        {
            output.Write(TypeName(t));
            var kind = KindOf(t);
            output.Write((byte)kind);
            if (kind != Kind.Fields) continue;
            var fields = FieldsOf(t);
            output.Write(fields.Length);
            foreach (var f in fields) output.Write(FieldKey(f, fields));
        }
        output.Write(w.Count);
        output.Write(w.Headers.ToArray());
        output.Write(w.Contents.ToArray());
        output.Flush();
        long bytes = stream.CanSeek ? stream.Position - start : 0;
        return new Summary(w.Count, bytes,
            w.ByType.OrderByDescending(x => x.Value).ToDictionary(x => TypeName(x.Key), x => x.Value),
            w.Services.ToDictionary(x => TypeName(x.Key), x => x.Value));
    }

    /// <summary>
    /// Reads a graph written by <see cref="Write"/>, finding the resources in <paramref name="live"/>'s script store
    /// and taking its services. What couldn't be found (a script gone from the game) is null and goes in
    /// <paramref name="missing"/>.
    /// </summary>
    public static object Read(Stream stream, ScriptVm live, List<string> missing = null)
    {
        ArgumentNullException.ThrowIfNull(live);
        using var input = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        if (input.ReadString() != Magic) throw new InvalidDataException("not an MMUCS state file");
        int version = input.ReadInt32();
        if (version > Version) throw new InvalidDataException($"state file version {version} is newer than this MMUCS reads ({Version})");
        var r = new Reader(input, live, missing ?? []);
        return r.Run();
    }

    // ---- the writer ----

    private sealed class Writer
    {
        public readonly List<Type> TypeList = [];
        private readonly Dictionary<Type, int> _typeIds = [];
        private readonly Dictionary<object, int> _ids = new(ReferenceEqualityComparer.Instance);
        private readonly Queue<object> _queue = new();
        public readonly MemoryStream Headers = new(), Contents = new();
        private readonly BinaryWriter _h, _c;
        public readonly Dictionary<Type, int> ByType = [];
        public readonly Dictionary<Type, int> Services = [];
        public int Count;

        public Writer()
        {
            _h = new BinaryWriter(Headers);
            _c = new BinaryWriter(Contents);
        }

        public void Run(object root)
        {
            Value(root);
            while (_queue.Count > 0) Body(_queue.Dequeue());
            _h.Flush();
            _c.Flush();
        }

        private int TypeId(Type t)
        {
            if (_typeIds.TryGetValue(t, out int id)) return id;
            _typeIds[t] = id = TypeList.Count;
            TypeList.Add(t);
            return id;
        }

        private void Value(object value)
        {
            if (value == null) { _c.Write((byte)Tag.Null); return; }
            var t = value.GetType();
            if (t.IsEnum)
            {
                _c.Write((byte)Tag.Enum);
                _c.Write(TypeId(t));
                _c.Write(Convert.ToInt64(value));
                return;
            }
            if (t.IsPrimitive)
            {
                _c.Write((byte)Tag.Primitive);
                WritePrimitive(_c, value);
                return;
            }
            if (value is string s)
            {
                _c.Write((byte)Tag.String);
                _c.Write(s);
                return;
            }
            if (t.IsValueType)
            {
                _c.Write((byte)Tag.Struct);
                _c.Write(TypeId(t));
                foreach (var f in FieldsOf(t)) Value(f.GetValue(value));
                return;
            }
            switch (value)
            {
                case ScriptCode script:
                    _c.Write((byte)Tag.Script);
                    _c.Write((byte)script.Kind);
                    _c.Write(script.Room);
                    _c.Write(script.Number);
                    _c.Write(script.Entry);
                    return;
                case ObjectCode code:
                    _c.Write((byte)Tag.Object);
                    _c.Write(code.Room);
                    _c.Write(code.Id);
                    return;
                case Costume costume:
                    _c.Write((byte)Tag.Costume);
                    _c.Write(costume.Number);
                    return;
            }
            if (IsComparer(t))
            {
                _c.Write((byte)Tag.Comparer);
                _c.Write(TypeId(t));
                return;
            }
            if (IsService(t))
            {
                _c.Write((byte)Tag.Service);
                _c.Write(TypeId(t));
                Services[t] = Services.GetValueOrDefault(t) + 1;
                return;
            }
            if (!_ids.TryGetValue(value, out int id))
            {
                _ids[value] = id = Count++;
                _h.Write(TypeId(t));
                if (value is Array a)
                {
                    if (a.Rank != 1) throw new NotSupportedException($"saving a {a.Rank}-dimensional {t.GetElementType()} array");
                    _h.Write(a.Length);
                }
                ByType[t] = ByType.GetValueOrDefault(t) + 1;
                _queue.Enqueue(value);
            }
            _c.Write((byte)Tag.Ref);
            _c.Write(id);
        }

        private void Body(object value)
        {
            var t = value.GetType();
            switch (KindOf(t))
            {
                case Kind.Array:
                {
                    var a = (Array)value;
                    if (t.GetElementType().IsPrimitive)
                    {
                        var bytes = new byte[Buffer.ByteLength(a)];
                        Buffer.BlockCopy(a, 0, bytes, 0, bytes.Length);
                        _c.Write(bytes);
                    }
                    else for (int i = 0; i < a.Length; i++) Value(a.GetValue(i));
                    return;
                }
                case Kind.Dictionary:
                {
                    var d = (IDictionary)value;
                    CheckDefaultComparer(t, t.GetProperty("Comparer").GetValue(value));
                    _c.Write(d.Count);
                    foreach (DictionaryEntry e in d)
                    {
                        Value(e.Key);
                        Value(e.Value);
                    }
                    return;
                }
                case Kind.Set:
                {
                    CheckDefaultComparer(t, t.GetProperty("Comparer").GetValue(value));
                    var items = ((IEnumerable)value).Cast<object>().ToList();
                    _c.Write(items.Count);
                    foreach (var item in items) Value(item);
                    return;
                }
                default:
                    foreach (var f in FieldsOf(t)) Value(f.GetValue(value));
                    return;
            }
        }

        private static void CheckDefaultComparer(Type t, object comparer)
        {
            var key = t.GetGenericArguments()[0];
            var standard = typeof(EqualityComparer<>).MakeGenericType(key).GetProperty("Default").GetValue(null);
            if (!Equals(comparer, standard)) throw new NotSupportedException($"saving a {TypeName(t)} with its own comparer ({comparer?.GetType()})");
        }
    }

    // ---- the reader ----

    private sealed class Reader(BinaryReader input, ScriptVm live, List<string> missing)
    {
        private Type[] _types;
        private (FieldInfo Field, bool Known)[][] _fields;
        private FieldInfo[][] _absent; // the type's fields the file doesn't have (a save from before they were added)
        private Kind[] _kinds;
        private object[] _objects;
        private readonly List<(object Collection, List<(object, object)> Entries)> _collections = [];
        private readonly Dictionary<(int, int, int), ScriptCode> _objectScripts = [];
        private Dictionary<Type, object> _services;

        public object Run()
        {
            int typeCount = input.ReadInt32();
            _types = new Type[typeCount];
            _fields = new (FieldInfo, bool)[typeCount][];
            _kinds = new Kind[typeCount];
            _absent = new FieldInfo[typeCount][];
            for (int i = 0; i < typeCount; i++)
            {
                string name = input.ReadString();
                var kind = (Kind)input.ReadByte();
                var type = FindType(name) ?? throw new InvalidDataException($"the state file has a {name}, which this MMUCS doesn't have");
                _types[i] = type;
                _kinds[i] = kind;
                if (kind != Kind.Fields) continue;
                var current = FieldsOf(type);
                var byKey = current.ToDictionary(f => FieldKey(f, current));
                int n = input.ReadInt32();
                _fields[i] = new (FieldInfo, bool)[n];
                for (int j = 0; j < n; j++)
                {
                    string key = input.ReadString();
                    _fields[i][j] = byKey.TryGetValue(key, out var f) ? (f, true) : (null, false);
                }
                var inFile = _fields[i].Where(x => x.Known).Select(x => x.Field).ToHashSet();
                _absent[i] = [.. current.Where(f => !inFile.Contains(f))];
            }

            int count = input.ReadInt32();
            _objects = new object[count];
            var typeOf = new int[count];
            for (int i = 0; i < count; i++)
            {
                int typeId = input.ReadInt32();
                typeOf[i] = typeId;
                var type = _types[typeId];
                _objects[i] = _kinds[typeId] switch
                {
                    Kind.Array => Array.CreateInstance(type.GetElementType(), input.ReadInt32()),
                    Kind.Dictionary or Kind.Set => Activator.CreateInstance(type),
                    _ => RuntimeHelpers.GetUninitializedObject(type),
                };
            }

            var root = Value();
            for (int i = 0; i < count; i++) Body(_objects[i], typeOf[i]);
            // The dictionaries and sets go last, once the keys in them are filled in (their hash codes may hang on them).
            for (int i = _collections.Count - 1; i >= 0; i--)
            {
                var (collection, entries) = _collections[i];
                if (collection is IDictionary d) foreach (var (k, v) in entries) d[k] = v;
                else
                {
                    var add = collection.GetType().GetMethod("Add");
                    foreach (var (k, _) in entries) add.Invoke(collection, [k]);
                }
            }
            return root;
        }

        private void Body(object target, int typeId)
        {
            var type = _types[typeId];
            switch (_kinds[typeId])
            {
                case Kind.Array:
                {
                    var a = (Array)target;
                    var element = type.GetElementType();
                    if (element.IsPrimitive)
                    {
                        var bytes = input.ReadBytes(Buffer.ByteLength(a));
                        Buffer.BlockCopy(bytes, 0, a, 0, bytes.Length);
                    }
                    else for (int i = 0; i < a.Length; i++) Set(() => a.SetValue(Value(), i));
                    return;
                }
                case Kind.Dictionary:
                case Kind.Set:
                {
                    bool pairs = _kinds[typeId] == Kind.Dictionary;
                    int n = input.ReadInt32();
                    var entries = new List<(object, object)>(n);
                    for (int i = 0; i < n; i++)
                    {
                        var k = Value();
                        entries.Add((k, pairs ? Value() : null));
                    }
                    _collections.Add((target, entries));
                    return;
                }
                default:
                    Fill(target, typeId);
                    return;
            }
        }

        private void Fill(object target, int typeId)
        {
            foreach (var (field, known) in _fields[typeId])
            {
                var value = Value();
                if (known) Set(() => field.SetValue(target, value));
            }
            foreach (var field in _absent[typeId]) Set(() => field.SetValue(target, Fresh(field)));
        }

        /// <summary>
        /// A field an older file doesn't have starts as a new object of its type would have it: from the type's own
        /// parameterless constructor when it has one, otherwise an empty collection for a collection (such as a list
        /// added since), and left as it is for the rest.
        /// </summary>
        private object Fresh(FieldInfo field)
        {
            var owner = field.DeclaringType;
            if (!_fresh.TryGetValue(owner, out var made))
            {
                try { made = owner.IsAbstract ? null : Activator.CreateInstance(owner, nonPublic: true); }
                catch (Exception ex) when (ex is MissingMethodException or TargetInvocationException or MemberAccessException) { made = null; }
                _fresh[owner] = made;
            }
            if (made != null) return field.GetValue(made);
            var t = field.FieldType;
            if (t.IsArray) return Array.CreateInstance(t.GetElementType(), 0);
            if (!t.IsValueType && !t.IsAbstract && !t.IsInterface && t.Namespace == "System.Collections.Generic" && t.GetConstructor(Type.EmptyTypes) != null)
                return Activator.CreateInstance(t);
            return field.FieldType.IsValueType ? Activator.CreateInstance(field.FieldType) : null;
        }

        private readonly Dictionary<Type, object> _fresh = [];

        /// <summary>A value that no longer fits where it goes (the field's type changed) is left out.</summary>
        private void Set(Action set)
        {
            try { set(); }
            catch (ArgumentException ex) { missing.Add(ex.Message); }
        }

        private object Value()
        {
            var tag = (Tag)input.ReadByte();
            switch (tag)
            {
                case Tag.Null: return null;
                case Tag.Ref: return _objects[input.ReadInt32()];
                case Tag.Primitive: return ReadPrimitive(input);
                case Tag.Enum:
                {
                    var t = _types[input.ReadInt32()];
                    return Enum.ToObject(t, input.ReadInt64());
                }
                case Tag.String: return input.ReadString();
                case Tag.Struct:
                {
                    int typeId = input.ReadInt32();
                    var box = RuntimeHelpers.GetUninitializedObject(_types[typeId]);
                    Fill(box, typeId);
                    return box;
                }
                case Tag.Script:
                {
                    var kind = (ScriptKind)input.ReadByte();
                    int room = input.ReadInt32(), number = input.ReadInt32(), entry = input.ReadInt32();
                    return Script(kind, room, number, entry);
                }
                case Tag.Object:
                {
                    int room = input.ReadInt32(), id = input.ReadInt32();
                    var code = live.Scripts.Object(room, id);
                    if (code == null) missing.Add($"room {room} object {id}'s code");
                    return code;
                }
                case Tag.Costume:
                {
                    int number = input.ReadInt32();
                    var costume = live.Scripts.Costume(number);
                    if (costume == null) missing.Add($"costume {number}");
                    return costume;
                }
                case Tag.Comparer: return DefaultComparer(_types[input.ReadInt32()]);
                case Tag.Service:
                {
                    var t = _types[input.ReadInt32()];
                    _services ??= LiveServices(live);
                    if (_services.TryGetValue(t, out var service)) return service;
                    missing.Add($"the engine's {TypeName(t)}");
                    return null;
                }
                default: throw new InvalidDataException($"state file: unknown value tag {tag}");
            }
        }

        private ScriptCode Script(ScriptKind kind, int room, int number, int entry)
        {
            var store = live.Scripts;
            ScriptCode script = kind switch
            {
                ScriptKind.Global => store.Global(number),
                ScriptKind.Local => store.Local(room, number),
                ScriptKind.RoomEntry => store.RoomEntry(room),
                ScriptKind.RoomExit => store.RoomExit(room),
                _ => ObjectScript(room, number, entry),
            };
            if (script == null) missing.Add($"{kind} script {number} in room {room}");
            return script;
        }

        /// <summary>An object's verb code is made afresh each time it's asked for, so one entry is made once and shared.</summary>
        private ScriptCode ObjectScript(int room, int id, int entry)
        {
            if (_objectScripts.TryGetValue((room, id, entry), out var made)) return made;
            if (live.Scripts.Object(room, id) is not { } code) return null;
            return _objectScripts[(room, id, entry)] = new ScriptCode
            {
                Kind = ScriptKind.Object, Number = id, Room = room, Code = code.Code, Entry = entry, Name = code.Name,
            };
        }
    }

    /// <summary>The services the live engine holds, by type (a type held by more than one object can't be told apart, so it's left out).</summary>
    private static Dictionary<Type, object> LiveServices(ScriptVm live)
    {
        var found = new Dictionary<Type, object>();
        var ambiguous = new HashSet<Type>();
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var stack = new Stack<object>();
        stack.Push(live);
        while (stack.Count > 0)
        {
            var value = stack.Pop();
            if (value == null) continue;
            var t = value.GetType();
            if (t.IsPrimitive || t.IsEnum || value is string or ScriptCode or ObjectCode or Costume) continue;
            if (!t.IsValueType)
            {
                if (!seen.Add(value)) continue;
                if (IsService(t))
                {
                    if (found.TryGetValue(t, out var other) && !ReferenceEquals(other, value)) ambiguous.Add(t);
                    found[t] = value;
                    continue;
                }
            }
            if (value is Array a)
            {
                if (!t.GetElementType().IsPrimitive) foreach (var item in a) stack.Push(item);
                continue;
            }
            if (value is IDictionary d)
            {
                foreach (DictionaryEntry e in d) { stack.Push(e.Key); stack.Push(e.Value); }
                continue;
            }
            foreach (var f in FieldsOf(t)) stack.Push(f.GetValue(value));
        }
        foreach (var t in ambiguous) found.Remove(t);
        return found;
    }

    // ---- types and fields ----

    private static Kind KindOf(Type t) =>
        t.IsArray ? Kind.Array
        : IsGeneric(t, typeof(Dictionary<,>)) ? Kind.Dictionary
        : IsGeneric(t, typeof(HashSet<>)) ? Kind.Set
        : Kind.Fields;

    private static bool IsGeneric(Type t, Type definition) => t.IsGenericType && t.GetGenericTypeDefinition() == definition;

    /// <summary>
    /// Whether a value is the engine's own (a service the reader's engine supplies) rather than state to write:
    /// what <see cref="StateGraph"/> shares, except plain records, comparers and the random numbers, whose place
    /// in their sequence a save keeps so a loaded game goes on as the saved one would have.
    /// </summary>
    private static bool IsService(Type t) =>
        StateGraph.IsShared(t) && !IsData(t) && !IsComparer(t) && t != typeof(Random) && t.DeclaringType != typeof(Random);

    private static bool IsComparer(Type t) => t.Namespace == "System.Collections.Generic" && t.Name.Contains("Comparer");

    private static object DefaultComparer(Type t)
    {
        foreach (var arg in t.IsGenericType ? t.GetGenericArguments() : [])
            foreach (var family in new[] { typeof(EqualityComparer<>), typeof(Comparer<>) })
                if (family.MakeGenericType(arg).GetProperty("Default").GetValue(null) is { } standard && standard.GetType() == t) return standard;
        throw new InvalidDataException($"state file: can't make a {TypeName(t)}");
    }

    /// <summary>
    /// A record made of plain values (numbers, strings, arrays and records of them), such as a music cue: written
    /// out in full even where <see cref="StateGraph"/> shares it as it is.
    /// </summary>
    private static bool IsData(Type type) => Data.GetOrAdd(type, static t => IsData(t, []));

    private static bool IsData(Type t, HashSet<Type> visiting)
    {
        if (t.IsPrimitive || t.IsEnum || t == typeof(string)) return true;
        if (t.IsArray) return IsData(t.GetElementType(), visiting);
        if (Nullable.GetUnderlyingType(t) is { } inner) return IsData(inner, visiting);
        if (!t.IsValueType && t.GetMethod("<Clone>$") == null) return false;
        if (!visiting.Add(t)) return true;
        foreach (var f in FieldsOf(t))
            if (!IsData(f.FieldType, visiting)) return false;
        return true;
    }

    /// <summary>Every instance field of the type and its base types that holds state (not delegates or <see cref="NonSerializedAttribute"/> ones).</summary>
    private static FieldInfo[] FieldsOf(Type type) => Fields.GetOrAdd(type, static t =>
    {
        var list = new List<FieldInfo>();
        for (var at = t; at != null && at != typeof(object) && at != typeof(ValueType); at = at.BaseType)
            foreach (var f in at.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                if (!f.IsDefined(typeof(NonSerializedAttribute), inherit: false) && !typeof(Delegate).IsAssignableFrom(f.FieldType))
                    list.Add(f);
        return [.. list];
    });

    /// <summary>A field's name, with the type declaring it when a base type has a field of the same name.</summary>
    private static string FieldKey(FieldInfo f, FieldInfo[] all) =>
        all.Count(x => x.Name == f.Name) > 1 ? $"{f.DeclaringType.Name}.{f.Name}" : f.Name;

    /// <summary>A type's name without assembly versions, so a file outlives an update of .NET or MMUCS.</summary>
    private static string TypeName(Type t)
    {
        if (t.IsArray) return TypeName(t.GetElementType()) + "[]";
        if (!t.IsGenericType) return t.FullName;
        var definition = t.GetGenericTypeDefinition().FullName;
        return $"{definition}{{{string.Join(",", t.GetGenericArguments().Select(TypeName))}}}"; // braces: compiler-made names have <>
    }

    private static Type FindType(string name) => Types.GetOrAdd(name, static n => ParseType(n, out int end) is { } t && end == n.Length ? t : null);

    private static Type ParseType(string s, out int end)
    {
        int i = 0;
        while (i < s.Length && s[i] is not ('{' or ',' or '}' or '[')) i++;
        var head = s[..i];
        Type type;
        if (i < s.Length && s[i] == '{')
        {
            var args = new List<Type>();
            int at = i + 1;
            while (true)
            {
                var arg = ParseType(s[at..], out int used);
                if (arg == null) { end = 0; return null; }
                args.Add(arg);
                at += used;
                if (s[at] == '}') { at++; break; }
                at++; // ','
            }
            var definition = Lookup(head);
            type = definition?.MakeGenericType([.. args]);
            i = at;
        }
        else type = Lookup(head);
        while (type != null && i + 1 < s.Length && s[i] == '[' && s[i + 1] == ']')
        {
            type = type.MakeArrayType();
            i += 2;
        }
        end = i;
        return type;
    }

    private static Type Lookup(string fullName)
    {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            if (assembly.GetType(fullName) is { } t) return t;
        return null;
    }

    private static void WritePrimitive(BinaryWriter w, object value)
    {
        var code = Type.GetTypeCode(value.GetType());
        w.Write((byte)code);
        switch (code)
        {
            case TypeCode.Boolean: w.Write((bool)value); break;
            case TypeCode.Char: w.Write((ushort)(char)value); break;
            case TypeCode.SByte: w.Write((sbyte)value); break;
            case TypeCode.Byte: w.Write((byte)value); break;
            case TypeCode.Int16: w.Write((short)value); break;
            case TypeCode.UInt16: w.Write((ushort)value); break;
            case TypeCode.Int32: w.Write((int)value); break;
            case TypeCode.UInt32: w.Write((uint)value); break;
            case TypeCode.Int64: w.Write((long)value); break;
            case TypeCode.UInt64: w.Write((ulong)value); break;
            case TypeCode.Single: w.Write((float)value); break;
            case TypeCode.Double: w.Write((double)value); break;
            default: throw new NotSupportedException($"saving a {value.GetType()}");
        }
    }

    private static object ReadPrimitive(BinaryReader r) => (TypeCode)r.ReadByte() switch
    {
        TypeCode.Boolean => r.ReadBoolean(),
        TypeCode.Char => (char)r.ReadUInt16(),
        TypeCode.SByte => r.ReadSByte(),
        TypeCode.Byte => r.ReadByte(),
        TypeCode.Int16 => r.ReadInt16(),
        TypeCode.UInt16 => r.ReadUInt16(),
        TypeCode.Int32 => r.ReadInt32(),
        TypeCode.UInt32 => r.ReadUInt32(),
        TypeCode.Int64 => r.ReadInt64(),
        TypeCode.UInt64 => r.ReadUInt64(),
        TypeCode.Single => r.ReadSingle(),
        TypeCode.Double => r.ReadDouble(),
        var code => throw new InvalidDataException($"state file: unknown primitive {code}"),
    };
}
