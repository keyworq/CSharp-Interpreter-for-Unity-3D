//-----------------------------------------------------------------------
// <summary>
// CSI: A simple C# interpreter
// </summary>
// <copyright file="interpreter.cs" company="Tiaan.com">
//   Copyright (c) 2008-2010 Tiaan Geldenhuys
//   Copyright (c) 2005 Steve Donovan
//
//   Permission is hereby granted, free of charge, to any person
//   obtaining a copy of this software and associated documentation
//   files (the "Software"), to deal in the Software without
//   restriction, including without limitation the rights to use,
//   copy, modify, merge, publish, distribute, sublicense, and/or
//   sell copies of the Software, and to permit persons to whom the
//   Software is furnished to do so, subject to the following
//   conditions:
//
//   The above copyright notice and this permission notice shall be
//   included in all copies or substantial portions of the Software.
//
//   THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
//   EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES
//   OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
//   NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT
//   HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY,
//   WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING
//   FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR
//   OTHER DEALINGS IN THE SOFTWARE.
// </copyright>
//-----------------------------------------------------------------------
namespace CSI
{
    using System;
    using System.CodeDom.Compiler;
    using System.Collections;
    using System.IO;
    using System.Reflection;
    using System.Text;
    using System.Text.RegularExpressions;
    using Microsoft.CSharp;

    public interface IConsole
    {
        void ReadLineAsync(Interpreter.InputHandler callback);
        string Write(string s);
        int GetLineWidth();
        int GetMaxLines();
    }

    public class Utils
    {
        static Type lastClass = null;
        public static Interpreter interpreter;

        // It's possible to load scripts from within the interpreter.
        public static void Include(string file)
        {
            interpreter.ReadIncludeFile(file);
        }
        private static System.Collections.Generic.Dictionary<string, Type> typeDictionary =
            new System.Collections.Generic.Dictionary<string, Type>(StringComparer.Ordinal);
        private static object typeDictionaryLock = new object();

        public static Type GetType(string typeName)
        {
            Type type;
            lock (typeDictionaryLock)
            {
                if (typeDictionary.TryGetValue(typeName, out type))
                    return type;
            }

            type = Type.GetType(typeName);
            if (type == null)
            {
                foreach (System.Reflection.Assembly assembly in System.AppDomain.CurrentDomain.GetAssemblies())
                {
                    try
                    {
                        type = assembly.GetType(typeName);
                        if (type != null)
                        {
                            break;
                        }
                    }
                    catch
                    {
                        // Skip problematic assemblies
                    }
                }

                if (type == null)
                {
                    string fullTypeName;
                    foreach (string nameSpace in interpreter.GetNamespaces())
                    {
                        fullTypeName = nameSpace + "." + typeName;
                        foreach (System.Reflection.Assembly assembly in System.AppDomain.CurrentDomain.GetAssemblies())
                        {
                            try
                            {
                                type = assembly.GetType(fullTypeName);
                                if (type != null)
                                {
                                    break;
                                }
                            }
                            catch
                            {
                                // Skip problematic assemblies
                            }
                        }

                        if (type != null)
                        {
                            break;
                        }
                    }
                }
            }

            // Cache the lookup result to speeds up subsequent lookups
            // NOTE: Failed lookups are also cached by inserting null values, 
            //       which prevents additional lengthy repeats of the process
            lock (typeDictionaryLock)
            {
                if (typeDictionary.ContainsKey(typeName))
                {
                    // Compensate for a possible race condition
                    if (typeDictionary[typeName] != null)
                    {
                        type = typeDictionary[typeName];
                    }
                    else if (type != null)
                    {
                        typeDictionary[typeName] = type;
                    }
                }
                else
                {
                    typeDictionary.Add(typeName, type);
                }
            }

            return type;
        }

        public static void Meta(object objectOrStaticType)
        {
            Meta(objectOrStaticType, null /* memberNameFilter */);
        }

        public static void Meta(object objectOrStaticType, string memberNameFilter)
        {
            string[] memberNames = GetMeta(objectOrStaticType, memberNameFilter);
            if ((memberNames == null) ||
                (memberNames.Length <= 0))
            {
                return;
            }

            foreach (string memberName in memberNames)
            {
                Write(memberName);
                Write(" ");
            }

            Write(Environment.NewLine);
        }

