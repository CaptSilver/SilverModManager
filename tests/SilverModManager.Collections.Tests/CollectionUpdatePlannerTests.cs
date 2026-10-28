using FluentAssertions;
using SilverModManager.Abstractions.NexusModsLibrary.Models;
using SilverModManager.Abstractions.Loadouts;

namespace SilverModManager.Collections.Tests;

public class CollectionUpdatePlannerTests
{
    private static ulong _nextId = 100;

    private static InstalledMod Installed(string file, string mod, string? spec = "spec", bool required = true) =>
        new(LoadoutItemGroupId.From(_nextId++), $"installed {file}", file, mod, spec, required);

    private static AvailableMod Available(string file, string mod, string spec = "spec", bool required = true) =>
        new(CollectionDownloadId.From(_nextId++), $"available {file}", file, mod, spec, required);

    [Fact]
    public void ModWithSameFileAndSpecIsKept()
    {
        var old = Installed("nexus:1:10", "nexus:1");
        var next = Available("nexus:1:10", "nexus:1");

        var plan = CollectionUpdatePlanner.Plan([old], [next]);

        plan.Kept.Should().ContainSingle().Which.Should().Be(new KeptMod(old, next));
        plan.Changed.Should().BeEmpty();
        plan.Added.Should().BeEmpty();
        plan.Dropped.Should().BeEmpty();
        plan.ToInstall.Should().BeEmpty("a kept mod is already installed");
    }

    [Fact]
    public void NewFileOfTheSameModIsAChange()
    {
        var old = Installed("nexus:1:10", "nexus:1");
        var next = Available("nexus:1:11", "nexus:1");

        var plan = CollectionUpdatePlanner.Plan([old], [next]);

        plan.Changed.Should().ContainSingle().Which.Should().Be(new ChangedMod(old, next));
        plan.Kept.Should().BeEmpty();
        plan.Dropped.Should().BeEmpty();
        plan.ToInstall.Should().Equal(next);
    }

    [Fact]
    public void ChangedInstallChoicesWithTheSameFileAreAChange()
    {
        var old = Installed("nexus:1:10", "nexus:1", spec: "choices A");
        var next = Available("nexus:1:10", "nexus:1", spec: "choices B");

        var plan = CollectionUpdatePlanner.Plan([old], [next]);

        plan.Changed.Should().ContainSingle().Which.Should().Be(new ChangedMod(old, next));
        plan.Kept.Should().BeEmpty();
    }

    [Fact]
    public void ModOnlyInTheNewRevisionIsAdded()
    {
        var kept = Installed("nexus:1:10", "nexus:1");
        var keptNext = Available("nexus:1:10", "nexus:1");
        var added = Available("nexus:2:20", "nexus:2");

        var plan = CollectionUpdatePlanner.Plan([kept], [keptNext, added]);

        plan.Added.Should().Equal(added);
        plan.ToInstall.Should().Equal(added);
    }

    [Fact]
    public void ModOnlyInTheOldRevisionIsDropped()
    {
        var kept = Installed("nexus:1:10", "nexus:1");
        var dropped = Installed("nexus:2:20", "nexus:2");
        var keptNext = Available("nexus:1:10", "nexus:1");

        var plan = CollectionUpdatePlanner.Plan([kept, dropped], [keptNext]);

        plan.Dropped.Should().Equal(dropped);
        plan.Kept.Should().ContainSingle();
    }

    [Fact]
    public void OptionalModTheUserNeverInstalledStaysUninstalled()
    {
        // The old revision had it as an optional mod, so there is no installed counterpart; the new one still lists it as optional.
        var optional = Available("nexus:3:30", "nexus:3", required: false);
        var required = Available("nexus:4:40", "nexus:4");

        var plan = CollectionUpdatePlanner.Plan([], [optional, required]);

        plan.Added.Should().BeEquivalentTo([optional, required]);
        plan.ToInstall.Should().Equal(required);
    }

