using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Isafom.Collector;

public sealed class CollectorSession : IDisposable
{
    private readonly SessionFingerprints hashes = new();
    private readonly List<string> activity = [];
    private Capture? first;
    private Capture? current;
    private RawQueue[] firstQueues = [];
    private RawQueue[] currentQueues = [];
    private readonly Dictionary<string, string> aliases = new(StringComparer.OrdinalIgnoreCase);
    private bool busy;
    private bool disposed;
    public QueueChoice[] AvailableQueues { get; private set; } = [];
    public QueueChoice? SelectedQueue { get; private set; }
    public bool DiscoveryAvailable { get; private set; }
    public bool HasCapture => current is not null;
    public event Action<string>? Progress;
    public int QueueCount => currentQueues.Length;
    public bool InventoryAvailable => current?.Portable.WindowsAndQueue.GetProperty("QueueCount").GetProperty("State").GetString() == "Verificata";
    public string[] LocalQueueNames => currentQueues.Select(q => q.Name).ToArray();
    public PortableCapture? Current => current?.Portable;

    public Task RefreshQueuesAsync(CancellationToken ct = default) => RunBackground(() =>
    {
        Log("Ricerca di tutte le code: Get-Printer e Win32_Printer, senza filtro sul nome del driver.");
        using var payload = DiagnosticBackend.Run(true, null, ct);
        DiscoveryAvailable = payload.RootElement.GetProperty("InventoryAvailable").GetBoolean();
        AvailableQueues = payload.RootElement.GetProperty("RuntimeQueues").EnumerateArray().Select(q =>
            new QueueChoice(q.GetProperty("Name").GetString()!, q.GetProperty("DriverName").GetString()!, q.GetProperty("PortName").GetString()!, q.GetProperty("ModelHint").GetBoolean(), q.GetProperty("Source").GetString()!)).ToArray();
        if (AvailableQueues.Length > 1024) throw new IOException();
        if (SelectedQueue is not null && !AvailableQueues.Contains(SelectedQueue)) { SelectedQueue = null; ClearCapture(); }
        Log(DiscoveryAvailable ? $"Elenco disponibile: {AvailableQueues.Length} code. Scegliere quella da analizzare." : "Entrambe le fonti di inventario non disponibili; nessuna assenza dedotta.");
    }, ct);

    // Metadata-only endpoint check for the reversed readback. Deliberately does
    // not inspect private DEVMODE, PrintTicket or capabilities through a vendor
    // provider. The diagnostic backend still reads the public color header.
    public async Task<JsonElement> ReadSelectedQueueDetailsAsync(CancellationToken ct = default)
    {
        JsonElement? report = null;
        await RunBackground(() =>
        {
            var selected = SelectedQueue ?? throw new InvalidOperationException();
            using var payload = DiagnosticBackend.Run(false, selected.Name, ct);
            var runtime = payload.RootElement.GetProperty("RuntimeQueues").EnumerateArray().ToArray();
            if (runtime.Length != 1 || runtime[0].GetProperty("Name").GetString() != selected.Name) throw new IOException();
            var portable = payload.RootElement.GetProperty("Report");
            if (portable.GetProperty("Queues").GetArrayLength() != 1) throw new IOException();
            report = portable.Clone();
        }, ct);
        return report ?? throw new IOException();
    }

    public void SelectQueue(QueueChoice? queue)
    {
        if (busy) throw new InvalidOperationException();
        if (queue is not null && !AvailableQueues.Contains(queue)) throw new InvalidOperationException();
        if (SelectedQueue == queue) return;
        SelectedQueue = queue; ClearCapture();
        Log(queue is null ? "Nessuna coda selezionata." : "Coda selezionata localmente; nome e driver non registrati nel log.");
    }
    private void ClearCapture() { first = current = null; firstQueues = currentQueues = []; }

    public Task CollectAsync(CancellationToken ct = default)
    {
        if (SelectedQueue is null) throw new InvalidOperationException("Scegliere una stampante dall'elenco.");
        return RunBackground(() => Collect(ct), ct);
    }

