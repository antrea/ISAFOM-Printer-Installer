using System.Security.Cryptography;
using Isafom.ConfigExtractor;

namespace Isafom.KxSentinel;

internal static class TrialPolicy
{
    internal const string Version = "0.1.6-research";
    internal const string Prefix = "ISAFOM_TEST_KX_";
    internal const string MarkerPrefix = "ISAFOM KX SENTINEL 0.1.5 ";
    internal static bool Model(string driver) => driver is "Kyocera CS 3554ci KX" or "Kyocera TASKalfa 3554ci KX";
    internal static string QueueName(string token) => Prefix + token[..8];
    internal static bool Owned(PrinterInfo info, string token) =>
        token.Length == 32 && token.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f') &&
        info.Name == QueueName(token) && info.Comment == MarkerPrefix + token &&
        Model(info.Driver) && info.Port == "FILE:" && (info.Attributes & (0x10u | 0x1000u)) == 0 &&
        info.Jobs == 0 && (info.Status & 1) != 0;

    internal static bool InputValid(string name, string code) =>
        name is "ISAFOM_SENTINELLA_A" or "ISAFOM_SENTINELLA_B" &&
        code.Length == 4 && code.All(c => c is >= '0' and <= '9');

    internal static string NewCode(string? previous)
    {
        string code;
        do { code = "0" + RandomNumberGenerator.GetInt32(1000).ToString("D3", System.Globalization.CultureInfo.InvariantCulture); }
        while (code == previous);
        return code;
    }

    // Saving returning true is deliberately insufficient. Native readback and
    // PrintTicket mode must be reacquired after the child has closed its context.
    internal static bool Matches(Kx85ReversedReadback read, bool privateMode, bool nativeAccepted) =>
        nativeAccepted && read.ExactDriverComponentVerified && read.SetupLayoutRecognized &&
        read.NameMatchesUtf8 == true && read.AccountMatches == true &&
        read.AccountingEnabled == true && read.SpecificAccountSelected == true &&
        read.PrivateCodeCandidateMatches == true && privateMode;
}