    [Fact]
    public void OptionalModTheUserInstalledIsKeptAndStaysOptional()
    {
        var old = Installed("nexus:3:30", "nexus:3", required: false);
        var next = Available("nexus:3:30", "nexus:3", required: false);

        var plan = CollectionUpdatePlanner.Plan([old], [next]);

        plan.Kept.Should().ContainSingle();
        plan.ToInstall.Should().BeEmpty();
    }

    [Fact]
    public void ChangedOptionalModTheUserInstalledIsInstalledAgain()
    {
        var old = Installed("nexus:3:30", "nexus:3", required: false);
        var next = Available("nexus:3:31", "nexus:3", required: false);

        var plan = CollectionUpdatePlanner.Plan([old], [next]);

        plan.ToInstall.Should().Equal(next);
    }

    [Fact]
    public void OptionalModThatBecameRequiredIsInstalledWhenTheUserDidNotHaveIt()
    {
        var next = Available("nexus:3:30", "nexus:3", required: true);

        var plan = CollectionUpdatePlanner.Plan([], [next]);

        plan.ToInstall.Should().Equal(next);
    }

    [Fact]
    public void OptionalModThatBecameRequiredIsKeptWhenTheUserHadIt()
    {
        var old = Installed("nexus:3:30", "nexus:3", required: false);
        var next = Available("nexus:3:30", "nexus:3", required: true);

        var plan = CollectionUpdatePlanner.Plan([old], [next]);

        plan.Kept.Should().ContainSingle().Which.Available.IsRequired.Should().BeTrue();
        plan.ToInstall.Should().BeEmpty();
    }

    [Fact]
    public void RequiredModThatBecameOptionalIsKept()
    {
        var old = Installed("nexus:3:30", "nexus:3", required: true);
        var next = Available("nexus:3:30", "nexus:3", required: false);

        var plan = CollectionUpdatePlanner.Plan([old], [next]);

        plan.Kept.Should().ContainSingle().Which.Available.IsRequired.Should().BeFalse();
        plan.ToInstall.Should().BeEmpty();
        plan.Dropped.Should().BeEmpty();
    }

    [Fact]
    public void ModsWithTheSameFileAreMatchedOneToOne()
    {
        var oldA = Installed("md5:aa", "uri:a");
        var oldB = Installed("md5:aa", "uri:a");
        var next = Available("md5:aa", "uri:a");

        var plan = CollectionUpdatePlanner.Plan([oldA, oldB], [next]);

        plan.Kept.Should().ContainSingle().Which.Installed.Should().Be(oldA);
        plan.Dropped.Should().Equal(oldB);
    }

    [Fact]
    public void ExactMatchWinsOverTheModLevelMatch()
    {
        // Two files of one mod: the new revision swaps one and keeps the other. The kept one must not be paired with the swapped one.
        var oldKept = Installed("nexus:1:10", "nexus:1");
        var oldSwapped = Installed("nexus:1:11", "nexus:1");
        var newSwapped = Available("nexus:1:12", "nexus:1");
        var newKept = Available("nexus:1:10", "nexus:1");

        var plan = CollectionUpdatePlanner.Plan([oldKept, oldSwapped], [newSwapped, newKept]);

        plan.Kept.Should().ContainSingle().Which.Should().Be(new KeptMod(oldKept, newKept));
        plan.Changed.Should().ContainSingle().Which.Should().Be(new ChangedMod(oldSwapped, newSwapped));
    }

    [Fact]
    public void ModWhoseOldSettingsAreUnknownIsKeptWhenTheFileIsTheSame()
    {
        var old = Installed("nexus:1:10", "nexus:1", spec: null);
        var next = Available("nexus:1:10", "nexus:1", spec: "choices B");

        var plan = CollectionUpdatePlanner.Plan([old], [next]);

        plan.Kept.Should().ContainSingle();
        plan.Changed.Should().BeEmpty();
    }
}
