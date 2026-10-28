using System.Globalization;
using System.Text.Json;
using SilverModManager.Abstractions.Cli;
using SilverModManager.Sdk.Errors;
using SilverModManager.Sdk.Settings;

namespace SilverModManager.App.Commandline;

/// <summary>One setting as the command line and MCP tools see it.</summary>
public sealed record SettingEntry(SettingsConfig Owner, PropertyConfig Property)
{
    public string Key => Property.Key(Owner);
}

/// <summary>A command that was refused for a reason the person can act on.</summary>
public sealed class SettingsRefusal(string message) : UserFacingException(message)
{
    public override string Title => "Setting not changed";
}

/// <summary>
/// Looks settings up by key and turns text into values of their type. Kept apart from the verbs so the same rules can
/// serve any front end.
/// </summary>
public static class SettingsCatalog
{
    public static IReadOnlyList<SettingEntry> List(ISettingsManager settings) =>
        settings.Configs.Values
            .SelectMany(owner => owner.Properties.Select(property => new SettingEntry(owner, property)))
            .OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public static SettingEntry Find(ISettingsManager settings, string key)
    {
        var all = List(settings);
        if (all.FirstOrDefault(entry => entry.Key.Equals(key.Trim(), StringComparison.OrdinalIgnoreCase)) is { } found) return found;

        var near = all
            .Where(entry => entry.Key.Contains(key, StringComparison.OrdinalIgnoreCase)
                || key.Contains(entry.Property.Name, StringComparison.OrdinalIgnoreCase)
                || Distance(entry.Key, key) <= Math.Max(3, key.Length / 4))
            .Select(entry => entry.Key)
            .Take(5)
            .ToArray();

        throw new SettingsRefusal(near.Length == 0
            ? $"There is no setting '{key}'. Run `settings list` to see them."
            : $"There is no setting '{key}'. Did you mean: {string.Join(", ", near)}?");
    }

    /// <summary>The text shown for a value, which <see cref="Parse"/> accepts back.</summary>
    public static string Format(object value) => value switch
    {
        string text => text,
        bool flag => flag ? "true" : "false",
        Enum e => e.ToString(),
        IFormattable number => number.ToString(null, CultureInfo.InvariantCulture),
        _ => JsonSerializer.Serialize(value, CliJson.Options),
    };

    /// <summary>
    /// Reads <paramref name="text"/> as the type of the setting and checks it against the setting's own validation.
    /// </summary>
    /// <exception cref="SettingsRefusal">The text doesn't fit the type, the type can't be edited here, or validation refuses it.</exception>
    public static object Parse(SettingEntry entry, string text)
    {
        var type = entry.Property.PropertyType;
        object? value = null;
        string? problem = null;

        if (type == typeof(string)) value = text;
        else if (type == typeof(bool))
        {
            if (bool.TryParse(text, out var flag)) value = flag;
            else problem = "Use true or false.";
        }
        else if (type == typeof(int) || type == typeof(long))
        {
            if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var whole))
            {
                if (type == typeof(long)) value = whole;
                else if (whole is >= int.MinValue and <= int.MaxValue) value = (int)whole;
                else problem = "That whole number is too large.";
            }
            else problem = "Use a whole number.";
        }
        else if (type == typeof(double))
        {
            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)) value = number;
            else problem = "Use a number such as 0.5.";
        }
        else if (type.IsEnum)
        {
            if (Enum.TryParse(type, text, ignoreCase: true, out var member) && Enum.GetNames(type).Contains(member?.ToString())) value = member;
            else problem = $"Use one of: {string.Join(", ", Enum.GetNames(type))}.";
        }
        else
        {
            throw new SettingsRefusal($"{entry.Key} can't be set from the command line: it holds a {type.Name}, which only the Settings page can edit.");
        }

        if (value is null) throw new SettingsRefusal($"'{text}' isn't a valid value for {entry.Key}. {problem}");

        var validation = entry.Property.Options.Validation?.Invoke(value);
        if (validation is { } result && result.IsFailed())
            throw new SettingsRefusal($"'{text}' isn't a valid value for {entry.Key}. {result.Value.AsT0.Reason}");

        RefuseWhatThePageDoesNotOffer(entry, text, value);
        return value;
    }

    /// <summary>
    /// The Settings page only lets a person pick from a choice list or inside a slider's range, and the code reading the
    /// setting relies on that, so the same limits apply here.
    /// </summary>
    private static void RefuseWhatThePageDoesNotOffer(SettingEntry entry, string text, object value)
    {
        switch (entry.Property.ContainerOptions)
        {
            case SingleValueMultipleChoiceContainerOptions choices
                when !choices.AllowedValues.Any(allowed => choices.ValueComparer.Equals(allowed, value)):
                throw new SettingsRefusal($"'{text}' isn't a choice for {entry.Key}. Use one of: {string.Join(", ", choices.AllowedValues.Select(choices.ValueToDisplayString.Invoke).Zip(choices.AllowedValues, (display, raw) => Format(raw) == display ? display : $"{Format(raw)} ({display})"))}.");
            case SliderContainerOptions slider
                when Convert.ToDouble(value, CultureInfo.InvariantCulture) is var number && (number < slider.Minimum || number > slider.Maximum):
                throw new SettingsRefusal($"'{text}' is outside the range of {entry.Key}. Use a value between {Format(slider.Minimum)} and {Format(slider.Maximum)}.");
        }
    }

    private static int Distance(string a, string b)
    {
        a = a.ToLowerInvariant();
        b = b.ToLowerInvariant();
        var row = Enumerable.Range(0, b.Length + 1).ToArray();
        for (var i = 1; i <= a.Length; i++)
        {
            var previous = row[0];
            row[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var current = row[j];
                row[j] = Math.Min(Math.Min(row[j] + 1, row[j - 1] + 1), previous + (a[i - 1] == b[j - 1] ? 0 : 1));
                previous = current;
            }
        }

        return row[b.Length];
    }
}
