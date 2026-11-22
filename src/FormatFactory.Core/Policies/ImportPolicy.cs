using FormatFactoryWinUI.Models;

namespace FormatFactoryWinUI.ApplicationServices;

/// <summary>
/// Pure import decisions shared by file, folder and queue-recovery entry points.
/// </summary>
public static class ImportPolicy
{
    public static bool RequiresFfprobe(ContentKind contentKind) =>
        contentKind is ContentKind.Video or ContentKind.Audio or ContentKind.Image;

    public static IComparer<string> NaturalPathComparer { get; } = new NaturalStringComparer();

    private sealed class NaturalStringComparer : IComparer<string>
    {
        public int Compare(string? left, string? right)
        {
            if (ReferenceEquals(left, right))
            {
                return 0;
            }
            if (left is null)
            {
                return -1;
            }
            if (right is null)
            {
                return 1;
            }

            var leftIndex = 0;
            var rightIndex = 0;
            while (leftIndex < left.Length && rightIndex < right.Length)
            {
                if (char.IsDigit(left[leftIndex]) && char.IsDigit(right[rightIndex]))
                {
                    var leftStart = leftIndex;
                    var rightStart = rightIndex;
                    while (leftIndex < left.Length && left[leftIndex] == '0')
                    {
                        leftIndex++;
                    }
                    while (rightIndex < right.Length && right[rightIndex] == '0')
                    {
                        rightIndex++;
                    }

                    var leftDigits = leftIndex;
                    var rightDigits = rightIndex;
                    while (leftIndex < left.Length && char.IsDigit(left[leftIndex]))
                    {
                        leftIndex++;
                    }
                    while (rightIndex < right.Length && char.IsDigit(right[rightIndex]))
                    {
                        rightIndex++;
                    }

                    var significantLength = (leftIndex - leftDigits).CompareTo(rightIndex - rightDigits);
                    if (significantLength != 0)
                    {
                        return significantLength;
                    }

                    var number = string.Compare(
                        left,
                        leftDigits,
                        right,
                        rightDigits,
                        leftIndex - leftDigits,
                        StringComparison.Ordinal);
                    if (number != 0)
                    {
                        return number;
                    }

                    var leadingZeros = (leftDigits - leftStart).CompareTo(rightDigits - rightStart);
                    if (leadingZeros != 0)
                    {
                        return leadingZeros;
                    }
                    continue;
                }

                var character = char.ToUpperInvariant(left[leftIndex])
                    .CompareTo(char.ToUpperInvariant(right[rightIndex]));
                if (character != 0)
                {
                    return character;
                }
                leftIndex++;
                rightIndex++;
            }

            var length = (left.Length - leftIndex).CompareTo(right.Length - rightIndex);
            return length != 0 ? length : string.Compare(left, right, StringComparison.Ordinal);
        }
    }
}
