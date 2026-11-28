namespace Navigator.Core.Imaging;

/// <summary>
/// The eight EXIF orientations (TIFF tag 0x0112): how the stored pixels must be transformed to be displayed upright.
/// Values match the tag; <see cref="Normal"/> needs nothing.
/// </summary>
public enum ImageOrientation
{
    Normal = 1,
    FlipHorizontal = 2,
    Rotate180 = 3,
    FlipVertical = 4,
    Transpose = 5,
    Rotate90 = 6,
    Transverse = 7,
    Rotate270 = 8,
}

/// <summary>Orientation arithmetic and pixel transforms shared by image rotation and thumbnail display.</summary>
public static class PixelOrientation
{
    // Composition "rotate the displayed picture a quarter turn" ∘ orientation, indexed by orientation - 1.
    private static readonly byte[] Clockwise = [6, 7, 8, 5, 2, 3, 4, 1];
    private static readonly byte[] CounterClockwise = [8, 5, 6, 7, 4, 1, 2, 3];

    public static bool IsValid(int value) => value is >= 1 and <= 8;

    /// <summary>The orientation that shows the picture turned a further quarter turn.</summary>
    public static ImageOrientation Turn(ImageOrientation current, bool clockwise)
    {
        var index = IsValid((int)current) ? (int)current - 1 : 0;
        return (ImageOrientation)(clockwise ? Clockwise[index] : CounterClockwise[index]);
    }

    /// <summary>True when displaying needs width and height swapped (orientations 5–8).</summary>
    public static bool SwapsDimensions(ImageOrientation orientation) => (int)orientation is >= 5 and <= 8;

    /// <summary>
    /// Applies <paramref name="orientation"/> to a packed pixel buffer (<paramref name="bytesPerPixel"/> bytes per pixel,
    /// rows of <paramref name="width"/> pixels without padding). Returns a new buffer; its size follows
    /// <see cref="SwapsDimensions"/>. Source rows are read sequentially, so the cost is one pass over the pixels.
    /// </summary>
    public static byte[] Apply(ReadOnlySpan<byte> source, int width, int height, int bytesPerPixel, ImageOrientation orientation)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bytesPerPixel);
        var length = checked(width * height * bytesPerPixel);
        if (source.Length < length)
            throw new ArgumentException("The buffer is smaller than width × height × bytesPerPixel.", nameof(source));

        var target = new byte[length];
        if (orientation == ImageOrientation.Normal || !IsValid((int)orientation))
        {
            source[..length].CopyTo(target);
            return target;
        }

        // Every orientation maps (x, y) to target column tx = a + b·x + c·y and row ty = d + e·x + f·y, so the target
        // index is linear in x and y: one origin and two steps, no per-pixel branching.
        var swap = SwapsDimensions(orientation);
        var targetWidth = swap ? height : width;
        var (a, b, c, d, e, f) = orientation switch
        {
            ImageOrientation.FlipHorizontal => (width - 1, -1, 0, 0, 0, 1),
            ImageOrientation.Rotate180 => (width - 1, -1, 0, height - 1, 0, -1),
            ImageOrientation.FlipVertical => (0, 1, 0, height - 1, 0, -1),
            ImageOrientation.Transpose => (0, 0, 1, 0, 1, 0),
            ImageOrientation.Rotate90 => (height - 1, 0, -1, 0, 1, 0),
            ImageOrientation.Transverse => (height - 1, 0, -1, width - 1, -1, 0),
            _ => (0, 0, 1, width - 1, -1, 0), // Rotate270
        };
        var origin = (d * targetWidth) + a;
        var stepX = (e * targetWidth) + b;
        var stepY = (f * targetWidth) + c;

        if (bytesPerPixel == 4)
        {
            var from = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(source[..length]);
            var to = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(target.AsSpan());
            for (var y = 0; y < height; y++)
            {
                var index = origin + (y * stepY);
                var row = from.Slice(y * width, width);
                for (var x = 0; x < row.Length; x++, index += stepX)
                    to[index] = row[x];
            }

            return target;
        }

        for (var y = 0; y < height; y++)
        {
            var index = origin + (y * stepY);
            var row = source.Slice(y * width * bytesPerPixel, width * bytesPerPixel);
            for (var x = 0; x < width; x++, index += stepX)
                row.Slice(x * bytesPerPixel, bytesPerPixel).CopyTo(target.AsSpan(index * bytesPerPixel, bytesPerPixel));
        }

        return target;
    }
}
