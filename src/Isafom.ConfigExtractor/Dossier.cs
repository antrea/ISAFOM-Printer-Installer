using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Isafom.Collector;

namespace Isafom.ConfigExtractor;

public sealed record StorageLocation(string Scope,string Location,string Meaning,string Precision);
public sealed record Integrity(bool? SelectedReadableConfigurationUnchanged,bool QueueInventoryUnchanged,bool? DefaultPrinterUnchanged,Difference[] Comparisons);
public sealed record Dossier(int SchemaVersion,string ToolVersion,DateTime GeneratedUtc,bool CompleteForAutomaticInstallation,
    JsonElement WindowsAndQueue,Setting[] Settings,PersonalReadback[] PersonalReadback,ParameterRule[] ParameterRules,
    StorageLocation[] StorageMap,Integrity Integrity,string[] Unresolved,string[] Sources);

public static class DossierWriter
{
    public const string Version="0.1.2-dev";
    private static readonly JsonSerializerOptions JsonOptions=new(){WriteIndented=true};
    public static readonly StorageLocation[] Locations=[
        new("Macchina",@"HKLM\SYSTEM\CurrentControlSet\Control\Print\Printers\<coda>","Coda, driver e PrinterDriverData","Metadati e proprietà pubbliche; mappature proprietarie non certificate"),
        new("Riferimento KX 8.5.2405",@"HKLM\SYSTEM\CurrentControlSet\Control\Print\Printers\<coda>\PrinterDriverData","Nome e codice temporanei osservati in due blocchi binari da 7.968 byte","Osservazione storica con sentinelle; non è una ricerca degli stessi valori nella lettura corrente né un formato di scrittura"),
        new("Riferimento KX 8.5.2405",@"HKLM\SYSTEM\CurrentControlSet\Control\Print\Printers\<coda>\PrinterDriverData","Due REG_SZ da 35 byte variano nella prova della sola password e tornano identici al ripristino; uno è JobId","Candidati: formato e significato non decodificati; il confronto MD5 non è risultato verificabile"),
        new("Utente corrente",@"HKCU\Printers\DevModePerUser","DEVMODE personale","Windows espone le preferenze personali tramite GetPrinter livello 9"),
        new("Utente corrente",@"HKCU\Printers\DevModes2","Memorizzazione alternativa osservata","Presenza non equivale a decodifica"),
        new("Utente corrente",@"HKCU\Software\Kyocera Mita\<coda>","Area proprietaria osservata sul PC di riferimento","Soli nomi consentiti, tipi, dimensioni e confronti protetti; nessun offset privato")
    ];
    public static readonly string[] Gaps=[
        "Nome utente: localizzato con sentinella sul riferimento; campo binario e scrittura supportata non verificati.",
        "Job accounting amministrativo: i simboli PrintTicket non certificano da soli la selezione della GUI Amministratore.",
        "Password privata: un parametro PrintTicket vuoto non dimostra password assente nel driver.",
        "Richiesta password: un default delle capabilities non certifica lo stato della casella.",
        "Opzioni installabili e impostazioni proprietarie: capacità dichiarate e metadati non certificano ogni accessorio fisico.",
        "Nel controllo sul riferimento, OK senza modifiche cambia tre copie DEVMODE utente: distinguere confronto integrale e verifica dei singoli campi; non ignorare interi blob.",
        "Sono state eseguite prove GUI manuali autorizzate; nessuna scrittura automatica verificata, persistenza dopo nuova sessione, stampa fisica o rilascio."
    ];
    public static readonly string[] Sources=[
        "https://isafom.cnr.it/stampante-taskalfa-3554ci-kx-portici/",
        "https://learn.microsoft.com/en-us/dotnet/api/system.printing.printticket",
        "https://learn.microsoft.com/en-us/dotnet/api/system.printing.printcapabilities",
        "https://learn.microsoft.com/en-us/windows/win32/api/wingdi/ns-wingdi-devmodew",
        "https://learn.microsoft.com/en-us/windows/win32/printdocs/printer-info-8",
        "https://learn.microsoft.com/en-us/windows/win32/printdocs/printer-info-9"
    ];
    public static object Blueprint(Dossier dossier)=>new {
        SchemaVersion=1,Executable=false,AutomaticInstallationReady=false,
        Target=new {Host="stampante.isafom.cnr.it",Protocol="RAW",Port=9100,SnmpEnabled=true,SnmpCommunity="isafom",SnmpIndex=1,Shared=false},
        Inputs=new[]{"Nome utente","Account ID"},
        IdentityRules=new {PreserveLeadingZeros=true,KeepAsString=true,UseExactSameCodeForAccountingAndPrivatePassword=true,GeneralAccountMaximumDigits=8,OperationalSharedCodeLengthConfirmed=4,OtherSharedCodeLengthsNeedValidation=true,CopyReferenceUsersIdentity=false},
        DesiredPreferences=new {OutputColor="Monochrome",JobStorage="PrivatePrint",PromptForPrivatePassword=false,Accounting="UseSpecificID"},
        ConfigurationScopes=new[]{"Macchina: driver, porta, coda e area Amministratore da validare","Ogni utente: identificazione e preferenze personali"},
        RequiredBindings=new[]{"Impostazioni periferica > Amministratore > Job accounting","Impostazioni periferica > Utente > Nome utente","Preferenze > Lavoro > Job accounting > Impostazioni","Preferenze > Lavoro > Specifica password"},
        DriverEvidence=dossier.WindowsAndQueue.GetProperty("Queues")[0].GetProperty("DriverPackage"),
        Blockers=dossier.Unresolved,
        Note="Specifiche di progetto, non istruzioni eseguibili. Il driver corrente non viene sostituito. UnavailableKxPreferences resta attivo."
    };
    public static string Markdown(Dossier dossier)
    {
        var text=new StringBuilder("# Configurazione Kyocera: estrazione in sola lettura\n\n");
        text.AppendLine($"Tool {dossier.ToolVersion}; UTC {dossier.GeneratedUtc:O}.\n");
        text.AppendLine("Il rapporto distingue dati letti, proprietà non esposte e capacità dichiarate. Nessun nome personale, codice, password, XML o DEVMODE grezzo viene esportato. Non è un backup importabile né una certificazione dell'installer.\n");
        text.AppendLine($"Inventario code invariato: {dossier.Integrity.QueueInventoryUnchanged}; predefinita invariata: {Show(dossier.Integrity.DefaultPrinterUnchanged)}; contenuti leggibili della coda invariati: {Show(dossier.Integrity.SelectedReadableConfigurationUnchanged)}.\n");
        text.AppendLine("## Dove vengono salvate le impostazioni\n");
        foreach(var item in dossier.StorageMap)text.AppendLine($"- **{item.Scope}** — `{item.Location}`: {item.Meaning}. {item.Precision}.");
        text.AppendLine("\nDEVMODE ha una parte pubblica documentata e una parte privata del driver. Il tool decodifica soltanto i membri pubblici dichiarati da dmFields; i contenuti privati sono confrontati con chiave casuale in memoria dal raccoglitore, senza esportarli.\n");
        text.AppendLine("Le righe contrassegnate Riferimento descrivono prove precedenti sul KX 8.5.2405 e non certificano le copie della coda appena letta. In quelle prove anche OK senza modifiche riscriveva i DEVMODE utente: una differenza del blob, da sola, non verifica i singoli campi. Nessun contenuto proprietario viene escluso dal confronto per ottenere un esito positivo.\n");
        text.AppendLine("## Campi personali: stato, senza valori\n");
        text.AppendLine("| Ambito | Accounting nel ticket | Archiviazione | Account ID | Codice privato | Uguaglianza confrontabile |\n|---|---|---|---|---|---|");
        foreach(var item in dossier.PersonalReadback)text.AppendLine($"| {item.Scope} | {item.AccountingMode} | {item.StorageMode} | {item.AccountValueState} | {item.PrivateCodeValueState} | {Show(item.AccountAndPrivateCodeEqual)} |");
        text.AppendLine("\nIl Nome utente della scheda Utente e la selezione del Job accounting amministrativo non sono dedotti da questi simboli. 'Non esposto' o 'vuoto' non significa disabilitato.\n");
        foreach(var scope in dossier.Settings.GroupBy(item=>item.Scope))
        {
            text.AppendLine($"## {scope.Key}\n");
            text.AppendLine("| Proprietà | Fonte | Stato | Valore pubblico | Unità |\n|---|---|---|---|---|");
            foreach(var item in scope)text.AppendLine($"| {item.Field} | {item.Source} | {item.State} | {item.Value?.GetRawText()??"—"} | {item.Unit} |");
            text.AppendLine();
        }
        text.AppendLine("## Vincoli dichiarati dal provider KX\n");
        foreach(var rule in dossier.ParameterRules)text.AppendLine($"- {rule.Parameter} / {rule.Property}: {rule.Value}.");
        text.AppendLine("\nQuesti sono vincoli dello schema, non valori personali né prova di impostazioni attive.\n");
        text.AppendLine("## Differenze rispetto alla guida\n");
        var color=dossier.Settings.FirstOrDefault(s=>s.Scope=="Utente corrente"&&s.Field=="OutputColor");
        text.AppendLine($"- Colore utente letto: {color?.Value?.GetRawText()??"non disponibile"}; la guida richiede Monochrome (bianco e nero). Nessuna correzione applicata.");
        text.AppendLine("- Piano_Installer.json descrive i requisiti pubblici e i due input futuri; non contiene credenziali o operazioni applicabili.\n");
        text.AppendLine("## Punti ancora non verificabili\n");
        foreach(string gap in dossier.Unresolved)text.AppendLine("- "+gap);
        text.AppendLine("\n## Fonti\n");foreach(string source in dossier.Sources)text.AppendLine("- "+source);
        return text.ToString();
    }
    private static string Show(bool? value)=>value is null?"Non verificabile":value.Value?"Sì":"No";
    public static void Export(string workRoot,string directory,Dossier dossier,PortableBundle evidence)
    {
        string target=Path.GetFullPath(directory);workRoot=Path.GetFullPath(workRoot).TrimEnd(Path.DirectorySeparatorChar);
        if(!target.StartsWith(workRoot+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)||Directory.Exists(target)||File.Exists(target))throw new IOException("UnsafeOrExistingOutput");
        string parent=Path.GetDirectoryName(target)!;
        if(!Directory.Exists(parent))throw new IOException("OutputParentMissing");
        for(var cursor=new DirectoryInfo(parent);cursor is not null&&cursor.FullName.StartsWith(workRoot,StringComparison.OrdinalIgnoreCase);cursor=cursor.Parent)
            if((cursor.Attributes&FileAttributes.ReparsePoint)!=0)throw new IOException("ReparsePointRefused");
        var files=new Dictionary<string,byte[]>();
        void Json(string name,object value)=>files.Add(name,Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value,JsonOptions)));
        Json("Configurazione.json",dossier);Json("Piano_Installer.json",Blueprint(dossier));Json("Evidenze_Diagnostiche.json",evidence);
        files.Add("Rapporto.md",Encoding.UTF8.GetBytes(Markdown(dossier)));
        Json("Manifest.json",new {SchemaVersion=1,Files=files.Select(f=>new {Name=f.Key,Bytes=f.Value.Length,SHA256=Convert.ToHexString(SHA256.HashData(f.Value))}).ToArray()});
        long bytes=files.Sum(f=>(long)f.Value.Length);
        long existing=new DirectoryInfo(workRoot).EnumerateFiles("*",SearchOption.AllDirectories).Sum(f=>f.Length);
        if(existing+bytes*2+65536>500L*1024*1024)throw new IOException("WorkspaceBudgetExceeded");
        Directory.CreateDirectory(target);var created=new List<string>();
        try
        {
            foreach(var file in files)
            {
                string path=Path.Combine(target,file.Key);
                using var output=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None);created.Add(path);
                output.Write(file.Value);
            }
            string archivePath=Path.Combine(target,"Configurazione_Kyocera.zip");
            using var zipOutput=new FileStream(archivePath,FileMode.CreateNew,FileAccess.Write,FileShare.None);created.Add(archivePath);
            using var zip=new ZipArchive(zipOutput,ZipArchiveMode.Create);
            foreach(var file in files){using var output=zip.CreateEntry(file.Key,CompressionLevel.NoCompression).Open();output.Write(file.Value);}
        }
        catch
        {
            // Only files created by this invocation, never a recursive delete.
            foreach(string file in created)File.Delete(file);
            if(!Directory.EnumerateFileSystemEntries(target).Any())Directory.Delete(target);
            throw;
        }
    }
}
