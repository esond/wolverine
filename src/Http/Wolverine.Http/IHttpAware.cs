using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Wolverine.Http.Resources;
using Wolverine.Runtime;

namespace Wolverine.Http;

/// <summary>
/// Interface for resource types in Wolverine.Http that need to modify
/// how the HTTP response is formatted. Use this for additional headers
/// or customized status codes
/// </summary>
public interface IHttpAware : IEndpointMetadataProvider
{
    void Apply(HttpContext context);
}

internal class HttpAwarePolicy : IHttpPolicy
{
    public void Apply(IReadOnlyList<HttpChain> chains, GenerationRules rules, IServiceContainer container)
    {
        var matching = chains.Where(x => x.ResourceType != null && x.ResourceType.CanBeCastTo(typeof(IHttpAware)));
        foreach (var chain in matching)
        {
            var resource = chain.Method.Creates.FirstOrDefault(x => x.VariableType == chain.ResourceType);
            if (resource == null)
            {
                // continue, not return: a chain whose resource is exposed through a derived or
                // interface variable has no exact type match here, and abandoning the loop would
                // silently drop the 201/202 handling from every later chain in the list.
                continue;
            }

            var apply = new ApplyHttpAware(resource);

            // This will have to run before any kind of resource writing
            chain.Postprocessors.Insert(0, apply);
        }
    }
}

// Shared shape for the postprocessor frames that hand a response marker to an HttpHandler helper
// alongside the HttpContext. The subclasses differ only in which helper they call and what comment
// they emit, so the HttpContext lookup lives here and stays in one place.
internal abstract class ApplyResponseMarkerFrame : SyncFrame
{
    private readonly Variable _target;
    private readonly string _comment;
    private readonly string _methodName;
    private Variable? _httpContext;

    protected ApplyResponseMarkerFrame(Variable target, string comment, string methodName)
    {
        _target = target;
        _comment = comment;
        _methodName = methodName;
        uses.Add(target);
    }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _httpContext = chain.FindVariable(typeof(HttpContext));
        yield return _httpContext;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.WriteComment(_comment);
        writer.Write($"{_methodName}({_target.Usage}, {_httpContext!.Usage});");
        Next?.GenerateCode(method, writer);
    }
}

internal class ApplyHttpAware : ApplyResponseMarkerFrame
{
    public ApplyHttpAware(Variable target) : base(target,
        "This response type customizes the HTTP response",
        nameof(HttpHandler.ApplyHttpAware))
    {
    }
}

internal class CreationAwarePolicy : IHttpPolicy
{
    public void Apply(IReadOnlyList<HttpChain> chains, GenerationRules rules, IServiceContainer container)
    {
        // IHttpAware resource types (CreationResponse et al) already control their own
        // status code and metadata through HttpAwarePolicy
        var matching = chains.Where(x =>
            x.ResourceType != null && !x.ResourceType.CanBeCastTo(typeof(IHttpAware)));

        foreach (var chain in matching)
        {
            var marker = chain.Method.Creates.FirstOrDefault(x => x.VariableType.CanBeCastTo(typeof(ICreationAware)));
            if (marker == null)
            {
                continue;
            }

            var resourceType = chain.ResourceType;

            // Finally() rather than Add(): BuildEndpoint calls establishResourceTypeMetadata first,
            // which appends Wolverine's built-in Produces(200) convention to the same list this
            // policy already registered into. A conventional Add() would run before that append and
            // remove nothing, leaving the endpoint advertising both 200 and 201.
            chain.Finally(builder =>
            {
                builder.RemoveStatusCodeResponse(200);
                builder.Metadata.Add(new WolverineProducesResponseTypeMetadata
                    { Type = resourceType, StatusCode = 201 });
            });

            // This will have to run before any kind of resource writing
            chain.Postprocessors.Insert(0, new ApplyCreationAware(marker));
        }
    }
}

internal class ApplyCreationAware : ApplyResponseMarkerFrame
{
    public ApplyCreationAware(Variable target) : base(target,
        "This response type denotes resource creation",
        nameof(HttpHandler.ApplyCreationAware))
    {
    }
}

public static class EndpointBuilderExtensions
{
    public static EndpointBuilder RemoveStatusCodeResponse(this EndpointBuilder builder, int statusCode)
    {
        builder.Metadata.RemoveAll(x => x is IProducesResponseTypeMetadata m && m.StatusCode == statusCode);
        return builder;
    }
}

#region sample_creationresponse
/// <summary>
/// Base class for resource types that denote some kind of resource being created
/// in the system. Wolverine specific, and more efficient, version of Created<T> from ASP.Net Core
/// </summary>
public record CreationResponse([StringSyntax("Route")]string Url) : IHttpAware
{
    public static void PopulateMetadata(MethodInfo method, EndpointBuilder builder)
    {
        builder.RemoveStatusCodeResponse(200);

        var create = new MethodCall(method.DeclaringType!, method).Creates.FirstOrDefault()?.VariableType;
        var metadata = new WolverineProducesResponseTypeMetadata { Type = create, StatusCode = 201 };
        builder.Metadata.Add(metadata);
    }

    void IHttpAware.Apply(HttpContext context)
    {
        context.Response.Headers.Location = Url;
        context.Response.StatusCode = 201;
    }

    public static CreationResponse<T> For<T>(T value, string url) => new CreationResponse<T>(url, value);
}

#endregion

public record CreationResponse<T>(string Url, T Value) : CreationResponse(Url);


#region sample_acceptresponse
/// <summary>
/// Base class for resource types that denote some kind of request being accepted in the system.
/// </summary>
public record AcceptResponse(string Url) : IHttpAware
{
    public static void PopulateMetadata(MethodInfo method, EndpointBuilder builder)
    {
        builder.RemoveStatusCodeResponse(200);

        var create = new MethodCall(method.DeclaringType!, method).Creates.FirstOrDefault()?.VariableType;
        var metadata = new WolverineProducesResponseTypeMetadata { Type = create, StatusCode = 202 };
        builder.Metadata.Add(metadata);
    }

    void IHttpAware.Apply(HttpContext context)
    {
        context.Response.Headers.Location = Url;
        context.Response.StatusCode = 202;
    }

    public static AcceptResponse<T> For<T>(T value, string url) => new AcceptResponse<T>(url, value);
}

#endregion

public record AcceptResponse<T>(string Url, T Value) : AcceptResponse(Url);