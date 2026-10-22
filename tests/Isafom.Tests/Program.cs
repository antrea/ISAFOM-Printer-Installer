using System.Text.Json;
using Isafom.Core;

var tests = new List<(string Name, Func<Task> Run)>();
void Test(string name, Action action) => tests.Add((name, () => { action(); return Task.CompletedTask; }));
void AsyncTest(string name, Func<Task> action) => tests.Add((name, action));
static void Assert(bool condition) { if (!condition) throw new Exception("Assertion failed"); }
static void Fails(ErrorCode code, Action action)
{
    try { action(); } catch (InstallerException e) when (e.Code == code) { return; }
    throw new Exception("Expected controlled error: " + code);
}
static async Task FailsAsync(ErrorCode code, Func<Task> action)
{
    try { await action(); } catch (InstallerException e) when (e.Code == code) { return; }
    throw new Exception("Expected controlled error: " + code);
}
var s = PrinterSettings.Default();
var endpoint = new Endpoint(s.Host, false);
var port = new PortState(s.PortName, s.Host, 1, 9100, true, s.SnmpCommunity, 1);
var driver = new DriverState(s.DriverName, s.DriverVersion);
var queue = new QueueState(s.PrinterName, s.DriverName, s.PortName, false, 0, true);
var empty = new SystemState(true, true, true, [], [], []);
var configured = empty with { Drivers = [driver], Ports = [port], Queues = [queue] };

Test("Configuration defaults validated", s.Validate);
Test("Configuration JSON roundtrip", () => Assert(PrinterSettings.Parse(JsonSerializer.Serialize(s)) == s));
Test("Duplicate JSON rejected", () => Fails(ErrorCode.Configuration, () => PrinterSettings.Parse("{\"Host\":\"one\",\"Host\":\"two\"}")));
Test("Unknown JSON rejected", () => Fails(ErrorCode.Configuration, () => PrinterSettings.Parse(JsonSerializer.Serialize(s).Replace("{", "{\"AccountID\":\"not accepted\","))));
Test("HTTP package rejected", () => Fails(ErrorCode.Configuration, () => (s with { DriverUri = "http://intranet.isafom.cnr.it/driver.zip" }).Validate()));
Test("Embedded 8.5 x64 package is the default", () => Assert(s.DriverUri == "" && s.DriverVersion == DriverProfiles.DefaultVersion && s.InfRelativePath.Contains("/64bit/")));
Test("8.7 is an explicit optional profile preserving the endpoint",()=>{var other=DriverProfiles.Select(s,DriverProfiles.OptionalVersion);other.Validate();Assert(other.Host==s.Host&&other.DriverVersion==DriverProfiles.OptionalVersion&&other.DriverArchiveSha256!=s.DriverArchiveSha256);});
Test("Unknown optional driver profile refused",()=>Fails(ErrorCode.Configuration,()=>DriverProfiles.Select(s,"9.0.0.0")));
Test("Third party package rejected", () => Fails(ErrorCode.Configuration, () => (s with { DriverUri = "https://example.com/driver.zip" }).Validate()));
Test("URL credentials rejected", () => Fails(ErrorCode.Configuration, () => (s with { DriverUri = "https://user:pass@intranet.isafom.cnr.it/driver.zip" }).Validate()));
Test("Bad hash rejected", () => Fails(ErrorCode.Configuration, () => (s with { DriverArchiveSha256 = "" }).Validate()));
Test("Null configuration value rejected", () => Fails(ErrorCode.Configuration, () => PrinterSettings.Parse(JsonSerializer.Serialize(s).Replace("\"Host\":\"stampante.isafom.cnr.it\"", "\"Host\":null"))));
Test("Path traversal rejected", () => Fails(ErrorCode.Configuration, () => (s with { InfRelativePath = "../64bit/OEMSETUP.INF" }).Validate()));
Test("Alternative protocol rejected", () => Fails(ErrorCode.Configuration, () => (s with { PortNumber = 515 }).Validate()));
Test("Accent and apostrophe preserved, external spaces trimmed", () => Assert(InputRules.Name("  Nicolò D'Àngelo  ") == "Nicolò D'Àngelo"));
Test("Empty name rejected", () => Fails(ErrorCode.InvalidName, () => InputRules.Name("  ")));
Test("Control characters rejected", () => Fails(ErrorCode.InvalidName, () => InputRules.Name("Mario\rRossi")));
Test("Model name limit enforced without truncation", () => { Assert(InputRules.Name(new string('À',31)).Length == 31); Fails(ErrorCode.InvalidName, () => InputRules.Name(new string('À',32))); });
foreach (var value in new[] { "0", "00123456", "12345678", "7" })
    Test("Numeric ID accepted, length " + value.Length, () => { InputRules.Account(value); Assert(value == string.Concat(value)); });
