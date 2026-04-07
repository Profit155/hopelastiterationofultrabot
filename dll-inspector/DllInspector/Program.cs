using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Loader;
using System.Text;

internal static class Program
{
    private static readonly string DefaultAssemblyPath = @"C:\Steam\steamapps\common\ULTRAKILL\ULTRAKILL_Data\Managed\Assembly-CSharp.dll";
    private static readonly string[] InterestingKeywords =
    {
        "InputActionState",
        "IsPressed",
        "WasPerformedThisFrame",
        "PerformedFrame",
        "Fire1",
        "Fire2",
        "Jump",
        "Dodge",
        "Slide",
        "Punch",
        "Hook",
        "Slot1",
        "Slot2",
        "Slot3",
        "Slot4",
        "Slot5",
        "Slot6",
        "InputManager",
        "InputSource",
        "PlayerInput",
        "KeyCode",
        "GetKey",
        "GameStateManager",
        "PlayerInputLocked",
        "activated",
    };

    private static readonly OpCode[] SingleByteOpCodes = new OpCode[0x100];
    private static readonly OpCode[] MultiByteOpCodes = new OpCode[0x100];

    private sealed record MethodRequest(string TypeName, string MethodName, Type[] ParameterTypes, string Label);

    private sealed record Instruction(int Offset, OpCode OpCode, string OperandText)
    {
        public override string ToString()
        {
            return string.IsNullOrEmpty(OperandText)
                ? $"IL_{Offset:X4}: {OpCode.Name}"
                : $"IL_{Offset:X4}: {OpCode.Name,-12} {OperandText}";
        }
    }

    static Program()
    {
        foreach (FieldInfo field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (field.GetValue(null) is not OpCode opCode)
            {
                continue;
            }

            ushort value = unchecked((ushort)opCode.Value);
            if (value < 0x100)
            {
                SingleByteOpCodes[value] = opCode;
            }
            else if ((value & 0xFF00) == 0xFE00)
            {
                MultiByteOpCodes[value & 0xFF] = opCode;
            }
        }
    }

    private static int Main(string[] args)
    {
        string assemblyPath = args.Length > 0 ? args[0] : DefaultAssemblyPath;
        assemblyPath = Path.GetFullPath(assemblyPath);

        if (!File.Exists(assemblyPath))
        {
            Console.Error.WriteLine($"Assembly not found: {assemblyPath}");
            return 1;
        }

        string managedDirectory = Path.GetDirectoryName(assemblyPath) ?? AppContext.BaseDirectory;
        AssemblyLoadContext.Default.Resolving += (_, name) =>
        {
            Assembly? alreadyLoaded = AssemblyLoadContext.Default.Assemblies
                .FirstOrDefault(loaded => string.Equals(loaded.GetName().Name, name.Name, StringComparison.OrdinalIgnoreCase));
            if (alreadyLoaded is not null)
            {
                return alreadyLoaded;
            }

            string candidate = Path.Combine(managedDirectory, $"{name.Name}.dll");
            return File.Exists(candidate)
                ? AssemblyLoadContext.Default.LoadFromAssemblyPath(candidate)
                : null;
        };

        Assembly assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(assemblyPath);

        MethodRequest[] requests =
        {
            new("NewMovement", "Update", Type.EmptyTypes, "NewMovement.Update()"),
            new("GunControl", "Update", Type.EmptyTypes, "GunControl.Update()"),
            new("HookArm", "Update", Type.EmptyTypes, "HookArm.Update()"),
            new("NewMovement", "Slamdown", new[] { typeof(float) }, "NewMovement.Slamdown(float)"),
            new("FistControl", "Update", Type.EmptyTypes, "FistControl.Update()"),
        };

        var report = new StringBuilder();
        report.AppendLine($"Assembly: {assemblyPath}");
        report.AppendLine($"Generated: {DateTime.UtcNow:O} UTC");
        report.AppendLine();

        foreach (MethodRequest request in requests)
        {
            MethodBase? method = ResolveMethod(assembly, request);
            DumpMethodIL(report, method, request.Label);
        }

        string reportPath = Path.Combine(AppContext.BaseDirectory, "ultrakill-il-report.txt");
        File.WriteAllText(reportPath, report.ToString());
        Console.Write(report.ToString());
        Console.Error.WriteLine($"Saved report to: {reportPath}");
        return 0;
    }

