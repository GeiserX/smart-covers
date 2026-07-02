namespace SmartCovers;

/// <summary>
/// Compares strings ordinally (case-insensitive), except that runs of ASCII digits
/// compare by numeric value — so <c>page-2</c> sorts before <c>page-10</c>. Comic
/// archives frequently name pages without zero-padding, where plain ordinal order
/// would misplace the first page (and therefore the cover).
/// </summary>
internal sealed class NaturalStringComparer : IComparer<string>
{
    /// <summary>
    /// The shared singleton instance.
    /// </summary>
    public static readonly NaturalStringComparer Instance = new();

    private NaturalStringComparer()
    {
    }

    /// <inheritdoc />
    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y))
        {
            return 0;
        }

        if (x is null)
        {
            return -1;
        }

        if (y is null)
        {
            return 1;
        }

        int i = 0;
        int j = 0;

        while (i < x.Length && j < y.Length)
        {
            if (char.IsAsciiDigit(x[i]) && char.IsAsciiDigit(y[j]))
            {
                // Locate both digit runs.
                int startX = i;
                int startY = j;
                while (i < x.Length && char.IsAsciiDigit(x[i]))
                {
                    i++;
                }

                while (j < y.Length && char.IsAsciiDigit(y[j]))
                {
                    j++;
                }

                // Skip leading zeros (keep at least one digit) and compare by
                // numeric value without overflow: longer significant run wins,
                // then digit-by-digit.
                int sigX = startX;
                int sigY = startY;
                while (sigX < i - 1 && x[sigX] == '0')
                {
                    sigX++;
                }

                while (sigY < j - 1 && y[sigY] == '0')
                {
                    sigY++;
                }

                int lenX = i - sigX;
                int lenY = j - sigY;
                if (lenX != lenY)
                {
                    return lenX - lenY;
                }

                for (int k = 0; k < lenX; k++)
                {
                    int d = x[sigX + k] - y[sigY + k];
                    if (d != 0)
                    {
                        return d;
                    }
                }

                // Same numeric value (e.g. "2" vs "002"): fewer leading zeros
                // first, for a deterministic total order.
                int zeroDiff = (i - startX) - (j - startY);
                if (zeroDiff != 0)
                {
                    return zeroDiff;
                }

                continue;
            }

            char cx = char.ToUpperInvariant(x[i]);
            char cy = char.ToUpperInvariant(y[j]);
            if (cx != cy)
            {
                return cx - cy;
            }

            i++;
            j++;
        }

        return (x.Length - i) - (y.Length - j);
    }
}
