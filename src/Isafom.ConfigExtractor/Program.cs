using System.IO;
using System.Printing;
using System.Text.Json;
using Isafom.Collector;

namespace Isafom.ConfigExtractor;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        bool identity=args.Length==1&&args[0]=="--verify-current-identity";
        bool reversed=args.Length==1&&args[0]=="--verify-kx85-reversed";
        try
        {
            string root=Workspace();
            Environment.SetEnvironmentVariable("TEMP",Path.Combine(root,".lavoro","tmp"));
            Environment.SetEnvironmentVariable("TMP",Path.Combine(root,".lavoro","tmp"));
            Environment.SetEnvironmentVariable("DOTNET_BUNDLE_EXTRACT_BASE_DIR",Path.Combine(root,".lavoro","bundle"));
            Directory.CreateDirectory(Path.Combine(root,".lavoro","ps-cache"));
            Environment.SetEnvironmentVariable("PSModuleAnalysisCachePath",Path.Combine(root,".lavoro","ps-cache","ModuleAnalysisCache"));
            Directory.CreateDirectory(Path.Combine(root,".lavoro","tmp"));Directory.CreateDirectory(Path.Combine(root,".lavoro","bundle"));
            bool sentinel=args.Length==1&&args[0]=="--sentinel";
            bool preflight=args.Length==1&&args[0]=="--sentinel-preflight";
            bool direct=args.Length==1&&args[0]=="--observe-direct";
            bool markers=args.Length==1&&args[0]=="--observe-markers";
            bool conversion=args.Length==1&&args[0]=="--probe-direct-conversion";
            bool identityUi=args.Length==1&&args[0]=="--verify-identity-ui";
            string directory=args.Length==2&&args[0]=="--output-dir" ? args[1] : args.Length==0||sentinel||preflight||direct||markers||conversion||identity||identityUi||reversed ? Path.Combine(root,"raccolta-locale","Configurazione_Kyocera_"+DateTime.Now.ToString("yyyyMMdd_HHmmss")) : throw new ArgumentException();
            if(reversed)
            {
                Console.InputEncoding=new System.Text.UTF8Encoding(false,true);
                Console.OutputEncoding=new System.Text.UTF8Encoding(false);
                return RunReversed();
            }
            if(!identity)Console.WriteLine("ISAFOM: estrazione della configurazione, sola lettura. Nessuna stampa.");
            using var session=new CollectorSession();session.RefreshQueuesAsync().GetAwaiter().GetResult();
            var inventory=session.AvailableQueues.Select(q=>(q.Name,q.DriverName,q.PortName)).OrderBy(q=>q.Name,StringComparer.Ordinal).ToArray();
            var choices=session.AvailableQueues.Where(q=>q.ModelHint).ToArray();
            if(choices.Length!=1){Console.WriteLine(identity?JsonSerializer.Serialize(new{Event="Error",Reason="TargetQueueNotUnique"}):"Selezione non univoca: usare il raccoglitore GUI. Nessuna coda scelta per tentativi.");return 2;}
            session.SelectQueue(choices[0]);if(!identity)Console.WriteLine("Lettura della coda candidata e controverifica dell'endpoint ISAFOM.");
            session.CollectAsync().GetAwaiter().GetResult();
            var queue=session.Current!.WindowsAndQueue.GetProperty("Queues")[0];
            var host=queue.GetProperty("Port").GetProperty("Host");
            if(host.GetProperty("State").GetString()!="Verificata"||host.GetProperty("Value").GetString()!="stampante.isafom.cnr.it")
            {Console.WriteLine(identity?JsonSerializer.Serialize(new{Event="Error",Reason="TargetEndpointNotVerified"}):"Endpoint non corrispondente al riferimento ISAFOM; estrazione mirata interrotta.");return 2;}
            if(identity)return IdentityVerification.Run(choices[0]);
            if(identityUi)return new System.Windows.Application().Run(new IdentityVerifierWindow(choices[0]));
            if(sentinel)return new System.Windows.Application().Run(new SentinelWindow(root,choices[0]));
            if(direct)return DirectObservation.Run(root,choices[0]);
            if(markers)return MarkerObservation.Run(root,choices[0]);
            if(conversion)return DirectConversionProbe.Run(root,choices[0]);
            if(preflight)
            {
                using var preflightSnapshot=SentinelSnapshot.Capture(choices[0].Name,choices[0].DriverName);
                Console.WriteLine($"SentinelPreflight: Entries={preflightSnapshot.Count}; Complete={preflightSnapshot.Complete}; NoChanges=True; RawDataExported=False");
                return preflightSnapshot.Complete?0:3;
            }
            string? defaultBefore=DefaultName();
            Console.WriteLine("Lettura proprietà standard globali/utente, capacità e DEVMODE pubblico.");
            var snapshot=StandardSettings.Read(choices[0].Name);
            Setting[] settings=[..snapshot.Settings,..PublicDevMode.Read(choices[0].Name,8),..PublicDevMode.Read(choices[0].Name,9)];
            Console.WriteLine("Confronto prima/dopo dei contenuti leggibili e dell'inventario.");
            session.CollectAsync().GetAwaiter().GetResult();var evidence=session.Bundle();
            bool hasDevModes=evidence.First.Evidence[0].DevModes.All(d=>d.PublicBytes.HasValue)&&evidence.Current.Evidence[0].DevModes.All(d=>d.PublicBytes.HasValue);
            bool changed=evidence.Differences.Any(d=>d.Result is "Contenuto cambiato" or "Comparso" or "Non più leggibile o rimosso" or "Diversa" or "Simboli diversi; significato da validare");
            bool? unchanged=hasDevModes&&evidence.Differences.Length>0?!changed:null;
            session.RefreshQueuesAsync().GetAwaiter().GetResult();
            bool sameInventory=inventory.SequenceEqual(session.AvailableQueues.Select(q=>(q.Name,q.DriverName,q.PortName)).OrderBy(q=>q.Name,StringComparer.Ordinal));
            string? defaultAfter=DefaultName();bool? sameDefault=defaultBefore is not null&&defaultAfter is not null?defaultBefore==defaultAfter:null;
            var dossier=new Dossier(1,DossierWriter.Version,DateTime.UtcNow,false,evidence.Current.WindowsAndQueue,settings,snapshot.Personal,snapshot.ParameterRules,DossierWriter.Locations,
                new(unchanged,sameInventory,sameDefault,evidence.Differences),DossierWriter.Gaps,DossierWriter.Sources);
            DossierWriter.Export(root,directory,dossier,evidence);
            Console.WriteLine($"Rapporto creato: {settings.Length} proprietà con stato; {snapshot.ParameterRules.Length} vincoli KX. Nessun dato personale esportato.");
            Console.WriteLine("Piano installer generato con blocchi espliciti: nessuna installazione dichiarata verificata.");
            return unchanged==false||!sameInventory||sameDefault==false?3:0;
        }
        catch(Exception error){Console.WriteLine(identity?JsonSerializer.Serialize(new{Event="Error",Reason=error.GetType().Name,RawDetailsExported=false}):"Estrazione non completata: "+error.GetType().Name+". Nessun errore locale grezzo esportato.");return 1;}
    }
    private static int RunReversed()
    {
        // Reuse the existing bounded discovery and endpoint check. Never accept
        // a queue name or expected secret as a command-line argument.
        try
        {
            using var session=new CollectorSession();
            session.RefreshQueuesAsync().GetAwaiter().GetResult();
            var candidates=session.AvailableQueues.Where(q=>q.ModelHint).ToArray();
            if(candidates.Length!=1)throw new IOException("TargetQueueNotUnique");
            session.SelectQueue(candidates[0]);
            var details=session.ReadSelectedQueueDetailsAsync().GetAwaiter().GetResult();
            var host=details.GetProperty("Queues")[0].GetProperty("Port").GetProperty("Host");
            if(host.GetProperty("State").GetString()!="Verificata"||host.GetProperty("Value").GetString()!="stampante.isafom.cnr.it")throw new IOException("TargetEndpointNotVerified");
            var input=IdentityVerification.ReadRequest(Console.In);
            var report=Kx85NativeReadback.Read(candidates[0].Name,input.UserName,input.AccountId);
            Console.WriteLine(JsonSerializer.Serialize(report));
            return 3; // Research readback cannot certify or unlock installation.
        }
        catch(Exception error)
        {
            string[] allowed=["TargetQueueNotUnique","TargetEndpointNotVerified","ExactOriginalKx85FileRequired","ExactDriverComponentNotFound","DriverDependencyListUnavailable","DriverMetadataUnavailable","PrinterReadUnavailable"];
            string reason=allowed.Contains(error.Message,StringComparer.Ordinal)?error.Message:error is ArgumentException?"InvalidInput":"ReadUnavailable";
            Console.WriteLine(JsonSerializer.Serialize(new{ReadOnly=true,Result="NonVerificabile",Reason=reason,InstallerReady=false,RawDetailsExported=false}));
            return 2;
        }
    }
    private static string? DefaultName()
    {
        try{using var server=new LocalPrintServer(PrintSystemDesiredAccess.EnumerateServer);using var queue=server.DefaultPrintQueue;return queue?.Name;}
        catch{return null;}
    }
    private static string Workspace()
    {
        for(var directory=new DirectoryInfo(AppContext.BaseDirectory);directory is not null;directory=directory.Parent)
        {
            if(File.Exists(Path.Combine(directory.FullName,"ISAFOM-Printer-Installer","Directory.Build.props")))return directory.FullName;
            if(directory.Name=="ISAFOM-Printer-Installer"&&File.Exists(Path.Combine(directory.FullName,"Directory.Build.props")))return directory.Parent!.FullName;
        }
        throw new IOException("WorkspaceNotFound");
    }
}