        public static string[] GetMeta(object objectOrStaticType)
        {
            return GetMeta(objectOrStaticType, null /* memberNameFilter */);
        }

        public static string[] GetMeta(object objectOrStaticType, string memberNameFilter)
        {
            if (objectOrStaticType == null)
            {
                objectOrStaticType = lastClass;
                if (objectOrStaticType == null)
                {
                    return null;
                }
            }

            Type type;
            MemberInfo[] members;
            if (objectOrStaticType is Type)
            {
                // Query static members
                type = (Type)objectOrStaticType;
                members = type.GetMembers(BindingFlags.Static | BindingFlags.Public | BindingFlags.FlattenHierarchy);
            }
            else
            {
                // Query instance members
                type = objectOrStaticType.GetType();
                members = type.GetMembers(BindingFlags.Instance | BindingFlags.Public | BindingFlags.FlattenHierarchy);
            }

            if ((members == null) || (members.Length <= 0))
            {
                return null;
            }

            // Sort alphabetically -- without LINQ :(
            lastClass = type;
            string[] memberNames = new string[members.Length];
            for (int index = members.Length - 1; index >= 0; index--)
            {
                MemberInfo member = members[index];
                if (member is MethodInfo)
                {
                    MethodInfo methodInfo = (MethodInfo)member;
                    if (((methodInfo.Attributes & MethodAttributes.SpecialName) == MethodAttributes.SpecialName) &&
                        (member.Name.StartsWith("get_") || member.Name.StartsWith("set_")))
                    {
                        memberNames[index] = member.Name.Substring(4);
                    }
                    else
                    {
                        memberNames[index] = member.Name;
                    }
                }
                else
                {
                    memberNames[index] = member.Name;
                }
            }

            Array.Sort(memberNames, members, StringComparer.OrdinalIgnoreCase);

            Regex filterRegex;
            if (string.IsNullOrEmpty(memberNameFilter))
            {
                filterRegex = null;
            }
            else
            {
                filterRegex = new Regex(memberNameFilter, RegexOptions.IgnoreCase);
            }

            string lastName = null;
            System.Collections.Generic.List<string> resultMemberNames =
                new System.Collections.Generic.List<string>();
            for (int index = 0; index < members.Length; index++)
            {
                string name = memberNames[index];
                MemberInfo member = members[index];
                if (string.Equals(lastName, name, StringComparison.Ordinal) ||
                    ((filterRegex != null) && (!filterRegex.IsMatch(name)) && (!filterRegex.IsMatch(member.Name))))
                {
                    continue;
                }

                lastName = name;
                resultMemberNames.Add(name);
            }

            return (resultMemberNames.Count <= 0) ? null : resultMemberNames.ToArray();
        }

        public static void MInfo(object ctype, string mname)
        {
            Type t;
            if (ctype == null)
            {
                if (lastClass != null)
                    ctype = lastClass;
                else
                    return;
            }
            if (ctype is String)
            {
                string cname = (string)ctype;
                if (cname.Length < 7 || cname.Substring(0, 7) != "System.")
                    cname = "System." + cname;
                t = GetType(cname);
                if (t == null) throw (new Exception("is not a type"));
            }
            else
                if (!(ctype is Type))
                    t = ctype.GetType();
                else
                    t = (Type)ctype;
            lastClass = t;
            try
            {
                string lastName = "";
                int k = 0;
                foreach (MethodInfo mi in t.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.FlattenHierarchy))
                {
                    if (mname == null)
                    {
                        if (mi.Name != lastName && mi.Name.IndexOf('_') == -1)
                        {
                            lastName = mi.Name;
                            Write(lastName);
                            if (++k == 5)
                            {
                                Print();
                                k = 0;
                            }
                            else
                                Write(" ");
                        }
                    }
                    else
                    {
                        if (mi.Name == mname)
                        {
                            string proto = mi.ToString();
                            proto = proto.Replace("System.", "");
                            if (mi.IsStatic)
                                proto = "static " + proto;
                            if (mi.IsVirtual)
                                proto = "virtual " + proto;
                            Print(proto);
                        }
                    }
                }
                if (k > 0)
                    Print();
            }
            catch (Exception e)
            {
                Print("Error: " + ctype, e.Message);
            }
        }

