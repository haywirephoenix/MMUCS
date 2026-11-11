using Godot;

[Tool]
public partial class MTitleBar : Control
{
    [Export] 
    public string Title
    {
        get{ return _title; }
        set
        {
            OnTitleChanged(value);
            _title = value;
        }
    }

    private string _title;

    [Export] public Label titleLabel;
    
    private void OnTitleChanged(string title)
    {
        if(titleLabel == null) return;
        titleLabel.Text = title;
    }


}