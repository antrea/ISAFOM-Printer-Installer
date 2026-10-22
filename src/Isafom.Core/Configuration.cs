using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Isafom.Core;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PrinterSettings
{
    public string PrinterName { get; init; } = "";
    public string DriverName { get; init; } = "";
    public string DriverVersion { get; init; } = "";
    public string Host { get; init; } = "";
    public string FallbackIPv4 { get; init; } = "";
    public bool AllowFallbackOnDnsFailure { get; init; }
    public string PortName { get; init; } = "";
    public int PortNumber { get; init; }
    public string SnmpCommunity { get; init; } = "";
    public int SnmpIndex { get; init; }
    public string DriverUri { get; init; } = "";
    public string DriverArchiveSha256 { get; init; } = "";
    public string InfRelativePath { get; init; } = "";

    public static PrinterSettings Default()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Isafom.Core.printer-settings.json")!;
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    }

    public static PrinterSettings Parse(string json)
    {
        if (json.Length > 16384) throw new InstallerException(ErrorCode.Configuration);
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                document.RootElement.EnumerateObject().GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1))
                throw new InstallerException(ErrorCode.Configuration);
            var result = JsonSerializer.Deserialize<PrinterSettings>(json) ?? throw new InstallerException(ErrorCode.Configuration);
            result.Validate();
            return result;
        }
        catch (JsonException) { throw new InstallerException(ErrorCode.Configuration); }
    }

    public void Validate()
    {
        if (new[] { PrinterName, DriverName, DriverVersion, Host, FallbackIPv4, PortName, SnmpCommunity, DriverUri, DriverArchiveSha256, InfRelativePath }.Any(v => v is null))
            throw new InstallerException(ErrorCode.Configuration);
        static bool SafeLabel(string value) => value.Length is > 0 and <= 120 && !value.Any(char.IsControl) && !value.Contains('\\') && !value.Contains('/');
        if (!SafeLabel(PrinterName) || DriverName != "Kyocera TASKalfa 3554ci KX" ||
            !Regex.IsMatch(DriverVersion, @"\A\d{1,2}\.\d{1,2}\.\d{1,5}\.\d{1,5}\z") ||
            Uri.CheckHostName(Host) != UriHostNameType.Dns || Host.Length > 253 ||
            !IPAddress.TryParse(FallbackIPv4, out var ip) || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork ||
            !SafeLabel(PortName) || PortNumber != 9100 || SnmpIndex is < 1 or > 65535 ||
            !Regex.IsMatch(SnmpCommunity, @"\A[A-Za-z0-9_-]{1,32}\z") ||
            !Regex.IsMatch(DriverArchiveSha256, @"\A[0-9a-fA-F]{64}\z") ||
            (DriverUri.Length != 0 && (!Uri.TryCreate(DriverUri, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            !string.IsNullOrEmpty(uri.UserInfo) || !uri.IsDefaultPort || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
            !(uri.Host.Equals("intranet.isafom.cnr.it", StringComparison.OrdinalIgnoreCase) ||
              uri.Host.Equals("www.kyoceradocumentsolutions.us", StringComparison.OrdinalIgnoreCase) ||
              uri.Host.Equals("www.kyoceradocumentsolutions.eu", StringComparison.OrdinalIgnoreCase)))) ||
            !Regex.IsMatch(InfRelativePath, @"\A[A-Za-z0-9_. /-]+/64bit/OEMSETUP\.INF\z", RegexOptions.IgnoreCase) ||
            InfRelativePath.Split('/').Any(p => p is ".." or "." or ""))
            throw new InstallerException(ErrorCode.Configuration);
    }

    public string Fingerprint() => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(this)));
}

public enum ErrorCode
{
    InvalidName, InvalidAccount, UnsupportedWindows, UnsupportedArchitecture, Configuration,
    SpoolerUnavailable, Network, DnsTimeout, ProviderUnavailable, ProviderNotValidated,
    DriverConflict, QueueConflict, PortConflict, AmbiguousQueue, ExistingJobs,
    DriverIntegrity, DriverSignature, ElevationCancelled, MachineFailure, Verification,
    Protocol, Busy, Unexpected, FourDigitAccountRequired, RemovalChanged, RemovalBlocked, PrinterMissing, PrinterUnavailable, BackendUnavailable, RemovalShared
}

