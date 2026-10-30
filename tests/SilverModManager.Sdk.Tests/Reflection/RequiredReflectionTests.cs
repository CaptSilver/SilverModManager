using System.Reflection;
using FluentAssertions;
using SilverModManager.Sdk.Reflection;

namespace SilverModManager.Sdk.Tests.Reflection;

public class RequiredReflectionTests
{
    [Fact]
    public void Test_GetRequiredMethod_NamesTypeMethodAndFlagsWhenMissing()
    {
        var act = () => typeof(string).GetRequiredMethod("NoSuchMethod", BindingFlags.Public | BindingFlags.Static);
        act.Should().Throw<MissingMethodException>().WithMessage("*System.String*NoSuchMethod*Public, Static*");
    }

    [Fact]
    public void Test_GetRequiredMethod_FindsAnExistingMethod()
    {
        typeof(string).GetRequiredMethod(nameof(string.ToUpperInvariant)).Name.Should().Be(nameof(string.ToUpperInvariant));
    }

    [Fact]
    public void Test_GetRequiredMethod_ByParameterTypes_NamesTypeMethodAndParametersWhenMissing()
    {
        var act = () => typeof(string).GetRequiredMethod(nameof(string.ToUpperInvariant), [typeof(Guid)]);
        act.Should().Throw<MissingMethodException>().WithMessage("*System.String*ToUpperInvariant*System.Guid*");
    }

    [Fact]
    public void Test_GetRequiredMethod_ByParameterTypes_FindsAnExistingMethod()
    {
        typeof(string).GetRequiredMethod("Concat", [typeof(string), typeof(string)]).Name.Should().Be("Concat");
    }

    [Fact]
    public void Test_GetRequiredConstructor_NamesTypeAndParametersWhenMissing()
    {
        var act = () => typeof(string).GetRequiredConstructor([typeof(Guid)]);
        act.Should().Throw<MissingMethodException>().WithMessage("*System.String*constructor*System.Guid*");
    }

    [Fact]
    public void Test_GetRequiredConstructor_FindsAnExistingConstructor()
    {
        typeof(InvalidOperationException).GetRequiredConstructor([typeof(string)]).GetParameters().Should().HaveCount(1);
    }

    [Fact]
    public void Test_CreateRequiredInstance_NamesTypeWhenActivatorReturnsNull()
    {
        var act = () => typeof(int?).CreateRequiredInstance();
        act.Should().Throw<InvalidOperationException>().WithMessage("*Nullable*Int32*");
    }

    [Fact]
    public void Test_CreateRequiredInstance_BuildsAnObject()
    {
        typeof(List<int>).CreateRequiredInstance<List<int>>().Should().BeEmpty();
    }

    [Fact]
    public void Test_CreateRequiredInstance_Generic_NamesTypeWhenInstanceIsNotTheRequestedType()
    {
        var act = () => typeof(List<int>).CreateRequiredInstance<string>();
        act.Should().Throw<InvalidOperationException>().WithMessage("*List*String*");
    }

    [Fact]
    public void Test_RequiredFullName_ThrowsForAnOpenGenericParameter()
    {
        var parameter = typeof(List<>).GetGenericArguments()[0];
        var act = () => parameter.RequiredFullName();
        act.Should().Throw<InvalidOperationException>().WithMessage("*T*no full name*");
    }

    [Fact]
    public void Test_RequiredFullName_ReturnsTheFullName()
    {
        typeof(string).RequiredFullName().Should().Be("System.String");
    }
}
