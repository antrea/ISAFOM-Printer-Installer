using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace Isafom.Collector;

// Only these explicitly constructed DTOs may be serialized. Runtime paths,
// actual names, private bytes and session HMACs live in separate internal objects.
public sealed record ValueEvidence(string Slot, string Name, string Kind, int Bytes, string Meaning, string? SafeValue);
public sealed record AreaEvidence(string Area, string Location, string State, ValueEvidence[] Values, bool Truncated);
public sealed record DevModeEvidence(string Scope, string State, int? PublicBytes, int? PrivateBytes, bool? Monochrome);
public sealed record SchemaEvidence(string Scope, string Feature, string? Option, string State);
public sealed record QueueEvidence(string Alias, AreaEvidence[] Registry, DevModeEvidence[] DevModes, SchemaEvidence[] PrintSchema);
public sealed record PortableCapture(DateTime GeneratedUtc, JsonElement WindowsAndQueue, QueueEvidence[] Evidence);
public sealed record Difference(string Queue, string Source, string Field, string Result);
public sealed record PortableBundle(int SchemaVersion, string ToolVersion, string Scope, string Privacy,
    PortableCapture First, PortableCapture Current, Difference[] Differences, string[] Activity);

internal sealed record RawQueue(string Name, string DriverName);
internal sealed record Fingerprint(string Queue, string Source, string Field, string Digest);
internal sealed record Capture(PortableCapture Portable, Dictionary<string, Fingerprint> Fingerprints);

public static class DiagnosticComparison
{
    internal static Difference[] Compare(Capture first, Capture current)
    {
        var changes = new List<Difference>();
        foreach (var key in first.Fingerprints.Keys.Union(current.Fingerprints.Keys).Order(StringComparer.Ordinal))
        {
            bool left = first.Fingerprints.TryGetValue(key, out var a);
            bool right = current.Fingerprints.TryGetValue(key, out var b);
            var item = b ?? a!;
            changes.Add(new(item.Queue, item.Source, item.Field,
                !left ? "Comparso" : !right ? "Non più leggibile o rimosso" :
                a!.Digest == b!.Digest ? "Invariato" : "Contenuto cambiato"));
        }
        // Safe, public facts are compared by their paths, with no untrusted import.
        CompareElement(first.Portable.WindowsAndQueue, current.Portable.WindowsAndQueue, "", changes);
        return changes.ToArray();
    }

    private static void CompareElement(JsonElement first, JsonElement current, string path, List<Difference> changes)
    {
        if (first.ValueKind == JsonValueKind.Object && current.ValueKind == JsonValueKind.Object)
        {
            if (first.TryGetProperty("State", out var a) && current.TryGetProperty("State", out var b))
            {
                string result = "Non confrontabile";
                if (a.GetString() == "Verificata" && b.GetString() == "Verificata" &&
                    first.TryGetProperty("Value", out var x) && current.TryGetProperty("Value", out var y))
                    result = x.GetRawText() == y.GetRawText() ? "Uguale" : "Diversa";
                changes.Add(new("—", "Lettura pubblica", path, result));
                return;
            }
            foreach (var p in first.EnumerateObject())
                if (p.Name is not ("GeneratedUtc" or "Scope" or "Privacy" or "RealValidation") && current.TryGetProperty(p.Name, out var q))
                    CompareElement(p.Value, q, path.Length == 0 ? p.Name : path + "/" + p.Name, changes);
        }
        // Never pair queue arrays by ordinal: order/count changes can alias another queue.
    }

    internal static Difference[] CompareQueues(Capture first, Capture current, RawQueue[] firstQueues, RawQueue[] currentQueues)
    {
        var result = Compare(first, current).ToList();
        if (!first.Portable.WindowsAndQueue.TryGetProperty("Queues", out var a) ||
            !current.Portable.WindowsAndQueue.TryGetProperty("Queues", out var b)) return result.ToArray();
        for (int i = 0; i < currentQueues.Length; i++)
        {
            int previous = Array.FindIndex(firstQueues, q => q == currentQueues[i]);
            if (previous >= 0 && previous < a.GetArrayLength() && i < b.GetArrayLength())
                CompareElement(a[previous], b[i], current.Portable.Evidence.ElementAtOrDefault(i)?.Alias ?? $"Q{i + 1:D2}", result);
        }
        foreach (var queue in current.Portable.Evidence)
        {
            var before = first.Portable.Evidence.FirstOrDefault(q => q.Alias == queue.Alias);
            if (before is null) continue;
            var left = before.PrintSchema.GroupBy(f => (f.Scope, f.Feature)).ToDictionary(g => g.Key, g => g.Where(f => f.Option is not null).Select(f => f.Option!).Order(StringComparer.Ordinal).ToArray());
            var right = queue.PrintSchema.GroupBy(f => (f.Scope, f.Feature)).ToDictionary(g => g.Key, g => g.Where(f => f.Option is not null).Select(f => f.Option!).Order(StringComparer.Ordinal).ToArray());
            foreach (var field in left.Keys.Union(right.Keys))
            {
                bool had = left.TryGetValue(field, out var x), has = right.TryGetValue(field, out var y);
                result.Add(new(queue.Alias, "PrintSchema / " + field.Scope, field.Feature,
                    !had ? "Comparso" : !has ? "Non più leggibile o rimosso" :
                    x!.Length == 0 || y!.Length == 0 ? "Non confrontabile" :
                    x.SequenceEqual(y) ? "Simboli uguali; significato da validare" : "Simboli diversi; significato da validare"));
            }
        }
        return result.ToArray();
    }
}

