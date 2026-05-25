using System;
using System.Collections.Generic;
using Godot;

public partial class ScummBlock : RefCounted
{

    // roomNames[0] = "" (room numbers are 1-based in SCUMM v8)
    // roomNames[roomNo] = name  — so the index IS the room number.
    public string FullName { get; set; }
    public string Description { get; set; }
    public string TagName { get; set; } // e.g. "RMHD", "BMAP", "AKOS"
    public uint Tag { get; set; } // e.g. "RMHD", "BMAP", "AKOS"
    public int Offset { get; set; } // absolute byte offset in file
    public int Size { get; set; } // total block size including header
    public int DataOffset { get; set; } // offset to data after block header
    // public byte[] RawData { get; set; }     // raw bytes, lazy-loaded
    public List<ScummBlock> Children { get; set; } = new();
    public ScummBlock Parent { get; set; }

    public int TagSiblingIndex { get; set; } = 0;



    public byte[] FileData;
    public int DataLength;

    public bool IsContainer => Children.Count > 0;

    public string DisplayName => $"{Tag}  @0x{Offset:X8}  ({Size} bytes)";

    // Resolved path from root for debugging
    public string FullPath
    {
        get
        {
            if (Parent == null) return TagName;
            return $"{Parent.FullPath} / {TagName}";
        }
    }

    private Dictionary<Variant, Variant> Metadata { get; set; } = new();

    public Type MetaSchema { get; set; }

    public void SetMetaSchema(Type type) => MetaSchema = type;

    public Dictionary<Variant, Variant> GetMetaDataDict() => Metadata;
    public void SetMetaDataDict(Dictionary<Variant, Variant> newdict) => Metadata = newdict;


    public void SetMetaDataItem<T>(T key, Variant value, bool propagate = false) where T : Enum
    {
        MetaSchema = typeof(T);
        // Convert the Enum key to a Variant (Godot wraps the underlying int/value)
        Variant vKey = Variant.From(key);

        Metadata[vKey] = value;

        if (propagate && Parent != null)
        {
            Parent.MetaSchema = typeof(T);
            Parent.Metadata[vKey] = value;
        }
    }

    public bool GetMetadataItem<T>(T key, out Variant variant) where T : Enum
    {
        bool found = Metadata.TryGetValue(Variant.From(key), out variant);
        if (!found) GD.PushError($"Missing metadata key {key}");
        return found;
    }

    public Variant GetMetadataItem<T>(T key) where T : Enum
    {
        bool found = Metadata.TryGetValue(Variant.From(key), out var variant);
        if (!found)
        {
            GD.PushError($"Missing metadata key {key}");
            return default;
        }
        return variant;
    }

    public ReadOnlySpan<byte> DataSpan =>
        FileData == null
            ? ReadOnlySpan<byte>.Empty
            : new ReadOnlySpan<byte>(FileData, DataOffset, DataLength);

    public ReadOnlySpan<byte> FullSpan =>
        FileData == null
            ? ReadOnlySpan<byte>.Empty
            : new ReadOnlySpan<byte>(FileData, Offset, Size);

    public ReadOnlyMemory<byte> DataMemory =>
        FileData == null
            ? ReadOnlyMemory<byte>.Empty
            : new ReadOnlyMemory<byte>(FileData, DataOffset, DataLength);

    public ReadOnlySpan<byte> Slice(int offset)
    {
        return DataSpan.Slice(offset);
    }

    public ReadOnlySpan<byte> Slice(int offset, int length)
    {
        return DataSpan.Slice(offset, length);
    }


    // Non-recursive — direct children only (keep for cases where you know the depth)
    public ScummBlock FindChild(uint tag)
    {
        foreach (var child in Children)
            if (child.Tag == tag)
                return child;
        return null;
    }
    public ScummBlock FindParent(uint tag)
    {
        var current = Parent;
        while (current != null)
        {
            if (current.Tag == tag) return current;
            current = current.Parent;
        }
        return null;
    }

    // Recursive — searches the full subtree
    public ScummBlock FindChildRecursive(uint tag)
    {
        foreach (var child in Children)
        {
            if (child.Tag == tag) return child;
            var found = child.FindChildRecursive(tag);
            if (found != null) return found;
        }
        return null;
    }
    public List<ScummBlock> FindChildrenRecursive(uint tag)
    {
        List<ScummBlock> results = new();

        foreach (var child in Children)
        {
            if (child.Tag == tag)
                results.Add(child);

            results.AddRange(child.FindChildrenRecursive(tag));
        }

        return results;
    }
    
    public int GetSiblingIndexByTag()
    {
        if (Parent == null) return 0;

        int tagIndex = 0;
        foreach (var sibling in Parent.Children)
        {
            if (sibling.Tag != Tag) continue;
            
            if (sibling.Offset == Offset) 
                return tagIndex;
                
            tagIndex++;
        }
        return -1;
    }

    public List<ScummBlock> FindSiblings(uint tag, bool includeSelf = false)
    {
        List<ScummBlock> results = new();

        if (Parent == null) return results;

        foreach (var sibling in Parent.Children)
        {
            if (sibling.Tag != tag) continue;
            if (!includeSelf && sibling.Offset == Offset) continue;

            results.Add(sibling);
        }
        return results;
    }

    public ScummBlock FindSiblingByTag(uint tag, bool includeSelf = false)
    {
        if (Parent == null) return null;

        foreach (var sibling in Parent.Children)
        {
            if (sibling.Tag != tag) continue;
            if (!includeSelf && sibling.Offset == Offset) continue;

            return sibling;
        }

        return null;
    }
}