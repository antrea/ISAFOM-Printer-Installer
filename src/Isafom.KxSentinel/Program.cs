using System.Diagnostics;
using System.IO;
using System.Printing;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Isafom.ConfigExtractor;

namespace Isafom.KxSentinel;

internal sealed record RoundReport(int Round, bool NativeCallsAccepted, WorkerResult Native, Kx85ReversedReadback Readback, bool? GlobalPrivateMode,
    bool? CurrentUserPrivateMode, bool ReopenedReadbackPassed, bool? ManualNameMatches, bool? ManualAccountMatches,
    bool? ManualPrivateCodeAndModeMatch, bool ReferenceConfigurationUnchanged, bool OtherQueuesUnchanged, bool DefaultPrinterUnchanged);
internal sealed record TrialReport(int SchemaVersion, string ToolVersion, string Reason, RoundReport[] Rounds,
    bool? TestQueueRemoved, bool? ReferenceConfigurationUnchanged, bool? OtherQueuesUnchanged, bool? DefaultPrinterUnchanged,
    bool PhysicalPrintPerformed = false, bool PersonalValuesExported = false, bool InstallerReady = false,
    bool ReadOnly = false, bool TestQueueOnly = true, bool QueueCreationAttempted = false,
    bool TestQueueCreated = false, bool NativeWriteAttempted = false, FilePortReport? FilePortCheck = null);

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.SequenceEqual(["--native-write-worker"])) return NativeWorker.Run();
        if (args.SequenceEqual(["--local-lab-readback"])) return LocalLabReadback.Run();
        if (args.SequenceEqual(["--port-probe"]))
        {
            var port = PrinterStore.CheckFilePort();
            Console.WriteLine(JsonSerializer.Serialize(port));
            return port.SafeFilePortAvailable ? 0 : 2;
        }
        if (!args.SequenceEqual(["--sentinel-trial"]) && !args.SequenceEqual(["--preflight"])) return 2;
        Console.OutputEncoding = new UTF8Encoding(false);
        bool preflight = args[0] == "--preflight";
        string? root = null, token = null;
        PrinterInfo? reference = null;
        PrinterStore.SafePrinter? test = null;
        ReferenceSnapshot? before = null;
        var rounds = new List<RoundReport>();
        string reason = "TrialUnavailable";
        bool removed = false;
        bool creationAttempted = false, created = false, nativeAttempted = false;
        FilePortReport? filePort = null;
        (bool Configuration, bool OtherQueues, bool DefaultPrinter)? integrity = null;
        try
        {
            root = Root(); Budget(root);
            string temp = Path.Combine(root, ".lavoro", "tmp"); Directory.CreateDirectory(temp);
            Environment.SetEnvironmentVariable("TEMP", temp); Environment.SetEnvironmentVariable("TMP", temp);
            var inventory = PrinterStore.Inventory();
            var candidates = inventory.Where(PrinterStore.ReferenceEndpoint).ToArray();
            if (candidates.Length != 1) throw new IOException("ReferenceQueueNotUnique");
            reference = candidates[0];
            using (var source = PrinterStore.Open(reference.Name)) _ = PrinterStore.Component(source);
            filePort = PrinterStore.CheckFilePort();
            if (!filePort.SafeFilePortAvailable) throw new IOException(filePort.Reason);
            if (inventory.Any(p => p.Name.StartsWith(TrialPolicy.Prefix, StringComparison.OrdinalIgnoreCase))) throw new IOException("PreviousTestQueuePresent");
            if (preflight) { Console.WriteLine("PASS: driver originale, riferimento univoco, porta FILE: locale. Nessuna modifica."); return 0; }
            using var identity = WindowsIdentity.GetCurrent();
            if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)) throw new IOException("AdministratorRequired");
            Console.WriteLine("ISAFOM — prova sentinelle KX 8.5 (0.1.6 sperimentale)");
            Console.WriteLine("Crea una sola coda temporanea, sospesa, sulla porta locale FILE:.");
            Console.WriteLine("Due serie di dati fittizi. Nessuna stampa o modifica della coda ISAFOM esistente.");
            Console.WriteLine("Durante la prova: ispeziona i valori, poi esci dalle finestre Kyocera con ANNULLA.");
            Console.WriteLine("Premi INVIO per iniziare; digita ESCI per annullare.");
            if (!Continue()) { reason = "UserCancelled"; return 2; }
            before = ReferenceSnapshot.Capture(reference);
            token = Guid.NewGuid().ToString("N");
            creationAttempted = true;
            test = PrinterStore.Create(reference.Driver, token);
            created = true;
            PrinterStore.RequireOwned(test, token);
            string queue = TrialPolicy.QueueName(token);
            Console.WriteLine("Coda di prova: " + queue);
            string? previous = null;
            for (int round = 1; round <= 2; round++)
            {
                integrity = before.Compare(reference, queue);
                if (!integrity.Value.Configuration || !integrity.Value.OtherQueues || !integrity.Value.DefaultPrinter)
                    throw new IOException("ReferenceStateChanged");
                string name = round == 1 ? "ISAFOM_SENTINELLA_A" : "ISAFOM_SENTINELLA_B";
                string code = TrialPolicy.NewCode(previous); previous = code;
                PrinterStore.RequireOwned(test, token);
                Console.WriteLine("Applicazione serie " + round + " alla sola coda di prova e successiva rilettura...");
                nativeAttempted = true;
                WorkerResult native = RunWorker(token, name, code);
                bool nativeAccepted = native.Accepted;
                // Native child has exited and closed/unloaded its context. This
                // independent Windows read opens a fresh printer handle.
                var read = Kx85NativeReadback.Read(queue, name, code);
                var modes = Modes(queue);
                bool passed = TrialPolicy.Matches(read, modes.Global == true, nativeAccepted);
                integrity = before.Compare(reference, queue);
                Console.WriteLine(); Console.WriteLine("SERIE " + round);
                Console.WriteLine("Nome atteso: " + name);
                Console.WriteLine("Account ID e password privata fittizi attesi: " + code);
                Console.WriteLine("Controverifica automatica: " + (passed ? "CORRISPONDE" : "INCOMPLETA — controlla la GUI e il report"));
                Console.WriteLine("Apri le PROPRIETÀ della sola coda " + queue + ".");
                Console.WriteLine("Impostazioni periferica > Utente: controlla Nome utente.");
                Console.WriteLine("Impostazioni periferica > Amministratore > Job Accounting: ID specifico e codice.");
                Console.WriteLine("Avanzate > Predefinite di stampa > Lavoro: Stampa privata, richiesta password disattivata e codice.");
                Console.WriteLine("Controlla anche Preferenze di stampa > Lavoro (utente corrente), se differiscono.");
                bool? manualName = Confirm("Il Nome corrisponde?");
                bool? manualAccount = Confirm("Job Accounting e Account ID corrispondono?");
                bool? manualPrivate = Confirm("Stampa privata, richiesta disattivata e password corrispondono nelle predefinite globali?");
                rounds.Add(new(round, nativeAccepted, native, read,
                    modes.Global, modes.User, passed, manualName, manualAccount, manualPrivate,
                    integrity.Value.Configuration, integrity.Value.OtherQueues, integrity.Value.DefaultPrinter));
                // Do not continue modifying even the test queue if the guarded
                // reference or another queue changed during the experiment.
                if (!integrity.Value.Configuration || !integrity.Value.OtherQueues || !integrity.Value.DefaultPrinter)
                    throw new IOException("ReferenceStateChanged");
                if (round == 1)
                {
                    Console.WriteLine("Chiudi TUTTE le finestre della coda di prova con ANNULLA.");
                    Console.WriteLine("INVIO: applica la seconda serie; ESCI: termina qui e rimuove la sola coda di prova.");
                    if (!Continue()) { reason = "FirstRoundOnly"; break; }
                }
            }
            if (rounds.Count == 2) reason = "TwoRoundsCompleted";
            Console.WriteLine("Chiudi le finestre Kyocera con ANNULLA e premi INVIO per rimuovere la coda di prova e salvare il report.");
            Console.ReadLine();
        }
        catch (Exception error)
        {
            string[] allowed = ["ReferenceQueueNotUnique", "PreviousTestQueuePresent", "AdministratorRequired", "FilePortUnavailable", "TestQueueCreationFailed", "TestQueuePauseFailed", "OwnedPausedQueueRequired", "ReferenceStateChanged", "WorkspaceBudgetExceeded", "NativeWorkerTimeout", "NativeWorkerUnavailable"];
            reason = allowed.Contains(error.Message, StringComparer.Ordinal) ? error.Message :
                filePort is not null && !filePort.SafeFilePortAvailable ? filePort.Reason : "TrialUnavailable";
            Console.WriteLine("Prova interrotta: " + reason + ". Nessun dettaglio personale o errore grezzo salvato.");
        }
        finally
        {
            if (test is not null && token is not null)
            {
                try { removed = PrinterStore.Remove(test, token); }
                catch { Console.WriteLine("Coda di prova lasciata sospesa: controllarla e rimuoverla manualmente. Nessuna coda estranea rimossa."); }
                test.Dispose();
                try { removed = removed && !PrinterStore.Inventory().Any(p => p.Name == TrialPolicy.QueueName(token)); }
                catch { removed = false; }
                if (!removed) Console.WriteLine("Rimozione della coda di prova non confermata: controlla l'elenco stampanti dopo aver chiuso le finestre Kyocera.");
            }
            if (before is not null && reference is not null && token is not null)
            {
                try { integrity = before.Compare(reference, TrialPolicy.QueueName(token)); }
                catch { integrity = null; }
                before.Dispose();
            }
            if (!preflight && root is not null)
            {
                try
                {
                    Budget(root);
                    string folder = Path.Combine(root, "risultati"); Directory.CreateDirectory(folder);
                    string path = Path.Combine(folder, "Prova_Sentinelle_KX_" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + ".json");
                    using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    JsonSerializer.Serialize(output, new TrialReport(2, TrialPolicy.Version, reason, [.. rounds], created ? removed : null,
                        integrity?.Configuration, integrity?.OtherQueues, integrity?.DefaultPrinter,
                        ReadOnly: !creationAttempted, QueueCreationAttempted: creationAttempted, TestQueueCreated: created,
                        NativeWriteAttempted: nativeAttempted, FilePortCheck: filePort), new JsonSerializerOptions { WriteIndented = true });
                    Console.WriteLine("Report senza nomi o PIN inseriti: " + path);
                }
                catch { Console.WriteLine("Report non salvato; nessun dettaglio grezzo esportato."); }
            }
        }
        return reason == "TwoRoundsCompleted" && removed && integrity is { Configuration: true, OtherQueues: true, DefaultPrinter: true } && rounds.All(r => r.ReopenedReadbackPassed) ? 0 : 3;
    }

    private static bool Continue() => Console.ReadLine() is "";
    private static bool? Confirm(string question)
    {
        Console.WriteLine(question + " [S = sì, N = no, INVIO = non verificato]");
        return Console.ReadLine()?.Trim().ToUpperInvariant() switch { "S" => true, "N" => false, _ => null };
    }
    private static (bool? Global, bool? User) Modes(string queueName)
    {
        try
        {
            using var server = new LocalPrintServer(PrintSystemDesiredAccess.EnumerateServer);
            using var queue = new PrintQueue(server, queueName, PrintSystemDesiredAccess.UsePrinter);
            bool? Read(PrintTicket ticket)
            {
                using var stream = ticket.GetXmlStream();
                string mode = PersonalReadbackReader.Read(stream, "Test").StorageMode;
                return mode == "PrivatePrint" ? true : mode is "None" or "Off" or "QuickCopy" or "ProofHold" or "StoredJob" or "JobStorage" ? false : null;
            }
            return (Read(queue.DefaultPrintTicket), Read(queue.UserPrintTicket));
        }
        catch { return (null, null); }
    }
    private static WorkerResult RunWorker(string token, string name, string code)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = new UTF8Encoding(false) };
        if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase)) start.ArgumentList.Add(typeof(Program).Assembly.Location);
        start.ArgumentList.Add("--native-write-worker");
        using var child = Process.Start(start) ?? throw new IOException("NativeWorkerUnavailable");
        try
        {
            var output = child.StandardOutput.ReadToEndAsync(); var errors = child.StandardError.ReadToEndAsync();
            child.StandardInput.WriteLine(JsonSerializer.Serialize(new WorkerRequest(token, name, code))); child.StandardInput.Close();
            if (!child.WaitForExit(90000)) throw new IOException("NativeWorkerTimeout");
            string raw = output.GetAwaiter().GetResult(); _ = errors.GetAwaiter().GetResult();
            if (raw.Length <= 4096)
            {
                WorkerResult? result;
                try { result = JsonSerializer.Deserialize<WorkerResult>(raw); }
                catch (JsonException) { result = null; }
                string[] stages = ["Input", "LoadComponent", "OpenContext", "SetName", "SetAccount", "SetStorage", "Save", "CommitExplicitDefaults", "CloseContext", "ReopenForModeReadback", "Completed"];
                if (result is not null && stages.Contains(result.Stage, StringComparer.Ordinal) && result.Reason is "NativeCallsCompleted" or "NativeCallsUnavailable")
                    return child.ExitCode == 0 || !result.Accepted ? result : result with { Reason = "NativeChildFailed" };
            }
            return new(false, false, false, false, false, "NativeChildFailed");
        }
        finally { if (!child.HasExited) { child.Kill(true); child.WaitForExit(5000); } }
    }
    private static string Root()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "LEGGIMI_SENTINELLE_KX.txt"))) return directory.FullName;
        throw new IOException("WorkspaceRequired");
    }
    private static void Budget(string root)
    {
        long size = 0;
        foreach (string path in Directory.EnumerateFiles(root, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false }))
        { size += new FileInfo(path).Length; if (size > 499L * 1024 * 1024) throw new IOException("WorkspaceBudgetExceeded"); }
    }
}
