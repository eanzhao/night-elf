# Genesis Block 生成

## 目标

issue [#28](https://github.com/eanzhao/night-elf/issues/28) 要求把 NightElf 的 genesis 初始化从“最小占位”补成可配置、可重放、可区分链身份的真实入口。

## 本次落地

入口仍在：
- [GenesisBlockService.cs](/Users/eanzhao/night-elf/src/NightElf.Launcher/GenesisBlockService.cs)

配置模型：
- [LauncherOptions.cs](/Users/eanzhao/night-elf/src/NightElf.Launcher/LauncherOptions.cs)
- [GenesisModels.cs](/Users/eanzhao/night-elf/src/NightElf.Launcher/GenesisModels.cs)

序列化上下文：
- [GenesisJsonSerializerContext.cs](/Users/eanzhao/night-elf/src/NightElf.Launcher/GenesisJsonSerializerContext.cs)

## Genesis 配置来源

`GenesisConfig` 现在支持两种来源：
- 直接来自 `appsettings` / environment variables
- 通过 `NightElf:Launcher:Genesis:ConfigFilePath` 指向独立 JSON 文件

解析策略是：
- 先读取 JSON 文件
- 再用当前配置源里的显式字段覆盖

这样做的目的：
- 部署时可以把 genesis 当作独立工件管理
- 仍然允许用环境变量覆写个别字段

## 系统合约部署交易

genesis block 现在不再只是空 block。

对于 `Genesis.SystemContracts` 中的每个合约：
- 会生成一笔 `DeploySystemContract` 交易
- `from` 使用确定性 Ed25519 deployer key
- `to` 使用确定性系统合约地址
- `params` 写入结构化部署 payload
- 交易 ID 会进入 genesis block body

当前默认仍然至少包含：
- `AgentSession`

## 状态写入

除了原有：
- `genesis:block-hash`
- `genesis:chain-id`
- `genesis:validators`
- `genesis:system-contracts`
- `genesis:config`

现在还会为每个系统合约写入：
- `system-contract:<name>`
- `system-contract:<name>:deployment`

`deployment` 记录里包含：
- 合约名
- 合约地址
- 部署交易 ID
- deployer 公钥
- code hash
- genesis block height/hash
- 部署时间

## 链身份

genesis 的 canonical block hash 现在按最终 block bytes 计算，而不是直接沿用共识提案里的临时 hash。

这意味着：
- 不同 `chainId`
- 不同 validator 集
- 不同 system contract 列表
- 不同 genesis 时间戳

都会导致不同的 genesis block hash。

这正是 NightElf 多租户隔离的链身份根。

## 验证

[GenesisBlockServiceTests.cs](/Users/eanzhao/night-elf/test/NightElf.Launcher.Tests/GenesisBlockServiceTests.cs) 现在覆盖：
- 首次创建 vs 重启跳过
- deployment transaction 写入 block body
- deployment record 写入 state store
- JSON genesis 文件加载与显式覆盖
- 不同 genesis 配置产生不同 block hash
