using NightElf.Core;
using NightElf.Core.Modularity;
using NightElf.Database.Hosting;
using NightElf.Kernel.Consensus;
using NightElf.OS.Network;
using NightElf.Runtime.CSharp;

namespace NightElf.Launcher;

public sealed class LauncherModuleCatalog
{
    private readonly IReadOnlyList<Type> _rootModuleTypes;

    public LauncherModuleCatalog(IEnumerable<Type>? rootModuleTypes = null)
    {
        _rootModuleTypes = (rootModuleTypes ?? CreateDefaultRootModuleTypes()).Distinct().ToArray();
    }

    public IReadOnlyList<Type> ResolveLoadOrder()
    {
        var states = new Dictionary<Type, ModuleVisitState>();
        var orderedModules = new List<Type>();

        foreach (var rootModuleType in _rootModuleTypes)
        {
            Visit(rootModuleType, states, orderedModules);
        }

        return orderedModules;
    }

    public static IReadOnlyList<Type> CreateDefaultRootModuleTypes()
    {
        return
        [
            typeof(NightElfDatabaseHostingModule),
            typeof(NightElfRuntimeCSharpModule),
            typeof(NightElfOSNetworkModule),
            typeof(NightElfKernelConsensusModule)
        ];
    }

    private static void Visit(
        Type moduleType,
        IDictionary<Type, ModuleVisitState> states,
        ICollection<Type> orderedModules)
    {
        if (!typeof(NightElfModule).IsAssignableFrom(moduleType))
        {
            throw new InvalidOperationException($"Module type '{moduleType.FullName}' must derive from NightElfModule.");
        }

        if (states.TryGetValue(moduleType, out var state))
        {
            switch (state)
            {
                case ModuleVisitState.Visited:
                    return;
                case ModuleVisitState.Visiting:
                    throw new InvalidOperationException($"Circular module dependency detected on '{moduleType.FullName}'.");
            }
        }

        states[moduleType] = ModuleVisitState.Visiting;

        foreach (var dependency in moduleType
                     .GetCustomAttributes(typeof(DependsOnAttribute), inherit: false)
                     .Cast<DependsOnAttribute>()
                     .SelectMany(static attribute => attribute.Dependencies)
                     .Distinct())
        {
            Visit(dependency, states, orderedModules);
        }

        states[moduleType] = ModuleVisitState.Visited;
        orderedModules.Add(moduleType);
    }

    private enum ModuleVisitState
    {
        Visiting,
        Visited
    }
}
