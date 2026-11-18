using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using SqlFlow.ControlPlane.Configuration;

namespace SqlFlow.ControlPlane.Api;

/// <summary>
/// The estate's environments as the title bar's environment menu lists them: <see cref="Current"/> is the one this control
/// plane is (null when none is configured, or when the configuration does not say which it is), and
/// <see cref="Environments"/> every one the menu lists, in the configured order, empty when only the current one is named.
/// </summary>
public sealed record EnvironmentsDto(string? Current, IReadOnlyList<EnvironmentDto> Environments);

/// <summary>One environment: its name, what it is for, the address of its GUI, and whether this control plane is it.</summary>
public sealed record EnvironmentDto(string Name, string? Description, string Url, bool Current);

/// <summary>
/// <c>GET /api/v1/environments</c>: the environments the estate runs as separate deployments
/// (<c>ControlPlane:Environments</c>, read once at start), so the GUI's title bar names the one a person is in and opens
/// another. A mistake in the setting is left out here and logged at start, never refused. It answers a signed-in caller,
/// since the list names the estate's other addresses.
/// </summary>
public static class EnvironmentEndpoints
{
    public static RouteGroupBuilder MapEnvironmentEndpoints(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);
        group.MapGet("/environments", Get).WithTags("Environments").WithName("GetEnvironments");
        return group;
    }

    private static Ok<EnvironmentsDto> Get(ConfiguredEnvironments environments)
        => TypedResults.Ok(new EnvironmentsDto(
            environments.Current,
            [.. environments.Environments.Select(environment => new EnvironmentDto(
                environment.Name,
                environment.Description,
                environment.Url,
                string.Equals(environment.Name, environments.Current, StringComparison.Ordinal)))]));
}
