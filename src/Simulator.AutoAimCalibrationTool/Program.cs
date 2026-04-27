using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

var repoRoot = ResolveRepoRoot();
string defaultLogPath = Path.Combine(repoRoot, "src", "Simulator.ThreeD", "bin", "Debug", "net10.0-windows", "logs", "autoaim_training.log");
string inputPath = args.Length > 0 ? Path.GetFullPath(args[0]) : defaultLogPath;
if (!File.Exists(inputPath))
{
    Console.Error.WriteLine($"未找到训练日志: {inputPath}");
    return 1;
}

List<CalibrationSample> samples = LoadSamples(inputPath);
if (samples.Count == 0)
{
    Console.Error.WriteLine("日志中没有可分析的自瞄训练样本。");
    return 2;
}

var groups = samples
    .GroupBy(sample => new CalibrationKey(sample.AmmoType, sample.TargetKind, ResolveDistanceBucket(sample.DistanceM)))
    .OrderBy(group => group.Key.AmmoType, StringComparer.OrdinalIgnoreCase)
    .ThenBy(group => group.Key.TargetKind, StringComparer.OrdinalIgnoreCase)
    .ThenBy(group => group.Key.DistanceBucket)
    .ToArray();

List<object> report = new(groups.Length);
foreach (IGrouping<CalibrationKey, CalibrationSample> group in groups)
{
    int hitCount = group.Count(sample => sample.IsHit);
    double avgSignedLeadErrorM = group.Average(sample => sample.SignedLeadErrorM);
    double avgLateralErrorM = group.Average(sample => sample.LateralErrorM);
    double avgVerticalErrorM = group.Average(sample => sample.VerticalErrorM);
    double avgDistanceM = group.Average(sample => sample.DistanceM);
    double speedMps = string.Equals(group.Key.AmmoType, "42mm", StringComparison.OrdinalIgnoreCase) ? 12.0 : 25.0;
    double recommendedTimeBiasDeltaSec = Math.Clamp(avgSignedLeadErrorM / Math.Max(speedMps, 1e-6), -0.08, 0.08);

    report.Add(new
    {
        ammo = group.Key.AmmoType,
        target_kind = group.Key.TargetKind,
        distance_bucket = group.Key.DistanceBucket,
        sample_count = group.Count(),
        hit_count = hitCount,
        avg_distance_m = Math.Round(avgDistanceM, 4),
        avg_signed_lead_error_m = Math.Round(avgSignedLeadErrorM, 4),
        avg_lateral_error_m = Math.Round(avgLateralErrorM, 4),
        avg_vertical_error_m = Math.Round(avgVerticalErrorM, 4),
        recommended_time_bias_delta_sec = Math.Round(recommendedTimeBiasDeltaSec, 5),
    });
}

string outputPath = Path.Combine(Path.GetDirectoryName(inputPath) ?? repoRoot, "autoaim_profile_recommendation.json");
JsonSerializerOptions jsonOptions = new() { WriteIndented = true };
File.WriteAllText(outputPath, JsonSerializer.Serialize(report, jsonOptions));

Console.WriteLine($"输入日志: {inputPath}");
Console.WriteLine($"样本数量: {samples.Count}");
Console.WriteLine($"建议输出: {outputPath}");
Console.WriteLine();
foreach (dynamic item in report)
{
    Console.WriteLine($"{item.ammo,-4} | {item.target_kind,-16} | {item.distance_bucket,-8} | 样本 {item.sample_count,4} | lead {item.avg_signed_lead_error_m,7:+0.000;-0.000;0.000}m | lat {item.avg_lateral_error_m,7:+0.000;-0.000;0.000}m | vert {item.avg_vertical_error_m,7:+0.000;-0.000;0.000}m | dt {item.recommended_time_bias_delta_sec,7:+0.00000;-0.00000;0.00000}s");
}

return 0;

static string ResolveRepoRoot()
{
    string current = Directory.GetCurrentDirectory();
    DirectoryInfo? cursor = new(current);
    while (cursor is not null)
    {
        if (File.Exists(Path.Combine(cursor.FullName, "Simulator.sln")))
        {
            return cursor.FullName;
        }

        cursor = cursor.Parent;
    }

    return current;
}

