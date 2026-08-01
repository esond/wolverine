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

// The CreatedAggregate error paths and its OpenAPI metadata only run through HttpChain compilation,
// so none of them are reachable from the MartenTests message-handler suite. ConfigureResponse runs in
// the HttpChain constructor, i.e. during MapWolverineEndpoints, so an invalid endpoint fails host
// startup rather than a request. Each endpoint therefore gets its own host, and all four carry
// [WolverineIgnore] so no other host in this assembly picks the invalid ones up - they are re-included
// one at a time through CustomizeHttpEndpointDiscovery.
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

    [Fact]
    public async Task created_aggregate_with_no_start_stream_complains()
    {
        var ex = await Should.ThrowAsync<InvalidOperationException>(async () =>
        {
            await using var host = await buildHostAsync(typeof(MissingStartStreamEndpoint));
        });

        ex.Message.ShouldContain("does not also return an IStartStream value");
    }

    [Fact]
    public async Task created_aggregate_with_mismatched_stream_type_complains()
    {
        var ex = await Should.ThrowAsync<InvalidOperationException>(async () =>
        {
            await using var host = await buildHostAsync(typeof(MismatchedStreamTypeEndpoint));
        });

        ex.Message.ShouldContain("starts a different aggregate type");
    }

    [Fact]
    public async Task created_aggregate_with_ambiguous_streams_complains()
    {
        var ex = await Should.ThrowAsync<InvalidOperationException>(async () =>
        {
            await using var host = await buildHostAsync(typeof(AmbiguousStreamsEndpoint));
        });

        ex.Message.ShouldContain("cannot determine which one starts");
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
}

public class CreatedAggregateTarget
{
    public Guid Id { get; set; }
    public int Count { get; set; }

    public void Apply(CreatedAggregateCounted _) => Count++;
}

public class OtherAggregateTarget
{
    public Guid Id { get; set; }
}

public record CreatedAggregateCounted;

public record StartCreatedAggregate(Guid Id);

[WolverineIgnore]
public static class ValidCreatedAggregateEndpoint
{
    [WolverinePost("/created-aggregate/valid")]
    public static (CreatedAggregate<CreatedAggregateTarget>, StartStream<CreatedAggregateTarget>) Post(
        StartCreatedAggregate command)
    {
        return (new CreatedAggregate<CreatedAggregateTarget>($"/created-aggregate/{command.Id}"),
            MartenOps.StartStream<CreatedAggregateTarget>(command.Id, new CreatedAggregateCounted()));
    }
}

[WolverineIgnore]
public static class MissingStartStreamEndpoint
{
    [WolverinePost("/created-aggregate/missing")]
    public static CreatedAggregate<CreatedAggregateTarget> Post(StartCreatedAggregate command) => new();
}

[WolverineIgnore]
public static class MismatchedStreamTypeEndpoint
{
    // The marker names CreatedAggregateTarget but the only stream starts an OtherAggregateTarget.
    [WolverinePost("/created-aggregate/mismatched")]
    public static (CreatedAggregate<CreatedAggregateTarget>, StartStream<OtherAggregateTarget>) Post(
        StartCreatedAggregate command)
    {
        return (new CreatedAggregate<CreatedAggregateTarget>(),
            MartenOps.StartStream<OtherAggregateTarget>(command.Id, new CreatedAggregateCounted()));
    }
}

[WolverineIgnore]
public static class AmbiguousStreamsEndpoint
{
    // Two IStartStream returns and neither declares a concrete StartStream<T>, so nothing can be
    // matched to the marker's type argument.
    [WolverinePost("/created-aggregate/ambiguous")]
    public static (CreatedAggregate<CreatedAggregateTarget>, IStartStream, IStartStream) Post(
        StartCreatedAggregate command)
    {
        return (new CreatedAggregate<CreatedAggregateTarget>(),
            MartenOps.StartStream<CreatedAggregateTarget>(new CreatedAggregateCounted()),
            MartenOps.StartStream<OtherAggregateTarget>(new CreatedAggregateCounted()));
    }
}
