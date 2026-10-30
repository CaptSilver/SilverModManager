using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Paths;
using OneOf;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.App.UI.Pages.TextEdit;
using SilverModManager.App.UI.Pages.Wabbajack;
using SilverModManager.App.UI.WorkspaceSystem;
using SilverModManager.Sdk.Library;

namespace SilverModManager.UI.Tests;

/// <summary>
/// Page contexts are written into the saved window state, so every one has to survive the serializer the
/// window state uses. One that cannot makes the whole saved state unreadable.
/// </summary>
public class PageContextSerializationTests(IServiceProvider provider)
{
    private JsonSerializerOptions Options => provider.GetRequiredService<JsonSerializerOptions>();

    public static IEnumerable<object[]> ContextTypes()
    {
        // Make sure the assemblies that hold page contexts are loaded before scanning.
        _ = typeof(WabbajackModlistPageContext).Assembly;

        return AppDomain.CurrentDomain.GetAssemblies()
            .SelectMany(SafeGetTypes)
            .Where(t => t is { IsClass: true, IsAbstract: false } && t.IsAssignableTo(typeof(IPageFactoryContext)))
            .OrderBy(t => t.FullName, StringComparer.Ordinal)
            .Select(t => new object[] { t });
    }

    private static IEnumerable<Type> SafeGetTypes(Assembly assembly)
    {
        try { return assembly.GetTypes(); }
        catch (ReflectionTypeLoadException e) { return e.Types.OfType<Type>(); }
    }

    [Fact]
    public void EveryPageContextTypeIsFound()
        => ContextTypes().Select(a => (Type)a[0]).Should().Contain(typeof(WabbajackModlistPageContext));

    [Theory]
    [MemberData(nameof(ContextTypes))]
    public void APageContextSurvivesAJsonRoundTrip(Type contextType)
    {
        var context = (IPageFactoryContext)Sample(contextType);

        // An ephemeral context is never written itself; its stand-in is what the window state holds.
        var persisted = context.IsEphemeral ? context.GetSerializablePageData()?.Context : context;
        if (persisted is null) return;

        var json = JsonSerializer.Serialize(persisted, Options);
        var restored = JsonSerializer.Deserialize<IPageFactoryContext>(json, Options);

        restored.Should().NotBeNull();
        restored!.GetType().Should().Be(persisted.GetType());
        restored.Should().BeEquivalentTo(persisted, o => o.RespectingRuntimeTypes());
    }

    [Fact]
    public void TheWabbajackModlistContextKeepsItsLibraryFile()
    {
        var id = LibraryFileId.From(1234);
        var json = JsonSerializer.Serialize<IPageFactoryContext>(new WabbajackModlistPageContext { LibraryFileId = id }, Options);

        var restored = JsonSerializer.Deserialize<IPageFactoryContext>(json, Options);

        restored.Should().BeOfType<WabbajackModlistPageContext>().Which.LibraryFileId.Should().Be(id);
    }

    [Fact]
    public void ATextEditorContextKeepsWhichKindOfFileItHolds()
    {
        var library = new TextEditorPageContext
        {
            FileId = OneOf<LoadoutFileId, LibraryFileId>.FromT1(LibraryFileId.From(9)),
            FilePath = RelativePath.FromUnsanitizedInput("a/b.txt"),
            IsReadOnly = true,
        };

        var json = JsonSerializer.Serialize<IPageFactoryContext>(library, Options);
        var restored = JsonSerializer.Deserialize<IPageFactoryContext>(json, Options);

        restored.Should().BeOfType<TextEditorPageContext>().Which.FileId.AsT1.Should().Be(LibraryFileId.From(9));
    }

    /// <summary>
    /// Builds an instance with every settable member filled by a default value of its type, using the widest
    /// constructor when there is no parameterless one.
    /// </summary>
    private static object Sample(Type type)
    {
        var ctor = type.GetConstructors().OrderBy(c => c.GetParameters().Length).First();
        var instance = ctor.Invoke(ctor.GetParameters().Select(p => SampleValue(p.ParameterType)).ToArray());

        foreach (var property in type.GetProperties().Where(p => p is { CanWrite: true, CanRead: true }))
        {
            property.SetValue(instance, SampleValue(property.PropertyType));
        }
        return instance;
    }

    private static object? SampleValue(Type type)
    {
        if (type == typeof(string)) return "sample";
        if (type.IsArray) return Array.CreateInstance(type.GetElementType()!, 0);
        // A default OneOf holds no value at all, which no real context does; hold its first alternative.
        if (type.IsGenericType && type.Name.StartsWith("OneOf`", StringComparison.Ordinal))
            return type.GetMethod("FromT0")!.Invoke(null, [SampleValue(type.GetGenericArguments()[0])]);
        return type.IsValueType ? Activator.CreateInstance(type) : null;
    }
}
