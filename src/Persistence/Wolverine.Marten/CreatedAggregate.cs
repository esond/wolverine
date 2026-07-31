using System.Reflection;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
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

        var stream = streams.Length == 1 ? streams[0] : disambiguate(streams, chain);

        Configure(chain, stream);
    }

    internal static void Configure(IChain chain, Variable stream)
    {
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

    public static ValueTask<T?> FetchAsync(IStartStream stream, IEventStoreOperations events,
        CancellationToken token)
    {
        return stream.StreamId != Guid.Empty
            ? events.FetchLatest<T>(stream.StreamId, token)
            : events.FetchLatest<T>(stream.StreamKey, token);
    }
}
