using System.Globalization;
using System.IO;
using System.Xml;

namespace Isafom.ConfigExtractor;

// No DTO has a slot for the actual identity or credential. They are used only
// transiently to compute presence/equality; raw XML is never returned.
public sealed record PersonalReadback(string Scope, string AccountingMode, string StorageMode,
    string AccountValueState, string PrivateCodeValueState, string UseAccessCodeState,
    bool? AccountAndPrivateCodeEqual, bool? BothCodesAsciiDigits, string UserNameMapping);
public sealed record ParameterRule(string Parameter, string Property, string Value);
public sealed record RequestedCodeReadback(string Scope,string AccountValueState,bool? AccountMatchesRequested,
    string PrivateCodeValueState,bool? PrivateMatchesRequested,bool BothLiteralValuesMatch);

public static class PersonalReadbackReader
{
    private const string Framework = "http://schemas.microsoft.com/windows/2003/08/printing/printschemaframework";
    private const string Kx = "http://schemas.kyocera.com/printers/KxDriver/v_1_0_";
    private const string Keywords = "http://schemas.microsoft.com/windows/2003/08/printing/printschemakeywords";
    private static XmlDocument Load(Stream stream)
    {
        var document = new XmlDocument { XmlResolver = null };
        using var reader = XmlReader.Create(stream,new XmlReaderSettings {
            DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 2 * 1024 * 1024, CloseInput = false
        });
        document.Load(reader); return document;
    }
    private static bool Named(XmlElement element, string local, string ns)
    {
        string name = element.GetAttribute("name"); int colon = name.IndexOf(':');
        return colon > 0 && name[(colon+1)..] == local && element.GetNamespaceOfPrefix(name[..colon]) == ns;
    }
    private static IEnumerable<XmlElement> Nodes(XmlDocument document, string kind) =>
        document.GetElementsByTagName(kind, Framework).OfType<XmlElement>();
    private static XmlElement? Child(XmlElement element, string kind) => element.ChildNodes.OfType<XmlElement>().FirstOrDefault(n => n.LocalName == kind && n.NamespaceURI == Framework);
    private static (string State, string? Value) Parameter(XmlDocument doc,string name)
    {
        var nodes = Nodes(doc,"ParameterInit").Where(n=>n.ParentNode==doc.DocumentElement&&Named(n,name,Kx)).ToArray();
        if (nodes.Length == 0) return ("Non esposto",null);
        if (nodes.Length != 1) return ("Ambiguo",null);
        var values = nodes[0].ChildNodes.OfType<XmlElement>().Where(n=>n.LocalName=="Value"&&n.NamespaceURI==Framework).ToArray();
        if (values.Length != 1 || values[0].ChildNodes.OfType<XmlElement>().Any()) return ("Valore non semplice",null);
        string value = values[0].InnerText;
        return (value.Length == 0 ? "Presente ma vuoto" : "Presente non vuoto",value);
    }
    private static string Mode(XmlDocument doc,string name,string[] allowed)
    {
        var features = Nodes(doc,"Feature").Where(n=>n.ParentNode==doc.DocumentElement&&Named(n,name,Kx)).ToArray();
        if (features.Length != 1) return features.Length==0 ? "Non esposto" : "Ambiguo";
        var options = features[0].ChildNodes.OfType<XmlElement>().Where(n=>n.LocalName=="Option"&&n.NamespaceURI==Framework).ToArray();
        if(options.Length!=1)return "Non decodificato";
        // Observed KX 8.5 uses the standard psk:None for global JobStorage.
        // Only this exact standard symbol is accepted under this exact feature.
        if(name=="JobStorage"&&Named(options[0],"None",Keywords))return "None";
        return allowed.FirstOrDefault(option=>Named(options[0],option,Kx)) ?? "Non decodificato";
    }
    public static PersonalReadback Read(Stream stream,string scope)
    {
        var doc=Load(stream);
        if(doc.DocumentElement?.LocalName!="PrintTicket"||doc.DocumentElement.NamespaceURI!=Framework)throw new XmlException("UnexpectedRoot");
        var account=Parameter(doc,"JobAccountID");var code=Parameter(doc,"JobStorageAccessCode");var use=Parameter(doc,"JobStorageUseAccessCode");
        bool comparable=account.State=="Presente non vuoto"&&code.State=="Presente non vuoto";
        bool? equal=comparable ? string.Equals(account.Value,code.Value,StringComparison.Ordinal) : null;
        bool? digits=comparable ? AsciiDigits(account.Value!)&&AsciiDigits(code.Value!) : null;
        // Only a fixed set of symbolic flags can be returned for this specific
        // flag parameter. A default in capabilities is never called active.
        string useState=use.Value is "On" or "Off" or "true" or "false" or "0" or "1" ? use.Value : use.State;
        return new(scope,Mode(doc,"JobAccounting",["UseSpecificID","Off","None","Prompt","PromptForAccountID","PromptForAccountIDAndValidate","DisplayAccountIDList"]),
            Mode(doc,"JobStorage",["PrivatePrint","None","Off","QuickCopy","ProofHold","StoredJob","JobStorage"]),
            account.State,code.State,useState,equal,digits,"Non verificata: distinto da User login e nome lavoro");
    }
    private static bool AsciiDigits(string value)=>value.Length is >=1 and <=8 && value.All(c=>c is >= '0' and <= '9');
    public static RequestedCodeReadback CompareRequestedCode(Stream stream,string scope,string requestedCode)
    {
        if(string.IsNullOrEmpty(requestedCode)||!AsciiDigits(requestedCode))throw new ArgumentException("ExpectedAsciiCodeRequired");
        var doc=Load(stream);
        if(doc.DocumentElement?.LocalName!="PrintTicket"||doc.DocumentElement.NamespaceURI!=Framework)throw new XmlException("UnexpectedRoot");
        var account=Parameter(doc,"JobAccountID");var code=Parameter(doc,"JobStorageAccessCode");
        bool? accountMatch=account.State=="Presente non vuoto"?string.Equals(account.Value,requestedCode,StringComparison.Ordinal):null;
        bool? codeMatch=code.State=="Presente non vuoto"?string.Equals(code.Value,requestedCode,StringComparison.Ordinal):null;
        // Literal readback only: this never certifies the GUI field mapping,
        // active modes, name, a write operation or physical job release.
        return new(scope,account.State,accountMatch,code.State,codeMatch,accountMatch==true&&codeMatch==true);
    }
    public static ParameterRule[] ReadRules(Stream stream)
    {
        var doc=Load(stream);var result=new List<ParameterRule>();
        if(doc.DocumentElement?.LocalName!="PrintCapabilities"||doc.DocumentElement.NamespaceURI!=Framework)throw new XmlException("UnexpectedRoot");
        foreach(string name in new[]{"JobAccountID","JobStorageAccessCode","JobStorageUseAccessCode"})
        foreach(var definition in Nodes(doc,"ParameterDef").Where(n=>Named(n,name,Kx)))
        foreach(var property in definition.ChildNodes.OfType<XmlElement>().Where(n=>n.LocalName=="Property"&&n.NamespaceURI==Framework))
        {
            foreach(string key in new[]{"MinLength","MaxLength","DataType"})
            {
                if(!Named(property,key,Framework)&&!Named(property,key,Keywords))continue;
                string? value=Child(property,"Value")?.InnerText;
                if(key=="DataType")
                {
                    if(value is not null && new[]{"xsd:string","xsd:integer","xsd:boolean","string","integer","boolean"}.Contains(value))result.Add(new(name,key,value));
                }
                else if(int.TryParse(value,NumberStyles.None,CultureInfo.InvariantCulture,out int number)&&number is >=0 and <=4096)
                    result.Add(new(name,key,number.ToString(CultureInfo.InvariantCulture)));
            }
        }
        return result.ToArray();
    }
}
