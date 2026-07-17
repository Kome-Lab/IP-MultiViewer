using System.Collections.Immutable;
using System.Globalization;

namespace IPMultiViewer.Core.Audio;

public readonly record struct AudioMeterReading(
    MeterDisplayMode Mode,
    double InputDbFs,
    double EffectiveDbFs,
    double DisplayValue,
    double Ratio,
    string Unit,
    string DisplayText);

public readonly record struct AudioMeterTick(
    double DisplayValue,
    double InputDbFs,
    double Ratio,
    string Label);

public sealed record AudioMeterScaleValidation(
    int CheckedCases,
    ImmutableArray<string> Failures)
{
    public bool IsValid => Failures.IsDefaultOrEmpty;

    public void ThrowIfInvalid()
    {
        if (!IsValid)
        {
            throw new InvalidOperationException(
                $"Audio meter scale validation failed: {string.Join("; ", Failures)}");
        }
    }
}

public static class AudioMeterScale
{
    public const double VuReferenceDbFs = -18d;
    public const double VuMinimum = -60d;
    public const double VuMaximum = 3d;
    public const double PeakMinimumDbFs = -60d;
    public const double PeakMaximumDbFs = 0d;

    private const double ComparisonTolerance = 1e-9;

    private static readonly ImmutableArray<AudioMeterTick> VuTicks = CreateTicks(
        MeterDisplayMode.Vu,
        [-60d, -50d, -40d, -30d, -20d, -10d, -7d, -5d, -3d, -2d, -1d, 0d, 1d, 2d, 3d]);

    private static readonly ImmutableArray<AudioMeterTick> PeakTicks = CreateTicks(
        MeterDisplayMode.Peak,
        [-60d, -54d, -48d, -42d, -36d, -30d, -24d, -18d, -12d, -6d, -3d, 0d]);

    public static AudioMeterReading Evaluate(double inputDbFs, MeterDisplayMode mode)
    {
        var minimumInput = MinimumInputDbFs(mode);
        var maximumInput = MaximumInputDbFs(mode);
        var effectiveInput = double.IsFinite(inputDbFs)
            ? Math.Clamp(inputDbFs, minimumInput, maximumInput)
            : minimumInput;
        var displayValue = mode switch
        {
            MeterDisplayMode.Vu => effectiveInput - VuReferenceDbFs,
            MeterDisplayMode.Peak => effectiveInput,
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown meter display mode.")
        };
        var minimumDisplay = MinimumDisplayValue(mode);
        var maximumDisplay = MaximumDisplayValue(mode);
        var ratio = (displayValue - minimumDisplay) / (maximumDisplay - minimumDisplay);

        return new AudioMeterReading(
            mode,
            inputDbFs,
            effectiveInput,
            displayValue,
            Math.Clamp(ratio, 0d, 1d),
            Unit(mode),
            FormatDisplayValue(displayValue, mode));
    }

    public static double GetRatio(double inputDbFs, MeterDisplayMode mode) =>
        Evaluate(inputDbFs, mode).Ratio;

    public static double GetDisplayValue(double inputDbFs, MeterDisplayMode mode) =>
        Evaluate(inputDbFs, mode).DisplayValue;

    public static ImmutableArray<AudioMeterTick> GetTicks(MeterDisplayMode mode) => mode switch
    {
        MeterDisplayMode.Vu => VuTicks,
        MeterDisplayMode.Peak => PeakTicks,
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown meter display mode.")
    };

    public static string FormatDisplayValue(double displayValue, MeterDisplayMode mode)
    {
        var minimum = MinimumDisplayValue(mode);
        var clamped = double.IsFinite(displayValue)
            ? Math.Clamp(displayValue, minimum, MaximumDisplayValue(mode))
            : minimum;
        var rounded = Math.Round(clamped, 1, MidpointRounding.AwayFromZero);
        var number = Math.Abs(rounded - Math.Round(rounded)) <= ComparisonTolerance
            ? Math.Round(rounded).ToString("0", CultureInfo.InvariantCulture)
            : rounded.ToString("0.0", CultureInfo.InvariantCulture);
        var prefix = mode == MeterDisplayMode.Vu && rounded > 0 ? "+" : string.Empty;
        return $"{prefix}{number} {Unit(mode)}";
    }

