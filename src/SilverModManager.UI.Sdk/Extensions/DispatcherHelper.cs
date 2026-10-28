using Avalonia.Threading;

namespace SilverModManager.UI.Sdk;

public static class DispatcherHelper
{
    private const string Operation = "DispatcherHelper";

    // The scope ends when the delegate returns, which for an async body is its first real await:
    // after that the UI thread is free and a stall is not this delegate's.
    private static T Run<T>(Func<T> func)
    {
        using var _ = UiOperation.Enter(Operation);
        return func();
    }
    
    /// <summary>
    /// Ensures the provided action is executed on the UI thread.
    /// </summary>
    public static Task EnsureOnUIThreadAsync(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            using var _ = UiOperation.Enter(Operation);
            action();
            return Task.CompletedTask;
        }
        
        return Dispatcher.UIThread.InvokeAsync(() =>
            {
                using var _ = UiOperation.Enter(Operation);
                action();
                return Task.CompletedTask;
            }
        );
    }
    
    /// <summary>
    /// Ensures the provided function is executed on the UI thread.
    /// </summary>
    public static Task<TResult> EnsureOnUIThreadAsync<TResult>(Func<TResult> func)
    {
        if (Dispatcher.UIThread.CheckAccess())
            return Task.FromResult(Run(func));
        
        return Dispatcher.UIThread.InvokeAsync(() => Task.FromResult(Run(func)));
    }
    
    
    
    /// <summary>
    /// Ensures the provided task is executed on the UI thread.
    /// </summary>
    public static Task EnsureOnUIThreadAsync(Func<Task> task)
    {
        if (Dispatcher.UIThread.CheckAccess())
            return Run(task);
        
        return Dispatcher.UIThread.InvokeAsync(() => Run(task));
    }
    
    /// <summary>
    /// Ensures the provided async function is executed on the UI thread.
    /// </summary>
    public static Task<TResult> EnsureOnUIThreadAsync<TResult>(Func<Task<TResult>> func)
    {
        if (Dispatcher.UIThread.CheckAccess())
            return Run(func);
        
        return Dispatcher.UIThread.InvokeAsync(() => Run(func));
    }
    
    public static void EnsureOnUIThread(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            using var _ = UiOperation.Enter(Operation);
            action();
            return;
        }
        
        Dispatcher.UIThread.Invoke(() =>
        {
            using var _ = UiOperation.Enter(Operation);
            action();
        });
    }
}
