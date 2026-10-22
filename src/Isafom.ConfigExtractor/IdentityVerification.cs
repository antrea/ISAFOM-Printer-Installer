using System.IO;
using System.Printing;
using System.Text;
using System.Text.Json;
using Isafom.Collector;

namespace Isafom.ConfigExtractor;

public sealed record IdentityVerificationResult(string ToolVersion,bool ReadOnly,bool NameLiteralObservedInSelectedSnapshots,
    bool? ExactGuiUserNameMatches,RequestedCodeReadback[] Codes,PersonalReadback[] Modes,bool SharedCodeLengthValidated,
    bool? PasswordPromptDisabled,RestorationAssessment Integrity,bool AllPersonalFieldsVerified,bool ProductionWriteMethodVerified,string Limitation);

internal static class IdentityVerification
{
    // Input objects never become part of a serializable result or exception.
    internal sealed record Request(string UserName,string AccountId);
    internal static Request ReadRequest(TextReader reader)
    {
        var buffer=new char[2048];int count=0;
        try
        {
            for(int next;(next=reader.Read())>=0&&next!='\n';)
            {
                if(count==buffer.Length)throw new ArgumentException("IdentityRequestTooLarge");
                buffer[count++]=(char)next;
            }
            using var doc=JsonDocument.Parse(new string(buffer,0,count),new JsonDocumentOptions{MaxDepth=4});
            if(doc.RootElement.ValueKind!=JsonValueKind.Object)throw new ArgumentException("IdentityObjectRequired");
            var fields=doc.RootElement.EnumerateObject().ToArray();
            if(fields.Length!=2||fields.Select(f=>f.Name).Distinct(StringComparer.Ordinal).Count()!=2||
                !doc.RootElement.TryGetProperty("UserName",out var name)||!doc.RootElement.TryGetProperty("AccountId",out var account)||
                name.ValueKind!=JsonValueKind.String||account.ValueKind!=JsonValueKind.String)throw new ArgumentException("TwoIdentityFieldsRequired");
            string identity=name.GetString()!;string code=account.GetString()!;
            if(string.IsNullOrWhiteSpace(identity)||identity.Length>128||identity.Any(char.IsControl)||
                code.Length is <1 or >8||code.Any(c=>c<'0'||c>'9'))throw new ArgumentException("InvalidIdentityInput");
            return new(identity,code);
        }
        finally{Array.Clear(buffer);}
    }
    internal static int Run(QueueChoice choice)
    {
        Console.InputEncoding=new UTF8Encoding(false,true);
        Console.OutputEncoding=new UTF8Encoding(false);
        var input=ReadRequest(Console.In);
        var report=Check(choice,input);
        Console.WriteLine(JsonSerializer.Serialize(report));
        return report.Codes.Any(c=>c.AccountMatchesRequested==false)?4:3;
    }
    internal static IdentityVerificationResult Check(QueueChoice choice,Request input)
    {
        using var before=SentinelSnapshot.Capture(choice.Name,choice.DriverName);
        using var server=new LocalPrintServer(PrintSystemDesiredAccess.EnumerateServer);
        using var queue=new PrintQueue(server,choice.Name,PrintSystemDesiredAccess.UsePrinter);
        var codes=new List<RequestedCodeReadback>();var modes=new List<PersonalReadback>();
        foreach(var pair in new[]{("Globale",queue.DefaultPrintTicket),("Utente corrente",queue.UserPrintTicket)})
        {
            using(var stream=pair.Item2.GetXmlStream())codes.Add(PersonalReadbackReader.CompareRequestedCode(stream,pair.Item1,input.AccountId));
            using(var stream=pair.Item2.GetXmlStream())modes.Add(PersonalReadbackReader.Read(stream,pair.Item1));
        }
        bool nameObserved=before.Contains(input.UserName);
        using var after=SentinelSnapshot.Capture(choice.Name,choice.DriverName);
        return new IdentityVerificationResult(DossierWriter.Version,true,nameObserved,null,codes.ToArray(),modes.ToArray(),input.AccountId.Length==4,
            null,before.AssessRestored(after),false,false,
            "Name literal presence is evidence only: no documented binary field mapping. Empty private PrintTicket values do not certify the GUI password. " +
            "Requested account strings are compared exactly, preserving leading zeros. Expected values, XML, hashes and DEVMODE are never returned. " +
            "Whole blobs remain part of integrity comparison. This read-only check cannot enable or certify the installer.");
    }
}
