using System.ComponentModel;
using Avalonia.Controls;
using Navigator.Presentation.Shell;

namespace Navigator.App.Views;

public sealed partial class TabContentView : UserControl
{
    private TabViewModel? _tab;

    public TabContentView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach(DataContext as TabViewModel);
    }

    private void Attach(TabViewModel? tab)
    {
        if (_tab is not null)
            _tab.PropertyChanged -= OnTabChanged;
        _tab = tab;
        if (tab is not null)
            tab.PropertyChanged += OnTabChanged;
        UpdateSplit();
    }

    private void OnTabChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TabViewModel.IsSplit))
            UpdateSplit();
    }

    private void UpdateSplit()
    {
        var split = _tab?.IsSplit == true;
        Root.ColumnDefinitions[1].Width = split ? new GridLength(4) : new GridLength(0);
        Root.ColumnDefinitions[2].Width = split ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        PrimaryPane.ShowHeader = split;
        SecondaryPane.ShowHeader = split;
    }
}
