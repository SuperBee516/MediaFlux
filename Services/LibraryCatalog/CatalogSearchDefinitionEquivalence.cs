using System.Text.Json;

namespace MediaFlux.Services.LibraryCatalog;

internal static class CatalogSearchDefinitionEquivalence
{
    internal static bool AreEquivalent(CatalogSearchDefinition left, CatalogSearchDefinition right)
    {
        if (left.Version != right.Version) return false;
        CatalogSearchDefinition leftBase = left with { Conditions = Array.Empty<CatalogSearchCondition>() };
        CatalogSearchDefinition rightBase = right with { Conditions = Array.Empty<CatalogSearchCondition>() };
        CompiledCatalogSearch leftCompiled = CatalogSearchSqlCompiler.Compile(leftBase);
        CompiledCatalogSearch rightCompiled = CatalogSearchSqlCompiler.Compile(rightBase);
        if (CompiledSignature(leftCompiled) != CompiledSignature(rightCompiled)) return false;

        string[] leftConditions = left.Conditions.Select(ConditionSignature).OrderBy(value => value, StringComparer.Ordinal).ToArray();
        string[] rightConditions = right.Conditions.Select(ConditionSignature).OrderBy(value => value, StringComparer.Ordinal).ToArray();
        return leftConditions.SequenceEqual(rightConditions, StringComparer.Ordinal);

        static string ConditionSignature(CatalogSearchCondition condition)
        {
            CatalogSearchDefinition onlyCondition = new(CatalogSearchDefinition.CurrentVersion,
                new[] { condition });
            return CompiledSignature(CatalogSearchSqlCompiler.Compile(onlyCondition));
        }

        static string CompiledSignature(CompiledCatalogSearch compiled) => JsonSerializer.Serialize(new
        {
            compiled.PredicateSql,
            Parameters = compiled.Parameters.Select(parameter => new
            {
                parameter.Name,
                Type = parameter.Value.GetType().FullName,
                Value = JsonSerializer.Serialize(parameter.Value)
            }),
            compiled.OrderExpression,
            compiled.Descending
        });
    }
}
