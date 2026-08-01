using IntegrationTests;
using JasperFx.Events;
using JasperFx.Events.Projections;
using Marten;
using JasperFx.MultiTenancy;
using Marten.Events;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using JasperFx.Resources;
using MartenTests.AggregateHandlerWorkflow;
using Shouldly;
using Wolverine;
using Wolverine.Marten;
using Wolverine.Persistence;
using Wolverine.Runtime;
using Wolverine.Runtime.Handlers;
using Wolverine.Tracking;

namespace MartenTests;

public class handler_actions_with_returned_StartStream : PostgresqlContext, IAsyncLifetime
{
    private IHost _host = null!;
    private IDocumentStore _store = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(StartStreamMessageHandler));
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Services
                    .AddMarten(Servers.PostgresConnectionString)
                    .IntegrateWithWolverine();

                opts.Policies.AutoApplyTransactions();

                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();

        _store = _host.Services.GetRequiredService<IDocumentStore>();

        await _store.Advanced.Clean.DeleteDocumentsByTypeAsync(typeof(NamedDocument));
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public async Task start_stream_by_guid1()
    {
        var id = Guid.NewGuid();

        await _host.InvokeMessageAndWaitAsync(new StartStreamMessage(id));

        using var session = _store.LightweightSession();
        var events = await session.Events.FetchStreamAsync(id, token: TestContext.Current.CancellationToken);
        events.Count.ShouldBe(2);
        events[0].Data.ShouldBeOfType<AEvent>();
        events[1].Data.ShouldBeOfType<BEvent>();
    }

    [Fact]
    public async Task using_created_aggregate_as_response()
    {
        var (_, created) =
            await _host.InvokeMessageAndWaitAsync<LetterAggregate>(new StartLetterMessage(2, 1));

        created.ShouldNotBeNull();
        created.Id.ShouldNotBe(Guid.Empty);
        created.ACount.ShouldBe(2);
        created.BCount.ShouldBe(1);
    }

    [Fact]
    public async Task created_aggregate_disambiguates_by_stream_type()
    {
        var letterId = Guid.NewGuid();
        var documentId = Guid.NewGuid();

        var (_, created) =
            await _host.InvokeMessageAndWaitAsync<LetterAggregate>(new StartTwoStreamsMessage(letterId, documentId));

        created.ShouldNotBeNull();
        created.Id.ShouldBe(letterId);
        created.ACount.ShouldBe(1);

        using var session = _store.LightweightSession();
        var events = await session.Events.FetchStreamAsync(documentId, token: TestContext.Current.CancellationToken);
        events.Count.ShouldBe(1);
    }

    [Fact]
    public async Task created_aggregate_with_no_start_stream_complains()
    {
        var ex = await Should.ThrowAsync<InvalidOperationException>(async () =>
        {
            using var host = await Host.CreateDefaultBuilder()
                .UseWolverine(opts =>
                {
                    opts.Discovery.DisableConventionalDiscovery()
                        .IncludeType(typeof(MissingStartStreamHandler));
                    opts.Durability.Mode = DurabilityMode.Solo;
                    opts.Services
                        .AddMarten(Servers.PostgresConnectionString)
                        .IntegrateWithWolverine();
                }).StartAsync();

            await host.InvokeMessageAndWaitAsync(new InvalidStartMessage());
        });

        ex.Message.ShouldContain("does not also return an IStartStream value");
    }

    [Fact]
    public async Task created_aggregate_with_mismatched_stream_type_complains()
    {
        var ex = await Should.ThrowAsync<InvalidOperationException>(async () =>
        {
            using var host = await Host.CreateDefaultBuilder()
                .UseWolverine(opts =>
                {
                    opts.Discovery.DisableConventionalDiscovery()
                        .IncludeType(typeof(MismatchedStreamTypeHandler));
                    opts.Durability.Mode = DurabilityMode.Solo;
                    opts.Services
                        .AddMarten(Servers.PostgresConnectionString)
                        .IntegrateWithWolverine();
                }).StartAsync();

            await host.InvokeMessageAndWaitAsync(new MismatchedStartMessage(Guid.NewGuid()));
        });

        ex.Message.ShouldContain("starts a different aggregate type");
    }

    // The cascaded response has to be enqueued BEFORE the outbox flush. HandlerChain.UseForResponse
    // appends CaptureCascadingMessages to the postprocessors, so a FlushOutgoingMessages that
    // MartenOpPolicy already added has to be moved back to the end. Otherwise the reply is enqueued
    // after the flush already ran and MultiFlushMode.OnlyOnce drops it (GH-3499). Asserted at the
    // composition surface: InvokeMessageAndWaitAsync sets DoNotCascadeResponse, so a runtime test
    // never exercises the enqueue path at all.
    [Fact]
    public async Task flush_outgoing_messages_runs_after_the_cascading_response_capture()
    {
        // Postprocessors are only assembled when the chain compiles, so drive one message through first.
        await _host.InvokeMessageAndWaitAsync<LetterAggregate>(new StartLetterMessage(1, 1));

        var chain = _host.Services.GetRequiredService<IWolverineRuntime>()
            .Options.HandlerGraph.ChainFor<StartLetterMessage>();

        chain.ShouldNotBeNull();

        var postprocessors = chain.Postprocessors;
        var flushAt = postprocessors.FindIndex(x => x is FlushOutgoingMessages);
        var captureAt = postprocessors.FindIndex(x => x is CaptureCascadingMessages);

        flushAt.ShouldBeGreaterThan(-1);
        captureAt.ShouldBeGreaterThan(-1);
        flushAt.ShouldBeGreaterThan(captureAt);
    }
}

