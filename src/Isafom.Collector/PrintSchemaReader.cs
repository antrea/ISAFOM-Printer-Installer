using System.IO;
using System.Printing;
using System.Security.Cryptography;
using System.Xml;

namespace Isafom.Collector;

public static class PrintSchemaReader
{
    private const string Keywords = "http://schemas.microsoft.com/windows/2003/08/printing/printschemakeywords";
    private const string Framework = "http://schemas.microsoft.com/windows/2003/08/printing/printschemaframework";
    private static readonly HashSet<string> Features = new(["PageOutputColor", "JobDuplexAllDocumentsContiguously", "DocumentDuplex", "PageMediaSize", "PageOrientation", "JobAccounting", "JobAccountingMode", "Accounting", "JobStorage", "JobStorageMode", "JobRetention", "PrivatePrint", "PrivatePrinting", "eMPS", "EMPS", "PromptForPassword", "SpecifyPassword", "UseSpecificAccountID", "UserName", "AccountID", "AccountId", "Password", "PIN", "AccessCode"], StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> Options = new(["Monochrome", "Color", "Grayscale", "Auto", "Portrait", "Landscape", "ReversePortrait", "ReverseLandscape", "OneSided", "TwoSidedLongEdge", "TwoSidedShortEdge", "ISOA4", "ISOA3", "NorthAmericaLetter", "PrivatePrint", "PrivatePrinting", "PrivateJob", "QuickCopy", "ProofAndHold", "ProofHold", "StoredJob", "JobStorage", "Off", "On", "True", "False", "None", "Enabled", "Disabled", "UseSpecificAccountID", "UseSpecificID", "Prompt", "Specify", "Always", "Never"], StringComparer.OrdinalIgnoreCase);
    // Observed on the installed KX 8.5.2405 provider. Presence only: parameter
    // values may be personal, empty placeholders, or encoded vendor data.
    private static readonly HashSet<string> Parameters = new(["JobAccountID", "JobStorageAccessCode", "JobStorageUseAccessCode"], StringComparer.OrdinalIgnoreCase);
    private static string? Allowed(HashSet<string> set, string text) => set.FirstOrDefault(x => string.Equals(x, text, StringComparison.OrdinalIgnoreCase));

    // No raw XML, namespace strings, unrecognized symbols, parameter values or
    // PageDevmodeSnapshot values leave this filter.
    public static SchemaEvidence[] Filter(Stream stream, string scope)
    {
        var result = new List<SchemaEvidence>();
        var featureStack = new Stack<(int Depth, string Feature)>();
        int hidden = 0;
        using var xml = XmlReader.Create(stream, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null,
            MaxCharactersInDocument = 2 * 1024 * 1024, IgnoreComments = true, CloseInput = false
        });
        while (xml.Read())
        {
            if (xml.NodeType == XmlNodeType.EndElement)
            { while (featureStack.Count != 0 && featureStack.Peek().Depth >= xml.Depth) featureStack.Pop(); continue; }
            if (xml.NodeType != XmlNodeType.Element || xml.NamespaceURI != Framework) continue;
            string raw = xml.GetAttribute("name") ?? "";
            string local = raw[(raw.IndexOf(':') + 1)..];
            string ns = raw.Contains(':') ? xml.LookupNamespace(raw[..raw.IndexOf(':')]) ?? "" : "";
            if (xml.LocalName == "Feature")
            {
                string? known = Allowed(Features, local);
                if (known is not null)
                {
                    string safe = known + (ns == Keywords ? " (standard Windows)" : " (simbolo proprietario, significato da validare)");
                    result.Add(new(scope, safe, null, "Presenza del simbolo verificata; opzione da leggere"));
                    if (!xml.IsEmptyElement) featureStack.Push((xml.Depth, safe));
                }
                else { hidden++; if (!xml.IsEmptyElement) featureStack.Push((xml.Depth, "Simbolo non esportato")); }
            }
            else if (xml.LocalName == "Option" && featureStack.Count != 0 && featureStack.Peek().Feature != "Simbolo non esportato")
            {
                string? option = Allowed(Options, local);
                // Options under identity/password features are always withheld,
                // even if the string happens to equal a permitted ordinary option.
                string feature = featureStack.Peek().Feature;
                bool secret = new[] { "UserName", "AccountID", "AccountId", "Password", "PIN", "AccessCode" }.Any(x => feature.StartsWith(x + " (", StringComparison.OrdinalIgnoreCase));
                result.Add(new(scope, feature, secret ? null : option, secret || option is null ? "Opzione omessa per privacy o non riconosciuta" : "Opzione XML verificata; significato proprietario da validare"));
            }
            else if (xml.LocalName == "ParameterInit" && (Allowed(Features, local) ?? Allowed(Parameters, local)) is string parameter)
                result.Add(new(scope, parameter, null, "Parametro presente; valore sempre omesso"));
            if (result.Count >= 256) { result.Add(new(scope, "Limite", null, "Raccolta limitata a 256 campi")); break; }
        }
        result.Add(new(scope, "Simboli non riconosciuti", null, hidden == 0 ? "Nessuno" : "Presenti, nomi e valori omessi"));
        return result.ToArray();
    }

    internal static SchemaEvidence[] Read(string queueName)
    {
        var result = new List<SchemaEvidence>();
        try
        {
            using var server = new LocalPrintServer(PrintSystemDesiredAccess.EnumerateServer);
            using var queue = new PrintQueue(server, queueName, PrintSystemDesiredAccess.UsePrinter);
            Read("PrintTicket globale (predefinito)", () => queue.DefaultPrintTicket?.GetXmlStream());
            Read("PrintTicket utente corrente", () => queue.UserPrintTicket?.GetXmlStream());
            Read("PrintCapabilities (opzioni supportate, non attive)", queue.GetPrintCapabilitiesAsXml);
        }
        catch { result.Add(new("PrintSchema", "Accesso", null, "Non disponibile; non implica preferenze disabilitate")); }
        return result.ToArray();

        void Read(string scope, Func<MemoryStream?> read)
        {
            MemoryStream? stream = null;
            try
            {
                stream = read();
                if (stream is null) { result.Add(new(scope, "Disponibilità", null, "Non disponibile")); return; }
                stream.Position = 0;
                result.AddRange(Filter(stream, scope));
            }
            catch { result.Add(new(scope, "Lettura", null, "Non verificabile o XML non valido/non consentito")); }
            finally
            {
                if (stream is not null)
                {
                    if (stream.TryGetBuffer(out var bytes)) CryptographicOperations.ZeroMemory(bytes.AsSpan());
                    stream.Dispose();
                }
            }
        }
    }
}
