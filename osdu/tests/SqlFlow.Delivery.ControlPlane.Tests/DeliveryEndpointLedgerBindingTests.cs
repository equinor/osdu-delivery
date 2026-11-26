using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using SqlFlow.ControlPlane.Hosting;
using SqlFlow.Delivery.ControlPlane;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Ledger;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// The module's endpoints take the part of the ledger, or the store beside it, each one uses, and a minimal API handler
/// reads such a parameter from the host's services only when the host registered its type: one it did not would be read
/// from the request's body, which a GET refuses when the endpoints are built, at start. So every endpoint the module maps
/// is built here as the host builds it, with the ledger's services registered as
/// <see cref="DeliveryServices.AddDeliveryLedger(IServiceCollection)"/> registers them. Every other parameter is taken for a
/// service, so nothing but a part of the ledger or a store can be read from a body; the module's API suites start the real
/// host. No database: building an endpoint resolves nothing.
/// </summary>
public sealed class DeliveryEndpointLedgerBindingTests
{
    /// <summary>The ledger, each part of its contract and each store beside it: the types a handler may take of it.</summary>
    private static readonly IReadOnlySet<Type> LedgerTypes = new HashSet<Type>(
        typeof(ILedger).GetInterfaces()
            .Concat(typeof(ILedger).GetProperties().Select(p => p.PropertyType))
            .Append(typeof(ILedger)));

    [Fact]
    public void Every_endpoint_reads_the_part_of_the_ledger_or_the_store_it_takes_from_the_host_s_services()
    {
        using var host = Host();
        using var registered = new ServiceCollection().AddDeliveryLedger().BuildServiceProvider();
        var endpoints = Endpoints(host, registered);

        var built = endpoints.DataSources.SelectMany(source => source.Endpoints).OfType<RouteEndpoint>().ToList();

        Assert.NotEmpty(built);
        var bodies = built
            .SelectMany(e => e.Metadata.GetOrderedMetadata<IAcceptsMetadata>().Select(a => (Endpoint: e, a.RequestType)))
            .Where(a => a.RequestType is not null && LedgerTypes.Contains(a.RequestType))
            .Select(a => $"{a.Endpoint.DisplayName} reads {a.RequestType!.Name} from its body")
            .ToList();
        Assert.Empty(bodies);

        // Each part of the ledger or store a handler takes is one the host registers, and the handlers take the parts and
        // stores, not only the whole ledger.
        var taken = built
            .Select(e => e.Metadata.OfType<MethodInfo>().FirstOrDefault())
            .OfType<MethodInfo>()
            .SelectMany(m => m.GetParameters().Select(p => p.ParameterType))
            .Where(LedgerTypes.Contains)
            .ToHashSet();
        var isService = registered.GetRequiredService<IServiceProviderIsService>();
        Assert.All(taken, type => Assert.True(isService.IsService(type), $"A handler takes {type.Name}, which the host does not register."));
        Assert.Contains(typeof(ILedgerRegistry), taken);
        Assert.Contains(typeof(DimensionStore), taken);
        Assert.Contains(typeof(InventoryStore), taken);
    }

    [Fact]
    public void Without_the_ledger_s_services_a_handler_taking_a_part_of_it_fails_to_build()
    {
        // The check above bites: with no ledger registered, a GET handler taking a part or a store would read it from its
        // body, and building the endpoints refuses that.
        using var host = Host();
        using var none = new ServiceCollection().BuildServiceProvider();
        var endpoints = Endpoints(host, none);

        var refused = Assert.Throws<InvalidOperationException>(
            () => endpoints.DataSources.SelectMany(source => source.Endpoints).ToList());

        Assert.Contains("Body was inferred", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>What building an endpoint reads of the host: its logging, options and routing.</summary>
    private static ServiceProvider Host() => new ServiceCollection().AddLogging().AddOptions().AddRouting().BuildServiceProvider();

    /// <summary>
    /// The module's endpoints, mapped by <see cref="DeliveryControlPlaneModule.MapEndpoints"/> on <c>/api/v1</c> as the
    /// control plane maps a module's, over <paramref name="host"/>'s services, which answer for the ledger's types as
    /// <paramref name="ledger"/> does.
    /// </summary>
    private static Routes Endpoints(ServiceProvider host, ServiceProvider ledger)
    {
        var routes = new Routes(new HostServices(host, ledger.GetRequiredService<IServiceProviderIsService>()));
        var api = routes.MapGroup("/api/v1");

        // The control plane hands a module its groups through an internal constructor (ControlPlaneModules), which a
        // module's own tests reach the same way: one call, so the module's own MapEndpoints decides what is mapped.
        var constructor = typeof(ControlPlaneModuleEndpoints).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single();
        var groups = (ControlPlaneModuleEndpoints)constructor.Invoke([new DeliveryControlPlaneModule().Name, routes.ServiceProvider, api]);
        new DeliveryControlPlaneModule().MapEndpoints(groups);
        return routes;
    }

    /// <summary>A route builder over <paramref name="services"/>, which the endpoints it maps are built with.</summary>
    private sealed class Routes(IServiceProvider services) : IEndpointRouteBuilder
    {
        public IServiceProvider ServiceProvider { get; } = services;

        public ICollection<EndpointDataSource> DataSources { get; } = [];

        public IApplicationBuilder CreateApplicationBuilder() => new ApplicationBuilder(ServiceProvider);
    }

    /// <summary>
    /// The services the endpoints are built with: <paramref name="inner"/>'s, with whether a type is a service answered by
    /// <paramref name="ledger"/> for the ledger's types and taken as yes for every other type.
    /// </summary>
    private sealed class HostServices(IServiceProvider inner, IServiceProviderIsService ledger) : IServiceProvider, IServiceProviderIsService
    {
        public object? GetService(Type serviceType)
            => serviceType == typeof(IServiceProviderIsService) ? this : inner.GetService(serviceType);

        public bool IsService(Type serviceType) => !LedgerTypes.Contains(serviceType) || ledger.IsService(serviceType);
    }
}
