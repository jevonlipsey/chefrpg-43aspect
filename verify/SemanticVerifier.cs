using System;
using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace ChefRPG43.Verify;

/// checks the intent of the patch, not just that the il parses: 4:3 defaults,
/// 640x480 / 800x600 zooms, crop+stretch setters, the safe-area hook, and that no
/// patched path can ask unity for a 16:9 reference or a 16:9 resolution.
public sealed class SemanticVerifier
{
    private readonly Checks checks;
    private readonly ModuleDefinition module;
    private readonly byte[]? helperPayload;
    private readonly Dictionary<TypeDefinition, List<MethodDefinition>> methodsWithSetResolution = new();

    // helperPayload is the SafeHelper.dll that was embedded into the patcher. the caller
    // supplies it (the standalone harness reads the file, the patcher passes its resource).
    public SemanticVerifier(Checks checks, ModuleDefinition module, byte[]? helperPayload)
    {
        this.checks = checks;
        this.module = module;
        this.helperPayload = helperPayload;
    }

    public void Run()
    {
        checks.Section("patch intent");

        var letterBoxer = RequireType("LetterBoxer");
        var cameraFollow = RequireType("CameraFollow");
        var formulas = RequireType("Formulas");
        var settings = RequireType("SettingsPanel");
        var helper = RequireType("ChefRPG43SafeArea");
        if (letterBoxer == null || cameraFollow == null || formulas == null || settings == null || helper == null)
            return;

        CheckLetterBoxer(letterBoxer, helper);
        CheckCameraFollow(cameraFollow);
        CheckFormulas(formulas);
        CheckFullscreen(settings);
        CheckHelper(helper);
        CheckNoAspectLiterals();
        CheckTooltipScale();
        CheckResolutionCalls();
        CheckPixelPerfectSetters();
    }

    // the game derives its ui scale as `size.y / 360` from a 16:9 size. wherever the patch
    // made that size 4:3 (GetActualScreenSize, a tooltip's screenSize once its 9/16 is
    // gone) the divisor must be 480, or tooltip bottom caps drop a third too low and leave
    // a dark band under the text (seen on the device). tooltips that keep `* 9f / 16f`
    // still hold a 16:9 size, where 720 / 360 is right.
    private void CheckTooltipScale()
    {
        int scaled = 0;
        var stale = new List<string>();
        foreach (var type in module.Types)
        {
            var bodies = type.Methods.Where(m => m.HasBody).ToList();
            bool sixteenNine = bodies.SelectMany(m => m.Body.Instructions)
                .Any(i => i.OpCode == OpCodes.Ldc_R4 && i.Operand is float f && Near(f, 9f, 0.0001f));
            var fourThree = new HashSet<string>();
            foreach (var ins in bodies.SelectMany(m => m.Body.Instructions))
                if (ins.OpCode == OpCodes.Stfld && ins.Operand is FieldReference f && f.FieldType.Name == "Vector2"
                    && (IsActualScreenSize(ins.Previous) || (type.Name.EndsWith("Tooltip") || type.Name.EndsWith("ToolTip")) && !sixteenNine))
                    fourThree.Add(f.Name);

            foreach (var m in bodies)
                foreach (var ins in m.Body.Instructions)
                {
                    if (ins.OpCode != OpCodes.Ldc_R4 || !(ins.Operand is float v) || ins.Next?.OpCode != OpCodes.Div) continue;
                    var y = ins.Previous?.OpCode == OpCodes.Mul ? ins.Previous.Previous : ins.Previous;
                    if (y?.OpCode != OpCodes.Ldfld || !(y.Operand is FieldReference yf) || yf.Name != "y") continue;
                    var src = y.Previous;
                    bool fourThreeSrc = IsActualScreenSize(src) ||
                        src != null && (src.OpCode == OpCodes.Ldflda || src.OpCode == OpCodes.Ldfld)
                        && src.Operand is FieldReference sf && fourThree.Contains(sf.Name);
                    if (!fourThreeSrc) continue;
                    if (Near(v, 480f, 0.001f)) scaled++;
                    else if (Near(v, 360f, 0.001f)) stale.Add($"{type.Name}.{m.Name}");
                }
        }
        checks.Check(stale.Count == 0,
            stale.Count == 0
                ? "ui scale on 4:3 sizes divides by 480 (tooltip bottom caps, farmers wager)"
                : $"ui scale still divides a 4:3 size by 360: {string.Join(", ", stale.Take(8))}");
        checks.Check(scaled >= 18, $"ui scale rewritten at {scaled} sites (16 tooltips + ToolsTooltip/QuestItemTooltip + farmers wager)");
    }

