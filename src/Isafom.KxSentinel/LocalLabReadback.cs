using System.IO;
using System.Printing;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Isafom.ConfigExtractor;
using Isafom.KxResearch;

namespace Isafom.KxSentinel;

internal static class LocalLabReadback
{
    internal static int Run()
    {
        Console.InputEncoding = new UTF8Encoding(false, true);
        Console.OutputEncoding = new UTF8Encoding(false);
        try
        {
            char[] chars = new char[1024]; int count = 0; string text;
            try
            {
                for (int c; (c = Console.In.Read()) >= 0 && c != '\n';)
                { if (count == chars.Length) throw new IOException(); chars[count++] = (char)c; }
                text = new string(chars, 0, count);
            }
            finally { Array.Clear(chars); }
            using var json = JsonDocument.Parse(text);
            var fields = json.RootElement.EnumerateObject().ToArray();
            if (fields.Length != 3 || fields.Select(f => f.Name).Distinct().Count() != 3 ||
                fields.Any(f => f.Name is not ("Token" or "UserName" or "AccountId") || f.Value.ValueKind != JsonValueKind.String)) throw new IOException();
            var input = JsonSerializer.Deserialize<WorkerRequest>(text) ?? throw new IOException();
            if (!TrialPolicy.InputValid(input.UserName, input.AccountId) || input.Token.Length != 32 ||
                input.Token.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f'))) throw new IOException();
            string name = TrialPolicy.QueueName(input.Token);
            using var printer = PrinterStore.Open(name);
            PrinterStore.RequireOwned(printer, input.Token);
            var read = Kx85NativeReadback.Read(name, input.UserName, input.AccountId);
            var stores = new List<object>();
            using var codec = new Kx85PrivateCodeCodec(PrinterStore.Component(printer));
            byte[] requested = Encoding.ASCII.GetBytes(input.AccountId);
            foreach (string key in new[] { "JobId", "JobJSId" })
            {
                byte[]? bytes = PrinterStore.Value(printer, key);
                byte[] encoded = codec.Encode(requested, 5);
                try
                {
                    stores.Add(new { Key = key, Present = bytes is not null, Bytes = bytes?.Length,
                        NativeLegacyPrefixMatches = bytes is not null && bytes.Length >= 5 && CryptographicOperations.FixedTimeEquals(bytes.AsSpan(0, 5), encoded),
                        AllBytesAfterLegacyPrefixAreZero = bytes is not null && bytes.Length >= 5 && bytes.AsSpan(5).IndexOfAnyExcept((byte)0) < 0 });
                }
                finally { if (bytes is not null) CryptographicOperations.ZeroMemory(bytes); }
                CryptographicOperations.ZeroMemory(encoded);
            }
            CryptographicOperations.ZeroMemory(requested);
            using var server = new LocalPrintServer(PrintSystemDesiredAccess.EnumerateServer);
            using var queue = new PrintQueue(server, name, PrintSystemDesiredAccess.UsePrinter);
            var modes = new List<PersonalReadback>();
            var accountChecks = new List<RequestedCodeReadback>();
            var colorChecks = new List<object>();
            var colorDiagnostics = new List<object>();
            foreach (var pair in new[] { ("Global", queue.DefaultPrintTicket), ("CurrentUser", queue.UserPrintTicket) })
            {
                using var stream = pair.Item2.GetXmlStream();
                modes.Add(PersonalReadbackReader.Read(stream, pair.Item1));
                stream.Position = 0;
                accountChecks.Add(PersonalReadbackReader.CompareRequestedCode(stream, pair.Item1, input.AccountId));
                colorChecks.Add(new { Scope = pair.Item1, Monochrome = pair.Item2.OutputColor is OutputColor.Monochrome or OutputColor.Grayscale,
                    ColorSymbol = pair.Item2.OutputColor?.ToString() });
                using var dmBuffer = PrinterStore.Get(printer, pair.Item1 == "Global" ? 8u : 9u);
                byte[] dm = dmBuffer.DevMode();
                byte[]? converted = null;
                try
                {
                    using var converter = new System.Printing.Interop.PrintTicketConverter(name, 1);
                    var ticket = converter.ConvertDevModeToPrintTicket(dm);
                    string? originalColor = ticket.OutputColor?.ToString();
                    ticket.OutputColor = OutputColor.Grayscale;
                    converted = converter.ConvertPrintTicketToDevMode(ticket, System.Printing.Interop.BaseDevModeType.PrinterDefault);
                    colorDiagnostics.Add(new { Scope = pair.Item1, DevModeBytes = dm.Length, ConvertedBytes = converted.Length,
                        OriginalColor = originalColor, ConvertedColor = converter.ConvertDevModeToPrintTicket(converted).OutputColor?.ToString(),
                        PublicColor = BitConverter.ToInt16(dm, 92), ConvertedPublicColor = BitConverter.ToInt16(converted, 92) });
                }
                finally { CryptographicOperations.ZeroMemory(dm); if (converted is not null) CryptographicOperations.ZeroMemory(converted); }
            }
            using var capabilities = queue.GetPrintCapabilitiesAsXml();
            var rules = PersonalReadbackReader.ReadRules(capabilities);
            Console.WriteLine(JsonSerializer.Serialize(new { ReadOnly = true, Readback = read, PasswordStoreMetadata = stores, Modes = modes,
                AccountChecks = accountChecks, ColorChecks = colorChecks, ColorDiagnostics = colorDiagnostics, Rules = rules,
                ExpectedValuesOrRawBuffersExported = false, PhysicalPrintPerformed = false }));
            return 0;
        }
        catch { Console.WriteLine("{\"ReadOnly\":true,\"Reason\":\"LocalLabReadbackUnavailable\"}"); return 2; }
    }
}
