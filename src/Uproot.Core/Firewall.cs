using System.Runtime.InteropServices;

namespace Uproot.Core;

/// <summary>
/// One Windows Firewall rule, with everything needed to put it back. Programs add these the first
/// time they listen on the network, and uninstallers almost never remove them.
/// </summary>
public sealed record FirewallRuleInfo
{
    public required string Name { get; init; }
    public string? Description { get; init; }
    public string? ApplicationName { get; init; }
    public string? ServiceName { get; init; }
    public int Protocol { get; init; } = 256;
    public string? LocalPorts { get; init; }
    public string? RemotePorts { get; init; }
    public string? LocalAddresses { get; init; }
    public string? RemoteAddresses { get; init; }
    public string? IcmpTypesAndCodes { get; init; }

    /// <summary>1 inbound, 2 outbound.</summary>
    public int Direction { get; init; } = 1;

    /// <summary>0 block, 1 allow.</summary>
    public int Action { get; init; } = 1;

    public bool Enabled { get; init; } = true;
    public string? Grouping { get; init; }
    public int Profiles { get; init; } = 0x7FFFFFFF;
    public bool EdgeTraversal { get; init; }
    public string? InterfaceTypes { get; init; }
}

/// <summary>The firewall as the scanner and remover see it, so tests can stand in for it.</summary>
public interface IFirewall
{
    /// <summary>Every rule, with everything needed to recreate it.</summary>
    IReadOnlyList<FirewallRuleInfo> ReadAll(CancellationToken ct = default);

    /// <summary>Every rule's name, program and group: enough to scan with, and much quicker to read.</summary>
    IReadOnlyList<FirewallRuleInfo> ReadSummaries(CancellationToken ct = default) => ReadAll(ct);

    /// <summary>Removes every rule with this name. True when none is left.</summary>
    bool RemoveAll(string name);

    void Add(FirewallRuleInfo rule);
}

/// <summary>
/// Windows Firewall through its COM interface, late-bound like the Task Scheduler. Reading works
/// from a standard account; changing rules needs administrator rights.
/// </summary>
public sealed class WindowsFirewall : IFirewall
{
    public static readonly WindowsFirewall Instance = new();

    private const int ProtocolTcp = 6, ProtocolUdp = 17, ProtocolIcmp4 = 1, ProtocolIcmp6 = 58;

    private static dynamic Policy()
    {
        var type = Type.GetTypeFromProgID("HNetCfg.FwPolicy2") ?? throw new InvalidOperationException("Windows Firewall is unavailable.");
        return Activator.CreateInstance(type)!;
    }

    /// <summary>
    /// Every rule, for removing or restoring. Throws when the rules cannot all be read: a rule
    /// that was not read is a rule that was not judged, and removal by name would take it too.
    /// </summary>
    public IReadOnlyList<FirewallRuleInfo> ReadAll(CancellationToken ct = default) => Read(ct, details: true, failClosed: true);

    /// <summary>Where the firewall keeps its local rules, one value per rule, readable without administrator rights.</summary>
    private const string RulesKey = @"SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\FirewallRules";

    /// <summary>
    /// Read from the firewall's own registry store, which takes milliseconds where walking the
    /// rules through COM takes a second. Removal still goes through COM, and reads each rule
    /// afresh there before judging it.
    /// </summary>
    public IReadOnlyList<FirewallRuleInfo> ReadSummaries(CancellationToken ct = default)
    {
        using var key = RegistryLocation.Machine(RulesKey).Open();
        if (key is null) return Read(ct, details: false);

        var rules = new List<FirewallRuleInfo>();

        try
        {
            foreach (var valueName in key.GetValueNames())
            {
                ct.ThrowIfCancellationRequested();

                if (key.GetValue(valueName) is not string data) continue;

                var fields = ParseStoredRule(data);
                if (!fields.TryGetValue("Name", out var name) || name.Length == 0) continue;

                rules.Add(new FirewallRuleInfo
                {
                    Name = name,
                    ApplicationName = fields.GetValueOrDefault("App"),
                    Grouping = fields.GetValueOrDefault("EmbedCtxt")
                });
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return Read(ct, details: false);
        }

        return rules;
    }

    /// <summary>
    /// A stored rule, "v2.33|Action=Allow|Dir=In|App=C:\…\x.exe|Name=X|EmbedCtxt=Y|", as fields.
    /// Where a field repeats, as ports can, the first is kept.
    /// </summary>
    internal static Dictionary<string, string> ParseStoredRule(string data)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var part in data.Split('|').Skip(1))
        {
            var equals = part.IndexOf('=');
            if (equals <= 0) continue;

            fields.TryAdd(part[..equals], part[(equals + 1)..]);
        }