    private static bool IsActualScreenSize(Instruction? ins) =>
        ins != null && (ins.OpCode == OpCodes.Call || ins.OpCode == OpCodes.Callvirt) && ins.Operand is MethodReference mr
        && mr.Name == "GetActualScreenSize" && mr.DeclaringType.Name == "Formulas";

    private TypeDefinition? RequireType(string name)
    {
        var type = module.Types.FirstOrDefault(t => t.Name == name);
        checks.Check(type != null, $"type {name} exists");
        return type;
    }

    private void CheckLetterBoxer(TypeDefinition lb, TypeDefinition helper)
    {
        var x = lb.Fields.FirstOrDefault(f => f.Name == "x");
        var y = lb.Fields.FirstOrDefault(f => f.Name == "y");
        var w = lb.Fields.FirstOrDefault(f => f.Name == "width");
        var h = lb.Fields.FirstOrDefault(f => f.Name == "height");
        var refMode = lb.Fields.FirstOrDefault(f => f.Name == "referenceMode");
        checks.Check(x != null && y != null && w != null && h != null && refMode != null,
            "LetterBoxer owns x/y/width/height/referenceMode fields");

        var ctor = lb.Methods.FirstOrDefault(m => m.Name == ".ctor");
        if (ctor != null)
        {
            var floats = FloatConstants(ctor).ToList();
            checks.Check(floats.Any(v => Near(v, 4f)) && floats.Any(v => Near(v, 3f)),
                "LetterBoxer..ctor initializes x=4 and y=3");
            checks.Check(!floats.Any(v => Near(v, 16f)) && !floats.Any(v => Near(v, 9f)),
                "LetterBoxer..ctor has no 16:9 float24 literals");
        }
        else checks.Fail("LetterBoxer..ctor missing");

        var awake = lb.Methods.FirstOrDefault(m => m.Name == "Awake");
        if (awake != null)
        {
            var stores = StoreFields(awake).ToList();
            checks.Check(stores.Contains(x!) && stores.Contains(y!) && stores.Contains(w!) && stores.Contains(h!),
                "LetterBoxer.Awake overwrites all four aspect fields");
            checks.Check(stores.Contains(refMode!), "LetterBoxer.Awake forces referenceMode = DesignedAspectRatio");
            var ints = IntConstants(awake).ToList();
            checks.Check(ints.Contains(0), "LetterBoxer.Awake writes referenceMode 0");
        }
        else checks.Fail("LetterBoxer.Awake missing");

        var update = lb.Methods.FirstOrDefault(m => m.Name == "Update" && m.Parameters.Count == 0);
        if (update != null)
        {
            // the watchdog policy lives in the helper (DisplayPolicy.NextRequest); the
            // game method only has to call it, on every path, once per Update
            var ui = update.Body.Instructions;
            var watchdogCall = ui.FirstOrDefault(i => IsCall(i, "ChefRPG43SafeArea", "DisplayWatchdog", 0));
            checks.Check(watchdogCall != null, "LetterBoxer.Update calls ChefRPG43SafeArea.DisplayWatchdog");
            checks.Check(ui.Count(i => IsCall(i, "ChefRPG43SafeArea", "DisplayWatchdog", 0)) == 1,
                "LetterBoxer.Update calls the watchdog exactly once");
            checks.Check(watchdogCall != null && watchdogCall.Next?.OpCode == OpCodes.Ret,
                "LetterBoxer.Update runs the watchdog last, right before the final ret");
            checks.Check(watchdogCall != null && !ui.Any(i => i.Operand is Instruction t && t.OpCode == OpCodes.Ret),
                "no branch in LetterBoxer.Update skips the watchdog (the onUpdate guard cannot disable it)");
            checks.Check(!ui.Any(i => IsCall(i, "Screen", "SetResolution", 3)),
                "LetterBoxer.Update holds no SetResolution of its own (the policy is in the helper)");
            checks.Check(!update.Body.Instructions.Any(i => i.Operand is FieldReference f && f.Name == "WindowedMode"),
                "LetterBoxer.Update watchdog is not gated on PlayStaticStats.WindowedMode");
            checks.Check(!update.Body.Instructions.Any(i => IsCall(i, "Math", "Abs", 1) ||
                                                           IsCall(i, "Math", "Abs", 2)),
                "LetterBoxer.Update no longer imports System.Math.Abs");
            checks.Check(!update.Body.Instructions.Any(i => IsCall(i, "Screen", "set_fullScreen", 1)),
                "LetterBoxer.Update does not raise Screen.fullScreen before SetResolution");
        }
        else checks.Fail("LetterBoxer.Update missing");

        var late = lb.Methods.FirstOrDefault(m => m.Name == "LateUpdate" && m.Parameters.Count == 0);
        if (late != null)
        {
            checks.Check(late.Body.Instructions.Any(i => IsCall(i, "ChefRPG43SafeArea", "ClampCanvasesTo43", 0)),
                "LetterBoxer.LateUpdate calls ChefRPG43SafeArea.ClampCanvasesTo43");
        }
        else checks.Fail("LetterBoxer.LateUpdate missing");

        checks.Check(lb.Methods.Count(m => m.Name == "LateUpdate") == 1,
            "LetterBoxer has exactly one LateUpdate hook");
    }

