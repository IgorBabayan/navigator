using CommunityToolkit.Mvvm.ComponentModel;
using Navigator.Presentation.Shell;

namespace Navigator.Presentation.SettingsPages;

/// <summary>One line of Settings → Appearance → Toolbar: a button with its visibility switch and move arrows.</summary>
public sealed class ToolbarItemRowViewModel : ObservableObject
{
    private readonly Action<ToolbarItemRowViewModel> _changed;
    private bool _isVisible;
    private bool _canMoveUp;
    private bool _canMoveDown;

    internal ToolbarItemRowViewModel(ToolbarItemInfo item, string name, string groupName, bool isVisible, Action<ToolbarItemRowViewModel> changed)
    {
        Item = item;
        Name = name;
        GroupName = groupName;
        _isVisible = isVisible;
        _changed = changed;
    }

    internal ToolbarItemInfo Item { get; }

    public string Id => Item.Id;

    public string Name { get; }

    /// <summary>"Command bar" or "View buttons": rows move only within their own group.</summary>
    public string GroupName { get; }

    public string IconKey => Item.IconKey;

    public bool IsSeparator => Item.IsSeparator;

    public bool IsVisible
    {
        get => _isVisible;
        set
        {
            if (SetProperty(ref _isVisible, value))
                _changed(this);
        }
    }

    public bool CanMoveUp
    {
        get => _canMoveUp;
        internal set => SetProperty(ref _canMoveUp, value);
    }

    public bool CanMoveDown
    {
        get => _canMoveDown;
        internal set => SetProperty(ref _canMoveDown, value);
    }
}
