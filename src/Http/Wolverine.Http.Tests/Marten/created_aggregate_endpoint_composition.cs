using Alba;
using IntegrationTests;
using JasperFx;
using JasperFx.Core.TypeScanning;
using Marten;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Wolverine.Attributes;
using Wolverine.Marten;
using Wolverine.Persistence;
using Xunit;

namespace Wolverine.Http.Tests.Marten;

// The CreatedAggregate OpenAPI metadata and its HTTP-chain frame composition only run through
// HttpChain compilation, so neither is reachable from the MartenTests message-handler suite.
// The endpoint carries [WolverineIgnore] so no other host in this assembly picks it up - it is
// re-included through CustomizeHttpEndpointDiscovery.
public class created_aggregate_endpoint_composition
{
    private static Task<IAlbaHost> buildHostAsync(Type endpointType)
    {
        var builder = WebApplication.CreateBuilder();

        builder.Host.UseWolverine(opts =>
        {
            opts.Durability.Mode = DurabilityMode.Solo;
            opts.Discovery.DisableConventionalDiscovery();

            // The "integration" collection pins JasperFxOptions.RememberedApplicationAssembly to
            // WolverineWebApi, so this assembly has to be named explicitly or endpoint discovery
            // scans the wrong one when the whole suite runs.
            opts.Discovery.IncludeAssembly(typeof(created_aggregate_endpoint_composition).Assembly);

            opts.Services.AddMarten(m =>
            {
                m.Connection(Servers.PostgresConnectionString);
                m.DatabaseSchemaName = "created_aggregate_http";
            }).IntegrateWithWolverine();
        });

        builder.Services.AddWolverineHttp();

        return AlbaHost.For(builder, app => app.MapWolverineEndpoints(opts =>
            opts.CustomizeHttpEndpointDiscovery(q =>
            {
                q.Excludes.WithCondition("Not the endpoint under test", t => t != endpointType);
                q.Includes.WithCondition("The endpoint under test", t => t == endpointType);
            })));
    }

    // CreationAwarePolicy has to register its metadata mutation with Finally() rather than Add().
    // BuildEndpoint runs establishResourceTypeMetadata first, and that appends Wolverine's built-in
    // Produces(200) to the very list the policy registered into - so an Add() convention runs before
    // the 200 exists and removes nothing, leaving the endpoint advertising both 200 and 201.
    [Fact]
    public async Task creation_metadata_replaces_the_200_rather_than_adding_alongside_it()
    {
        await using var host = await buildHostAsync(typeof(ValidCreatedAggregateEndpoint));

        var chain = host.Services.GetRequiredService<WolverineHttpOptions>()
            .Endpoints!.ChainFor("POST", "/created-aggregate/valid");
        chain.ShouldNotBeNull();

        var statusCodes = chain.Endpoint!.Metadata
            .OfType<IProducesResponseTypeMetadata>()
            .Select(x => x.StatusCode)
            .ToArray();

        statusCodes.ShouldContain(201);
        statusCodes.ShouldNotContain(200);
    }

    // Guards the load-bearing frame placement in CreatedAggregate<T>.ConfigureResponse:
    // HttpChain.Codegen emits return actions only for Method.Creates.Skip(1), and the marker is
    // the single return value, i.e. Creates[0]. If the stream-start were ever moved from the
    // postprocessors onto the marker's return action it would still pass every message-handler
    // test and silently stop persisting on HTTP endpoints - exactly what this test would catch.
    [Fact]
    public async Task http_chain_persists_the_stream_and_returns_the_aggregate()
    {
        await using var host = await buildHostAsync(typeof(ValidCreatedAggregateEndpoint));

        var id = Guid.NewGuid();

        var result = await host.Scenario(x =>
        {
            x.Post.Json(new StartCreatedAggregate(id)).ToUrl("/created-aggregate/valid");
            x.StatusCodeShouldBe(201);
            x.Header("Location").SingleValueShouldEqual($"/created-aggregate/{id}");
        });

        var aggregate = await result.ReadAsJsonAsync<CreatedAggregateTarget>();
        aggregate.ShouldNotBeNull();
        aggregate.Id.ShouldBe(id);
        aggregate.Count.ShouldBe(1);

        await using var session = host.Services.GetRequiredService<IDocumentStore>().LightweightSession();
        var events = await session.Events.FetchStreamAsync(id, token: TestContext.Current.CancellationToken);
        events.Count.ShouldBe(1);
    }
}

public class CreatedAggregateTarget
{
    public Guid Id { get; set; }
    public int Count { get; set; }

    public void Apply(CreatedAggregateCounted _) => Count++;
}

public record CreatedAggregateCounted;

public record StartCreatedAggregate(Guid Id);

[WolverineIgnore]
public static class ValidCreatedAggregateEndpoint
{
    [WolverinePost("/created-aggregate/valid")]
    public static CreatedAggregate<CreatedAggregateTarget> Post(StartCreatedAggregate command)
    {
        return new CreatedAggregate<CreatedAggregateTarget>(
            MartenOps.StartStream<CreatedAggregateTarget>(command.Id, new CreatedAggregateCounted()),
            $"/created-aggregate/{command.Id}");
    }
}
