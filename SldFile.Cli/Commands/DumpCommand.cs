using System;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace SldFile.Cli.Commands;

public static class DumpCommand
{
    public static int Execute(CommandLineOptions opts)
    {
        if (string.IsNullOrWhiteSpace(opts.FilePath))
        {
            Console.Error.WriteLine("Error: File path is required for 'dump' command. See 'sldfile-cli --help'.");
            return 1;
        }

        string fullPath = Path.GetFullPath(opts.FilePath);
        if (!File.Exists(fullPath))
        {
            Console.Error.WriteLine($"Error: File not found: '{fullPath}'");
            return 1;
        }

        try
        {
            using var file = SwFile.Open(fullPath, readOnly: true);

            var streams = file.RawStreams?.Select(kvp => new
            {
                Name = kvp.Key,
                Size = (long)kvp.Value.Length
            }).OrderBy(s => s.Name).ToList();

            if (opts.JsonOutput)
            {
                string json = JsonSerializer.Serialize(new
                {
                    File = Path.GetFileName(fullPath),
                    TotalStreams = streams?.Count ?? 0,
                    Streams = streams
                }, new JsonSerializerOptions { WriteIndented = true });

                Console.WriteLine(json);
                return 0;
            }

            Console.WriteLine($"Compound File Binary Streams: {Path.GetFileName(fullPath)} ({streams?.Count ?? 0} streams)");
            Console.WriteLine("--------------------------------------------------------------------------------");
            Console.WriteLine($"  {"Stream Name",-48} {"Size (Bytes)",16} {"Size (KB)",12}");
            Console.WriteLine("--------------------------------------------------------------------------------");

            if (streams != null)
            {
                long totalBytes = 0;
                foreach (var s in streams)
                {
                    totalBytes += s.Size;
                    Console.WriteLine($"  {s.Name,-48} {s.Size,16:N0} {s.Size / 1024.0,12:F1}");
                }
                Console.WriteLine("--------------------------------------------------------------------------------");
                Console.WriteLine($"  {"Total",-48} {totalBytes,16:N0} {totalBytes / (1024.0 * 1024.0),12:F2} MB");
            }

            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error dumping streams: {ex.Message}");
            return 1;
        }
    }
}
