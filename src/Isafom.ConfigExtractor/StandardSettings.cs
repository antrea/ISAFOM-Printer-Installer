using System.Collections;
using System.IO;
using System.Printing;
using System.Reflection;
using System.Text.Json;

namespace Isafom.ConfigExtractor;

public sealed record Setting(string Scope, string Source, string Field, string State, JsonElement? Value, string Unit);
public sealed record SettingsSnapshot(Setting[] Settings, PersonalReadback[] Personal, ParameterRule[] ParameterRules);

public static class StandardSettings
{
    // Property names/types come from the trusted framework classes, never from
    // arbitrary XML symbols. Strings, paths, raw XML and driver objects cannot
    // pass this serializer. New framework properties have explicit coverage.
    public static Setting[] ReadProperties(object instance, Type contract, string scope)
    {
        if (instance.GetType() != contract) throw new ArgumentException("UnexpectedContract");
        return contract.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.GetIndexParameters().Length == 0)
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .Select(p => ReadProperty(instance, p, scope, contract.FullName!)).ToArray();
    }
    private static Setting ReadProperty(object instance, PropertyInfo property, string scope, string source)
    {
        if (!Supported(property.PropertyType)) return new(scope, source, property.Name, "Tipo non esportabile", null, "");
        try
        {
            object? value = property.GetValue(instance);
            if (value is null) return new(scope, source, property.Name, "Non esposto", null, Unit(property.Name));
            var (safe, complete) = Normalize(value);
            return new(scope, source, property.Name, complete ? "Letto" : "Parziale o non decodificato",
                safe is null ? null : JsonSerializer.SerializeToElement(safe), Unit(property.Name));
        }
        catch { return new(scope, source, property.Name, "Lettura non disponibile", null, Unit(property.Name)); }
    }
    private static bool Supported(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        return type.IsEnum || type == typeof(int) || type == typeof(double) || type == typeof(bool) ||
            type == typeof(PageMediaSize) || type == typeof(PageResolution) || type == typeof(PageImageableArea) ||
            type == typeof(PageScalingFactorRange) ||
            (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(System.Collections.ObjectModel.ReadOnlyCollection<>) && Supported(type.GetGenericArguments()[0]));
    }
    internal static (object? Safe, bool Complete) Normalize(object value)
    {
        if (value is Enum enumeration)
            return Enum.IsDefined(enumeration.GetType(), enumeration) && enumeration.ToString() != "Unknown"
                ? (enumeration.ToString(), true) : (null, false);
        if (value is int integer) return (integer, true);
        if (value is bool boolean) return (boolean, true);
        if (value is double number) return double.IsFinite(number) ? (number, true) : (null, false);
        if (value is PageMediaSize media)
        {
            string? name = media.PageMediaSizeName is { } n && Enum.IsDefined(n) && n != PageMediaSizeName.Unknown ? n.ToString() : null;
            return (new { Name = name, Width = Finite(media.Width), Height = Finite(media.Height) }, true);
        }
        if (value is PageResolution resolution)
        {
            string? quality = resolution.QualitativeResolution is { } q && Enum.IsDefined(q) && q != PageQualitativeResolution.Unknown ? q.ToString() : null;
            return (new { resolution.X, resolution.Y, Qualitative = quality }, true);
        }
        if (value is PageImageableArea area)
            return (new { OriginWidth = Finite(area.OriginWidth), OriginHeight = Finite(area.OriginHeight), ExtentWidth = Finite(area.ExtentWidth), ExtentHeight = Finite(area.ExtentHeight) }, true);
        if (value is PageScalingFactorRange range) return (new { range.MinimumScale, range.MaximumScale }, true);
        if (value is IEnumerable sequence && value is not string)
        {
            var items = new List<object?>(); bool complete = true;
            foreach (object? item in sequence)
            {
                if (items.Count == 512) { complete = false; break; }
                if (item is null) { items.Add(null); continue; }
                var parsed = Normalize(item); items.Add(parsed.Safe); complete &= parsed.Complete;
            }
            return (items.ToArray(), complete);
        }
        return (null, false);
    }
    private static double? Finite(double? value) => value is { } d && double.IsFinite(d) ? d : null;
    private static string Unit(string property) => property.Contains("Resolution", StringComparison.Ordinal) ? "dpi / qualità dichiarata" :
        property.Contains("MediaSize", StringComparison.Ordinal) || property.Contains("ImageableArea", StringComparison.Ordinal) ||
        property.StartsWith("OrientedPageMedia", StringComparison.Ordinal) ? "dimensioni in DIP (1/96 pollice)" :
        property.Contains("Scaling", StringComparison.Ordinal) ? "percentuale" : "";

    public static SettingsSnapshot Read(string queueName)
    {
        var settings = new List<Setting>(); var personal = new List<PersonalReadback>(); var rules = new List<ParameterRule>();
        using var server = new LocalPrintServer(PrintSystemDesiredAccess.EnumerateServer);
        using var queue = new PrintQueue(server, queueName, PrintSystemDesiredAccess.UsePrinter);
        PrintTicket? user = null;
        Ticket("Globale", () => queue.DefaultPrintTicket);
        Ticket("Utente corrente", () => user = queue.UserPrintTicket);
        try
        {
            var capabilities = user is null ? queue.GetPrintCapabilities() : queue.GetPrintCapabilities(user);
            settings.AddRange(ReadProperties(capabilities, typeof(PrintCapabilities), user is null ? "Capacità senza ticket utente" : "Capacità per ticket utente"));
        }
        catch { settings.Add(new("Capacità", "System.Printing", "Disponibilità", "Lettura non disponibile", null, "")); }
        try
        {
            using var stream = queue.GetPrintCapabilitiesAsXml();
            try { rules.AddRange(PersonalReadbackReader.ReadRules(stream)); }
            finally { Clear(stream); }
        }
        catch { settings.Add(new("Capacità", "PrintCapabilities XML", "Vincoli KX", "Lettura non disponibile", null, "")); }
        return new(settings.ToArray(), personal.ToArray(), rules.ToArray());

        void Ticket(string scope, Func<PrintTicket?> read)
        {
            try
            {
                var ticket = read();
                if (ticket is null) { settings.Add(new(scope, "PrintTicket", "Disponibilità", "Non esposto", null, "")); return; }
                settings.AddRange(ReadProperties(ticket, typeof(PrintTicket), scope));
                using var stream = ticket.GetXmlStream();
                try { personal.Add(PersonalReadbackReader.Read(stream, scope)); }
                finally { Clear(stream); }
            }
            catch { settings.Add(new(scope, "PrintTicket", "Disponibilità", "Lettura non disponibile", null, "")); }
        }
    }
    private static void Clear(MemoryStream stream)
    {
        if (stream.TryGetBuffer(out var buffer)) System.Security.Cryptography.CryptographicOperations.ZeroMemory(buffer.AsSpan());
    }
}
