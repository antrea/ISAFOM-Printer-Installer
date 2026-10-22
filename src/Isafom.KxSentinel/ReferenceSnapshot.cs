using System.Security.Cryptography;

namespace Isafom.KxSentinel;

internal sealed class ReferenceSnapshot : IDisposable
{
    private readonly List<byte[]?> values = [];
    private readonly string queue;
    private readonly PrinterInfo metadata;
    private readonly string defaultPrinter;
    private readonly PrinterInfo[] inventory;
    private ReferenceSnapshot(string queue, PrinterInfo metadata, string defaultPrinter, PrinterInfo[] inventory)
    { this.queue = queue; this.metadata = metadata; this.defaultPrinter = defaultPrinter; this.inventory = inventory; }

    internal static ReferenceSnapshot Capture(PrinterInfo reference)
    {
        using var printer = PrinterStore.Open(reference.Name);
        var snapshot = new ReferenceSnapshot(reference.Name, PrinterStore.Info(printer), PrinterStore.DefaultName(), PrinterStore.Inventory());
        try
        {
            foreach (string key in new[] { "Setup", "JobId", "JobJSId", "KXP_FLAG" }) snapshot.values.Add(PrinterStore.Value(printer, key));
            foreach (uint level in new uint[] { 8, 9 })
            {
                using var buffer = PrinterStore.Get(printer, level);
                snapshot.values.Add(buffer.DevMode());
            }
            return snapshot;
        }
        catch { snapshot.Dispose(); throw; }
    }

    internal (bool Configuration, bool OtherQueues, bool DefaultPrinter) Compare(PrinterInfo reference, string ownQueue)
    {
        using var other = Capture(reference);
        bool sameValues = queue == other.queue && values.Count == other.values.Count &&
            values.Zip(other.values).All(pair => pair.First is null && pair.Second is null ||
                pair.First is not null && pair.Second is not null && CryptographicOperations.FixedTimeEquals(pair.First, pair.Second));
        // Status/job counts may change independently while colleagues print.
        static object Stable(PrinterInfo info) => new { info.Name, info.Driver, info.Port, info.Comment, info.Attributes };
        bool sameMetadata = Equals(Stable(metadata), Stable(other.metadata));
        bool sameInventory = inventory.Where(p => p.Name != ownQueue).OrderBy(p => p.Name, StringComparer.Ordinal).Select(Stable)
            .SequenceEqual(other.inventory.Where(p => p.Name != ownQueue).OrderBy(p => p.Name, StringComparer.Ordinal).Select(Stable));
        return (sameValues && sameMetadata, sameInventory, defaultPrinter == other.defaultPrinter);
    }
    public void Dispose()
    {
        foreach (byte[]? value in values) if (value is not null) CryptographicOperations.ZeroMemory(value);
        values.Clear();
    }
}
