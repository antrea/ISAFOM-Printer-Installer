using System.Diagnostics;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using Isafom.Core;
using Isafom.KxSentinel;
using Isafom.Windows;

internal static class Tests
{
    [STAThread]
    private static int Main(string[] args)
    {
        string? requestedProfile=null;
        if(args.Length==3&&DriverProfiles.Supported(args[2])){requestedProfile=args[2];args=args[..2];}
        if(args.Length == 2 && args[0] == "--gui") return GuiChecks.Run(args[1]);
        if (args.SequenceEqual(["--lab-worker"])) return NativeWorker.Run();
        if (args.SequenceEqual(["--network"]))
        {
            try
            {
                var endpoint=new WindowsMachine(PrinterSettings.Default()).ResolveAndProbeAsync(PrinterSettings.Default(),CancellationToken.None).GetAwaiter().GetResult();
                Console.WriteLine(JsonSerializer.Serialize(new{Reachable=true,endpoint.UsedFallback,PrintSent=false}));
            }
            catch(InstallerException error){Console.WriteLine(JsonSerializer.Serialize(new{Reachable=false,Reason=error.Code.ToString(),PrintSent=false}));}
            return 0;
        }
        if(args.SequenceEqual(["--selective-removal"])) return SelectiveRemovalChecks.Run();
        if(args.SequenceEqual(["--selective-live"])) return SelectiveRemovalChecks.RunLive();
        int passed = 0;
        void Check(bool value,string label) { if(!value)throw new Exception(label); passed++; }
        try
        {
            var settings = DriverProfiles.Select(PrinterSettings.Default(),requestedProfile??DriverProfiles.DefaultVersion);
            var provider = new Kx85Preferences(settings);
            Check(provider.Support(settings) is { Available:true, ValidatedForExactDriver:true, OriginalUserContextPreserved:true }, "Exact supported version");
            Check(!provider.Support(settings with {DriverVersion="8.5.9999.0"}).Available,"Other versions not enabled");
            Check(provider.Support(settings with {DriverVersion=DriverProfiles.DefaultVersion}).Available,"8.5 remains available as the default");
            Check(provider.Support(settings with {DriverVersion=DriverProfiles.OptionalVersion}).Available,"8.7 is supported only when explicitly selected");
            if(args.SequenceEqual(["--source-audit"]))Console.WriteLine("SKIP Proprietary driver payload checks: source audit without vendor archives.");
            else foreach(string version in new[]{DriverProfiles.DefaultVersion,DriverProfiles.OptionalVersion})
            {
                var profile=DriverProfiles.Select(settings,version);
                using(var payload=DriverPackage.OpenEmbedded(profile))Check(Convert.ToHexString(SHA256.HashData(payload))==profile.DriverArchiveSha256,"Embedded driver matches the configured exact archive hash");
                using var content=DriverPackage.OpenEmbedded(profile);
                using var archive=new ZipArchive(content,ZipArchiveMode.Read);
                Check(archive.Entries.Count==(version==DriverProfiles.DefaultVersion?661:721) && archive.Entries.Sum(e=>e.Length)<128L*1024*1024,"Complete bounded offline x64 driver payload");
                Check(!archive.Entries.Any(e=>e.FullName.Contains("/32bit/")||e.FullName.Contains("/arm64/")),"No unwanted processor variant embedded");
                var inf=archive.GetEntry(profile.InfRelativePath)!;
                using(var stream=inf.Open())Check(Convert.ToHexString(SHA256.HashData(stream))==DriverProfiles.InfSha256(version),"Embedded INF is the official selected x64 model list");
                Check(archive.Entries.Count(e=>e.FullName.Contains("/Setup/EULA/License_"))==2,"Official Italian and English licenses retained");
            }
            provider.ValidateInputs("Èlia D'Ángelo", "0001"); passed++;
            foreach(string code in new[]{"", "1", "123", "12345", "12345678", "１２３４", "١٢٣٤"})
            {
                bool rejected=false;try{provider.ValidateInputs("Sentinella",code);}catch(InstallerException){rejected=true;}
                Check(rejected,"Invalid private-code input rejected before printer API");
            }
            string sid=WindowsIdentity.GetCurrent().User!.Value;
            string marker=Kx85Preferences.UserMarker(sid);
            Check(Kx85Preferences.RecognizedIdentity(marker,null,marker),"Completed legacy comment remains recognized for migration");
            Check(Kx85Preferences.RecognizedIdentity(marker+" pending",null,marker),"Interrupted legacy installation remains recoverable");
            Check(Kx85Preferences.RecognizedIdentity(Kx85Preferences.AuthorComment,marker,marker),"Author comment and matching hidden owner identify the queue");
            Check(!Kx85Preferences.RecognizedIdentity(Kx85Preferences.AuthorComment,null,marker),"Author text alone cannot authorize another printer");
            Check(!Kx85Preferences.RecognizedIdentity(Kx85Preferences.AuthorComment,Kx85Preferences.UserMarker("S-1-5-21-1-2-3-9999"),marker),"Another user's hidden owner cannot authorize the queue");
            Check(!Kx85Preferences.RecognizedIdentity("Unrelated printer",marker,marker),"Hidden owner alone does not adopt unrelated comments");
            string sddl="O:"+sid+"G:SYD:P(A;;0xF000C;;;SY)(A;;0xF000C;;;BA)(A;;0xF000C;;;"+sid+")";
            Check(Kx85Preferences.SecurityOwned(new(sddl),sid),"Dedicated queue grants only owner and system administrators");
            Check(!Kx85Preferences.SecurityOwned(new(sddl+"(A;;0x20008;;;BU)"),sid),"Other user access refused for personal global fields");
            Check(!Kx85Preferences.SecurityOwned(new("O:BAG:SYD:P(A;;0xF000C;;;BA)"),sid),"Another queue owner refused");
            Check(!Kx85Preferences.SecurityOwned(new("O:"+sid+"G:SYD:"),sid),"Empty ACL refused");
            Check(ProcessIdentity.UserSid(Environment.ProcessId)==sid,"Elevated helper binds provisioning owner to requesting process");
            bool foreign=false;try{Kx85Preferences.Authorize(settings,new("Other printer","Sentinella","0001"));}catch(InstallerException){foreign=true;}
            Check(foreign,"Worker cannot select an arbitrary printer");
            if(DriverPackage.TryInstalledInf(settings) is not null)
                Check(true,"Existing verified signed package can be reused without download");
            else Console.WriteLine("SKIP DriverStore reuse: exact Kyocera package is not installed on this PC.");
            var app=new Application();
            var window=new MainWindow();
            var driverChoice=(ComboBox)window.FindName("DriverChoice");
            Check(window.SelectedDriverVersion==DriverProfiles.DefaultVersion,"Every new GUI starts with 8.5 selected");
            driverChoice.SelectedIndex=1;
            Check(window.SelectedDriverVersion==DriverProfiles.OptionalVersion&&((TextBlock)window.FindName("HeaderSubtitle")).Text.Contains("8.7.0917"),"Debug explicitly selects 8.7 and shows it on the main screen");
            driverChoice.SelectedIndex=0;
            Check(window.SelectedDriverVersion==DriverProfiles.DefaultVersion,"Debug can restore 8.5 before installation");
            Check(((PasswordBox)window.FindName("Account")).MaxLength==4,"GUI matches institution four-digit IDs");
            Check(((TabControl)window.FindName("Pages")).Items.Count==2,"Installation and debug pages available");
            Check(window.FindName("CaptureButton") is Button && window.FindName("DebugLog") is TextBox,"Live log and independent capture controls");
            Check(window.FindName("UninstallButton") is Button,"Red uninstall action is available in Debug");
            Check(window.FindName("ForceUninstallButton") is Button,"Explicit force action is available beside ordinary removal");
            var removalPlan=new RemovalPlan(settings.PrinterName,false,0,false,settings.DriverName,settings.DriverVersion,true,true,[],true,null,new string('A',64));
            var removalWindow=new RemovalWindow(removalPlan);
            Check(!removalWindow.RemoveButton.IsEnabled,"Opening the preview cannot immediately delete anything");
            removalWindow.Acknowledgement.IsChecked=true;
            Check(removalWindow.RemoveButton.IsEnabled,"Explicit confirmation enables the reviewed complete removal");
            Check(removalWindow.RemoveButton.Content.ToString()!.Contains("completamente"),"The action explicitly performs a complete reset");
            removalWindow.Close();
            var blockedRemoval=new RemovalWindow(removalPlan with {CanProceed=false,BlockedCode=ErrorCode.ExistingJobs});
            blockedRemoval.Acknowledgement.IsChecked=true;
            Check(!blockedRemoval.RemoveButton.IsEnabled,"Confirmation cannot override blocked jobs");
            blockedRemoval.Close();
            var driverOnly=new RemovalWindow(removalPlan with {QueueName=null});
            driverOnly.Acknowledgement.IsChecked=true;
            Check(driverOnly.RemoveButton.IsEnabled,"Complete reset also handles an already absent queue");
            driverOnly.Close();
            SelectiveRemovalChecks.CheckUi(Check);
            var residueOnly=removalPlan with {DriverPresent=false,QueueName=null,DownloadCachePresent=true};
            Check(!residueOnly.RequiresConfirmation&&residueOnly.HasWork,"Driver-absent residue cleanup needs no redundant application confirmation");
            Check(!(residueOnly with {DownloadCachePresent=false}).HasWork,"An empty removal plan is a no-op before dialog or elevation");
            Check((residueOnly with {DriverRegistrations=[new("Kyocera TASKalfa 5052ci KX","Windows x64")]}).RequiresConfirmation,"Additional driver registrations still require explicit force confirmation");
            var incompleteResult=new RemovalResult(true,true,false,true,true,true,false,true,true,DriverFilesRemoved:true);
            Check(!incompleteResult.Complete,"A retained Driver Store package cannot be reported as a complete reset");
            Check((incompleteResult with {PackageRemoved=true}).Complete,"All readback outcomes are required for complete reset");
            window.Close();app.Shutdown();
            if(args.Length==2&&args[0]=="--full-reset-published")
            {
                string executable=Path.GetFullPath("dist/installer-0.2.7-rc1/Installa_Stampante_ISAFOM.exe");
                MachineElevation.ExecutableOverride=executable;
                var machine=new WindowsMachine(settings);
                var before=machine.InspectAsync(CancellationToken.None).GetAwaiter().GetResult();
                string originalDefault=PrinterStore.DefaultName();
                string Others(SystemState state)=>JsonSerializer.Serialize(state.Queues.Where(q=>q.Name!=settings.PrinterName).OrderBy(q=>q.Name).Select(q=>new{q.Name,q.DriverName,q.PortName,q.Shared}));
                var plan=RemovalService.PreviewAsync(settings).GetAwaiter().GetResult();
                Check(plan.CanProceed&&plan.FullRemoval,"Full reset preview permits only the reviewed unused resources");
                var result=RemovalService.RemoveAsync(settings,plan,allowSpoolerRestart:true).GetAwaiter().GetResult();
                var after=machine.InspectAsync(CancellationToken.None).GetAwaiter().GetResult();
                var fresh=RemovalService.PreviewAsync(settings).GetAwaiter().GetResult();
                bool clean=fresh.QueueName is null&&!fresh.DriverPresent&&(fresh.Packages??[]).Length==0&&
                    (fresh.DriverRegistrations??[]).Length==0&&(fresh.DriverFiles??[]).Length==0&&fresh.PortName is null&&!fresh.DownloadCachePresent&&!fresh.SettingsFilePresent&&!fresh.UserPreferencesPresent;
                using var binary=File.OpenRead(executable);
                File.WriteAllText(args[1],JsonSerializer.Serialize(new{Completed=result.Complete&&clean,Result=result,
                    PackagesReviewed=(plan.Packages??[]).Select(p=>p.Name),RegistrationsReviewed=plan.DriverRegistrations,
                    AfterRemovalNoTargetResources=clean,OtherPrinterMetadataUnchanged=Others(before)==Others(after),
                    DefaultUnchanged=PrinterStore.DefaultName()==originalDefault,PhysicalPrintPerformed=false,
                    InstallerSha256=Convert.ToHexString(SHA256.HashData(binary)),PublishedExecutableUsed=true,PersonalValuesExported=false},new JsonSerializerOptions{WriteIndented=true}));
                Check(result.Complete&&clean,"Real full reset removes every reviewed driver version, package, port, preference and cache");
                Check(Others(before)==Others(after)&&PrinterStore.DefaultName()==originalDefault,"Real full reset preserves unrelated printers and Windows default");
            }
            if(args.Length==2&&args[0] is "--end-to-end" or "--end-to-end-published")
            {
                if(args[0]=="--end-to-end-published")
                {
                    string executable=Path.GetFullPath("dist/installer-0.2.7-rc1/Installa_Stampante_ISAFOM.exe");
                    Kx85Preferences.ExecutableOverride=executable;MachineElevation.ExecutableOverride=executable;
                }
                var machine=new WindowsMachine(settings);
                var before=machine.InspectAsync(CancellationToken.None).GetAwaiter().GetResult();
                if(before.Queues.Any(q=>q.Name==settings.PrinterName))
                {
                    using var existing=PrinterStore.Open(settings.PrinterName);
                    if(PrinterStore.Info(existing).Comment!=Kx85Preferences.UserMarker(sid)+" pending")throw new Exception("Existing target refused by integration test");
                }
                string originalDefault=PrinterStore.DefaultName();
                string othersBefore=JsonSerializer.Serialize(before.Queues.Where(q=>q.Name!=settings.PrinterName).OrderBy(q=>q.Name).Select(q=>new{q.Name,q.DriverName,q.PortName,q.Shared}));
                string code=TrialPolicy.NewCode(null);
                string name="ISAFOM_SENTINELLA_A";
                bool removed=false,attempted=false;
                InstallationResult? installed=null;
                try
                {
                    attempted=true;
                    installed=new InstallerEngine(machine,provider,new SafeEventLog()).InstallAsync(settings,name,code.ToCharArray()).GetAwaiter().GetResult();
                    Check(installed.ConfigurationVerified,"Real provisioning, native preferences and independent verification complete");
                    var verified=provider.VerifyForCurrentUserAsync(settings.PrinterName,name,code.ToCharArray(),CancellationToken.None).GetAwaiter().GetResult();
                    Check(verified.All,"Separate production verifier confirms all preferences");
                    // Repeating on the same queue must update values without duplicates.
                    name="ISAFOM È D'Ángelo";code=TrialPolicy.NewCode(code);
                    installed=new InstallerEngine(machine,provider,new SafeEventLog()).InstallAsync(settings,name,code.ToCharArray()).GetAwaiter().GetResult();
                    Check(installed.ConfigurationVerified&&installed.Kind==InstallKind.Configure,"Second run updates existing owned queue without another provisioning step");
                }
                finally
                {
                    var now=machine.InspectAsync(CancellationToken.None).GetAwaiter().GetResult();
                    if(attempted&&now.Queues.Any(q=>q.Name==settings.PrinterName))
                    {
                        var target=Kx85Preferences.Authorize(settings,new(settings.PrinterName,name,code));
                        using var handle=PrinterStore.Open(settings.PrinterName,0x3000c);
                        removed=PrinterStore.RemoveAuthorizedTest(handle,target);
                    }
                    var after=machine.InspectAsync(CancellationToken.None).GetAwaiter().GetResult();
                    string othersAfter=JsonSerializer.Serialize(after.Queues.Where(q=>q.Name!=settings.PrinterName).OrderBy(q=>q.Name).Select(q=>new{q.Name,q.DriverName,q.PortName,q.Shared}));
                    string? executableHash=null;
                    if(Kx85Preferences.ExecutableOverride is not null){using var binary=File.OpenRead(Kx85Preferences.ExecutableOverride);executableHash=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(binary));}
                    File.WriteAllText(args[1],JsonSerializer.Serialize(new{Completed=installed?.ConfigurationVerified==true,Checks=installed?.Checks,InstallerSha256=executableHash,
                        PublishedExecutableUsed=args[0]=="--end-to-end-published",OwnedTestQueueRemoved=removed,OtherPrinterMetadataUnchanged=othersBefore==othersAfter,
                        DefaultUnchanged=PrinterStore.DefaultName()==originalDefault,PhysicalPrintPerformed=false,ExpectedValuesExported=false},new JsonSerializerOptions{WriteIndented=true}));
                }
                Check(removed,"Only the newly created, owned, empty integration-test queue removed");
            }
            if(args.Length==2&&args[0]=="--lab")
            {
                using var state=JsonDocument.Parse(File.ReadAllText(args[1]));
                string owner=state.RootElement.GetProperty("Token").GetString()!;
                string queue=TrialPolicy.QueueName(owner);
                foreach(string name in new[]{"ISAFOM_SENTINELLA_A","ISAFOM_SENTINELLA_B"})
                {
                    string code=TrialPolicy.NewCode(null);
                    var start=new ProcessStartInfo(Environment.ProcessPath!){UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true,StandardInputEncoding=new UTF8Encoding(false)};
                    start.ArgumentList.Add(typeof(Tests).Assembly.Location);start.ArgumentList.Add("--lab-worker");
                    using var child=Process.Start(start)!;
                    var stdout=child.StandardOutput.ReadToEndAsync();var stderr=child.StandardError.ReadToEndAsync();
                    child.StandardInput.WriteLine(JsonSerializer.Serialize(new WorkerRequest(owner,name,code)));child.StandardInput.Close();
                    if(!child.WaitForExit(90000)){child.Kill();throw new Exception("Lab timeout");}
                    string reply=stdout.GetAwaiter().GetResult();_ = stderr.GetAwaiter().GetResult();
                    var native=JsonSerializer.Deserialize<WorkerResult>(reply)!;
                    Check(child.ExitCode==0&&native.Accepted&&native.ColorModeAfterReopen==2,"Same production native source writes B/W and private mode");
                    var checks=Kx85Preferences.ReadChecks(queue,name,code,p=>PrinterStore.RequireOwned(p,owner));
                    Check(checks.All,"All nine independent checks pass after new-process writer exits");
                    File.WriteAllText(Path.Combine(Path.GetDirectoryName(args[1])!,"installer-lab-"+name[^1]+"-checks.json"),JsonSerializer.Serialize(checks,new JsonSerializerOptions{WriteIndented=true}));
                }
            }
            Console.WriteLine($"PASS {passed} installer checks; personal values not exported; no physical printing.");return 0;
        }
        catch(Exception error){Console.WriteLine("FAIL installer check: "+error.GetType().Name+(error is InstallerException known?" "+known.Code:"" )+" step="+MachineElevation.LastStep+" native="+MachineElevation.LastNativeCode);Console.WriteLine(JsonSerializer.Serialize(new{Native=Kx85Preferences.LastNativeResult,Checks=Kx85Preferences.LastChecks}));return 1;}
    }
}