    private void CheckCameraFollow(TypeDefinition cf)
    {
        var zin = cf.Methods.FirstOrDefault(m => m.Name == "SetZoomInMode");
        var zout = cf.Methods.FirstOrDefault(m => m.Name == "SetZoomOutMode");
        if (zin == null || zout == null)
        {
            checks.Fail("CameraFollow zoom methods missing");
            return;
        }

        var inInts = IntConstants(zin).ToList();
        var outInts = IntConstants(zout).ToList();
        checks.Check(inInts.Contains(640) && inInts.Contains(480),
            "SetZoomInMode uses 640x480 (4:3)");
        checks.Check(!inInts.Contains(360), "SetZoomInMode no longer uses 640x360");
        checks.Check(outInts.Contains(800) && outInts.Contains(600),
            "SetZoomOutMode uses 800x600 (4:3)");
        checks.Check(!outInts.Contains(450), "SetZoomOutMode no longer uses 800x450");

        foreach (var (method, name) in new[] { (zin, "SetZoomInMode"), (zout, "SetZoomOutMode") })
        {
            checks.Check(method.Body.Instructions.Any(i => IsCall(i, "PixelPerfectCamera", "set_cropFrameX", 1)),
                $"{name} enables cropFrameX");
            checks.Check(method.Body.Instructions.Any(i => IsCall(i, "PixelPerfectCamera", "set_cropFrameY", 1)),
                $"{name} enables cropFrameY");
            checks.Check(method.Body.Instructions.Any(i => IsCall(i, "PixelPerfectCamera", "set_stretchFill", 1)),
                $"{name} enables stretchFill");
            checks.Check(method.Body.Instructions.Any(i => IsCall(i, "PixelPerfectCamera", "set_refResolutionY", 1)),
                $"{name} writes refResolutionY");
        }
    }

    private void CheckFormulas(TypeDefinition formulas)
    {
        var actual = formulas.Methods.FirstOrDefault(m => m.Name == "GetActualScreenSize");
        var locate = formulas.Methods.FirstOrDefault(m => m.Name == "LocateScreenPosition");
        var corner = formulas.Methods.FirstOrDefault(m => m.Name == "GetCornerOffset");
        if (actual == null || locate == null || corner == null)
        {
            checks.Fail("Formulas screen math methods missing");
            return;
        }

        checks.Check(actual.Body.Instructions.Count(i => IsCall(i, "Screen", "get_height", 0)) == 2,
            "GetActualScreenSize derives width and height from Screen.height");
        checks.Check(!actual.Body.Instructions.Any(i => IsCall(i, "Screen", "get_width", 0)),
            "GetActualScreenSize no longer trusts Screen.width");
        var actualFloats = FloatConstants(actual).ToList();
        checks.Check(actualFloats.Any(v => Near(v, 4f)) && actualFloats.Any(v => Near(v, 3f)),
            "GetActualScreenSize uses the 4/3 ratio");

        foreach (var (method, name) in new[] { (locate, "LocateScreenPosition"), (corner, "GetCornerOffset") })
        {
            checks.Check(method.Body.Instructions.Any(i => IsCall(i, "Formulas", "GetActualScreenSize", 0)),
                $"{name} calls GetActualScreenSize");
            checks.Check(FloatConstants(method).Any(v => Near(v, 4f / 3f, 0.001f)),
                $"{name} branches on the 4:3 ratio");
        }
    }

