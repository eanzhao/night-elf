using NightElf.Core.Modularity;

namespace NightElf.Database.Redis;

[DependsOn(typeof(NightElfDatabaseModule))]
public sealed class NightElfDatabaseRedisModule : NightElfModule
{
}
