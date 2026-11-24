namespace Navigator.Core.Sorting;

/// <summary>
/// "Natural" file name ordering: digit runs compare by numeric value ("file2" &lt; "file10"),
/// everything else compares case-insensitively. Allocation-free; works on spans.
/// </summary>
public sealed class NaturalStringComparer : IComparer<string?>
{
    public static NaturalStringComparer Instance { get; } = new();

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y))
            return 0;
        if (x is null)
            return -1;
        if (y is null)
            return 1;

        var result = Compare(x.AsSpan(), y.AsSpan());
        // Deterministic tie-break so that "a" and "A" never compare equal (stable UI ordering).
        return result != 0 ? result : string.CompareOrdinal(x, y);
    }

    public static int Compare(ReadOnlySpan<char> x, ReadOnlySpan<char> y)
    {
        var i = 0;
        var j = 0;
        while (i < x.Length && j < y.Length)
        {
            var cx = x[i];
            var cy = y[j];

            if (char.IsAsciiDigit(cx) && char.IsAsciiDigit(cy))
            {
                var startX = i;
                var startY = j;
                while (startX < x.Length - 1 && x[startX] == '0' && char.IsAsciiDigit(x[startX + 1]))
                    startX++;
                while (startY < y.Length - 1 && y[startY] == '0' && char.IsAsciiDigit(y[startY + 1]))
                    startY++;

                var endX = startX;
                var endY = startY;
                while (endX < x.Length && char.IsAsciiDigit(x[endX]))
                    endX++;
                while (endY < y.Length && char.IsAsciiDigit(y[endY]))
                    endY++;

                var lengthX = endX - startX;
                var lengthY = endY - startY;
                if (lengthX != lengthY)
                    return lengthX < lengthY ? -1 : 1;

                var digits = x[startX..endX].SequenceCompareTo(y[startY..endY]);
                if (digits != 0)
                    return digits < 0 ? -1 : 1;

                // Same value: fewer leading zeros first ("1" < "01").
                var zerosX = startX - i;
                var zerosY = startY - j;
                if (zerosX != zerosY)
                    return zerosX < zerosY ? -1 : 1;

                i = endX;
                j = endY;
                continue;
            }

            if (cx != cy)
            {
                var ux = char.ToUpperInvariant(cx);
                var uy = char.ToUpperInvariant(cy);
                if (ux != uy)
                {
                    if (char.IsAscii(ux) && char.IsAscii(uy))
                        return ux < uy ? -1 : 1;

                    // Non-ASCII: invariant culture keeps accented letters next to their base letter.
#pragma warning disable CA1309 // Linguistic order is the point here: "é" sorts next to "e".
                    var cmp = string.Compare(ux.ToString(), uy.ToString(), StringComparison.InvariantCultureIgnoreCase);
#pragma warning restore CA1309
                    if (cmp == 0)
                        cmp = ux.CompareTo(uy);
                    return cmp < 0 ? -1 : 1;
                }
            }

            i++;
            j++;
        }

        var remainingX = x.Length - i;
        var remainingY = y.Length - j;
        return remainingX == remainingY ? 0 : remainingX < remainingY ? -1 : 1;
    }
}
