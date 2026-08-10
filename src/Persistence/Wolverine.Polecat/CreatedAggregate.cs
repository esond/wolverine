using JasperFx.CodeGeneration.Frames;
using Polecat;
using Wolverine.Configuration;
using Wolverine.Persistence;
using Wolverine.Persistence.Sagas;
using Wolverine.Polecat.Codegen;
using Wolverine.Polecat.Persistence.Sagas;

namespace Wolverine.Polecat;

/// <summary>
/// Use this as the single return value from a message handler or HTTP endpoint to start a new
/// event stream and respond with the newly created, projected aggregate after the transaction
/// commits. The creation-flow counterpart to <see cref="UpdatedAggregate"/>. HTTP endpoints
/// respond with a 201 status code, and optionally the Location response header when the Url
/// is supplied.
/// </summary>
/// <typeparam name="T">The aggregate type started by the wrapped <see cref="StartStream{T}"/> operation</typeparam>
public class CreatedAggregate<T> : IResponseAware, ICreationAware where T : class
{
    /// <param name="stream">The stream-start operation, e.g. from PolecatOps.StartStream&lt;T&gt;(...)</param>
    /// <param name="url">Optional value for the HTTP Location response header, e.g. the URL of the newly created resource</param>
    public CreatedAggregate(StartStream<T> stream, string? url = null)
    {
        Stream = stream ?? throw new ArgumentNullException(nameof(stream));
        Url = url;
    }

    public StartStream<T> Stream { get; }

    public string? Url { get; }

    public static void ConfigureResponse(IChain chain)
    {
        // tryApplyResponseAware enforces a single IResponseAware return per chain, and it is
        // exactly the variable that routed here
        var marker = chain.ReturnVariablesOfType<CreatedAggregate<T>>().Single();

        // PolecatOpPolicy routes message handler chains that return IPolecatOp values through
        // PolecatPersistenceFrameProvider.ApplyTransactionSupport, but the wrapped StartStream<T>
        // is not a return value here so that policy never sees this chain, Wolverine.Http has
        // no equivalent policy for endpoint chains, and the AutoApplyTransactions policy runs
        // after IResponseAware configuration — which would leave the FetchLatest frame *before*
        // the SaveChangesAsync postprocessor. Add the transactional frames here (idempotently,
        // mirroring ApplyTransactionSupport, SagaChain guard included) so the response fetch
        // lands after the commit.
        if (!chain.Middleware.OfType<CreateDocumentSessionFrame>().Any())
        {
            chain.Middleware.Add(new CreateDocumentSessionFrame(chain));
        }

        if (chain is not SagaChain)
        {
            if (!chain.Postprocessors.OfType<DocumentSessionSaveChanges>().Any())
            {
                chain.Postprocessors.Add(new DocumentSessionSaveChanges());
            }

            if (!chain.Postprocessors.OfType<FlushOutgoingMessages>().Any())
            {
                chain.Postprocessors.Add(new FlushOutgoingMessages());
            }
        }

        // IsTransactional is a declaration, not a measurement: every path that guarantees a commit
        // frame has to set it before IHttpPolicy / IHandlerPolicy authors read it (GH-3893).
        // AutoApplyTransactions cannot do it for this chain - its CanApply never matches because
        // the wrapped StartStream<T> is not a return variable.
        chain.IsTransactional = true;

        // The stream-start MUST be emitted as a postprocessor, never through the marker's return
        // action. HttpChain.Codegen emits return actions only for Method.Creates.Skip(1) — the
        // first created variable's return action is silently dropped on HTTP chains — while
        // HandlerChain emits all of them. As the single return value this marker IS Creates[0],
        // so a return action here would persist the events on message handlers but silently skip
        // persistence on HTTP endpoints. It also has to precede DocumentSessionSaveChanges or the
        // appended events are never committed.
        var execute = new MethodCall(typeof(CreatedAggregate<T>),
            typeof(CreatedAggregate<T>).GetMethod(nameof(Execute))!)
        {
            Arguments =
            {
                [0] = marker
            }
        };

        var saveChangesAt = chain.Postprocessors.FindIndex(x => x is DocumentSessionSaveChanges);
        if (saveChangesAt >= 0)
        {
            chain.Postprocessors.Insert(saveChangesAt, execute);
        }
        else
        {
            chain.Postprocessors.Add(execute);
        }

        var fetch = new MethodCall(typeof(CreatedAggregate<T>),
            typeof(CreatedAggregate<T>).GetMethod(nameof(FetchAsync))!)
        {
            Arguments =
            {
                [0] = marker
            }
        };

        chain.UseForResponse(fetch);

        // HandlerChain.UseForResponse appends the fetched aggregate's CaptureCascadingMessages
        // after the frames above, so an already-present FlushOutgoingMessages has to be moved
        // back to the end. Otherwise the cascaded response is enqueued after the outbox already
        // flushed and MultiFlushMode.OnlyOnce silently drops it (GH-3499).
        var flush = chain.Postprocessors.OfType<FlushOutgoingMessages>().FirstOrDefault();
        if (flush != null)
        {
            chain.Postprocessors.Remove(flush);
            chain.Postprocessors.Add(flush);
        }
    }

    public static void Execute(CreatedAggregate<T>? response, IDocumentSession session)
    {
        // Returning a null response from a conditional branch is a supported pattern, and a
        // null marker means no stream is started at all
        response?.Stream.Execute(session);
    }

    public static ValueTask<T?> FetchAsync(CreatedAggregate<T>? response, IDocumentSession session,
        CancellationToken token)
    {
        if (response == null)
        {
            return default;
        }

        var stream = response.Stream;

        // StartStream<T>.Execute commits through session.ForTenant() when a tenant id was
        // supplied, so the read has to be scoped the same way or it queries the ambient
        // tenant's event store instead.
        var events = stream.TenantId != null ? session.ForTenant(stream.TenantId).Events : session.Events;

        return stream.StreamId != Guid.Empty
            ? events.FetchLatest<T>(stream.StreamId, token)
            : events.FetchLatest<T>(stream.StreamKey, token);
    }
}