foreach (var value in new[] { "", "123456789", " 123", "123 ", "１２３", "١٢٣", "12a3", "12\n3" })
    Test("Malformed ID rejected, case " + tests.Count, () => Fails(ErrorCode.InvalidAccount, () => InputRules.Account(value)));

Test("New installation plan", () => { var p = Planner.Create(s, empty, endpoint); Assert(p.AddDriver && p.AddPort && p.AddQueue && !p.RepairQueue); });
Test("Second execution creates no duplicates", () => { var p = Planner.Create(s, configured, endpoint); Assert(!p.AddDriver && !p.AddPort && !p.AddQueue && !p.RepairQueue); });
Test("Windows queue and port names are case insensitive", () => { var p = Planner.Create(s, configured with { Queues = [queue with { Name = queue.Name.ToUpperInvariant(), PortName = queue.PortName.ToUpperInvariant() }] }, endpoint); Assert(!p.AddQueue && !p.RepairQueue); });
Test("Existing exact driver reused", () => Assert(!Planner.Create(s, empty with { Drivers = [driver] }, endpoint).AddDriver));
Test("Different driver version blocked", () => Fails(ErrorCode.DriverConflict, () => Planner.Create(s, configured with { Drivers = [driver with { Version = "8.7.1.0" }] }, endpoint)));
Test("Correct existing port reused", () => Assert(!Planner.Create(s, empty with { Ports = [port] }, endpoint).AddPort));
Test("Legacy fallback IP port reused only when probed as fallback", () => Assert(!Planner.Create(s, configured with { Ports = [port with { Host = s.FallbackIPv4 }] }, new Endpoint(s.FallbackIPv4, true)).AddPort));
Test("DNS success cannot certify an unprobed fallback port", () => Fails(ErrorCode.PortConflict, () => Planner.Create(s, configured with { Ports = [port with { Host = s.FallbackIPv4 }] }, endpoint)));
Test("DNS-failure fallback cannot reuse an unresolved DNS port", () => Fails(ErrorCode.PortConflict, () => Planner.Create(s, configured, new Endpoint(s.FallbackIPv4, true))));
foreach (var bad in new[] { port with { Protocol = 2 }, port with { PortNumber = 515 }, port with { SnmpEnabled = false }, port with { Community = "public" }, port with { SnmpIndex = 2 }, port with { Host = "other.example" } })
    Test("Conflicting port never modified " + tests.Count, () => Fails(ErrorCode.PortConflict, () => Planner.Create(s, empty with { Ports = [bad] }, endpoint)));
