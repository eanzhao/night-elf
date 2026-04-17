using System;
using System.Linq;

namespace NightElf.Runtime.CSharp.Security;

public sealed class ContractAssemblyStaticAnalysisException : InvalidOperationException
{
    public ContractAssemblyStaticAnalysisException(ContractStaticAnalysisResult result)
        : base(string.Join(Environment.NewLine, result.Violations.Select(static violation => violation.Message)))
    {
        Result = result ?? throw new ArgumentNullException(nameof(result));
    }

    public ContractStaticAnalysisResult Result { get; }
}
