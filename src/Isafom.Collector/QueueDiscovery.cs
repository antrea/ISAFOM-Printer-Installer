using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace Isafom.Collector;

// Names are shown only in the local picker and transported through a private
// pipe. These objects must never be placed in a PortableBundle.
public sealed record QueueChoice(string Name, string DriverName, string PortName, bool ModelHint, string Source)
{
    public string DisplayName => Name + " — " + DriverName;
    public override string ToString() => DisplayName;
}

internal static class DiagnosticBackend
{
    public static JsonDocument Run(bool listOnly, string? queueName, CancellationToken ct)
    {
        using var source = typeof(CollectorSession).Assembly.GetManifestResourceStream("Isafom.Collector.Diagnostic.ps1") ?? throw new IOException();
        using var reader = new StreamReader(source, Encoding.UTF8);
        const string command = "[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false); [Console]::InputEncoding = New-Object System.Text.UTF8Encoding($false); $r=ConvertFrom-Json ([Console]::In.ReadToEnd()); & ([scriptblock]::Create($r.Source)) -InMemory -ListOnly:([bool]$r.ListOnly) -QueueName ([string]$r.QueueName)";
        var start = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe"),
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var arg in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(command)) }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new IOException();
        try
        {
            var stdout = process.StandardOutput.ReadToEndAsync(ct);
            var stderr = process.StandardError.ReadToEndAsync(ct);
            process.StandardInput.Write(JsonSerializer.Serialize(new { Source = reader.ReadToEnd(), ListOnly = listOnly, QueueName = queueName ?? "" }));
            process.StandardInput.Close();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(45));
            process.WaitForExitAsync(timeout.Token).GetAwaiter().GetResult();
            string json = stdout.GetAwaiter().GetResult();
            _ = stderr.GetAwaiter().GetResult(); // never copy raw local errors to a report
            if (process.ExitCode != 0 || json.Length > 2 * 1024 * 1024) throw new IOException();
            return JsonDocument.Parse(json);
        }
        finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
    }
}
