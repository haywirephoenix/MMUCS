using System.IO;
using Godot;

public static class ImageUtils
{
    public static Texture2D LoadExternalImage(string absolutePath)
    {
        byte[] bytes = File.ReadAllBytes(absolutePath);
    
        var image = new Image();
        var error = image.LoadPngFromBuffer(bytes); 
    
        if (error != Error.Ok) return null;

        return ImageTexture.CreateFromImage(image);
    }
}