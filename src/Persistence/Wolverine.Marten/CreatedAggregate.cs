using System.Reflection;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using Marten;
using Wolverine.Configuration;
using Wolverine.Marten.Codegen;
using Wolverine.Marten.Persistence.Sagas;
using Wolverine.Persistence;
using Wolverine.Persistence.Sagas;

namespace Wolverine.Marten;

/// <summary>
/// Use this as a response from a message handler or HTTP endpoint that starts a new
/// event stream through an <see cref="IStartStream"/> side effect to respond with the
/// newly created, projected aggregate after the transaction commits. The creation-flow
/// counterpart to <see cref="UpdatedAggregate"/>. HTTP endpoints respond with a
/// 201 status code, and optionally the Location response header when the Url is supplied.
/// The aggregate type is resolved from the accompanying StartStream&lt;T&gt; return value,
/// so this non-generic version requires one of the MartenOps.StartStream&lt;T&gt;(Guid streamId, ...)
/// overloads. Use <see cref="CreatedAggregate{T}"/> with the other overloads.
/// </summary>
public class CreatedAggregate : IResponseAware, ICreationAware
{
    public CreatedAggregate()
    {
    }

    /// <param name="url">Value for the HTTP Location response header, e.g. the URL of the newly created resource</param>
    public CreatedAggregate(string url)
    {
        Url = url;
    }

    public string? Url { get; }

    public static void ConfigureResponse(IChain chain)
    {
        var streams = chain.ReturnVariablesOfType<IStartStream>().ToArray();
        if (streams.Length == 0)
        {
            throw new InvalidOperationException(
                $"{nameof(CreatedAggregate)} cannot be used because Chain {chain} does not also return an {nameof(IStartStream)} value. Return one with MartenOps.StartStream<T>()");
        }

        if (streams.Length > 1)
        {
            throw new InvalidOperationException(
                $"{nameof(CreatedAggregate)} cannot be used because Chain {chain} returns multiple {nameof(IStartStream)} values. Use the generic CreatedAggregate<T> to tell Wolverine which stream's aggregate is the response.");
        }

        var stream = streams[0];
        if (!stream.VariableType.IsGenericType ||
            stream.VariableType.GetGenericTypeDefinition() != typeof(StartStream<>))
        {
            throw new InvalidOperationException(
                $"{nameof(CreatedAggregate)} cannot determine the aggregate type from the {nameof(IStartStream)} return value of Chain {chain}. Either use one of the MartenOps.StartStream<T>(Guid streamId, ...) overloads that return StartStream<T>, or use the generic CreatedAggregate<T> instead.");
        }

        var aggregateType = stream.VariableType.GetGenericArguments()[0];
        var configure = typeof(CreatedAggregate<>).MakeGenericType(aggregateType)
            .GetMethod(nameof(CreatedAggregate<object>.Configure), BindingFlags.NonPublic | BindingFlags.Static)!;

        configure.Invoke(null, [chain, stream]);
    }
}

/// <summary>
/// Use this as a response from a message handler or HTTP endpoint that starts a new
/// event stream through an <see cref="IStartStream"/> side effect to respond with the
/// newly created, projected aggregate after the transaction commits. The creation-flow
/// counterpart to <see cref="UpdatedAggregate"/>. HTTP endpoints respond with a
/// 201 status code, and optionally the Location response header when the Url is supplied.
/// </summary>
/// <typeparam name="T">The aggregate type started by the accompanying <see cref="IStartStream"/> return value</typeparam>
public class CreatedAggregate<T> : IResponseAware, ICreationAware where T : class
{
    public CreatedAggregate()
    {
    }

    /// <param name="url">Value for the HTTP Location response header, e.g. the URL of the newly created resource</param>
    public CreatedAggregate(string url)
    {
        Url = url;
    }

    public string? Url { get; }

