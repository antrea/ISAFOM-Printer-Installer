using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Isafom.Collector;

namespace Isafom.ConfigExtractor;

public sealed record SentinelDifference(string Area,string Slot,string Name,string Change,int? BeforeBytes,int? AfterBytes,bool ContainsUtf16Pattern,bool ContainsUtf8Pattern,bool ExactStringMatch,
    uint? BeforeRegistryType=null,uint? AfterRegistryType=null,bool IsWindowsChangeIdentifier=false);
public sealed record RestorationAssessment(bool? AllReadableBytesRestored,bool? ConfigurationExcludingChangeIdRestored,
    SentinelDifference[] RemainingConfigurationDifferences,SentinelDifference[] WindowsChangeIdentifierDifferences);
public sealed record SentinelReport(int SchemaVersion,string Field,DateTime GeneratedUtc,bool BaselineComplete,bool ChangedSnapshotComplete,
    bool RestoredSnapshotComplete,bool? ExactReadableContentRestored,SentinelDifference[] Changes,bool ProductionWriteMethodVerified,string Limitation,
    RestorationAssessment? RestorationDetails=null);

// These objects have no public serializable byte/path properties. They live in
// this process only, are bounded, and are cleared after the three observations.
internal sealed partial class SentinelSnapshot : IDisposable
{
    internal sealed record Cell(string Area,string SafeName,uint Kind,byte[] Bytes,bool IsWindowsChangeIdentifier=false);
    private readonly Dictionary<string,Cell> cells=new(StringComparer.OrdinalIgnoreCase);
    internal bool Complete {get;private set;}=true;
    internal int Count=>cells.Count;
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,EntryPoint="RegQueryValueExW")]
    private static extern int Query(SafeRegistryHandle key,string name,IntPtr reserved,out uint type,byte[]? data,ref uint size);
    internal static SentinelSnapshot Capture(string queue,string driver)
    {
        var result=new SentinelSnapshot();int keys=0;int budget=8*1024*1024;
        Add(RegistryHive.LocalMachine,@"SYSTEM\CurrentControlSet\Control\Print\Printers\"+queue,"Macchina / coda",null,2);
        Add(RegistryHive.CurrentUser,@"Printers\DevModePerUser","Utente / DevModePerUser",queue,0);
        Add(RegistryHive.CurrentUser,@"Printers\DevModes2","Utente / DevModes2",queue,0);
        foreach(string vendor in new[]{"Generic","Kyocera","Kyocera Mita","KYOCERA Document Solutions"})
        {
            Add(RegistryHive.CurrentUser,@"Software\"+vendor+"\\"+queue,"Utente / "+vendor+" / coda",null,4);
            Add(RegistryHive.CurrentUser,@"Software\"+vendor+"\\"+driver+"\\"+queue,"Utente / "+vendor+" / driver / coda",null,4);
        }
        foreach(uint level in new uint[]{8,9})
        {
            byte[]? bytes=PublicDevMode.SnapshotBytes(queue,level);
            if(bytes is null){result.Complete=false;continue;}
            if(bytes.Length>budget){CryptographicOperations.ZeroMemory(bytes);result.Complete=false;continue;}
            budget-=bytes.Length;string area=level==8?"DEVMODE globale":"DEVMODE utente corrente";
            result.cells.Add("Native:"+level,new(area,"DEVMODE completo, valori omessi",3,bytes));
        }
        return result;

        void Add(RegistryHive hive,string path,string area,string? valueOnly,int depth)
        {
            try
            {
                using var root=RegistryKey.OpenBaseKey(hive,RegistryView.Registry64);
                using var key=root.OpenSubKey(path,writable:false);
                if(key is not null)Walk(key,"",0);
            }
            catch{result.Complete=false;}
            void Walk(RegistryKey key,string relative,int level)
            {
                if(keys++>=128||result.cells.Count>=1024){result.Complete=false;return;}
                foreach(string name in valueOnly is null?key.GetValueNames():[valueOnly])
                {
                    if(result.cells.Count>=1024){result.Complete=false;return;}
                    uint size=0;int status=Query(key.Handle,name,IntPtr.Zero,out uint kind,null,ref size);
                    if(status==2&&valueOnly is not null)continue;
                    if(status!=0||size>1024*1024||size>budget){result.Complete=false;continue;}
                    var bytes=new byte[size];uint actual=size;
                    if(Query(key.Handle,name,IntPtr.Zero,out kind,bytes,ref actual)!=0||actual>size){CryptographicOperations.ZeroMemory(bytes);result.Complete=false;continue;}
                    if(actual!=size){var resized=bytes.AsSpan(0,(int)actual).ToArray();CryptographicOperations.ZeroMemory(bytes);bytes=resized;}
                    budget-=(int)actual;
                    // Only the exact Windows queue-root ChangeID DWORD is
                    // classified as a change identifier. Vendor lookalikes,
                    // added/removed values and other types are never excluded.
                    bool changeId=hive==RegistryHive.LocalMachine&&relative.Length==0&&name.Equals("ChangeID",StringComparison.OrdinalIgnoreCase)&&kind==4&&actual==4;
                    string safe=changeId?"ChangeID":valueOnly is null?StorageNamePolicy.Canonical(name):"Valore della coda selezionata";
                    result.cells.Add(hive+"\0"+path+"\0"+relative+"\0"+name,new(area,safe,kind,bytes,changeId));
                }
                if(valueOnly is not null)return;
                var children=key.GetSubKeyNames();
                if(level==depth){if(children.Length>0)result.Complete=false;return;}
                foreach(string child in children.Order(StringComparer.OrdinalIgnoreCase))
                {
                    using var sub=key.OpenSubKey(child,writable:false);
                    if(sub is not null)Walk(sub,relative+"\\"+child,level+1);
                }
            }
        }
    }
    internal bool Contains(string pattern)
    {
        var utf16=Encoding.Unicode.GetBytes(pattern);var utf8=Encoding.UTF8.GetBytes(pattern);
        try{return cells.Values.Any(v=>v.Bytes.AsSpan().IndexOf(utf16)>=0||v.Bytes.AsSpan().IndexOf(utf8)>=0);}
        finally{CryptographicOperations.ZeroMemory(utf16);CryptographicOperations.ZeroMemory(utf8);}
    }
    internal SentinelDifference[] Compare(SentinelSnapshot other,string pattern)
    {
        var changes=new List<SentinelDifference>();var utf16=Encoding.Unicode.GetBytes(pattern);var utf8=Encoding.UTF8.GetBytes(pattern);int index=0;
        try
        {
            foreach(string key in cells.Keys.Union(other.cells.Keys,StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase))
            {
                index++;cells.TryGetValue(key,out var before);other.cells.TryGetValue(key,out var after);
                if(before is not null&&after is not null&&before.Kind==after.Kind&&before.Bytes.AsSpan().SequenceEqual(after.Bytes))continue;
                var item=after??before!;bool u16=after is not null&&after.Bytes.AsSpan().IndexOf(utf16)>=0;
                bool u8=after is not null&&after.Bytes.AsSpan().IndexOf(utf8)>=0;
                bool exact=after is not null&&after.Kind is 1 or 2&&ExactUtf16(after.Bytes,utf16);
                bool changeId=before is not null&&after is not null&&before.IsWindowsChangeIdentifier&&after.IsWindowsChangeIdentifier&&before.Kind==4&&after.Kind==4&&before.Bytes.Length==4&&after.Bytes.Length==4;
                changes.Add(new(item.Area,"V"+index.ToString("D4"),item.SafeName,before is null?"Comparso":after is null?"Rimosso":"Contenuto cambiato",before?.Bytes.Length,after?.Bytes.Length,u16,u8,exact,before?.Kind,after?.Kind,changeId));
            }
        }
        finally{CryptographicOperations.ZeroMemory(utf16);CryptographicOperations.ZeroMemory(utf8);}
        return changes.ToArray();
    }
    private static bool ExactUtf16(byte[] data,byte[] pattern)
    {
        if(data.Length%2!=0)return false;
        int count=data.Length;while(count>=2&&data[count-1]==0&&data[count-2]==0)count-=2;
        return data.AsSpan(0,count).SequenceEqual(pattern);
    }
    internal bool? Restored(SentinelSnapshot other)=>Complete&&other.Complete?Compare(other,"\0ISAFOM_PATTERN_NOT_PRESENT\0").Length==0:null;
    internal RestorationAssessment AssessRestored(SentinelSnapshot other)
    {
        var differences=Compare(other,"\0ISAFOM_PATTERN_NOT_PRESENT\0");
        var configuration=differences.Where(d=>!d.IsWindowsChangeIdentifier).ToArray();
        return new(Complete&&other.Complete?differences.Length==0:null,Complete&&other.Complete?configuration.Length==0:null,
            configuration,differences.Where(d=>d.IsWindowsChangeIdentifier).ToArray());
    }
    internal static SentinelSnapshot Fixture(params (string Key,string Name,uint Kind,byte[] Bytes)[] items)
    {
        var snapshot=new SentinelSnapshot();foreach(var item in items)snapshot.cells.Add(item.Key,new("Test",StorageNamePolicy.Canonical(item.Name),item.Kind,item.Bytes.ToArray()));return snapshot;
    }
    internal static SentinelSnapshot FixtureCells(params (string Key,Cell Value)[] items)
    {
        var snapshot=new SentinelSnapshot();foreach(var item in items)snapshot.cells.Add(item.Key,item.Value with {Bytes=item.Value.Bytes.ToArray()});return snapshot;
    }
    public void Dispose(){foreach(var cell in cells.Values)CryptographicOperations.ZeroMemory(cell.Bytes);cells.Clear();}
}
