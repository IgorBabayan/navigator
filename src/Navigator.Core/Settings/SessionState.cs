namespace Navigator.Core.Settings;

/// <summary>Open windows and tabs, restored on startup when <see cref="StartupBehavior.RestoreSession"/> is selected.</summary>
public sealed class SessionState
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public List<WindowSession> Windows { get; set; } = [];

    /// <summary>Most recently closed first; bounded by the tab manager.</summary>
    public List<TabSession> RecentlyClosedTabs { get; set; } = [];
}

public sealed class WindowSession
{
    public double X { get; set; }

    public double Y { get; set; }

    public double Width { get; set; } = 1180;

    public double Height { get; set; } = 740;

    public bool IsMaximized { get; set; }

    public int SelectedTabIndex { get; set; }

    public List<TabSession> Tabs { get; set; } = [];
}

public sealed class TabSession
{
    /// <summary><see cref="Navigation.Location.Serialize"/> form of the primary pane location.</summary>
    public string Location { get; set; } = "home:";

    /// <summary>Second pane location when the tab is in split (dual pane) view.</summary>
    public string? SecondaryLocation { get; set; }

    public bool IsPinned { get; set; }
}

/// <summary>Tag assignments: normalized item path → tag ids.</summary>
public sealed class TagAssignments
{
    public int SchemaVersion { get; set; } = 1;

    public Dictionary<string, List<string>> Items { get; set; } = [];
}
