using System.Text;

namespace Dormant.Cli.Commands;

/// <summary>
/// Writes a report or export file whole or not at all: to a temporary file next to it, then moved into place. On Linux a
/// new directory is 0700 and the file 0600, like the store, because reports hold the same names (docs/threat-model.md).
/// </summary>
internal static class OutputFile
{
    private const UnixFileMode PrivateDirectory = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode PrivateFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    public static async Task WriteAsync(string path, Func<Stream, Task> write, CancellationToken ct)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)!;
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(directory);
        }
        else if (!Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory, PrivateDirectory);
        }

        var temporary = fullPath + ".tmp-" + Environment.ProcessId;
        var options = new FileStreamOptions
        {
            Mode = FileMode.Create,
            Access = FileAccess.Write,
            Share = FileShare.None,
            BufferSize = 64 * 1024,
            Options = FileOptions.Asynchronous,
        };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = PrivateFile;
        }

        try
        {
            await using (var stream = new FileStream(temporary, options))
            {
                await write(stream);
                await stream.FlushAsync(ct);
            }

            File.Move(temporary, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    public static Task WriteTextAsync(string path, string text, Encoding encoding, CancellationToken ct) =>
        WriteAsync(path, async stream =>
        {
            await using var writer = new StreamWriter(stream, encoding, leaveOpen: true);
            await writer.WriteAsync(text.AsMemory(), ct);
        }, ct);

    public static bool IsWriteError(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException;
}
