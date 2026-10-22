using System.Security.Cryptography;
using System.Text;
using Isafom.ConfigExtractor;
using Isafom.KxResearch;

if(args.Length!=1)return 2;
int passed=0;
void Check(bool value,string name){if(!value)throw new Exception(name);passed++;}
try
{
    using var codec=new Kx85PrivateCodeCodec(args[0]);
    byte[] code=Encoding.ASCII.GetBytes("0"+RandomNumberGenerator.GetInt32(1000).ToString("D3",System.Globalization.CultureInfo.InvariantCulture));
    byte[] stored=codec.Encode(code,33);
    Check(Kx85NativeReadback.CandidateMatches(codec,code,stored),"Modern representation");
    Check(Kx85NativeReadback.CandidateMatches(codec,code,[..stored,0,0]),"Bounded terminating NUL bytes");
    Check(!Kx85NativeReadback.CandidateMatches(codec,code,[..stored,1]),"Nonzero trailing data rejected");
    Check(!Kx85NativeReadback.CandidateMatches(codec,code,[0,0,..stored]),"Arbitrary prefix not removed");
    Check(!Kx85NativeReadback.CandidateMatches(codec,code,[..stored,0,0,0]),"Oversized representation rejected");
    stored[0]^=1;
    Check(!Kx85NativeReadback.CandidateMatches(codec,code,stored),"Wrong encrypted code never matches");
    Check(Kx85NativeReadback.CandidateMatches(codec,code,codec.Encode(code,5)),"Legacy fixed-width representation");
    byte[] legacy=codec.Encode(code,5);
    Check(Kx85NativeReadback.CandidateMatches(codec,code,[..legacy,0,0,0]),"Observed native legacy value with spooler UTF16 termination");
    Check(!Kx85NativeReadback.CandidateMatches(codec,code,[..legacy,0,0,0,0]),"Excess legacy padding refused");
    Check(!Kx85NativeReadback.CandidateMatches(codec,code,[..legacy,0,0,1]),"Nonzero extra legacy data refused");
    byte[] field=new byte[12];code.CopyTo(field,2);
    Check(Kx85NativeReadback.Literal(field,2,10,code)==true,"Exact string preserves leading zero");
    Check(Kx85NativeReadback.Literal(field,2,10,code[1..])==false,"Leading zero never discarded");
    Check(Kx85NativeReadback.Literal(field,3,10,code) is null,"Truncated field is unknown");
    Array.Fill(field,(byte)1);
    Check(Kx85NativeReadback.Literal(field,2,10,code) is null,"Missing terminator is unknown");
    bool rejected=false;try{using var wrong=new Kx85PrivateCodeCodec(typeof(object).Assembly.Location);}catch(System.IO.InvalidDataException){rejected=true;}
    Check(rejected,"Unrecognised driver component refused");
    Console.WriteLine($"PASS {passed} offline reversed-readback checks; no printer API called; no values exported.");
    return 0;
}
catch{Console.WriteLine("FAIL reversed readback; input and raw error omitted.");return 1;}
