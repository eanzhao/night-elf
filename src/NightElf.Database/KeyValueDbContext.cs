namespace NightElf.Database;

public abstract class KeyValueDbContext<TContext>
    where TContext : KeyValueDbContext<TContext>
{
    public static string Name => typeof(TContext).Name;
}
