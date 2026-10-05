using System.Text;
using System.Text.RegularExpressions;

namespace PdfOcrPreprocessor.Core;

public sealed record ComparisonInput(string? Text, string State = "Success", int PageCount = 1, string? FileHash = null);
public sealed record CandidateDifference(string Lexeme, int LeftCount, int RightCount);
public sealed record ComparisonResult(string State, bool? Equal, double? CharacterAgreement, double? TokenAgreement,
    int? TextLengthDifference, CandidateDifference[] NumericDateDifferences, string[] Notes);

public static partial class Comparison
{
    public static string Normalize(string value) => Whitespace().Replace(value.Normalize(NormalizationForm.FormC), " ").Trim();

    public static ComparisonResult Compare(ComparisonInput left, ComparisonInput right, CancellationToken cancellationToken = default)
    {
        ComparisonResult Unavailable(string state) => new(state, null, null, null, null, [], []);
        if (left.State != "Success" || right.State != "Success") return Unavailable($"NotComparable: {left.State}/{right.State}");
        if (left.PageCount != right.PageCount) return Unavailable("PageCountMismatch");
        if (left.Text is null || right.Text is null) return Unavailable("Missing");
        var leftText = Normalize(left.Text);
        var rightText = Normalize(right.Text);
        if (leftText.Length == 0 && rightText.Length == 0) return Unavailable("NoTextualEvidence");
        var leftScalars = leftText.EnumerateRunes().Select(rune => rune.Value).ToArray();
        var rightScalars = rightText.EnumerateRunes().Select(rune => rune.Value).ToArray();
        if ((long)leftScalars.Length * rightScalars.Length > 25_000_000 && leftText != rightText)
            return Unavailable("ComparisonResourceLimit");
        var distance = leftText == rightText ? 0 : EditDistance(leftScalars, rightScalars, cancellationToken);
        var leftTokens = Counts(Tokens().Matches(leftText).Select(match => match.Value));
        var rightTokens = Counts(Tokens().Matches(rightText).Select(match => match.Value));
        var total = leftTokens.Values.Sum() + rightTokens.Values.Sum();
        var overlap = leftTokens.Sum(pair => Math.Min(pair.Value, rightTokens.GetValueOrDefault(pair.Key)));
        var leftNumbers = Counts(NumericDates().Matches(leftText).Select(match => match.Value));
        var rightNumbers = Counts(NumericDates().Matches(rightText).Select(match => match.Value));
        var differences = leftNumbers.Keys.Union(rightNumbers.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal)
            .Where(key => leftNumbers.GetValueOrDefault(key) != rightNumbers.GetValueOrDefault(key))
            .Select(key => new CandidateDifference(key, leftNumbers.GetValueOrDefault(key), rightNumbers.GetValueOrDefault(key))).ToArray();
        var notes = new List<string> { "Provisional ordinal correspondence; no visual registration.", "Numeric/date candidates are unpaired lexeme occurrences, not matched table cells." };
        if (left.FileHash != null && left.FileHash == right.FileHash) notes.Add("Identical file hashes: duplicate evidence, not independent corroboration.");
        if (leftText == rightText) notes.Add("Identical text is agreement, not independent corroboration or correctness.");
        return new(leftText.Length == 0 || rightText.Length == 0 ? "OneSidedText" : "Compared", leftText == rightText,
            1d - distance / (double)Math.Max(leftScalars.Length, rightScalars.Length), total == 0 ? null : 2d * overlap / total,
            leftScalars.Length - rightScalars.Length, differences, notes.ToArray());
    }

    private static Dictionary<string, int> Counts(IEnumerable<string> values) => values.GroupBy(value => value, StringComparer.Ordinal)
        .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

    private static int EditDistance(int[] left, int[] right, CancellationToken cancellationToken)
    {
        var previous = Enumerable.Range(0, right.Length + 1).ToArray();
        var current = new int[right.Length + 1];
        for (var row = 1; row <= left.Length; row++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            current[0] = row;
            for (var column = 1; column <= right.Length; column++)
                current[column] = Math.Min(Math.Min(previous[column] + 1, current[column - 1] + 1), previous[column - 1] + (left[row - 1] == right[column - 1] ? 0 : 1));
            (previous, current) = (current, previous);
        }
        return previous[right.Length];
    }

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)] private static partial Regex Whitespace();
    [GeneratedRegex(@"\S+", RegexOptions.CultureInvariant)] private static partial Regex Tokens();
    [GeneratedRegex(@"(?<![\p{L}\p{N}])(?:\(?[-+]?[$\p{Sc}]?\d[\d,./:\-]*%?\)?)(?![\p{L}\p{N}])", RegexOptions.CultureInvariant)]
    private static partial Regex NumericDates();
}