    public static void ConfigureResponse(IChain chain)
    {
        var streams = chain.ReturnVariablesOfType<IStartStream>().ToArray();
        if (streams.Length == 0)
        {
            throw new InvalidOperationException(
                $"CreatedAggregate<{typeof(T).Name}> cannot be used because Chain {chain} does not also return an {nameof(IStartStream)} value. Return one with MartenOps.StartStream<{typeof(T).Name}>()");
        }

        var stream = streams.Length == 1 ? assertMatchingType(streams[0], chain) : disambiguate(streams, chain);

        Configure(chain, stream);
    }

    internal static void Configure(IChain chain, Variable stream)
    {
        // MartenOpPolicy already routes message handler chains that return a single IMartenOp
        // through MartenPersistenceFrameProvider.ApplyTransactionSupport, but Wolverine.Http has
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

        var call = new MethodCall(typeof(CreatedAggregate<T>),
            typeof(CreatedAggregate<T>).GetMethod(nameof(FetchAsync))!)
        {
            Arguments =
            {
                [0] = stream
            }
        };

        chain.UseForResponse(call);

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

    private static Variable assertMatchingType(Variable stream, IChain chain)
    {
        // Only the MartenOps.StartStream<T>(Guid streamId, ...) overloads declare the concrete
        // StartStream<T> return type. The no-id and string-key overloads declare plain
        // IStartStream, so there is nothing to match against for those.
        if (!stream.VariableType.IsGenericType ||
            stream.VariableType.GetGenericTypeDefinition() != typeof(StartStream<>))
        {
            return stream;
        }

        var aggregateType = stream.VariableType.GetGenericArguments()[0];
        if (aggregateType != typeof(T))
        {
            throw new InvalidOperationException(
                $"CreatedAggregate<{typeof(T).Name}> cannot be used because Chain {chain} returns StartStream<{aggregateType.Name}>, which starts a different aggregate type. Use CreatedAggregate<{aggregateType.Name}> instead.");
        }

        return stream;
    }

    private static Variable disambiguate(Variable[] streams, IChain chain)
    {
        // Only the MartenOps.StartStream<T>(Guid streamId, ...) overloads declare the
        // concrete StartStream<T> return type, so type matching is only possible there.
        // The no-id and string-key overloads declare plain IStartStream.
        var matching = streams.Where(x =>
            x.VariableType.IsGenericType &&
            x.VariableType.GetGenericTypeDefinition() == typeof(StartStream<>) &&
            x.VariableType.GetGenericArguments()[0] == typeof(T)).ToArray();

        if (matching.Length == 1)
        {
            return matching[0];
        }

        throw new InvalidOperationException(
            $"CreatedAggregate<{typeof(T).Name}> cannot be used because Chain {chain} returns multiple {nameof(IStartStream)} values and Wolverine cannot determine which one starts the {typeof(T).Name} stream. Declare exactly one of them as StartStream<{typeof(T).Name}> with the MartenOps.StartStream<{typeof(T).Name}>(Guid streamId, ...) overload so it can be matched by type.");
    }

    public static ValueTask<T?> FetchAsync(IStartStream? stream, IDocumentSession session,
        CancellationToken token)
    {
        // Returning a null side effect from a conditional branch is a supported pattern -
        // Wolverine's SideEffectPolicy guards the generated Execute() call - so the response
        // fetch has to tolerate it too.
        if (stream == null)
        {
            return default;
        }

        // StartStream<T>.Execute commits through session.ForTenant() when a tenant id was
        // supplied, so the read has to be scoped the same way or it queries the ambient
        // tenant's event store instead.
        var tenantId = (stream as StartStream<T>)?.TenantId;
        var events = tenantId != null ? session.ForTenant(tenantId).Events : session.Events;

        return stream.StreamId != Guid.Empty
            ? events.FetchLatest<T>(stream.StreamId, token)
            : events.FetchLatest<T>(stream.StreamKey, token);
    }
}