static List<CalibrationSample> LoadSamples(string path)
{
    Regex scalarRegex = new(@"(?<key>[a-z_]+)=((?<value>[^\s]+))", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    Regex vectorRegex = new(@"(?<key>obs|pred|actual|err)=\((?<x>[+\-]?\d+(?:\.\d+)?),(?<y>[+\-]?\d+(?:\.\d+)?),(?<z>[+\-]?\d+(?:\.\d+)?)\)", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    List<CalibrationSample> result = new();
    foreach (string line in File.ReadLines(path))
    {
        Dictionary<string, string> scalars = new(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in scalarRegex.Matches(line))
        {
            scalars[match.Groups["key"].Value] = match.Groups["value"].Value;
        }

        Dictionary<string, (double X, double Y, double Z)> vectors = new(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in vectorRegex.Matches(line))
        {
            vectors[match.Groups["key"].Value] = (
                ParseDouble(match.Groups["x"].Value),
                ParseDouble(match.Groups["y"].Value),
                ParseDouble(match.Groups["z"].Value));
        }

        if (!scalars.TryGetValue("ammo", out string? ammoType)
            || !scalars.TryGetValue("kind", out string? targetKind)
            || !scalars.TryGetValue("outcome", out string? outcome)
            || !scalars.TryGetValue("distance_m", out string? distanceText)
            || !vectors.TryGetValue("obs", out var observed)
            || !vectors.TryGetValue("pred", out var predicted)
            || !vectors.TryGetValue("actual", out var actual))
        {
            continue;
        }

        double distanceM = ParseDouble(distanceText);
        (double signedLeadErrorM, double lateralErrorM) = ResolveHorizontalErrors(observed, predicted, actual);
        result.Add(new CalibrationSample(
            ammoType,
            targetKind,
            outcome,
            distanceM,
            signedLeadErrorM,
            lateralErrorM,
            actual.Z - predicted.Z));
    }

    return result;
}

static (double SignedLeadErrorM, double LateralErrorM) ResolveHorizontalErrors(
    (double X, double Y, double Z) observed,
    (double X, double Y, double Z) predicted,
    (double X, double Y, double Z) actual)
{
    double leadX = predicted.X - observed.X;
    double leadY = predicted.Y - observed.Y;
    double leadLength = Math.Sqrt(leadX * leadX + leadY * leadY);
    double errorX = actual.X - predicted.X;
    double errorY = actual.Y - predicted.Y;
    if (leadLength <= 1e-6)
    {
        return (0.0, Math.Sqrt(errorX * errorX + errorY * errorY));
    }

    double dirX = leadX / leadLength;
    double dirY = leadY / leadLength;
    double rightX = -dirY;
    double rightY = dirX;
    double signedLeadError = errorX * dirX + errorY * dirY;
    double lateralError = errorX * rightX + errorY * rightY;
    return (signedLeadError, lateralError);
}

static string ResolveDistanceBucket(double distanceM)
{
    if (distanceM < 4.0)
    {
        return "0_4m";
    }

    if (distanceM < 8.0)
    {
        return "4_8m";
    }

    if (distanceM < 12.0)
    {
        return "8_12m";
    }

    return "12m_plus";
}

static double ParseDouble(string text)
    => double.TryParse(text, NumberStyles.Float | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out double value)
        ? value
        : 0.0;

internal readonly record struct CalibrationKey(string AmmoType, string TargetKind, string DistanceBucket);

internal readonly record struct CalibrationSample(
    string AmmoType,
    string TargetKind,
    string Outcome,
    double DistanceM,
    double SignedLeadErrorM,
    double LateralErrorM,
    double VerticalErrorM)
{
    public bool IsHit => string.Equals(Outcome, "armor_hit", StringComparison.OrdinalIgnoreCase)
        || string.Equals(Outcome, "energy_hit", StringComparison.OrdinalIgnoreCase);
}
