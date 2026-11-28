using Meshmakers.Octo.Backend.McpServices.MethodClients.DataTransferObjects.System.Identity.v3;
using Meshmakers.Octo.Backend.McpServices.Models;

namespace Meshmakers.Octo.Backend.McpServices.Tools;

/// <summary>
///     Turns the result record of <c>Client.SetMirrorRoles-1</c> into one row per child tenant, with the same markers and
///     role sets as octo-cli <c>SetClientMirrorRoles</c>:
///     <list type="bullet">
///         <item>roles before = already present + removed + hand-set; roles after = already present + added + hand-set
///         (a failed mirror keeps the roles before);</item>
///         <item>a dry run names what would happen (<c>WOULD-ADD</c>, <c>WOULD-REMOVE</c>, <c>WOULD-ADOPT</c>), a real run what
///         happened (<c>ADDED</c>, <c>REMOVED</c>, <c>ADOPTED</c>).</item>
///     </list>
/// </summary>
internal static class ClientMirrorRoleRows
{
    private const string Failed = "Failed";

    internal static SetClientMirrorRolesResponse ToResponse(RtClientMirrorRolesResultRecordDto? result, bool dryRun)
    {
        var rows = (result?.Tenants ?? [])
            .Where(t => t != null)
            .Select(t => ToRow(t!, result?.DryRun ?? dryRun))
            .OrderBy(r => r.ChildTenantId, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new SetClientMirrorRolesResponse
        {
            IsSuccess = true,
            DryRun = result?.DryRun ?? dryRun,
            RoleNames = Clean(result?.RoleNames),
            Tenants = rows,
            FailedTenants = rows.Where(r => string.Equals(r.Outcome, Failed, StringComparison.OrdinalIgnoreCase))
                .Select(r => r.ChildTenantId ?? string.Empty).ToList()
        };
    }

    internal static ClientMirrorRoleTenantRow ToRow(RtClientMirrorRoleTenantResultRecordDto tenant, bool dryRun)
    {
        var row = new ClientMirrorRoleTenantRow
        {
            ChildTenantId = tenant.ChildTenantId,
            Outcome = tenant.Outcome,
            Added = Clean(tenant.Added),
            Removed = Clean(tenant.Removed),
            AlreadyPresent = Clean(tenant.AlreadyPresent),
            Adopted = Clean(tenant.Adopted),
            HandSet = Clean(tenant.HandSet),
            RoleMissing = Clean(tenant.RoleMissing),
            Declined = Clean(tenant.Declined),
            RoleRefused = Clean(tenant.RoleRefused)
        };
        var failed = string.Equals(row.Outcome, Failed, StringComparison.OrdinalIgnoreCase);
        row.RolesBefore = Union(row.AlreadyPresent, row.Removed, row.HandSet);
        row.RolesAfter = failed ? row.RolesBefore : Union(row.AlreadyPresent, row.Added, row.HandSet);

        if (failed)
        {
            row.Status.Add("FAILED");
        }
        else if (string.Equals(row.Outcome, "Skipped", StringComparison.OrdinalIgnoreCase))
        {
            row.Status.Add("SKIPPED");
        }

        Mark(row.Status, row.Added, dryRun ? "WOULD-ADD" : "ADDED");
        Mark(row.Status, row.Removed, dryRun ? "WOULD-REMOVE" : "REMOVED");
        Mark(row.Status, row.Adopted, dryRun ? "WOULD-ADOPT" : "ADOPTED");
        Mark(row.Status, row.AlreadyPresent.Except(row.Adopted, StringComparer.OrdinalIgnoreCase).ToList(), "ALREADY-PRESENT");
        Mark(row.Status, row.HandSet, "HAND-SET");
        Mark(row.Status, row.RoleMissing, "ROLE-MISSING");
        Mark(row.Status, row.Declined, "DECLINED");
        Mark(row.Status, row.RoleRefused, "ROLE-REFUSED");
        if (row.Status.Count == 0)
        {
            row.Status.Add("NO-ROLES");
        }

        return row;
    }

    private static void Mark(List<string> status, IReadOnlyCollection<string> names, string marker)
    {
        if (names.Count > 0)
        {
            status.Add(marker);
        }
    }

    private static List<string> Clean(IEnumerable<string?>? names) =>
        (names ?? []).Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n!)
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();

    private static List<string> Union(params IEnumerable<string>[] lists) =>
        lists.SelectMany(l => l).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();
}
