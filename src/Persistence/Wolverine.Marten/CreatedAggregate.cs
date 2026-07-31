using JasperFx.CodeGeneration.Frames;
using Marten.Events;
using Wolverine.Configuration;
using Wolverine.Marten.Codegen;
using Wolverine.Marten.Persistence.Sagas;
using Wolverine.Persistence;

namespace Wolverine.Marten;

/// <summary>
/// Use this as a response from a message handler or HTTP endpoint that starts a new
/// event stream through an <see cref="IStartStream"/> side effect to respond with the
/// newly created, projected aggregate after the transaction commits. The creation-flow
/// counterpart to <see cref="UpdatedAggregate"/>.
/// </summary>
/// <typeparam name="T">The aggregate type started by the accompanying <see cref="IStartStream"/> return value</typeparam>
public class CreatedAggregate<T> : IResponseAware where T : class
{
    public static void ConfigureResponse(IChain chain)
    {
        var stream = chain.ReturnVariablesOfType<IStartStream>().FirstOrDefault();
        if (stream == null)
        {
            throw new InvalidOperationException(
                $"CreatedAggregate<{typeof(T).Name}> cannot be used because Chain {chain} does not also return an {nameof(IStartStream)} value. Return one with MartenOps.StartStream<{typeof(T).Name}>()");
        }

        // A creation chain has no [AggregateHandler]/[Aggregate] usage to add the Marten
        // transactional frames during attribute processing, and the AutoApplyTransactions
        // policy runs after IResponseAware configuration — which would leave the FetchLatest
        // frame *before* the SaveChangesAsync postprocessor. Add the transactional frames
        // here (idempotently, mirroring AggregateHandling.Apply) so the response fetch
        // lands after the commit.
        if (!chain.Middleware.OfType<CreateDocumentSessionFrame>().Any())
        {
            chain.Middleware.Add(new CreateDocumentSessionFrame(chain));
        }

        if (!chain.Postprocessors.OfType<DocumentSessionSaveChanges>().Any())
        {
            chain.Postprocessors.Add(new DocumentSessionSaveChanges());
        }

        if (!chain.Postprocessors.OfType<FlushOutgoingMessages>().Any())
        {
            chain.Postprocessors.Add(new FlushOutgoingMessages());
        }

        var call = new MethodCall(typeof(CreatedAggregate<T>),
            typeof(CreatedAggregate<T>).GetMethod(nameof(FetchAsync))!)
        {
            Arguments =
            {
                [0] = stream
            }
        };

        chain.UseForResponse(call);
    }

    public static ValueTask<T?> FetchAsync(IStartStream stream, IEventStoreOperations events,
        CancellationToken token)
    {
        return stream.StreamId != Guid.Empty
            ? events.FetchLatest<T>(stream.StreamId, token)
            : events.FetchLatest<T>(stream.StreamKey, token);
    }
}
