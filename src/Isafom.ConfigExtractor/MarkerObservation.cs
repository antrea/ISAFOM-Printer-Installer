using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Isafom.Collector;

namespace Isafom.ConfigExtractor;

internal static class MarkerObservation
{
    internal static int Run(string root,QueueChoice queue)
    {
        using var baseline=SentinelSnapshot.Capture(queue.Name,queue.DriverName);
        if(!baseline.Complete){Reply(new{Event="Error",Reason="IncompleteBaseline"});return 3;}
        string name="",code="";bool clear=false;
        for(int n=0;n<32;n++)
        {
            name="ISAFOM_TEST_"+Convert.ToHexString(RandomNumberGenerator.GetBytes(4));
            code=RandomNumberGenerator.GetInt32(1000,10000).ToString("D4",CultureInfo.InvariantCulture);
            if(baseline.FindMarkers(baseline,name,code).Length==0){clear=true;break;}
        }
        if(!clear){Reply(new{Event="Error",Reason="MarkerCollision"});return 3;}
        // The human explicitly requested shared synthetic markers. Display
        // them once, to the console only. Evidence never contains the values.
        Reply(new{Event="Ready",Protocol="MarkerObservation/1",NameSentinel=name,CodeSentinel=code,
            baseline.Complete,Entries=baseline.Count,BaselineMarkerMatches=0,RawDataExported=false,NoConfigurationWrites=true});
        for(string? command;(command=Console.ReadLine()) is not null;)
        {
            if(command is not ("changed" or "restored" or "check" or "quit"))
            {Reply(new{Event="Error",Reason="UnknownCommand"});continue;}
            try
            {
                using var current=SentinelSnapshot.Capture(queue.Name,queue.DriverName);
                var assessment=baseline.AssessRestored(current);
                if(command=="quit")
                {
                    if(assessment.ConfigurationExcludingChangeIdRestored!=true){Reply(new{Event="Error",Reason="RestorationNotVerifiedBaselineRetained"});continue;}
                    Reply(new{Event="Closed",RawSnapshotsSaved=false});return 0;
                }
                var locations=baseline.FindMarkers(current,name,code);
                var changes=baseline.Compare(current,"ISAFOM_TEST_");
                var report=new{SchemaVersion=1,Protocol="MarkerObservation/1",Phase=command,GeneratedUtc=DateTime.UtcNow,
                    BaselineComplete=baseline.Complete,CurrentComplete=current.Complete,Entries=current.Count,Locations=locations,Changes=changes,Assessment=assessment,
                    SimultaneousFields=true,IndividualFieldAttributionVerified=false,ProductionWriteMethodVerified=false,
                    Scope="Bounded selected queue registry keys, current user's selected-queue keys and native DEVMODE levels 8/9. No disk-wide or other-user search.",
                    Limitation="Matches locate literal markers, not a supported setter. One shared code cannot distinguish accounting from private password. Missing matches do not prove missing settings.",
                    RealConfigurationWrites=false,MarkerValuesExported=false,RawDataExported=false};
                string? file=null;
                if(command!="check")
                {
                    byte[] bytes=Encoding.UTF8.GetBytes(JsonSerializer.Serialize(report));
                    long existing=new DirectoryInfo(root).EnumerateFiles("*",SearchOption.AllDirectories).Sum(f=>f.Length);
                    if(existing+bytes.Length+65536>500L*1024*1024)throw new IOException("WorkspaceBudgetExceeded");
                    file="Sentinelle_Condivise_"+DateTime.Now.ToString("yyyyMMdd_HHmmss_fff")+".json";
                    using var output=new FileStream(Path.Combine(root,"raccolta-locale",file),FileMode.CreateNew,FileAccess.Write,FileShare.None);
                    output.Write(bytes);
                }
                Reply(new{Event="Observation",Phase=command,Report=file,Complete=current.Complete,Entries=current.Count,Locations=locations,Changes=changes,Assessment=assessment});
            }
            catch(Exception error){Reply(new{Event="Error",Reason=error.GetType().Name,RawDetailsExported=false});}
        }
        return 0;
    }
    private static void Reply(object value){Console.WriteLine(JsonSerializer.Serialize(value));Console.Out.Flush();}
}