        return fields;
    }

    private static IReadOnlyList<FirewallRuleInfo> Read(CancellationToken ct, bool details, bool failClosed = false)
    {
        var rules = new List<FirewallRuleInfo>();

        try
        {
            foreach (dynamic rule in Policy().Rules)
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    rules.Add(details ? Read(rule) : Summary(rule));
                }
                catch (Exception ex) when (ex is COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException or InvalidCastException)
                {
                    // A rule whose name cannot be read cannot be removed by name either, so
                    // skipping it hides nothing removal could reach.
                }
            }
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or InvalidOperationException
                                       or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            if (failClosed) throw new InvalidOperationException($"Windows Firewall's rules could not all be read: {ex.Message}", ex);
        }

        return rules;
    }

    private static string? Text(Func<object?> read)
    {
        try { return read() as string; }
        catch (Exception ex) when (ex is COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException) { return null; }
    }

    private static FirewallRuleInfo Summary(dynamic rule) => new()
    {
        Name = (string)rule.Name,
        ApplicationName = Text(() => rule.ApplicationName),
        Grouping = Text(() => rule.Grouping)
    };

    private static FirewallRuleInfo Read(dynamic rule)
    {

        int Number(Func<object?> read, int fallback)
        {
            try { return read() is { } value ? Convert.ToInt32(value) : fallback; }
            catch (Exception ex) when (ex is COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException or InvalidCastException or FormatException) { return fallback; }
        }

        bool Flag(Func<object?> read)
        {
            try { return read() is true; }
            catch (Exception ex) when (ex is COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException) { return false; }
        }

        return new FirewallRuleInfo
        {
            Name = (string)rule.Name,
            Description = Text(() => rule.Description),
            ApplicationName = Text(() => rule.ApplicationName),
            ServiceName = Text(() => rule.ServiceName),
            Protocol = Number(() => rule.Protocol, 256),
            LocalPorts = Text(() => rule.LocalPorts),
            RemotePorts = Text(() => rule.RemotePorts),
            LocalAddresses = Text(() => rule.LocalAddresses),
            RemoteAddresses = Text(() => rule.RemoteAddresses),
            IcmpTypesAndCodes = Text(() => rule.IcmpTypesAndCodes),
            Direction = Number(() => rule.Direction, 1),
            Action = Number(() => rule.Action, 1),
            Enabled = Flag(() => rule.Enabled),
            Grouping = Text(() => rule.Grouping),
            Profiles = Number(() => rule.Profiles, 0x7FFFFFFF),
            EdgeTraversal = Flag(() => rule.EdgeTraversal),
            InterfaceTypes = Text(() => rule.InterfaceTypes)
        };
    }

    /// <summary>
    /// Removes by name until none is left. The COM interface takes a name, not a rule, and may
    /// remove only the first match each time; the scan has already judged every rule of that name.
    /// </summary>
    public bool RemoveAll(string name)
    {
        var rules = Policy().Rules;

        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (!Exists(rules, name)) return true;
            rules.Remove(name);
        }

        return !Exists(rules, name);
    }

    private static bool Exists(dynamic rules, string name)
    {
        try
        {
            _ = rules.Item(name);
            return true;
        }
        catch (Exception ex) when (ex is COMException or FileNotFoundException or ArgumentException)
        {
            return false;
        }
    }

    public void Add(FirewallRuleInfo info)
    {
        var type = Type.GetTypeFromProgID("HNetCfg.FWRule") ?? throw new InvalidOperationException("Windows Firewall is unavailable.");
        dynamic rule = Activator.CreateInstance(type)!;

        rule.Name = info.Name;
        if (info.Description is not null) rule.Description = info.Description;
        if (info.ApplicationName is not null) rule.ApplicationName = info.ApplicationName;
        if (info.ServiceName is not null) rule.ServiceName = info.ServiceName;

        // The protocol has to be set before ports or ICMP types, which it constrains.
        rule.Protocol = info.Protocol;
        if (info.Protocol is ProtocolTcp or ProtocolUdp)
        {
            if (info.LocalPorts is not null) rule.LocalPorts = info.LocalPorts;
            if (info.RemotePorts is not null) rule.RemotePorts = info.RemotePorts;
        }

        if (info.Protocol is ProtocolIcmp4 or ProtocolIcmp6 && info.IcmpTypesAndCodes is not null)
            rule.IcmpTypesAndCodes = info.IcmpTypesAndCodes;

        if (info.LocalAddresses is not null) rule.LocalAddresses = info.LocalAddresses;
        if (info.RemoteAddresses is not null) rule.RemoteAddresses = info.RemoteAddresses;
        rule.Direction = info.Direction;
        rule.Action = info.Action;
        if (info.Grouping is not null) rule.Grouping = info.Grouping;
        rule.Profiles = info.Profiles;
        rule.EdgeTraversal = info.EdgeTraversal;
        if (info.InterfaceTypes is not null) rule.InterfaceTypes = info.InterfaceTypes;
        rule.Enabled = info.Enabled;

        Policy().Rules.Add(rule);
    }
}
