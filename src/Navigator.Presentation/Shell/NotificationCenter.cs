using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using Navigator.Presentation.Services;

namespace Navigator.Presentation.Shell;

public sealed class NotificationViewModel(InAppNotification notification, Action<NotificationViewModel> dismiss)
{
    private IRelayCommand? _dismissCommand;
    private IRelayCommand? _actionCommand;

    public string Title { get; } = notification.Title;

    public string Message { get; } = notification.Message;

    public NotificationSeverity Severity { get; } = notification.Severity;

    public string? ActionText { get; } = notification.ActionText;

    public bool HasAction => notification.Action is not null;

    public IRelayCommand DismissCommand => _dismissCommand ??= new RelayCommand(() => dismiss(this));

    public IRelayCommand ActionCommand => _actionCommand ??= new RelayCommand(() =>
    {
        notification.Action?.Invoke();
        dismiss(this);
    });
}

/// <summary>In-app toast stack (bottom-right). Auto-dismisses after a few seconds; errors stay longer.</summary>
public sealed class NotificationCenter(IUiDispatcher dispatcher) : INotificationCenter
{
    private const int MaxVisible = 4;

    public ObservableCollection<NotificationViewModel> Items { get; } = [];

    public void Show(InAppNotification notification)
        => dispatcher.Post(() =>
        {
            NotificationViewModel? model = null;
            model = new NotificationViewModel(notification, m => Items.Remove(m));
            Items.Add(model);
            while (Items.Count > MaxVisible)
                Items.RemoveAt(0);

            var lifetime = notification.Severity == NotificationSeverity.Error ? TimeSpan.FromSeconds(10) : TimeSpan.FromSeconds(5);
            IDisposable? timer = null;
            timer = dispatcher.StartTimer(lifetime, () =>
            {
                timer?.Dispose();
                Items.Remove(model);
            });
        });

    public void Dismiss(NotificationViewModel model) => Items.Remove(model);
}
