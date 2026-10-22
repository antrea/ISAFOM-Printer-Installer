using System.IO;
using System.IO.Compression;
using System.Printing;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Isafom.Collector;

internal static class Program
{
    private static int passed;
    // Generated only in memory: the source-return archive carries no fixed
    // synthetic identity or credential value.
    private static readonly string Secret = "0" + System.Security.Cryptography.RandomNumberGenerator.GetInt32(10_000_000).ToString("D7", System.Globalization.CultureInfo.InvariantCulture);
    private static readonly string Personal = "TEST_" + Guid.NewGuid().ToString("N")[..20];
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            bool offlineOnly = args.Length == 1 && args[0] == "--offline-only";
            if (args.Length != 0 && !offlineOnly)
                throw new ArgumentException("Unsupported test arguments");
            TestFiltering();
            TestComparisonAndExport();
            ConfigurationTests.Run(Assert, Secret, Personal);
            if (offlineOnly)
            {
                Console.WriteLine($"RESULT {passed} offline privacy/export checks passed. No printer API called or windows shown.");
                return 0;
            }
            TestReadOnlyNative();
            WindowLayoutTests.Geometry(Assert);
            TestGui();
            TestLiveCapture();
            Console.WriteLine($"RESULT {passed} collector checks passed. No configuration changes or print jobs.");
            return 0;
        }
        catch (Exception error)
        {
            Console.WriteLine(error.Message.StartsWith("FAIL ", StringComparison.Ordinal) ? error.Message : "FAIL " + error.GetType().Name);
            return 1;
        }
    }
    private static void Assert(bool condition, string name)
    {
        if (!condition) throw new Exception("FAIL " + name);
        passed++; Console.WriteLine("PASS " + name);
    }
    private static void TestFiltering()
    {
        Assert(StorageNamePolicy.Canonical("JobAccounting") == "JobAccounting", "Known registry label retained");
        Assert(StorageNamePolicy.Canonical(Secret) == "Nome non esportato", "Numeric registry name omitted");
        Assert(StorageNamePolicy.Canonical(Personal) == "Nome non esportato", "Personal registry name omitted");
        Assert(StorageNamePolicy.SafeBoolean("AccountID", 4, BitConverter.GetBytes(1)) is null, "Account field never exported even if numeric 1");
        Assert(StorageNamePolicy.SafeBoolean("Password", 4, BitConverter.GetBytes(0)) is null, "Password field never exported even if numeric 0");
        Assert(StorageNamePolicy.SafeBoolean("JobAccounting", 4, BitConverter.GetBytes(1))?.StartsWith("1 ", StringComparison.Ordinal) == true, "Known DWORD flag exported only as an unvalidated clue");
        Assert(StorageNamePolicy.SafeBoolean("JobAccounting", 4, BitConverter.GetBytes(88)) is null, "Nonboolean vendor numbers withheld");
        Assert(StorageNamePolicy.SafeBoolean("JobAccounting", 1, Encoding.Unicode.GetBytes(Secret)) is null, "String contents withheld");
        string xml = $"""
          <psf:PrintTicket xmlns:psf="http://schemas.microsoft.com/windows/2003/08/printing/printschemaframework"
           xmlns:k="http://schemas.microsoft.com/windows/2003/08/printing/printschemakeywords" xmlns:v="https://vendor.invalid/{Personal}">
           <psf:Feature name="k:PageOutputColor"><psf:Option name="k:Monochrome"/></psf:Feature>
           <psf:Feature name="v:PrivatePrint"><psf:Option name="v:PrivatePrint"/></psf:Feature>
           <psf:Feature name="v:JobAccounting"><psf:Option name="v:UseSpecificID"/></psf:Feature>
           <psf:ParameterInit name="v:JobAccountID"><psf:Value>{Secret}</psf:Value></psf:ParameterInit>
           <psf:ParameterInit name="v:JobStorageAccessCode"><psf:Value>{Secret}</psf:Value></psf:ParameterInit>
           <psf:ParameterInit name="v:JobStorageUseAccessCode"><psf:Value>Yes</psf:Value></psf:ParameterInit>
           <psf:Feature name="v:UserName"><psf:Option name="v:{Personal}"/></psf:Feature>
           <psf:Feature name="v:Password"><psf:Option name="v:Off"/></psf:Feature>
           <psf:Feature name="v:AccountID"><psf:Option name="v:{Secret}"/></psf:Feature>
           <psf:ParameterInit name="v:AccountID"><psf:Value>{Secret}</psf:Value></psf:ParameterInit>
           <psf:ParameterInit name="v:PageDevmodeSnapshot"><psf:Value>{Secret}</psf:Value></psf:ParameterInit>
           <psf:Feature name="v:{Personal}"><psf:Option name="v:On"/></psf:Feature>
          </psf:PrintTicket>
          """;
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        var fields = PrintSchemaReader.Filter(stream, "Synthetic");
        string filtered = JsonSerializer.Serialize(fields);
        Assert(!filtered.Contains(Secret) && !filtered.Contains(Personal) && !filtered.Contains("PageDevmodeSnapshot"), "PrintSchema omits all synthetic personal data and private snapshots");
        Assert(fields.Any(f => f.Feature == "PageOutputColor (standard Windows)" && f.Option == "Monochrome"), "Standard color option decoded");
        Assert(fields.Any(f => f.Feature.StartsWith("PrivatePrint (", StringComparison.Ordinal) && f.Option == "PrivatePrint"), "Known private-print symbol recorded as a clue");
        Assert(fields.Any(f => f.Feature.StartsWith("JobAccounting (", StringComparison.Ordinal) && f.Option == "UseSpecificID"), "Installed KX specific-account option retained without an account value");
        Assert(new[] { "JobAccountID", "JobStorageAccessCode", "JobStorageUseAccessCode" }.All(name => fields.Any(f => f.Feature == name && f.Option is null)), "KX parameter presence retained while all parameter values are withheld");
        Assert(!filtered.Contains("Yes", StringComparison.Ordinal), "Even nonsecret-looking KX parameter contents remain withheld");
        Assert(fields.Where(f => f.Feature.StartsWith("Password (", StringComparison.Ordinal)).All(f => f.Option is null), "Password option never exported even if it matches a permitted word");
        Assert(!fields.Any(f => f.Feature == "Simbolo non esportato" && f.Option == "On"), "Unknown parent feature cannot smuggle an allowed option");
        bool rejected = false;
        using var unsafeXml = new MemoryStream(Encoding.UTF8.GetBytes("<!DOCTYPE a [<!ENTITY e SYSTEM 'file:///C:/Windows/win.ini'>]><a>&e;</a>"));
        try { PrintSchemaReader.Filter(unsafeXml, "Synthetic"); } catch (System.Xml.XmlException) { rejected = true; }
        Assert(rejected, "XML DTD/external entities prohibited");
    }
    private static JsonElement Report(bool enabled, int port = 9100)
    {
        object Finding(object? value, string state = "Verificata") => new { State = state, Value = value };
        return JsonSerializer.SerializeToElement(new
        {
            Environment = new { SpoolerRunning = Finding(enabled) },
            Queues = new[] { new { Port = new { PortNumber = Finding(port) }, Proprietary = new { JobAccounting = Finding(null, "Non verificabile") } } }
        });
    }
    private static void TestComparisonAndExport()
    {
        using var hashes = new SessionFingerprints();
        using var other = new SessionFingerprints();
        var bytes = Encoding.UTF8.GetBytes(Secret);
        string digest = hashes.Compute(bytes);
        Assert(digest != other.Compute(bytes), "Content fingerprints differ across sessions");
        Assert(digest == hashes.Compute(bytes), "Content fingerprints stable within one session");
        string key = Personal + "\\" + Secret;
        var first = new Capture(new(DateTime.UtcNow, Report(true), []), new() { [key] = new("Q01", "Coda globale", "K01/V001", digest) });
        var second = new Capture(new(DateTime.UtcNow, Report(false, 515), []), new() { [key] = new("Q01", "Coda globale", "K01/V001", hashes.Compute(Encoding.UTF8.GetBytes("changed"))) });
        RawQueue[] queues = [new(Personal, "Kyocera TASKalfa 3554ci KX")];
        var differences = DiagnosticComparison.CompareQueues(first, second, queues, queues);
        Assert(differences.Any(d => d.Result == "Contenuto cambiato"), "Changed private content detected without exporting bytes");
        Assert(differences.Any(d => d.Field.EndsWith("PortNumber", StringComparison.Ordinal) && d.Result == "Diversa"), "Public port difference detected");
        Assert(differences.Any(d => d.Field.EndsWith("JobAccounting", StringComparison.Ordinal) && d.Result == "Non confrontabile"), "Unknown proprietary state is never equal/disabled");
        var noPair = DiagnosticComparison.CompareQueues(first, second, queues, [new("Different", queues[0].DriverName)]);
        Assert(!noPair.Any(d => d.Field.EndsWith("PortNumber", StringComparison.Ordinal)), "Different queues never paired by anonymous ordinal");
        var schemaLeft = first with { Portable = first.Portable with { Evidence = [new("Q01", [], [], [new("Utente", "PrivatePrint", "Off", "indizio")])] } };
        var schemaRight = second with { Portable = second.Portable with { Evidence = [new("Q01", [], [], [new("Utente", "PrivatePrint", "PrivatePrint", "indizio")])] } };
        var schemaDiff = DiagnosticComparison.CompareQueues(schemaLeft, schemaRight, queues, queues);
        Assert(schemaDiff.Any(d => d.Field == "PrivatePrint" && d.Result.StartsWith("Simboli diversi", StringComparison.Ordinal)), "Safe PrintSchema option changes compared");
        var schemaUnknown = schemaRight with { Portable = schemaRight.Portable with { Evidence = [new("Q01", [], [], [new("Utente", "PrivatePrint", null, "omesso")])] } };
        Assert(DiagnosticComparison.CompareQueues(schemaUnknown, schemaUnknown, queues, queues).Any(d => d.Field == "PrivatePrint" && d.Result == "Non confrontabile"), "Omitted PrintSchema options never treated as equal");
        var bundle = new PortableBundle(2, "test", "read-only", "privacy", first.Portable, second.Portable, differences, ["Synthetic test"]);
        var dir = Path.Combine(Root(), "artifacts", "collector-tests-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "fixture.zip");
        ReportWriter.Export(path, bundle);
        using (var archive = ZipFile.OpenRead(path))
        {
            Assert(archive.Entries.Select(x => x.FullName).Order().SequenceEqual(new[] { "Attivita.txt", "Raccolta.json", "Raccolta.md" }), "ZIP contains only the three allowlisted diagnostic files");
            foreach (var entry in archive.Entries)
            {
                using var reader = new StreamReader(entry.Open()); string text = reader.ReadToEnd();
                Assert(!text.Contains(Secret) && !text.Contains(Personal) && !text.Contains(digest), "No synthetic secret, queue name or fingerprint in " + entry.FullName);
            }
        }
        bool noOverwrite = false;
        try { ReportWriter.Export(path, bundle); } catch (IOException) { noOverwrite = true; }
        Assert(noOverwrite, "Export never overwrites an existing file");
    }
    private static void TestReadOnlyNative()
    {
        using var server = new LocalPrintServer(PrintSystemDesiredAccess.EnumerateServer);
        using var queues = server.GetPrintQueues();
        string before = Inventory(server);
        var pdf = queues.FirstOrDefault(q => q.QueueDriver.Name == "Microsoft Print To PDF");
        if (pdf is not null)
        {
            using var hashes = new SessionFingerprints();
            var prints = new Dictionary<string, Fingerprint>();
            var global = DevModeReader.Read(pdf.Name, "TEST", 8, hashes, prints);
            var user = DevModeReader.Read(pdf.Name, "TEST", 9, hashes, prints);
            Assert(global.PublicBytes >= 94, "Native global DEVMODE getter works on existing Microsoft PDF queue");
            Assert(user.State != "Non verificabile", "Native user DEVMODE getter works on existing Microsoft PDF queue");
            var schema = PrintSchemaReader.Read(pdf.Name);
            Assert(schema.Any(f => f.Feature.StartsWith("PageOutputColor (", StringComparison.Ordinal)), "Real PrintTicket/Capabilities reader works on existing Microsoft PDF queue");
            var storage = new StorageReader(hashes, prints).Read(new(pdf.Name, "Microsoft Print To PDF"), "TEST");
            Assert(storage.Any(s => s.Area == "Coda globale" && s.Values.Length != 0), "Real registry reader finds only selected queue metadata");
            Assert(!JsonSerializer.Serialize(storage).Contains(pdf.Name), "Raw selected queue name absent from registry report");
        }
        Assert(Inventory(server) == before, "Native, PrintSchema and registry reads preserve printer configuration/default");
    }
    private static string Inventory(LocalPrintServer server)
    {
        using var queues = server.GetPrintQueues();
        using var defaultQueue = LocalPrintServer.GetDefaultPrintQueue();
        return JsonSerializer.Serialize(new { Default = defaultQueue?.Name, Queues = queues.OrderBy(q => q.Name).Select(q => new { q.Name, Driver = q.QueueDriver.Name, Port = q.QueuePort.Name, q.IsShared, q.NumberOfJobs }) });
    }
    private static void TestGui()
    {
        var previousContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var window = new CollectorWindow();
        WindowLayoutTests.NativeAndContent(window, Assert);
        var refresh = (Button)window.FindName("RefreshQueuesButton");
        PumpUntil(() => refresh.IsEnabled, TimeSpan.FromSeconds(55));
        Assert(!((Button)window.FindName("CaptureButton")).IsEnabled, "GUI requires a queue selection before collecting");
        Assert(!((Button)window.FindName("ExportButton")).IsEnabled, "GUI cannot export a nonexistent capture");
        Assert(!((Button)window.FindName("BaselineButton")).IsEnabled, "GUI cannot use a nonexistent baseline");
        var tabs = Find<TabControl>((DependencyObject)window.Content);
        Assert(tabs is not null && tabs.Items.Count == 4, "GUI exposes results, storage, comparison and live log");
        var capture = (Button)window.FindName("CaptureButton");
        var export = (Button)window.FindName("ExportButton");
        var picker = (ComboBox)window.FindName("QueuePicker");
        Assert(picker.Items.Count > 0, "GUI lists real printer queues even when no Kyocera driver is present");
        picker.SelectedItem = picker.Items.OfType<QueueChoice>().Single(q => q.DriverName == "Microsoft Print To PDF");
        Assert(capture.IsEnabled, "GUI accepts explicit selection of a non-Kyocera driver");
        capture.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert(!capture.IsEnabled && !export.IsEnabled, "GUI prevents overlapping collection and export");
        PumpUntil(() => capture.IsEnabled, TimeSpan.FromSeconds(55));
        Assert(export.IsEnabled, "GUI actual collection enables ZIP export");
        Assert(((TextBox)window.FindName("Summary")).Text.Contains("Configurazione Windows e coda"), "GUI shows the actual captured report");
        Assert(((DataGrid)window.FindName("StorageGrid")).Items.Count > 0, "GUI actually shows storage evidence for selected nonmatching driver");
        Assert(((TextBox)window.FindName("LogText")).Text.Contains("Lettura completata"), "GUI receives live collector log");
        capture.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        PumpUntil(() => capture.IsEnabled, TimeSpan.FromSeconds(55));
        Assert(((DataGrid)window.FindName("ComparisonGrid")).Items.Count != 0, "GUI second reading populates comparison grid");
        ((Button)window.FindName("BaselineButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert(((DataGrid)window.FindName("ComparisonGrid")).Items.Count == 0, "GUI baseline action resets comparisons");
        picker.SelectedItem = picker.Items.OfType<QueueChoice>().First(q => q.DriverName != "Microsoft Print To PDF");
        Assert(!export.IsEnabled && ((DataGrid)window.FindName("StorageGrid")).Items.Count == 0, "Changing selected printer invalidates previous export and evidence");
        window.Close();
        SynchronizationContext.SetSynchronizationContext(previousContext);
    }
    private static void PumpUntil(Func<bool> completed, TimeSpan timeout)
    {
        var frame = new DispatcherFrame();
        var limit = DateTime.UtcNow + timeout;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
        timer.Tick += (_, _) => { if (completed() || DateTime.UtcNow >= limit) frame.Continue = false; };
        timer.Start(); Dispatcher.PushFrame(frame); timer.Stop();
        if (!completed()) throw new Exception("FAIL GUI collection timeout");
    }
    private static T? Find<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T value) return value;
        foreach (object child in LogicalTreeHelper.GetChildren(root))
            if (child is DependencyObject d && Find<T>(d) is T found) return found;
        return null;
    }
    private static void TestLiveCapture()
    {
        using var server = new LocalPrintServer(PrintSystemDesiredAccess.EnumerateServer);
        string before = Inventory(server);
        using var session = new CollectorSession();
        bool noSelection = false;
        try { session.CollectAsync().GetAwaiter().GetResult(); } catch (InvalidOperationException) { noSelection = true; }
        Assert(noSelection && !session.HasCapture, "No empty successful capture without explicit selection");
        session.RefreshQueuesAsync().GetAwaiter().GetResult();
        Assert(session.DiscoveryAvailable && session.AvailableQueues.Length > 0, "Actual discovery includes all installed printer queues");
        var selected = session.AvailableQueues.Single(q => q.DriverName == "Microsoft Print To PDF");
        session.SelectQueue(selected);
        session.CollectAsync().GetAwaiter().GetResult();
        Assert(session.HasCapture && session.Bundle().SchemaVersion == 2, "Live PowerShell collector pipe returns a complete safe capture");
        var captured = session.Bundle();
        Assert(captured.Current.Evidence.Length == 1 && captured.Current.Evidence[0].Registry.Length > 0 && captured.Current.Evidence[0].DevModes.Length == 2 && captured.Current.Evidence[0].PrintSchema.Length > 0, "Explicitly selected Microsoft PDF queue has real registry, DEVMODE and PrintSchema evidence");
        var queue = captured.Current.WindowsAndQueue.GetProperty("Queues")[0];
        Assert(queue.GetProperty("DriverVersion").GetProperty("State").GetString() == "Verificata", "Selected driver file version read using real MSFT Path property");
        Assert(!queue.GetProperty("UsesExpectedDriver").GetProperty("Value").GetBoolean(), "Non-Kyocera selection never falsely labeled as expected Kyocera driver");
        Assert(captured.Current.WindowsAndQueue.GetProperty("QueueDiscovery").GetProperty("TotalCount").GetInt32() >= session.AvailableQueues.Length, "Portable report records all-queue count without raw identities");
        string portableText = JsonSerializer.Serialize(captured);
        Assert(!portableText.Contains(selected.Name) && !portableText.Contains(selected.PortName), "Selected nonstandard queue/port names omitted from exported capture");
        Assert(session.Bundle().Differences.Length == 0, "First capture never claims comparison");
        session.CollectAsync().GetAwaiter().GetResult();
        var b = session.Bundle();
        Assert(b.Differences.Any(d => d.Field == "Environment/SpoolerRunning" && d.Result == "Uguale"), "Second capture compares actual public readings");
        Assert(!JsonSerializer.Serialize(b).Contains("RuntimeQueues"), "Runtime queue identities are never exported");
        session.ResetBaseline();
        Assert(session.Bundle().Differences.Length == 0, "Replacing baseline resets comparison");
        Assert(Inventory(server) == before, "Two complete collections preserve queues, drivers, ports, sharing, default and job counts");
    }
    private static string Root()
    {
        // Allow offline builds to keep test artifacts in the requested workspace
        // even when their assemblies are outside the source-tree output folders.
        var overrideRoot = Environment.GetEnvironmentVariable("ISAFOM_TEST_OUTPUT_ROOT");
        if (!string.IsNullOrWhiteSpace(overrideRoot)) return Path.GetFullPath(overrideRoot);
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Directory.Build.props"))) directory = directory.Parent;
        return directory?.FullName ?? throw new IOException();
    }
}
