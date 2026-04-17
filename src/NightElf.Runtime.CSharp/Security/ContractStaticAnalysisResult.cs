using System;
using System.Collections.Generic;

namespace NightElf.Runtime.CSharp.Security;

public sealed class ContractStaticAnalysisResult
{
    public ContractStaticAnalysisResult(IReadOnlyList<ContractStaticAnalysisViolation> violations)
    {
        Violations = violations ?? throw new ArgumentNullException(nameof(violations));
    }

    public IReadOnlyList<ContractStaticAnalysisViolation> Violations { get; }

    public bool Succeeded => Violations.Count == 0;

    public void ThrowIfFailed()
    {
        if (!Succeeded)
        {
            throw new ContractAssemblyStaticAnalysisException(this);
        }
    }
}

public sealed class ContractStaticAnalysisViolation
{
    public required string RuleId { get; init; }

    public required string Message { get; init; }
}
