using IPMultiViewer.Core.Audio;

var behaviorChecks = 0;

void Require(bool condition, string message)
{
    behaviorChecks++;
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

var validation = AudioMeterScale.ValidateBoundaries();
if (!validation.IsValid)
{
    foreach (var failure in validation.Failures)
    {
        Console.Error.WriteLine(failure);
    }

    return 1;
}

var vuReference = AudioMeterScale.Evaluate(-18d, MeterDisplayMode.Vu);
var peakMidpoint = AudioMeterScale.Evaluate(-30d, MeterDisplayMode.Peak);
Require(
    vuReference.DisplayValue == 0d && vuReference.Ratio > 0.95d,
    "-18 dBFS must map to the VU reference point.");
Require(
    peakMidpoint.DisplayValue == -30d && Math.Abs(peakMidpoint.Ratio - 0.5d) < 0.000001d,
    "-30 dBFS must map to the peak meter midpoint.");

Console.WriteLine(
    $"desktop_core_self_test_ok audio_checks={validation.CheckedCases} behavior_checks={behaviorChecks} " +
    $"vu_reference={vuReference.DisplayText}@{vuReference.Ratio:F6} " +
    $"peak_midpoint={peakMidpoint.DisplayText}@{peakMidpoint.Ratio:F6}");
return 0;
