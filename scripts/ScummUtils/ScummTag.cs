using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

public static class ScummTag
{
    public const uint LECF = 0x4C454346;
    public const uint LFLF = 0x4C464C46;
    public const uint LOFF = 0x4C4F4646;
    public const uint VERB = 0x56455242;
    public const uint ROOM = 0x524F4F4D;
    public const uint RMHD = 0x524D4844;
    public const uint BOXD = 0x424F5844;
    public const uint BOXM = 0x424F584D;
    public const uint SCAL = 0x5343414C;
    public const uint RMSC = 0x524D5343;
    public const uint OBNA = 0x4F424E41;
    public const uint OBCD = 0x4F424344;
    public const uint RMIM = 0x524D494D;
    public const uint IMHD = 0x494D4844;
    public const uint IMAG = 0x494D4147;
    public const uint SMAP = 0x534D4150;
    public const uint BSTR = 0x42535452;
    public const uint BOMP = 0x424F4D50;
    public const uint ZPLN = 0x5A504C4E;
    public const uint CYCL = 0x4359434C;
    public const uint APAL = 0x4150414C;
    public const uint RGBS = 0x52474253;
    
    public const uint AKOS = 0x414B4F53;
    public const uint AKHD = 0x414b4844;
    public const uint AKSQ = 0x414B5351;
    public const uint AKCH = 0x414B4348;
    public const uint AKOF = 0x414B4F46;
    public const uint AKCI = 0x414B4349;
    public const uint AKCD = 0x414B4344;
    public const uint AKPL = 0x414B504C;
    public const uint AKFO = 0x414b4f46;
    
    public const uint iMUS = 0x694D5553;
    public const uint MAP  = 0x4D415020; // Includes trailing space
    public const uint FRMT = 0x46524D54;
    public const uint SOUN = 0x534F554E;
    public const uint DIGI = 0x44494749;
    public const uint TALK = 0x54414C4B;
    public const uint SCRP = 0x53435250;
    public const uint LSCR = 0x4C534352;
    public const uint GSCR = 0x47534352;
    public const uint ENCD = 0x454E4344;
    public const uint ZSTR = 0x5A535452;
    public const uint WRAP = 0x57524150;
    public const uint OFFS = 0x4F464653;
    public const uint TRNS = 0x54524E53;
    public const uint PALS = 0x50414C53;
    public const uint RMIH = 0x524D4948;
    public const uint OBIM = 0x4F42494D;
    public const uint CDHD = 0x43444844;
    public const uint NLSC = 0x4E4C5343;
    public const uint MIDI = 0x4D494449;
    public const uint COST = 0x434F5354;
    public const uint CHAR = 0x43484152;
    public const uint DROO = 0x44524F4F;
    public const uint RNAM = 0x524E414D;
    public const uint DSCR = 0x44534352;
    public const uint DCOS = 0x44434F53;
    public const uint DSOU = 0x44534F55;
    public const uint DCHR = 0x44434852;
    public const uint AARY = 0x41415259;
    public const uint MAXS = 0x4D415853;
    public const uint BMAP = 0x424D4150;
    public const uint REGN = 0x5245474e;
    public const uint STOP = 0x53544f50;
    public const uint DATA = 0x44415441;
    public const uint EXCD = 0x45584344;

    public record struct TagMeta(string TagName, string FullName, string Description);


