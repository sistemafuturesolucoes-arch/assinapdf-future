using System;
using System.Collections.Generic;
using System.Linq;

namespace AssinaPDF.MacPrototype.Services;

public sealed record PdfiumTextWordInfo(
    int PageIndex,
    int LineIndex,
    int WordIndex,
    string Text,
    double Left,
    double Bottom,
    double Right,
    double Top,
    double FontSize,
    string? FontName,
    IReadOnlyList<PdfiumCharacterInfo> Characters);

public sealed record PdfiumTextLineInfo(
    int PageIndex,
    int LineIndex,
    string Text,
    double Left,
    double Bottom,
    double Right,
    double Top,
    IReadOnlyList<PdfiumCharacterInfo> Characters,
    IReadOnlyList<PdfiumTextWordInfo> Words);

/// <summary>
/// Agrupa os caracteres extraídos pelo PDFium em linhas e palavras.
/// As coordenadas permanecem no sistema PDF: pontos, origem inferior esquerda.
/// </summary>
public static class PdfiumTextGrouping
{
    private sealed class LineBucket
    {
        public List<PdfiumCharacterInfo> Characters { get; } = new();
        public double AverageBaseline =>
            Characters.Count == 0 ? 0 : Characters.Average(item => item.Bottom);
    }

    public static IReadOnlyList<PdfiumTextLineInfo> GroupIntoLines(
        IReadOnlyList<PdfiumCharacterInfo> characters,
        int pageIndex,
        double baselineToleranceFactor = 0.30,
        double minimumBaselineTolerance = 2.0)
    {
        ArgumentNullException.ThrowIfNull(characters);

        if (pageIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(pageIndex));
        if (!double.IsFinite(baselineToleranceFactor) || baselineToleranceFactor <= 0)
            throw new ArgumentOutOfRangeException(nameof(baselineToleranceFactor));
        if (!double.IsFinite(minimumBaselineTolerance) || minimumBaselineTolerance < 0)
            throw new ArgumentOutOfRangeException(nameof(minimumBaselineTolerance));

        var ordered = characters
            .Where(item =>
                item.PageIndex == pageIndex &&
                !string.IsNullOrEmpty(item.Text) &&
                double.IsFinite(item.Left) &&
                double.IsFinite(item.Bottom) &&
                double.IsFinite(item.Right) &&
                double.IsFinite(item.Top) &&
                item.Right >= item.Left &&
                item.Top >= item.Bottom)
            .OrderByDescending(item => (item.Bottom + item.Top) / 2)
            .ThenBy(item => item.Left)
            .ThenBy(item => item.CharacterIndex)
            .ToList();

        var buckets = new List<LineBucket>();

        foreach (var character in ordered)
        {
            LineBucket? bestBucket = null;
            double bestDistance = double.PositiveInfinity;

            foreach (var bucket in buckets)
            {
                double distance = Math.Abs(character.Bottom - bucket.AverageBaseline);
                double referenceFontSize = bucket.Characters
                    .Where(item => item.FontSize > 0 && double.IsFinite(item.FontSize))
                    .Select(item => item.FontSize)
                    .DefaultIfEmpty(character.FontSize)
                    .Average();

                double tolerance = Math.Max(
                    minimumBaselineTolerance,
                    Math.Min(
                        character.FontSize > 0 ? character.FontSize : referenceFontSize,
                        referenceFontSize > 0 ? referenceFontSize : character.FontSize) *
                    baselineToleranceFactor);

                bool baselineMatches = distance <= tolerance;
                bool verticalOverlapMatches = HasMeaningfulVerticalOverlap(
                    character, bucket.Characters);

                if ((baselineMatches || verticalOverlapMatches) && distance < bestDistance)
                {
                    bestBucket = bucket;
                    bestDistance = distance;
                }
            }

            if (bestBucket is null)
            {
                bestBucket = new LineBucket();
                buckets.Add(bestBucket);
            }

            bestBucket.Characters.Add(character);
        }

        var sortedBuckets = buckets
            .OrderByDescending(bucket => bucket.Characters.Average(
                item => (item.Bottom + item.Top) / 2))
            .ThenBy(bucket => bucket.Characters.Min(item => item.Left))
            .ToList();

        var lines = new List<PdfiumTextLineInfo>(sortedBuckets.Count);

        for (int lineIndex = 0; lineIndex < sortedBuckets.Count; lineIndex++)
        {
            var lineCharacters = sortedBuckets[lineIndex].Characters
                .OrderBy(item => item.Left)
                .ThenBy(item => item.CharacterIndex)
                .ToList();

            var words = BuildWords(pageIndex, lineIndex, lineCharacters);

            lines.Add(new PdfiumTextLineInfo(
                pageIndex,
                lineIndex,
                string.Concat(lineCharacters.Select(item => item.Text)),
                lineCharacters.Min(item => item.Left),
                lineCharacters.Min(item => item.Bottom),
                lineCharacters.Max(item => item.Right),
                lineCharacters.Max(item => item.Top),
                lineCharacters,
                words));
        }

        return lines;
    }

    private static IReadOnlyList<PdfiumTextWordInfo> BuildWords(
        int pageIndex,
        int lineIndex,
        IReadOnlyList<PdfiumCharacterInfo> characters)
    {
        var words = new List<PdfiumTextWordInfo>();
        var current = new List<PdfiumCharacterInfo>();

        void Flush()
        {
            if (current.Count == 0)
                return;

            words.Add(new PdfiumTextWordInfo(
                pageIndex,
                lineIndex,
                words.Count,
                string.Concat(current.Select(item => item.Text)),
                current.Min(item => item.Left),
                current.Min(item => item.Bottom),
                current.Max(item => item.Right),
                current.Max(item => item.Top),
                current.Where(item => item.FontSize > 0)
                    .Select(item => item.FontSize)
                    .DefaultIfEmpty(0)
                    .Average(),
                current.Select(item => item.FontName)
                    .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name)),
                current.ToArray()));

            current.Clear();
        }

        foreach (var character in characters)
        {
            if (string.IsNullOrWhiteSpace(character.Text))
            {
                Flush();
                continue;
            }

            if (current.Count > 0)
            {
                var previous = current[^1];
                double gap = character.Left - previous.Right;
                double fontSize = Math.Min(
                    previous.FontSize > 0 ? previous.FontSize : character.FontSize,
                    character.FontSize > 0 ? character.FontSize : previous.FontSize);
                double gapThreshold = Math.Max(1.5, fontSize * 0.28);

                if (gap > gapThreshold)
                    Flush();
            }

            current.Add(character);
        }

        Flush();
        return words;
    }

    private static bool HasMeaningfulVerticalOverlap(
        PdfiumCharacterInfo character,
        IReadOnlyList<PdfiumCharacterInfo> lineCharacters)
    {
        double characterHeight = character.Top - character.Bottom;
        if (characterHeight <= 0)
            return false;

        foreach (var other in lineCharacters)
        {
            double otherHeight = other.Top - other.Bottom;
            if (otherHeight <= 0)
                continue;

            double overlap = Math.Min(character.Top, other.Top) -
                             Math.Max(character.Bottom, other.Bottom);
            double requiredOverlap = Math.Min(characterHeight, otherHeight) * 0.45;

            if (overlap >= requiredOverlap)
                return true;
        }

        return false;
    }
}
