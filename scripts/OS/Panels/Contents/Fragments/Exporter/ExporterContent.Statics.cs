using System;
using Godot;
using System.Collections.Generic;

public partial class ExporterContent : Control
{
    //shared
    private static readonly NodePath PathNextButton     = "ContentRoot/VBoxContainer/NavButtons/NextButton";
    private static readonly NodePath PathPrevButton     = "ContentRoot/VBoxContainer/NavButtons/PrevButton";
    private static readonly NodePath PathRestartButtonn = "ContentRoot/VBoxContainer/NavButtons/RestartButton";
    private static readonly NodePath PathStepsContainer = "ContentRoot/VBoxContainer/Steps";
    
    //start
    private static readonly NodePath PathExpBgsButton   = "ContentRoot/VBoxContainer/Steps/Step1_Choices/VBoxContainer/Content/Toggles/ExpBgsButton";
    private static readonly NodePath PathExpObjsButton  = "ContentRoot/VBoxContainer/Steps/Step1_Choices/VBoxContainer/Content/Toggles/ExpObjsButton";
    private static readonly NodePath PathExpCostsButton = "ContentRoot/VBoxContainer/Steps/Step1_Choices/VBoxContainer/Content/Toggles/ExpCostsButton";
    
    private static readonly NodePath PathPreviewContainer = "ContentRoot/VBoxContainer/Steps/Step2_Customize/VBoxContainer/Content/PreviewWrapper/PreviewContainer";

    private static void SetButtonActive(bool active, params Button[] buttons)
    {
        foreach (Button button in buttons)
            button.Disabled = !active;
    }
    private static void SetButtonVisible(bool visible, params Button[] buttons)
    {
        foreach (Button button in buttons)
            button.Visible = visible;
    }
    private static void SetButtonActiveAndVisible(bool activeVisible, params Button[] buttons)
    {
        foreach (Button button in buttons)
        {
            button.Visible  = activeVisible;
            button.Disabled = !activeVisible;
        }
    }
    
}