    public static readonly Dictionary<uint, TagMeta> ScummV8Metadata = new()
    {
        {LECF, new("LECF","Main Container", "The master container for the entire game resource file.")},
        {LFLF, new("LFLF","Library File", "A block containing resources for a specific room or segment.")},
        {LOFF, new("LOFF","Local Offsets", "A directory of offsets for all resources within an LFLF block.")},
        {VERB, new("VERB","Verb Data", "Definitions for UI verbs and their associated script triggers.")},
        {BOXD, new("BOXD","Box Data", "Defines walkable polygons (boxes) for character pathfinding.")},
        {BOXM, new("BOXM","Box Matrix", "A connectivity table defining which boxes can reach each other.")},
        {SCAL, new("SCAL","Scale Table", "Defines character scaling/depth based on vertical screen position.")},
        {OBNA, new("OBNA","Object Name", "The plaintext name of an interactive object.")},
        {OBCD, new("OBCD","Object Code", "Logic, variables, and script pointers for a specific object.")},
        {RMIM, new("RMIM","Room Image", "Container for background image layers and tile data.")},
        {IMHD, new("IMHD","Image Header", "Width, height, and format metadata for an image.")},
        {IMAG, new("IMAG","Image Data", "Wrapper for the actual bitstream of an image/sprite.")},
        {SMAP, new("SMAP","Shared Map", "The bitmap data container for background images.")},
        {BSTR, new("BSTR","Bitstream", "Compressed graphical data (often RLE or Huffman).")},
        {BOMP, new("BOMP","Bitmask Map", "Transparency or hit-detection mask for an object.")},
        {ZPLN, new("ZPLN","Z-Plane", "Depth-mask layers used to handle occlusion.")},
        {CYCL, new("CYCL","Color Cycle", "Definitions for palette-shifting animation effects.")},
        {APAL, new("APAL","Applied Palette", "The hardware-level palette to be loaded for this block.")},
        
        {AKOS, new("AKOS","AKOS Costume", "V7/V8 container for character sprites and animations.")},
        {AKHD, new("AKHD","AKOS Header", "Akos Header")},
        {AKPL, new("AKPL","AKOS Palette", "Palette data specific to an AKOS costume.")},
        {RGBS, new("RGBS","RGB Sample", "Raw RGB values for true-color modes in V8.")},
        {AKSQ, new("AKSQ","AKOS Command Sequence", "The picture indices are mixed with the commands, the stream is a mix of 8 and 16 bits values. 8 bits values are always picture index. All 16 bits value with 0xC0 in the MSB are commands, the rest are picture index to which a mask of 0xFFF is applied.")},
        {AKCH, new("AKCH","AKOS Changes", "Offset table giving access to all entries, followed by all definitions. The definitions start with a mask indicating which limb are active, followed by the actual limb definitions.")},
        {AKOF, new("AKOF","AKOS Offsets", "Pointer table for components within an AKOS block.")},
        {AKCI, new("AKCI","AKOS Costume Info", "Setup data for character layers.")},
        {AKCD, new("AKCD","Frames data", "All compressed frames packed together.")},
        
        {iMUS, new("iMUS","iMUSE", "Core container for the interactive music system.")},
        {MAP,  new("MAP ","iMUSE Map", "Maps logical audio tracks to physical resources.")},
        {FRMT, new("FRMT","Audio Format", "Specifies encoding and sample rate.")},
        {SOUN, new("SOUN","Sound Resource", "Header and wrapper for sound or MIDI data.")},
        {DIGI, new("DIGI","Digital Audio", "Raw digitized audio sample data.")},
        {TALK, new("TALK","Talkie Data", "Digital speech audio for dialogue.")},
        {SCRP, new("SCRP","Script Pointer", "Entry-point table for scripts in the block.")},
        {LSCR, new("LSCR","Local Script", "Compiled bytecode for room-specific scripts.")},
        {GSCR, new("GSCR","Global Script", "Compiled bytecode for game-wide scripts.")},
        {ZSTR, new("ZSTR","Z-String", "Compressed/Encrypted string table.")},
        {WRAP, new("WRAP","Wrapper", "Structural wrapper block.")},
        {OFFS, new("OFFS","Offset Table", "Offset table for data blocks.")},
        {TRNS, new("TRNS","Transparent Color", "Defines the transparent palette index.")},
        {PALS, new("PALS","Palette Data", "Container for palette-related blocks.")},
        {RMIH, new("RMIH","Room Image Header", "Defines the number of Z-planes in the room.")},
        {OBIM, new("OBIM","Object Image", "Object image states and rendering data.")},
        {CDHD, new("CDHD","Code Header", "Object interaction bounds and relationships.")},
        {NLSC, new("NLSC","Number of Local Scripts", "Count of local scripts in the room.")},
        {MIDI, new("MIDI","MIDI Data", "Standard MIDI music data.")},
        {COST, new("COST","Costume", "Actor costume resource (limbs, sprites).")},
        {CHAR, new("CHAR","Charset", "Bitmap font resource.")},
        
        {DROO, new("DROO","Directory of Rooms", "Directory of room resources.")},
        {ROOM, new("ROOM","Room", "Primary container for room-specific data, background, and logic.")},
        {RMHD, new("RMHD","Room Header", "Defines room dimensions and internal resource counts.")},
        {RMSC, new("RMSC","Room Script", "Container for room code blocks.")},
        {RNAM, new("RNAM","Room Name", "Storage for the room name string.")},
        {ENCD, new("ENCD","Room Entry Script", "Cntains special code when a room is entered.")},
        {EXCD, new("EXCD","Room Exit Script", "Cntains special code when a room is exited.")},
        
        {DSCR, new("DSCR","Directory of Scripts", "Directory for room scripts.")},
        {DCOS, new("DCOS","Directory of Costumes", "Directory for costumes.")},
        {DSOU, new("DSOU","Directory of Sounds", "Directory for sounds.")},
        {DCHR, new("DCHR","Directory of Charsets", "Directory for charsets.")},
        {AARY, new("AARY","Array", "Array definition parameters.")},
        {MAXS, new("MAXS","Maximums", "Game engine limit definitions.")},
        {BMAP, new("BMAP","Bitmap", "Bitmap image data.")},
        
        {REGN, new("REGN","Sound Region", "")},
        {STOP, new("STOP","Sound Stop", "")},
        {DATA, new("DATA","Sound Data", "")},
    };


    public static string GetTagName(uint tag) => TryGetMeta(tag, out var meta) ? meta.TagName : "---";
    public static string GetFullName(uint tag) => TryGetMeta(tag, out var meta) ? meta.FullName : "---";
    public static string GetDescription(uint tag) => TryGetMeta(tag, out var meta) ? meta.Description : "---";

    public static bool TryGetMeta(uint tag, out TagMeta meta) => ScummV8Metadata.TryGetValue(tag, out meta);
    public static bool TryGetMeta(string tag, out TagMeta meta) => ScummV8Metadata.TryGetValue(FromString(tag), out meta);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint Read(byte[] data, int pos) => 
        ((uint)data[pos] << 24) | ((uint)data[pos + 1] << 16) | ((uint)data[pos + 2] << 8) | data[pos + 3];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint Make(char a, char b, char c, char d) => 
        ((uint)a << 24) | ((uint)b << 16) | ((uint)c << 8) | d;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint FromString(ReadOnlySpan<char> s) =>
        s.Length != 4 ? throw new ArgumentException("FourCC must be 4 chars") : ((uint)s[0] << 24) | ((uint)s[1] << 16) | ((uint)s[2] << 8) | s[3];
    
    public static string ToString(uint value) => string.Create(4, value, (span, v) => {
        span[0] = (char)(v >> 24);
        span[1] = (char)((v >> 16) & 0xFF);
        span[2] = (char)((v >> 8) & 0xFF);
        span[3] = (char)(v & 0xFF);
    });
    public static string ToString_prev(uint value) => 
        new string(new[] { (char)(value >> 24), (char)((value >> 16) & 0xFF), (char)((value >> 8) & 0xFF), (char)(value & 0xFF) });
}