        // This is a smart version of Printl, which tries to keep to a reasonable
        // line width, and won't go on forever. Also, strings and chars are quoted.
        public static void Dumpl(IEnumerable c)
        {
            Write("{");
            int nlines = 0;
            StringBuilder sb = new StringBuilder();
            int screenWidth = GetLineWidth();
            int maxLines = GetMaxLines() - 1;
            bool isFirstItem = true;
            foreach (object o in c)
            {
                if (isFirstItem)
                {
                    isFirstItem = false;
                }
                else
                {
                    sb.Append(',');
                }
                string s;
                if (o != null)
                {
                    s = o.ToString();
                    if (o is string) s = "\"" + s + "\"";
                    else
                        if (o is char) s = "\'" + s + "\'";
                }
                else
                    s = "<null>";
                if (sb.Length + s.Length >= screenWidth)
                {
                    if (sb.Length > 0)
                    {
                        Write(sb.ToString());
                        Write("\n");
                        sb.Length = 0;
                    }
                    if (++nlines > maxLines)
                    {
                        sb.Append(".....");
                        break;
                    }
                }
                sb.Append(s);
            }
            Write(sb.ToString() + "}\n");
        }

        public static void Printl(IEnumerable c)
        {
            foreach (object o in c)
            {
                if (o != null) Write(o.ToString());
                else Write("<null>");
                Write(" ");
            }
            Write("\n");
        }

        // a very convenient function for quick output ('Print' is easier to type than 'Console.WriteLine')
        public static void Print(params object[] obj)
        {
            Printl(obj);
        }

        public static void ReadLineAsync(Interpreter.InputHandler callback)
        {
            Interpreter.Console.ReadLineAsync(callback);
        }

        public static void Write(string s)
        {
            Interpreter.Console.Write(s);
        }

        public static int GetLineWidth()
        {
            return Interpreter.Console.GetLineWidth();
        }

