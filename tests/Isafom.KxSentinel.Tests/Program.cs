using System.Runtime.InteropServices;
using System.Text.Json;
using Isafom.ConfigExtractor;
using Isafom.KxSentinel;

int passed = 0;
void Check(bool value, string label) { if (!value) throw new Exception(label); passed++; }
try
{
    string token = Guid.NewGuid().ToString("N");
    var owned = new PrinterInfo(TrialPolicy.QueueName(token), "Kyocera CS 3554ci KX", "FILE:", TrialPolicy.MarkerPrefix + token, 0x40, 1, 0);
    Check(TrialPolicy.Owned(owned, token), "Fresh marked paused FILE queue allowed");
    foreach (var invalid in new[] {
        owned with { Name = "Stampante ISAFOM" }, owned with { Name = owned.Name + "x" },
        owned with { Port = "IP_140.164.75.11" }, owned with { Port = "FILE:,IP_140.164.75.11" },
        owned with { Comment = "" }, owned with { Comment = TrialPolicy.MarkerPrefix + Guid.NewGuid().ToString("N") },
        owned with { Driver = "Generic / Text Only" }, owned with { Jobs = 1 }, owned with { Status = 0 },
        owned with { Attributes = 0x40 | 0x10 }, owned with { Attributes = 0x40 | 0x1000 } })
        Check(!TrialPolicy.Owned(invalid, token), "Foreign/shared/unpaused/busy target rejected");
    Check(!TrialPolicy.Owned(owned, ""), "Malformed owner rejected");
    Check(!TrialPolicy.Owned(owned, new string('z', 32)), "Nonhex owner rejected");
    Check(TrialPolicy.InputValid("ISAFOM_SENTINELLA_A", "0001"), "Sentinel preserves leading zeros");
    Check(!TrialPolicy.InputValid("Mario Rossi", "0001"), "Personal name forbidden in native worker");
    Check(!TrialPolicy.InputValid("ISAFOM_SENTINELLA_A", "12345"), "Unsupported PIN length rejected");
    Check(!TrialPolicy.InputValid("ISAFOM_SENTINELLA_A", "١٢٣٤"), "NonASCII digits rejected");
    string code = TrialPolicy.NewCode(null);
    string next = TrialPolicy.NewCode(code);
    Check(code.Length == 4 && code[0] == '0' && next != code && next[0] == '0', "Two distinct synthetic four-digit codes");
    var valid = new Kx85ReversedReadback("0.1.3-research", true, true, true, true, true, true, true, true, true, true, true, false, false, "", "");
    Check(TrialPolicy.Matches(valid, true, true), "All independent fields plus mode and native status required");
    Check(!TrialPolicy.Matches(valid, true, false), "False native status cannot certify success");
    Check(!TrialPolicy.Matches(valid, false, true), "Stored PIN alone cannot certify private mode");
    Check(!new WorkerResult(true, true, true, true, true, "NativeCallsUnavailable", "CloseContext").Accepted,
        "A close error cannot be hidden by successful setter booleans");
    Check(!new WorkerResult(true, true, true, true, true, "NativeCallsCompleted", "Completed",
        DevModeValidated:true,GlobalDefaultsCommitted:false,UserDefaultsCommitted:true).Accepted,
        "User defaults alone cannot certify global defaults");
    Check(new WorkerResult(true, true, true, true, true, "NativeCallsCompleted", "Completed",
        DevModeValidated:true,GlobalDefaultsCommitted:true,UserDefaultsCommitted:true,ColorAccepted:true).Accepted,
        "Explicit validated global and current-user defaults required");
    foreach (var missing in new[] { valid with { NameMatchesUtf8 = false }, valid with { AccountMatches = false },
        valid with { PrivateCodeCandidateMatches = null }, valid with { AccountingEnabled = false },
        valid with { SpecificAccountSelected = false }, valid with { SetupLayoutRecognized = false },
        valid with { ExactDriverComponentVerified = false } })
        Check(!TrialPolicy.Matches(missing, true, true), "Accepted save with old/missing readback rejected");
    var report = new TrialReport(1, TrialPolicy.Version, "TwoRoundsCompleted", [], true, true, true, true);
    string json = JsonSerializer.Serialize(report);
    Check(!json.Contains(code) && !json.Contains("ISAFOM_SENTINELLA_A") && !report.InstallerReady && !report.PhysicalPrintPerformed,
        "Report never contains expected values or certifies installation");
    using var buffer = new PrinterStore.Buffer(80);
    Marshal.WriteIntPtr(buffer.Pointer, IntPtr.Zero);
    Check(buffer.DevMode().Length == 0, "Missing user DEVMODE is not an invented blob");
    bool bad = false; try { buffer.Offset(buffer.Pointer + 79, 2); } catch { bad = true; }
    Check(bad, "Out-of-bounds spooler pointer refused");
    Marshal.WriteIntPtr(buffer.Pointer, buffer.Pointer + 16);
    bad = false; try { buffer.DevMode(); } catch { bad = true; }
    Check(bad, "Truncated DEVMODE refused");
    var local = new MonitorDescriptor("Local Port", "localspl.dll");
    var file = new PortDescriptor("FILE:", "Local Port", 1);
    Check(FilePortPolicy.Evaluate([file], [local]).SafeFilePortAvailable, "Registered local FILE port accepted");
    var alias = FilePortPolicy.Evaluate([file with { Monitor = "Local Monitor" }], [local]);
    Check(alias.SafeFilePortAvailable && alias.BuiltinMonitorAliasUsed == true, "Real Windows Local Monitor alias resolved to localspl");
    Check(FilePortPolicy.Evaluate([file with { Monitor = "Porta locale" }], [local with { Name = "Porta locale" }]).SafeFilePortAvailable,
        "Localized matching monitor bound to its component");
    Check(!FilePortPolicy.Evaluate([file], [local with { Dll = "thirdparty.dll" }]).SafeFilePortAvailable, "Third-party FILE monitor rejected");
    Check(!FilePortPolicy.Evaluate([file with { Monitor = "Local Monitor" }], [local, new("Local Monitor", "thirdparty.dll")]).SafeFilePortAvailable,
        "Alias cannot hide a conflicting registered monitor");
    Check(!FilePortPolicy.Evaluate([file, file], [local]).SafeFilePortAvailable, "Duplicate FILE port ambiguous");
    Check(!FilePortPolicy.Evaluate([file with { Name = "FILE:,IP_140.164.75.11" }], [local]).SafeFilePortAvailable, "Port list never accepted as FILE");
    Check(!FilePortPolicy.Evaluate([file with { Name = "LPT1:" }], [local]).SafeFilePortAvailable, "Physical local port never fallback");
    Check(!FilePortPolicy.Evaluate([file with { Type = 1 | 8 }], [local]).SafeFilePortAvailable, "Network flag rejected");
    Check(!FilePortPolicy.Evaluate([file with { Type = 1 | 4 }], [local]).SafeFilePortAvailable, "Redirected flag rejected");
    Check(!FilePortPolicy.Evaluate([file with { Type = 0 }], [local]).SafeFilePortAvailable, "Unknown port type rejected");
    Check(!FilePortPolicy.Evaluate([file], [local, local]).SafeFilePortAvailable, "Ambiguous monitor rejected");
    Check(!FilePortPolicy.Evaluate([file], [local with { Dll = @"elsewhere\localspl.dll" }]).SafeFilePortAvailable, "Relative alternate localspl path rejected");
    Check(FilePortPolicy.Evaluate([file], [local with { Dll = System.IO.Path.Combine(Environment.SystemDirectory, "localspl.dll") }]).SafeFilePortAvailable,
        "Exact system path for local monitor accepted");
    var notStarted = new TrialReport(2, TrialPolicy.Version, "FilePortNotPresent", [], null, null, null, null,
        ReadOnly: true, QueueCreationAttempted: false, TestQueueCreated: false, NativeWriteAttempted: false);
    Check(notStarted.TestQueueRemoved is null && notStarted.ReadOnly && !notStarted.TestQueueCreated && !notStarted.NativeWriteAttempted,
        "Not-created queue is distinct from failed removal");
    Console.WriteLine($"PASS {passed} sentinel guard/readback checks; no Windows printer mutation or vendor DLL load.");
    return 0;
}
catch { Console.WriteLine("FAIL sentinel tests; raw values and errors omitted."); return 1; }
