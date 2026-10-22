using System.IO;
using System.Text;
using System.Text.Json;
using Isafom.Collector;

namespace Isafom.ConfigExtractor;

// JSON-line observations operated by the assistant. Commands contain labels
// only: no names or credentials are accepted, generated or echoed. The human
// operates the Kyocera UI; this process has no configuration writer.
internal static class DirectObservation
{
    private static readonly JsonSerializerOptions Compact=new();
    private static readonly string[] Fields=["Nome utente","Account ID amministratore","Account ID preferenze","Password privata"];
    internal static int Run(string root,QueueChoice queue)
    {
        SentinelSnapshot? baseline=null;SentinelSnapshot? changed=null;string? field=null;
        try
        {
            Reply(new {Event="Ready",ProtocolVersion=1,ToolVersion=DossierWriter.Version,NoConfigurationWrites=true,RawDataExported=false});
            for(string? command;(command=Console.ReadLine()) is not null;)
            {
                try
                {
                    if(command=="quit"){Reply(new {Event="Closed",RawSnapshotsSaved=false});return 0;}
                    if(command.StartsWith("baseline ",StringComparison.Ordinal))
                    {
                        string label=command[9..];
                        if(!Fields.Contains(label)){Reply(new {Event="Error",Reason="UnknownFieldLabel"});continue;}
                        if(baseline is not null){Reply(new {Event="Error",Reason="ExistingBaselineRetained"});continue;}
                        baseline=SentinelSnapshot.Capture(queue.Name,queue.DriverName);field=label;
                        Reply(new {Event="Baseline",Field=field,baseline.Complete,Entries=baseline.Count,RawDataExported=false});continue;
                    }
                    if(command=="changed")
                    {
                        if(baseline is null||!baseline.Complete){Reply(new {Event="Error",Reason="CompleteBaselineRequired"});continue;}
                        changed?.Dispose();changed=SentinelSnapshot.Capture(queue.Name,queue.DriverName);
                        Reply(new {Event="Changed",Field=field,changed.Complete,Entries=changed.Count,Changes=baseline.Compare(changed,"ISAFOM_TEST_")});continue;
                    }
                    if(command=="restored")
                    {
                        if(baseline is null||changed is null){Reply(new {Event="Error",Reason="BaselineAndChangedObservationRequired"});continue;}
                        using var restored=SentinelSnapshot.Capture(queue.Name,queue.DriverName);
                        var assessment=baseline.AssessRestored(restored);
                        var report=new SentinelReport(2,field!,DateTime.UtcNow,baseline.Complete,changed.Complete,restored.Complete,assessment.AllReadableBytesRestored,
                            baseline.Compare(changed,"ISAFOM_TEST_"),false,"Direct observations without a user-facing tool. Only the documented queue-root ChangeID DWORD is separated. No personal value, digest, raw blob or offset is exported.",assessment);
                        string json=JsonSerializer.Serialize(report,Compact);byte[] bytes=Encoding.UTF8.GetBytes(json);
                        long existing=new DirectoryInfo(root).EnumerateFiles("*",SearchOption.AllDirectories).Sum(f=>f.Length);
                        if(existing+bytes.Length+65536>500L*1024*1024)throw new IOException("WorkspaceBudgetExceeded");
                        string name="Osservazione_Diretta_"+DateTime.Now.ToString("yyyyMMdd_HHmmss_fff")+".json";
                        using(var output=new FileStream(Path.Combine(root,"raccolta-locale",name),FileMode.CreateNew,FileAccess.Write,FileShare.None))output.Write(bytes);
                        Reply(new {Event="Restored",Field=field,Report=name,Assessment=assessment});continue;
                    }
                    if(command=="reset")
                    {
                        if(baseline is not null)
                        {
                            using var current=SentinelSnapshot.Capture(queue.Name,queue.DriverName);
                            if(baseline.AssessRestored(current).ConfigurationExcludingChangeIdRestored!=true){Reply(new {Event="Error",Reason="RestorationNotVerifiedBaselineRetained"});continue;}
                        }
                        baseline?.Dispose();changed?.Dispose();baseline=changed=null;field=null;
                        Reply(new {Event="Reset",RawSnapshotsSaved=false});continue;
                    }
                    Reply(new {Event="Error",Reason="UnknownCommand"});
                }
                catch(Exception error){Reply(new {Event="Error",Reason=error.GetType().Name,RawDetailsExported=false});}
            }
            return 0;
        }
        finally{baseline?.Dispose();changed?.Dispose();}
    }
    private static void Reply(object value){Console.WriteLine(JsonSerializer.Serialize(value,Compact));Console.Out.Flush();}
}