    private void CheckFullscreen(TypeDefinition settings)
    {
        var fs = settings.Methods.FirstOrDefault(m => m.Name == "FullscreenResolution");
        if (fs == null)
        {
            checks.Fail("SettingsPanel.FullscreenResolution missing");
            return;
        }
        checks.Check(fs.Body.Instructions.Any(i => IsCall(i, "Display", "get_main", 0)),
            "FullscreenResolution reads Display.main");
        checks.Check(fs.Body.Instructions.Any(i => IsCall(i, "Display", "get_systemWidth", 0)) &&
                     fs.Body.Instructions.Any(i => IsCall(i, "Display", "get_systemHeight", 0)),
            "FullscreenResolution uses the native display size");
        checks.Check(fs.Body.Instructions.Any(i => IsCall(i, "ChefRPG43SafeArea", "ApplyFullscreen", 0)),
            "FullscreenResolution calls ChefRPG43SafeArea.ApplyFullscreen");
        checks.Check(!fs.Body.Instructions.Any(i => IsCall(i, "Screen", "SetResolution", 3)),
            "FullscreenResolution holds no SetResolution of its own");
        checks.Check(!fs.Body.Instructions.Any(i => IsCall(i, "Screen", "set_fullScreen", 1)),
            "FullscreenResolution does not raise Screen.fullScreen first");
        checks.Check(!fs.Body.Instructions.Any(i => i.OpCode == OpCodes.Stsfld && i.Operand is FieldReference f && f.Name == "CurrentResolution"),
            "FullscreenResolution leaves the remembered windowed size (CurrentResolution) alone");

        // the rebuilt method may count the mode list for its diagnostic log, but the
        // size it applies must come from the display, never from a resolutions entry
        var ins = fs.Body.Instructions;
        var resIdx = Enumerable.Range(0, ins.Count)
            .Where(i => IsCall(ins[i], "Screen", "get_resolutions", 0)).ToList();
        checks.Check(resIdx.All(i => i + 1 < ins.Count && ins[i + 1].OpCode == OpCodes.Ldlen),
            "FullscreenResolution only reads the mode count (ldlen) for diagnostics");

        // ApplyFullscreen is where the native size is chosen
        var helper = module.Types.FirstOrDefault(t => t.Name == "ChefRPG43SafeArea");
        var applyFs = helper?.Methods.FirstOrDefault(m => m.Name == "ApplyFullscreen");
        var sysSize = helper?.Methods.FirstOrDefault(m => m.Name == "SysSize");
        if (applyFs != null && sysSize != null)
        {
            var ai = applyFs.Body.Instructions;
            int set = Enumerable.Range(0, ai.Count).FirstOrDefault(i => IsCall(ai[i], "Screen", "SetResolution", 3), -1);
            checks.Check(set >= 1 && ai[set - 1].OpCode == OpCodes.Ldc_I4_1 && IsFullScreenModeOverload(ai[set]),
                "ApplyFullscreen requests FullScreenMode.FullScreenWindow (1) through SetResolution(int,int,FullScreenMode)");
            checks.Check(sysSize.Body.Instructions.Any(i => IsCall(i, "Display", "get_systemWidth", 0)) &&
                         sysSize.Body.Instructions.Any(i => IsCall(i, "Display", "get_systemHeight", 0)),
                "the size ApplyFullscreen requests comes from Display.main.systemWidth/Height");
            checks.Check(ai.Any(i => IsCall(i, "ChefRPG43SafeArea", "SysSize", 2)),
                "ApplyFullscreen sizes itself with SysSize (the native display)");
            checks.Check(!ai.Any(i => IsCall(i, "Screen", "set_fullScreen", 1)),
                "ApplyFullscreen does not raise Screen.fullScreen first");
        }
        else checks.Fail("ChefRPG43SafeArea.ApplyFullscreen / SysSize missing");

        var load = settings.Methods.FirstOrDefault(m => m.Name == "LoadSettings");
        if (load != null)
        {
            var li = load.Body.Instructions;
            checks.Check(li.Any(i => IsCall(i, "ChefRPG43SafeArea", "Coerce43", 1)),
                "LoadSettings coerces the saved resolution to 4:3 (Coerce43)");
            checks.Check(li.Any(i => IsCall(i, "SettingsPanel", "FullscreenResolution", 0)),
                "LoadSettings still takes the fullscreen path");
            checks.Check(li.Any(i => IsCall(i, "ChefRPG43SafeArea", "ApplyWindowed", 2)),
                "LoadSettings' windowed branch calls ApplyWindowed");
            checks.Check(!li.Any(i => IsRawSetResolution(i)) && !li.Any(i => IsCall(i, "Screen", "set_fullScreen", 1)),
                "LoadSettings has no raw SetResolution(int,int,bool) or set_fullScreen left");
            checks.Check(li.Any(i => i.OpCode == OpCodes.Stsfld && i.Operand is FieldReference f && f.Name == "CurrentResolution"),
                "LoadSettings still writes PlayStaticStats.CurrentResolution");

            // the v5.5/v5.6 patch stored `false` into WindowedMode right after every store,
            // so a saved windowed choice could never survive a boot. the game's own
            // fallback (an invalid saved size drops to fullscreen) is the only extra store.
            var stores = li.Where(i => i.OpCode == OpCodes.Stsfld && i.Operand is FieldReference f && f.Name == "WindowedMode").ToList();
            bool doubled = stores.Any(st => st.Previous?.OpCode == OpCodes.Ldc_I4_0 && st.Previous.Previous?.OpCode == OpCodes.Stsfld
                && st.Previous.Previous.Operand is FieldReference pf && pf.Name == "WindowedMode");
            checks.Check(!doubled, "LoadSettings has no WindowedMode=false forcing after the saved value is stored");
            checks.Check(stores.Count == 2,
                $"LoadSettings keeps exactly the game's own two WindowedMode stores ({stores.Count})");
        }
        else checks.Fail("SettingsPanel.LoadSettings missing");

        var windowed = settings.Methods.FirstOrDefault(m => m.Name == "WindowedMode");
        checks.Check(windowed != null && !windowed.Body.Instructions.Any(i => IsCall(i, "SettingsPanel", "FullscreenMode", 0)),
            "WindowedMode does not redirect to FullscreenMode");
        checks.Check(windowed != null && windowed.Body.Instructions.Any(i => IsCall(i, "ChefRPG43SafeArea", "ApplyWindowed", 2)),
            "WindowedMode calls ApplyWindowed");
        checks.Check(windowed != null && windowed.Body.Instructions.Any(i => i.OpCode == OpCodes.Stsfld && i.Operand is FieldReference f && f.Name == "WindowedMode"),
            "WindowedMode still marks PlayStaticStats.WindowedMode (original body kept)");

        var setValue = settings.Methods.FirstOrDefault(m => m.Name == "SetResolutionValue");
        checks.Check(setValue != null && !setValue.Body.Instructions.Any(i => IsCall(i, "SettingsPanel", "FullscreenMode", 0)),
            "SetResolutionValue does not redirect to FullscreenMode");
        checks.Check(setValue != null && setValue.Body.Instructions.Any(i => IsCall(i, "ChefRPG43SafeArea", "ApplyWindowed", 2)),
            "SetResolutionValue calls ApplyWindowed");
        checks.Check(setValue != null && setValue.Body.Instructions.Any(i =>
                IsCall(i, "SettingsPanel", "ExitWindowedModePopup", 0)),
            "SetResolutionValue closes the resolution picker");

        checks.Check(!settings.Methods.Where(m => m.HasBody).SelectMany(m => m.Body.Instructions).Any(IsRawSetResolution),
            "no raw Screen.SetResolution(int,int,bool) remains anywhere in SettingsPanel");

        var change = settings.Methods.FirstOrDefault(m => m.Name == "ChangeResolution");
        checks.Check(change != null && !change.Body.Instructions.Any(i => IsCall(i, "Screen", "get_resolutions", 0)),
            "ChangeResolution no longer reads the container's 16:9 Screen.resolutions");
        checks.Check(change != null && change.Body.Instructions.Any(i => IsCall(i, "ChefRPG43SafeArea", "Resolutions43", 0)),
            "ChangeResolution lists ChefRPG43SafeArea.Resolutions43");

        var smm = module.Types.FirstOrDefault(t => t.Name == "StartMenuManager");
        var awake = smm?.Methods.FirstOrDefault(m => m.Name == "Awake" && m.Parameters.Count == 0);
        checks.Check(awake != null && awake.Body.Instructions[0] is { } first && IsCall(first, "ChefRPG43SafeArea", "DisplayWatchdog", 0),
            "StartMenuManager.Awake runs DisplayWatchdog first (4:3 on the first title frame)");
    }

