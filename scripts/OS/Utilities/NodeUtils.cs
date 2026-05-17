
using System;
using System.Collections.Generic;
using Godot;
public static class NodeUtils
{
    public static void QueueFreeChildren(this Node node)
    {
        foreach (Node child in node.GetChildren())
        {
            child.QueueFree();
        }
    }
    
    public static void QueueFreeChildrenRecursive(this Node node)
    {
        foreach (Node child in node.GetChildren())
        {
            child.QueueFreeChildrenRecursive();
            child.QueueFree();
        }
    }
    
    public static void QueueFreeChildrenOfType<T>(this Node node) where T : Node
    {
        foreach (Node child in node.GetChildren())
        {
            if (child is T)
                child.QueueFree();
        }
    }
    
    public static void QueueFreeChildrenWhere(this Node node, Func<Node, bool> predicate)
    {
        foreach (Node child in node.GetChildren())
        {
            if (predicate(child))
                child.QueueFree();
        }
    }
    
    public static List<T> GetChildrenOfType<T>(this Node node) where T : Node
    {
        var result = new List<T>();

        foreach (Node child in node.GetChildren())
        {
            if (child is T typed)
                result.Add(typed);
        }

        return result;
    }
    
    public static T GetAndLogPath<T>(this Node searchContext, string path, Node scriptRoot) where T : Node
    {
        var node = searchContext.GetNode<T>(path);

        if (node != null)
        {
            // GD.Print($"{typeof(T).Name} full path: \"{scriptRoot.GetPathTo(node)}\"");
            
            GD.Print($"private static readonly NodePath Path{node.Name} = \"{scriptRoot.GetPathTo(node)}\";");
        }
        else
        {
            GD.PrintErr($"Warning: Could not find {typeof(T).Name} at path: {path} relative to {searchContext.Name}");
        }

        return node;
    }
    public static T GetAndLogPath<T>(this Node searchContext, string path) where T : Node
    {
        var node = searchContext.GetNode<T>(path);

        if (node != null)
        {
            // GD.Print($"{typeof(T).Name} full path: \"{searchContext.GetPathTo(node)}\"");
            
            GD.Print($"private static readonly NodePath Path{node.Name} = \"{searchContext.GetPathTo(node)}\";");
        }
        else
        {
            GD.PrintErr($"Warning: Could not find {typeof(T).Name} at path: {path} relative to {searchContext.Name}");
        }

        return node;
    }

    public static void LogNodePath(this Node root, Node node)
    {
        GD.Print($"private static readonly NodePath Path{node.Name} = \"{root.GetPathTo(node)}\";");
    }
}