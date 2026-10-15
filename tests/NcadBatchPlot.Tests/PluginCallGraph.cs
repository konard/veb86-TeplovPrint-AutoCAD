using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace NcadBatchPlot.Tests;

/// <summary>
/// Static call graph of the methods defined in the plugin itself (call, callvirt, newobj, ldftn),
/// built from IL without loading the assembly. Lambdas are followed through their ldftn.
/// Methods are keyed as "Namespace.Type::Name" (overloads are merged).
/// Calls into other assemblies (e.g. nanoCAD's hostmgd/hostdbmgd) are leaf nodes keyed the same way.
/// </summary>
internal sealed class PluginCallGraph
{
    private readonly Dictionary<string, HashSet<string>> _calls = new(StringComparer.Ordinal);

    public PluginCallGraph(string pluginPath)
    {
        using var stream = File.OpenRead(pluginPath);
        using var pe = new PEReader(stream);
        var md = pe.GetMetadataReader();
        foreach (var typeHandle in md.TypeDefinitions)
        {
            foreach (var methodHandle in md.GetTypeDefinition(typeHandle).GetMethods())
            {
                var method = md.GetMethodDefinition(methodHandle);
                var key = Key(md, methodHandle);
                if (!_calls.TryGetValue(key, out var targets))
                {
                    targets = new HashSet<string>(StringComparer.Ordinal);
                    _calls[key] = targets;
                }

                if (method.RelativeVirtualAddress == 0)
                {
                    continue;
                }

                var il = pe.GetMethodBody(method.RelativeVirtualAddress).GetILBytes() ?? Array.Empty<byte>();
                foreach (var target in CalledMethods(il))
                {
                    if (target.Kind == HandleKind.MethodDefinition)
                    {
                        targets.Add(Key(md, (MethodDefinitionHandle)target));
                    }
                    else if (target.Kind == HandleKind.MemberReference
                             && ExternalKey(md, (MemberReferenceHandle)target) is { } external)
                    {
                        targets.Add(external);
                    }
                }
            }
        }
    }

    public bool Contains(string method) => _calls.ContainsKey(method);

    /// <summary>Every method that calls <paramref name="target"/> directly or transitively.</summary>
    public ISet<string> FindTransitiveCallers(string target)
    {
        var callers = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>();
        queue.Enqueue(target);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            foreach (var (caller, targets) in _calls)
            {
                if (targets.Contains(current) && callers.Add(caller))
                {
                    queue.Enqueue(caller);
                }
            }
        }

        return callers;
    }

    /// <summary>Returns the shortest call chain from <paramref name="from"/> to <paramref name="to"/>, or null.</summary>
    public IReadOnlyList<string>? FindPath(string from, string to)
    {
        var previous = new Dictionary<string, string?>(StringComparer.Ordinal) { [from] = null };
        var queue = new Queue<string>();
        queue.Enqueue(from);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (current == to)
            {
                var path = new List<string>();
                for (string? step = current; step != null; step = previous[step])
                {
                    path.Add(step);
                }

                path.Reverse();
                return path;
            }

            if (!_calls.TryGetValue(current, out var targets))
            {
                continue;
            }

            foreach (var target in targets)
            {
                if (previous.TryAdd(target, current))
                {
                    queue.Enqueue(target);
                }
            }
        }

        return null;
    }

    private static string Key(MetadataReader md, MethodDefinitionHandle handle)
    {
        var method = md.GetMethodDefinition(handle);
        return TypeName(md, method.GetDeclaringType()) + "::" + md.GetString(method.Name);
    }

    private static string? ExternalKey(MetadataReader md, MemberReferenceHandle handle)
    {
        var member = md.GetMemberReference(handle);
        return member.Parent.Kind == HandleKind.TypeReference
            ? TypeName(md, (TypeReferenceHandle)member.Parent) + "::" + md.GetString(member.Name)
            : null;
    }

    private static string TypeName(MetadataReader md, TypeReferenceHandle handle)
    {
        var type = md.GetTypeReference(handle);
        if (type.ResolutionScope.Kind == HandleKind.TypeReference)
        {
            return TypeName(md, (TypeReferenceHandle)type.ResolutionScope) + "/" + md.GetString(type.Name);
        }

        var ns = md.GetString(type.Namespace);
        return ns.Length == 0 ? md.GetString(type.Name) : ns + "." + md.GetString(type.Name);
    }

    private static string TypeName(MetadataReader md, TypeDefinitionHandle handle)
    {
        var type = md.GetTypeDefinition(handle);
        var declaring = type.GetDeclaringType();
        if (!declaring.IsNil)
        {
            // Compiler-generated closures and lambdas belong to the method's owner type.
            return TypeName(md, declaring) + "/" + md.GetString(type.Name);
        }

        var ns = md.GetString(type.Namespace);
        return ns.Length == 0 ? md.GetString(type.Name) : ns + "." + md.GetString(type.Name);
    }

    private static IEnumerable<EntityHandle> CalledMethods(byte[] il)
    {
        var offset = 0;
        while (offset < il.Length)
        {
            short value = il[offset++];
            if (value == 0xFE)
            {
                value = unchecked((short)(0xFE00 | il[offset++]));
            }

            var opCode = OpCodeTable[value];
            if (opCode == OpCodes.Call || opCode == OpCodes.Callvirt || opCode == OpCodes.Newobj
                || opCode == OpCodes.Ldftn || opCode == OpCodes.Ldvirtftn)
            {
                yield return MetadataTokens.EntityHandle(BitConverter.ToInt32(il, offset));
            }

            offset += opCode.OperandType switch
            {
                OperandType.InlineNone => 0,
                OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                OperandType.InlineVar => 2,
                OperandType.InlineI8 or OperandType.InlineR => 8,
                OperandType.InlineSwitch => 4 + 4 * BitConverter.ToInt32(il, offset),
                _ => 4,
            };
        }
    }

    private static readonly Dictionary<short, OpCode> OpCodeTable = typeof(OpCodes)
        .GetFields()
        .Select(f => (OpCode)f.GetValue(null)!)
        .ToDictionary(o => o.Value);
}