        public static int GetMaxLines()
        {
            return Interpreter.Console.GetMaxLines();
        }
    }

    public class CodeChunk : Utils
    {
        public static bool DumpingValue = true;

        // the generated assemblies will have to override this method
        public virtual void Go(Hashtable V)
        {
        }

        // here's the template used to generate the assemblies
        public const string Template =
             @"$USES$
       class CsiChunk : CodeChunk { 
        public override void Go(Hashtable V) {
          $BODY$;
        }
      }";

        public static void Instantiate(Assembly a, Interpreter interp)
        {
            Hashtable table = interp.VarTable;
            try
            {
                CodeChunk chunk = (CodeChunk)a.CreateInstance("CsiChunk");
                chunk.Go(table);
                // we display the type and value of expressions.  The variable $_ is 
                // always set, which is useful if you want to save the result of the last
                // calculation.
                if (interp.returnsValue && DumpingValue)
                {
                    string stype;
                    object val = table["_"];
                    if (val == null)
                    {
                        stype = null;
                    }
                    else
                    {
                        Type type = val.GetType();
                        stype = interp.GetPublicRuntimeTypeName(type, true);
                        stype = "(" + (stype ?? interp.GetTypeName(type, true)) + ")";
                    }
                    if (val == null)
                    {
                        Print("null");
                    }
                    else
                        if (val is string)
                        {
                            Print(stype, "'" + val + "'");
                        }
                        else
                            if (val is IEnumerable)
                            {
                                Print(stype);
                                Dumpl((IEnumerable)val);
                            }
                            else
                                Print(stype, val);
                }
            }
            catch (Exception ex)
            {
                Print(ex.GetType() + " was thrown: " + ex.Message);
            }
        }
    }

    public class CsiFunctionContext : Utils
    {
        public Hashtable V;

        public const string Template =
             @"$USES$
       public class $CLASS$ : CsiFunctionContext { 
         public $BODY$
      }";

        public static void Instantiate(Assembly a, Hashtable table, string className, string funName)
        {
            try
            {
                CsiFunctionContext dll = (CsiFunctionContext)a.CreateInstance(className);
                dll.V = table;
                table[className] = dll;
            }
            catch (Exception ex)
            {
                Print(ex.GetType() + " was thrown: " + ex.Message);
            }
        }
    }

    public class Interpreter
    {
        public delegate bool InputHandler(string str);
        readonly Hashtable varTable;
        public delegate object GetUnknownItem(object key);
        public event GetUnknownItem OnGetUnknownItem;

        private class HashtableWithItemGetterHook : Hashtable
        {
            readonly Interpreter interpreter;
            public HashtableWithItemGetterHook(Interpreter interpreter)
            {
                this.interpreter = interpreter;
            }

            public override object this[object key]
            {
                get
                {
                    if (this.ContainsKey(key))
                    {
                        return base[key];
                    }

                    if (this.interpreter.OnGetUnknownItem == null)
                    {
                        return null;
                    }

                    return this.interpreter.OnGetUnknownItem(key);
                }

                set
                {
                    base[key] = value;
                }
            }
        }
        string namespaceString = "";
        ArrayList referenceList = new ArrayList();
        CSharpCodeProvider prov;
        ICodeGenerator gen;
        bool mustDeclare = false;
        bool showCode = false;
        StringBuilder sb = new StringBuilder();
        int bcount = 0;
        public bool returnsValue;
        public static IConsole Console;
        static string[] keywords = { "for", "foreach", "while", "using", "if", "switch", "do" };
        enum CHash { Expression, Assignment, Function, Class };

        MacroSubstitutor macro = new MacroSubstitutor();

        public Interpreter()
        {
            this.varTable = new HashtableWithItemGetterHook(this);
            AddNamespace("System");
            AddNamespace("System.Collections");
            AddReference("System.dll");  // Also works on Mono
            SetValue("interpreter", this);
            SetValue("_", this);
            Utils.interpreter = this;
            string fullExecutablePath = FullExecutablePath();
            if (File.Exists(fullExecutablePath))
            {
                AddReference(fullExecutablePath);
            }

            string localNamespace = typeof(Interpreter).Namespace;
            if (!string.IsNullOrEmpty(localNamespace))
            {
                AddNamespace(localNamespace);
            }

            ConstructorInfo constructorInfo = typeof(CSharpCodeProvider).GetConstructor(
                new Type[] { typeof(System.Collections.Generic.Dictionary<string, string>) });
            if (constructorInfo == null)
            {
                prov = new CSharpCodeProvider();
            }
            else
            {
                System.Collections.Generic.Dictionary<string, string> providerOptions = 
                    new System.Collections.Generic.Dictionary<string, string>();
                providerOptions.Add(
                    "CompilerVersion", 
                    (Environment.Version < new Version("4.0.0.0")) ? "v3.5" : "v4.0");
                prov = (CSharpCodeProvider)constructorInfo.Invoke(new object[] { providerOptions });
            }

            using (StringWriter stringWriter = new StringWriter())
            {
                gen = prov.CreateGenerator(stringWriter);
            }
        }

        // abosolute path of our executable, so it can always be found!
        public string FullExecutablePath()
        {
            Assembly thisAssembly = Assembly.GetAssembly(typeof(Interpreter));
            return new Uri(thisAssembly.CodeBase).LocalPath;
        }

        // the default .csi file is now found with the executable
        public string DefaultIncludeFile()
        {
            return Path.ChangeExtension(FullExecutablePath(), ".csi");
        }

        public bool ReadIncludeFile(string file)
        {
            if (File.Exists(file))
            {
                CodeChunk.DumpingValue = false;
                using (TextReader tr = File.OpenText(file))
                {
                    while (ProcessLine(tr.ReadLine()))
                        ;
                }
                CodeChunk.DumpingValue = true;
                return true;
            }

            return false;
        }

        public bool ReadIncludeCode(string code)
        {
            if (string.IsNullOrEmpty(code))
            {
                return false;
            }

            CodeChunk.DumpingValue = false;
            try
            {
                using (TextReader tr = new StringReader(code))
                {
                    while (ProcessLine(tr.ReadLine()))
                        ;
                }
            }
            finally
            {
                CodeChunk.DumpingValue = true;
            }

            return true;
        }

        public void SetValue(string name, object val)
        {
            varTable[name] = val;
        }

        static readonly Regex usingDirectiveRegex = new Regex(@"^\s*using\s+(?<namespace>[a-zA-Z_][a-zA-Z0-9_\.]*)\s*;?\s*$");

        public bool ProcessLine(string line)
        {
            // Statements inside braces will be compiled together
            if (line == null)
                return false;
            if (line == "")
                return true;
            if ((line[0] == '/') && ((line.Length < 2) || (line[1] != '*')))  // Let comment segments through: "/*"
            {
                if ((line.Length < 2) || (line[1] != '/'))  // Ignore comment lines: "//"
                    ProcessCommand(line);
                return true;
            }
            Match usingMatch = usingDirectiveRegex.Match(line);
            if (usingMatch.Success)
            {
                AddNamespace(usingMatch.Groups["namespace"].Value);
                return true;
            }
            sb.Append(line);
            // ignore {} inside strings!  Otherwise keep track of our block level
            bool insideQuote = false;
            for (int i = 0; i < line.Length; i++)
            {
                if (line[i] == '\"')
                    insideQuote = !insideQuote;
                if (!insideQuote)
                {
                    if (line[i] == '{') bcount++;
                    else
                        if (line[i] == '}') bcount--;
                }
            }
            if (bcount == 0)
            {
                string code = sb.ToString();
                sb = new StringBuilder();
                if (code != "")
                    ExecuteLine(code);
            }
            return true;
        }

        static Regex cmdSplit = new Regex(@"(\w+)($|\s+.+)");
        static Regex spaces = new Regex(@"\s+");

        void ProcessCommand(string line)
        {
            Match m = cmdSplit.Match(line);
            string cmd = m.Groups[1].ToString();
            string arg = m.Groups[2].ToString().TrimStart(null);
            switch (cmd)
            {
                case "n":
                    AddNamespace(arg);
                    break;
                case "r":
                    AddReference(arg);
                    break;
                case "v":
                    foreach (string v in varTable.Keys)
                        Utils.Print(v + " = " + varTable[v]);
                    break;
                case "dcl":
                    MustDeclare = !MustDeclare;
                    break;
                case "code": //  show code sent to compiler!
                    showCode = !showCode;
                    break;
                default:
                    // a macro may be used as a command; the line is split up and
                    // and supplied as arguments.
                    // For macros taking one argument, the whole line is supplied.
                    MacroEntry me = macro.Lookup(cmd);
                    if (me != null && me.Parms != null)
                    {
                        string[] parms;
                        if (me.Parms.Length > 1)
                            parms = spaces.Split(arg);
                        else
                            parms = new string[] { arg };
                        string s = macro.ReplaceParms(me, parms);
                        ExecuteLine(s);
                    }
                    else
                        Utils.Print("unrecognized command, or bad macro");
                    break;
            }
        }

        // the actual dynamic type of an object may not be publically available
        // (e.g. Type.GetMethods() returns an array of RuntimeMethodInfo)
        // so we look for the first public base class.
        static Type GetPublicRuntimeType(Type symType)
        {
            if (symType == null)
            {
                return symType;
            }
            // Find try to find a public class-type
            Type pubType = symType;
            while ((pubType != null) && (!pubType.IsPublic) && (!pubType.IsNestedPublic))
            {
                pubType = pubType.BaseType;
            }
            bool isObject = (pubType == typeof(object));
            if (isObject)
            {
                // Rather try to find a more specific interface-type 
                // instead, although we remember that this is an object and 
                // revert back to that type if no public interface is found
                pubType = null;
            }
            if (pubType == null)
            {
                // As a last resort, try to find a public interface-type
                System.Collections.Generic.List<Type> interfaceTypes =
                    new System.Collections.Generic.List<Type>();
                int interfaceIndex = 0;
                while ((pubType == null) &&
                    (symType != null) &&
                    (symType != typeof(object)))
                {
                    foreach (Type interfaceType in symType.GetInterfaces())
                    {
                        if (interfaceTypes.Contains(interfaceType))
                        {
                            continue;
                        }
                        interfaceTypes.Add(interfaceType);
                    }
                    symType = symType.BaseType;
                    while (interfaceIndex < interfaceTypes.Count)
                    {
                        Type interfaceType = interfaceTypes[interfaceIndex++];
                        if (interfaceType.IsPublic || interfaceType.IsNestedPublic)
                        {
                            pubType = interfaceType;
                            break;
                        }
                        interfaceType = interfaceType.BaseType;
                        if ((interfaceType == null) ||
                            (interfaceType == typeof(object)) ||
                            (interfaceTypes.Contains(interfaceType)))
                        {
                            continue;
                        }
                        interfaceTypes.Add(interfaceType);
                    }
                }
            }
            return pubType ?? ((isObject) ? typeof(object) : null);
        }

        internal string GetPublicRuntimeTypeName(Type symType, bool useSimplifiedNamespaces)
        {
            return GetTypeName(GetPublicRuntimeType(symType), useSimplifiedNamespaces);
        }

        internal string GetTypeName(Type symType, bool useSimplifiedNamespaces)
        {
            if (symType == null)
            {
                return null;
            }
            if ((symType.IsGenericType) &&
                (symType.Namespace == "System") &&
                (symType.GetGenericArguments().Length == 1))
            {
                if (symType == typeof(bool?))
                {
                    return "bool?";
                }
                if (symType == typeof(sbyte?))
                {
                    return "sbyte?";
                }
                if (symType == typeof(byte?))
                {
                    return "byte?";
                }
                if (symType == typeof(char?))
                {
                    return "char?";
                }
                if (symType == typeof(short?))
                {
                    return "short?";
                }
                if (symType == typeof(ushort?))
                {
                    return "ushort?";
                }
                if (symType == typeof(int?))
                {
                    return "int?";
                }
                if (symType == typeof(uint?))
                {
                    return "uint?";
                }
                if (symType == typeof(long?))
                {
                    return "long?";
                }
                if (symType == typeof(ulong?))
                {
                    return "ulong?";
                }
                if (symType == typeof(float?))
                {
                    return "float?";
                }
                if (symType == typeof(double?))
                {
                    return "double?";
                }
                if (symType == typeof(decimal?))
                {
                    return "decimal?";
                }
                if ((symType == typeof(Guid?)) ||
                    (symType == typeof(DateTime?)))
                {
                    return GetTypeName(symType.GetGenericArguments()[0],
                        useSimplifiedNamespaces) + "?";
                }
            }
            string symTypeName;
            using (StringWriter stringWriter = new StringWriter())
            {
                gen.GenerateCodeFromExpression(new System.CodeDom.
                    CodeTypeReferenceExpression(symType), stringWriter, null);
                symTypeName = stringWriter.ToString();
            }
            if (useSimplifiedNamespaces)
            {
                foreach (string namespacePrefix in new string[]
                {
                    "System.Collections.Generic.",
                    "System.Collections.",
                    "System."
                })
                {
                    symTypeName = symTypeName.Replace(namespacePrefix, "");
                }
            }
            return symTypeName;
        }

        static Regex quoteRegex = new Regex(@"^""|[^\\]""");
        static Regex dollarWord = new Regex(@"\$(?:\w+|\{[^\{\}\r\n\t\f\v""]+\})");
        static Regex dollarAssignment = new Regex(@"\$(?:\w+|\{[^\{\}\r\n\t\f\v""]+\})\s*=[^=]");
        static Regex plainWord = new Regex(@"\b[a-zA-Z_]\w*");
        static Regex plainAssignment = new Regex(@"\b[a-zA-Z_]\w*\s*=[^=]");
        static Regex assignment = dollarAssignment;
        static Regex wordPattern = dollarWord;

        // 'session variables' like $x will be replaced by ((LastType)V["x"]) where
        // LastType is the current type associated with the last value of V["x"].
        // The 'MustDeclare' mode; session variables don't need '$', but they must be
        // previously declared using var; declarations must look like this 'var <var> = <expr>'.
        string MassageInput(string s, out bool wasAssignment)
        {
            // process the words in reverse order when looking for assignments!
            MatchCollection words = wordPattern.Matches(s);
            Match[] wordArray = new Match[words.Count];
            words.CopyTo(wordArray, 0);
            Array.Reverse(wordArray);
            wasAssignment = false;
            bool varDeclaration = false;
            for (int i = 0; i < wordArray.Length; i++)
            {
                Match m = wordArray[i];
                // exclude matches found inside strings
                if (m.Index > 0 && (quoteRegex.Matches(s.Substring(0, m.Index)).Count % 2) != 0 && (quoteRegex.Matches(s.Substring(m.Index)).Count % 2) != 0)
                    continue;
                string sym = m.Value;
                if (!mustDeclare)     // strip the '$'
                    sym = sym.EndsWith("}") ? sym.Substring(2, sym.Length - 3) : sym.Substring(1);
                else
                { // either it's a declaration, or the var was previously declared.
                    if (sym == "var")
                        continue;
                    // are we preceded by 'var'?  If so, this is a declaration
                    if (i + 1 < wordArray.Length && wordArray[i + 1].Value == "var")
                        varDeclaration = true;
                    else if (varTable[sym] == null)
                        continue;
                }
                string symRef = "V[\"" + sym + "\"]";  // will index our hashtable
                // are we followed by an assignment operator?
                Match lhs = assignment.Match(s, m.Index);
                wasAssignment = lhs.Success && lhs.Index == m.Index;
                object symVal = varTable[sym];
                string symTypeName = (symVal == null) ? null : GetPublicRuntimeTypeName(symVal.GetType(), false);
                // unless we're on the LHS, try to strongly type this variable reference.
                if ((!string.IsNullOrEmpty(symTypeName)) && (!wasAssignment))
                {
                    symRef = "((" + symTypeName + ")" + symRef + ")";
                }
                s = wordPattern.Replace(s, symRef, 1, m.Index);
            }
            if (varDeclaration)
                s = s.Replace("var ", "");
            return s;
        }

        static Regex funDef = new Regex(@"^\s*[a-zA-Z]\w*\s+([a-zA-Z]\w*)\s*\(.*\)\s*{");
        static int nextAssembly = 1;

        void ExecuteLine(string codeStr)
        {
            // at this point we either have a line to be immediately compiled and evaluated,
            // or a function definition.
            CHash type = CHash.Expression;
            string className = null, assemblyName = null, funName = null;
            Match funMatch = funDef.Match(codeStr);
            if (funMatch.Success)
                type = CHash.Function;
            if (type == CHash.Function)
            {
                funName = funMatch.Groups[1].ToString();
                macro.RemoveMacro(funName);
                className = "Csi" + nextAssembly++;
                assemblyName = className + ".dll";
                codeStr = codeStr.Insert(funMatch.Groups[1].Index, "_");
            }
            codeStr = macro.ProcessLine(codeStr);
            if (codeStr == "")  // may have been a prepro statement!
                return;
            bool wasAssignment;
            codeStr = MassageInput(codeStr, out wasAssignment);
            if (wasAssignment)
                type = CHash.Assignment;
            CompilerResults cr = CompileLine(codeStr.TrimStart(), type, assemblyName, className);
            if (cr != null)
            {
                Assembly ass = cr.CompiledAssembly;
                if (type != CHash.Function)
                    CodeChunk.Instantiate(ass, this);
                else
                {
                    CsiFunctionContext.Instantiate(ass, varTable, className, funName);
                    string prefix = mustDeclare ? "" : "$";
                    macro.AddMacro(funName, prefix + className + "._" + funName, null);
                    AddReference(Path.GetFullPath(assemblyName));
                }
            }
        }

        CompilerResults CompileTemplate(CompilerParameters cp, string codeStr, CHash type, string className)
        {
            if (showCode)
                Utils.Print("code:", codeStr);
            string finalSource = CodeChunk.Template;
            if (type == CHash.Function)
                finalSource = CsiFunctionContext.Template;
            finalSource = finalSource.Replace("$USES$", namespaceString);
            finalSource = finalSource.Replace("$BODY$", codeStr);
            if (type == CHash.Function)
                finalSource = finalSource.Replace("$CLASS$", className);
            return prov.CompileAssemblyFromSource(cp, finalSource);
        }

        static Regex beginWord = new Regex(@"^\w+");

        string firstToken(string s)
        {
            Match m = beginWord.Match(s);
            return m.ToString();
        }

        bool word_within(string s, string[] strs)
        {
            return Array.IndexOf(strs, s) != -1;
        }

        CompilerResults CompileLine(string codeStr, CHash type, string assemblyName, string className)
        {
            CompilerParameters cp = new CompilerParameters();
            if (type == CHash.Function)
                cp.OutputAssembly = assemblyName;
            else
                cp.GenerateInMemory = true;

            foreach (string r in referenceList)
            {
#if DEBUG
                if (!System.Diagnostics.Debugger.IsAttached)
                    Utils.Print(r);
#endif
                cp.ReferencedAssemblies.Add(r);
            }

            string exprStr = codeStr;
            returnsValue = false;
            if (type == CHash.Expression)
            {
                if (codeStr[0] != '{' && !word_within(firstToken(codeStr), keywords))
                {
                    returnsValue = true;
                    exprStr = "V[\"_\"] = " + codeStr;
                }
            }
            CompilerResults cr = CompileTemplate(cp, exprStr, type, className);
            if (cr.Errors.HasErrors)
            {
                if (returnsValue)
                {
                    // we assumed that this expression did return a value; we were wrong.
                    // Try it again, without assignment to $_
                    returnsValue = false;
                    cp.OutputAssembly = null;  // Reset value, which is needed for Mono to work
                    CompilerResults cr2 = CompileTemplate(cp, codeStr, CHash.Expression, "");
                    if (!cr2.Errors.HasErrors)
                        return cr2;
                    try
                    {
                        bool firstErrorIsTypeConversion = false;
                        foreach (CompilerError err in cr.Errors)
                        {
                            // Check for "Cannot implicitly convert type `void' to `object'"
                            if (string.Equals("CS0029", err.ErrorNumber, StringComparison.OrdinalIgnoreCase)
                                && (!string.IsNullOrEmpty(err.ErrorText))
                                && (err.ErrorText.IndexOf("void", 0, StringComparison.OrdinalIgnoreCase) >= 0))
                            {
                                firstErrorIsTypeConversion = true;
                                break;
                            }
                        }
                        
                        bool secondErrorIsTooCommon = false;
                        foreach (CompilerError err in cr2.Errors)
                        {
                            // Check for "Only assignment, call, increment, decrement, and new object expressions can be used as a statement"
                            if (string.Equals("CS0201", err.ErrorNumber, StringComparison.OrdinalIgnoreCase))
                            {
                                secondErrorIsTooCommon = true;
                                break;
                            }
                        }
                        
                        // Usually show the second error, unless it is not very 
                        // informative and the first error is unlikely to have 
                        // been caused by our editing of the expression string
                        if ((!secondErrorIsTooCommon) || (firstErrorIsTypeConversion))
                        {
                            cr = cr2;
                        }
                    }
                    catch
                    {
                        // Assume that most recent error is mostly appropriate
                        cr = cr2;
                    }
                }
                ShowErrors(cr, codeStr);
                return null;
            }
            else
                return cr;
        }

        System.Collections.Generic.List<string> namespaces =
            new System.Collections.Generic.List<string>();

        public string[] GetNamespaces()
        {
            return this.namespaces.ToArray();
        }

        public bool AddNamespace(string ns)
        {
            foreach (string nameSpace in this.namespaces)
            {
                if (string.Equals(ns, nameSpace, StringComparison.Ordinal))
                {
                    return false;
                }
            }

            this.namespaces.Add(ns);
            namespaceString = namespaceString + "using " + ns + ";\n";
            return true;
        }

        public void AddReference(string r)
        {
            referenceList.Add(r);
        }

        void ShowErrors(CompilerResults cr, string codeStr)
        {
            StringBuilder sbErr;
            sbErr = new StringBuilder("Compiling string: ");
            sbErr.AppendFormat("'{0}'\n\n", codeStr);
            foreach (CompilerError err in cr.Errors)
            {
                sbErr.AppendFormat(
                    "{0}{1}\n", 
                    (err.ErrorText ?? string.Empty).Trim(), 
                    (string.IsNullOrEmpty(err.ErrorNumber) ? string.Empty : (" [" + err.ErrorNumber + "]")));
            }
            Utils.Print(sbErr.ToString());
        }

        public Hashtable VarTable
        {
            get { return varTable; }
        }

        public int BlockLevel
        {
            get { return bcount; }
        }

        public bool MustDeclare
        {
            get { return mustDeclare; }
            set
            {
                mustDeclare = value;
                wordPattern = mustDeclare ? plainWord : dollarWord;
                assignment = mustDeclare ? plainAssignment : dollarAssignment;
            }
        }
    }
}
