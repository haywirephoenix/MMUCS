using System;
using Godot;
using System.Collections.Generic;

public partial class ExporterContent : Control
{
    public enum EState
    {
        Start     = 0,
        Customize = 1,
        Path      = 2,
        Process   = 3,
        Complete  = 4
    }

    public EState CurrentState;
    
    [Export] public FloatingPanel    ParentPanel;
    
    [ExportGroup("Shared References")]
    [Export] public Control          StepsContainer;
    [Export] public Button           PrevButton;
    [Export] public Button           NextButton;
    [Export] public Button           RestartButton;
    
    [ExportGroup("Start")]
    [Export] public CheckButton           ExpBgsButton;
    [Export] public CheckButton           ExpObjsButton;
    [Export] public CheckButton           ExpCostsButton;
    
    

    private readonly List<Control> _stepContainers = new();
    
    public void OnReady()
    {
        GetSteps();

        PrevButton.Pressed    += OnPrevButtonPressed;
        NextButton.Pressed    += OnNextButtonPressed;
        RestartButton.Pressed += OnRestartButtonPressed;
        
        ExpBgsButton.Pressed         += ValidateStartNext;
        ExpObjsButton.Pressed        += ValidateStartNext;
        ExpCostsButton.Pressed       += ValidateStartNext;
        
        EventBus.Instance.FileParsed += OnScummblockLoaded;

        SetState(EState.Start);
    }

    private void OnScummblockLoaded(ScummBlock block)
    {
        InitCustomize();
    }

    public void OnExit()
    {
        PrevButton.Pressed    -= OnPrevButtonPressed;
        NextButton.Pressed    -= OnNextButtonPressed;
        RestartButton.Pressed -= OnRestartButtonPressed;
        
        DeInitCustomize();
    }

    public void AssignNodes()
    {
        NextButton       = GetNode<Button>(PathNextButton);
        PrevButton       = GetNode<Button>(PathPrevButton);
        RestartButton    = GetNode<Button>(PathRestartButtonn);
        StepsContainer   = GetNode<Control>(PathStepsContainer);
        ZoomableViewport = GetNode<ZoomableViewport>(PathPreviewContainer);
        
        ExpBgsButton       = GetNode<CheckButton>(PathExpBgsButton);
        ExpObjsButton      = GetNode<CheckButton>(PathExpObjsButton);
        ExpCostsButton     = GetNode<CheckButton>(PathExpCostsButton);
    }

    private void OnNextButtonPressed()
    {
        int nextIndex = GetNextIndex();
        SetState((EState)nextIndex);
    }
    private void OnPrevButtonPressed()
    {
        int prevIndex = GetPrevIndex();
        SetState((EState)prevIndex);
    }

    private void OnRestartButtonPressed()
    {
        SetState(EState.Start);
    }

    private List<Control> GetSteps()
    {
        if (StepsContainer == null) return _stepContainers;

        foreach (Node child in StepsContainer.GetChildren())
        {
            if (child is Control controlNode)
                _stepContainers.Add(controlNode);
        }
        return _stepContainers;
    }

    private int GetNextIndex() => Math.Clamp((int)CurrentState + 1, (int)EState.Start, (int)EState.Complete);
    private int GetPrevIndex() => Math.Clamp((int)CurrentState - 1, (int)EState.Start, (int)EState.Complete);

    private void SetState(EState state)
    {
        CurrentState = state;
        SetActiveScreen(state);

        switch (state)
        {
            case EState.Start: OnEnterStart(); return;
            case EState.Customize: OnEnterCustomize(); return;
            case EState.Path: OnEnterPath(); return;
            case EState.Process: OnEnterProcess(); return;
            case EState.Complete: OnEnterComplete(); return;
            default: return;
        }
    }

    private void SetActiveScreen(EState state)
    {
        for (int i = 0; i < _stepContainers.Count; i++)
            _stepContainers[i].Visible = i == (int)state;
    }

    private void OnEnterStart()
    {
        SetButtonActiveAndVisible(false, RestartButton);
        SetButtonVisible(true, NextButton, PrevButton);
        SetButtonActive(false, PrevButton, NextButton);
        ValidateStartNext();
    }
    private void ValidateStartNext()
    {
        bool isValid = ExpBgsButton.ButtonPressed || 
                       ExpObjsButton.ButtonPressed || 
                       ExpCostsButton.ButtonPressed;
                   
        SetButtonActive(isValid, NextButton);
    }

    private void OnEnterCustomize()
    {
        SetButtonActive(true, PrevButton);
        SetButtonActive(false, NextButton);
    }
    private void OnEnterPath()
    {
        SetButtonActive(true, PrevButton);
        SetButtonActive(false, NextButton);
    }
    private void OnEnterProcess()
    {
        SetButtonActive(false, NextButton, PrevButton);
    }
    private void OnEnterComplete()
    {
        SetButtonActiveAndVisible(true, RestartButton);
        SetButtonActiveAndVisible(false, NextButton, PrevButton);
    }

    


}