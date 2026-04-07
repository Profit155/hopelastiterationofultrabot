using System;
using System.Reflection;
using System.Linq;

var asm = Assembly.LoadFrom(@"C:\Steam\steamapps\common\ULTRAKILL\ULTRAKILL_Data\Managed\Assembly-CSharp.dll");
var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

foreach (var typeName in new[] { "InputManager", "PlayerInput" })
{
    var t = asm.GetType(typeName);
    if (t == null) { Console.WriteLine($"{typeName} NOT FOUND"); continue; }
    Console.WriteLine($"\n=== {t.FullName} (base: {t.BaseType?.Name}) ===");
    foreach (var f in t.GetFields(flags))
        Console.WriteLine($"  field: {(f.IsPublic?"pub":"prv")} {(f.IsStatic?"static ":"")}{f.FieldType.Name} {f.Name}");
    foreach (var p in t.GetProperties(flags))
        Console.WriteLine($"  prop: {p.PropertyType.Name} {p.Name} get={p.CanRead} set={p.CanWrite}");
    foreach (var m in t.GetMethods(flags).OrderBy(m => m.Name))
        Console.WriteLine($"  method: {(m.IsPublic?"pub":"prv")} {(m.IsStatic?"static ":"")}{m.ReturnType.Name} {m.Name}({string.Join(", ", m.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"))})");
}
