using System.Security.Principal;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using Isafom.Core;

namespace Isafom.Windows;

public sealed record RemovalPackage(string Name, string Sha256);
public sealed record RemovalDriver(string Name,string Environment,string? Signature=null);
public sealed record RemovalDriverFile(string Path,string Sha256);
public sealed record RemovalQueue(string Name,string DriverName,string PortName,int Jobs,bool Shared,bool IsDefault);
public sealed record RemovalPort(string Name,string Host,int Protocol,int PortNumber,string? Monitor=null);
public sealed record RemovalPlan(string? QueueName, bool ManualQueue, int Jobs, bool IsDefault, string DriverName,
    string? DriverVersion, bool DriverPresent, bool DriverRemovable, string[] DriverUsedBy, bool CanProceed,
    ErrorCode? BlockedCode, string Fingerprint, string? PortName=null, string[]? PortUsedBy=null,
    RemovalPackage[]? Packages=null, string[]? PackageDriverNames=null, string[]? PreferenceQueues=null,
    bool DownloadCachePresent=false, bool SettingsFilePresent=false, bool UserPreferencesPresent=false, bool FullRemoval=true,
    RemovalDriver[]? DriverRegistrations=null,RemovalDriverFile[]? DriverFiles=null,bool ForceRemoval=false,
    RemovalQueue[]? QueuesToRemove=null,RemovalPort[]? PortsToRemove=null)
{
    public bool RequiresConfirmation=>DriverPresent||(DriverRegistrations??[]).Length>0||(Packages??[]).Length>0;
    public bool HasWork=>QueueName is not null||RequiresConfirmation||(DriverFiles??[]).Length>0||(QueuesToRemove??[]).Length>0||
        PortName is not null||(PortsToRemove??[]).Length>0||DownloadCachePresent||SettingsFilePresent||UserPreferencesPresent;
    public string Description =>
        $"Coda principale ISAFOM: {QueueName ?? "già assente"}\n" +
        ((QueuesToRemove??[]).Length>0?"Tutte le code da eliminare:\n• "+string.Join("\n• ",QueuesToRemove!.Select(q=>q.Name+(q.Shared?" [CONDIVISA]":"")+(q.IsDefault?" [PREDEFINITA]":"")))+"\n":"")+
        (QueueName is null ? "" : "Verranno eliminate le impostazioni di questa coda, inclusi Nome, Job Accounting e codice privato salvati nel driver.\n") +
        (ManualQueue ? "Coda installata manualmente: la rimozione locale può interessare anche altri utenti di questo PC.\n" : "") +
        (IsDefault ? "Questa è la stampante predefinita: Windows potrà sceglierne un'altra dopo la rimozione.\n" : "") +
        $"\nDriver: {DriverName}\nVersione rilevata: {DriverVersion ?? "non installato"}\n" +
        "Modelli registrati da eliminare:\n• "+string.Join("\n• ",DriverRegistrations?.Select(d=>d.Name+" ("+d.Environment+")")??PackageDriverNames??(DriverPresent?[DriverName]:["già assenti"]))+"\n"+
        "Pacchetti da eliminare dal Driver Store:\n• "+string.Join("\n• ",(Packages??[]).Select(p=>p.Name).DefaultIfEmpty("già assenti"))+"\n"+
        $"Copie locali dei file driver da eliminare: {(DriverFiles??[]).Length} (solo file inutilizzati e verificati).\n"+
        "Porte da eliminare:\n• "+string.Join("\n• ",(PortsToRemove??[]).Select(p=>p.Name+" — "+(p.Host.Length>0?p.Host:p.Monitor??"porta locale")).DefaultIfEmpty(PortName??"già assenti"))+"\n"+
        "Preferenze della coda nel tuo profilo: verranno cancellate.\n"+
        (DownloadCachePresent?"Cache e dati temporanei del tool: verranno eliminati.\n":"")+
        (SettingsFilePresent?"Configurazione amministrativa del tool: verrà eliminata.\n":"")+
        (DriverUsedBy.Length>0?"\nDriver/pacchetto usato da altre stampanti o dispositivi:\n• "+string.Join("\n• ",DriverUsedBy)+"\n":"")+
        ((PortUsedBy??[]).Length>0?"\nPorta usata da altre stampanti:\n• "+string.Join("\n• ",PortUsedBy!)+"\n":"")+
        "\nIl ripristino è completo solo se tutti gli elementi elencati risultano rimossi. I lavori di stampa non vengono cancellati."+
        (BlockedCode is { } code ? "\n\nRimozione bloccata: " + Messages.For(code) :
            !CanProceed ? "\n\nNon ci sono elementi rimovibili: il driver può essere ancora utilizzato da altre stampanti." : "");
}

public sealed record RemovalResult(bool QueueRemoved, bool DriverRemoved, bool DriverRetained, bool RequestedDriverRemoval,
    bool UserPreferencesCleared=false, bool QueueAbsent=false, bool PackageRemoved=false, bool PortRemoved=false,
    bool StorageRemoved=false, bool RestartRequired=false,bool DriverFilesRemoved=false,int DriverFilesRemaining=0,bool SpoolerRestarted=false,int IsolatedHostsClosed=0,bool PackageRestartRequired=false)
{
    public bool GlobalComplete=>QueueAbsent&&DriverRemoved&&PackageRemoved&&PortRemoved&&DriverFilesRemoved&&!RestartRequired;
    public bool Complete=>GlobalComplete&&StorageRemoved&&UserPreferencesCleared;
}

internal sealed record DriverFileRemovalResult(bool Complete,int Remaining,bool RestartRequired);