// Raw exception messages can contain printer names, paths or credentials. Never expose them.
public sealed class InstallerException(ErrorCode code) : Exception(code.ToString())
{
    public ErrorCode Code { get; } = code;
}

public static class Messages
{
    public static string For(ErrorCode code) => code switch
    {
        ErrorCode.InvalidName => "Inserisci il tuo nome, fino a 31 caratteri e senza caratteri di controllo. Il nome non viene abbreviato automaticamente.",
        ErrorCode.InvalidAccount => "L'Account ID deve contenere da 1 a 8 cifre numeriche.",
        ErrorCode.FourDigitAccountRequired => "Inserisci le 4 cifre dell'Account ID assegnato dall'istituto. Il driver richiede lo stesso codice di 4 cifre per la stampa privata; gli zeri iniziali vengono conservati.",
        ErrorCode.UnsupportedWindows => "È necessario Windows 10 (versione 1607 o successiva) oppure Windows 11.",
        ErrorCode.UnsupportedArchitecture => "È necessario un PC Intel/AMD x64 con Windows 10 o 11; ARM64 e Windows a 32 bit non sono supportati da questo installer.",
        ErrorCode.Configuration => "Il file di configurazione amministrativo non è valido o non è protetto correttamente.",
        ErrorCode.SpoolerUnavailable => "Il servizio di stampa non è disponibile. Rivolgiti all'assistenza informatica.",
        ErrorCode.Network or ErrorCode.DnsTimeout => "Stampante non raggiungibile. Collega il PC alla rete dell'Istituto e riprova.",
        ErrorCode.ProviderUnavailable or ErrorCode.ProviderNotValidated => "Automazione Kyocera non ancora disponibile: Nome utente, Job Accounting, Account ID e stampa privata non possono essere applicati e verificati in sicurezza con questa versione. Nessuna installazione è stata avviata. Rivolgiti ad Andrea Esposito.",
        ErrorCode.DriverConflict => "È presente una versione diversa del driver Kyocera. In Debug e verifica usa Rimuovi completamente per controllare la rimozione della vecchia installazione, poi riprova. Se il driver serve ad altre stampanti, rivolgiti ad Andrea Esposito.",
        ErrorCode.QueueConflict or ErrorCode.AmbiguousQueue => "La coda esistente è diversa da quella prevista, condivisa oppure non è identificabile in modo univoco. Rivolgiti ad Andrea Esposito.",
        ErrorCode.PortConflict => "La porta esistente ha impostazioni diverse. Nessuna porta condivisa è stata modificata.",
        ErrorCode.ExistingJobs => "Sono presenti lavori di stampa. Attendi il loro completamento prima della riparazione.",
        ErrorCode.DriverIntegrity or ErrorCode.DriverSignature => "Verifica del pacchetto driver non superata. Installazione interrotta.",
        ErrorCode.ElevationCancelled => "Autorizzazione amministrativa annullata. Riprova quando un amministratore locale è disponibile.",
        ErrorCode.Verification => "Configurazione incompleta: almeno una verifica necessaria non è stata superata. Non utilizzare la coda per documenti riservati.",
        ErrorCode.Busy => "Un'altra installazione ISAFOM è già in corso.",
        ErrorCode.RemovalChanged => "La configurazione è cambiata dopo l'anteprima. Riapri la disinstallazione per controllare l'elenco aggiornato.",
        ErrorCode.RemovalBlocked => "Rimozione non consentita: controlla i lavori in attesa, le altre stampanti che usano il driver e l'appartenenza della coda.",
        ErrorCode.RemovalShared => "Ripristino completo bloccato: altre stampanti usano il driver, il suo pacchetto o la porta. Consulta l'elenco nell'anteprima. Nessun elemento è stato rimosso.",
        ErrorCode.PrinterMissing => "La stampante ISAFOM non è installata: non c'è ancora una coda da verificare. Completa prima Installa stampante; se l'installazione si è interrotta, risolvi l'errore indicato.",
        ErrorCode.PrinterUnavailable => "Windows non riesce ad aprire la coda ISAFOM. Potrebbe essere stata rimossa oppure mancano le autorizzazioni: rileggi la configurazione nella scheda Debug e verifica.",
        ErrorCode.BackendUnavailable => "Impossibile avviare il componente Windows per leggere o configurare la stampante. Controlla che Windows PowerShell sia disponibile e consulta il log tecnico.",
        _ => "Operazione non completata. Rivolgiti ad Andrea Esposito comunicando il codice dell'errore."
    };
}
