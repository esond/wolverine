using System.Reflection;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.Core.Reflection;
using Microsoft.AspNetCore.Mvc;

namespace Wolverine.Http.CodeGen;

internal class ComplexObjectQueryStringValue : SyncFrame
{
    public ComplexObjectQueryStringValue(ParameterInfo parameter)
    {
        Variable = new QuerystringVariable(parameter.ParameterType, parameter.Name!, this);
    }

    public QuerystringVariable Variable { get; }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        var alias = Variable.VariableType.FullNameInCode();

        string GetQueryParameterName(PropertyInfo property)
        {
            return property.GetCustomAttribute<FromQueryAttribute>()?.Name ?? property.Name;
        }

        var writeableProperties = Variable.VariableType
            //.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .GetProperties()
            .Where(p => p.PropertyType.IsVisible)
            .ToList();

        var writeablePropertiesByName = writeableProperties.ToDictionary(GetQueryParameterName, v => v.PropertyType);

        // Assign variables for each writeable property
        foreach (var (propertyName, propertyType) in writeablePropertiesByName)
        {
            if (propertyType == typeof(string))
            {
                new ReadStringQueryStringValue(propertyName).GenerateCode(method, writer);
            }
            else if (propertyType == typeof(string[]))
            {
                new ParsedArrayQueryStringValue(propertyType, propertyName).GenerateCode(method, writer);
            }
            else if (propertyType.IsNullable())
            {
                var inner = propertyType.GetInnerTypeFromNullable();

                new ParsedNullableQueryStringValue(inner, propertyName).GenerateCode(method, writer);
            }
            else if (propertyType.IsArray)
            {
                new ParsedArrayQueryStringValue(propertyType, propertyName).GenerateCode(method, writer);
            }
            else if (ParsedCollectionQueryStringValue.CanParse(propertyType))
            {
                new ParsedCollectionQueryStringValue(propertyType, propertyName).GenerateCode(method, writer);
            }
            else
            {
                new ParsedQueryStringValue(propertyType, propertyName).GenerateCode(method, writer);
            }
        }

        // Set properties using an object initializer to handle required and init-only properties.
        writer.Write($"BLOCK:{alias} {Variable.Usage} = new {alias}");

        foreach (var property in writeableProperties)
        {
            writer.Write($"{property.Name} = {GetQueryParameterName(property)},");
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