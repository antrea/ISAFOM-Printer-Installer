using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Isafom.ConfigExtractor;

public sealed record MarkerPresence(bool NameUtf8,bool NameUtf16,bool CodeUtf8,bool CodeUtf16,bool CodeExactDword)
{
    public bool Any=>NameUtf8||NameUtf16||CodeUtf8||CodeUtf16||CodeExactDword;
}
public sealed record MarkerLocation(string Area,string Location,string Slot,string Name,uint? RegistryType,int? Bytes,
    bool ContentChanged,MarkerPresence Before,MarkerPresence After);

internal sealed partial class SentinelSnapshot
{
    // Search only the selected queue/current user's bounded snapshots. Raw
    // identifiers, offsets and payloads never become part of the result.
    internal MarkerLocation[] FindMarkers(SentinelSnapshot after,string name,string code)
    {
        if(name.Length<8||code.Length!=4||code.Any(c=>c<'0'||c>'9'))throw new ArgumentException("InvalidMarkers");
        byte[][] patterns=[Encoding.UTF8.GetBytes(name),Encoding.Unicode.GetBytes(name),Encoding.UTF8.GetBytes(code),Encoding.Unicode.GetBytes(code)];
        uint numeric=uint.Parse(code,CultureInfo.InvariantCulture);
        var result=new List<MarkerLocation>();int index=0;
        try
        {
            foreach(string key in cells.Keys.Union(after.cells.Keys,StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase))
            {
                index++;cells.TryGetValue(key,out var beforeCell);after.cells.TryGetValue(key,out var afterCell);
                MarkerPresence Detect(Cell? cell)=>cell is null?new(false,false,false,false,false):new(
                    cell.Bytes.AsSpan().IndexOf(patterns[0])>=0,cell.Bytes.AsSpan().IndexOf(patterns[1])>=0,
                    cell.Bytes.AsSpan().IndexOf(patterns[2])>=0,cell.Bytes.AsSpan().IndexOf(patterns[3])>=0,
                    cell.Kind==4&&cell.Bytes.Length==4&&BinaryPrimitives.ReadUInt32LittleEndian(cell.Bytes)==numeric);
                var before=Detect(beforeCell);var current=Detect(afterCell);
                if(!before.Any&&!current.Any)continue;
                var item=afterCell??beforeCell!;
                bool changed=beforeCell is null||afterCell is null||beforeCell.Kind!=afterCell.Kind||!beforeCell.Bytes.AsSpan().SequenceEqual(afterCell.Bytes);
                result.Add(new(item.Area,MarkerSafeLocation(key),"V"+index.ToString("D4"),item.SafeName,
                    afterCell?.Kind,afterCell?.Bytes.Length,changed,before,current));
            }
            return result.ToArray();
        }
        finally{foreach(var pattern in patterns)CryptographicOperations.ZeroMemory(pattern);}
    }
    private static string MarkerSafeLocation(string key)
    {
        if(key=="Native:8")return "GetPrinter level 8 / global DEVMODE";
        if(key=="Native:9")return "GetPrinter level 9 / current-user DEVMODE";
        string[] parts=key.Split('\0');
        if(parts.Length!=4)return "Fixture / location withheld";
        string root=parts[0]=="LocalMachine"?@"HKLM\SYSTEM\CurrentControlSet\Control\Print\Printers\<selected queue>":
            parts[1].Equals(@"Printers\DevModePerUser",StringComparison.OrdinalIgnoreCase)?@"HKCU\Printers\DevModePerUser":
            parts[1].Equals(@"Printers\DevModes2",StringComparison.OrdinalIgnoreCase)?@"HKCU\Printers\DevModes2":@"HKCU\Software\<selected vendor/driver/queue>";
        string[] safeChildren=["PrinterDriverData","DsDriver","DsSpooler","DsUser","CopyFiles","Control","Spooler","UserSettings","DeviceSettings","Properties","Config"];
        foreach(string child in parts[2].Split('\\',StringSplitOptions.RemoveEmptyEntries))
            root+="\\"+(safeChildren.FirstOrDefault(s=>s.Equals(child,StringComparison.OrdinalIgnoreCase))??"<subkey withheld>");
        return root;
    }
}
