using System.Diagnostics.CodeAnalysis;

namespace NightElf.Core.Modularity;

[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public sealed class DependsOnAttribute : Attribute
{
    public DependsOnAttribute([NotNull] params Type[] dependencies)
    {
        Dependencies = dependencies ?? throw new ArgumentNullException(nameof(dependencies));
    }

    public IReadOnlyList<Type> Dependencies { get; }
}
