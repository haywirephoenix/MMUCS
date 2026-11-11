using System.Threading;
using System.Threading.Tasks;
using Godot;
public class ScummExporters
{
    
    public async Task ExportAllBackgroundsAsync(ScummBlock[] rooms)
    {
        // foreach(var room in rooms)
        // {
        //     var result = await ScummBackgroundCache.GetCachedBackgroundAsync(
        //         room.Id, room.Rmhd, room.Block, cacheDisabled: false, paletteIndex: 0, CancellationToken.None);
        //     
        //     if (result.ExecutedImage != null)
        //     {
        //         result.ExecutedImage.SavePng($"user://exports/room_{room.Id}.png");
        //     }
        // }
    }
    public async Task ExportRoomObjectsAsync(ScummBlock roomBlock, string exportFolder)
    {
        var obims = roomBlock.FindChildrenRecursive(ScummTag.OBIM);
        foreach(var obim in obims)
        {
            if (ObimDecoders.TryGetObimInfo(obim, out var info))
            {
                for (int f = 0; f < info.TotalFrames; f++)
                {
                    var result = await ObimCache.GetCachedObimAsync(
                        obim, f, info.DefaultPaletteIndex, bakeImage: true, CancellationToken.None);
                
                    result.ExecutedImage?.SavePng($"{exportFolder}/obj_{obim.Offset}_frame_{f}.png");
                }
            }
        }
    }
    public async Task ExportAkosCelsAsync(AkosData akos, CelExportConfig userConfig, string targetDir)
    {
        for (int i = 0; i < akos.CelOffsets.Length; i++)
        {
            var celResult = await AkosCelCache.GetCachedCelAsync(akos, i, bakeImages: true, false, CancellationToken.None);
        
            Image finalizedExportImage = IndexedRenderer.RenderWithConfig(
                celResult.ShaderDataImage, 
                akos.ResolvedColors, 
                userConfig
            );

            if (finalizedExportImage != null)
            {
                finalizedExportImage.SavePng($"{targetDir}/cel_{i}.png");
            }
        }
    }
}