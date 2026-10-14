using System.Collections.Immutable;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace NcadBatchPlot.Tests;

/// <summary>
/// Finds nanoCAD .NET API members that are not implemented by the host.
/// In the real nanoCAD 24 hostmgd.dll such members consist of a single call
/// <c>HostMgd.Internal.API.Logger.Report(3, MethodBase.GetCurrentMethod().Name, 255)</c>,
/// which shows the modal "Mgd / NOT_IMPLEMENTED / &lt;member&gt;" dialog reported in issue #3
/// and then returns null/default.
/// </summary>
internal static class NanoCadApiStubs
{
    public static readonly string[] HostAssemblies = { "hostmgd", "hostdbmgd" };

    private const string LoggerType = "HostMgd.Internal.API.Logger";
    private const string LoggerMethod = "Report";

    /// <summary>Returns "Type::Member(signature)" keys of every not-implemented host member.</summary>
    public static ISet<string> FindStubs(string apiDir)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in HostAssemblies)
        {
            var path = Path.Combine(apiDir, name + ".dll");
            using var stream = File.OpenRead(path);
            using var pe = new PEReader(stream);
            var md = pe.GetMetadataReader();
            foreach (var typeHandle in md.TypeDefinitions)
            {
                var type = md.GetTypeDefinition(typeHandle);
                foreach (var methodHandle in type.GetMethods())
                {
                    var method = md.GetMethodDefinition(methodHandle);
                    // hostmgd/hostdbmgd are C++/CLI mixed-mode assemblies: skip native method bodies.
                    if (method.RelativeVirtualAddress == 0
                        || (method.ImplAttributes & System.Reflection.MethodImplAttributes.CodeTypeMask) != System.Reflection.MethodImplAttributes.IL
                        || (method.ImplAttributes & System.Reflection.MethodImplAttributes.Unmanaged) != 0)
                    {
                        continue;
                    }

                    var body = pe.GetMethodBody(method.RelativeVirtualAddress);
                    if (CallsLoggerReport(md, body.GetILBytes() ?? Array.Empty<byte>()))
                    {
                        result.Add(MemberKey(TypeName(md, typeHandle), md.GetString(method.Name),
                            method.DecodeSignature(SignatureNames.Instance, null)));
                    }
                }
            }
        }

        return result;
    }

    /// <summary>Returns "Type::Member(signature)" keys of every nanoCAD API method referenced by the plugin.</summary>
    public static ISet<string> FindHostApiReferences(string pluginPath)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        using var stream = File.OpenRead(pluginPath);
        using var pe = new PEReader(stream);
        var md = pe.GetMetadataReader();
        foreach (var handle in md.MemberReferences)
        {
            var member = md.GetMemberReference(handle);
            if (member.GetKind() != MemberReferenceKind.Method || member.Parent.Kind != HandleKind.TypeReference)
            {
                continue;
            }

            var typeRef = (TypeReferenceHandle)member.Parent;
            if (!IsHostAssembly(md, typeRef))
            {
                continue;
            }

            result.Add(MemberKey(TypeName(md, typeRef), md.GetString(member.Name),
                member.DecodeMethodSignature(SignatureNames.Instance, null)));
        }

        return result;
    }

    private static string MemberKey(string type, string name, MethodSignature<string> signature)
        => $"{type}::{name}({string.Join(", ", signature.ParameterTypes)})";

    private static bool IsHostAssembly(MetadataReader md, TypeReferenceHandle handle)
    {
        var scope = md.GetTypeReference(handle).ResolutionScope;
        while (scope.Kind == HandleKind.TypeReference)
        {
            scope = md.GetTypeReference((TypeReferenceHandle)scope).ResolutionScope;
        }

        if (scope.Kind != HandleKind.AssemblyReference)
        {
            return false;
        }

        var name = md.GetString(md.GetAssemblyReference((AssemblyReferenceHandle)scope).Name);
        return HostAssemblies.Contains(name, StringComparer.OrdinalIgnoreCase);
    }

    private static bool CallsLoggerReport(MetadataReader md, byte[] il)
    {
        var offset = 0;
        while (offset < il.Length)
        {
            var opCode = ReadOpCode(il, ref offset);
            if ((opCode == OpCodes.Call || opCode == OpCodes.Callvirt)
                && MetadataTokens.EntityHandle(BitConverter.ToInt32(il, offset)) is var target
                && target.Kind == HandleKind.MethodDefinition)
            {
                var method = md.GetMethodDefinition((MethodDefinitionHandle)target);
                if (md.GetString(method.Name) == LoggerMethod && TypeName(md, method.GetDeclaringType()) == LoggerType)
                {
                    return true;
                }
            }

            offset += OperandSize(opCode, il, offset);
        }

        return false;
    }

    private static readonly Dictionary<short, OpCode> OpCodeTable = typeof(OpCodes)
        .GetFields()
        .Select(f => (OpCode)f.GetValue(null)!)
        .ToDictionary(o => o.Value);

    private static OpCode ReadOpCode(byte[] il, ref int offset)
    {
        short value = il[offset++];
        if (value == 0xFE)
        {
            value = unchecked((short)(0xFE00 | il[offset++]));
        }

        return OpCodeTable[value];
    }

    private static int OperandSize(OpCode opCode, byte[] il, int offset) => opCode.OperandType switch
    {
        OperandType.InlineNone => 0,
        OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
        OperandType.InlineVar => 2,
        OperandType.InlineI8 or OperandType.InlineR => 8,
        OperandType.InlineSwitch => 4 + 4 * BitConverter.ToInt32(il, offset),
        _ => 4,
    };

    private static string TypeName(MetadataReader md, TypeDefinitionHandle handle)
    {
        var type = md.GetTypeDefinition(handle);
        var declaring = type.GetDeclaringType();
        return declaring.IsNil
            ? Join(md.GetString(type.Namespace), md.GetString(type.Name))
            : TypeName(md, declaring) + "/" + md.GetString(type.Name);
    }

    private static string TypeName(MetadataReader md, TypeReferenceHandle handle)
    {
        var type = md.GetTypeReference(handle);
        return type.ResolutionScope.Kind == HandleKind.TypeReference
            ? TypeName(md, (TypeReferenceHandle)type.ResolutionScope) + "/" + md.GetString(type.Name)
            : Join(md.GetString(type.Namespace), md.GetString(type.Name));
    }

    private static string Join(string ns, string name) => ns.Length == 0 ? name : ns + "." + name;

    /// <summary>Decodes signature types into assembly-independent names, ignoring C++/CLI modifiers.</summary>
    private sealed class SignatureNames : ISignatureTypeProvider<string, object?>
    {
        public static readonly SignatureNames Instance = new();

        public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode.ToString();
        public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) => TypeName(reader, handle);
        public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) => TypeName(reader, handle);
        public string GetTypeFromSpecification(MetadataReader reader, object? genericContext, TypeSpecificationHandle handle, byte rawTypeKind)
            => reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);
        public string GetSZArrayType(string elementType) => elementType + "[]";
        public string GetArrayType(string elementType, ArrayShape shape) => elementType + "[" + new string(',', shape.Rank - 1) + "]";
        public string GetByReferenceType(string elementType) => elementType + "&";
        public string GetPointerType(string elementType) => elementType + "*";
        public string GetPinnedType(string elementType) => elementType;
        public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) => unmodifiedType;
        public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments)
            => genericType + "<" + string.Join(",", typeArguments) + ">";
        public string GetGenericTypeParameter(object? genericContext, int index) => "!" + index;
        public string GetGenericMethodParameter(object? genericContext, int index) => "!!" + index;
        public string GetFunctionPointerType(MethodSignature<string> signature) => "fnptr";
    }
}
