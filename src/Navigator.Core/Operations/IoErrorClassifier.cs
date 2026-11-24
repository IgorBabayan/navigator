namespace Navigator.Core.Operations;

/// <summary>Maps exceptions from System.IO to user-meaningful error kinds on every platform.</summary>
public static class IoErrorClassifier
{
    // Windows HRESULTs (0x8007xxxx = Win32 error codes)
    private const int ErrorSharingViolation = unchecked((int)0x80070020);
    private const int ErrorLockViolation = unchecked((int)0x80070021);
    private const int ErrorHandleDiskFull = unchecked((int)0x80070027);
    private const int ErrorDiskFull = unchecked((int)0x80070070);
    private const int ErrorNotReady = unchecked((int)0x80070015);
    private const int ErrorFileExists = unchecked((int)0x80070050);
    private const int ErrorAlreadyExists = unchecked((int)0x800700B7);
    private const int ErrorInvalidName = unchecked((int)0x8007007B);
    private const int ErrorNetNameDeleted = unchecked((int)0x80070040);
    private const int ErrorBadNetPath = unchecked((int)0x80070035);
    private const int ErrorUnexpectedNetworkError = unchecked((int)0x8007003B);

    // On Unix .NET stores errno in HResult for errors without a dedicated exception type.
    private const int ErrnoNoSpace = 28;
    private const int ErrnoQuotaLinux = 122;
    private const int ErrnoQuotaMac = 69;
    private const int ErrnoTextBusy = 26;
    private const int ErrnoBusy = 16;
    private const int ErrnoExists = 17;
    private const int ErrnoNameTooLongLinux = 36;
    private const int ErrnoNameTooLongMac = 63;
    private const int ErrnoNotSupportedLinux = 95;
    private const int ErrnoPermission = 1;
    private const int ErrnoAccess = 13;
    private const int ErrnoReadOnlyFs = 30;
    private const int ErrnoIo = 5;
    private const int ErrnoNotConnectedLinux = 107;
    private const int ErrnoHostDownLinux = 112;
    private const int ErrnoStaleLinux = 116;

    public static FileOperationErrorKind Classify(Exception exception) => exception switch
    {
        UnauthorizedAccessException => FileOperationErrorKind.AccessDenied,
        FileNotFoundException or DirectoryNotFoundException => FileOperationErrorKind.NotFound,
        PathTooLongException => FileOperationErrorKind.PathTooLong,
        DriveNotFoundException => FileOperationErrorKind.DeviceNotReady,
        NotSupportedException or PlatformNotSupportedException => FileOperationErrorKind.NotSupported,
        IOException io => ClassifyHResult(io.HResult),
        ArgumentException => FileOperationErrorKind.InvalidName,
        _ => FileOperationErrorKind.Unknown,
    };

    private static FileOperationErrorKind ClassifyHResult(int hresult)
    {
        if (OperatingSystem.IsWindows())
        {
            return hresult switch
            {
                ErrorSharingViolation or ErrorLockViolation => FileOperationErrorKind.FileInUse,
                ErrorDiskFull or ErrorHandleDiskFull => FileOperationErrorKind.DiskFull,
                ErrorNotReady => FileOperationErrorKind.DeviceNotReady,
                ErrorFileExists or ErrorAlreadyExists => FileOperationErrorKind.AlreadyExists,
                ErrorInvalidName => FileOperationErrorKind.InvalidName,
                ErrorNetNameDeleted or ErrorBadNetPath or ErrorUnexpectedNetworkError => FileOperationErrorKind.NetworkError,
                _ => FileOperationErrorKind.Unknown,
            };
        }

        var mac = OperatingSystem.IsMacOS();
        return hresult switch
        {
            ErrnoNoSpace => FileOperationErrorKind.DiskFull,
            ErrnoQuotaLinux when !mac => FileOperationErrorKind.DiskFull,
            ErrnoQuotaMac when mac => FileOperationErrorKind.DiskFull,
            ErrnoTextBusy or ErrnoBusy => FileOperationErrorKind.FileInUse,
            ErrnoExists => FileOperationErrorKind.AlreadyExists,
            ErrnoNameTooLongLinux when !mac => FileOperationErrorKind.PathTooLong,
            ErrnoNameTooLongMac when mac => FileOperationErrorKind.PathTooLong,
            ErrnoNotSupportedLinux when !mac => FileOperationErrorKind.NotSupported,
            ErrnoPermission or ErrnoAccess or ErrnoReadOnlyFs => FileOperationErrorKind.AccessDenied,
            ErrnoIo => FileOperationErrorKind.DeviceNotReady,
            ErrnoNotConnectedLinux or ErrnoHostDownLinux or ErrnoStaleLinux when !mac => FileOperationErrorKind.NetworkError,
            _ => FileOperationErrorKind.Unknown,
        };
    }

    /// <summary>Errors where retrying the same step cannot succeed without the user changing something first.</summary>
    public static bool IsRetryable(FileOperationErrorKind kind)
        => kind is not (FileOperationErrorKind.DestinationInsideSource or FileOperationErrorKind.InvalidName or FileOperationErrorKind.NotSupported);
}
