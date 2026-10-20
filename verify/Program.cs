using Mono.Cecil;
using ChefRPG43.Verify;

// loads the patched dll, runs the il, semantic, and numeric verifiers, then exits
// non-zero if anything failed so a deploy step can gate on this harness.
var dllPath = args.FirstOrDefault(a => !a.StartsWith('-')) ?? "Assembly-CSharp-43.dll";
string full = Path.GetFullPath(dllPath);
if (!File.Exists(full))
{
    Console.Error.WriteLine($"verify: missing dll '{full}'");
    return 2;
}

string UNITY_MANAGED = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
    "Library/Application Support/Steam/steamapps/common/Chef RPG/ChefRPG.app/Contents/Resources/Data/Managed");

var resolver = new DefaultAssemblyResolver();
if (Directory.Exists(UNITY_MANAGED)) resolver.AddSearchDirectory(UNITY_MANAGED);
resolver.AddSearchDirectory("original");
resolver.AddSearchDirectory(Path.GetDirectoryName(full)!);

// windows-only refs (facepunch win64) so the droiddeck build resolves too.
// added last so it can never shadow a mac managed assembly with the same name.
const string WIN_REFS = "verify/remote/win-refs";
if (Directory.Exists(WIN_REFS)) resolver.AddSearchDirectory(WIN_REFS);

var checks = new Checks();
Console.WriteLine($"chefrpg 43-aspect verification: {Path.GetFileName(full)}");
Console.WriteLine($"  sha256 {Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(full))) }");

using var module = ModuleDefinition.ReadModule(full, new ReaderParameters { AssemblyResolver = resolver });

// the helper payload is read from disk here so a stale embed in the patcher still fails
const string HELPER_PAYLOAD = "patcher/safehelper/SafeHelper.dll";
byte[]? helperPayload = File.Exists(HELPER_PAYLOAD) ? File.ReadAllBytes(HELPER_PAYLOAD) : null;
if (helperPayload == null)
    Console.Error.WriteLine($"verify: {HELPER_PAYLOAD} not found (run from the repo root)");

VerifyRunner.Run(checks, module, VerifyMode.Full, helperPayload);

checks.Summarize();
return checks.Failed == 0 ? 0 : 1;