Test("Foreign queue with same name blocked", () => Fails(ErrorCode.QueueConflict, () => Planner.Create(s, configured with { Queues = [queue with { DriverName = "Other driver" }] }, endpoint)));
Test("Foreign endpoint blocked", () => Fails(ErrorCode.QueueConflict, () => Planner.Create(s, configured with { Queues = [queue with { Managed = false }], Ports = [port with { Host = "other.example" }] }, endpoint)));
Test("Shared queue blocked", () => Fails(ErrorCode.QueueConflict, () => Planner.Create(s, configured with { Queues = [queue with { Shared = true }] }, endpoint)));
Test("Existing jobs not disrupted", () => Fails(ErrorCode.ExistingJobs, () => Planner.Create(s, configured with { Queues = [queue with { Jobs = 1 }] }, endpoint)));
Test("Unknown job count blocks mutation", () => Fails(ErrorCode.ExistingJobs, () => Planner.Create(s, configured with { Queues = [queue with { Jobs = -1 }] }, endpoint)));
Test("Ambiguous queues blocked", () => Fails(ErrorCode.AmbiguousQueue, () => Planner.Create(s, configured with { Queues = [queue, queue with { Name = "Another queue" }] }, endpoint)));
Test("Existing renamed queue reused", () => Assert(Planner.Create(s, configured with { Queues = [queue with { Name = "Existing renamed queue", Managed = false }] }, endpoint).QueueName == "Existing renamed queue"));
Test("Managed partial installation repaired", () => Assert(Planner.Create(s, configured with { Queues = [queue with { PortName = "broken" }] }, endpoint).RepairQueue));
Test("Unmanaged wrong port not repaired", () => Fails(ErrorCode.QueueConflict, () => Planner.Create(s, configured with { Queues = [queue with { PortName = "broken", Managed = false }] }, endpoint)));
Test("Other printers preserved by plan", () => { var p = Planner.Create(s, configured with { Queues = [queue, new("Unrelated", "Unrelated", "Any", true, 99, false)] }, endpoint); Assert(!p.AddQueue && !p.RepairQueue); });
Test("Verification preflight accepts the configured dedicated queue",()=>VerificationPreflight.Validate(s,configured));
Test("Verification after failed installation reports missing printer",()=>Fails(ErrorCode.PrinterMissing,()=>VerificationPreflight.Validate(s,empty)));
Test("Verification missing queue is explicit even with installed driver",()=>Fails(ErrorCode.PrinterMissing,()=>VerificationPreflight.Validate(s,empty with {Drivers=[driver]})));
Test("Verification conflict reports driver error",()=>Fails(ErrorCode.DriverConflict,()=>VerificationPreflight.Validate(s,configured with {Drivers=[driver with {Version="9.0.0.0"}]})));
Test("Verification foreign queue is refused explicitly",()=>Fails(ErrorCode.QueueConflict,()=>VerificationPreflight.Validate(s,configured with {Queues=[queue with {Managed=false}]})));
Test("Verification bad port is explicit",()=>Fails(ErrorCode.PortConflict,()=>VerificationPreflight.Validate(s,configured with {Ports=[port with {SnmpEnabled=false}]})));
Test("Verification pending jobs is explicit",()=>Fails(ErrorCode.ExistingJobs,()=>VerificationPreflight.Validate(s,configured with {Queues=[queue with {Jobs=1}]})));
Test("Verification ambiguity is explicit",()=>Fails(ErrorCode.AmbiguousQueue,()=>VerificationPreflight.Validate(s,configured with {Queues=[queue,queue with {Name="Duplicate"}]})));
foreach (var scenario in new[] { (empty with { SupportedWindows = false }, ErrorCode.UnsupportedWindows), (empty with { X64 = false }, ErrorCode.UnsupportedArchitecture), (empty with { SpoolerRunning = false }, ErrorCode.SpoolerUnavailable) })
    Test("Platform gate " + scenario.Item2, () => Fails(scenario.Item2, () => Planner.Create(s, scenario.Item1, endpoint)));

AsyncTest("Production adapter blocks BEFORE network and UAC", async () =>
{
    var machine = new FakeMachine(configured, configured);
    var sink = new Sink();
    var engine = new InstallerEngine(machine, new UnavailableKxPreferences(), sink);
    await FailsAsync(ErrorCode.ProviderUnavailable, () => engine.InstallAsync(s, "Test user", "00123456".AsMemory()));
    Assert(machine.EnsureCount == 0 && machine.NetworkCount == 0 && !sink.Stages.Contains(Stage.Finished));
});
AsyncTest("Driver conflict is attributed to driver phase before any mutation",async()=>
{
    var machine=new FakeMachine(configured with {Drivers=[driver with {Version="9.0.0.0"}]},configured);
    var sink=new Sink();
    await FailsAsync(ErrorCode.DriverConflict,()=>new InstallerEngine(machine,new FakePreferences(),sink).InstallAsync(s,"Sentinella","0073".AsMemory()));
    Assert(sink.Stages.Last()==Stage.Driver&&machine.EnsureCount==0);
});
foreach (var support in new[] { new PreferenceSupport(true, false, true), new PreferenceSupport(true, true, false) })
    AsyncTest("Unvalidated or global-secret adapter blocked " + tests.Count, async () =>
    {
        var machine = new FakeMachine(configured, configured);
        var provider = new FakePreferences { Availability = support };
        await FailsAsync(ErrorCode.ProviderNotValidated, () => new InstallerEngine(machine, provider, new Sink()).InstallAsync(s, "Test", "00123456".AsMemory()));
        Assert(machine.EnsureCount == 0 && provider.ApplyCount == 0);
    });
