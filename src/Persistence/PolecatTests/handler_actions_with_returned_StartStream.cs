using IntegrationTests;
using JasperFx.Events;
using JasperFx.Events.Projections;
using JasperFx.Resources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Polecat;
using Polecat.Events;
using PolecatTests.AggregateHandlerWorkflow;
using Shouldly;
using Wolverine;
using Wolverine.Polecat;
using Wolverine.Tracking;

namespace PolecatTests;

public class handler_actions_with_returned_StartStream : IAsyncLifetime
{
    private IHost _host = null!;
    private IDocumentStore _store = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddPolecat(m =>
                    {
                        m.ConnectionString = Servers.SqlServerConnectionString;
                        m.DatabaseSchemaName = "start_stream";
                        m.Projections.Snapshot<LetterAggregate>(SnapshotLifecycle.Inline);
                    })
                    .IntegrateWithWolverine();

                opts.Policies.AutoApplyTransactions();

                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();

        _store = _host.Services.GetRequiredService<IDocumentStore>();
        await ((DocumentStore)_store).Database.ApplyAllConfiguredChangesToDatabaseAsync();
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

        await _host.InvokeMessageAndWaitAsync(new PcStartStreamMessage(id));

        await using var session = _store.LightweightSession();
        var events = await session.Events.FetchStreamAsync(id, token: TestContext.Current.CancellationToken);
        events.Count.ShouldBe(2);
        events[0].Data.ShouldBeOfType<AEvent>();
        events[1].Data.ShouldBeOfType<BEvent>();
    }

    [Fact]
    public async Task using_created_aggregate_as_response()
    {
        var (_, created) =
            await _host.InvokeMessageAndWaitAsync<LetterAggregate>(new PcStartLetterMessage(2, 1));

        created.ShouldNotBeNull();
        created.Id.ShouldNotBe(Guid.Empty);
        created.ACount.ShouldBe(2);
        created.BCount.ShouldBe(1);
    }

    [Fact]
    public async Task using_non_generic_created_aggregate_as_response()
    {
        var id = Guid.NewGuid();

        var (_, created) =
            await _host.InvokeMessageAndWaitAsync<LetterAggregate>(new PcStartLetterWithIdMessage(id));

        created.ShouldNotBeNull();
        created.Id.ShouldBe(id);
        created.ACount.ShouldBe(1);
        created.BCount.ShouldBe(1);
    }
}

public record PcStartStreamMessage(Guid Id);
public record PcStartLetterMessage(int A, int B);
public record PcStartLetterWithIdMessage(Guid Id);

public static class PcStartStreamMessageHandler
{
    public static IStartStream Handle(PcStartStreamMessage message)
    {
        return PolecatOps.StartStream<PcNamedDocument>(message.Id, new AEvent(), new BEvent());
    }

    public static (CreatedAggregate<LetterAggregate>, IStartStream) Handle(PcStartLetterMessage message)
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

        return (new CreatedAggregate<LetterAggregate>(), PolecatOps.StartStream<LetterAggregate>(events.ToArray()));
    }

    public static (CreatedAggregate, StartStream<LetterAggregate>) Handle(PcStartLetterWithIdMessage message)
    {
        return (new CreatedAggregate(),
            PolecatOps.StartStream<LetterAggregate>(message.Id, new LetterStarted(), new AEvent(), new BEvent()));
    }
}
