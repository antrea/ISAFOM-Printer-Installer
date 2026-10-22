using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Isafom.KxResearch;

// stdin is a private pipe from the original-code emulator, never a file with
// real configuration. Return booleans only; do not echo plaintext/ciphertext.
if (args.Length != 1) return 2;
try
{
    using var codec = new Kx85PrivateCodeCodec(args[0]);
    int passed = 0;
    while (Console.ReadLine() is string line)
    {
        if (line.Length > 8192) return 2;
        using var json = JsonDocument.Parse(line);
        byte[] plain = Encoding.ASCII.GetBytes(json.RootElement.GetProperty("Clear").GetString()!);
        byte[] expected = Convert.FromBase64String(json.RootElement.GetProperty("EncodedBase64").GetString()!);
        byte[]? encoded = null, decoded = null;
        try
        {
            encoded = codec.Encode(plain, expected.Length);
            decoded = codec.Decode(expected);
            bool matches = CryptographicOperations.FixedTimeEquals(encoded, expected) && decoded is not null &&
                           CryptographicOperations.FixedTimeEquals(decoded, plain);
            Console.WriteLine(JsonSerializer.Serialize(new { MatchesOriginalNativeCode = matches }));
            if (!matches) return 1;
            passed++;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain); CryptographicOperations.ZeroMemory(expected);
            if (encoded is not null) CryptographicOperations.ZeroMemory(encoded);
            if (decoded is not null) CryptographicOperations.ZeroMemory(decoded);
        }
    }
    return passed > 0 ? 0 : 2;
}
catch
{
    Console.WriteLine("{\"ProofFailed\":true,\"InputValuesExported\":false}");
    return 1;
}
