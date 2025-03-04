using System.Reflection;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using Microsoft.AspNetCore.Http;
using Wolverine.Runtime;

namespace Wolverine.Http.CodeGen;

internal class CompoundParameterStrategy : IParameterStrategy
{
    public bool TryMatch(HttpChain chain, IServiceContainer container, ParameterInfo parameter, out Variable? variable)
    {
        variable = null;

        var hasAsParametersAttribute = parameter.HasAttribute<AsParametersAttribute>();
        var hasDefaultConstructor = parameter.ParameterType.HasDefaultConstructor();
        var isValidType = parameter.ParameterType.IsClass || parameter.ParameterType.IsValueType;

        if (!hasAsParametersAttribute || !hasDefaultConstructor || !isValidType)
            return false;

        var value = new CompoundParametersValue(parameter, []);
        variable = value.Variable;

        return true;
    }
}

public class CompoundVariable : Variable
{
    public CompoundVariable(Type variableType, string usage, Frame? creator) : base(variableType, usage, creator)
    {
    }
}

internal class CompoundParametersValue : SyncFrame
{
    private readonly ParameterInfo _parameter;
    private readonly List<Variable> _variables;

    public CompoundParametersValue(ParameterInfo parameter, List<Variable> variables)
    {
        _parameter = parameter;
        _variables = variables;

        Variable = new Variable(parameter.ParameterType, parameter.Name!, this);
    }

    public Variable Variable { get; }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        var alias = _parameter.ParameterType.FullNameInCode();

        foreach (var variable in _variables)
        {
            variable.Creator?.GenerateCode(method, writer);
        }

        // Set properties using an object initializer to handle required and init-only properties.
        writer.Write($"BLOCK:{alias} {_parameter.Name} = new {alias}");

        foreach (var variable in _variables)
        {
            writer.Write(
                $"{variable.Usage} = {variable.AssignmentUsage},"); // todo: double check the assignment values here and what's available
        }

        writer.FinishBlock(";"); // initializer block

        Next?.GenerateCode(method, writer);
    }
}