AsyncTest("Simulated orchestration preserves leading zeros in process", async () =>
{
    var provider = new FakePreferences();
    var sink = new Sink();
    var result = await new InstallerEngine(new FakeMachine(empty, configured), provider, sink).InstallAsync(s, "  À Test O'Name  ", "00123456".AsMemory());
    Assert(provider.CapturedName == "À Test O'Name" && provider.LeadingZeroPreserved && result.ConfigurationVerified && sink.Stages.Last() == Stage.Finished);
});
// Each independent missing proprietary check must prevent a success result.
for (int i = 0; i < 9; i++)
{
    int index = i;
    AsyncTest("Missing final preference check " + i + " blocks success", async () =>
    {
        var flags = Enumerable.Repeat(true, 9).ToArray(); flags[index] = false;
        var provider = new FakePreferences { Checks = new(flags[0],flags[1],flags[2],flags[3],flags[4],flags[5],flags[6],flags[7],flags[8]) };
        var sink = new Sink();
        await FailsAsync(ErrorCode.Verification, () => new InstallerEngine(new FakeMachine(configured, configured), provider, sink).InstallAsync(s, "Test", "00123456".AsMemory()));
        Assert(!sink.Stages.Contains(Stage.Finished));
    });
}
AsyncTest("Incomplete final queue blocks success", async () =>
{
    await FailsAsync(ErrorCode.Verification, () => new InstallerEngine(new FakeMachine(empty, empty), new FakePreferences(), new Sink()).InstallAsync(s, "Test", "00123456".AsMemory()));
});
AsyncTest("Existing installation updates name and code twice without reinstalling driver or creating queue", async () =>
{
    var machine = new FakeMachine(configured, configured);
    var preferences = new FakePreferences();
    var engine = new InstallerEngine(machine, preferences, new Sink());
    var first = await engine.InstallAsync(s, "Primo Nome", "0073".AsMemory());
    Assert(preferences.CapturedName == "Primo Nome" && preferences.CapturedAccount == "0073");
    var second = await engine.InstallAsync(s, "Secondo Nome", "0084".AsMemory());
    Assert(first.Kind == InstallKind.Configure && second.Kind == InstallKind.Configure && preferences.ApplyCount == 2);
    Assert(preferences.CapturedName == "Secondo Nome" && preferences.CapturedAccount == "0084");
    Assert(machine.Plans.Count == 2 && machine.Plans.All(p => !p.AddDriver && !p.AddPort && !p.AddQueue && !p.RepairQueue));
});
AsyncTest("Cancelled preflight never mutates", async () =>
{
    var machine = new FakeMachine(configured, configured);
    using var cts = new CancellationTokenSource(); cts.Cancel();
    try { await new InstallerEngine(machine, new FakePreferences(), new Sink()).InstallAsync(s, "Test", "00123456".AsMemory(), token: cts.Token); throw new Exception("Cancellation expected"); }
    catch (OperationCanceledException) { Assert(machine.EnsureCount == 0); }
});
Test("Error messages contain no synthetic secret", () => Assert(Enum.GetValues<ErrorCode>().All(code => !Messages.For(code).Contains("00123456"))));

int failed = 0;
foreach (var test in tests)
{
    try { await test.Run(); Console.WriteLine("PASS " + test.Name); }
    catch (Exception e) { failed++; Console.WriteLine("FAIL " + test.Name + " (" + e.GetType().Name + ")"); }
}
Console.WriteLine($"RESULT {tests.Count - failed}/{tests.Count} passed; {failed} failed. Tests use synthetic adapters; no printer modified.");
return failed == 0 ? 0 : 1;

sealed class Sink : IEventSink
{
    public List<Stage> Stages { get; } = [];
    public void Record(Stage stage, ErrorCode? error = null) => Stages.Add(stage);
}
sealed class FakeMachine(SystemState initial, SystemState final) : IWindowsMachine
{
    public List<InstallationPlan> Plans { get; } = [];
    private bool changed;
    public int EnsureCount { get; private set; }
    public int NetworkCount { get; private set; }
    public Task<SystemState> InspectAsync(CancellationToken token) { token.ThrowIfCancellationRequested(); return Task.FromResult(changed ? final : initial); }
    public Task<Endpoint> ResolveAndProbeAsync(PrinterSettings settings, CancellationToken token) { NetworkCount++; return Task.FromResult(new Endpoint(settings.Host, false)); }
    public Task EnsureAsync(PrinterSettings settings, Endpoint endpoint, InstallationPlan plan, CancellationToken token) { Plans.Add(plan); EnsureCount++; changed = true; return Task.CompletedTask; }
}
sealed class FakePreferences : IKxPreferences
{
    public PreferenceSupport Availability { get; init; } = new(true, true, true);
    public PreferenceChecks Checks { get; init; } = new(true,true,true,true,true,true,true,true,true);
    public int ApplyCount { get; private set; }
    public string? CapturedName { get; private set; }
    public string? CapturedAccount { get; private set; }
    public bool LeadingZeroPreserved { get; private set; }
    public PreferenceSupport Support(PrinterSettings settings) => Availability;
    public Task ApplyForCurrentUserAsync(string queue, string userName, ReadOnlyMemory<char> account, CancellationToken token)
    { ApplyCount++; CapturedName = userName; CapturedAccount = new string(account.Span); LeadingZeroPreserved = account.Span.SequenceEqual("00123456".AsSpan()); return Task.CompletedTask; }
    public Task<PreferenceChecks> VerifyForCurrentUserAsync(string queue, string userName, ReadOnlyMemory<char> account, CancellationToken token) => Task.FromResult(Checks);
}
