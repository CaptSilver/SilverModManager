using System.Linq.Expressions;
using System.Reactive.Linq;
using System.Windows.Input;
using Avalonia;
using DynamicData;
using DynamicData.Alias;
using ReactiveUI;

namespace SilverModManager.App.UI.Extensions;

public static class ReactiveExtensions
{
    /// <summary>
    /// Applies the given class to the given control when a true value is observed
    /// removes the class when a false value is observed.
    /// </summary>
    /// <param name="obs"></param>
    /// <param name="target"></param>
    /// <param name="classToApply"></param>
    /// <typeparam name="TTarget"></typeparam>
    /// <returns></returns>
    public static IDisposable BindToClasses<TTarget>(this IObservable<bool> obs, TTarget target, string classToApply)
        where TTarget : StyledElement
    {
        return obs
            .OnUI()
            .SubscribeWithErrorLogging(logger: default, val =>
            {
                if (val)
                    target.Classes.Add(classToApply);
                else
                    target.Classes.Remove(classToApply);
            });
    }

    /// <summary>
    /// Applies the given class to the given control when a true value is observed, and the false value when false is observed
    /// removes the opposite class when an opposite value is observed.
    /// </summary>
    /// <param name="obs"></param>
    /// <param name="target"></param>
    /// <param name="trueClass"></param>
    /// <param name="falseClass"></param>
    /// <typeparam name="TTarget"></typeparam>
    /// <returns></returns>
    public static IDisposable BindToClasses<TTarget>(this IObservable<bool> obs, TTarget target, string trueClass, string falseClass)
        where TTarget : StyledElement
    {
        return obs
            .OnUI()
            .SubscribeWithErrorLogging(logger: default, val =>
            {
                if (val)
                {
                    target.Classes.Remove(falseClass);
                    target.Classes.Add(trueClass);
                }
                else
                {
                    target.Classes.Remove(trueClass);
                    target.Classes.Add(falseClass);
                }
            });
    }

    /// <summary>
    /// Shorthand for applying "Active" to the given element when true is observed
    /// </summary>
    /// <param name="obs"></param>
    /// <param name="target"></param>
    /// <typeparam name="TTarget"></typeparam>
    /// <returns></returns>
    public static IDisposable BindToActive<TTarget>(this IObservable<bool> obs,
        TTarget target) where TTarget : StyledElement
    {
        return obs.BindToClasses(target, "Active");
    }


    /// <summary>
    /// Prefer this over `InvokeCommand` for better type checking.
    /// A utility method that will pipe an Observable to an ICommand (i.e.
    /// it will first call its CanExecute with the provided value, then if
    /// the command can be executed, Execute() will be called).
    /// </summary>
    /// <typeparam name="T">The type.</typeparam>
    /// <typeparam name="TResult">The result type.</typeparam>
    /// <param name="item">The source observable to pipe into the command.</param>
    /// <param name="command">The command to be executed.</param>
    /// <returns>An object that when disposes, disconnects the Observable
    /// from the command.</returns>
    public static IDisposable InvokeReactiveCommand<T, TResult>(
        this IObservable<T> item,
        ReactiveCommandBase<T, TResult>? command)
    {
        // ReSharper disable once InvokeAsExtensionMethod
        return ReactiveCommandMixins.InvokeCommand(item, command);
    }
    
    
    /// <summary>
    /// Prefer this over `InvokeCommand` for better type checking.
    /// A utility method that will pipe an Observable to an ICommand (i.e.
    /// it will first call its CanExecute with the provided value, then if
    /// the command can be executed, Execute() will be called).
    /// This will set up a subscription to the (potentially reactive) command property,
    /// and should be used when a view needs to invoke a command on a ViewModel.
    /// </summary>
    /// <typeparam name="T">The type.</typeparam>
    /// <typeparam name="TResult">The result type.</typeparam>
    /// <typeparam name="TTarget">The target type.</typeparam>
    /// <param name="item">The source observable to pipe into the command.</param>
    /// <param name="target">The root object which has the Command.</param>
    /// <param name="commandProperty">The expression to reference the Command.</param>
    /// <returns>An object that when disposes, disconnects the Observable
    /// from the command.</returns>
    public static IDisposable InvokeReactiveCommand<T, TResult, TTarget>(
        this IObservable<T> item, 
        TTarget? target, 
        Expression<Func<TTarget, ReactiveCommandBase<T, TResult>?>> commandProperty)
        where TTarget : class
    {
        return item.InvokeCommand(target, commandProperty);
    }

    /// <summary>
    /// Pipes <paramref name="item"/> into the latest command of <paramref name="command"/>, for a command that
    /// belongs to something that comes and goes (a view's view model). A value is dropped while there is no command
    /// yet or the latest command cannot execute; when the command changes, later values go to the new one.
    /// Same semantics as ReactiveUI's <c>InvokeCommand</c> over a single command, which has no overload over a stream of commands.
    /// </summary>
    /// <param name="item">The source observable to pipe into the command.</param>
    /// <param name="command">The commands to invoke, newest last.</param>
    public static IDisposable InvokeReactiveCommand<T, TResult>(
        this IObservable<T> item,
        IObservable<ReactiveCommandBase<T, TResult>?> command)
    {
        var latestCommand = command
            .Select(static current => current is null
                ? Observable.Return<(ReactiveCommandBase<T, TResult>? Command, bool CanExecute)>((null, false))
                : current.CanExecute.Select(canExecute => (Command: (ReactiveCommandBase<T, TResult>?)current, CanExecute: canExecute)))
            .Switch();

        return item
            .WithLatestFrom(latestCommand, static (value, latest) => (Value: value, latest.Command, latest.CanExecute))
            .Where(static invocation => invocation.CanExecute)
            .Subscribe(static invocation =>
            {
                if (invocation.Command is { } current)
                    current.Execute(invocation.Value).Catch(Observable.Empty<TResult>()).Subscribe();
            });
    }
        
}
