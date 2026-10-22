using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Isafom.Core;

namespace Isafom.Windows;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length == 2 && DriverProfiles.Supported(args[1]))
        {
            try
            {
                var selected=DriverProfiles.Select(AdminConfiguration.Load(),args[1]);
                if(args[0]=="--kx-apply") return Isafom.KxSentinel.NativeWorker.Run(request=>Kx85Preferences.Authorize(selected,request));
                if(args[0]=="--kx-verify") return Kx85Preferences.VerifyWorker(settings:selected);
                if(args[0]=="--kx-finish") return Kx85Preferences.VerifyWorker(finish:true,settings:selected);
            }
            catch{return 2;}
        }
        if (args.Length == 3 && args[0] == "--machine-helper" && int.TryParse(args[2], out int parentPid))
            return MachineElevation.HelperAsync(args[1], parentPid).GetAwaiter().GetResult();
        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        app.DispatcherUnhandledException += (_, e) =>
        {
            MessageBox.Show(Messages.For(ErrorCode.Unexpected), "Installazione stampante ISAFOM", MessageBoxButton.OK, MessageBoxImage.Error);
            e.Handled = true;
        };
        try
        {
            if (args.Length == 1 && args[0] == "--validate-config-stdin")
            {
                Console.InputEncoding = System.Text.Encoding.UTF8;
                var text = Console.In.ReadToEnd();
                _ = PrinterSettings.Parse(text);
                return 0;
            }
            if (args.Length == 2 && args[0] == "--validate-config")
            {
                var file = new FileInfo(Path.GetFullPath(args[1]));
                if (file.Length > 16384) return 2;
                _ = PrinterSettings.Parse(File.ReadAllText(file.FullName));
                return 0;
            }
            if (args.Length == 2 && args[0] is "--render-preview" or "--render-debug-preview" or "--render-compact-preview")
            {
                var window = new MainWindow();
                var content = (FrameworkElement)window.Content;
                content.Resources.MergedDictionaries.Add(window.Resources);
                System.Windows.Documents.TextElement.SetFontFamily(content, window.FontFamily);
                System.Windows.Documents.TextElement.SetFontSize(content, window.FontSize);
                content.Language = window.Language;
                if (args[0] == "--render-debug-preview") ((System.Windows.Controls.TabControl)window.FindName("Pages")).SelectedIndex = 1;
                // Detach from the unshown (collapsed) Window so its inherited
                // visibility cannot suppress the offscreen visual tree.
                window.Content = null;
                content.ApplyTemplate();
                int width = args[0] == "--render-compact-preview" ? 760 : 920;
                int height = args[0] == "--render-compact-preview" ? 580 : 660;
                content.Measure(new Size(width, height));
                content.Arrange(new Rect(0, 0, width, height));
                content.UpdateLayout();
                var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(content);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var file = File.Create(Path.GetFullPath(args[1]));
                encoder.Save(file);
                return 0;
            }
            if (args.Length == 2 && args[0] == "--probe-output")
            {
                var report = ReadOnlyVerification.RunAsync(AdminConfiguration.Load()).GetAwaiter().GetResult();
                File.WriteAllText(Path.GetFullPath(args[1]), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
                return 0;
            }
            if (args.Length == 2 && args[0] is "--removal-preview-output" or "--force-removal-preview-output")
            {
                var plan = RemovalService.PreviewAsync(AdminConfiguration.Load(),forceRemoval:args[0]=="--force-removal-preview-output").GetAwaiter().GetResult();
                File.WriteAllText(Path.GetFullPath(args[1]), JsonSerializer.Serialize(plan, new JsonSerializerOptions { WriteIndented = true }));
                return 0;
            }
            if (args.Length != 0) return 2;
            return app.Run(new MainWindow());
        }
        catch (Exception error)
        {
            var code = error is InstallerException known ? known.Code : ErrorCode.Unexpected;
            new SafeEventLog().Record(Stage.Verification, code);
            if (args.Length == 0) MessageBox.Show(Messages.For(code), "Installazione stampante ISAFOM", MessageBoxButton.OK, MessageBoxImage.Error);
            return 1;
        }
    }
}

public sealed record ReadOnlyVerification(bool SupportedWindows, bool X64, bool SpoolerRunning,
    int TargetQueueCount, bool DriverPresent, bool DriverVersionMatches, bool? PortCorrect,
    bool? NotShared, bool? GlobalMonochrome, bool? UserMonochrome, string ProprietaryStatus,
    string? WindowsBuild = null, string? InstalledDriverVersion = null, string? PortHost = null, int? RawPort = null, bool? SnmpEnabled = null)
{
    public static async Task<ReadOnlyVerification> RunAsync(PrinterSettings settings)
    {
        var state = await new WindowsMachine(settings).InspectAsync(CancellationToken.None);
        var queues = state.Queues.Where(q => q.DriverName == settings.DriverName &&
            state.Ports.Any(p => p.Name == q.PortName && Planner.IsTarget(settings, p.Host))).ToArray();
        var queue = queues.Length == 1 ? queues[0] : null;
        var port = queue is null ? null : state.Ports.FirstOrDefault(p => p.Name == queue.PortName);
        var drivers = state.Drivers.Where(d => d.Name == settings.DriverName).ToArray();
        return new(state.SupportedWindows, state.X64, state.SpoolerRunning, queues.Length, drivers.Length != 0,
            drivers.Length == 1 && drivers[0].Version == settings.DriverVersion,
            port is null ? null : Planner.CorrectPort(settings, port), queue is null ? null : !queue.Shared,
            queue is null ? null : NativeColor.Read(queue.Name, 8), queue is null ? null : NativeColor.Read(queue.Name, 9),
            "Non verificabile senza confronto locale con Nome e Account ID inseriti dall'utente.",
            Environment.OSVersion.Version.ToString(), drivers.Length == 1 ? drivers[0].Version : null,
            port?.Host, port?.PortNumber, port?.SnmpEnabled);
    }
    public string ToItalian()
    {
        static string Yes(bool? value) => value is null ? "non verificabile" : value.Value ? "verificato" : "non conforme";
        return $"Windows 10/11 x64: {Yes(SupportedWindows && X64)} — build {WindowsBuild ?? "non rilevata"}\nServizio di stampa: {Yes(SpoolerRunning)}\n" +
            $"Code ISAFOM identificate: {TargetQueueCount}\nDriver KX previsto: {Yes(DriverPresent && DriverVersionMatches)}\n" +
            $"Versione pacchetto installato: {InstalledDriverVersion ?? "non rilevata"}\nHost: {PortHost ?? "non rilevato"}\nPorta: {RawPort?.ToString() ?? "non rilevata"}\nSNMP attivo: {Yes(SnmpEnabled)}\n" +
            $"Porta RAW 9100 e SNMP conformi: {Yes(PortCorrect)}\nCondivisione disabilitata: {Yes(NotShared)}\n" +
            $"Bianco e nero globale: {Yes(GlobalMonochrome)}\nBianco e nero utente: {Yes(UserMonochrome)}\n\n" +
            "Nome utente e codici: usare il confronto con gli input nella scheda Installazione.\n" +
            "IN ATTESA DI VALIDAZIONE REALE: pannello della stampante, rilascio privato e persistenza dopo riavvio.\n" +
            "Per completare la verifica rivolgiti ad Andrea Esposito.";
    }
}
