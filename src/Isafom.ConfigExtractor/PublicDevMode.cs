using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace Isafom.ConfigExtractor;

public static class PublicDevMode
{
    [DllImport("winspool.drv", CharSet=CharSet.Unicode, SetLastError=true)]
    [return:MarshalAs(UnmanagedType.Bool)] private static extern bool OpenPrinter(string name,out IntPtr handle,IntPtr defaults);
    [DllImport("winspool.drv",CharSet=CharSet.Unicode,SetLastError=true)]
    [return:MarshalAs(UnmanagedType.Bool)] private static extern bool GetPrinter(IntPtr handle,uint level,IntPtr buffer,uint bytes,out uint needed);
    [DllImport("winspool.drv")]
    [return:MarshalAs(UnmanagedType.Bool)] private static extern bool ClosePrinter(IntPtr handle);

    // Only published DEVMODEW members/flags. No vendor-private offset, name or
    // form-name field is interpreted or returned.
    private sealed record Member(string Name,int Offset,uint Flag,string Unit);
    private static readonly Member[] Members = [
        new("Orientation",76,0x1,"codice DEVMODE"),new("PaperSize",78,0x2,"codice DMPAPER"),
        new("PaperLength",80,0x4,"0,1 mm"),new("PaperWidth",82,0x8,"0,1 mm"),new("Scale",84,0x10,"percentuale"),
        new("Copies",86,0x100,"copie"),new("DefaultSource",88,0x200,"codice DMBIN"),new("PrintQuality",90,0x400,"dpi o codice DMRES negativo"),
        new("Color",92,0x800,"codice DMCOLOR"),new("Duplex",94,0x1000,"codice DMDUP"),new("YResolution",96,0x2000,"dpi"),
        new("TTOption",98,0x4000,"codice DMTT"),new("Collate",100,0x8000,"codice DMCOLLATE"),
        new("Nup",180,0x40,"codice DMNUP"),new("ICMMethod",188,0x800000,"codice DMICMMETHOD"),
        new("ICMIntent",192,0x1000000,"codice DMICM"),new("MediaType",196,0x2000000,"codice DMMEDIA"),
        new("DitherType",200,0x4000000,"codice DMDITHER")
    ];
    public static Setting[] Parse(ReadOnlySpan<byte> data,string scope)
    {
        const string source="GetPrinter / DEVMODEW pubblico";
        if(data.Length<76)return [new(scope,source,"Struttura","Non valida o incompleta",null,"")];
        int declared=BinaryPrimitives.ReadUInt16LittleEndian(data[68..]);
        if(declared<76||declared>data.Length)return [new(scope,source,"Struttura","Non valida o incompleta",null,"")];
        uint flags=BinaryPrimitives.ReadUInt32LittleEndian(data[72..]);
        var result=new List<Setting>();
        foreach(var member in Members)
        {
            int width=member.Offset<102?2:4;
            if((flags&member.Flag)==0){result.Add(new(scope,source,member.Name,"Campo non dichiarato da dmFields",null,member.Unit));continue;}
            if(member.Offset+width>declared){result.Add(new(scope,source,member.Name,"Campo fuori dalla struttura dichiarata",null,member.Unit));continue;}
            long code=width==2?BinaryPrimitives.ReadInt16LittleEndian(data[member.Offset..]):BinaryPrimitives.ReadUInt32LittleEndian(data[member.Offset..]);
            string? symbol=(member.Name,code) switch {
                ("Orientation",1)=>"Portrait",("Orientation",2)=>"Landscape",("Color",1)=>"Monochrome",("Color",2)=>"Color",
                ("Duplex",1)=>"OneSided",("Duplex",2)=>"TwoSidedLongEdge",("Duplex",3)=>"TwoSidedShortEdge",
                ("Collate",0)=>"False",("Collate",1)=>"True",("PaperSize",9)=>"ISOA4",("PaperSize",8)=>"ISOA3",("PaperSize",1)=>"NorthAmericaLetter",_=>null};
            result.Add(new(scope,source,member.Name,"Letto",JsonSerializer.SerializeToElement(new {Code=code,Symbol=symbol}),member.Unit));
        }
        return result.ToArray();
    }
    public static Setting[] Read(string queue,uint level)
    {
        string scope=level==8?"DEVMODE globale":"DEVMODE utente corrente";
        Setting[] unavailable=[new(scope,"GetPrinter","Disponibilità","Non verificabile",null,"")];
        if(level is not (8 or 9)||!OpenPrinter(queue,out var handle,IntPtr.Zero))return unavailable;
        IntPtr buffer=IntPtr.Zero;uint size=0;
        try
        {
            GetPrinter(handle,level,IntPtr.Zero,0,out size);
            if(size<IntPtr.Size||size>1024*1024)return unavailable;
            buffer=Marshal.AllocHGlobal((int)size);
            if(!GetPrinter(handle,level,buffer,size,out _))return unavailable;
            IntPtr dm=Marshal.ReadIntPtr(buffer);long offset=dm.ToInt64()-buffer.ToInt64();
            if(dm==IntPtr.Zero||offset<0||offset+76>size)return unavailable;
            int publicBytes=(ushort)Marshal.ReadInt16(dm,68),extra=(ushort)Marshal.ReadInt16(dm,70);
            if(publicBytes<76||offset+publicBytes+extra>size)return unavailable;
            var data=new byte[publicBytes];
            try{Marshal.Copy(dm,data,0,data.Length);return Parse(data,scope);}
            finally{CryptographicOperations.ZeroMemory(data);}
        }
        catch{return unavailable;}
        finally
        {
            if(buffer!=IntPtr.Zero){for(int i=0;i<size;i++)Marshal.WriteByte(buffer,i,0);Marshal.FreeHGlobal(buffer);}
            ClosePrinter(handle);
        }
    }

    // Private to the differential study. The caller owns and clears the bytes;
    // this result can never enter a report DTO or an archive.
    internal static byte[]? SnapshotBytes(string queue,uint level)
    {
        if(level is not (8 or 9)||!OpenPrinter(queue,out var handle,IntPtr.Zero))return null;
        IntPtr buffer=IntPtr.Zero;uint size=0;
        try
        {
            GetPrinter(handle,level,IntPtr.Zero,0,out size);
            if(size<IntPtr.Size||size>1024*1024)return null;
            buffer=Marshal.AllocHGlobal((int)size);
            if(!GetPrinter(handle,level,buffer,size,out _))return null;
            IntPtr dm=Marshal.ReadIntPtr(buffer);long offset=dm.ToInt64()-buffer.ToInt64();
            if(dm==IntPtr.Zero||offset<0||offset+76>size)return null;
            int publicBytes=(ushort)Marshal.ReadInt16(dm,68),extra=(ushort)Marshal.ReadInt16(dm,70);
            if(publicBytes<76||offset+publicBytes+extra>size)return null;
            var bytes=new byte[publicBytes+extra];Marshal.Copy(dm,bytes,0,bytes.Length);return bytes;
        }
        catch{return null;}
        finally
        {
            if(buffer!=IntPtr.Zero){for(int i=0;i<size;i++)Marshal.WriteByte(buffer,i,0);Marshal.FreeHGlobal(buffer);}
            ClosePrinter(handle);
        }
    }
}
