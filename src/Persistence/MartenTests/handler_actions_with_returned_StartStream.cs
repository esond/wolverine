using IntegrationTests;
using JasperFx.Events;
using Marten;
using Marten.Events;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using JasperFx.Resources;
using MartenTests.AggregateHandlerWorkflow;
using Shouldly;
using Wolverine;
using Wolverine.Marten;
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

public record StartStreamMessage(Guid Id);
public record StartStreamMessage2(string Id);
public record StartLetterMessage(int A, int B);
public record StartTwoStreamsMessage(Guid LetterId, Guid DocumentId);
public record StartLetterBookMessage(string Id);
public record InvalidStartMessage;

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