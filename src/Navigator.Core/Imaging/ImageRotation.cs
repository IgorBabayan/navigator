namespace Navigator.Core.Imaging;

/// <summary>
/// "Rotate left / right" for picture files. JPEG photos only get their EXIF orientation changed (lossless, in place
/// when the tag exists), PNG images are turned pixel by pixel and re-encoded losslessly. Other formats are not offered.
/// </summary>
public static class ImageRotation
{
    /// <summary>Decided from the name only, so menus can ask for every right-click.</summary>
    public static bool CanRotate(string path) => JpegOrientation.IsJpeg(path) || PngRotation.IsPng(path);

    /// <summary>Turns the picture a quarter turn. Runs on the calling thread; call it off the UI thread.</summary>
    public static void Rotate(string path, bool clockwise)
    {
        if (JpegOrientation.IsJpeg(path))
            JpegOrientation.Rotate(path, clockwise);
        else if (PngRotation.IsPng(path))
            PngRotation.RotateFile(path, clockwise);
        else
            throw new NotSupportedException($"'{Path.GetFileName(path)}' cannot be rotated.");
    }
}