public class start_stream_by_string_from_return_value : PostgresqlContext, IAsyncLifetime
{
    private IHost _host = null!;
    private IDocumentStore _store = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(StartStreamMessageHandler));
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Services
                    .AddMarten(m =>
                    {
                        m.Connection(Servers.PostgresConnectionString);
                        m.Events.StreamIdentity = StreamIdentity.AsString;
                        m.DatabaseSchemaName = "string_identity";
                    })
                    .IntegrateWithWolverine();

                opts.Policies.AutoApplyTransactions();

                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();

        _store = _host.Services.GetRequiredService<IDocumentStore>();

        await _store.Advanced.Clean.DeleteDocumentsByTypeAsync(typeof(NamedDocument));
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public async Task start_stream_by_string()
    {
        var id = Guid.NewGuid().ToString();

        await _host.InvokeMessageAndWaitAsync(new StartStreamMessage2(id));

        using var session = _store.LightweightSession();
        var events = await session.Events.FetchStreamAsync(id, token: TestContext.Current.CancellationToken);
        events.Count.ShouldBe(2);
        events[0].Data.ShouldBeOfType<CEvent>();
        events[1].Data.ShouldBeOfType<BEvent>();
    }

    [Fact]
    public async Task using_created_aggregate_as_response_with_string_identity()
    {
        var id = Guid.NewGuid().ToString();

        var (_, created) =
            await _host.InvokeMessageAndWaitAsync<LetterBook>(new StartLetterBookMessage(id));

        created.ShouldNotBeNull();
        created.Id.ShouldBe(id);
        created.ACount.ShouldBe(2);
        created.BCount.ShouldBe(1);
    }
}

// StartStream<T>.Execute commits through session.ForTenant(TenantId), so the CreatedAggregate response
// fetch has to be scoped the same way. Reading through the ambient session instead queries the wrong
// tenant's event store: normally a 404 for a stream that was created, and a cross-tenant read when the
// same stream id happens to exist under the ambient tenant.
public class created_aggregate_with_tenanted_stream : PostgresqlContext, IAsyncLifetime
{
    private IHost _host = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(TenantedStartStreamHandler));
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Services
                    .AddMarten(m =>
                    {
                        m.Connection(Servers.PostgresConnectionString);
                        m.DatabaseSchemaName = "created_aggregate_tenancy";
                        m.Events.TenancyStyle = TenancyStyle.Conjoined;
                        m.Policies.AllDocumentsAreMultiTenanted();
                        m.Projections.Snapshot<LetterAggregate>(SnapshotLifecycle.Inline);
                    })
                    .IntegrateWithWolverine();

                opts.Policies.AutoApplyTransactions();

                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public async Task response_is_fetched_from_the_stream_own_tenant()
    {
        var id = Guid.NewGuid();

        var (_, created) =
            await _host.InvokeMessageAndWaitAsync<LetterAggregate>(new StartTenantedLetterMessage(id, "acme"));

        created.ShouldNotBeNull();
        created.Id.ShouldBe(id);
        created.ACount.ShouldBe(1);
    }
}

