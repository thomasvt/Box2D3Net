﻿using System.Text;
using System.Text.RegularExpressions;

namespace Box2dNetGen
{
    internal class CsGenerator
    {
        private readonly string _extraUsings;
        private readonly Dictionary<string, string> _structTypeReplacer;
        private readonly Func<string, bool> _shouldGenerateInitCtor;
        private Dictionary<string, ApiStruct> _structs;
        private Dictionary<string, ApiEnum> _enums;
        private Dictionary<string, ApiDelegate> _delegates;
        private HashSet<string> _excludedTypes;

        public CsGenerator(string extraUsings, Dictionary<string, string> structTypeReplacer, Func<string, bool> shouldGenerateInitCtor)
        {
            _extraUsings = extraUsings;
            _structTypeReplacer = structTypeReplacer;
            _shouldGenerateInitCtor = shouldGenerateInitCtor;
        }

        public string GenerateCsCode(List<ApiConstant> constants, List<ApiStruct> structs, List<ApiDelegate> delegates, List<ApiFunction> functions,
            List<ApiEnum> enums, HashSet<string> excludedTypes)
        {
            _structs = structs.ToDictionary(s => s.Identifier);
            _enums = enums.ToDictionary(e => e.Identifier);
            _delegates = delegates.ToDictionary(d => d.Identifier);
            _excludedTypes = excludedTypes;

            var sb = new StringBuilder();
            sb.AppendLine($"// Generated by Box2dNetGen for Box2D v3 on {DateTime.Now:G}");
            sb.Append(@"
using System.Runtime.InteropServices;
");

            sb.AppendLine(_extraUsings);

            sb.Append(@"
// ReSharper disable InconsistentNaming

namespace Box2dNet.Interop
{
");

            var enumCount = GenerateEnums(enums, sb);
            var delegateCount = GenerateDelegates(delegates, sb);
            var structCount = GenerateStructs(structs, constants, sb);

            sb.Append(@"
/// <summary>
/// The (more or less) full Box2D v3.x API as PInvoke functions. (functions marked with C macro 'B2_API' in original sources)
/// </summary>
public static partial class B2Api
    {
#if DEBUG
        private const string Box2DLibrary = ""box2dd.dll"";
#else
        private const string Box2DLibrary = ""box2d.dll"";
#endif
");

            var constantCount = GenerateConstants(constants, sb);
            var functionCount = GenerateFunctions(functions, sb);

            sb.Append(@"
    }
}
");

            Console.WriteLine("Generated:");
            Console.WriteLine($"{enumCount} enums");
            Console.WriteLine($"{delegateCount} delegates");
            Console.WriteLine($"{structCount} structs");
            Console.WriteLine($"{constantCount} constants");
            Console.WriteLine($"{functionCount} functions");

            return sb.ToString();
        }

        private int GenerateFunctions(List<ApiFunction> functions, StringBuilder sb)
        {
            var cnt = 0;
            foreach (var apiFunction in functions)
            {
                try
                {
                    var sbI = new StringBuilder();
                    var parameters = GenerateParameterList(apiFunction.Parameters, true, true, out var containsDelegateParameters);
                    sbI.AppendLine();
                    AppendComment(sbI, apiFunction.Comment, apiFunction.ReturnType, apiFunction.Parameters.ToDictionary(p => p.Identifier, p => $"(Original C type: {p.Type})"));
                    sbI.AppendLine($"[DllImport(Box2DLibrary, CallingConvention = CallingConvention.Cdecl)]");
                    sbI.AppendLine(
                        $"public static extern {MapType(apiFunction.ReturnType, false, CodeDirection.ClrToNative, true, out _)} {apiFunction.Identifier}({parameters});");

                    if (containsDelegateParameters)
                    {
                        // also generate C# overload that accepts the strongly typed delegate instead of IntPtr.
                        sbI.AppendLine();
                        parameters = GenerateParameterList(apiFunction.Parameters, false, false, out _);
                        var arguments = GenerateArgumentList(apiFunction.Parameters);
                        AppendComment(sbI, apiFunction.Comment, apiFunction.ReturnType, apiFunction.Parameters.ToDictionary(p => p.Identifier, p => $"(Original C type: {p.Type})"));
                        sbI.AppendLine(
                            $"public static {MapType(apiFunction.ReturnType, false, CodeDirection.ClrToNative, true, out _)} {apiFunction.Identifier}({parameters})");
                        var @return = apiFunction.ReturnType != "void" ? "return " : "";
                        sbI.AppendLine(
                            $"{{\r\n    {@return}{apiFunction.Identifier}({arguments});\r\n}}");

                    }

                    sb.Append(sbI.ToString()); // commit
                    cnt++;
                }
                catch (NoGenException e)
                {
                    Console.WriteLine($"WARNING: skipping function '{apiFunction.Identifier}' because: {e.Message}");
                    ;
                }
            }

            return cnt;
        }

        private int GenerateConstants(List<ApiConstant> constants, StringBuilder sb)
        {
            var cnt = 0;
            foreach (var apiConstant in constants)
            {
                try
                {
                    sb.AppendLine();
                    AppendComment(sb, apiConstant.Comment, apiConstant.Type);
                    var code = $"public const {apiConstant.Type} {apiConstant.Identifier} = {apiConstant.Value};";
                    sb.AppendLine(code);
                    cnt++;
                }
                catch (NoGenException e)
                {
                    Console.WriteLine($"WARNING: skipping const '{apiConstant.Identifier}' because: {e.Message}");
                }
            }

            return cnt;
        }

        private record ClrStructField(string Identifier, string ClrType);

        private int GenerateStructs(List<ApiStruct> structs, List<ApiConstant> constants, StringBuilder sb)
        {
            var cnt = 0;
            foreach (var apiStruct in structs)
            {
                try
                {
                    var isUnsafe = false; // apiStruct.Fields.Any(f => f.IsFixedArray);
                    var sbI = new StringBuilder();
                    sbI.AppendLine();
                    AppendComment(sbI, apiStruct.Comment, null);
                    sbI.AppendLine("[StructLayout(LayoutKind.Sequential)]");
                    var unsafePart = isUnsafe ? "unsafe " : "";
                    sbI.AppendLine($"public {unsafePart} partial struct {apiStruct.Identifier}");
                    sbI.AppendLine("{");

                    var clrFields = new List<ClrStructField>(apiStruct.Fields.Count);
                    var noFieldsAreArray = true;

                    foreach (var field in apiStruct.Fields)
                    {
                        sbI.AppendLine();
                        AppendComment(sbI, field.Comment, field.Type);
                        var clrType = MapType(field.Type, false, CodeDirection.NativeToClr, true, out _);
                        if (field.IsFixedArray)
                        {
                            noFieldsAreArray = false;

                            var cte = constants.Find(c => c.Identifier == field.ArrayLength);

                            // this Marshal ByValArray thing doesn't seem to work. Get memory access issues at runtime ...
                            // var arrayLength = cte == null ? field.ArrayLength : "B2Api." + cte.Identifier;
                            // sbI.AppendLine($"  [MarshalAs(UnmanagedType.ByValArray, SizeConst = {arrayLength})]");

                            // ... so we just repeat the fields to mimic the inline array, and add a helper method to get them by index:
                            var arrayLength = int.Parse(cte == null ? field.ArrayLength : cte.Value);
                            var switchCases = new List<string>();
                            ;
                            for (var i = 0; i < arrayLength; i++)
                            {
                                sbI.AppendLine($"  public {clrType} {field.Identifier}{i};");
                                switchCases.Add($"      {i} => {field.Identifier}{i},\r\n");
                            }
                            sbI.AppendLine($"  /// <summary>.NET helper to get the inline {field.Identifier} by index. </summary>");
                            sbI.AppendLine($"  public {clrType} {field.Identifier}(int idx)\r\n  {{\r\n    return idx switch\r\n    {{");
                            sbI.Append(string.Join("", switchCases));
                            sbI.AppendLine($"      _ => throw new ArgumentOutOfRangeException(nameof(idx), \"There are only {arrayLength} {field.Identifier}.\")\r\n    }};\r\n  }}");
                        }
                        else
                        {
                            if (field.Type == "bool")
                                sbI.AppendLine("  [MarshalAs(UnmanagedType.U1)]"); // else .NET marshals as 32 bit, very fun to narrow down on that one.
                            sbI.AppendLine($"  public {clrType} {field.Identifier};");

                            if (noFieldsAreArray)
                                clrFields.Add(new ClrStructField(field.Identifier, clrType));
                        }
                    }

                    if (noFieldsAreArray)
                        GenerateInitCtor(sbI, apiStruct.Identifier, clrFields);

                    sbI.AppendLine("}");

                    sb.Append(sbI.ToString()); // commit

                    cnt++;
                }
                catch (NoGenException e)
                {
                    Console.WriteLine($"WARNING: skipping struct '{apiStruct.Identifier}' because: {e.Message}");
                    ;
                }
            }

            return cnt;
        }

        /// <summary>
        /// Generates a convenience constructor that accepts all fields as parameters. Only does this for simple structs.
        /// </summary>
        private void GenerateInitCtor(StringBuilder sb, string structIdentifier, IReadOnlyCollection<ClrStructField> fields)
        {
            if (!_shouldGenerateInitCtor(structIdentifier))
                return;
            sb.AppendLine();
            sb.Append($"  public {structIdentifier}(");
            sb.Append(string.Join(", ", fields.Select(f => $"in {f.ClrType} {f.Identifier}")));
            sb.AppendLine(")");
            sb.AppendLine("  {");
            sb.AppendLine(string.Join(Environment.NewLine, fields.Select(f => $"    this.{f.Identifier} = {f.Identifier};")));
            sb.AppendLine("  }");
            sb.AppendLine();
        }

        private int GenerateDelegates(List<ApiDelegate> delegates, StringBuilder sb)
        {
            var cnt = 0;
            foreach (var apiDelegate in delegates)
            {
                try
                {
                    sb.AppendLine();
                    AppendComment(sb, apiDelegate.Comment, apiDelegate.ReturnType, apiDelegate.Parameters.ToDictionary(p => p.Identifier, p =>
                        $"(Original C type: {p.Type})"));
                    var parameters = GenerateParameterList(apiDelegate.Parameters, true, true, out _);
                    sb.AppendLine("  [UnmanagedFunctionPointer(CallingConvention.Cdecl)]");
                    sb.AppendLine(
                        $"  public delegate {MapType(apiDelegate.ReturnType, false, CodeDirection.ClrToNative, true, out _)} {apiDelegate.Identifier}({parameters});");
                    cnt++;
                }
                catch (NoGenException e)
                {
                    Console.WriteLine($"WARNING: skipping delegate '{apiDelegate.Identifier}' because: {e.Message}");
                    ;
                }
            }

            return cnt;
        }

        /// <summary>
        /// Generates a list of parameters for a function.
        /// </summary>
        private string GenerateParameterList(List<ApiParameter> parameters, bool includeMarshalAttributes, bool delegateAsIntPtr, out bool containsDelegateParameters)
        {
            containsDelegateParameters = false;
            var csParameters = new List<string>();
            for (var i = 0; i < parameters.Count; i++)
            {
                var p = parameters[i];
                var nextParameterIdentifier = i < parameters.Count - 1 ? parameters[i + 1].Identifier : "";
                var isArray = p.Type.EndsWith("*") &&
                              (p.Identifier.ToLower().EndsWith("array") || nextParameterIdentifier.ToLower().Contains("count") || nextParameterIdentifier.ToLower().Contains("capacity")); // naive but seems to work for box2d: when a pointer parameter is followed by a 'count' parameter, it's an array, else it's a ptr to a single item.
                var attribute = "";
                if (includeMarshalAttributes)
                    attribute = (p.Type == "bool") ? "[MarshalAs(UnmanagedType.U1)] " : "";
                csParameters.Add($"{attribute}{MapType(p.Type, !isArray, CodeDirection.ClrToNative, delegateAsIntPtr, out var isDelegate)} {p.Identifier}");
                if (isDelegate)
                    containsDelegateParameters = true;
            }
            return string.Join(", ", csParameters);
        }

        /// <summary>
        /// Generates a list of arguments for a function call using the parameter names as arguments.
        /// </summary>
        private string GenerateArgumentList(List<ApiParameter> parameters)
        {
            var csArguments = new List<string>();
            for (var i = 0; i < parameters.Count; i++)
            {
                var p = parameters[i];
                var nextParameterIdentifier = i < parameters.Count - 1 ? parameters[i + 1].Identifier : "";
                var isArray = p.Type.EndsWith("*") &&
                              (p.Identifier.ToLower().EndsWith("array") || nextParameterIdentifier.ToLower().Contains("count") || nextParameterIdentifier.ToLower().Contains("capacity")); // naive but seems to work for box2d: when a pointer parameter is followed by a 'count' parameter, it's an array, else it's a ptr to a single item.
                MapType(p.Type, !isArray, CodeDirection.ClrToNative, false, out var isDelegate);
                if (isDelegate)
                {
                    csArguments.Add($"Marshal.GetFunctionPointerForDelegate({p.Identifier})");
                }
                else
                {
                    csArguments.Add($"{p.Identifier}");
                }
            }
            return string.Join(", ", csArguments);
        }

        private int GenerateEnums(List<ApiEnum> enums, StringBuilder sb)
        {
            var cnt = 0;
            foreach (var apiEnum in enums)
            {
                sb.AppendLine();
                sb.AppendLine($"public enum {apiEnum.Identifier}");
                sb.AppendLine("{");
                foreach (var field in apiEnum.Fields)
                {
                    sb.AppendLine();
                    AppendComment(sb, field.Comment, null);

                    var valuePart = field.Value == null ? "" : $" = {field.Value}";
                    sb.AppendLine($"  {field.Identifier}{valuePart},");
                }

                sb.AppendLine("}");

                cnt++;
            }

            return cnt;
        }

        private static Regex ParameterRegex = new("@param\\s+(?<identifier>\\S+)\\s+(?<description>.*)");

        private void AppendComment(StringBuilder sb, List<string> comment, string? returnType, Dictionary<string, string>? extraParameterComments = null)
        {
            var originalParameterComments = new Dictionary<string, string>();
            if (comment.Count > 0)
            {
                sb.AppendLine("  /// <summary>");
                foreach (var s in comment)
                {
                    var parameterMatch = ParameterRegex.Match(s);
                    if (parameterMatch.Success)
                    {
                        originalParameterComments.Add(parameterMatch.Groups["identifier"].Value, parameterMatch.Groups["description"].Value);
                    }
                    else
                    {
                        sb.AppendLine("  /// " + s);
                    }
                }
                sb.AppendLine("  /// </summary>");
            }

            if (!string.IsNullOrWhiteSpace(returnType))
                sb.AppendLine($"  /// <returns>Original C type: {returnType}</returns>");

            AppendParameterComments(sb, originalParameterComments, extraParameterComments); // if any...
        }

        private static void AppendParameterComments(StringBuilder sb, Dictionary<string, string> originalParameterComments,
            Dictionary<string, string>? extraParameterComments)
        {
            var parameterIdentifiers = originalParameterComments.Keys.ToList();
            if (extraParameterComments != null) parameterIdentifiers.AddRange(extraParameterComments.Keys);
            foreach (var parameter in parameterIdentifiers)
            {
                var parameterCommentLines = new List<string>();
                if (originalParameterComments.TryGetValue(parameter, out var originalComment))
                    parameterCommentLines.Add(originalComment);
                if (extraParameterComments != null && extraParameterComments.TryGetValue(parameter, out var extraComment))
                    parameterCommentLines.Add(extraComment);
                sb.AppendLine($"  /// <param name=\"{parameter}\">{string.Join("\r\n  /// ", parameterCommentLines)}</param>");
            }
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="cType"></param>
        /// <param name="isNotArray">Only relevant when the type is a pointer. Else ignored.</param>
        /// <param name="codeDirection"></param>
        /// <returns></returns>
        /// <exception cref="Exception"></exception>
        /// <exception cref="NoGenException"></exception>
        private string MapType(in string cType, bool isNotArray, CodeDirection codeDirection, bool returnDelegateAsIntPtr, out bool isDelegate)
        {
            isDelegate = false;
            var type = RemoveSpaces(cType);
            var isConst = false;
            if (type.StartsWith("const"))
            {
                isConst = true;
                type = type[5..];
            }
            var isPointer = type.EndsWith("*");
            if (isPointer) type = type.TrimEnd('*');

            // check intrinsic types:
            if (isPointer)
            {
                if (type == "char") return "string";
                if (type == "void") return "IntPtr /* void* */";
                if (isNotArray)
                {
                    if (type == "uint8_t") return "ref byte";
                    if (type == "uint16_t") return "ref ushort";
                    if (type == "uint32_t") return "ref uint";
                    if (type == "uint64_t") return "ref ulong";
                }
                else
                {
                    if (type == "uint8_t") return "IntPtr /* uint8_t* */";
                    if (type == "uint64_t") return "IntPtr /* uint64_t* */";
                }
            }
            else
            {
                if (type == "bool") return "bool";
                if (type == "float") return "float";
                if (type == "int") return "int";
                if (type == "uint8_t") return "byte";
                if (type == "int16_t") return "short";
                if (type == "uint16_t") return "ushort";
                if (type == "int32_t") return "int";
                if (type == "uint32_t") return "uint";
                if (type == "int64_t") return "long";
                if (type == "uint64_t") return "ulong";
                if (type == "unsignedint") return "uint";
                if (type == "void") return "void";
            }

            // type is a enum?
            if (_enums.TryGetValue(type, out var apiEnum))
            {
                if (isPointer) throw new Exception($"Used type seems to be enum '{apiEnum.Identifier}' but it's a pointer, which is suspicious in C.");
                return apiEnum.Identifier;
            }

            // type is a delegate?
            if (_delegates.TryGetValue(type, out var apiDelegate))
            {
                if (!isPointer) throw new Exception($"Used type seems to be delegate '{apiDelegate.Identifier}' but it's not a pointer, which is invalid C.");
                isDelegate = true;
                if (returnDelegateAsIntPtr)
                    return $"IntPtr";
                return type;
            }

            // type is a struct?
            var isReplacedByDotNetStruct = false;
            if (_structTypeReplacer.TryGetValue(type, out var dotNetStruct))
            {
                type = dotNetStruct;
                isReplacedByDotNetStruct = true;
            }

            var typeIsUserStruct = _structs.ContainsKey(type); // it's a struct defined by Box2D code
            if (typeIsUserStruct || isReplacedByDotNetStruct)
            {
                if (isPointer)
                {
                    if (isNotArray)
                    {
                        if (isConst) return "in " + type;
                        return "ref " + type;
                    }

                    if (codeDirection == CodeDirection.NativeToClr)
                        return "IntPtr"; // 'returning' arrays won't allocate .NET arrays. We have to accept the array as an IntPtr and loop over it. See helper method NativeArrayAsSpan in Box2dNet.

                    return $"{type}[]";
                }

                return type;
            }

            if (_excludedTypes.Contains(type))
            {
                throw new NoGenException($"Type '{cType}' is in the 'Excluded' list.");
            }
            throw new NoGenException($"No known mapping for type '{cType}'.");
        }

        private static string RemoveSpaces(string src)
        {
            return new string(src.Where(ch => ch != ' ').ToArray());
        }
    }

    public enum CodeDirection
    {
        ClrToNative,
        NativeToClr,
    }
}