internal static class RemovalService
{
    internal static Task<RemovalPlan> PreviewAsync(PrinterSettings settings,bool forceRemoval=false)
    {
        RemovalStorage.Validate();
        return PowerShellBackend.RunAsync<RemovalPlan>(new { Mode = "PlanRemoval", Settings = settings,ForceRemoval=forceRemoval }, CancellationToken.None);
    }

    internal static async Task<RemovalResult> RemoveAsync(PrinterSettings settings, RemovalPlan approved,bool allowSpoolerRestart=false)
    {
        if(!approved.CanProceed||!approved.FullRemoval)throw new InstallerException(ErrorCode.RemovalBlocked);
        string originalSid = WindowsIdentity.GetCurrent().User!.Value;
        var result = await MachineElevation.RemoveAsync(settings, approved.Fingerprint, true,allowSpoolerRestart,approved.ForceRemoval);
        bool cleared = false;
        if (result.QueueAbsent)
        {
            // Clean only two exact queue-named cache values in the ORIGINAL user
            // profile. Never enumerate, read/export blobs, or load other profiles.
            if (WindowsIdentity.GetCurrent().User!.Value != originalSid) throw new InstallerException(ErrorCode.Protocol);
            var state = await new WindowsMachine(settings).InspectAsync(CancellationToken.None);
            string[] names=approved.PreferenceQueues??[settings.PrinterName];
            if(state.Queues.Any(q=>names.Contains(q.Name,StringComparer.OrdinalIgnoreCase)))throw new InstallerException(ErrorCode.RemovalChanged);
            cleared=true;
            try
            {
                foreach (string path in new[] { @"Printers\DevModePerUser", @"Printers\DevModes2" })
                {
                    using var key = Registry.CurrentUser.OpenSubKey(path, writable:true);
                    foreach(string queue in names)key?.DeleteValue(queue,throwOnMissingValue:false);
                    if(key is not null&&key.GetValueNames().Any(n=>names.Contains(n,StringComparer.OrdinalIgnoreCase)))cleared=false;
                }
            }
            catch (UnauthorizedAccessException) {cleared=false;}
            catch (System.IO.IOException) {cleared=false;}
        }
        return result with { UserPreferencesCleared = cleared };
    }
}

// A separate confirmation keeps the destructive action explicit and reviewable.
// Constructing or closing this window never performs a removal or requests UAC.
internal sealed class RemovalWindow : Window
{
    internal CheckBox Acknowledgement { get; } = new() { Margin = new(0,10,0,12) };
    internal Button RemoveButton { get; } = new() { Content = "Rimuovi completamente", Background = new SolidColorBrush(Color.FromRgb(181,47,47)), Foreground = Brushes.White, Padding = new(18,8,18,8), MinHeight = 36, IsEnabled = false };
    internal RemovalPlan SelectedPlan {get;private set;}
    private readonly Isafom.Collector.WindowPlacement placement;

    internal RemovalWindow(RemovalPlan plan)
    {
        SelectedPlan=plan;
        Title = "Rimuovi completamente la stampante ISAFOM"; Width=660;Height=620;MinWidth=360;MinHeight=300;
        FontFamily=new FontFamily("Segoe UI");FontSize=13;Background=new SolidColorBrush(Color.FromRgb(247,249,251));
        Icon = System.Windows.Application.Current?.MainWindow?.Icon;
        WindowStartupLocation=WindowStartupLocation.CenterOwner;
        var dock=new DockPanel { Margin=new(18) };
        var actions=new StackPanel { Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right,Margin=new(0,12,0,0) };
        DockPanel.SetDock(actions,Dock.Bottom);
        var cancel=new Button { Content="Annulla",IsCancel=true,Padding=new(18,8,18,8),MinHeight=36,Margin=new(0,0,10,0) };
        if(Application.Current?.MainWindow?.TryFindResource(typeof(Button)) is Style buttonStyle)
        {RemoveButton.Style=buttonStyle;cancel.Style=buttonStyle;}
        actions.Children.Add(cancel);actions.Children.Add(RemoveButton);dock.Children.Add(actions);
        var content=new StackPanel();
        content.Children.Add(new TextBlock { Text="Controlla cosa verrà rimosso",FontSize=20,FontWeight=FontWeights.SemiBold,TextWrapping=TextWrapping.Wrap,Margin=new(0,0,0,12) });
        var description=new TextBlock {Text=SelectedPlan.Description,TextWrapping=TextWrapping.Wrap};
        content.Children.Add(description);
        Acknowledgement.Content=new TextBlock { Text="Confermo la rimozione degli elementi elencati. Per liberare i file autorizzo anche il riavvio del servizio di stampa e la chiusura dei soli componenti dedicati del driver Kyocera, se necessari e solo quando tutte le code risultano vuote.",TextWrapping=TextWrapping.Wrap };
        Acknowledgement.IsEnabled=SelectedPlan.CanProceed;
        content.Children.Add(Acknowledgement);
        dock.Children.Add(new ScrollViewer { Content=content,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled });
        Content=dock;
        void Update() => RemoveButton.IsEnabled=SelectedPlan.CanProceed&&SelectedPlan.FullRemoval&&Acknowledgement.IsChecked==true;
        Acknowledgement.Checked+=(_,_)=>Update();Acknowledgement.Unchecked+=(_,_)=>Update();
        RemoveButton.Click+=(_,_)=>DialogResult=true;
        placement=new(this,660,620,minimumHeight:300,minimumWidth:360);placement.Attach();
    }
}