public record StartStreamMessage(Guid Id);
public record StartTenantedLetterMessage(Guid Id, string TenantId);
public record StartStreamMessage2(string Id);
public record StartLetterMessage(int A, int B);
public record StartTwoStreamsMessage(Guid LetterId, Guid DocumentId);
public record StartLetterBookMessage(string Id);
public record InvalidStartMessage;
public record MismatchedStartMessage(Guid Id);

public class LetterBook
{
    public string Id { get; set; } = string.Empty;
    public int ACount { get; set; }
    public int BCount { get; set; }

    public void Apply(AEvent e) => ACount++;
    public void Apply(BEvent e) => BCount++;
}

public static class StartStreamMessageHandler
{
    public static IStartStream Handle(StartStreamMessage message)
    {
        return MartenOps.StartStream<NamedDocument>(message.Id, new AEvent(), new BEvent());
    }

    public static IStartStream Handle(StartStreamMessage2 message)
    {
        return MartenOps.StartStream<NamedDocument>(message.Id, new CEvent(), new BEvent());
    }

    public static (CreatedAggregate<LetterAggregate>, IStartStream) Handle(StartLetterMessage message)
    {
        var events = new List<object> { new LetterStarted() };
        for (var i = 0; i < message.A; i++)
        {
            events.Add(new AEvent());
        }

        for (var i = 0; i < message.B; i++)
        {
            events.Add(new BEvent());
        }

        return (new CreatedAggregate<LetterAggregate>(), MartenOps.StartStream<LetterAggregate>(events.ToArray()));
    }

    public static (CreatedAggregate<LetterAggregate>, StartStream<LetterAggregate>, StartStream<NamedDocument>) Handle(
        StartTwoStreamsMessage message)
    {
        return (
            new CreatedAggregate<LetterAggregate>(),
            MartenOps.StartStream<LetterAggregate>(message.LetterId, new LetterStarted(), new AEvent()),
            MartenOps.StartStream<NamedDocument>(message.DocumentId, new AEvent())
        );
    }

    public static (CreatedAggregate<LetterBook>, IStartStream) Handle(StartLetterBookMessage message)
    {
        return (new CreatedAggregate<LetterBook>(),
            MartenOps.StartStream<LetterBook>(message.Id, new AEvent(), new AEvent(), new BEvent()));
    }
}

public static class MissingStartStreamHandler
{
    public static CreatedAggregate<LetterAggregate> Handle(InvalidStartMessage message) => new();
}

public static class TenantedStartStreamHandler
{
    public static (CreatedAggregate<LetterAggregate>, StartStream<LetterAggregate>) Handle(
        StartTenantedLetterMessage message)
    {
        return (new CreatedAggregate<LetterAggregate>(),
            MartenOps.StartStream<LetterAggregate>(message.Id, message.TenantId, new LetterStarted(), new AEvent()));
    }
}

public static class MismatchedStreamTypeHandler
{
    // The marker names LetterAggregate but the only stream starts a NamedDocument. Wolverine has to
    // catch that at codegen time rather than project one aggregate's events into the other.
    public static (CreatedAggregate<LetterAggregate>, StartStream<NamedDocument>) Handle(
        MismatchedStartMessage message)
    {
        return (new CreatedAggregate<LetterAggregate>(),
            MartenOps.StartStream<NamedDocument>(message.Id, new AEvent()));
    }
}