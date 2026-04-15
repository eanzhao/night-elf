namespace NightElf.Core.Modularity;

public abstract class NightElfModule
{
    public virtual string Name => GetType().Name;
}
