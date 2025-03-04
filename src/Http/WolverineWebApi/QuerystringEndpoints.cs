using JasperFx.Core;
using Marten;
using Microsoft.AspNetCore.Mvc;
using Wolverine.Http;

namespace WolverineWebApi;

public static class QuerystringEndpoints
{

    [WolverineGet("/querystring/enum")]
    public static string UsingEnumQuerystring(Direction direction)
    {
        return direction.ToString();
    }

    [WolverineGet("/querystring/explicit")]
    public static string UsingEnumQuerystring([FromQuery(Name = "name")]string value)
    {
        return value ?? "";
    }

    [WolverineGet("/querystring/enum/nullable")]
    public static string UsingNullableEnumQuerystring(Direction? direction)
    {
        return direction?.ToString() ?? "none";
    }

    [WolverineGet("/querystring/stringarray")]
    public static string StringArray(string[]? values)
    {
        if (values == null || values.IsEmpty()) return "none";

        return values.Join(",");
    }

    [WolverineGet("/querystring/intarray")]
    public static string IntArray(int[]? values)
    {
        if (values == null || values.IsEmpty()) return "none";

        return values.OrderBy(x => x).Select(x => x.ToString()).Join(",");
    }

    #region sample_query_string_object

    [WolverineGet("/querystring/object")]
    public static DataRequest Object([FromQuery] DataRequest request)
    {
        return new DataRequest
        {
            PageNumber = request.PageNumber,
            PageSize = request.PageSize,
            Filters = request.Filters,
            OrderBy = request.OrderBy,
            SortDirection = request.SortDirection,
        };

    }

    #endregion
}

public record DataRequest
{
    public required int PageNumber { get; set; }

    public int PageSize { get; init ; }

    public string? OrderBy { get; set; }

    public string[] Filters { get; set; } = [];

    [FromQuery(Name = "dir")]
    public SortDirection SortDirection { get; set; }
}

public enum SortDirection
{
    Ascending,
    Descending
}