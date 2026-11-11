using Godot;
using System.Collections.Generic;
using static ScummScriptDecoder;

public partial class ScriptViewerContent : Control
{
    [Export] public FloatingPanel ParentPanel;
    [Export] public ItemList      CodeListDisplay;
    [Export] public Label         HeaderLabel;
    
    private const string s_ContentRootPath    = "ContentRoot";
    private static readonly NodePath PathCodeListDisplay = s_ContentRootPath + "/CodeListDisplay";
    private static readonly NodePath PathHeaderLabel     = s_ContentRootPath + "/HeaderLabel";
    
    public void OnReady(){}
    public void OnExit(){}
    
    public void AssignNodes()
    {
        CodeListDisplay = GetNode<ItemList>(PathCodeListDisplay);
        HeaderLabel     = GetNode<Label>(PathHeaderLabel);
    }

    public void PopulateScriptView(ScummBlock selectedScriptBlock)
    {
        CodeListDisplay.Clear();
        HeaderLabel.Text = $"Script Disassembly: {selectedScriptBlock.DisplayName}";
        
            var lines = selectedScriptBlock.DisassemblyLines;

            if (lines == null || lines.Count == 0)
            {
                CodeListDisplay.AddItem("// No bytecode elements or parsing found inside this block block container.");
                return;
            }
            
            foreach (var line in lines)
            {
                                 // "0x0000002A:  actorOps::SO_COSTUME (Actor: 3, Param: 315)"
                string listEntry = $"0x{line.Address:X4}:  {line.DecodedString}";
                
                CodeListDisplay.AddItem(listEntry);
                int addedIndex = CodeListDisplay.GetItemCount() - 1;
                switch (line.Opcode)
                {
                    // actorOps
                    case 0xAC:
                        CodeListDisplay.SetItemCustomBgColor(addedIndex, new Color(0.15f, 0.3f, 0.15f)); // Greenish tint
                        break;
                    // Jumps
                    case 0x66:
                        CodeListDisplay.SetItemCustomBgColor(addedIndex, new Color(0.3f, 0.15f, 0.15f)); // Reddish tint
                        break;
                }
            }
        
        
    }
}