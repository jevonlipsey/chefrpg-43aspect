using System;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

public static class Helper
{
    public static MethodReference FindMethod(ModuleDefinition module, string typeName, string methodName, int paramCount = -1)
    {
        // Search in imported member references
        foreach (var mr in module.GetMemberReferences().OfType<MethodReference>())
        {
            if (mr.DeclaringType.Name == typeName && mr.Name == methodName)
            {
                if (paramCount < 0 || mr.Parameters.Count == paramCount)
                    return mr;
            }
        }
        // Search in local types
        foreach (var t in module.Types)
        {
            if (t.Name == typeName)
            {
                var m = t.Methods.FirstOrDefault(x => x.Name == methodName && (paramCount < 0 || x.Parameters.Count == paramCount));
                if (m != null) return module.ImportReference(m);
            }
        }
        return null;
    }

    public static TypeReference FindType(ModuleDefinition module, string typeName)
    {
        foreach (var tr in module.GetTypeReferences())
        {
            if (tr.Name == typeName) return tr;
        }
        foreach (var t in module.Types)
        {
            if (t.Name == typeName) return t;
        }
        return null;
    }
}
