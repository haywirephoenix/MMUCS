using System;
using Godot;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

public partial class AkosViewerContent : Control
{
    [Export] public GridContainer _headerGrid;
    [Export] public GridContainer _configGrid;
    [Export] public Label         _codecLabel;
    
    private static readonly NodePath PathheaderGrid = s_tabsPath + "/Overview/OverviewContainer/headerGrid";
    private static readonly NodePath PathCodecLabel = s_tabsPath + "/Overview/OverviewContainer/CodecLabel";

    
    private void _PopulateOverview()
    {
        foreach (Node child in _headerGrid.GetChildren()) child.QueueFree();

        var h = _akos.Header;
        _AddGridRow("Version", h.VersionNumber.ToString());
        _AddGridRow("Flags", $"0x{h.CostumeFlags:X4}  ({(h.HasManyDirections ? "8-dir" : "4-dir")}{(h.MirroredCostume ? ", mirrored" : "")})");
        _AddGridRow("Chore count", h.ChoreCount.ToString());
        _AddGridRow("Cel count", h.CelsCount.ToString());
        _AddGridRow("Layer count", h.LayerCount.ToString());
        _AddGridRow("Anim count", _akos.AnimCount.ToString());
        _AddGridRow("Direction count", _akos.DirectionCount.ToString());

        _codecLabel.Text = h.Codec switch
        {
            AkosCodec.ByleRLE => "1 – Byle RLE\nUsed by SCUMM v7/v8 (COMI/DIG). Variable-length run-length encoding per scanline.",
            AkosCodec.CdatRLE => "2 – CDAT RLE (BOMP)\nBomp decompressor. Used by earlier SCUMM versions.",
            AkosCodec.MajMin => "5 – MajMin\nMajority/Minority bit-plane codec. Used by HE games.",
            AkosCodec.TRLE => "16 – TRLE\nTransparent RLE, HE95+. Supports alpha and shadow tables.",
            _ => $"Unknown codec {(int)h.Codec}",
        };
    }

    private void _AddGridRow(string key, string value)
    {
        var k = new Label();
        k.Text = key;
        k.AddThemeColorOverride("font_color", new Color("aaaaaa"));
        _headerGrid.AddChild(k);

        var v = new Label();
        v.Text = value;
        _headerGrid.AddChild(v);
    }

}