using System.IO;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;

namespace Isafom.KxResearch;

// Version-bound research implementation, compared with original x64 code in
// Unicorn. It only transforms caller-supplied buffers. No printer/registry API
// and no driver DLL loading. Not an enabled production preference adapter.
internal sealed class Kx85PrivateCodeCodec : IDisposable
{
    private const string ExpectedSha256 = "9FCC2D8DB6851FA59B0D8D5794F1980627B122ABBF154159793F8F46FAFA1CD1";
    private const string Kx87Sha256 = "6805C42C9B92F52244909B0700727D239DA81C30A1A4A1638FA2B46E3AA02358";
    internal int SetupBytes { get; }
    private readonly byte[] key;
    private readonly byte[] padding;

    public Kx85PrivateCodeCodec(string originalDriverFile)
    {
        using var file = File.OpenRead(originalDriverFile);
        string hash = Convert.ToHexString(SHA256.HashData(file));
        if (hash != ExpectedSha256 && hash != Kx87Sha256)
            throw new InvalidDataException("ExactOriginalKxFileRequired");
        bool kx87 = hash == Kx87Sha256;
        SetupBytes = kx87 ? 0x1f24 : 0x1f20;
        file.Position = 0;
        using var pe = new PEReader(file);
        var header = pe.PEHeaders.PEHeader ?? throw new InvalidDataException();
        byte[] pointer = Read(kx87 ? 0x63dec8 : 0x5e9e78, 8);
        ulong keyAddress = BitConverter.ToUInt64(pointer);
        if (keyAddress < header.ImageBase || keyAddress - header.ImageBase > int.MaxValue)
            throw new InvalidDataException();
        byte[] candidate = Read((int)(keyAddress - header.ImageBase), 256);
        try
        {
            int length = Array.IndexOf(candidate, (byte)0);
            if (length is < 1 or > 255) throw new InvalidDataException();
            key = candidate.AsSpan(0, length).ToArray();
        }
        finally { CryptographicOperations.ZeroMemory(candidate); }
        padding = Read(kx87 ? 0x5e7a60 : 0x5967d0, 32);

        byte[] Read(int rva, int count)
        {
            var block = pe.GetSectionData(rva);
            if (block.Length < count) throw new InvalidDataException();
            return block.GetContent(0, count).ToArray();
        }
    }

    public byte[] Encode(ReadOnlySpan<byte> input, int encodedLength)
    {
        if (input.Length > 32 || encodedLength <= input.Length || encodedLength > 67 || input.Contains((byte)0))
            throw new ArgumentException("UnsupportedResearchBuffer");
        byte[] output = new byte[encodedLength];
        for (int i = 0; i < output.Length; i++)
            output[i] = i < input.Length ? (byte)(input[i] ^ padding[i % 32]) : padding[i % 32];
        Transform(output);
        return output;
    }

    public byte[]? Decode(ReadOnlySpan<byte> encoded)
    {
        if (encoded.Length is < 1 or > 67) return null;
        byte[] result = encoded.ToArray();
        Transform(result);
        bool marker = false;
        for (int i = 0; i < result.Length; i++)
        {
            if (result[i] == padding[i % 32]) { result[i] = 0; marker = true; }
            else result[i] ^= padding[i % 32];
        }
        try
        {
            if (!marker) return null;
            int length = Array.IndexOf(result, (byte)0);
            return result.AsSpan(0, length).ToArray();
        }
        finally { CryptographicOperations.ZeroMemory(result); }
    }

    private void Transform(Span<byte> buffer)
    {
        Span<byte> state = stackalloc byte[256];
        for (int i = 0; i < state.Length; i++) state[i] = (byte)i;
        int j = 0;
        for (int i = 0; i < state.Length; i++)
        {
            j = (j + state[i] + key[i % key.Length]) & 255;
            (state[i], state[j]) = (state[j], state[i]);
        }
        int x = 0; j = 0;
        for (int i = 0; i < buffer.Length; i++)
        {
            x = (x + 1) & 255;
            j = (j + state[x]) & 255;
            (state[x], state[j]) = (state[j], state[x]);
            buffer[i] ^= state[(state[x] + state[j]) & 255];
        }
        CryptographicOperations.ZeroMemory(state);
    }

    public void Dispose()
    {
        CryptographicOperations.ZeroMemory(key);
        CryptographicOperations.ZeroMemory(padding);
    }
}
