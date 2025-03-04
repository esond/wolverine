using System.Reflection;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.Core.Reflection;
using Microsoft.AspNetCore.Mvc;

namespace Wolverine.Http.CodeGen;

internal class CompoundQueryStringValue : SyncFrame
{
    private readonly ParameterInfo _parameter;
    private readonly List<QuerystringVariable> _querystringVariables;

    public CompoundQueryStringValue(ParameterInfo parameter, List<QuerystringVariable> querystringVariables)
    {
        _parameter = parameter;
        _querystringVariables = querystringVariables;

        Variable = new QuerystringVariable(parameter.ParameterType, parameter.Name!, this);
    }

    public QuerystringVariable Variable { get; }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        var alias = _parameter.ParameterType.FullNameInCode();

        string GetQueryParameterName(PropertyInfo property)
        {
            return property.GetCustomAttribute<FromQueryAttribute>()?.Name ?? property.Name;
        }

        var writeableProperties = _parameter.ParameterType
            //.GetProperties(BindingFlags.Public) // todo: gotta be a more precise way
            .GetProperties()
            .Where(p => p.PropertyType.IsVisible)
            .ToList();

        foreach (var variable in _querystringVariables)
        {
            variable.Creator?.GenerateCode(method, writer);
        }

        // Set properties using an object initializer to handle required and init-only properties.
        writer.Write($"BLOCK:{alias} {_parameter.Name} = new {alias}");

        foreach (var property in writeableProperties)
        {
            writer.Write($"{property.Name} = {GetQueryParameterName(property)},"); // todo: this depends on behaviour of other query string variables
        }

        writer.FinishBlock(";"); // initializer block

        Next?.GenerateCode(method, writer);
    }

    public static bool CanParse(Type argType)
    {
        // Handle reference types (classes and records) and structs that aren't enums.
        return (argType.IsClass || argType.IsValueType) && argType.HasDefaultConstructor();
    }
}