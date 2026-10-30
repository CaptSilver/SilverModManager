using System.Reflection;
using JetBrains.Annotations;

namespace SilverModManager.Sdk.Reflection;

/// <summary>
/// Reflection lookups that fail with a message naming what was asked for, so a renamed member is found from the
/// message and not from a NullReferenceException three frames later.
/// </summary>
[PublicAPI]
public static class RequiredReflection
{
    /// <summary>
    /// The method, or a <see cref="MissingMethodException"/> naming the type, the method and the binding flags.
    /// </summary>
    public static MethodInfo GetRequiredMethod(this Type type, string name, BindingFlags flags = BindingFlags.Public | BindingFlags.Instance)
    {
        return type.GetMethod(name, flags)
            ?? throw new MissingMethodException($"{type.FullName ?? type.Name} has no method '{name}' with binding flags {DescribeFlags(flags)}");
    }

    /// <summary>
    /// The public method taking exactly these parameter types, or a <see cref="MissingMethodException"/> naming the type,
    /// the method and the parameters.
    /// </summary>
    public static MethodInfo GetRequiredMethod(this Type type, string name, Type[] parameterTypes)
    {
        return type.GetMethod(name, parameterTypes)
            ?? throw new MissingMethodException($"{type.FullName ?? type.Name} has no method '{name}' taking ({DescribeParameters(parameterTypes)})");
    }

    /// <summary>
    /// The public constructor taking exactly these parameter types, or a <see cref="MissingMethodException"/> naming the type
    /// and the parameters.
    /// </summary>
    public static ConstructorInfo GetRequiredConstructor(this Type type, Type[] parameterTypes)
    {
        return type.GetConstructor(parameterTypes)
            ?? throw new MissingMethodException($"{type.FullName ?? type.Name} has no constructor taking ({DescribeParameters(parameterTypes)})");
    }

    /// <summary>
    /// <see cref="Activator.CreateInstance(Type)"/>, or an <see cref="InvalidOperationException"/> naming the type when it
    /// returns null (which only <see cref="Nullable{T}"/> does).
    /// </summary>
    public static object CreateRequiredInstance(this Type type)
    {
        return Activator.CreateInstance(type)
            ?? throw new InvalidOperationException($"Activator created no instance of {type.FullName ?? type.Name}");
    }

    /// <summary>
    /// <see cref="CreateRequiredInstance(Type)"/> as a <typeparamref name="T"/>, or an <see cref="InvalidOperationException"/>
    /// naming both types when the instance is something else.
    /// </summary>
    public static T CreateRequiredInstance<T>(this Type type) where T : class
    {
        return type.CreateRequiredInstance() as T
            ?? throw new InvalidOperationException($"An instance of {type.FullName ?? type.Name} is not a {typeof(T).FullName}");
    }

    /// <summary>
    /// <see cref="Type.FullName"/>, or an <see cref="InvalidOperationException"/> for the types that have none
    /// (open generic parameters, arrays of them).
    /// </summary>
    public static string RequiredFullName(this Type type)
    {
        return type.FullName
            ?? throw new InvalidOperationException($"Type {type.Name} has no full name");
    }

    private static readonly BindingFlags[] ReportedFlags = [BindingFlags.Public, BindingFlags.NonPublic, BindingFlags.Instance, BindingFlags.Static];

    private static string DescribeFlags(BindingFlags flags) => string.Join(", ", ReportedFlags.Where(f => flags.HasFlag(f)));

    private static string DescribeParameters(Type[] parameterTypes) => string.Join(", ", parameterTypes.Select(t => t.FullName ?? t.Name));
}
