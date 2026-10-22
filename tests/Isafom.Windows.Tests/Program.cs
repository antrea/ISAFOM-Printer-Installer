using System.IO;
using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Isafom.Core;
using Isafom.Windows;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        int passed = 0;
        void Check(bool value, string description)
        {
            if (!value) throw new Exception("Assertion failed: " + description);
            passed++; Console.WriteLine("PASS " + description);
        }
        try
        {
            var settings = AdminConfiguration.Load();
            Check(settings.DriverName == "Kyocera TASKalfa 3554ci KX", "Load protected/default administrative settings");
            var machine = new WindowsMachine(settings);
            var before = machine.InspectAsync(CancellationToken.None).GetAwaiter().GetResult();
            var removalPreview=PowerShellBackend.RunAsync<RemovalPlan>(new{Mode="PlanRemoval",Settings=settings},CancellationToken.None).GetAwaiter().GetResult();
            Check(removalPreview.Fingerprint.Length==64&&(removalPreview.DriverFiles??[]).All(f=>f.Sha256.Length==64),"Actual desktop PowerShell preview parses embedded file arrays without nesting or long-path errors");
            Check(before.SupportedWindows && before.X64 && before.SpoolerRunning, "Live Windows 10/11 x64 inventory and spooler read via embedded Windows PowerShell backend");
            var events = new SafeEventLog();
            char[] synthetic = "0099".ToCharArray();
            try
            {
                try
                {
                    new InstallerEngine(machine, new UnavailableKxPreferences(), events).InstallAsync(settings, "Soggetto sintetico", synthetic).GetAwaiter().GetResult();
                    throw new Exception("Unavailable adapter must block");
                }
                catch (InstallerException e) { Check(e.Code == ErrorCode.ProviderUnavailable, "Production adapter blocks installation on real Windows without UAC or mutation"); }
            }
            finally { Array.Clear(synthetic); }
            string Fingerprint(SystemState state) => JsonSerializer.Serialize(new {
                Drivers=state.Drivers.OrderBy(d=>d.Name), Ports=state.Ports.OrderBy(p=>p.Name),
                Queues=state.Queues.OrderBy(q=>q.Name).Select(q=>new { q.Name,q.DriverName,q.PortName,q.Shared }) });
            var after = machine.InspectAsync(CancellationToken.None).GetAwaiter().GetResult();
            Check(Fingerprint(before) == Fingerprint(after), "Queues, drivers and TCP/IP ports unchanged by production fail-closed path");
            // The timestamp's fractional seconds can coincidentally contain a
            // four-digit sentinel. Inspect the structured payload for disclosure.
            var log = string.Join('\n', File.ReadLines(events.PathName).Select(line =>
                line.Contains(" stage=", StringComparison.Ordinal) ? line[(line.IndexOf(" stage=", StringComparison.Ordinal) + 1)..] : line));
            Check(!log.Contains("0099") && !log.Contains("Soggetto sintetico"), "Actual application log contains neither synthetic Account ID nor personal name");
            var summary = ReadOnlyVerification.RunAsync(settings).GetAwaiter().GetResult();
            Check(summary.ProprietaryStatus.StartsWith("Non verificabile", StringComparison.Ordinal), "Live verifier cannot certify proprietary preferences");
            var app = new Application();
            var window = new MainWindow();
            Check(window.Title == "Installazione stampante ISAFOM", "WPF Italian window instantiated");
            var name = (TextBox)window.FindName("UserName");
            var account = (PasswordBox)window.FindName("Account");
            var reveal = (TextBlock)window.FindName("Revealed");
            Check(name.Text.Length == 0, "Name is not generated from the Windows account");
            Check(account.MaxLength == 4 && account.Password.Length == 0, "Account input is masked, empty and limited to four digits");
            ((Button)window.FindName("RevealButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(reveal.Text.Length == 0, "Empty Account ID reveal contains no stored code");
            account.Password = "0099";
            ((Button)window.FindName("RevealButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(reveal.Text == "0099", "Requested reveal preserves the complete synthetic code and leading zeros");
            var clock = Stopwatch.StartNew();
            var frame = new DispatcherFrame();
            var pump = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
            pump.Tick += (_, _) => { if (reveal.Text.Length == 0 || clock.Elapsed > TimeSpan.FromSeconds(8)) frame.Continue = false; };
            pump.Start(); Dispatcher.PushFrame(frame); pump.Stop();
            Check(clock.Elapsed.TotalSeconds >= 4.5 && ((Border)window.FindName("RevealedBorder")).Visibility == Visibility.Collapsed,
                "Five-second reveal timer conceals the code through the actual WPF dispatcher");
            Check(reveal.Text.Length == 0, "Revealed text is cleared after timer expiry");
            // No asynchronous install is triggered by this invalid name. Keep the
            // masked ID for retry/verification, as explicitly requested by the user.
            name.Text = "  "; account.Password = "0099";
            ((Button)window.FindName("InstallButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(((TextBlock)window.FindName("StatusTitle")).Text == "Operazione non completata", "GUI validation rejects empty name and never displays completion");
            Check(account.Password == "0099" && reveal.Text.Length == 0, "Account remains masked and available after validation error for retry and verification");
            if(before.Queues.All(q=>q.Name!=settings.PrinterName))
            {
                name.Text="Utente sentinella";account.Password="0099";
                var priorContext=SynchronizationContext.Current;
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
                ((Button)window.FindName("VerifyButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var verifyClock=Stopwatch.StartNew();var verifyFrame=new DispatcherFrame();
                var verifyPump=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(50)};
                verifyPump.Tick+=(_,_)=>{if(((Button)window.FindName("VerifyButton")).IsEnabled||verifyClock.Elapsed>TimeSpan.FromSeconds(30))verifyFrame.Continue=false;};
                verifyPump.Start();Dispatcher.PushFrame(verifyFrame);verifyPump.Stop();
                SynchronizationContext.SetSynchronizationContext(priorContext);
                Check(((TextBlock)window.FindName("StatusText")).Text.Contains("PrinterMissing"),"Actual Verify click after absent installation reports PrinterMissing instead of Unexpected");
                var verifiedState=machine.InspectAsync(CancellationToken.None).GetAwaiter().GetResult();
                Check(Fingerprint(before)==Fingerprint(verifiedState),"Missing-printer verification preserves all queues, drivers and ports");
            }
            account.Password = "0099";
            ((Button)window.FindName("RevealButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            window.Close();
            Check(reveal.Text.Length == 0 && account.Password.Length == 0, "Closing the window clears revealed text and masked input");
            app.Shutdown();
            Console.WriteLine($"RESULT {passed} Windows/UI/privacy checks passed; no printer installed and no print sent.");
            return 0;
        }
        catch (Exception e)
        {
            Console.WriteLine("FAIL Windows integration: " + e.GetType().Name);
            if(e is InstallerException known)Console.WriteLine("Controlled code: "+known.Code);
            if (e.Message.StartsWith("Assertion failed:", StringComparison.Ordinal)) Console.WriteLine(e.Message);
            return 1;
        }
    }
}