public static class ReportWriter
{
    public static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    public static void Export(string path, PortableBundle bundle)
    {
        // CreateNew: do not replace an existing report, configuration or arbitrary file.
        using var output = new FileStream(Path.GetFullPath(path), FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var archive = new ZipArchive(output, ZipArchiveMode.Create);
        Write(archive, "Raccolta.json", JsonSerializer.Serialize(bundle, JsonOptions));
        Write(archive, "Raccolta.md", ToMarkdown(bundle));
        Write(archive, "Attivita.txt", string.Join(Environment.NewLine, bundle.Activity));
    }
    private static void Write(ZipArchive archive, string name, string text)
    {
        using var writer = new StreamWriter(archive.CreateEntry(name, CompressionLevel.Optimal).Open(), new UTF8Encoding(false));
        writer.Write(text);
    }
    public static string ToMarkdown(PortableBundle b)
    {
        var s = new StringBuilder("# Raccolta impostazioni Kyocera ISAFOM\n\n");
        s.AppendLine($"Versione {b.ToolVersion}; UTC {b.Current.GeneratedUtc:O}\n");
        s.AppendLine(b.Scope + "\n\n" + b.Privacy + "\n");
        s.AppendLine("Le letture rilevano la configurazione locale. Nessuna stampa né verifica fisica. Un dato mancante non significa disabilitato.\n");
        s.AppendLine("## Configurazione Windows e coda\n");
        if (b.Current.WindowsAndQueue.TryGetProperty("QueueDiscovery", out var discovery))
        {
            s.AppendLine($"- Code complessivamente rilevate: {discovery.GetProperty("TotalCount").GetInt32()}; Get-Printer disponibile: {discovery.GetProperty("PrimaryAvailable").GetBoolean()}; Win32_Printer disponibile: {discovery.GetProperty("FallbackAvailable").GetBoolean()}.");
            s.AppendLine("- È stata analizzata la coda scelta localmente; nessun vincolo sul nome del driver. Nomi e porte delle altre code non esportati.\n");
        }
        AppendFacts(s, b.Current.WindowsAndQueue, "");
        foreach (var q in b.Current.Evidence)
        {
            s.AppendLine($"\n## Memorizzazione — {q.Alias}\n");
            s.AppendLine("La presenza di un'area o di un nome non dimostra che il driver la utilizzi. I valori indicati come indizi non certificano Job Accounting o stampa privata.\n");
            foreach (var a in q.Registry)
            {
                s.AppendLine($"- **{a.Area}** — `{a.Location}`: {a.State}; dati limitati: {a.Truncated}.");
                foreach (var v in a.Values)
                    s.AppendLine($"  - {v.Slot}: {v.Name}; {v.Kind}; {v.Bytes} byte; {v.Meaning}" + (v.SafeValue is null ? "." : $"; valore consentito: {v.SafeValue}."));
            }
            s.AppendLine("\n### DEVMODE — solo dimensioni e colore pubblico\n");
            foreach (var d in q.DevModes)
                s.AppendLine($"- {d.Scope}: {d.State}; parte pubblica {d.PublicBytes?.ToString() ?? "?"} byte; parte privata {d.PrivateBytes?.ToString() ?? "?"} byte; monocromatico {d.Monochrome?.ToString() ?? "non verificabile"}.");
            s.AppendLine("\n### PrintTicket / PrintCapabilities — campi consentiti\n");
            foreach (var f in q.PrintSchema) s.AppendLine($"- {f.Scope}: {f.Feature} = {f.Option ?? "valore omesso/non decodificato"}; {f.State}.");
        }
        s.AppendLine("\n## Confronto nella stessa sessione\n");
        s.AppendLine("I confronti del contenuto usano impronte con chiave casuale tenute solo in memoria. Né chiave né impronte sono esportate. Un cambiamento non ne rivela il significato. 'Invariato' non significa 'corretto'.\n");
        if (b.Differences.Length == 0) s.AppendLine("Una sola lettura disponibile; nessun confronto eseguito.");
        foreach (var d in b.Differences) s.AppendLine($"- {d.Queue} / {d.Source} / {d.Field}: **{d.Result}**.");
        s.AppendLine("\nIN ATTESA DI VALIDAZIONE REALE: significato dei campi proprietari, Account ID, password privata, pannello, riavvio e nuova sessione.\n");
        s.AppendLine("Assistenza informatica: Andrea Esposito — CNR-ISAFOM Portici");
        return s.ToString();
    }
    private static void AppendFacts(StringBuilder s, JsonElement e, string path)
    {
        if (e.ValueKind == JsonValueKind.Object)
        {
            if (e.TryGetProperty("State", out var state) && e.TryGetProperty("Value", out var v))
            {
                s.AppendLine($"- **{path}**: {state.GetString()} — {(v.ValueKind == JsonValueKind.Null ? "non disponibile" : v.ToString())}");
                return;
            }
            foreach (var p in e.EnumerateObject())
                if (p.Name is not ("Privacy" or "Scope" or "GeneratedUtc" or "RealValidation")) AppendFacts(s, p.Value, path.Length == 0 ? p.Name : path + "/" + p.Name);
        }
        else if (e.ValueKind == JsonValueKind.Array)
        {
            int i = 0;
            foreach (var item in e.EnumerateArray()) AppendFacts(s, item, path + $"/Q{++i:D2}");
        }
    }
}
