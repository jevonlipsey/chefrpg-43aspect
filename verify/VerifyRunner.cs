using System;
using Mono.Cecil;

namespace ChefRPG43.Verify;

/// runs the three verifier layers in order against an open module. shared by the
/// standalone harness (verify/Program.cs) and the patcher, which gates its output on it.
public static class VerifyRunner
{
    // helperPayload is the SafeHelper.dll the patcher embedded; see SemanticVerifier.
    public static void Run(Checks checks, ModuleDefinition module, VerifyMode mode, byte[]? helperPayload)
    {
        new IlVerifier(checks, module, mode).Run();
        new SemanticVerifier(checks, module, helperPayload).Run();
        new Simulator(checks).Run();
    }
}
