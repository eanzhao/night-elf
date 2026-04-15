using NightElf.Core.Modularity;
using NightElf.Database.Redis;
using NightElf.Database.Tsavorite;

namespace NightElf.Database.Hosting;

[DependsOn(typeof(NightElfDatabaseRedisModule), typeof(NightElfDatabaseTsavoriteModule))]
public sealed class NightElfDatabaseHostingModule : NightElfModule
{
}