    public static AudioMeterScaleValidation ValidateBoundaries()
    {
        var failures = ImmutableArray.CreateBuilder<string>();
        var checkedCases = 0;

        void Check(string name, bool condition, string actual)
        {
            checkedCases++;
            if (!condition)
            {
                failures.Add($"{name}: {actual}");
            }
        }

        void CheckReading(
            string name,
            double inputDbFs,
            MeterDisplayMode mode,
            double expectedDisplay,
            double expectedRatio)
        {
            var reading = Evaluate(inputDbFs, mode);
            Check(
                name,
                NearlyEqual(reading.DisplayValue, expectedDisplay) && NearlyEqual(reading.Ratio, expectedRatio),
                $"display={reading.DisplayValue:R}, ratio={reading.Ratio:R}");
        }

        CheckReading("VU floor", -78d, MeterDisplayMode.Vu, -60d, 0d);
        CheckReading("VU reference", -18d, MeterDisplayMode.Vu, 0d, 60d / 63d);
        CheckReading("VU ceiling", -15d, MeterDisplayMode.Vu, 3d, 1d);
        CheckReading("VU lower clamp", -120d, MeterDisplayMode.Vu, -60d, 0d);
        CheckReading("VU upper clamp", 0d, MeterDisplayMode.Vu, 3d, 1d);
        CheckReading("Peak floor", -60d, MeterDisplayMode.Peak, -60d, 0d);
        CheckReading("Peak midpoint", -30d, MeterDisplayMode.Peak, -30d, 0.5d);
        CheckReading("Peak ceiling", 0d, MeterDisplayMode.Peak, 0d, 1d);
        CheckReading("Peak lower clamp", -120d, MeterDisplayMode.Peak, -60d, 0d);
        CheckReading("Peak upper clamp", 12d, MeterDisplayMode.Peak, 0d, 1d);
        CheckReading("Non-finite VU fail-safe", double.NaN, MeterDisplayMode.Vu, -60d, 0d);
        CheckReading("Non-finite Peak fail-safe", double.PositiveInfinity, MeterDisplayMode.Peak, -60d, 0d);

        Check(
            "VU tick boundaries",
            TickBoundariesAre(VuTicks, VuMinimum, VuMaximum),
            TickSummary(VuTicks));
        Check(
            "Peak tick boundaries",
            TickBoundariesAre(PeakTicks, PeakMinimumDbFs, PeakMaximumDbFs),
            TickSummary(PeakTicks));
        Check(
            "VU ticks increase monotonically",
            TicksIncreaseMonotonically(VuTicks),
            TickSummary(VuTicks));
        Check(
            "Peak ticks increase monotonically",
            TicksIncreaseMonotonically(PeakTicks),
            TickSummary(PeakTicks));

        var vuReferenceTick = VuTicks.SingleOrDefault(tick => NearlyEqual(tick.DisplayValue, 0d));
        Check(
            "VU zero tick uses -18 dBFS reference",
            NearlyEqual(vuReferenceTick.InputDbFs, VuReferenceDbFs) &&
            NearlyEqual(vuReferenceTick.Ratio, 60d / 63d),
            $"input={vuReferenceTick.InputDbFs:R}, ratio={vuReferenceTick.Ratio:R}");

        return new AudioMeterScaleValidation(checkedCases, failures.ToImmutable());
    }

    internal static void ValidateOrThrow() => ValidateBoundaries().ThrowIfInvalid();

    private static ImmutableArray<AudioMeterTick> CreateTicks(
        MeterDisplayMode mode,
        IEnumerable<double> displayValues)
    {
        var builder = ImmutableArray.CreateBuilder<AudioMeterTick>();
        foreach (var displayValue in displayValues)
        {
            var inputDbFs = mode switch
            {
                MeterDisplayMode.Vu => VuReferenceDbFs + displayValue,
                MeterDisplayMode.Peak => displayValue,
                _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown meter display mode.")
            };
            var reading = Evaluate(inputDbFs, mode);
            builder.Add(new AudioMeterTick(
                reading.DisplayValue,
                reading.EffectiveDbFs,
                reading.Ratio,
                reading.DisplayText));
        }

        return builder.ToImmutable();
    }

    private static double MinimumInputDbFs(MeterDisplayMode mode) => mode switch
    {
        MeterDisplayMode.Vu => VuReferenceDbFs + VuMinimum,
        MeterDisplayMode.Peak => PeakMinimumDbFs,
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown meter display mode.")
    };

    private static double MaximumInputDbFs(MeterDisplayMode mode) => mode switch
    {
        MeterDisplayMode.Vu => VuReferenceDbFs + VuMaximum,
        MeterDisplayMode.Peak => PeakMaximumDbFs,
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown meter display mode.")
    };

    private static double MinimumDisplayValue(MeterDisplayMode mode) => mode switch
    {
        MeterDisplayMode.Vu => VuMinimum,
        MeterDisplayMode.Peak => PeakMinimumDbFs,
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown meter display mode.")
    };

    private static double MaximumDisplayValue(MeterDisplayMode mode) => mode switch
    {
        MeterDisplayMode.Vu => VuMaximum,
        MeterDisplayMode.Peak => PeakMaximumDbFs,
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown meter display mode.")
    };

    private static string Unit(MeterDisplayMode mode) => mode switch
    {
        MeterDisplayMode.Vu => "VU",
        MeterDisplayMode.Peak => "dBFS",
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown meter display mode.")
    };

    private static bool TickBoundariesAre(
        ImmutableArray<AudioMeterTick> ticks,
        double expectedMinimum,
        double expectedMaximum) =>
        !ticks.IsDefaultOrEmpty &&
        NearlyEqual(ticks[0].DisplayValue, expectedMinimum) &&
        NearlyEqual(ticks[^1].DisplayValue, expectedMaximum) &&
        NearlyEqual(ticks[0].Ratio, 0d) &&
        NearlyEqual(ticks[^1].Ratio, 1d);

    private static bool TicksIncreaseMonotonically(ImmutableArray<AudioMeterTick> ticks)
    {
        for (var index = 1; index < ticks.Length; index++)
        {
            if (ticks[index].DisplayValue <= ticks[index - 1].DisplayValue ||
                ticks[index].Ratio <= ticks[index - 1].Ratio)
            {
                return false;
            }
        }

        return true;
    }

    private static string TickSummary(ImmutableArray<AudioMeterTick> ticks) =>
        string.Join(", ", ticks.Select(tick => $"{tick.DisplayValue:R}@{tick.Ratio:R}"));

    private static bool NearlyEqual(double left, double right) =>
        Math.Abs(left - right) <= ComparisonTolerance;
}