    private static MethodBase? ResolveMethod(Assembly assembly, MethodRequest request)
    {
        Type? type = GetLoadableTypes(assembly)
            .FirstOrDefault(candidate => string.Equals(candidate.FullName, request.TypeName, StringComparison.Ordinal)
                || string.Equals(candidate.Name, request.TypeName, StringComparison.Ordinal));

        if (type is null)
        {
            return null;
        }

        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        return type.GetMethods(flags)
            .Where(method => string.Equals(method.Name, request.MethodName, StringComparison.Ordinal))
            .FirstOrDefault(method => ParametersMatch(method.GetParameters(), request.ParameterTypes));
    }

    private static IEnumerable<Type> GetLoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.OfType<Type>();
        }
    }

    private static bool ParametersMatch(IReadOnlyList<ParameterInfo> parameters, IReadOnlyList<Type> expectedTypes)
    {
        if (parameters.Count != expectedTypes.Count)
        {
            return false;
        }

        for (int i = 0; i < parameters.Count; i++)
        {
            if (parameters[i].ParameterType != expectedTypes[i])
            {
                return false;
            }
        }

        return true;
    }

    private static void DumpMethodIL(StringBuilder report, MethodBase? method, string label)
    {
        report.AppendLine(new string('=', 80));
        report.AppendLine(label);
        report.AppendLine(new string('=', 80));

        if (method is null)
        {
            report.AppendLine("Method not found.");
            report.AppendLine();
            return;
        }

        MethodBody? body = method.GetMethodBody();
        report.AppendLine($"Declaring type: {method.DeclaringType?.FullName ?? "<unknown>"}");
        report.AppendLine($"Signature: {FormatMethodSignature(method)}");

        if (body is null)
        {
            report.AppendLine("No method body.");
            report.AppendLine();
            return;
        }

        report.AppendLine($"MaxStack: {body.MaxStackSize}");
        report.AppendLine($"InitLocals: {body.InitLocals}");
        report.AppendLine($"Local count: {body.LocalVariables.Count}");

        if (body.LocalVariables.Count > 0)
        {
            for (int i = 0; i < body.LocalVariables.Count; i++)
            {
                LocalVariableInfo local = body.LocalVariables[i];
                string pinnedText = local.IsPinned ? " pinned" : string.Empty;
                report.AppendLine($"  V_{i}: {FormatTypeName(local.LocalType)}{pinnedText}");
            }
        }

        List<Instruction> instructions = ReadInstructions(method, body);
        List<Instruction> interestingInstructions = instructions
            .Where(instruction => InterestingKeywords.Any(keyword => instruction.ToString().Contains(keyword, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        report.AppendLine();
        report.AppendLine("Interesting references:");
        if (interestingInstructions.Count == 0)
        {
            report.AppendLine("  <none matched requested keywords>");
        }
        else
        {
            foreach (Instruction instruction in interestingInstructions)
            {
                report.AppendLine($"  {instruction}");
            }
        }

        report.AppendLine();
        report.AppendLine("Full IL:");
        foreach (Instruction instruction in instructions)
        {
            report.AppendLine(instruction.ToString());
        }

        if (body.ExceptionHandlingClauses.Count > 0)
        {
            report.AppendLine();
            report.AppendLine("Exception clauses:");
            foreach (ExceptionHandlingClause clause in body.ExceptionHandlingClauses)
            {
                string catchType = clause.CatchType?.FullName ?? "<none>";
                report.AppendLine(
                    $"  {clause.Flags}: try IL_{clause.TryOffset:X4}-IL_{clause.TryOffset + clause.TryLength:X4}, " +
                    $"handler IL_{clause.HandlerOffset:X4}-IL_{clause.HandlerOffset + clause.HandlerLength:X4}, catch {catchType}");
            }
        }

        report.AppendLine();
    }

    private static List<Instruction> ReadInstructions(MethodBase method, MethodBody body)
    {
        byte[] il = body.GetILAsByteArray() ?? Array.Empty<byte>();
        var instructions = new List<Instruction>(il.Length);
        Module module = method.Module;
        int position = 0;

        while (position < il.Length)
        {
            int offset = position;
            OpCode opCode = ReadOpCode(il, ref position);
            string operandText = ReadOperandText(il, ref position, opCode, method, body, module);
            instructions.Add(new Instruction(offset, opCode, operandText));
        }

        return instructions;
    }

    private static OpCode ReadOpCode(byte[] il, ref int position)
    {
        byte value = il[position++];
        if (value != 0xFE)
        {
            return SingleByteOpCodes[value];
        }

        return MultiByteOpCodes[il[position++]];
    }

    private static string ReadOperandText(byte[] il, ref int position, OpCode opCode, MethodBase method, MethodBody body, Module module)
    {
        switch (opCode.OperandType)
        {
            case OperandType.InlineNone:
                return string.Empty;

            case OperandType.ShortInlineI:
                return ((sbyte)il[position++]).ToString();

            case OperandType.InlineI:
                return BitConverter.ToInt32(il, Advance(ref position, 4)).ToString();

            case OperandType.InlineI8:
                return BitConverter.ToInt64(il, Advance(ref position, 8)).ToString();

            case OperandType.ShortInlineR:
                return BitConverter.ToSingle(il, Advance(ref position, 4)).ToString("R");

            case OperandType.InlineR:
                return BitConverter.ToDouble(il, Advance(ref position, 8)).ToString("R");

            case OperandType.ShortInlineBrTarget:
            {
                int baseOffset = position + 1;
                int target = baseOffset + (sbyte)il[position++];
                return $"IL_{target:X4}";
            }

            case OperandType.InlineBrTarget:
            {
                int rawOffset = BitConverter.ToInt32(il, Advance(ref position, 4));
                int target = position + rawOffset;
                return $"IL_{target:X4}";
            }

            case OperandType.ShortInlineVar:
            {
                byte index = il[position++];
                return FormatVariableOperand(opCode, index, method, body);
            }

            case OperandType.InlineVar:
            {
                ushort index = BitConverter.ToUInt16(il, Advance(ref position, 2));
                return FormatVariableOperand(opCode, index, method, body);
            }

            case OperandType.InlineSwitch:
            {
                int caseCount = BitConverter.ToInt32(il, Advance(ref position, 4));
                int[] targets = new int[caseCount];
                int baseOffset = position + (caseCount * 4);
                for (int i = 0; i < caseCount; i++)
                {
                    targets[i] = baseOffset + BitConverter.ToInt32(il, Advance(ref position, 4));
                }

                return string.Join(", ", targets.Select(target => $"IL_{target:X4}"));
            }

            case OperandType.InlineString:
            {
                int token = BitConverter.ToInt32(il, Advance(ref position, 4));
                string value = SafeResolve(() => module.ResolveString(token), $"string-token 0x{token:X8}");
                return $"\"{EscapeString(value)}\" (token 0x{token:X8})";
            }

            case OperandType.InlineField:
            {
                int token = BitConverter.ToInt32(il, Advance(ref position, 4));
                return FormatResolvedToken(module, token, method, ResolveKind.Field);
            }

            case OperandType.InlineMethod:
            {
                int token = BitConverter.ToInt32(il, Advance(ref position, 4));
                return FormatResolvedToken(module, token, method, ResolveKind.Method);
            }

            case OperandType.InlineType:
            {
                int token = BitConverter.ToInt32(il, Advance(ref position, 4));
                return FormatResolvedToken(module, token, method, ResolveKind.Type);
            }

            case OperandType.InlineTok:
            {
                int token = BitConverter.ToInt32(il, Advance(ref position, 4));
                return FormatResolvedToken(module, token, method, ResolveKind.Any);
            }

            case OperandType.InlineSig:
            {
                int token = BitConverter.ToInt32(il, Advance(ref position, 4));
                byte[] signature = SafeResolve(() => module.ResolveSignature(token), Array.Empty<byte>());
                return $"signature {BitConverter.ToString(signature)} (token 0x{token:X8})";
            }

            default:
                return $"<unsupported operand type {opCode.OperandType}>";
        }
    }

    private static int Advance(ref int position, int bytes)
    {
        int start = position;
        position += bytes;
        return start;
    }

    private static string FormatVariableOperand(OpCode opCode, int index, MethodBase method, MethodBody body)
    {
        string name = opCode.Name ?? string.Empty;
        if (name.Contains("arg", StringComparison.Ordinal))
        {
            return FormatArgument(index, method);
        }

        if (name.Contains("loc", StringComparison.Ordinal))
        {
            if (index >= 0 && index < body.LocalVariables.Count)
            {
                LocalVariableInfo local = body.LocalVariables[index];
                return $"V_{index} ({FormatTypeName(local.LocalType)})";
            }

            return $"V_{index}";
        }

        return index.ToString();
    }

    private static string FormatArgument(int index, MethodBase method)
    {
        if (!method.IsStatic && index == 0)
        {
            return $"arg0 (this: {FormatTypeName(method.DeclaringType)})";
        }

        int parameterIndex = method.IsStatic ? index : index - 1;
        ParameterInfo[] parameters = method.GetParameters();
        if (parameterIndex >= 0 && parameterIndex < parameters.Length)
        {
            ParameterInfo parameter = parameters[parameterIndex];
            string parameterName = string.IsNullOrWhiteSpace(parameter.Name) ? $"arg{index}" : parameter.Name!;
            return $"arg{index} ({FormatTypeName(parameter.ParameterType)} {parameterName})";
        }

        return $"arg{index}";
    }

    private static string FormatResolvedToken(Module module, int token, MethodBase context, ResolveKind kind)
    {
        string fallback = $"token 0x{token:X8}";
        object? resolved = kind switch
        {
            ResolveKind.Field => SafeResolve(() => module.ResolveField(token, GetTypeArguments(context), GetMethodArguments(context)), null as object),
            ResolveKind.Method => SafeResolve(() => module.ResolveMethod(token, GetTypeArguments(context), GetMethodArguments(context)), null as object),
            ResolveKind.Type => SafeResolve(() => module.ResolveType(token, GetTypeArguments(context), GetMethodArguments(context)), null as object),
            ResolveKind.Any => ResolveAny(module, token, context),
            _ => null,
        };

        return resolved is null
            ? fallback
            : $"{FormatResolvedMember(resolved)} (token 0x{token:X8})";
    }

    private static object? ResolveAny(Module module, int token, MethodBase context)
    {
        Type[]? typeArguments = GetTypeArguments(context);
        Type[]? methodArguments = GetMethodArguments(context);

        return SafeResolve(() => module.ResolveMember(token, typeArguments, methodArguments), null as object)
            ?? SafeResolve(() => module.ResolveMethod(token, typeArguments, methodArguments), null as object)
            ?? SafeResolve(() => module.ResolveField(token, typeArguments, methodArguments), null as object)
            ?? SafeResolve(() => module.ResolveType(token, typeArguments, methodArguments), null as object)
            ?? SafeResolve(() => module.ResolveString(token), null as object)
            ?? SafeResolve(() => module.ResolveSignature(token), null as object);
    }

    private static Type[]? GetTypeArguments(MethodBase context)
    {
        Type? declaringType = context.DeclaringType;
        return declaringType is not null && declaringType.IsGenericType
            ? declaringType.GetGenericArguments()
            : null;
    }

    private static Type[]? GetMethodArguments(MethodBase context)
    {
        return context.IsGenericMethod ? context.GetGenericArguments() : null;
    }

    private static string FormatResolvedMember(object resolved)
    {
        return resolved switch
        {
            MethodBase method => FormatMethodSignature(method),
            FieldInfo field => $"{FormatTypeName(field.FieldType)} {FormatTypeName(field.DeclaringType)}.{field.Name}",
            Type type => FormatTypeName(type),
            string text => $"\"{EscapeString(text)}\"",
            byte[] signature => $"signature {BitConverter.ToString(signature)}",
            MemberInfo member => $"{FormatTypeName(member.DeclaringType)}.{member.Name}",
            _ => resolved.ToString() ?? "<unknown>",
        };
    }

    private static string FormatMethodSignature(MethodBase method)
    {
        string declaringType = FormatTypeName(method.DeclaringType);
        string parameters = string.Join(", ", method.GetParameters().Select(parameter =>
            $"{FormatTypeName(parameter.ParameterType)} {parameter.Name}"));

        if (method is MethodInfo methodInfo)
        {
            return $"{FormatTypeName(methodInfo.ReturnType)} {declaringType}.{method.Name}({parameters})";
        }

        return $"{declaringType}.{method.Name}({parameters})";
    }

    private static string FormatTypeName(Type? type)
    {
        if (type is null)
        {
            return "<null>";
        }

        if (!type.IsGenericType)
        {
            return type.FullName ?? type.Name;
        }

        string genericTypeName = type.GetGenericTypeDefinition().FullName ?? type.Name;
        int tickIndex = genericTypeName.IndexOf('`');
        if (tickIndex >= 0)
        {
            genericTypeName = genericTypeName[..tickIndex];
        }

        string genericArguments = string.Join(", ", type.GetGenericArguments().Select(FormatTypeName));
        return $"{genericTypeName}<{genericArguments}>";
    }

    private static string EscapeString(string value)
    {
        return value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("\t", "\\t", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);
    }

    private static T SafeResolve<T>(Func<T> resolver, T fallback)
    {
        try
        {
            return resolver();
        }
        catch
        {
            return fallback;
        }
    }

    private enum ResolveKind
    {
        Any,
        Field,
        Method,
        Type,
    }
}