    private static bool IsRawSetResolution(Instruction i) =>
        IsCall(i, "Screen", "SetResolution", 3) && i.Operand is MethodReference mr
        && mr.Parameters[2].ParameterType.Name == "Boolean";

    private static bool IsFullScreenModeOverload(Instruction i) =>
        IsCall(i, "Screen", "SetResolution", 3) && i.Operand is MethodReference mr
        && mr.Parameters[2].ParameterType.Name == "FullScreenMode";

    private void CheckHelper(TypeDefinition helper)
    {
        var clamp = helper.Methods.FirstOrDefault(m => m.Name == "ClampCanvasesTo43");
        checks.Check(clamp != null && clamp.IsStatic && clamp.Parameters.Count == 0,
            "ChefRPG43SafeArea.ClampCanvasesTo43 is a static parameterless hook");
        checks.Check(helper.Methods.Any(m => m.Name == "PinClockIfNeeded"), "helper pins the clock");
        checks.Check(helper.Methods.Any(m => m.Name == "ContainPinned"), "helper contains pinned side prompts");
        checks.Check(helper.Methods.Any(m => m.Name == "HideMatteBars"), "helper hides the 16:9 matte bars");
        checks.Check(helper.Methods.Any(m => m.Name == "EnforcePpRefs"), "helper enforces 4:3 pixel-perfect refs");

        var frameChecks = helper.Methods.Where(m => m.HasBody)
            .SelectMany(m => m.Body.Instructions)
            .Count(i => IsCall(i, "Time", "get_frameCount", 0));
        checks.Check(frameChecks == 1, "helper reads Time.frameCount once (single per-frame gate)");

        // the helper now owns the display policy, so it does call SetResolution - but only
        // the borderless-capable (int,int,FullScreenMode) overload, never the legacy bool
        // one that toggles exclusive fullscreen, and never Screen.fullScreen
        var helperIns = helper.Methods.Where(m => m.HasBody).SelectMany(m => m.Body.Instructions).ToList();
        var setRes = helperIns.Where(i => IsCall(i, "Screen", "SetResolution", 3)).ToList();
        checks.Check(setRes.Count > 0 && setRes.All(IsFullScreenModeOverload),
            $"helper only calls Screen.SetResolution(int,int,FullScreenMode) ({setRes.Count} call sites)");
        checks.Check(!helperIns.Any(i => IsCall(i, "Screen", "set_fullScreen", 1)),
            "helper never raises Screen.fullScreen");
        foreach (var name in new[] { "ApplyFullscreen", "ApplyWindowed", "Resolutions43", "DisplayWatchdog", "NextRequest", "Modes43" })
            checks.Check(helper.Methods.Any(m => m.Name == name), $"helper has {name}");
        checks.Check(helper.Methods.Count(m => m.Name == "Coerce43") == 2,
            "helper has both Coerce43 overloads (Resolution and the six-int policy form)");
        checks.Check(helper.Methods.Where(m => m.HasBody).SelectMany(m => m.Body.Instructions)
                .All(i => !(i.Operand is MethodReference mr) || mr.DeclaringType?.FullName != "System.Math"),
            "helper imports nothing from System.Math");
        CheckHelperHandlers(helper);

        var helperCalls = helper.Methods.Where(m => m.HasBody)
            .SelectMany(m => m.Body.Instructions)
            .Where(i => i.Operand is MethodReference)
            .Select(i => (MethodReference)i.Operand)
            .Where(m => m.DeclaringType?.Name == "ChefRPG43SafeArea")
            .ToList();
        checks.Check(helperCalls.Count > 0, "helper self-calls are present");
        checks.Check(helperCalls.All(m => m.DeclaringType != null && m.DeclaringType.Module == module),
            "every helper self-call is bound to the injected type, not an external assembly");
    }

