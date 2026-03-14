using System.Text;

namespace StreamBench;

internal static class CliLog
{
    private sealed class TeeTextWriter(TextWriter primary, TextWriter secondary) : TextWriter
    {
        public override Encoding Encoding => primary.Encoding;

        public override void Write(char value)
        {
            primary.Write(value);
            secondary.Write(value);
        }

        public override void Write(string? value)
        {
            primary.Write(value);
            secondary.Write(value);
        }

        public override void Write(char[] buffer, int index, int count)
        {
            primary.Write(buffer, index, count);
            secondary.Write(buffer, index, count);
        }

        public override void WriteLine()
        {
            primary.WriteLine();
            secondary.WriteLine();
        }

        public override void WriteLine(string? value)
        {
            primary.WriteLine(value);
            secondary.WriteLine(value);
        }

        public override void Flush()
        {
            primary.Flush();
            secondary.Flush();
        }
    }

    private static StreamWriter? _fileWriter;
    private static TextWriter? _originalOut;
    private static TextWriter? _originalError;

    internal static string? LogPath { get; private set; }

    internal static void InitializeFromEnvironment()
    {
        if (_fileWriter is not null)
            return;

        string? rawPath = Environment.GetEnvironmentVariable("STREAMBENCH_CLI_LOG");
        if (string.IsNullOrWhiteSpace(rawPath))
            return;

        try
        {
            string fullPath = Path.GetFullPath(rawPath);
            string? directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            _originalOut = Console.Out;
            _originalError = Console.Error;
            var fileStream = new FileStream(
                fullPath,
                FileMode.Append,
                FileAccess.Write,
                FileShare.ReadWrite);
            _fileWriter = new StreamWriter(fileStream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
            {
                AutoFlush = true
            };

            Console.SetOut(new TeeTextWriter(_originalOut, _fileWriter));
            Console.SetError(new TeeTextWriter(_originalError, _fileWriter));
            LogPath = fullPath;
        }
        catch
        {
            _fileWriter?.Dispose();
            _fileWriter = null;
            LogPath = null;
        }
    }

    internal static void Shutdown()
    {
        try
        {
            if (_originalOut is not null)
                Console.SetOut(_originalOut);

            if (_originalError is not null)
                Console.SetError(_originalError);

            _fileWriter?.Flush();
            _fileWriter?.Dispose();
        }
        catch
        {
        }
        finally
        {
            _fileWriter = null;
            _originalOut = null;
            _originalError = null;
        }
    }
}
