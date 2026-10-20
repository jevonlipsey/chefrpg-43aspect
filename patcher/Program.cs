using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ChefRPG43.Verify;
using Mono.Cecil;
using Mono.Cecil.Cil;

class Program
{
    // the mac steam install's Managed folder, used by the zero-argument dev default
    static readonly string DEV_MANAGED = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Library/Application Support/Steam/steamapps/common/Chef RPG/ChefRPG.app/Contents/Resources/Data/Managed");

    // built by hand in BuildCoreRefs (no UnityEngine.CoreModule.dll needed), then reset per run
    static MethodReference debugLogRef;
    static MethodReference displayGetMain;
    static MethodReference displayGetSysW;
    static MethodReference displayGetSysH;
    static MethodReference screenGetFullScreenRef;
    static TypeReference v2TypeRef;
    static FieldReference v2FieldX;
    static FieldReference v2FieldY;

    // FullScreenMode.FullScreenWindow, a constant of the unity enum (no reference to it is emitted)
    const int FullScreenWindowValue = 1;

    // resolved against the target module for the on-disk diagnostic
    static MethodReference diagAppPath;
    static MethodReference diagConcatSS;
    static MethodReference diagConcatOO;
    static MethodReference diagAppendAllText;

    // live camera numbers, used by the runtime dump
    static MethodReference objGetName;
    static MethodReference camGetPixelRect;
    static MethodReference camGetOrthoSize;
    static MethodReference camGetTargetTexture;
    static MethodReference camGetDepth;
    static MethodReference rectGetX, rectGetY, rectGetWidth, rectGetHeight;
    static MethodReference timeGetFrameCount;
    static MethodReference mathfRoundToInt;

    // the patcher keeps its working refs in statics. the browser host calls PatchApi.Patch
    // repeatedly in one process, so nothing from a previous module may leak into the next.
    static void ResetState()
    {
        debugLogRef = displayGetMain = displayGetSysW = displayGetSysH = screenGetFullScreenRef = null;
        v2TypeRef = null;
        v2FieldX = v2FieldY = null;
        diagAppPath = diagConcatSS = diagConcatOO = diagAppendAllText = null;
        objGetName = camGetPixelRect = camGetOrthoSize = camGetTargetTexture = camGetDepth = null;
        rectGetX = rectGetY = rectGetWidth = rectGetHeight = null;
        timeGetFrameCount = mathfRoundToInt = null;
    }

    // exit codes: 0 patched and verified (output written), 1 verify failed or any other
    // error (no output), 3 the input is already patched, 4 the input is not Chef RPG's
    // Assembly-CSharp.dll (or not a .net assembly at all).
    //
    // usage:
    //   patcher                                  dev default (original/ -> Assembly-CSharp-43.dll)
    //   patcher <ManagedDir>                     <ManagedDir>/Assembly-CSharp.dll -> Assembly-CSharp-43.dll beside it
    //   patcher <inputDll>                       no unity refs: reference-free verify, output beside the input
    //   patcher <inputDll> <ManagedDir> [out]    full verify against the unity refs
    //   patcher <inputDll> <out>                 reference-free verify (second arg is not a directory)
    //   patcher --verify <dll>                   diagnostic: list SafeHelper references
    // extra: --refs <dir> adds a resolver directory to the full verify (repeatable)
    //
    // a windows user can drag Assembly-CSharp.dll (or the Managed folder) onto patcher.exe; that is
    // the one-argument form above. explorer gives such a launch its own console window that closes
    // when the process exits, so Main waits for Enter first (see WaitIfOwnConsole).
    static int Main(string[] args)
    {
        int code = Run(args);
        WaitIfOwnConsole();
        return code;
    }

    // kernel32 is only touched from WaitIfOwnConsole (windows, from Main). the browser build links
    // this file too but enters through WasmEntry, so it compiles the declaration and never calls it.
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    static extern uint GetConsoleProcessList(uint[] processList, uint processCount);

    // true when this process is the only one attached to its console: launched from explorer by drag
    // or double-click rather than from cmd/powershell (which also attach), patch.py or ci.
    static void WaitIfOwnConsole()
    {
        try
        {
            if (!OperatingSystem.IsWindows() || Console.IsInputRedirected) return;
            if (GetConsoleProcessList(new uint[2], 2) != 1) return;
            Console.WriteLine();
            Console.WriteLine("Press Enter to close this window.");
            Console.ReadLine();
        }
        catch (Exception)
        {
            // no console, or kernel32 unavailable: just exit
        }
    }

    static void PrintUsage()
    {
        Console.WriteLine();
        Console.WriteLine("Drag Assembly-CSharp.dll (or the whole Managed folder) onto patcher.exe.");
        Console.WriteLine("For Chef RPG on Windows it is in:  Chef RPG\\Chef RPG_Data\\Managed");
        Console.WriteLine();
        Console.WriteLine("From a terminal:");
        Console.WriteLine("  patcher <ManagedDir>                    full verify, writes Assembly-CSharp-43.dll in that folder");
        Console.WriteLine("  patcher <Assembly-CSharp.dll>           writes Assembly-CSharp-43.dll beside it");
        Console.WriteLine("  patcher <inputDll> <ManagedDir> [out]   full verify against the Unity refs");
    }

