# Sentinel Consensus: NightElf 共识机制

## 定位

NightElf 不是通用 L1 公链。它是 **AI Agent 可验证执行层**——为 AI agent 的推理调用、token 消耗、会话结算提供链上确定性记录。共识机制应当反映这一核心用途。

传统 DPoS 以经济质押为唯一准入门槛。Sentinel Consensus 在质押之上引入两个维度：

1. **计算贡献（Computation Credits）**——节点为 agent session 提供了多少有效推理服务
2. **计量诚实度（Reputation Score）**——节点的 metering attestation 历史是否可信

## 核心概念

### Sentinel（哨兵）

NightElf 的验证节点不叫 validator 或 miner，叫 **Sentinel**——Night Elf 的守望者。

成为 Sentinel 需要：
- 注册并提供最低质押（`min_sentinel_stake`）
- 声明节点网络端点（host + port）
- 从此刻起参与 agent session 的推理服务和 metering 验证

### Computation Credits（计算积分）

每当一个 Sentinel 为某个 agent session 提供了推理服务（通过 `RecordComputationCredit` 记录），它的计算积分增加。积分量等于该 session 步骤中的 `weighted_tokens_served`——即经过置信度加权的 token 消耗量。

这意味着：
- 服务更多 session 的节点积累更多积分
- 使用 `Verified` metering source 的服务权重高于 `SelfReported`
- 积分在每个 epoch 结束时重置

### Reputation Score（声誉分）

Reputation 是长期指标，跨 epoch 累计：

| 事件 | 声誉变化 |
|------|----------|
| HONEST attestation | `+weighted_tokens_served` |
| CHALLENGED but vindicated | `+weighted_tokens_served / 2` |
| PENALIZED (dishonest) | `-weighted_tokens_served * 2` |

声誉分决定了节点在 epoch 选举中的长期权重。一个长期诚实但本 epoch 计算量较小的节点，仍可能入选 active sentinel set。

### Epoch（纪元）

每个 epoch 由 `AdvanceEpoch` 调用触发（通常由出块节点在特定高度自动触发）。

选举逻辑：
```
score = reputation_score * 0.7 + computation_credits * 0.3
```

取 score 最高的 `active_sentinel_count` 个节点作为本 epoch 的 active sentinel set。Active sentinels 参与 AEDPoS 轮转出块。

每个 epoch 结束时：
- 记录 `EpochSnapshot`（参与者、总积分、起始高度）
- 重置所有 sentinel 的 `computation_credits`
- 保留 `reputation_score`

### Governance Parameters（治理参数）

管理员（通过虚拟地址授权）可以调整：

| 参数 | 默认值 | 说明 |
|------|--------|------|
| `max_session_token_budget` | 10,000,000 | 单个 agent session 的最大 token 预算 |
| `max_session_duration_blocks` | 1000 | session 最大存活区块数 |
| `min_sentinel_stake` | 100,000 | 成为 sentinel 的最低质押 |
| `active_sentinel_count` | 21 | 每 epoch 的活跃 sentinel 数量 |
| `metering_challenge_window_blocks` | 100 | metering 质疑窗口期 |

## 与现有系统的关系

```
┌─────────────────────────────────────────────────┐
│                  SentinelRegistry                │
│  sentinel 注册 / 计算积分 / 声誉 / epoch 选举     │
└──────────┬──────────────────┬────────────────────┘
           │                  │
           ▼                  ▼
┌──────────────────┐  ┌──────────────────────┐
│  IConsensusEngine │  │  AgentSession        │
│  (AEDPoS v2)     │  │  Contract            │
│                  │  │                      │
│  active sentinel │  │  RecordStep 产生     │
│  set 来自 epoch  │  │  weighted tokens →   │
│  snapshot        │  │  RecordComputationCredit│
└──────────────────┘  └──────────────────────┘
```

- **AgentSession.RecordStep** 产生 token metering 数据
- **SentinelRegistry.RecordComputationCredit** 将这些数据转化为 sentinel 的计算积分和声誉
- **SentinelRegistry.AdvanceEpoch** 选出下一轮 active sentinels
- **IConsensusEngine** 使用 active sentinel set 进行 AEDPoS 轮转出块

## 合约接口

### 写操作

| 方法 | 说明 |
|------|------|
| `RegisterSentinel` | 注册新 sentinel，质押 + 端点声明 |
| `ExitSentinel` | 退出 sentinel，返还质押 |
| `RecordComputationCredit` | 记录 sentinel 的计算贡献和 attestation 结果 |
| `AdvanceEpoch` | 推进 epoch，选举新一轮 active sentinels |
| `UpdateGovernance` | 管理员更新治理参数 |

### 读操作

| 方法 | 说明 |
|------|------|
| `GetSentinel` | 查询 sentinel 状态 |
| `GetCurrentEpoch` | 查询当前 epoch 快照 |
| `GetGovernanceParameters` | 查询治理参数 |

## 为什么不用纯 DPoS

NightElf 的价值不在于 "谁有更多 token"，而在于 "谁为 AI agent 提供了更多可信的计算服务"。Sentinel Consensus 将区块生产权与实际计算贡献绑定，使得：

1. 空转节点（有质押但不服务 agent session）在 epoch 选举中被边缘化
2. 诚实 metering 的节点获得长期优势（reputation 跨 epoch 累计）
3. 作弊节点被惩罚后需要大量诚实工作才能恢复（`-2x` 惩罚 vs `+1x` 奖励）
4. 治理参数与 AI agent 使用场景直接相关（session 预算、metering 窗口期）
