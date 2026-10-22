namespace Isafom.Core;

// Only these two exact, analysed x64 payloads can be selected by the GUI/helper.
public static class DriverProfiles
{
    public const string DefaultVersion = "8.5.2405.0";
    public const string OptionalVersion = "8.7.917.0";
    public static PrinterSettings Select(PrinterSettings settings, string version) => version switch
    {
        DefaultVersion => settings with { DriverVersion=DefaultVersion, DriverUri="",
            DriverArchiveSha256="7100E66596C06252621B604D6CF19EA30527949D56319D83873909A66FC3092E",
            InfRelativePath="Kx85_UPD_8.5.2405_en_RC2_WHQL/64bit/OEMSETUP.INF" },
        OptionalVersion => settings with { DriverVersion=OptionalVersion, DriverUri="",
            DriverArchiveSha256="C1ABC58594979E2B38B8344B8AB7DF3DBDFC987DFFDF3FF02BB5F6FC289DCD8F",
            InfRelativePath="Kx87_UPD_8.7.0917_en_RC3_signed/64bit/OEMSETUP.INF" },
        _ => throw new InstallerException(ErrorCode.Configuration)
    };
    public static bool Supported(string version) => version is DefaultVersion or OptionalVersion;
    public static string Component(string version) => version switch
    {
        DefaultVersion=>"KMFS85O5.DLL", OptionalVersion=>"KMFS879H.DLL",
        _=>throw new InstallerException(ErrorCode.ProviderUnavailable)
    };
    public static string InfSha256(string version) => version switch
    {
        DefaultVersion=>"B711AAE2DFFD78BFCE3D666288F9852D07EA9C94BF8E83DF7974BA2A1DD6F6B7",
        OptionalVersion=>"6B65DAC2A2217707B0B7A6CE69438E1203C3BD49E82C6ECDDCBE1E984427D139",
        _=>throw new InstallerException(ErrorCode.ProviderUnavailable)
    };
}