    static void PrintNextSteps(string output)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(output));
        Console.WriteLine();
        Console.WriteLine("Patched and verified. Your original was not changed. Next steps:");
        Console.WriteLine($"  1. Open the folder {dir}");
        Console.WriteLine("  2. Back up the original: rename Assembly-CSharp.dll to Assembly-CSharp.dll.bak");
        Console.WriteLine("  3. Rename Assembly-CSharp-43.dll to Assembly-CSharp.dll");
        Console.WriteLine("  To undo, put the .bak back, or use Steam: Verify integrity of game files.");
    }

    static void PrintFailure(string reason)
    {
        Console.WriteLine();
        switch (reason)
        {
            case "already_patched":
                Console.WriteLine("This Assembly-CSharp.dll is already patched, so nothing was written.");
                Console.WriteLine("Restore Assembly-CSharp.dll.bak, or use Steam (Chef RPG > Properties > Installed Files >");
                Console.WriteLine("Verify integrity of game files), then patch the original again.");
                break;
            case "not_chef_rpg":
                Console.WriteLine("That is not Chef RPG's Assembly-CSharp.dll, so nothing was written.");
                Console.WriteLine("Pick the Assembly-CSharp.dll inside Chef RPG's Managed folder.");
                break;
            default:
                Console.WriteLine("Verification failed, so nothing was written and your game is untouched.");
                Console.WriteLine("The game has probably been updated and the patch no longer matches.");
                Console.WriteLine("Please open an issue: https://github.com/jevonlipsey/chefrpg-43aspect/issues");
                break;
        }
    }

    static int Run(string[] args)
    {
        Console.WriteLine("==================================================");
        Console.WriteLine($"   Chef RPG 4:3 Aspect Ratio Patcher {PatchApi.Version}");
        Console.WriteLine("==================================================");

        if (args.Length == 2 && args[0] == "--verify")
        {
            VerifyAssembly(args[1]);
            return 0;
        }

        var refDirs = new List<string>();
        var pos = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--refs" && i + 1 < args.Length) refDirs.Add(args[++i]);
            else pos.Add(args[i]);
        }

        string input, managedDir = null, output;
        if (pos.Count == 0)
        {
            input = "original/Assembly-CSharp.dll";
            if (!File.Exists(input))
            {
                // a released patcher.exe opened by double-click: there is no dev input to fall back on
                PrintUsage();
                return 2;
            }
            managedDir = Directory.Exists(DEV_MANAGED) ? DEV_MANAGED : null;
            output = "Assembly-CSharp-43.dll";
        }
        else if (pos.Count == 1 && Directory.Exists(pos[0]))
        {
            managedDir = pos[0];
            input = Path.Combine(managedDir, "Assembly-CSharp.dll");
            output = Path.Combine(managedDir, "Assembly-CSharp-43.dll");
        }
        else if (pos.Count == 1)
        {
            input = pos[0];
            output = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(input)), "Assembly-CSharp-43.dll");
        }
        else if (pos.Count == 2)
        {
            input = pos[0];
            if (Directory.Exists(pos[1]))
            {
                managedDir = pos[1];
                output = "Assembly-CSharp-43.dll";
            }
            else
                output = pos[1];
        }
        else
        {
            input = pos[0];
            managedDir = pos[1];
            output = pos[2];
        }

        if (!File.Exists(input))
        {
            Console.Error.WriteLine($"[!] cannot find {input}");
            if (pos.Count <= 1)
                Console.WriteLine("Use the Assembly-CSharp.dll from Chef RPG's Managed folder, or drop that folder itself.");
            return 1;
        }
        if (managedDir != null && !Directory.Exists(managedDir))
        {
            Console.Error.WriteLine($"[!] managed dir {managedDir} does not exist");
            return 1;
        }

        // a managed dir means the verifier can resolve every unity member; without one it
        // checks everything it can inside the module and reports the rest as skipped
        IAssemblyResolver resolver = null;
        var mode = VerifyMode.ReferenceFree;
        if (managedDir != null)
        {
            var dflt = new DefaultAssemblyResolver();
            dflt.AddSearchDirectory(managedDir);
            dflt.AddSearchDirectory(Path.GetDirectoryName(Path.GetFullPath(input)));
            foreach (var dir in refDirs) dflt.AddSearchDirectory(dir);
            resolver = dflt;
            mode = VerifyMode.Full;
        }

        Console.WriteLine($"[+] input {input}");
        var result = PatchApi.Patch(File.ReadAllBytes(input), mode, resolver);
        Console.Write(result.Log);
        if (result.Passed + result.Failed + result.Skipped > 0)
        {
            Console.WriteLine(result.Summary);
            foreach (var f in result.Failures) Console.WriteLine($"    - {f}");
        }

        if (result.Output == null)
        {
            Console.Error.WriteLine($"[!] nothing written: {result.Reason}");
            if (pos.Count <= 1) PrintFailure(result.Reason);
            return result.Reason switch
            {
                "already_patched" => 3,
                "not_chef_rpg" => 4,
                _ => 1,
            };
        }

        File.WriteAllBytes(output, result.Output);
        Console.WriteLine($"[+] saved {output}");
        if (pos.Count <= 1) PrintNextSteps(output);
        return 0;
    }

    /// <summary>
    /// diagnostic mode: prints the module identity, the assembly refs, and every
    /// instruction that still points into a SafeHelper assembly. the last builds
    /// shipped a call into SafeHelper.dll (which is not in the game folder) and
    /// every frame threw FileNotFoundException, so this check guards the output.
    /// </summary>
    static void VerifyAssembly(string path)
    {
        var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
        using (var asm = AssemblyDefinition.ReadAssembly(path, new ReaderParameters { AssemblyResolver = resolver }))
        {
            var module = asm.MainModule;
            Console.WriteLine($"module: {module.Name}");
            Console.WriteLine($"mvid:   {module.Mvid}");
            Console.WriteLine("assembly refs:");
            foreach (var r in module.AssemblyReferences)
                Console.WriteLine($"  {r.Name} {r.Version}");
            Console.WriteLine($"types: {module.Types.Count}");

            int hits = 0;
            foreach (var type in module.Types)
                foreach (var method in type.Methods)
                {
                    if (!method.HasBody) continue;
                    foreach (var ins in method.Body.Instructions)
                    {
                        var scope = (ins.Operand as MemberReference)?.DeclaringType?.Scope
                            ?? (ins.Operand as TypeReference)?.Scope;
                        var name = scope switch
                        {
                            AssemblyNameReference a => a.Name,
                            ModuleDefinition m => m.Name,
                            ModuleReference mr => mr.Name,
                            _ => null
                        };
                        if (name != null && name.StartsWith("SafeHelper", StringComparison.Ordinal))
                        {
                            hits++;
                            Console.WriteLine($"  SAFEHELPER REF: {type.Name}::{method.Name}+{ins.Offset:x4} {ins.OpCode} {ins.Operand}");
                        }
                    }
                }
            Console.WriteLine(hits == 0 ? "ok: no SafeHelper references" : $"FAIL: {hits} SafeHelper reference(s)");
        }
    }

    /// <summary>
    /// builds the unity calls the game never references (Display, Screen.SetResolution with
    /// a FullScreenMode, Rect getters, ...) by hand, scoped to the module's own
    /// UnityEngine.CoreModule reference. cecil can only import what it can resolve, and the
    /// browser host has no managed folder, so nothing here may touch a unity dll. unity
    /// assemblies all bind by name at 0.0.0.0, so a hand-built ref writes the same MemberRef
    /// as an imported one. a ref the game already carries is reused.
    /// </summary>
    static void BuildCoreRefs(ModuleDefinition module)
    {
        var scope = module.AssemblyReferences.FirstOrDefault(r => r.Name == "UnityEngine.CoreModule");
        if (scope == null)
        {
            scope = new AssemblyNameReference("UnityEngine.CoreModule", new Version(0, 0, 0, 0));
            module.AssemblyReferences.Add(scope);
        }

        var existing = new Dictionary<string, MemberReference>();
        foreach (var mr in module.GetMemberReferences())
            if (mr.DeclaringType?.Scope == scope) existing.TryAdd(mr.FullName, mr);

        TypeReference Cls(string name) => new TypeReference("UnityEngine", name, module, scope, false);
        TypeReference Val(string name) => new TypeReference("UnityEngine", name, module, scope, true);
        MethodReference Method(TypeReference decl, string name, TypeReference ret, bool hasThis, params TypeReference[] ps)
        {
            var m = new MethodReference(name, ret, decl) { HasThis = hasThis };
            foreach (var p in ps) m.Parameters.Add(new ParameterDefinition(p));
            return existing.TryGetValue(m.FullName, out var found) ? (MethodReference)found : m;
        }
        FieldReference Field(TypeReference decl, string name, TypeReference type)
        {
            var f = new FieldReference(name, type, decl);
            return existing.TryGetValue(f.FullName, out var found) ? (FieldReference)found : f;
        }

        var ts = module.TypeSystem;
        var debug = Cls("Debug");
        var display = Cls("Display");
        var screen = Cls("Screen");
        var fullScreenMode = Val("FullScreenMode");
        var obj = Cls("Object");
        var cam = Cls("Camera");
        var rect = Val("Rect");
        var time = Cls("Time");
        var mathf = Val("Mathf");
        var v2 = Val("Vector2");

        debugLogRef = Method(debug, "Log", ts.Void, false, ts.Object);
        displayGetMain = Method(display, "get_main", display, false);
        displayGetSysW = Method(display, "get_systemWidth", ts.Int32, true);
        displayGetSysH = Method(display, "get_systemHeight", ts.Int32, true);
        screenGetFullScreenRef = Method(screen, "get_fullScreen", ts.Boolean, false);

        objGetName = Method(obj, "get_name", ts.String, true);
        camGetPixelRect = Method(cam, "get_pixelRect", rect, true);
        camGetOrthoSize = Method(cam, "get_orthographicSize", ts.Single, true);
        camGetTargetTexture = Method(cam, "get_targetTexture", Cls("RenderTexture"), true);
        camGetDepth = Method(cam, "get_depth", ts.Single, true);
        rectGetX = Method(rect, "get_x", ts.Single, true);
        rectGetY = Method(rect, "get_y", ts.Single, true);
        rectGetWidth = Method(rect, "get_width", ts.Single, true);
        rectGetHeight = Method(rect, "get_height", ts.Single, true);
        timeGetFrameCount = Method(time, "get_frameCount", ts.Int32, false);
        mathfRoundToInt = Method(mathf, "RoundToInt", ts.Int32, false, ts.Single);

        v2TypeRef = v2;
        v2FieldX = Field(v2, "x", ts.Single);
        v2FieldY = Field(v2, "y", ts.Single);

        Console.WriteLine($"    FullScreenMode.FullScreenWindow = {FullScreenWindowValue}");
        Console.WriteLine($"    core refs built ({scope.Name})");
    }

    /// <summary>
    /// runs every patch on an open assembly and returns the written bytes. i/o stays with
    /// the caller (PatchApi), so this works from memory in the browser as well as from disk.
    /// </summary>
    internal static byte[] PatchAssembly(AssemblyDefinition asm)
    {
        ResetState();
        var module = asm.MainModule;
        BuildCoreRefs(module);

        // import the core calls into this module
        var debugLog = module.ImportReference(debugLogRef);
        var getMain = module.ImportReference(displayGetMain);
        var getSysW = module.ImportReference(displayGetSysW);
        var getSysH = module.ImportReference(displayGetSysH);

        var screenGetWidth = Helper.FindMethod(module, "Screen", "get_width", 0);
        var screenGetHeight = Helper.FindMethod(module, "Screen", "get_height", 0);
        var screenGetFullScreen = screenGetFullScreenRef != null ? module.ImportReference(screenGetFullScreenRef) : null;
        var v2Ctor = Helper.FindMethod(module, "Vector2", ".ctor", 2);
        var v2Zero = Helper.FindMethod(module, "Vector2", "get_zero", 0);
        var rectCtor = Helper.FindMethod(module, "Rect", ".ctor", 4);
        var camSetRect = Helper.FindMethod(module, "Camera", "set_rect", 1);

        // resolve refs for the on-disk diagnostic (file next to the save data)
        diagAppPath = Helper.FindMethod(module, "Application", "get_persistentDataPath", 0);
        diagConcatSS = FindConcat(module, "String", "String");
        diagConcatOO = FindConcat(module, "Object", "Object");
        diagAppendAllText = Helper.FindMethod(module, "File", "AppendAllText", 2);
        Console.WriteLine($"    diagnostics: appPath={(diagAppPath != null)} concat={(diagConcatSS != null)}/{(diagConcatOO != null)} append={(diagAppendAllText != null)}");

        ConfigureLetterBoxer43(module);
        // the canvas scaler is deliberately left alone. the game's own ScreenRatioSetup
        // picks match-width for every aspect up to 16:9 and match-height above it, which
        // keeps the 640x360 hud design box fully visible at any aspect. forcing
        // match-height (as v5.2 did) scaled the design up 33% and cropped 160 units off
        // its width on a 4:3 screen, which is exactly what cut the clock and the map off
        FixFormulas(module, screenGetWidth, screenGetHeight, v2Ctor, v2Zero);
        FixCameraZoom(module);
        ForcePixelPerfectStretch(module);
        // AddCameraDiag is no longer installed: it logged ~20 lines with stack traces
        // every 300 frames, a regular hitch under emulation on the device. the helper's
        // one-shot render dump (DumpRenderPath) reports the same camera numbers.
        FixTooltips(module);
        FixUiScale360(module);
        FixDialogue(module);
        FixUILineRenderer(module);
        // the display policy calls into the cloned helper type, so inject it first
        InjectSafeAreaHelper(module);
        ApplyDisplayPolicy(module, debugLog, getMain, getSysW, getSysH, screenGetFullScreen);
        AddFullscreenRetry(module);

        using var ms = new MemoryStream();
        asm.Write(ms);
        var bytes = ms.ToArray();
        VerifyBranches(bytes);
        Console.WriteLine($"[+] wrote patched assembly ({bytes.Length} bytes)");
        return bytes;
    }

    /// <summary>
    /// cecil writes a short branch as a single signed byte and never widens it, so
    /// inserting code in front of a branch target silently overflows the operand and
    /// sends the branch into the middle of an instruction. mono then jits garbage and
    /// the game dies with an access violation. widen every short branch in any method
    /// we edit to its long form before cecil gets a chance to write the body.
    /// </summary>
    static void ExpandShortBranches(MethodDefinition method)
    {
        if (!method.HasBody) return;
        foreach (var ins in method.Body.Instructions)
        {
            var wide = LongBranch(ins.OpCode);
            if (wide.HasValue) ins.OpCode = wide.Value;
        }
    }

    static OpCode? LongBranch(OpCode op)
    {
        switch (op.Code)
        {
            case Code.Br_S: return OpCodes.Br;
            case Code.Brfalse_S: return OpCodes.Brfalse;
            case Code.Brtrue_S: return OpCodes.Brtrue;
            case Code.Beq_S: return OpCodes.Beq;
            case Code.Bge_S: return OpCodes.Bge;
            case Code.Bgt_S: return OpCodes.Bgt;
            case Code.Ble_S: return OpCodes.Ble;
            case Code.Blt_S: return OpCodes.Blt;
            case Code.Bne_Un_S: return OpCodes.Bne_Un;
            case Code.Bge_Un_S: return OpCodes.Bge_Un;
            case Code.Bgt_Un_S: return OpCodes.Bgt_Un;
            case Code.Ble_Un_S: return OpCodes.Ble_Un;
            case Code.Blt_Un_S: return OpCodes.Blt_Un;
            case Code.Leave_S: return OpCodes.Leave;
            default: return null;
        }
    }

    // cecil keeps branch targets as instruction references. inserting code before
    // a target does not move existing branches, so a path that used to jump there
    // skips the new prologue. point every branch (and switch arm) at the new head.
    static void RetargetBranches(MethodDefinition method, Instruction oldTarget, Instruction newTarget)
    {
        foreach (var ins in method.Body.Instructions)
        {
            if (ReferenceEquals(ins.Operand, oldTarget))
                ins.Operand = newTarget;
            else if (ins.Operand is Instruction[] arms)
                for (int i = 0; i < arms.Length; i++)
                    if (ReferenceEquals(arms[i], oldTarget)) arms[i] = newTarget;
        }
    }

    /// <summary>
    /// re-reads what we just wrote. a branch whose target does not land on an
    /// instruction boundary comes back with a null operand, so this is the cheap way
    /// to catch a corrupt body before anyone puts it on a device.
    /// </summary>
    static void VerifyBranches(byte[] assemblyBytes)
    {
        var broken = new List<string>();
        using (var ms = new MemoryStream(assemblyBytes))
        using (var asm = AssemblyDefinition.ReadAssembly(ms, new ReaderParameters { AssemblyResolver = new NullAssemblyResolver() }))
        {
            foreach (var type in asm.MainModule.Types)
                foreach (var method in type.Methods)
                {
                    if (!method.HasBody) continue;
                    foreach (var ins in method.Body.Instructions)
                    {
                        if (ins.OpCode.OperandType != OperandType.ShortInlineBrTarget &&
                            ins.OpCode.OperandType != OperandType.InlineBrTarget) continue;
                        if (ins.Operand == null)
                            broken.Add($"{type.Name}::{method.Name}@{ins.Offset:x4}");
                    }
                }
        }

        if (broken.Count > 0)
            throw new InvalidOperationException(
                $"patched IL is corrupt: {broken.Count} branch(es) point into the middle of an instruction ({string.Join(", ", broken.Take(8))})");

        Console.WriteLine("    [OK] every branch target in the output still resolves");
    }

    /// <summary>
    /// LetterBoxer hardcodes a 16:9 (x=16,y=9) matte and shrinks cam.rect every
    /// Update to letterbox the game into a 16:9 box. on a 4:3 panel that is the
    /// black bar source, so we hand the camera the full viewport instead.
    /// we also neutralize Awake() and change the default field values (x, y, width, height)
    /// to 4:3 so whatever reads them gets native 4:3 aspect.
    /// </summary>
    static void ConfigureLetterBoxer43(ModuleDefinition module)
    {
        var lb = module.Types.FirstOrDefault(t => t.Name == "LetterBoxer");
        if (lb == null) { Console.WriteLine("    [!] LetterBoxer not found"); return; }

        // Change field initializers in .ctor to 4:3 (x=4f, y=3f)
        var ctor = lb.Methods.FirstOrDefault(m => m.Name == ".ctor");
        if (ctor != null)
        {
            foreach (var ins in ctor.Body.Instructions)
            {
                if (ins.OpCode == OpCodes.Ldc_R4 && (float)ins.Operand == 16f) ins.Operand = 4f;
                if (ins.OpCode == OpCodes.Ldc_R4 && (float)ins.Operand == 9f) ins.Operand = 3f;
            }
            Console.WriteLine("    [OK] LetterBoxer..ctor -> x=4f, y=3f");
        }

        // Ensure Awake() forces x=4f, y=3f, width=4f, height=3f, referenceMode=0 (DesignedAspectRatio)
        // so existing serialized prefab values (16:9 or 960x540) are completely overridden!
        var awake = lb.Methods.FirstOrDefault(m => m.Name == "Awake");
        if (awake != null && awake.HasBody)
        {
            var fldX = lb.Fields.FirstOrDefault(f => f.Name == "x");
            var fldY = lb.Fields.FirstOrDefault(f => f.Name == "y");
            var fldW = lb.Fields.FirstOrDefault(f => f.Name == "width");
            var fldH = lb.Fields.FirstOrDefault(f => f.Name == "height");
            var fldRefMode = lb.Fields.FirstOrDefault(f => f.Name == "referenceMode");
            
            var il = awake.Body.GetILProcessor();
            var first = awake.Body.Instructions[0];
            if (fldX != null)
            {
                il.InsertBefore(first, il.Create(OpCodes.Ldarg_0));
                il.InsertBefore(first, il.Create(OpCodes.Ldc_R4, 4f));
                il.InsertBefore(first, il.Create(OpCodes.Stfld, fldX));
            }
            if (fldY != null)
            {
                il.InsertBefore(first, il.Create(OpCodes.Ldarg_0));
                il.InsertBefore(first, il.Create(OpCodes.Ldc_R4, 3f));
                il.InsertBefore(first, il.Create(OpCodes.Stfld, fldY));
            }
            if (fldW != null)
            {
                il.InsertBefore(first, il.Create(OpCodes.Ldarg_0));
                il.InsertBefore(first, il.Create(OpCodes.Ldc_R4, 4f));
                il.InsertBefore(first, il.Create(OpCodes.Stfld, fldW));
            }
            if (fldH != null)
            {
                il.InsertBefore(first, il.Create(OpCodes.Ldarg_0));
                il.InsertBefore(first, il.Create(OpCodes.Ldc_R4, 3f));
                il.InsertBefore(first, il.Create(OpCodes.Stfld, fldH));
            }
            if (fldRefMode != null)
            {
                il.InsertBefore(first, il.Create(OpCodes.Ldarg_0));
                il.InsertBefore(first, il.Create(OpCodes.Ldc_I4_0)); // ReferenceMode.DesignedAspectRatio = 0
                il.InsertBefore(first, il.Create(OpCodes.Stfld, fldRefMode));
            }
            // the watchdog lives at the tail of Update, inside the onUpdate branch.
            // a prefab that serialized it off would silently disable the fix.
            var fldOnUpdate = lb.Fields.FirstOrDefault(f => f.Name == "onUpdate");
            if (fldOnUpdate != null)
            {
                il.InsertBefore(first, il.Create(OpCodes.Ldarg_0));
                il.InsertBefore(first, il.Create(OpCodes.Ldc_I4_1));
                il.InsertBefore(first, il.Create(OpCodes.Stfld, fldOnUpdate));
            }
            ExpandShortBranches(awake);
            Console.WriteLine("    [OK] LetterBoxer.Awake -> enforce x=4f, y=3f, width=4f, height=3f, referenceMode=0, onUpdate=1");
        }

        // performSizing would shrink the camera back to a 4:3 box inside the
        // framebuffer. pixel-perfect already owns the aspect-fit blit, so let it
        // render full-frame and keep the two from fighting over cam.rect.
        foreach (var name in new[] { "PerformSizing", "AddLetterBoxingCamera" })
        {
            var m = lb.Methods.FirstOrDefault(x => x.Name == name);
            if (m == null || !m.HasBody) { Console.WriteLine($"    [!] LetterBoxer.{name} not found"); continue; }
            m.Body.Instructions.Clear();
            m.Body.Variables.Clear();
            m.Body.InitLocals = false;
            var il = m.Body.GetILProcessor();
            il.Append(il.Create(OpCodes.Ret));
            Console.WriteLine($"    [OK] LetterBoxer.{name} -> no-op");
        }
    }

    /// <summary>
    /// ScreenRatioSetup is no longer touched. it flips CanvasScaler.matchWidthOrHeight
    /// between match-width (0f, every aspect up to 16:9) and match-height (1f, wider
    /// than 16:9), which is the right rule: the canvas then always shows the whole
    /// 640x360 hud design box. forcing match-height for 4:3 cropped the box.
    /// </summary>

    /// <summary>
    /// Formulas bakes the assumption that the visible frame is always 16:9 and
    /// offsets the mouse by the letterbox. with a full-screen 4:3 frame those
    /// offsets must vanish or the cursor drifts off the UI.
    /// </summary>
    static void FixFormulas(ModuleDefinition module, MethodReference screenGetWidth,
        MethodReference screenGetHeight, MethodReference v2Ctor, MethodReference v2Zero)
    {
        var formulas = module.Types.FirstOrDefault(t => t.Name == "Formulas");
        if (formulas == null) { Console.WriteLine("    [!] Formulas not found"); return; }

        var v2Type = Helper.FindType(module, "Vector2") ?? v2TypeRef;
        var fldX = module.ImportReference(v2FieldX);
        var fldY = module.ImportReference(v2FieldY);
        var getActual = formulas.Methods.FirstOrDefault(m => m.Name == "GetActualScreenSize");
        var locate = formulas.Methods.FirstOrDefault(m => m.Name == "LocateScreenPosition");
        var corner = formulas.Methods.FirstOrDefault(m => m.Name == "GetCornerOffset");
        if (getActual == null || locate == null || corner == null)
        {
            Console.WriteLine("    [!] Formulas missing members");
            return;
        }

        // actual view = the largest 4:3 rect inside the screen, height-preferring,
        // matching the pixel-perfect camera's aspect-fit blit
        getActual.Body.Instructions.Clear();
        getActual.Body.Variables.Clear();
        var v0 = new VariableDefinition(v2Type);
        getActual.Body.Variables.Add(v0);
        var il = getActual.Body.GetILProcessor();
        var aRet = Instruction.Create(OpCodes.Ldloc, v0);
        var aEnd = Instruction.Create(OpCodes.Ret);
        foreach (var ins in new List<Instruction>
        {
            // width = Screen.height * 4/3, height = Screen.height
            Instruction.Create(OpCodes.Call, screenGetHeight), Instruction.Create(OpCodes.Conv_R4),
            Instruction.Create(OpCodes.Ldc_R4, 3f), Instruction.Create(OpCodes.Div),
            Instruction.Create(OpCodes.Ldc_R4, 4f), Instruction.Create(OpCodes.Mul),
            Instruction.Create(OpCodes.Call, screenGetHeight), Instruction.Create(OpCodes.Conv_R4),
            Instruction.Create(OpCodes.Newobj, v2Ctor),
            Instruction.Create(OpCodes.Stloc, v0),
            aRet,
            aEnd,
        })
        {
            il.Append(ins);
        }
        Console.WriteLine("    [OK] Formulas.GetActualScreenSize -> (Screen.height*4/3, Screen.height)");

        // the original locate/corner math letterboxed a 16:9 view inside the
        // screen (with a dead integer-division aspect compare). the world camera
        // now renders an aspect-fit 4:3 view, so the same mapping runs on 4:3
        // with the bar sizes as offsets. hud elements like the virtual cursor
        // anchor at cornerOffset, so without this they sit inside the bars.
        var usr = module.ImportReference(getActual);

        // locate: result = pos; if (w/h > 4/3) result.x -= (w-actual.x)/2;
        // else if (w/h < 4/3) result.y -= (h-actual.y)/2
        locate.Body.Instructions.Clear();
        locate.Body.Variables.Clear();
        var vRes = new VariableDefinition(v2Type);
        var vAct = new VariableDefinition(v2Type);
        locate.Body.Variables.Add(vRes); locate.Body.Variables.Add(vAct);
        il = locate.Body.GetILProcessor();
        var lRet = Instruction.Create(OpCodes.Ldloc, vRes);
        var lEnd = Instruction.Create(OpCodes.Ret);
        var lPillar = Instruction.Create(OpCodes.Ldloca, vRes);
        var lNarrow = Instruction.Create(OpCodes.Ldloca, vRes);
        foreach (var ins in new List<Instruction>
        {
            Instruction.Create(OpCodes.Ldarg_1),
            Instruction.Create(OpCodes.Stloc, vRes),
            Instruction.Create(OpCodes.Ldarg_0),
            Instruction.Create(OpCodes.Callvirt, usr),
            Instruction.Create(OpCodes.Stloc, vAct),
            Instruction.Create(OpCodes.Call, screenGetWidth), Instruction.Create(OpCodes.Conv_R4),
            Instruction.Create(OpCodes.Call, screenGetHeight), Instruction.Create(OpCodes.Conv_R4),
            Instruction.Create(OpCodes.Div),
            Instruction.Create(OpCodes.Ldc_R4, 4f / 3f + 1e-6f),
            Instruction.Create(OpCodes.Bgt, lPillar),
            Instruction.Create(OpCodes.Call, screenGetWidth), Instruction.Create(OpCodes.Conv_R4),
            Instruction.Create(OpCodes.Call, screenGetHeight), Instruction.Create(OpCodes.Conv_R4),
            Instruction.Create(OpCodes.Div),
            Instruction.Create(OpCodes.Ldc_R4, 4f / 3f + 1e-6f),
            Instruction.Create(OpCodes.Blt, lNarrow),
            Instruction.Create(OpCodes.Br, lRet),
            lPillar,
            Instruction.Create(OpCodes.Ldloc, vRes), Instruction.Create(OpCodes.Ldfld, fldX),
            Instruction.Create(OpCodes.Call, screenGetWidth), Instruction.Create(OpCodes.Conv_R4),
            Instruction.Create(OpCodes.Ldloc, vAct), Instruction.Create(OpCodes.Ldfld, fldX),
            Instruction.Create(OpCodes.Sub),
            Instruction.Create(OpCodes.Ldc_I4_2), Instruction.Create(OpCodes.Conv_R4),
            Instruction.Create(OpCodes.Div),
            Instruction.Create(OpCodes.Sub),
            Instruction.Create(OpCodes.Stfld, fldX),
            Instruction.Create(OpCodes.Br, lRet),
            lNarrow,
            Instruction.Create(OpCodes.Ldloc, vRes), Instruction.Create(OpCodes.Ldfld, fldY),
            Instruction.Create(OpCodes.Call, screenGetHeight), Instruction.Create(OpCodes.Conv_R4),
            Instruction.Create(OpCodes.Ldloc, vAct), Instruction.Create(OpCodes.Ldfld, fldY),
            Instruction.Create(OpCodes.Sub),
            Instruction.Create(OpCodes.Ldc_I4_2), Instruction.Create(OpCodes.Conv_R4),
            Instruction.Create(OpCodes.Div),
            Instruction.Create(OpCodes.Sub),
            Instruction.Create(OpCodes.Stfld, fldY),
            lRet,
            lEnd,
        })
        {
            il.Append(ins);
        }
        ExpandShortBranches(locate);
        Console.WriteLine("    [OK] Formulas.LocateScreenPosition -> remap into the 4:3 view");

        // corner: result = ((w-actual.x)/2, 0); if (w/h < 4/3) result = (0, (h-actual.y)/2)
        corner.Body.Instructions.Clear();
        corner.Body.Variables.Clear();
        var vAct2 = new VariableDefinition(v2Type);
        var vRes2 = new VariableDefinition(v2Type);
        corner.Body.Variables.Add(vAct2); corner.Body.Variables.Add(vRes2);
        il = corner.Body.GetILProcessor();
        var cRet = Instruction.Create(OpCodes.Ldloc, vRes2);
        var cEnd = Instruction.Create(OpCodes.Ret);
        var cNarrow = Instruction.Create(OpCodes.Ldc_R4, 0f);
        foreach (var ins in new List<Instruction>
        {
            Instruction.Create(OpCodes.Ldarg_0),
            Instruction.Create(OpCodes.Callvirt, usr),
            Instruction.Create(OpCodes.Stloc, vAct2),
            Instruction.Create(OpCodes.Call, screenGetWidth), Instruction.Create(OpCodes.Conv_R4),
            Instruction.Create(OpCodes.Ldloc, vAct2), Instruction.Create(OpCodes.Ldfld, fldX),
            Instruction.Create(OpCodes.Sub),
            Instruction.Create(OpCodes.Ldc_I4_2), Instruction.Create(OpCodes.Conv_R4),
            Instruction.Create(OpCodes.Div),
            Instruction.Create(OpCodes.Ldc_R4, 0f),
            Instruction.Create(OpCodes.Newobj, v2Ctor),
            Instruction.Create(OpCodes.Stloc, vRes2),
            Instruction.Create(OpCodes.Call, screenGetWidth), Instruction.Create(OpCodes.Conv_R4),
            Instruction.Create(OpCodes.Call, screenGetHeight), Instruction.Create(OpCodes.Conv_R4),
            Instruction.Create(OpCodes.Div),
            Instruction.Create(OpCodes.Ldc_R4, 4f / 3f + 1e-6f),
            Instruction.Create(OpCodes.Blt, cNarrow),
            Instruction.Create(OpCodes.Br, cRet),
            cNarrow,
            Instruction.Create(OpCodes.Call, screenGetHeight), Instruction.Create(OpCodes.Conv_R4),
            Instruction.Create(OpCodes.Ldloc, vAct2), Instruction.Create(OpCodes.Ldfld, fldY),
            Instruction.Create(OpCodes.Sub),
            Instruction.Create(OpCodes.Ldc_I4_2), Instruction.Create(OpCodes.Conv_R4),
            Instruction.Create(OpCodes.Div),
            Instruction.Create(OpCodes.Newobj, v2Ctor),
            Instruction.Create(OpCodes.Stloc, vRes2),
            cRet,
            cEnd,
        })
        {
            il.Append(ins);
        }
        ExpandShortBranches(corner);
        Console.WriteLine("    [OK] Formulas.GetCornerOffset -> pillar/letterbox offset of the 4:3 view");
    }
    /// the reference resolution sets the world height the camera shows and, through
    /// integer zoom, the size an art pixel lands on. at 1280x960 a 480-tall reference
    /// is an exact 2x, which is the whole point on a fixed pixel-art panel, so bump
    /// the authored height (360/450) to the panel's half height and let the width
    /// keep its authored 4:3-compatible value (640x480 / 800x600).
    /// </summary>
    static void FixCameraZoom(ModuleDefinition module)
    {
        var cf = module.Types.FirstOrDefault(t => t.Name == "CameraFollow");
        if (cf == null) { Console.WriteLine("    [!] CameraFollow not found"); return; }

        var zoomIn = cf.Methods.FirstOrDefault(m => m.Name == "SetZoomInMode");
        if (zoomIn != null)
        {
            foreach (var ins in zoomIn.Body.Instructions)
                if (ins.OpCode == OpCodes.Ldc_I4 && (int)ins.Operand == 360)
                    ins.Operand = 480;
            Console.WriteLine("    [OK] CameraFollow.SetZoomInMode -> 640x480");
        }

        var zoomOut = cf.Methods.FirstOrDefault(m => m.Name == "SetZoomOutMode");
        if (zoomOut != null)
        {
            foreach (var ins in zoomOut.Body.Instructions)
                if (ins.OpCode == OpCodes.Ldc_I4 && (int)ins.Operand == 450)
                    ins.Operand = 600;
            Console.WriteLine("    [OK] CameraFollow.SetZoomOutMode -> 800x600");
        }
    }

    /// <summary>
    /// the pixel-perfect camera only zooms by whole integers, so a 4:3 reference
    /// resolution on a 4:3 screen still leaves a 1-2 pixel border when the display
    /// size is not an exact multiple. enabling cropFrame + stretchFill switches it
    /// to the aspect-fit blit, which fills edge to edge whenever the frame is 4:3
    /// and shows a centered 4:3 pillarbox on wider screens. the reference stays
    /// at the authored 4:3 values - adapting refX to the live screen aspect would
    /// widen the world view past 4:3, which the whole mod is meant to avoid.
    /// </summary>
    static void ForcePixelPerfectStretch(ModuleDefinition module)
    {
        var cf = module.Types.FirstOrDefault(t => t.Name == "CameraFollow");
        var ppcType = Helper.FindType(module, "PixelPerfectCamera");
        if (cf == null || ppcType == null) { Console.WriteLine("    [!] PixelPerfectCamera not resolved"); return; }

        var camField = cf.Fields.FirstOrDefault(f => f.Name == "PixelPerfectCamera");
        if (camField == null) { Console.WriteLine("    [!] CameraFollow.PixelPerfectCamera field not found"); return; }

        MethodReference Prop(string name, TypeReference ret, TypeReference arg = null)
        {
            var mr = new MethodReference(name, module.ImportReference(ret), ppcType) { HasThis = true };
            if (arg != null) mr.Parameters.Add(new ParameterDefinition(module.ImportReference(arg)));
            return module.ImportReference(mr);
        }

        var setCropX = Prop("set_cropFrameX", module.TypeSystem.Void, module.TypeSystem.Boolean);
        var setCropY = Prop("set_cropFrameY", module.TypeSystem.Void, module.TypeSystem.Boolean);
        var setStretch = Prop("set_stretchFill", module.TypeSystem.Void, module.TypeSystem.Boolean);
        var setRefX = Prop("set_refResolutionX", module.TypeSystem.Void, module.TypeSystem.Int32);
        var getRefY = Prop("get_refResolutionY", module.TypeSystem.Int32);

        var screenGetWidth = Helper.FindMethod(module, "Screen", "get_width", 0);
        var screenGetHeight = Helper.FindMethod(module, "Screen", "get_height", 0);
        var roundToInt = Helper.FindMethod(module, "Mathf", "RoundToInt", 1)
            ?? (mathfRoundToInt != null ? module.ImportReference(mathfRoundToInt) : null);

        int patched = 0;
        foreach (var name in new[] { "SetZoomInMode", "SetZoomOutMode" })
        {
            var method = cf.Methods.FirstOrDefault(m => m.Name == name);
            if (method == null || !method.HasBody) continue;

            // we are already inside the method's `if (PixelPerfectCamera != null)` block
            Instruction anchor = null;
            foreach (var ins in method.Body.Instructions)
                if (ins.OpCode == OpCodes.Callvirt && ins.Operand.ToString().Contains("set_refResolutionY"))
                    anchor = ins;
            if (anchor == null) { Console.WriteLine($"    [!] {name}: refResolutionY store not found"); continue; }

            var il = method.Body.GetILProcessor();
            var next = anchor.Next;
            foreach (var setter in new[] { setCropX, setCropY, setStretch })
            {
                il.InsertBefore(next, il.Create(OpCodes.Ldarg_0));
                il.InsertBefore(next, il.Create(OpCodes.Ldfld, camField));
                il.InsertBefore(next, il.Create(OpCodes.Ldc_I4_1));
                il.InsertBefore(next, il.Create(OpCodes.Callvirt, setter));
            }

            // fixed authored 4:3 reference resolution; stretchFill does the
            // aspect-fit blit, so wide screens show true 4:3 pillarboxed and
            // the 4:3 device fills edge to edge. do not adapt refX to the live
            // aspect - that widened the world view to the screen's aspect.
            ExpandShortBranches(method);
            patched++;
        }
        Console.WriteLine($"    [OK] CameraFollow -> cropFrameX/Y + stretchFill = true, refX from live aspect ({patched} methods)");
    }

    /// <summary>
    /// dumps what the camera and the pixel-perfect camera actually resolve to at
    /// runtime. a 16:9 frame on a 4:3 screen can come from the game, from urp's
    /// cropped blit, or from the container, and only the live numbers separate
    /// them. hooked on LetterBoxer.Update because it runs every frame and its
    /// `cam` field is the real world camera.
    /// </summary>
    static void AddCameraDiag(ModuleDefinition module, MethodReference debugLog)
    {
        var lb = module.Types.FirstOrDefault(t => t.Name == "LetterBoxer");
        var upd = lb?.Methods.FirstOrDefault(m => m.Name == "Update" && m.Parameters.Count == 0);
        if (upd == null || !upd.HasBody) { Console.WriteLine("    [!] LetterBoxer.Update not found"); return; }

        var camField = lb.Fields.FirstOrDefault(f => f.Name == "cam");
        var rectType = Helper.FindType(module, "Rect");
        var ppcType = Helper.FindType(module, "PixelPerfectCamera");
        if (camField == null || rectType == null || ppcType == null)
        {
            Console.WriteLine("    [!] camera diag: cam/Rect/PixelPerfectCamera not all resolved");
            return;
        }

        var getRect = module.ImportReference(camGetPixelRect);
        var getOrtho = module.ImportReference(camGetOrthoSize);
        var getTT = module.ImportReference(camGetTargetTexture);
        var getDepth = module.ImportReference(camGetDepth);
        var getName = module.ImportReference(objGetName);
        var frameCount = module.ImportReference(timeGetFrameCount);
        var screenW = Helper.FindMethod(module, "Screen", "get_width", 0);
        var screenH = Helper.FindMethod(module, "Screen", "get_height", 0);

        // GetComponent<PixelPerfectCamera>() - the game already calls this in
        // CameraFollow.Awake, so the reference exists to specialise.
        MethodReference getComp = null;
        foreach (var mr in module.GetMemberReferences().OfType<MethodReference>())
        {
            if (mr.Name != "GetComponent" || mr.Parameters.Count != 0) continue;
            var element = (mr as GenericInstanceMethod)?.ElementMethod ?? mr;
            if (element.GenericParameters.Count != 1) continue;
            if (mr.DeclaringType.Name != "Component" && mr.DeclaringType.Name != "GameObject") continue;
            var gim = new GenericInstanceMethod(module.ImportReference(element));
            gim.GenericArguments.Add(module.ImportReference(ppcType));
            getComp = gim;
            break;
        }

        Func<MethodReference, Action<List<Instruction>>> camProp = getter => l =>
        {
            l.Add(Instruction.Create(OpCodes.Ldarg_0));
            l.Add(Instruction.Create(OpCodes.Ldfld, camField));
            l.Add(Instruction.Create(OpCodes.Callvirt, getter));
        };

        var body = upd.Body;
        body.InitLocals = true;
        var rectLocal = new VariableDefinition(module.ImportReference(rectType));
        var ppcLocal = new VariableDefinition(module.ImportReference(ppcType));
        body.Variables.Add(rectLocal);
        body.Variables.Add(ppcLocal);

        Func<MethodReference, Action<List<Instruction>>> rectProp = getter => l =>
        {
            l.Add(Instruction.Create(OpCodes.Ldloca, rectLocal));
            l.Add(Instruction.Create(OpCodes.Call, getter));
            l.Add(Instruction.Create(OpCodes.Conv_I4));
        };
        Action<List<Instruction>, MethodReference> ppInt = (l, getter) =>
        {
            l.Add(Instruction.Create(OpCodes.Ldloc, ppcLocal));
            l.Add(Instruction.Create(OpCodes.Callvirt, getter));
        };
        Action<List<Instruction>, MethodReference> ppBool = (l, getter) =>
        {
            l.Add(Instruction.Create(OpCodes.Ldloc, ppcLocal));
            l.Add(Instruction.Create(OpCodes.Callvirt, getter));
            l.Add(Instruction.Create(OpCodes.Ldc_I4_0));
            l.Add(Instruction.Create(OpCodes.Cgt_Un));
        };
        Func<string, TypeReference, MethodReference> ppGetter = (name, ret) =>
            module.ImportReference(new MethodReference(name, module.ImportReference(ret),
                module.ImportReference(ppcType)) { HasThis = true });

        var skip = Instruction.Create(OpCodes.Nop);
        var skipPpu = Instruction.Create(OpCodes.Nop);
        var code = new List<Instruction>();

        // once every 300 frames (~5s), enough to see the steady state and cheap enough
        // to leave the log readable
        code.Add(Instruction.Create(OpCodes.Call, frameCount));
        code.Add(Instruction.Create(OpCodes.Ldc_I4, 300));
        code.Add(Instruction.Create(OpCodes.Rem));
        code.Add(Instruction.Create(OpCodes.Brtrue, skip));

        AppendStrDiag(code, debugLog, "CHEFRPG43 camName=", l =>
        {
            l.Add(Instruction.Create(OpCodes.Ldarg_0));
            l.Add(Instruction.Create(OpCodes.Ldfld, camField));
            l.Add(Instruction.Create(OpCodes.Callvirt, getName));
        });
        AppendDiag(code, module, debugLog, "CHEFRPG43 camDepth=", l =>
        {
            camProp(getDepth)(l);
            l.Add(Instruction.Create(OpCodes.Conv_I4));
        });
        AppendDiag(code, module, debugLog, "CHEFRPG43 camScrW=", l => l.Add(Instruction.Create(OpCodes.Call, screenW)));
        AppendDiag(code, module, debugLog, "CHEFRPG43 camScrH=", l => l.Add(Instruction.Create(OpCodes.Call, screenH)));
        AppendDiag(code, module, debugLog, "CHEFRPG43 camTexture=", l =>
        {
            camProp(getTT)(l);
            l.Add(Instruction.Create(OpCodes.Ldnull));
            l.Add(Instruction.Create(OpCodes.Cgt_Un));
        });

        code.Add(Instruction.Create(OpCodes.Ldarg_0));
        code.Add(Instruction.Create(OpCodes.Ldfld, camField));
        code.Add(Instruction.Create(OpCodes.Callvirt, getRect));
        code.Add(Instruction.Create(OpCodes.Stloc, rectLocal));
        AppendDiag(code, module, debugLog, "CHEFRPG43 camRectX=", rectProp(module.ImportReference(rectGetX)));
        AppendDiag(code, module, debugLog, "CHEFRPG43 camRectY=", rectProp(module.ImportReference(rectGetY)));
        AppendDiag(code, module, debugLog, "CHEFRPG43 camRectW=", rectProp(module.ImportReference(rectGetWidth)));
        AppendDiag(code, module, debugLog, "CHEFRPG43 camRectH=", rectProp(module.ImportReference(rectGetHeight)));
        AppendDiag(code, module, debugLog, "CHEFRPG43 camOrtho=", l =>
        {
            camProp(getOrtho)(l);
            l.Add(Instruction.Create(OpCodes.Ldc_R4, 100f));
            l.Add(Instruction.Create(OpCodes.Mul));
            l.Add(Instruction.Create(OpCodes.Conv_I4));
        });

        if (getComp != null)
        {
            code.Add(Instruction.Create(OpCodes.Ldarg_0));
            code.Add(Instruction.Create(OpCodes.Ldfld, camField));
            code.Add(Instruction.Create(OpCodes.Callvirt, getComp));
            code.Add(Instruction.Create(OpCodes.Stloc, ppcLocal));
            code.Add(Instruction.Create(OpCodes.Ldloc, ppcLocal));
            code.Add(Instruction.Create(OpCodes.Brfalse, skipPpu));

            var i32 = module.TypeSystem.Int32;
            var b = module.TypeSystem.Boolean;
            AppendDiag(code, module, debugLog, "CHEFRPG43 ppRefW=", l => ppInt(l, ppGetter("get_refResolutionX", i32)));
            AppendDiag(code, module, debugLog, "CHEFRPG43 ppRefH=", l => ppInt(l, ppGetter("get_refResolutionY", i32)));
            AppendDiag(code, module, debugLog, "CHEFRPG43 ppAssets=", l => ppInt(l, ppGetter("get_assetsPPU", i32)));
            AppendDiag(code, module, debugLog, "CHEFRPG43 ppCropX=", l => ppBool(l, ppGetter("get_cropFrameX", b)));
            AppendDiag(code, module, debugLog, "CHEFRPG43 ppCropY=", l => ppBool(l, ppGetter("get_cropFrameY", b)));
            AppendDiag(code, module, debugLog, "CHEFRPG43 ppStretch=", l => ppBool(l, ppGetter("get_stretchFill", b)));
            AppendDiag(code, module, debugLog, "CHEFRPG43 ppUpscale=", l => ppBool(l, ppGetter("get_upscaleRT", b)));
            AppendDiag(code, module, debugLog, "CHEFRPG43 ppSnap=", l => ppBool(l, ppGetter("get_pixelSnapping", b)));
        }
        else
        {
            Console.WriteLine("    [!] camera diag: GetComponent<T> reference not found, ppu values will be missing");
        }

        code.Add(skipPpu);
        code.Add(skip);

        var il = body.GetILProcessor();
        var entry = body.Instructions[0];
        foreach (var ins in code) il.InsertBefore(entry, ins);
        ExpandShortBranches(upd);
        Console.WriteLine("    [OK] LetterBoxer.Update -> runtime camera/pixel-perfect dump (every 300 frames)");
    }

    static readonly string[] TOOLTIPS =
    {
        "AwardTooltip", "BlueprintTooltip", "BrewingProductTooltip", "ConsumableTooltip",
        "CustomerTooltip", "CompostTooltip", "EquipmentToolTip", "FarmedPlantTooltip",
        "FestivalItemTooltip", "FishFarmStarterTooltip", "FurnitureTooltip", "GiftTooltip",
        "IngredientTooltip", "MaterialTooltip", "NoteTooltip", "PaperRecipeTooltip",
        "PetTooltip", "PlantTooltip", "QuestInfoTooltip", "SeedTooltip", "TakeoutTooltip"
    };

    /// <summary>
    /// tooltips compute a screen-space offset with `x = screenSize.x * 9/16` (or the
    /// inverse) to keep themselves on screen. rewriting 9/16 -> 3/4 makes that
    /// expression evaluate to Screen.width on a 4:3 display, so offsets go to zero.
    /// </summary>
    static void FixTooltips(ModuleDefinition module)
    {
        int touched = 0, scaled = 0;
        foreach (var name in TOOLTIPS.Concat(new[] { "StaffTooltip", "AnimalTooltip" }))
        {
            var type = module.Types.FirstOrDefault(t => t.Name == name);
            if (type == null) continue;
            foreach (var method in type.Methods)
            {
                if (!method.HasBody) continue;
                if (RewriteNineSixteen(method)) touched++;
                RewriteAspectLiteral(method);
            }
        }
        Console.WriteLine($"    [OK] tooltips 9/16 -> 3/4 ({touched} methods, plus 1.777 literals)");
    }

    /// <summary>
    /// the game turns a 16:9 screen size into the ui scale with `size.y / 360` (the design
    /// box height), e.g. every tooltip's bottom cap: `rect.height * screenSize.y / 360`.
    /// once the patch makes that size 4:3 (Formulas.GetActualScreenSize, or a tooltip's
    /// screenSize after the 9/16 rewrite) the matching divisor is 480, the 4:3 canvas
    /// height: 960 / 360 put tooltip caps a third too low (a dark band under every item
    /// tooltip on the device) and over-scaled the farmers wager ui. only `ldc.r4 360; div`
    /// whose dividend is `.y` of one of those 4:3 sizes is touched.
    /// </summary>
    static void FixUiScale360(ModuleDefinition module)
    {
        var tooltips = new HashSet<string>(TOOLTIPS.Concat(new[] { "StaffTooltip", "AnimalTooltip" }));
        int n = 0;
        foreach (var type in module.Types)
        {
            // screenSize fields that now hold a 4:3 size: in the 9/16-rewritten tooltips,
            // or assigned straight from GetActualScreenSize
            var fourThree = new HashSet<string>();
            foreach (var m in type.Methods.Where(x => x.HasBody))
                foreach (var ins in m.Body.Instructions)
                    if (ins.OpCode == OpCodes.Stfld && ins.Operand is FieldReference f && f.FieldType.Name == "Vector2"
                        && (tooltips.Contains(type.Name) || IsActualScreenSize(ins.Previous)))
                        fourThree.Add(f.Name);

            foreach (var m in type.Methods.Where(x => x.HasBody))
                foreach (var ins in m.Body.Instructions)
                {
                    if (ins.OpCode != OpCodes.Ldc_R4 || !Is(ins, 360f) || ins.Next?.OpCode != OpCodes.Div) continue;
                    // `size.y / 360`, or the tooltips' `(rect.height * size.y) / 360`
                    var y = ins.Previous?.OpCode == OpCodes.Mul ? ins.Previous.Previous : ins.Previous;
                    if (y == null || y.OpCode != OpCodes.Ldfld || !(y.Operand is FieldReference yf)
                        || yf.Name != "y" || yf.DeclaringType.Name != "Vector2") continue;
                    var src = y.Previous;
                    bool fromActual = IsActualScreenSize(src);
                    bool fromField = src != null && (src.OpCode == OpCodes.Ldflda || src.OpCode == OpCodes.Ldfld)
                        && src.Operand is FieldReference sf && fourThree.Contains(sf.Name);
                    if (!fromActual && !fromField) continue;
                    ins.Operand = 480f;
                    n++;
                }
        }
        Console.WriteLine($"    [OK] ui scale size.y / 360 -> / 480 on 4:3 sizes ({n} sites)");
    }

    static bool IsActualScreenSize(Instruction ins) =>
        ins != null && (ins.OpCode == OpCodes.Call || ins.OpCode == OpCodes.Callvirt) && ins.Operand is MethodReference mr
        && mr.Name == "GetActualScreenSize" && mr.DeclaringType.Name == "Formulas";

    /// <summary>
    /// every dialogue box manager scales its chat frame against a hardcoded 16:9.
    /// </summary>
    static void FixDialogue(ModuleDefinition module)
    {
        string[] managers =
        {
            "ChefDialogueManager", "CustomerDialogueManager", "MajorNPCDialogueManager",
            "MinorNPCDialogueManager", "PlayerDialogueManager", "RandomNPCDialogueManager",
            "WaiterDialogueManager", "MidsummerDialogueController"
        };

        int touched = 0;
        foreach (var name in managers)
        {
            var type = module.Types.FirstOrDefault(t => t.Name == name);
            if (type == null) continue;
            foreach (var method in type.Methods)
            {
                if (!method.HasBody) continue;
                if (RewriteNineSixteen(method)) touched++;
                RewriteAspectLiteral(method);
            }
        }
        Console.WriteLine($"    [OK] dialogue scale -> 3/4 ({touched} methods)");
    }

    static void FixUILineRenderer(ModuleDefinition module)
    {
        var type = module.Types.FirstOrDefault(t => t.Name == "UILineRenderer");
        if (type == null) { Console.WriteLine("    [!] UILineRenderer not found"); return; }
        int touched = 0;
        foreach (var method in type.Methods)
        {
            if (!method.HasBody) continue;
            bool literal = RewriteAspectLiteral(method);
            bool nine = RewriteNineSixteen(method);
            if (literal || nine) touched++;
        }
        Console.WriteLine($"    [OK] UILineRenderer aspect literal -> 4/3 ({touched} methods)");
    }

    /// <summary>
    /// finds `push 9; mul; push 16; div` / `push 16; mul; push 9; div` pairs and
    /// swaps them to 3/4 and 4/3 respectively.
    /// </summary>
    static bool RewriteNineSixteen(MethodDefinition method)
    {
        bool changed = false;
        var instrs = method.Body.Instructions;
        for (int i = 0; i < instrs.Count - 3; i++)
        {
            if (instrs[i].OpCode == OpCodes.Ldc_R4 && Is(instrs[i], 9f) &&
                instrs[i + 1].OpCode == OpCodes.Mul &&
                instrs[i + 2].OpCode == OpCodes.Ldc_R4 && Is(instrs[i + 2], 16f) &&
                instrs[i + 3].OpCode == OpCodes.Div)
            {
                instrs[i].Operand = 3f;
                instrs[i + 2].Operand = 4f;
                changed = true;
            }
            else if (instrs[i].OpCode == OpCodes.Ldc_R4 && Is(instrs[i], 16f) &&
                     instrs[i + 1].OpCode == OpCodes.Mul &&
                     instrs[i + 2].OpCode == OpCodes.Ldc_R4 && Is(instrs[i + 2], 9f) &&
                     instrs[i + 3].OpCode == OpCodes.Div)
            {
                instrs[i].Operand = 4f;
                instrs[i + 2].Operand = 3f;
                changed = true;
            }
        }
        return changed;
    }

    static bool RewriteAspectLiteral(MethodDefinition method)
    {
        bool changed = false;
        foreach (var ins in method.Body.Instructions)
        {
            if (ins.OpCode == OpCodes.Ldc_R4 && Math.Abs(Convert.ToSingle(ins.Operand) - 1.7777778f) < 0.0001f)
            {
                ins.Operand = 4f / 3f;
                changed = true;
            }
        }
        return changed;
    }

    static bool Is(Instruction ins, float v) => Math.Abs(Convert.ToSingle(ins.Operand) - v) < 0.0001f;

    /// <summary>
    /// the real fix for fullscreen: the game picks Screen.resolutions[last] (a 16:9
    /// mode) and renders into a 16:9 framebuffer that the 4:3 panel shows with bars.
    /// render at the display's native size instead. we also force fullscreen at boot
    /// so a stale windowed/resolution choice in the save data cannot undo it.
    /// </summary>
    static MethodReference FindConcat(ModuleDefinition module, string p0, string p1)
    {
        foreach (var mr in module.GetMemberReferences().OfType<MethodReference>())
            if (mr.DeclaringType.Name == "String" && mr.Name == "Concat" && mr.Parameters.Count == 2
                && mr.Parameters[0].ParameterType.Name == p0 && mr.Parameters[1].ParameterType.Name == p1)
                return mr;
        return null;
    }

    /// <summary>
    /// the display policy, wired from the cloned helper (DisplayPolicy.cs + the Apply*
    /// wrappers in ChefRPG43SafeArea.cs) instead of hand-written il:
    ///   - fullscreen is borderless at the panel's native size. the game picked
    ///     Screen.resolutions[last], a 16:9 mode the 4:3 panel shows with bars, and
    ///     raising Screen.fullScreen first made the container pin an exclusive 16:9 mode.
    ///   - windowed is a real 4:3 window. the game's own windowed paths are kept and only
    ///     their raw Screen.SetResolution(w, h, false) is swapped for ApplyWindowed, which
    ///     coerces to 4:3 and tells the watchdog that a window is what we want. v5.6
    ///     redirected them to fullscreen, which is why the Windowed button did nothing.
    ///   - the resolution picker lists 4:3 sizes only (Resolutions43 replaces
    ///     Screen.resolutions), and a saved 16:9 size is coerced to 4:3 on load.
    /// the helper type has to exist already, so InjectSafeAreaHelper runs first.
    /// </summary>
    static void ApplyDisplayPolicy(ModuleDefinition module, MethodReference debugLog,
        MethodReference getMain, MethodReference getSysW, MethodReference getSysH,
        MethodReference screenGetFullScreen)
    {
        var settings = module.Types.FirstOrDefault(t => t.Name == "SettingsPanel");
        if (settings == null) { Console.WriteLine("    [!] SettingsPanel not found"); return; }

        var helper = module.Types.FirstOrDefault(t => t.Name == "ChefRPG43SafeArea");
        if (helper == null) throw new InvalidOperationException("ChefRPG43SafeArea must be injected before the display policy is wired");
        MethodDefinition Helper_(string name, int args) =>
            helper.Methods.First(m => m.Name == name && m.Parameters.Count == args);
        var applyFullscreen = Helper_("ApplyFullscreen", 0);
        var applyWindowed = Helper_("ApplyWindowed", 2);
        var resolutions43 = Helper_("Resolutions43", 0);
        var coerce43 = Helper_("Coerce43", 1);

        var screenGetResolutions = Helper.FindMethod(module, "Screen", "get_resolutions", 0);
        var screenGetWidth = Helper.FindMethod(module, "Screen", "get_width", 0);
        var screenGetHeight = Helper.FindMethod(module, "Screen", "get_height", 0);
        var pss = module.Types.FirstOrDefault(t => t.Name == "PlayStaticStats");
        var currentResField = pss?.Fields.FirstOrDefault(f => f.Name == "CurrentResolution");

        var fs = settings.Methods.FirstOrDefault(m => m.Name == "FullscreenResolution");
        if (fs != null)
        {
            fs.Body.Instructions.Clear();
            fs.Body.Variables.Clear();
            fs.Body.InitLocals = false;
            var il = fs.Body.GetILProcessor();

            // --- breadcrumbs: what does the container actually report? ---
            EmitDiag(il, module, debugLog, "CHEFRPG43 sysW=", x => DisplayInt(x, getMain, getSysW));
            EmitDiag(il, module, debugLog, "CHEFRPG43 sysH=", x => DisplayInt(x, getMain, getSysH));
            EmitDiag(il, module, debugLog, "CHEFRPG43 scrW=", x => x.Append(x.Create(OpCodes.Call, screenGetWidth)));
            EmitDiag(il, module, debugLog, "CHEFRPG43 scrH=", x => x.Append(x.Create(OpCodes.Call, screenGetHeight)));
            if (screenGetResolutions != null)
                EmitDiag(il, module, debugLog, "CHEFRPG43 modes=", x =>
                {
                    x.Append(x.Create(OpCodes.Call, screenGetResolutions));
                    x.Append(x.Create(OpCodes.Ldlen));
                    x.Append(x.Create(OpCodes.Conv_I4));
                });

            // borderless fullscreen at the display's size, no mode switch. CurrentResolution
            // is deliberately left alone: it is the remembered windowed size, and writing the
            // native size into it made every settings close save 1280x960 as "windowed".
            il.Append(il.Create(OpCodes.Call, applyFullscreen));

            EmitDiag(il, module, debugLog, "CHEFRPG43 newScrW=", x => x.Append(x.Create(OpCodes.Call, screenGetWidth)));
            EmitDiag(il, module, debugLog, "CHEFRPG43 newScrH=", x => x.Append(x.Create(OpCodes.Call, screenGetHeight)));

            il.Append(il.Create(OpCodes.Ret));
            Console.WriteLine("    [OK] SettingsPanel.FullscreenResolution -> ChefRPG43SafeArea.ApplyFullscreen + diagnostics");
        }

        var load = settings.Methods.FirstOrDefault(m => m.Name == "LoadSettings");
        if (load != null && load.HasBody)
        {
            // the saved resolution is whatever the player picked from a 16:9 list. coerce it
            // to 4:3 before it lands in CurrentResolution, so the panel label and the window
            // agree. the value is read from local 0 right before the stsfld, so the coercion
            // goes in front of that load, and any branch that jumped straight at it is
            // pointed at the new head (the v5.5 bug mutated the local after the load).
            var setResStore = currentResField == null ? null : load.Body.Instructions.FirstOrDefault(i =>
                i.OpCode == OpCodes.Stsfld && i.Operand == currentResField);
            var valueLoad = setResStore?.Previous;
            if (valueLoad != null && valueLoad.OpCode == OpCodes.Ldloc_0)
            {
                var v0 = load.Body.Variables[0];
                var ilLoad = load.Body.GetILProcessor();
                var first = ilLoad.Create(OpCodes.Ldloc, v0);
                ilLoad.InsertBefore(valueLoad, first);
                ilLoad.InsertBefore(valueLoad, ilLoad.Create(OpCodes.Call, coerce43));
                ilLoad.InsertBefore(valueLoad, ilLoad.Create(OpCodes.Stloc, v0));
                RetargetBranches(load, valueLoad, first);
                Console.WriteLine("    [OK] SettingsPanel.LoadSettings -> CurrentResolution = Coerce43(saved resolution)");
            }
            else Console.WriteLine("    [!] SettingsPanel.LoadSettings: CurrentResolution store not found, saved size left as is");

            // the windowed branch: `Screen.fullScreen = false; Screen.SetResolution(w, h, false)`.
            // raising/lowering fullScreen separately is what upset the container, so both
            // calls go and ApplyWindowed does the whole job. the branch targets that point at
            // the first dropped instruction keep working because the slots become nops.
            int dropped = NopSetFullScreen(load);
            int redirected = RedirectSetResolution(load, applyWindowed);
            ExpandShortBranches(load);
            Console.WriteLine($"    [OK] SettingsPanel.LoadSettings -> windowed branch uses ApplyWindowed ({dropped} set_fullScreen dropped, {redirected} SetResolution redirected)");
        }

        // WindowedMode and SetResolutionValue keep their original bodies (button sprites,
        // label, popup close); only the raw SetResolution inside becomes ApplyWindowed.
        foreach (var name in new[] { "WindowedMode", "SetResolutionValue" })
        {
            var m = settings.Methods.FirstOrDefault(x => x.Name == name);
            if (m == null || !m.HasBody) { Console.WriteLine($"    [!] SettingsPanel.{name} not found"); continue; }
            int n = RedirectSetResolution(m, applyWindowed);
            ExpandShortBranches(m);
            if (n == 0) Console.WriteLine($"    [!] SettingsPanel.{name}: SetResolution call not found");
            else Console.WriteLine($"    [OK] SettingsPanel.{name} -> original body, SetResolution -> ApplyWindowed");
        }

        // the picker reads Screen.resolutions, which under the container is all 16:9. the
        // game's own filtering, labels and buttons then run over the 4:3 list.
        var change = settings.Methods.FirstOrDefault(m => m.Name == "ChangeResolution");
        if (change != null && change.HasBody)
        {
            int swapped = 0;
            foreach (var ins in change.Body.Instructions)
                if (ins.OpCode == OpCodes.Call && ins.Operand is MethodReference mr
                    && mr.Name == "get_resolutions" && mr.DeclaringType.Name == "Screen")
                {
                    ins.Operand = resolutions43;
                    swapped++;
                }
            ExpandShortBranches(change);
            Console.WriteLine($"    [{(swapped > 0 ? "OK" : "!")}] SettingsPanel.ChangeResolution -> Resolutions43 ({swapped} get_resolutions swapped)");
        }
        else Console.WriteLine("    [!] SettingsPanel.ChangeResolution not found");

        // first title frame: correct the resolution before Character.Start -> LoadSettings
        // runs, instead of showing a stale 16:9 window until then.
        var smm = module.Types.FirstOrDefault(t => t.Name == "StartMenuManager");
        var awake = smm?.Methods.FirstOrDefault(m => m.Name == "Awake" && m.Parameters.Count == 0 && m.HasBody);
        var watchdog = Helper_("DisplayWatchdog", 0);
        if (awake != null)
        {
            var il = awake.Body.GetILProcessor();
            var head = awake.Body.Instructions[0];
            var call = il.Create(OpCodes.Call, watchdog);
            il.InsertBefore(head, call);
            RetargetBranches(awake, head, call);
            ExpandShortBranches(awake);
            Console.WriteLine("    [OK] StartMenuManager.Awake -> DisplayWatchdog at the head (4:3 on the first title frame)");
        }
        else Console.WriteLine("    [!] StartMenuManager.Awake not found");

        // one-shot reading taken on the first panel Update, i.e. after a frame has
        // actually elapsed - Screen.width/height read immediately after
        // SetResolution are stale because the change lands at end of frame.
        var upd = settings.Methods.FirstOrDefault(m => m.Name == "Update" && m.Parameters.Count == 0);
        if (upd != null && upd.HasBody && screenGetFullScreen != null && screenGetWidth != null && screenGetHeight != null)
        {
            var flag = settings.Fields.FirstOrDefault(f => f.Name == "_chefrpg43Logged");
            if (flag == null)
            {
                flag = new FieldDefinition("_chefrpg43Logged",
                    FieldAttributes.Private | FieldAttributes.Static, module.TypeSystem.Boolean);
                settings.Fields.Add(flag);
            }

            var il = upd.Body.GetILProcessor();
            var anchor = upd.Body.Instructions.LastOrDefault(i => i.OpCode == OpCodes.Ret)
                ?? upd.Body.Instructions[upd.Body.Instructions.Count - 1];

            var skip = Instruction.Create(OpCodes.Nop);
            var code = new List<Instruction>
            {
                Instruction.Create(OpCodes.Ldsfld, flag),
                Instruction.Create(OpCodes.Brtrue, skip),
                Instruction.Create(OpCodes.Ldc_I4_1),
                Instruction.Create(OpCodes.Stsfld, flag)
            };
            AppendDiag(code, module, debugLog, "CHEFRPG43 lateScrW=", l => l.Add(Instruction.Create(OpCodes.Call, screenGetWidth)));
            AppendDiag(code, module, debugLog, "CHEFRPG43 lateScrH=", l => l.Add(Instruction.Create(OpCodes.Call, screenGetHeight)));
            AppendDiag(code, module, debugLog, "CHEFRPG43 lateFull=", l =>
            {
                var isTrue = Instruction.Create(OpCodes.Ldc_I4_1);
                var end = Instruction.Create(OpCodes.Nop);
                l.Add(Instruction.Create(OpCodes.Call, screenGetFullScreen));
                l.Add(Instruction.Create(OpCodes.Brtrue, isTrue));
                l.Add(Instruction.Create(OpCodes.Ldc_I4_0));
                l.Add(Instruction.Create(OpCodes.Br, end));
                l.Add(isTrue);
                l.Add(end);
            });
            code.Add(skip);

            foreach (var ins in code) il.InsertBefore(anchor, ins);
            ExpandShortBranches(upd);
            Console.WriteLine("    [OK] SettingsPanel.Update -> one-shot late resolution diagnostic");
        }
    }

    /// <summary>
    /// swaps `Screen.SetResolution(int, int, bool)` for `target(int, int)` in a method. the
    /// trailing bool constant becomes a nop in place (not removed), so a branch that
    /// targeted it still lands on a valid instruction. returns how many calls were swapped.
    /// </summary>
    static int RedirectSetResolution(MethodDefinition method, MethodReference target)
    {
        int swapped = 0;
        foreach (var ins in method.Body.Instructions)
        {
            if (ins.OpCode != OpCodes.Call || !(ins.Operand is MethodReference mr)) continue;
            if (mr.Name != "SetResolution" || mr.DeclaringType.Name != "Screen" || mr.Parameters.Count != 3) continue;
            if (mr.Parameters[2].ParameterType.Name != "Boolean") continue;

            var flag = ins.Previous;
            if (flag == null || (flag.OpCode != OpCodes.Ldc_I4_0 && flag.OpCode != OpCodes.Ldc_I4_1))
                throw new InvalidOperationException($"{method.Name}: SetResolution's bool argument is not a constant ({flag?.OpCode})");
            flag.OpCode = OpCodes.Nop;
            flag.Operand = null;
            ins.Operand = target;
            swapped++;
        }
        return swapped;
    }

    /// <summary>
    /// drops `Screen.fullScreen = <const>` (the constant load and the setter call) by turning
    /// both into nops in place.
    /// </summary>
    static int NopSetFullScreen(MethodDefinition method)
    {
        int dropped = 0;
        foreach (var ins in method.Body.Instructions)
        {
            if (ins.OpCode != OpCodes.Call || !(ins.Operand is MethodReference mr)) continue;
            if (mr.Name != "set_fullScreen" || mr.DeclaringType.Name != "Screen") continue;

            var flag = ins.Previous;
            if (flag == null || (flag.OpCode != OpCodes.Ldc_I4_0 && flag.OpCode != OpCodes.Ldc_I4_1))
                throw new InvalidOperationException($"{method.Name}: set_fullScreen's argument is not a constant ({flag?.OpCode})");
            flag.OpCode = OpCodes.Nop;
            flag.Operand = null;
            ins.OpCode = OpCodes.Nop;
            ins.Operand = null;
            dropped++;
        }
        return dropped;
    }

    /// <summary>
    /// gamescope may not yet expose the 4:3 mode when the game's boot-time
    /// SetResolution runs, in which case Unity silently clamps to the closest
    /// enumerated mode (16:9) and the frame stays letterboxed forever. every ~2s,
    /// from LetterBoxer.Update (present in every scene), the helper's watchdog compares
    /// the screen with what the last fullscreen/windowed choice asked for and re-requests
    /// it only if they differ. the policy lives in the helper (DisplayPolicy.NextRequest),
    /// not in il, so the verifier's simulator exercises the same code.
    /// </summary>
    static void AddFullscreenRetry(ModuleDefinition module)
    {
        var lb = module.Types.FirstOrDefault(t => t.Name == "LetterBoxer");
        var upd = lb?.Methods.FirstOrDefault(m => m.Name == "Update" && m.Parameters.Count == 0 && m.HasBody);
        var helper = module.Types.FirstOrDefault(t => t.Name == "ChefRPG43SafeArea");
        var watchdog = helper?.Methods.FirstOrDefault(m => m.Name == "DisplayWatchdog" && m.Parameters.Count == 0);
        if (lb == null || upd == null || watchdog == null)
        {
            Console.WriteLine("    [!] display watchdog: missing LetterBoxer.Update / ChefRPG43SafeArea.DisplayWatchdog");
            return;
        }

        // before the final ret, and every branch that used to skip to that ret now runs it
        // too: the original `if (onUpdate)` guard must not be able to switch the watchdog off
        var il = upd.Body.GetILProcessor();
        var anchor = upd.Body.Instructions.LastOrDefault(i => i.OpCode == OpCodes.Ret)
            ?? upd.Body.Instructions[upd.Body.Instructions.Count - 1];
        var call = il.Create(OpCodes.Call, watchdog);
        il.InsertBefore(anchor, call);
        RetargetBranches(upd, anchor, call);
        ExpandShortBranches(upd);
        Console.WriteLine("    [OK] LetterBoxer.Update -> ChefRPG43SafeArea.DisplayWatchdog (permanent)");
    }

    /// <summary>
    /// list-based twin of EmitDiag, for injecting before an anchor instead of
    /// appending to the end of a method body.
    /// </summary>
    static void AppendDiag(List<Instruction> code, ModuleDefinition module, MethodReference debugLog, string label,
        Action<List<Instruction>> loadInt)
    {
        code.Add(Instruction.Create(OpCodes.Ldstr, label));
        code.Add(Instruction.Create(OpCodes.Call, debugLog));
        loadInt(code);
        code.Add(Instruction.Create(OpCodes.Box, module.TypeSystem.Int32));
        code.Add(Instruction.Create(OpCodes.Call, debugLog));

        if (diagAppPath == null || diagConcatSS == null || diagConcatOO == null || diagAppendAllText == null)
            return;

        code.Add(Instruction.Create(OpCodes.Call, diagAppPath));
        code.Add(Instruction.Create(OpCodes.Ldstr, "/chefrpg43.txt"));
        code.Add(Instruction.Create(OpCodes.Call, diagConcatSS));
        code.Add(Instruction.Create(OpCodes.Ldstr, label));
        loadInt(code);
        code.Add(Instruction.Create(OpCodes.Box, module.TypeSystem.Int32));
        code.Add(Instruction.Create(OpCodes.Call, diagConcatOO));
        code.Add(Instruction.Create(OpCodes.Ldstr, "\n"));
        code.Add(Instruction.Create(OpCodes.Call, diagConcatSS));
        code.Add(Instruction.Create(OpCodes.Call, diagAppendAllText));
    }

    /// <summary>
    /// string-valued twin of AppendDiag, for values that are already strings.
    /// </summary>
    static void AppendStrDiag(List<Instruction> code, MethodReference debugLog, string label,
        Action<List<Instruction>> loadStr)
    {
        code.Add(Instruction.Create(OpCodes.Ldstr, label));
        code.Add(Instruction.Create(OpCodes.Call, debugLog));
        loadStr(code);
        code.Add(Instruction.Create(OpCodes.Call, debugLog));

        if (diagAppPath == null || diagConcatSS == null || diagAppendAllText == null)
            return;

        code.Add(Instruction.Create(OpCodes.Call, diagAppPath));
        code.Add(Instruction.Create(OpCodes.Ldstr, "/chefrpg43.txt"));
        code.Add(Instruction.Create(OpCodes.Call, diagConcatSS));
        code.Add(Instruction.Create(OpCodes.Ldstr, label));
        loadStr(code);
        code.Add(Instruction.Create(OpCodes.Call, diagConcatSS));
        code.Add(Instruction.Create(OpCodes.Ldstr, "\n"));
        code.Add(Instruction.Create(OpCodes.Call, diagConcatSS));
        code.Add(Instruction.Create(OpCodes.Call, diagAppendAllText));
    }

    static bool IsAddressOf(Instruction ins) =>
        ins.OpCode == OpCodes.Ldsflda || ins.OpCode == OpCodes.Ldloca || ins.OpCode == OpCodes.Ldloca_S;

    static void DisplayInt(ILProcessor il, MethodReference getMain, MethodReference getter)
    {
        il.Append(il.Create(OpCodes.Call, getMain));
        il.Append(il.Create(OpCodes.Callvirt, getter));
    }

    /// <summary>
    /// emits `Debug.Log(label); Debug.Log(value);` plus an append to
    /// &lt;persistentDataPath&gt;/chefrpg43.txt so we can read the numbers off the device
    /// without hunting for Player.log.
    /// </summary>
    static void EmitDiag(ILProcessor il, ModuleDefinition module, MethodReference debugLog, string label,
        Action<ILProcessor> loadInt)
    {
        il.Append(il.Create(OpCodes.Ldstr, label));
        il.Append(il.Create(OpCodes.Call, debugLog));
        loadInt(il);
        il.Append(il.Create(OpCodes.Box, module.TypeSystem.Int32));
        il.Append(il.Create(OpCodes.Call, debugLog));

        if (diagAppPath == null || diagConcatSS == null || diagConcatOO == null || diagAppendAllText == null)
            return;

        il.Append(il.Create(OpCodes.Call, diagAppPath));
        il.Append(il.Create(OpCodes.Ldstr, "/chefrpg43.txt"));
        il.Append(il.Create(OpCodes.Call, diagConcatSS));
        il.Append(il.Create(OpCodes.Ldstr, label));
        loadInt(il);
        il.Append(il.Create(OpCodes.Box, module.TypeSystem.Int32));
        il.Append(il.Create(OpCodes.Call, diagConcatOO));
        il.Append(il.Create(OpCodes.Ldstr, "\n"));
        il.Append(il.Create(OpCodes.Call, diagConcatSS));
        il.Append(il.Create(OpCodes.Call, diagAppendAllText));
    }

    /// <summary>
    /// Clones the ChefRPG43SafeArea helper type from the embedded SafeHelper.dll into
    /// the target assembly and hooks ClampCanvasesTo43 into LetterBoxer.Update.
    /// the helper walks root overlay canvases and nudges panels that sit outside the
    /// 4:3 view back inside. no reparenting, no second camera.
    ///
    /// the tricky part is self-references: any call the helper makes to its own
    /// static methods must be rewritten to the cloned type. leaving it as an
    /// ImportReference binds the call to SafeHelper.dll, which does not exist in the
    /// game folder, and every frame throws FileNotFoundException - the last few
    /// builds died exactly that way, which is why nothing changed on screen.
    /// </summary>
    static void InjectSafeAreaHelper(ModuleDefinition targetModule)
    {
        var helperBytes = ReadHelperAssembly();
        if (helperBytes == null)
            throw new FileNotFoundException("embedded SafeHelper.dll payload missing from the patcher binary");

        // the helper is only read and cloned, never resolved, so it gets no search dirs
        var readerParams = new ReaderParameters { AssemblyResolver = new NullAssemblyResolver() };

        using (var ms = new MemoryStream(helperBytes))
        using (var helperAsm = AssemblyDefinition.ReadAssembly(ms, readerParams))
        {
            var srcType = helperAsm.MainModule.Types.First(t => t.Name == "ChefRPG43SafeArea");

            // Create new type in targetModule
            var newType = new TypeDefinition(srcType.Namespace, srcType.Name, srcType.Attributes, targetModule.TypeSystem.Object);
            targetModule.Types.Add(newType);

            // Copy fields
            var fieldMap = new Dictionary<string, FieldDefinition>();
            foreach (var f in srcType.Fields)
            {
                // consts are inlined by the compiler and nothing references them; cloning
                // the literal without its constant row would leave a malformed field
                if (f.IsLiteral) continue;
                var newField = new FieldDefinition(f.Name, f.Attributes, targetModule.ImportReference(f.FieldType));
                newType.Fields.Add(newField);
                fieldMap[f.Name] = newField;
            }

            // pass 1: declare every method and parameter before any body is copied.
            // a self-call from ClampCanvasesTo43 to Walk can only resolve once Walk
            // exists on the clone - otherwise the mapping falls back to an import of
            // SafeHelper.dll (which is not in the game folder) and every frame throws
            // FileNotFoundException, which is exactly how the earlier builds died.
            foreach (var m in srcType.Methods)
            {
                var decl = new MethodDefinition(m.Name, m.Attributes, targetModule.ImportReference(m.ReturnType));
                foreach (var p in m.Parameters)
                    decl.Parameters.Add(new ParameterDefinition(p.Name, p.Attributes, targetModule.ImportReference(p.ParameterType)));
                newType.Methods.Add(decl);
            }

            // pass 2: copy the bodies, resolving self references against the clones
            for (int mi = 0; mi < srcType.Methods.Count; mi++)
            {
                var m = srcType.Methods[mi];
                var newMethod = newType.Methods[mi];
                if (!m.HasBody) continue;

                newMethod.Body.InitLocals = m.Body.InitLocals;
                newMethod.Body.MaxStackSize = m.Body.MaxStackSize;

                var varMap = new Dictionary<VariableDefinition, VariableDefinition>();
                foreach (var v in m.Body.Variables)
                {
                    var newVar = new VariableDefinition(targetModule.ImportReference(v.VariableType));
                    newMethod.Body.Variables.Add(newVar);
                    varMap[v] = newVar;
                }

                var il = newMethod.Body.GetILProcessor();
                var insMap = new Dictionary<Instruction, Instruction>();

                foreach (var ins in m.Body.Instructions)
                {
                    var dummy = Instruction.Create(OpCodes.Nop);
                    insMap[ins] = dummy;
                    il.Append(dummy);
                }

                for (int idx = 0; idx < m.Body.Instructions.Count; idx++)
                {
                    var ins = m.Body.Instructions[idx];
                    var newIns = newMethod.Body.Instructions[idx];
                    newIns.OpCode = ins.OpCode;

                    if (ins.Operand == null)
                    {
                        newIns.Operand = null;
                    }
                    else if (ins.Operand is FieldReference fr)
                    {
                        if (fr.DeclaringType != null && fr.DeclaringType.FullName == srcType.FullName
                            && fieldMap.TryGetValue(fr.Name, out var mappedField))
                            newIns.Operand = mappedField;
                        else
                            newIns.Operand = targetModule.ImportReference(fr);
                    }
                    else if (ins.Operand is MethodReference mr)
                    {
                        newIns.Operand = MapHelperMethod(targetModule, srcType, newType, mr);
                    }
                    else if (ins.Operand is TypeReference tr)
                    {
                        newIns.Operand = tr.FullName == srcType.FullName ? (TypeReference)newType : targetModule.ImportReference(tr);
                    }
                    else if (ins.Operand is VariableDefinition vd)
                    {
                        newIns.Operand = varMap[vd];
                    }
                    else if (ins.Operand is ParameterDefinition pd)
                    {
                        newIns.Operand = newMethod.Parameters[pd.Index];
                    }
                    else if (ins.Operand is Instruction targetIns)
                    {
                        newIns.Operand = insMap[targetIns];
                    }
                    else if (ins.Operand is Instruction[] targetInss)
                    {
                        newIns.Operand = targetInss.Select(t => insMap[t]).ToArray();
                    }
                    else
                    {
                        newIns.Operand = ins.Operand;
                    }
                }

                // the try/catch blocks of the helper (reflection probes, screenshot capture,
                // property setters) live in the exception table, not in the instruction
                // stream. copying only instructions silently stripped every catch, so one
                // reflection exception aborted the whole clamp for that frame. map each
                // boundary through insMap; a null TryEnd/HandlerEnd means "end of body".
                Instruction MapIns(Instruction i) => i == null ? null : insMap[i];
                foreach (var eh in m.Body.ExceptionHandlers)
                {
                    var neh = new ExceptionHandler(eh.HandlerType)
                    {
                        TryStart = MapIns(eh.TryStart),
                        TryEnd = MapIns(eh.TryEnd),
                        HandlerStart = MapIns(eh.HandlerStart),
                        HandlerEnd = MapIns(eh.HandlerEnd),
                        FilterStart = MapIns(eh.FilterStart),
                        CatchType = eh.CatchType == null ? null : targetModule.ImportReference(eh.CatchType),
                    };
                    newMethod.Body.ExceptionHandlers.Add(neh);
                }
            }

            // Hook the clamp into LateUpdate only. it used to run from Update *and*
            // LateUpdate (twice per frame) and walked the whole canvas hierarchy, so a
            // 228 rect hud cost 6+ms per frame and dirtied rects the game had just
            // positioned. LateUpdate runs after every game Update, so shifts land
            // before rendering and only rects that actually cross the view move.
            var lb = targetModule.Types.First(t => t.Name == "LetterBoxer");
            var clampMethod = newType.Methods.First(x => x.Name == "ClampCanvasesTo43");
            var late = lb.Methods.FirstOrDefault(x => x.Name == "LateUpdate" && x.Parameters.Count == 0);
            if (late == null)
            {
                late = new MethodDefinition("LateUpdate",
                    MethodAttributes.Private | MethodAttributes.HideBySig, targetModule.TypeSystem.Void);
                late.Body.GetILProcessor().Append(Instruction.Create(OpCodes.Ret));
                lb.Methods.Add(late);
                Console.WriteLine("    [OK] LetterBoxer.LateUpdate -> added");
            }
            var lateIl = late.Body.GetILProcessor();
            var lateRet = late.Body.Instructions.LastOrDefault(i => i.OpCode == OpCodes.Ret);
            if (lateRet != null)
                lateIl.InsertBefore(lateRet, lateIl.Create(OpCodes.Call, clampMethod));
            else
                lateIl.Append(lateIl.Create(OpCodes.Call, clampMethod));
            ExpandShortBranches(late);

            Console.WriteLine("    [OK] ChefRPG43SafeArea -> injected and hooked into LetterBoxer.LateUpdate");
        }
    }

    /// <summary>
    /// the clamp helper ships inside the patcher binary as an embedded resource, so a
    /// downloaded patcher runs on a machine that has never built the helper and does
    /// not depend on any /tmp path.
    /// </summary>
    internal static byte[] ReadHelperAssembly()
    {
        var asm = System.Reflection.Assembly.GetExecutingAssembly();
        foreach (var name in asm.GetManifestResourceNames())
        {
            if (!name.EndsWith("SafeHelper.dll", StringComparison.Ordinal)) continue;
            using (var s = asm.GetManifestResourceStream(name))
            {
                if (s == null) continue;
                using (var ms = new MemoryStream())
                {
                    s.CopyTo(ms);
                    return ms.ToArray();
                }
            }
        }
        return null;
    }

    /// <summary>
    /// maps a method operand onto the clone: calls inside the helper class resolve to
    /// the cloned type, everything else imports normally. generic instantiations are
    /// preserved (none in the current helper, but cheap insurance).
    /// </summary>
    static MethodReference MapHelperMethod(ModuleDefinition target, TypeDefinition src, TypeDefinition dst, MethodReference mr)
    {
        var element = (mr as GenericInstanceMethod)?.ElementMethod ?? mr;
        if (element.DeclaringType != null && element.DeclaringType.FullName == src.FullName)
        {
            // overloads share a name (Coerce43 has a six-int and a Resolution form), so the
            // parameter types have to match too, not just the count
            var match = dst.Methods.FirstOrDefault(m => m.Name == element.Name
                && m.Parameters.Count == element.Parameters.Count
                && m.Parameters.Select(p => p.ParameterType.FullName).SequenceEqual(
                    element.Parameters.Select(p => p.ParameterType.FullName)));
            if (match != null)
            {
                if (mr is GenericInstanceMethod gim)
                {
                    var ng = new GenericInstanceMethod(match);
                    foreach (var ga in gim.GenericArguments) ng.GenericArguments.Add(target.ImportReference(ga));
                    return ng;
                }
                return match;
            }
        }
        return target.ImportReference(mr);
    }

}