    // the cloned helper must keep the same try/catch blocks as the SafeHelper.dll it was
    // cloned from. the payload comes from the caller: the harness reads
    // patcher/safehelper/SafeHelper.dll, the file that is embedded into the patcher, so a
    // stale embed fails there; the patcher passes the resource it actually cloned.
    private void CheckHelperHandlers(TypeDefinition clone)
    {
        if (helperPayload == null || helperPayload.Length == 0)
        {
            checks.Fail("embedded helper payload not supplied to the semantic verifier");
            return;
        }
        using var payloadStream = new System.IO.MemoryStream(helperPayload);
        using var src = AssemblyDefinition.ReadAssembly(payloadStream);
        var srcType = src.MainModule.Types.First(t => t.Name == "ChefRPG43SafeArea");

        static string Key(MethodDefinition m) =>
            m.Name + "(" + string.Join(",", m.Parameters.Select(p => p.ParameterType.FullName)) + ")";
        var want = srcType.Methods.Where(m => m.HasBody && m.Body.ExceptionHandlers.Count > 0)
            .ToDictionary(Key, m => m.Body.ExceptionHandlers.Count);
        var have = clone.Methods.Where(m => m.HasBody && m.Body.ExceptionHandlers.Count > 0)
            .ToDictionary(Key, m => m.Body.ExceptionHandlers.Count);

        checks.Check(want.Count > 0, $"the embedded helper has methods with try/catch ({want.Count})");
        var missing = want.Where(kv => !have.TryGetValue(kv.Key, out var n) || n != kv.Value).Select(kv => kv.Key).ToList();
        var extra = have.Keys.Where(k => !want.ContainsKey(k)).ToList();
        checks.Check(missing.Count == 0 && extra.Count == 0,
            missing.Count == 0 && extra.Count == 0
                ? $"cloned helper keeps all {want.Sum(kv => kv.Value)} exception handlers in the same {want.Count} methods as the embedded SafeHelper.dll"
                : $"helper handlers differ from the payload: missing/changed [{string.Join(", ", missing)}] extra [{string.Join(", ", extra)}]");
        checks.Check(clone.Methods.Count == srcType.Methods.Count,
            $"cloned helper has every method of the payload ({clone.Methods.Count}/{srcType.Methods.Count})");
    }