    private Task RunBackground(Action work, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (busy) throw new InvalidOperationException("Raccolta in corso");
        busy = true;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { work(); Finish(); completion.SetResult(); }
            catch (OperationCanceledException) { Finish(); completion.SetCanceled(ct); }
            catch { Log("Raccolta interrotta: errore di lettura. Nessun dettaglio personale registrato."); Finish(); completion.SetException(new IOException("Raccolta non completata.")); }
        }) { IsBackground = true, Name = "ISAFOM read-only collector" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;

        void Finish()
        {
            lock (hashes) { busy = false; if (disposed) hashes.Dispose(); }
        }
    }

    private void Collect(CancellationToken ct)
    {
        current = null; currentQueues = [];
        Log("Inizio lettura locale. Nessuna modifica alla configurazione.");
            var selected = SelectedQueue ?? throw new InvalidOperationException();
            using var payload = DiagnosticBackend.Run(false, selected.Name, ct);
            var safeReport = JsonNode.Parse(payload.RootElement.GetProperty("Report").GetRawText())!;
            var queues = payload.RootElement.GetProperty("RuntimeQueues").EnumerateArray().Select(q => new RawQueue(q.GetProperty("Name").GetString()!, q.GetProperty("DriverName").GetString()!)).ToArray();
            if (queues.Length != 1 || !string.Equals(queues[0].Name, selected.Name, StringComparison.OrdinalIgnoreCase)) throw new IOException();
            bool inventoryAvailable = safeReport["QueueCount"]?["State"]?.GetValue<string>() == "Verificata";
            if (!inventoryAvailable || safeReport["Queues"]?.AsArray().Count != 1) throw new IOException();
            Log("Controverifica selezione riuscita: acquisita esattamente la coda richiesta, senza filtro sul driver.");
            var fingerprints = new Dictionary<string, Fingerprint>(StringComparer.OrdinalIgnoreCase);
            var evidence = new List<QueueEvidence>();
            for (int i = 0; i < queues.Length; i++)
            {
                ct.ThrowIfCancellationRequested();
                if (!aliases.TryGetValue(queues[i].Name, out string? alias)) aliases[queues[i].Name] = alias = $"Q{aliases.Count + 1:D2}";
                safeReport["Queues"]![i]!["Alias"] = alias;
                Log(alias + ": lettura aree del registro globali e dell'utente corrente.");
                var areas = new StorageReader(hashes, fingerprints).Read(queues[i], alias);
                ct.ThrowIfCancellationRequested();
                Log(alias + ": lettura DEVMODE globale/utente; contenuti riservati solo in memoria.");
                var devModes = new[] { DevModeReader.Read(queues[i].Name, alias, 8, hashes, fingerprints), DevModeReader.Read(queues[i].Name, alias, 9, hashes, fingerprints) };
                ct.ThrowIfCancellationRequested();
                Log(alias + ": lettura PrintTicket e opzioni supportate, filtro dei soli campi consentiti.");
                var schema = PrintSchemaReader.Read(queues[i].Name);
                evidence.Add(new(alias, areas, devModes, schema));
            }
            var captured = new Capture(new(DateTime.UtcNow, JsonSerializer.SerializeToElement(safeReport), evidence.ToArray()), fingerprints);
            current = captured;
            currentQueues = queues;
            if (first is null) { first = captured; firstQueues = queues; }
            Log("Lettura completata. Il rapporto è in memoria; usare Esporta ZIP per salvarlo.");
    }

    public PortableBundle Bundle()
    {
        if (current is null || first is null || busy) throw new InvalidOperationException();
        return new(2, "0.3.1-dev", "Raccolta di sola lettura della coda scelta e del solo profilo Windows in uso. Nessuna stampa, installazione, modifica di preferenze, registro o Spooler.",
            "Nessun Account ID, PIN, password, nome personale, nome PC, SID, percorso reale della coda, hive o blob DEVMODE/PrintTicket esportato. I nomi di campi non riconosciuti sono omessi. Le impronte e la relativa chiave casuale restano in memoria.",
            first.Portable, current.Portable, ReferenceEquals(first, current) ? [] : DiagnosticComparison.CompareQueues(first, current, firstQueues, currentQueues), activity.ToArray());
    }
    public void ResetBaseline()
    {
        if (busy || current is null) throw new InvalidOperationException();
        first = current; firstQueues = currentQueues;
        Log("Lettura corrente impostata come riferimento della sessione.");
    }
    private void Log(string message)
    {
        string entry = DateTime.UtcNow.ToString("HH:mm:ss 'UTC'") + "  " + message;
        activity.Add(entry);
        try { Progress?.Invoke(entry); } catch { /* A closed UI must not interrupt cleanup. */ }
    }
    public void Dispose()
    {
        // A window can close while a driver read is still returning. Delay key
        // clearing until that read has finished; never race an HMAC operation.
        lock (hashes) { disposed = true; if (!busy) hashes.Dispose(); }
    }
}
