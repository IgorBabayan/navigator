using Avalonia.Threading;
using Navigator.Presentation.Services;

namespace Navigator.App.Services;

internal sealed class AvaloniaDispatcher : IUiDispatcher
{
    public bool CheckAccess() => Dispatcher.UIThread.CheckAccess();

    public void Post(Action action) => Dispatcher.UIThread.Post(action);

    public Task InvokeAsync(Action action) => Dispatcher.UIThread.InvokeAsync(action).GetTask();

    public IDisposable StartTimer(TimeSpan interval, Action tick)
    {
        var timer = new DispatcherTimer(interval, DispatcherPriority.Background, (_, _) => tick());
        timer.Start();
        return new TimerHandle(timer);
    }

    private sealed class TimerHandle(DispatcherTimer timer) : IDisposable
    {
        public void Dispose() => timer.Stop();
    }
}