    private void CheckNoAspectLiterals()
    {
        string[] patchedTypes =
        {
            "LetterBoxer", "Formulas", "CameraFollow", "UILineRenderer",
            "AwardTooltip", "BlueprintTooltip", "BrewingProductTooltip", "ConsumableTooltip",
            "CustomerTooltip", "CompostTooltip", "EquipmentToolTip", "FarmedPlantTooltip",
            "FestivalItemTooltip", "FishFarmStarterTooltip", "FurnitureTooltip", "GiftTooltip",
            "IngredientTooltip", "MaterialTooltip", "NoteTooltip", "PaperRecipeTooltip",
            "PetTooltip", "PlantTooltip", "QuestInfoTooltip", "SeedTooltip", "TakeoutTooltip",
            "StaffTooltip", "AnimalTooltip",
            "ChefDialogueManager", "CustomerDialogueManager", "MajorNPCDialogueManager",
            "MinorNPCDialogueManager", "PlayerDialogueManager", "RandomNPCDialogueManager",
            "WaiterDialogueManager", "MidsummerDialogueController",
        };

        var offenders = new List<string>();
        foreach (var name in patchedTypes)
        {
            var type = module.Types.FirstOrDefault(t => t.Name == name);
            if (type == null) continue;
            foreach (var method in type.Methods)
            {
                if (!method.HasBody) continue;
                foreach (var ins in method.Body.Instructions)
                {
                    if (ins.OpCode == OpCodes.Ldc_R4 && ins.Operand is float f && Near(f, 16f / 9f, 0.0001f))
                        offenders.Add($"{type.Name}.{method.Name}@{ins.Offset:x4}=1.7778");
                }
            }
        }
        checks.Check(offenders.Count == 0,
            offenders.Count == 0
                ? "no 16:9 float literal remains in any patched window"
                : $"16:9 literals remain: {string.Join(", ", offenders.Take(8))}");

        var nineSixteen = new List<string>();
        foreach (var name in patchedTypes)
        {
            var type = module.Types.FirstOrDefault(t => t.Name == name);
            if (type == null) continue;
            foreach (var method in type.Methods)
            {
                if (!method.HasBody) continue;
                var ins = method.Body.Instructions;
                for (int i = 0; i + 3 < ins.Count; i++)
                {
                    if (ins[i].OpCode == OpCodes.Ldc_R4 && ins[i].Operand is float a && Near(a, 9f) &&
                        ins[i + 1].OpCode == OpCodes.Mul &&
                        ins[i + 2].OpCode == OpCodes.Ldc_R4 && ins[i + 2].Operand is float b && Near(b, 16f) &&
                        ins[i + 3].OpCode == OpCodes.Div)
                        nineSixteen.Add($"{type.Name}.{method.Name}@{ins[i].Offset:x4}=x*9/16");
                }
            }
        }
        checks.Check(nineSixteen.Count == 0,
            nineSixteen.Count == 0
                ? "no *9/16 scaling pattern remains in tooltips or dialogue"
                : $"9/16 patterns remain: {string.Join(", ", nineSixteen.Take(8))}");
    }

