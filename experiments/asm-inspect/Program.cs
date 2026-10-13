// Dumps target framework, references and public types/members of a .NET assembly
// using System.Reflection.Metadata (no loading). Usage: asm-inspect <dll> [typeFilter]
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
var path = args[0];
var filter = args.Length > 1 ? args[1] : null;
using var fs = File.OpenRead(path);
using var pe = new PEReader(fs);
var md = pe.GetMetadataReader();
var asm = md.GetAssemblyDefinition();
Console.WriteLine($"Assembly: {md.GetString(asm.Name)} {asm.Version}");
foreach (var h in asm.GetCustomAttributes())
{
    var ca = md.GetCustomAttribute(h);
    if (ca.Constructor.Kind == HandleKind.MemberReference)
    {
        var mr = md.GetMemberReference((MemberReferenceHandle)ca.Constructor);
        var tr = md.GetTypeReference((TypeReferenceHandle)mr.Parent);
        if (md.GetString(tr.Name) == "TargetFrameworkAttribute")
        {
            var blob = md.GetBlobReader(ca.Value); blob.ReadUInt16();
            Console.WriteLine($"TargetFramework: {blob.ReadSerializedString()}");
        }
    }
}
foreach (var r in md.AssemblyReferences) { var a = md.GetAssemblyReference(r); Console.WriteLine($"Ref: {md.GetString(a.Name)} {a.Version}"); }
if (filter == null) return;
foreach (var th in md.TypeDefinitions)
{
    var t = md.GetTypeDefinition(th);
    var full = md.GetString(t.Namespace) + "." + md.GetString(t.Name);
    if ((t.Attributes & System.Reflection.TypeAttributes.VisibilityMask) != System.Reflection.TypeAttributes.Public) continue;
    if (filter != "*" && !System.Text.RegularExpressions.Regex.IsMatch(full, filter)) continue;
    Console.WriteLine($"TYPE {full}");
    if (filter == "*") continue;
    foreach (var mh in t.GetMethods()) { var m = md.GetMethodDefinition(mh); if ((m.Attributes & System.Reflection.MethodAttributes.Public) == System.Reflection.MethodAttributes.Public) Console.WriteLine($"   M {md.GetString(m.Name)}"); }
}
