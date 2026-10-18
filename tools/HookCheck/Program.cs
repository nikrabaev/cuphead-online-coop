using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace HookCheck
{
    /// <summary>
    /// Checks CupheadOnline.dll against the game's own assemblies, so a Cuphead update (or a typo) that removes a
    /// hooked member fails the build instead of shipping a mod that silently half works:
    ///   - every [HarmonyPatch(typeof(T), "Method", argTypes)] target, merged across class and method attributes;
    ///   - every constant AccessTools.Method/DeclaredMethod/Field/Property/PropertyGetter/PropertySetter(typeof(T)
    ///     or a TypeByName("T") local, "name") and AccessTools.TypeByName("T") lookup in the mod's IL.
    /// Lookups on a runtime type (obj.GetType()) can't be resolved statically and are counted as skipped.
    /// Usage: HookCheck path/to/CupheadOnline.dll path/to/Cuphead_Data/Managed
    /// </summary>
    internal static class Program
    {
        private static readonly List<string> Errors = new List<string>();
        private static int patchTargets, members, skipped;
        private static ModuleDefinition mod;
        private static readonly List<ModuleDefinition> GameModules = new List<ModuleDefinition>();

        private static int Main(string[] args)
        {
            if (args.Length != 2)
            {
                Console.Error.WriteLine("usage: HookCheck CupheadOnline.dll GameManagedDir");
                return 2;
            }
            var resolver = new DefaultAssemblyResolver();
            resolver.AddSearchDirectory(args[1]);
            resolver.AddSearchDirectory(Path.GetDirectoryName(Path.GetFullPath(args[0])));
            var parameters = new ReaderParameters { AssemblyResolver = resolver };
            mod = ModuleDefinition.ReadModule(args[0], parameters);
            foreach (string dll in Directory.GetFiles(args[1], "*.dll"))
            {
                try { GameModules.Add(ModuleDefinition.ReadModule(dll, parameters)); }
                catch (BadImageFormatException) { } // native plugin
            }

            foreach (TypeDefinition type in AllTypes(mod))
            {
                CheckPatchClass(type);
                foreach (MethodDefinition method in type.Methods)
                {
                    if (method.HasBody) ScanLookups(method);
                }
            }

            foreach (string e in Errors) Console.Error.WriteLine("error: " + e);
            Console.WriteLine("Hook check: " + patchTargets + " Harmony targets and " + members + " reflected game members checked" +
                (skipped > 0 ? ", " + skipped + " runtime-typed lookups skipped" : "") + (Errors.Count == 0 ? ", all present." : ", " + Errors.Count + " MISSING."));
            return Errors.Count == 0 ? 0 : 1;
        }

        private static IEnumerable<TypeDefinition> AllTypes(ModuleDefinition module)
        {
            var stack = new Stack<TypeDefinition>(module.Types);
            while (stack.Count > 0)
            {
                TypeDefinition t = stack.Pop();
                yield return t;
                foreach (TypeDefinition nested in t.NestedTypes) stack.Push(nested);
            }
        }

        // ------------------------------------------------------------------ [HarmonyPatch] attributes

        private sealed class Target
        {
            public TypeReference Type;
            public string Method;
            public TypeReference[] Args;
            public int Kind; // HarmonyLib.MethodType: 0 Normal, 1 Getter, 2 Setter, 3 Constructor, 4 StaticConstructor
        }

        private static void CheckPatchClass(TypeDefinition type)
        {
            var classAttrs = type.CustomAttributes.Where(IsHarmonyPatch).ToList();
            bool hasMethodTargets = type.Methods.Any(m => m.CustomAttributes.Any(IsHarmonyPatch));
            if (classAttrs.Count == 0 && !hasMethodTargets) return;
            if (type.Methods.Any(m => m.Name == "TargetMethod" || m.Name == "TargetMethods")) return; // resolved in code; IL scan covers it

            var classTarget = new Target();
            foreach (CustomAttribute a in classAttrs) Merge(classTarget, a);

            bool patchedAny = false;
            foreach (MethodDefinition m in type.Methods)
            {
                var attrs = m.CustomAttributes.Where(IsHarmonyPatch).ToList();
                if (attrs.Count == 0) continue;
                var t = new Target { Type = classTarget.Type, Method = classTarget.Method, Args = classTarget.Args, Kind = classTarget.Kind };
                foreach (CustomAttribute a in attrs) Merge(t, a);
                if (t.Type == null) continue; // a method-level attribute without a target only tags the patch type
                Check(t, type.FullName + "." + m.Name);
                patchedAny = true;
            }
            if (!patchedAny && classTarget.Type != null) Check(classTarget, type.FullName);
        }

        private static bool IsHarmonyPatch(CustomAttribute a)
        {
            return a.AttributeType.FullName == "HarmonyLib.HarmonyPatch";
        }

        private static void Merge(Target t, CustomAttribute a)
        {
            foreach (CustomAttributeArgument arg in a.ConstructorArguments)
            {
                if (arg.Value is TypeReference tr) t.Type = tr;
                else if (arg.Value is string s) t.Method = s;
                else if (arg.Value is CustomAttributeArgument[] arr) t.Args = arr.Select(x => (TypeReference)x.Value).ToArray();
                else if (arg.Type.FullName == "HarmonyLib.MethodType") t.Kind = Convert.ToInt32(arg.Value);
            }
        }

        private static void Check(Target t, string where)
        {
            patchTargets++;
            TypeDefinition type = Resolve(t.Type);
            string described = t.Type.FullName + "." + (t.Method ?? "?") + (t.Args != null ? "(" + string.Join(", ", t.Args.Select(a => a.Name)) + ")" : "");
            if (type == null) { Errors.Add(where + ": patched type " + t.Type.FullName + " not found"); return; }
            string name = t.Kind == 1 ? "get_" + t.Method : t.Kind == 2 ? "set_" + t.Method : t.Kind == 3 ? ".ctor" : t.Kind == 4 ? ".cctor" : t.Method;
            if (name == null) { Errors.Add(where + ": no target method in its [HarmonyPatch] attributes"); return; }
            bool found = Hierarchy(type).SelectMany(d => d.Methods).Any(m => m.Name == name && (t.Args == null || SameParameters(m, t.Args)));
            if (!found) Errors.Add(where + ": patch target " + described + " not found");
        }

        private static bool SameParameters(MethodDefinition m, TypeReference[] args)
        {
            if (m.Parameters.Count != args.Length) return false;
            for (int i = 0; i < args.Length; i++)
            {
                if (m.Parameters[i].ParameterType.FullName != args[i].FullName) return false;
            }
            return true;
        }

        // ------------------------------------------------------------------ reflection lookups in IL

        private static readonly HashSet<string> MemberLookups = new HashSet<string>
        {
            "Method", "DeclaredMethod", "Field", "DeclaredField", "Property", "DeclaredProperty", "PropertyGetter", "PropertySetter",
        };

        private static void ScanLookups(MethodDefinition method)
        {
            var body = method.Body.Instructions;
            for (int i = 0; i < body.Count; i++)
            {
                if (!(body[i].Operand is MethodReference call)) continue;
                if (body[i].OpCode.Code != Code.Call && body[i].OpCode.Code != Code.Callvirt) continue;
                if (call.DeclaringType.FullName != "HarmonyLib.AccessTools") continue;
                string where = method.DeclaringType.FullName + "." + method.Name;
                if (call.Name == "TypeByName")
                {
                    string typeName = Producer(body, i, 0) is Instruction s && s.OpCode.Code == Code.Ldstr ? (string)s.Operand : null;
                    if (typeName == null) { skipped++; continue; }
                    members++;
                    if (FindType(typeName) == null) Errors.Add(where + ": AccessTools.TypeByName(\"" + typeName + "\") finds no type");
                    continue;
                }
                if (!MemberLookups.Contains(call.Name) || call.Parameters.Count < 2 || call.Parameters[0].ParameterType.FullName != "System.Type") continue;
                TypeDefinition owner = TypeArgument(body, Producer(body, i, 0));
                Instruction nameIns = Producer(body, i, 1);
                if (owner == null || nameIns == null || nameIns.OpCode.Code != Code.Ldstr) { skipped++; continue; }
                string name = (string)nameIns.Operand;
                members++;
                bool found;
                if (call.Name.Contains("Field")) found = Hierarchy(owner).Any(d => d.Fields.Any(f => f.Name == name));
                else if (call.Name.Contains("Propert")) found = Hierarchy(owner).Any(d => d.Properties.Any(p => p.Name == name));
                else found = Hierarchy(owner).Any(d => d.Methods.Any(m => m.Name == name));
                if (!found) Errors.Add(where + ": AccessTools." + call.Name + "(" + owner.FullName + ", \"" + name + "\") finds nothing");
            }
        }

        /// <summary>The type a System.Type argument evaluates to: typeof(T), TypeByName("T"), or a local holding either.</summary>
        private static TypeDefinition TypeArgument(IList<Instruction> body, Instruction producer)
        {
            for (int guard = 0; producer != null && guard < 8; guard++)
            {
                if (producer.Operand is MethodReference m && (producer.OpCode.Code == Code.Call || producer.OpCode.Code == Code.Callvirt))
                {
                    int at = body.IndexOf(producer);
                    if (m.Name == "GetTypeFromHandle")
                    {
                        Instruction token = Producer(body, at, 0);
                        return token != null && token.OpCode.Code == Code.Ldtoken ? Resolve(token.Operand as TypeReference) : null;
                    }
                    if (m.Name == "TypeByName" && m.DeclaringType.FullName == "HarmonyLib.AccessTools")
                    {
                        Instruction s = Producer(body, at, 0);
                        return s != null && s.OpCode.Code == Code.Ldstr ? FindType((string)s.Operand) : null;
                    }
                    return null;
                }
                int local = LocalIndex(producer, load: true);
                if (local < 0) return null;
                // The closest earlier store to the same local (straight-line code in these lookups).
                int idx = body.IndexOf(producer);
                Instruction store = null;
                for (int j = idx - 1; j >= 0; j--)
                {
                    if (LocalIndex(body[j], load: false) == local) { store = body[j]; break; }
                }
                if (store == null) return null;
                producer = Producer(body, body.IndexOf(store), 0);
            }
            return null;
        }

        private static int LocalIndex(Instruction ins, bool load)
        {
            switch (ins.OpCode.Code)
            {
                case Code.Ldloc_0: return load ? 0 : -1;
                case Code.Ldloc_1: return load ? 1 : -1;
                case Code.Ldloc_2: return load ? 2 : -1;
                case Code.Ldloc_3: return load ? 3 : -1;
                case Code.Ldloc: case Code.Ldloc_S: return load ? ((VariableDefinition)ins.Operand).Index : -1;
                case Code.Stloc_0: return load ? -1 : 0;
                case Code.Stloc_1: return load ? -1 : 1;
                case Code.Stloc_2: return load ? -1 : 2;
                case Code.Stloc_3: return load ? -1 : 3;
                case Code.Stloc: case Code.Stloc_S: return load ? -1 : ((VariableDefinition)ins.Operand).Index;
                default: return -1;
            }
        }

        /// <summary>
        /// The instruction that pushed argument <paramref name="arg"/> of the call (or stack consumer) at
        /// <paramref name="at"/>, found by walking back and counting stack effects. Null if it isn't straight-line.
        /// </summary>
        private static Instruction Producer(IList<Instruction> body, int at, int arg)
        {
            int pops = Pops(body[at]);
            int need = pops - arg; // depth below the top, 1 = top of stack
            for (int j = at - 1; j >= 0; j--)
            {
                Instruction ins = body[j];
                if (ins.OpCode.FlowControl == FlowControl.Branch || ins.OpCode.FlowControl == FlowControl.Cond_Branch ||
                    ins.OpCode.FlowControl == FlowControl.Return || ins.OpCode.FlowControl == FlowControl.Throw) return null;
                int push = Pushes(ins);
                if (need <= push) return push == 1 ? ins : null;
                need = need - push + Pops(ins);
            }
            return null;
        }

        private static int Pops(Instruction ins)
        {
            if (ins.Operand is MethodReference m && (ins.OpCode.Code == Code.Call || ins.OpCode.Code == Code.Callvirt || ins.OpCode.Code == Code.Newobj))
            {
                return m.Parameters.Count + (m.HasThis && ins.OpCode.Code != Code.Newobj ? 1 : 0);
            }
            switch (ins.OpCode.StackBehaviourPop)
            {
                case StackBehaviour.Pop0: return 0;
                case StackBehaviour.Pop1: case StackBehaviour.Popi: case StackBehaviour.Popref: return 1;
                case StackBehaviour.Pop1_pop1: case StackBehaviour.Popi_pop1: case StackBehaviour.Popi_popi:
                case StackBehaviour.Popi_popi8: case StackBehaviour.Popi_popr4: case StackBehaviour.Popi_popr8:
                case StackBehaviour.Popref_pop1: case StackBehaviour.Popref_popi: return 2;
                case StackBehaviour.Popi_popi_popi: case StackBehaviour.Popref_popi_popi: case StackBehaviour.Popref_popi_popi8:
                case StackBehaviour.Popref_popi_popr4: case StackBehaviour.Popref_popi_popr8: case StackBehaviour.Popref_popi_popref: return 3;
                default: return 0;
            }
        }

        private static int Pushes(Instruction ins)
        {
            if (ins.Operand is MethodReference m && (ins.OpCode.Code == Code.Call || ins.OpCode.Code == Code.Callvirt))
            {
                return m.ReturnType.FullName == "System.Void" ? 0 : 1;
            }
            switch (ins.OpCode.StackBehaviourPush)
            {
                case StackBehaviour.Push0: return 0;
                case StackBehaviour.Push1_push1: return 2;
                default: return 1;
            }
        }

        // ------------------------------------------------------------------ resolution

        private static TypeDefinition Resolve(TypeReference t)
        {
            if (t == null) return null;
            try { return t.Resolve(); }
            catch (AssemblyResolutionException) { return null; }
        }

        private static IEnumerable<TypeDefinition> Hierarchy(TypeDefinition t)
        {
            for (TypeDefinition d = t; d != null; d = d.BaseType != null ? Resolve(d.BaseType) : null) yield return d;
        }

        /// <summary>Like AccessTools.TypeByName: full name or plain name, in any game assembly (or the mod).</summary>
        private static TypeDefinition FindType(string name)
        {
            foreach (ModuleDefinition module in GameModules.Concat(new[] { mod }))
            {
                foreach (TypeDefinition t in AllTypes(module))
                {
                    if (t.FullName == name || t.FullName.Replace('/', '+') == name || t.Name == name) return t;
                }
            }
            return null;
        }
    }
}