    private void CheckResolutionCalls()
    {
        foreach (var type in module.Types)
        foreach (var method in type.Methods)
        {
            if (!method.HasBody) continue;
            var ins = method.Body.Instructions;
            for (int i = 0; i < ins.Count; i++)
            {
                if (!IsCall(ins[i], "Screen", "SetResolution", 3)) continue;
                var previous = ins.Skip(Math.Max(0, i - 6)).Take(Math.Min(6, i)).ToList();
                var largeConstants = previous.Where(x => x.OpCode == OpCodes.Ldc_I4 && x.Operand is int n && n >= 1280)
                    .Select(x => (int)x.Operand).ToList();
                if (largeConstants.Contains(1920) || largeConstants.Contains(1080) || largeConstants.Contains(1600))
                    checks.Fail($"{type.Name}.{method.Name} calls SetResolution with a 16:9 literal ({string.Join('x', largeConstants)})");
            }
        }
        checks.Pass("no Screen.SetResolution call passes a 16:9 width/height literal");
    }

    private void CheckPixelPerfectSetters()
    {
        var bad = new List<string>();
        foreach (var type in module.Types)
        foreach (var method in type.Methods)
        {
            if (!method.HasBody) continue;
            var ins = method.Body.Instructions;
            for (int i = 0; i < ins.Count; i++)
            {
                if (!(ins[i].Operand is MethodReference mr)) continue;
                if (mr.DeclaringType?.Name != "PixelPerfectCamera") continue;
                if (mr.Name != "set_refResolutionY") continue;
                int? value = null;
                for (int j = i - 1; j >= 0 && j >= i - 3; j--)
                {
                    if (TryInt(ins[j], out var n)) { value = n; break; }
                }
                if (value is 360 or 450)
                    bad.Add($"{type.Name}.{method.Name}@{ins[i].Offset:x4}={value}");
            }
        }
        checks.Check(bad.Count == 0,
            bad.Count == 0
                ? "no method writes a 16:9 refResolutionY (360/450)"
                : $"16:9 refResolutionY writes remain: {string.Join(", ", bad)}");
    }

    internal static bool IsCall(Instruction ins, string typeName, string methodName, int? parameterCount = null)
    {
        if (ins.Operand is not MethodReference mr) return false;
        var element = (mr as GenericInstanceMethod)?.ElementMethod ?? mr;
        if (element.Name != methodName) return false;
        var declaring = element.DeclaringType;
        if (declaring == null) return false;
        if (declaring.Name != typeName && declaring.FullName != typeName) return false;
        if (parameterCount.HasValue && element.Parameters.Count != parameterCount.Value) return false;
        return true;
    }

    internal static IEnumerable<int> IntConstants(MethodDefinition method)
    {
        foreach (var ins in method.Body.Instructions)
            if (TryInt(ins, out var v)) yield return v;
    }

    internal static bool TryInt(Instruction ins, out int value)
    {
        value = 0;
        switch (ins.OpCode.Code)
        {
            case Code.Ldc_I4_M1: value = -1; return true;
            case Code.Ldc_I4_0: value = 0; return true;
            case Code.Ldc_I4_1: value = 1; return true;
            case Code.Ldc_I4_2: value = 2; return true;
            case Code.Ldc_I4_3: value = 3; return true;
            case Code.Ldc_I4_4: value = 4; return true;
            case Code.Ldc_I4_5: value = 5; return true;
            case Code.Ldc_I4_6: value = 6; return true;
            case Code.Ldc_I4_7: value = 7; return true;
            case Code.Ldc_I4_8: value = 8; return true;
            case Code.Ldc_I4_S: value = (sbyte)ins.Operand; return true;
            case Code.Ldc_I4: value = (int)ins.Operand; return true;
            case Code.Ldc_I8:
                long l = (long)ins.Operand;
                if (l < int.MinValue || l > int.MaxValue) return false;
                value = (int)l; return true;
            default: return false;
        }
    }

    internal static IEnumerable<float> FloatConstants(MethodDefinition method)
    {
        foreach (var ins in method.Body.Instructions)
            if (ins.OpCode == OpCodes.Ldc_R4 && ins.Operand is float f) yield return f;
    }

    private static IEnumerable<FieldReference> StoreFields(MethodDefinition method)
    {
        foreach (var ins in method.Body.Instructions)
            if (ins.OpCode == OpCodes.Stfld && ins.Operand is FieldReference f) yield return f;
    }

    internal static bool Near(float a, float b, float epsilon = 0.0001f) => Math.Abs(a - b) <= epsilon;
}
