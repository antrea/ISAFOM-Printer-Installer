using System.IO;
using System.Printing;
using System.Printing.Interop;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml;
using Isafom.Collector;

namespace Isafom.ConfigExtractor;

public sealed record DirectConversionCase(string Case,string BaseDevMode,string State,bool? MonochromeRoundTripped,
    RequestedCodeReadback? Codes,int? CandidateDevModeBytes,bool? RequestedCodeLiteralInDevMode,int RemovedPrivateSnapshots,
    string? FailureStage=null,string? FailureParameter=null,int? FailureHResult=null);

internal static class DirectConversionProbe
{
    private const string Framework="http://schemas.microsoft.com/windows/2003/08/printing/printschemaframework";
    private const string Kx="http://schemas.kyocera.com/printers/KxDriver/v_1_0_";
    private const string Keywords="http://schemas.microsoft.com/windows/2003/08/printing/printschemakeywords";
    internal static int Run(string root,QueueChoice choice)
    {
        using var baseline=SentinelSnapshot.Capture(choice.Name,choice.DriverName);
        if(!baseline.Complete)throw new IOException("IncompleteProbeBaseline");
        using var server=new LocalPrintServer(PrintSystemDesiredAccess.EnumerateServer);
        using var queue=new PrintQueue(server,choice.Name,PrintSystemDesiredAccess.UsePrinter);
        using var converter=new PrintTicketConverter(choice.Name,1);
        var cases=new List<DirectConversionCase>();
        string expected;
        do{expected="0"+RandomNumberGenerator.GetInt32(1000).ToString("D3",System.Globalization.CultureInfo.InvariantCulture);}
        while(!AbsentFromOriginalTickets(expected));
        using var capsStream=queue.GetPrintCapabilitiesAsXml();var caps=Load(capsStream);
        var control=new PrintTicket{OutputColor=OutputColor.Monochrome,Duplexing=Duplexing.TwoSidedLongEdge};
        Convert("StandardControlNoMerge",control,BaseDevModeType.UserDefault,0);
        Convert("UnchangedCurrentTicketControl",queue.UserPrintTicket,BaseDevModeType.UserDefault,0);
        using(var source=queue.UserPrintTicket.GetXmlStream())
        {
            var preserved=Load(source);ApplyVendorRequest(preserved,caps,expected);
            Convert("UserTicketKeepingPrivateSnapshotNoMerge",Ticket(preserved),BaseDevModeType.UserDefault,0);
        }
        foreach(var baseType in new[]{BaseDevModeType.UserDefault,BaseDevModeType.PrinterDefault})
        {
            var fresh=NewTicket();ApplyVendorRequest(fresh,caps,expected);
            var ticket=Ticket(fresh);Convert("FreshVendorTicketNoMerge",ticket,baseType,0);
        }
        using(var source=queue.UserPrintTicket.GetXmlStream())
        {
            var stripped=Load(source);int removed=0;
            foreach(var element in stripped.DocumentElement!.ChildNodes.OfType<XmlElement>().ToArray())
            {
                if(element.LocalName=="ParameterInit"&&element.NamespaceURI==Framework&&Named(element,"PageDevmodeSnapshot",Kx))
                {stripped.DocumentElement.RemoveChild(element);removed++;}
            }
            ApplyVendorRequest(stripped,caps,expected);
            var ticket=Ticket(stripped);Convert("UserTicketWithoutPrivateSnapshotNoMerge",ticket,BaseDevModeType.UserDefault,removed);
        }
        using var current=SentinelSnapshot.Capture(choice.Name,choice.DriverName);
        var integrity=baseline.AssessRestored(current);
        var report=new{SchemaVersion=1,ToolVersion=DossierWriter.Version,GeneratedUtc=DateTime.UtcNow,
            Hypothesis="MergeAndValidate may discard vendor credentials; compare direct conversions without that stage, in memory only.",
            Cases=cases.ToArray(),Integrity=integrity,ActualConfigurationWrites=false,PersonalValuesExported=false,RawXmlOrDevModeExported=false,
            NameFieldMappingVerified=false,ProductionAdapterReady=false,
            Scope="Selected queue registry/current-user DEVMODE snapshots only; no printing, Commit, setter or vendor utility execution.",
            Source="https://learn.microsoft.com/en-us/dotnet/api/system.printing.interop.printticketconverter.convertprinttickettodevmode?view=windowsdesktop-10.0"};
        string json=JsonSerializer.Serialize(report,new JsonSerializerOptions{WriteIndented=true});
        if(json.Contains(expected,StringComparison.Ordinal))throw new IOException("SyntheticValueLeakRefused");
        byte[] bytes=Encoding.UTF8.GetBytes(json);
        long existing=new DirectoryInfo(root).EnumerateFiles("*",SearchOption.AllDirectories).Sum(f=>f.Length);
        if(existing+bytes.Length+65536>500L*1024*1024)throw new IOException("WorkspaceBudgetExceeded");
        string name="Prova_Conversione_Diretta_"+DateTime.Now.ToString("yyyyMMdd_HHmmss_fff")+".json";
        using(var file=new FileStream(Path.Combine(root,"raccolta-locale",name),FileMode.CreateNew,FileAccess.Write,FileShare.None))file.Write(bytes);
        Console.WriteLine(JsonSerializer.Serialize(new{Event="DirectConversionProbe",Report=name,Cases=cases.ToArray(),Integrity=integrity,NoConfigurationWrites=true}));
        return integrity.ConfigurationExcludingChangeIdRestored==true?0:3;

        bool AbsentFromOriginalTickets(string code)
        {
            foreach(var original in new[]{queue.DefaultPrintTicket,queue.UserPrintTicket})
            {
                using var stream=original.GetXmlStream();
                var comparison=PersonalReadbackReader.CompareRequestedCode(stream,"Original",code);
                if(comparison.AccountMatchesRequested==true||comparison.PrivateMatchesRequested==true)return false;
            }
            return true;
        }
        void Convert(string label,PrintTicket input,BaseDevModeType baseType,int removed)
        {
            byte[]? devMode=null;byte[]? literal=null;byte[]? unicode=null;
            string stage="ConvertPrintTicketToDevMode";
            try
            {
                // Deliberately no MergeAndValidate: this is the distinct
                // hypothesis. The returned buffer is never passed to a setter.
                devMode=converter.ConvertPrintTicketToDevMode(input,baseType,PrintTicketScope.JobScope);
                literal=Encoding.ASCII.GetBytes(expected);unicode=Encoding.Unicode.GetBytes(expected);
                bool contains=devMode.AsSpan().IndexOf(literal)>=0||devMode.AsSpan().IndexOf(unicode)>=0;
                stage="ConvertDevModeToPrintTicket";
                var roundTrip=converter.ConvertDevModeToPrintTicket(devMode,PrintTicketScope.JobScope);
                using var stream=roundTrip.GetXmlStream();
                stage="CompareRequestedCode";
                var comparison=PersonalReadbackReader.CompareRequestedCode(stream,"ConvertedInMemory",expected);
                cases.Add(new(label,baseType.ToString(),"ConvertedInMemory",roundTrip.OutputColor==OutputColor.Monochrome,comparison,devMode.Length,contains,removed));
            }
            catch(Exception error)
            {
                string? parameter=error is ArgumentException argument&&argument.ParamName is "printTicket" or "baseType" or "scope" or "devMode"?argument.ParamName:null;
                cases.Add(new(label,baseType.ToString(),error.GetType().Name,null,null,devMode?.Length,null,removed,stage,parameter,error.HResult));
            }
            finally
            {
                if(devMode is not null)CryptographicOperations.ZeroMemory(devMode);
                if(literal is not null)CryptographicOperations.ZeroMemory(literal);
                if(unicode is not null)CryptographicOperations.ZeroMemory(unicode);
            }
        }
    }
    private static XmlDocument Load(Stream stream)
    {
        using var reader=XmlReader.Create(stream,new XmlReaderSettings{DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null,MaxCharactersInDocument=2*1024*1024,CloseInput=false});
        var doc=new XmlDocument{XmlResolver=null};doc.Load(reader);return doc;
    }
    private static bool Named(XmlElement element,string local,string ns)
    {
        string name=element.GetAttribute("name");int colon=name.IndexOf(':');
        return colon>0&&name[(colon+1)..]==local&&element.GetNamespaceOfPrefix(name[..colon])==ns;
    }
    internal static XmlDocument NewTicket()
    {
        var doc=new XmlDocument{XmlResolver=null};var root=doc.CreateElement("psf","PrintTicket",Framework);
        root.SetAttribute("version","1");root.SetAttribute("xmlns:kx",Kx);root.SetAttribute("xmlns:psk",Keywords);
        root.SetAttribute("xmlns:xsi","http://www.w3.org/2001/XMLSchema-instance");root.SetAttribute("xmlns:xsd","http://www.w3.org/2001/XMLSchema");
        doc.AppendChild(root);return doc;
    }
    internal static void ApplyVendorRequest(XmlDocument doc,XmlDocument caps,string code)
    {
        var root=doc.DocumentElement!;
        // Preserve the capability namespace bindings needed by its copied
        // scored properties; they are schema metadata, never exported here.
        foreach(XmlAttribute attribute in caps.DocumentElement!.Attributes)
            if(attribute.NamespaceURI=="http://www.w3.org/2000/xmlns/")root.Attributes.Append((XmlAttribute)doc.ImportNode(attribute,true));
        root.SetAttribute("xmlns:xsi","http://www.w3.org/2001/XMLSchema-instance");root.SetAttribute("xmlns:xsd","http://www.w3.org/2001/XMLSchema");
        foreach(var element in root.ChildNodes.OfType<XmlElement>().ToArray())
            if(element.NamespaceURI==Framework&&((element.LocalName=="Feature"&&(Named(element,"JobAccounting",Kx)||Named(element,"JobStorage",Kx)||Named(element,"PageOutputColor",Keywords)))||
                (element.LocalName=="ParameterInit"&&new[]{"JobAccountID","JobStorageAccessCode","JobStorageUseAccessCode"}.Any(n=>Named(element,n,Kx)))))root.RemoveChild(element);
        foreach(var choice in new[]{("JobAccounting","UseSpecificID",Kx),("JobStorage","PrivatePrint",Kx),("PageOutputColor","Monochrome",Keywords)})
        {
            var feature=caps.GetElementsByTagName("Feature",Framework).OfType<XmlElement>().Single(n=>Named(n,choice.Item1,choice.Item3));
            var option=feature.ChildNodes.OfType<XmlElement>().Single(n=>n.LocalName=="Option"&&n.NamespaceURI==Framework&&Named(n,choice.Item2,choice.Item3));
            var copy=doc.CreateElement("psf","Feature",Framework);copy.SetAttribute("name",feature.GetAttribute("name"));
            var selected=doc.CreateElement("psf","Option",Framework);selected.SetAttribute("name",option.GetAttribute("name"));
            foreach(var property in option.ChildNodes.OfType<XmlElement>().Where(n=>n.LocalName=="ScoredProperty"&&n.NamespaceURI==Framework))selected.AppendChild(doc.ImportNode(property,true));
            copy.AppendChild(selected);root.AppendChild(copy);
        }
        foreach(string parameter in new[]{"JobAccountID","JobStorageAccessCode","JobStorageUseAccessCode"})
        {
            var definition=caps.GetElementsByTagName("ParameterDef",Framework).OfType<XmlElement>().Single(n=>Named(n,parameter,Kx));
            var init=doc.CreateElement("psf","ParameterInit",Framework);init.SetAttribute("name",definition.GetAttribute("name"));
            var value=doc.CreateElement("psf","Value",Framework);value.SetAttribute("type","http://www.w3.org/2001/XMLSchema-instance","xsd:string");
            value.InnerText=parameter=="JobStorageUseAccessCode"?"On":code;init.AppendChild(value);root.AppendChild(init);
        }
    }
    private static PrintTicket Ticket(XmlDocument doc)
    {
        byte[] bytes=Encoding.UTF8.GetBytes(doc.OuterXml);
        try{using var stream=new MemoryStream(bytes,false);return new PrintTicket(stream);}
        finally{CryptographicOperations.ZeroMemory(bytes);}
    }
}
