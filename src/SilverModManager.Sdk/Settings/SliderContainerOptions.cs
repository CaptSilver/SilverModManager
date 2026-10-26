using JetBrains.Annotations;

namespace SilverModManager.Sdk.Settings;

/// <summary>
/// A numeric setting edited with a slider between two bounds.
/// </summary>
[PublicAPI]
public class SliderContainerOptions : IContainerOptions
{
    public double Minimum { get; }
    public double Maximum { get; }
    public double TickFrequency { get; }
    public Func<double, string> ValueToDisplayString { get; }

    public SliderContainerOptions(double minimum, double maximum, double tickFrequency, Func<double, string>? valueToDisplayString = null)
    {
        if (!(minimum < maximum)) throw new ArgumentException($"Minimum {minimum} must be below maximum {maximum}", nameof(minimum));
        if (!(tickFrequency > 0)) throw new ArgumentOutOfRangeException(nameof(tickFrequency), tickFrequency, "The tick frequency must be positive");

        Minimum = minimum;
        Maximum = maximum;
        TickFrequency = tickFrequency;
        ValueToDisplayString = valueToDisplayString ?? (value => value.ToString("0.##"));
    }
}
