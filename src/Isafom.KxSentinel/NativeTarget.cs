namespace Isafom.KxSentinel;

// Constructed only by the caller's fixed queue policy, never from a serialized
// callback. Production and laboratory authorization remain separate.
internal sealed record NativeTarget(string Name, Action<PrinterStore.SafePrinter> Require);
