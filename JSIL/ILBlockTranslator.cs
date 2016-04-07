﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.ILAst;
using JSIL.Ast;
using JSIL.Compiler.Extensibility;
using JSIL.Internal;
using JSIL.Transforms;
using Microsoft.CSharp.RuntimeBinder;
using Mono.Cecil;
using Mono.Cecil.Cil;

using TypeInfo = JSIL.Internal.TypeInfo;
using SequencePoint = Mono.Cecil.Cil.SequencePoint;

namespace JSIL {
    public class ILBlockTranslator {
        public readonly AssemblyTranslator Translator;
        public readonly DecompilerContext Context;
        public readonly MethodReference ThisMethodReference;
        public readonly MethodDefinition ThisMethod;
        public readonly MethodSymbols Symbols;
        public readonly ILBlock Block;
        public readonly JavascriptFormatter Output = null;

        public readonly Dictionary<string, JSVariable> Variables = new Dictionary<string, JSVariable>();

        public readonly SpecialIdentifiers SpecialIdentifiers;

        public List<TypeReference> TemporaryVariableTypes = new List<TypeReference>();
        protected int RenamedVariableCount = 0;
        protected int UnlabelledBlockCount = 0;
        protected int NextSwitchId = 0;

        protected readonly Stack<bool> AutoCastingState = new Stack<bool>();
        protected readonly Stack<JSStatement> Blocks = new Stack<JSStatement>();

        static readonly ConcurrentCache<ILCode, System.Reflection.MethodInfo[]> NodeTranslatorCache = new ConcurrentCache<ILCode, System.Reflection.MethodInfo[]>();
        static readonly ConcurrentCache<ILCode, System.Reflection.MethodInfo[]>.CreatorFunction GetNodeTranslatorsUncached; 

        protected readonly Func<TypeReference, TypeReference> TypeReferenceReplacer;
        protected readonly IFunctionTransformer[] FunctionTransformers;

        private readonly HashSet<TypeReference> _rawTypes;

        static ILBlockTranslator () {
            GetNodeTranslatorsUncached = (code) => {
                var methodName = String.Format("Translate_{0}", code);
                var bindingFlags = System.Reflection.BindingFlags.Instance |
                            System.Reflection.BindingFlags.InvokeMethod |
                            System.Reflection.BindingFlags.NonPublic;

                var t = typeof(ILBlockTranslator);

                var methods = t.GetMember(
                        methodName, MemberTypes.Method, bindingFlags
                    ).OfType<System.Reflection.MethodInfo>().ToArray();

                if (methods.Length == 0) {
                    var alternateMethodName = methodName.Substring(0, methodName.LastIndexOf("_"));
                    methods = t.GetMember(
                            alternateMethodName, MemberTypes.Method, bindingFlags
                        ).OfType<System.Reflection.MethodInfo>().ToArray();
                }

                if (methods.Length == 0)
                    return null;

                return methods;
            };
        }

        public ILBlockTranslator (
            AssemblyTranslator translator, DecompilerContext context, 
            MethodReference methodReference, MethodDefinition methodDefinition,
            MethodSymbols methodSymbols,
            ILBlock ilb, IEnumerable<ILVariable> parameters, 
            IEnumerable<ILVariable> allVariables,
            Func<TypeReference, TypeReference> referenceReplacer = null
        ) {
            Translator = translator;
            Context = context;
            ThisMethodReference = methodReference;
            ThisMethod = methodDefinition;
            Block = ilb;
            TypeReferenceReplacer = referenceReplacer;

            Symbols = methodSymbols;

            SpecialIdentifiers = translator.GetSpecialIdentifiers(TypeSystem);

            _rawTypes = new HashSet<TypeReference>
            {
                TypeSystem.Boolean,
                TypeSystem.SByte,
                TypeSystem.Byte,
                TypeSystem.Int16,
                TypeSystem.UInt16,
                TypeSystem.Int32,
                TypeSystem.UInt32,
                TypeSystem.Single,
                TypeSystem.Double,
                TypeSystem.Char
            };

            if (methodReference.HasThis)
                Variables.Add("this", JSThisParameter.New(methodReference.DeclaringType, methodReference));

            foreach (var parameter in parameters) {
                if ((parameter.Name == "this") && (parameter.OriginalParameter.Index == -1))
                    continue;

                var jsp = new JSParameter(parameter.Name, parameter.Type, methodReference);

                Variables.Add(jsp.Name, jsp);
            }

            foreach (var variable in allVariables) {
                DeclareVariable(variable, methodReference);
            }

            var methodInfo = TypeInfo.Get(methodReference) as Internal.MethodInfo;
            TypeReference packedArrayAttributeType;
            var packedArrayArgumentNames = PackedArrayUtil.GetPackedArrayArgumentNames(methodInfo, out packedArrayAttributeType);

            if (packedArrayArgumentNames != null)
            foreach (var argumentName in packedArrayArgumentNames) {
                if (!Variables.ContainsKey(argumentName))
                    throw new ArgumentException("JSPackedArrayArguments specifies an argument named '" + argumentName + "' but no such argument exists");

                var variable = Variables[argumentName];

                var newVariableType = PackedArrayUtil.MakePackedArrayType(variable.GetActualType(TypeSystem), packedArrayAttributeType);
                if (newVariableType == null)
                    throw new ArgumentException("JSPackedArrayArguments specifies an argument named '" + argumentName + "' but it cannot be made a packed array");

                ChangeVariableType(Variables[argumentName], newVariableType);
            }

            AutoCastingState.Push(true);

            FunctionTransformers = Translator.FunctionTransformers;
        }

        protected TypeReference FixupReference (TypeReference reference) {
            // TODO: Expand !N to actual generic parameter it references

            if (TypeReferenceReplacer != null)
                return TypeReferenceReplacer(reference);
            else
                return reference;
        }

        // When a method body is replaced by the body of a proxy method, the method body
        //  will contain references to members of the proxy class instead of the class being
        //  proxied. We correct those references to point to the class being proxied (via
        //  a call to FixupReference) before doing MemberInfo lookups.
        protected T GetMember<T> (MemberReference member) 
            where T : class, IMemberInfo 
        {
            var declaringType = member.DeclaringType;
            declaringType = FixupReference(declaringType);

            var typeInfo = TypeInfo.Get(declaringType);
            if (typeInfo == null) {
                Console.Error.WriteLine("Warning: type not loaded: {0}", declaringType.FullName);
                return default(T);
            }

            var identifier = MemberIdentifier.New(TypeInfo, member);

            IMemberInfo result;
            if (!typeInfo.Members.TryGetValue(identifier, out result)) {
                // Console.Error.WriteLine("Warning: member not defined: {0}", member.FullName);
                return default(T);
            }

            return result as T;
        }

        protected JSIL.Internal.MethodInfo GetMethod (MethodReference method) {
            return GetMember<JSIL.Internal.MethodInfo>(method);
        }

        protected JSIL.Internal.FieldInfo GetField (FieldReference field) {
            return GetMember<JSIL.Internal.FieldInfo>(field);
        }

        internal MethodTypeFactory MethodTypes {
            get {
                return Translator.FunctionCache.MethodTypes;
            }
        }

        protected JSSpecialIdentifiers JS {
            get {
                return SpecialIdentifiers.JS;
            }
        }

        protected JSILIdentifier JSIL {
            get {
                return SpecialIdentifiers.JSIL;
            }
        }

        public ITypeInfoSource TypeInfo {
            get {
                return Translator.TypeInfoProvider;
            }
        }

        public TypeSystem TypeSystem {
            get {
                return Context.CurrentModule.TypeSystem;
            }
        }

        public JSBlockStatement Translate () {
            try {
                return TranslateNode(Block);
            } catch (AbortTranslation at) {
                Translator.WarningFormat("Method {0} not translated: {1}", ThisMethod.Name, at.Message);
                return null;
            }
        }

        public JSNode TranslateNode (ILNode node) {
            Translator.WarningFormat("Node        NYI: {0}", node.GetType().Name);

            return new JSUntranslatableStatement(node.GetType().Name);
        }

        public List<JSExpression> Translate (IList<ILExpression> values, IList<ParameterDefinition> parameters, bool hasThis) {
            var result = new List<JSExpression>();
            ParameterDefinition parameter;

            for (int i = 0, c = values.Count; i < c; i++) {
                var value = values[i];

                var parameterIndex = i;
                if (hasThis)
                    parameterIndex -= 1;

                if ((parameterIndex < parameters.Count) && (parameterIndex >= 0))
                    parameter = parameters[parameterIndex];
                else
                    parameter = null;

                var translated = TranslateNode(value);

                if ((parameter != null) && (parameter.ParameterType is ByReferenceType)) {
                    result.Add(new JSPassByReferenceExpression(translated));
                } else
                    result.Add(translated);
            }


            if (result.Any((je) => je == null)) {
                var errorString = new StringBuilder();
                errorString.AppendLine("The following expressions failed to translate:");

                for (var i = 0; i < values.Count; i++) {
                    if (result[i] == null)
                        errorString.AppendLine(values[i].ToString());
                }

                throw new InvalidDataException(errorString.ToString());
            }

            return result;
        }

        public List<JSExpression> Translate (IEnumerable<ILExpression> values) {
            var result = new List<JSExpression>();
            StringBuilder errorString = null;

            foreach (var value in values) {
                var translated = TranslateNode(value);

                if (translated == null) {
                    if (errorString == null) {
                        errorString = new StringBuilder();
                        errorString.AppendLine("The following expressions failed to translate:");
                    }

                    errorString.AppendLine(value.ToString());
                } else {
                    result.Add(translated);
                }
            }

            if (errorString != null)
                throw new InvalidDataException(errorString.ToString());

            return result;
        }

        protected bool NeedToRenameVariable (string name, TypeReference type) {
            if (String.IsNullOrWhiteSpace(name))
                return true;

            if (!Variables.ContainsKey(name))
                return false;

            if (!TypeUtil.TypesAreEqual(Variables[name].IdentifierType, type))
                return true;

            return false;
        }

        protected JSVariable DeclareVariable (ILVariable variable, MethodReference function) {
            if (variable.Name.StartsWith("<>c__")) {
                return DeclareVariableInternal(JSClosureVariable.New(variable, function));
            }

            var name = variable.Name;
            if (NeedToRenameVariable(name, variable.Type)) {
                if (!NeedToRenameVariable(variable.OriginalVariable.Name, variable.Type))
                    name = variable.OriginalVariable.Name;
                else
                    name = String.Format("{0}${1}", name, RenamedVariableCount++);
            }

            var result = JSVariable.New(name, variable.Type, function);
            return DeclareVariableInternal(result);
        }

        protected JSVariable DeclareVariableInternal (JSVariable variable) {
            JSVariable existing;
            if (Variables.TryGetValue(variable.Identifier, out existing)) {
                if (!TypeUtil.TypesAreEqual(variable.IdentifierType, existing.IdentifierType)) {
                    throw new InvalidOperationException(String.Format(
                        "A variable with the name '{0}' is already declared in this scope, with a different type.",
                        variable.Identifier
                    ));
                } else if (!variable.DefaultValue.Equals(existing.DefaultValue)) {
                    throw new InvalidOperationException(String.Format(
                        "A variable with the name '{0}' is already declared in this scope, with a different default value.",
                        variable.Identifier
                    ));
                }

                return existing;
            }

            Variables[variable.Identifier] = variable;

            return variable;
        }

        protected static bool CopyOnReturn (TypeReference type) {
            return TypeUtil.IsStruct(type);
        }

        protected JSExpression Translate_UnaryOp (ILExpression node, JSUnaryOperator op) {
            var inner = TranslateNode(node.Arguments[0]);
            var innerType = JSExpression.DeReferenceType(inner.GetActualType(TypeSystem));

            // Detect the weird pattern '!(x = y as z)' and transform it into '(x = y as z) != null'
            if (
                (op == JSOperator.LogicalNot) && 
                !TypeUtil.TypesAreAssignable(TypeInfo, TypeSystem.Boolean, innerType)
            ) {
                return new JSBinaryOperatorExpression(
                    JSOperator.Equal, inner, new JSDefaultValueLiteral(innerType), TypeSystem.Boolean
                );
            }

            // Insert correct casts when unary operators are applied to enums.
            if (TypeUtil.IsEnum(innerType) && TypeUtil.IsEnum(node.InferredType ?? node.ExpectedType)) {
                return JSCastExpression.New(
                    new JSUnaryOperatorExpression(
                        op,
                        JSCastExpression.New(inner, TypeSystem.Int32, TypeSystem),
                        TypeSystem.Int32
                    ),
                    node.InferredType ?? node.ExpectedType, TypeSystem
                );
            }

            return new JSUnaryOperatorExpression(
                op, inner, node.InferredType ?? node.ExpectedType
            );
        }

        public static bool ShouldSuppressAutoCastingForOperator (JSOperator op) {
            return (op is JSComparisonOperator);
        }

        protected JSExpression Translate_BinaryOp_Pointer (ILExpression node, JSBinaryOperator op, JSExpression lhs, JSExpression rhs) {
            if ((lhs is JSUntranslatableExpression) || (rhs is JSUntranslatableExpression))
                return new JSUntranslatableExpression(node);

            // We can end up with a pointer literal in an arithmetic expression.
            // In this case we want to switch it back to a normal integer literal so that the math operations work.
            var leftPointer = lhs as JSPointerLiteral;
            var rightPointer = rhs as JSPointerLiteral;
            if (!(op is JSAssignmentOperator)) {
                if (leftPointer != null)
                    lhs = new JSNativeIntegerLiteral((int)leftPointer.Value);
                if (rightPointer != null)
                    rhs = new JSNativeIntegerLiteral((int)rightPointer.Value);
            }

            var leftCast = lhs as JSPointerCastExpression;
            var rightCast = rhs as JSPointerCastExpression;

            // HACK: IL sometimes does (T*)((UInt64)lhs + (UInt64)rhs). Strip the conversions so we can make sense of it
            if (
                (leftCast != null) &&
                TypeUtil.IsIntegral(leftCast.NewType.Type)
            )
                lhs = leftCast.Pointer;

            if (
                (rightCast != null) &&
                TypeUtil.IsIntegral(rightCast.NewType.Type)
            )
                rhs = rightCast.Pointer;

            var leftType = lhs.GetActualType(TypeSystem);
            var rightType = rhs.GetActualType(TypeSystem);
            var leftIsNativeInt = TypeUtil.IsNativeInteger(leftType);
            var rightIsNativeInt = TypeUtil.IsNativeInteger(rightType);
            var leftIsPointerish = TypeUtil.IsPointer(leftType) || leftIsNativeInt;
            var rightIsPointerish = TypeUtil.IsPointer(rightType) || rightIsNativeInt;

            JSExpression result = null;
            if (leftIsPointerish && TypeUtil.IsIntegral(rightType)) {
                if (
                    (op == JSOperator.Add) ||
                    (op == JSOperator.AddAssignment)
                ) {
                    result = new JSPointerAddExpression(
                        lhs, rhs,
                        op == JSOperator.AddAssignment
                    );
                } else if (
                    (op == JSOperator.Subtract) ||
                    (op == JSOperator.SubtractAssignment)
                ) {
                    result = new JSPointerAddExpression(
                        lhs,
                        new JSUnaryOperatorExpression(JSOperator.Negation, rhs, TypeSystem.NativeInt()),
                        op == JSOperator.SubtractAssignment
                    );
                } else if (
                    (op == JSOperator.Divide) ||
                    (op == JSOperator.DivideAssignment)
                ) {
                    // This should only happen when the lhs is already a native int
                    if (TypeUtil.IsPointer(leftType))
                        return new JSUntranslatableExpression(node);

                    result = new JSBinaryOperatorExpression(
                        op, lhs, rhs, TypeSystem.NativeInt()
                    );
                } else if (
                    op is JSComparisonOperator
                ) {
                    if (!TypeUtil.IsPointer(leftType))
                        return new JSUntranslatableExpression(node);

                    result = new JSBinaryOperatorExpression(
                        op,
                        new JSDotExpression(lhs, new JSStringIdentifier("offsetInBytes", TypeSystem.Int32, true)),
                        rhs,
                        TypeSystem.Boolean
                    );
                } else {
                    // TODO: Implement ptr * <native-int>
                    // TODO: Implement ptr & <mask>
                    if (Debugger.IsAttached) {
                        Console.WriteLine("Debugger.Break()");
                        Console.Error.WriteLine("Debugger.Break()");
                        // Debugger.Break();
                    }
                }
            } else if (leftIsPointerish && rightIsPointerish) {
                if (op == JSOperator.Subtract) {
                    result = new JSPointerDeltaExpression(
                        lhs, rhs, TypeSystem.NativeInt()
                    );
                } else if (op is JSComparisonOperator) {
                    result = new JSPointerComparisonExpression(op, lhs, rhs, TypeSystem.Boolean);
                } else if ((op == JSOperator.Add) && (leftIsNativeInt || rightIsNativeInt)) {
                    if (leftIsNativeInt)
                        return new JSPointerAddExpression(rhs, lhs, false);
                    else /* if (rightIsNativeInt) */
                        return new JSPointerAddExpression(lhs, rhs, false);
                } else {
                    if (Debugger.IsAttached) {
                        Console.WriteLine("Debugger.Break()");
                        Console.Error.WriteLine("Debugger.Break()");
                        // Debugger.Break();
                    }
                }
            }

            if (result == null)
                return new JSUntranslatableExpression(node);
            else
                return result;
        }

        protected JSExpression Translate_BinaryOp (ILExpression node, JSBinaryOperator op) {
            // Detect attempts to perform pointer arithmetic
            if (TypeUtil.IsIgnoredType(node.Arguments[0].ExpectedType) ||
                TypeUtil.IsIgnoredType(node.Arguments[1].ExpectedType) ||
                TypeUtil.IsIgnoredType(node.Arguments[0].InferredType) ||
                TypeUtil.IsIgnoredType(node.Arguments[1].InferredType)
            ) {
                return new JSUntranslatableExpression(node);
            }

            // Detect attempts to perform pointer arithmetic on a local variable.
            // (ldloca produces a reference, not a pointer, so the previous check won't catch this.)
            if (
                (node.Arguments[0].Code == ILCode.Ldloca) &&
                !(op is JSAssignmentOperator)
            )
                return new JSUntranslatableExpression(node);

            // HACK: Auto-casting for pointer arithmetic is undesirable, because ILSpy
            //  infers incorrect types here
            var arePointersInvolved =
                TypeUtil.IsPointer(TypeUtil.DereferenceType(node.Arguments[0].ExpectedType)) ||
                TypeUtil.IsPointer(TypeUtil.DereferenceType(node.Arguments[0].InferredType)) ||
                TypeUtil.IsPointer(TypeUtil.DereferenceType(node.Arguments[1].ExpectedType)) ||
                TypeUtil.IsPointer(TypeUtil.DereferenceType(node.Arguments[1].InferredType));

            JSExpression lhs, rhs;
            AutoCastingState.Push(
                !ShouldSuppressAutoCastingForOperator(op) &&
                !arePointersInvolved
            );
            try {
                lhs = TranslateNode(node.Arguments[0]);
                rhs = TranslateNode(node.Arguments[1]);
            } finally {
                AutoCastingState.Pop();
            }

            if (TypeUtil.IsPointer(lhs.GetActualType(TypeSystem)))
                arePointersInvolved |= true;
            else if (TypeUtil.IsPointer(rhs.GetActualType(TypeSystem)))
                arePointersInvolved |= true;

            var boeLeft = lhs as JSBinaryOperatorExpression;
            if (
                (op is JSAssignmentOperator) &&
                (boeLeft != null) && !(boeLeft.Operator is JSAssignmentOperator)
            )
                return new JSUntranslatableExpression(node);

            if (arePointersInvolved)
                return Translate_BinaryOp_Pointer(node, op, lhs, rhs);

            var resultType = node.InferredType ?? node.ExpectedType;
            var leftType = lhs.GetActualType(TypeSystem);
            var rightType = rhs.GetActualType(TypeSystem);

            if (
                TypeUtil.IsIntegral(leftType) && 
                TypeUtil.IsIntegral(rightType) &&
                TypeUtil.IsIntegral(resultType) &&
                !(op is JSBitwiseOperator)
            ) {
                // HACK: Compensate for broken ILSpy type inference on certain forms of integer arithmetic

                var sizeofLeft = TypeUtil.SizeOfType(leftType);
                var sizeofRight = TypeUtil.SizeOfType(rightType);
                TypeReference largestType;

                if (sizeofLeft > sizeofRight) 
                    largestType = leftType;
                else
                    largestType = rightType;

                var sizeofInferred = (node.InferredType != null) && TypeUtil.IsIntegral(node.InferredType)
                    ? TypeUtil.SizeOfType(node.InferredType)
                    : 0;
                var sizeofExpected = (node.ExpectedType != null) && TypeUtil.IsIntegral(node.ExpectedType)
                    ? TypeUtil.SizeOfType(node.ExpectedType)
                    : 0;

                if (TypeUtil.SizeOfType(largestType) > Math.Max(sizeofInferred, sizeofExpected)) {
                    // FIXME: Get the sign right?
                    resultType = largestType;
                }
            }

            var result = new JSBinaryOperatorExpression(
                op, lhs, rhs, resultType
            );

            return result;
        }

        protected JSExpression HandleJSReplacement (
            MethodReference method, Internal.MethodInfo methodInfo, 
            JSExpression thisExpression, JSExpression[] arguments,
            TypeReference resultType, bool explicitThis
        ) {
            foreach (var transformer in FunctionTransformers) {
                var externalReplacement = transformer.MaybeReplaceMethodCall(
                    ThisMethodReference,
                    method, methodInfo, 
                    thisExpression, arguments, 
                    resultType, explicitThis
                );

                if (externalReplacement != null)
                    return externalReplacement;
            }

            var metadata = methodInfo.Metadata;
            if (metadata != null) {
                var parms = metadata.GetAttributeParameters("JSIL.Meta.JSReplacement");
                if (parms != null) {
                    var argsDict = new Dictionary<string, JSExpression>();

                    argsDict["assemblyof(executing)"] = new JSReflectionAssembly(ThisMethod.DeclaringType.Module.Assembly);

                    if (methodInfo.IsStatic) {
                        argsDict["this"] = new JSNullLiteral(TypeSystem.Object);
                        argsDict["typeof(this)"] = Translate_TypeOf(methodInfo.DeclaringType.Definition);
                        argsDict["etypeof(this)"] = Translate_TypeOf(methodInfo.DeclaringType.Definition.GetElementType());
                        argsDict["declaringType(method)"] = Translate_TypeOf(methodInfo.DeclaringType.Definition);
                        argsDict["explicitThis(method)"] = new JSBooleanLiteral(false);
                    }
                    else if (thisExpression != null)
                    {
                        argsDict["this"] = thisExpression;
                        argsDict["typeof(this)"] = Translate_TypeOf(thisExpression.GetActualType(TypeSystem));
                        argsDict["etypeof(this)"] = Translate_TypeOf(thisExpression.GetActualType(TypeSystem).GetElementType());
                        argsDict["this"] = thisExpression;
                        argsDict["declaringType(method)"] = Translate_TypeOf(methodInfo.DeclaringType.Definition);
                        argsDict["explicitThis(method)"] = new JSBooleanLiteral(explicitThis);
                    } 

                    var genericMethod = method as GenericInstanceMethod;
                    if (genericMethod != null) {
                        foreach (var kvp in methodInfo.GenericParameterNames.Zip(genericMethod.GenericArguments, (n, p) => new { Name = n, Value = p })) {
                            argsDict.Add(kvp.Name, new JSTypeOfExpression(kvp.Value));
                        }
                    }

                    foreach (var kvp in methodInfo.Parameters.Zip(arguments, (p, v) => new { p.Name, Value = v })) {
                        argsDict.Add(kvp.Name, kvp.Value);
                        var type = kvp.Value.GetActualType(TypeSystem);
                        argsDict["typeof(" + kvp.Name + ")"] = Translate_TypeOf(type);
                        var typeSpecification = type as TypeSpecification;
                        argsDict["etypeof(" + kvp.Name + ")"] = Translate_TypeOf(typeSpecification != null ? typeSpecification.ElementType : type.GetElementType());
                    }

                    var isConstantIfArgumentsAre = methodInfo.Metadata.HasAttribute("JSIL.Meta.JSIsPure");

                    var result = new JSVerbatimLiteral(
                        method.Name, (string)parms[0].Value, argsDict, resultType, isConstantIfArgumentsAre
                    );

                    return PackedArrayUtil.FilterInvocationResult(
                        method, methodInfo, 
                        result,                         
                        TypeInfo, TypeSystem
                    );
                }
            }

            return null;
        }

        protected JSExpression Translate_ConstructorReplacement (
            MethodReference constructor, Internal.MethodInfo constructorInfo, JSNewExpression newExpression
        ) {
            var instanceType = newExpression.GetActualType(TypeSystem);
            var jsr = HandleJSReplacement(
                constructor, constructorInfo, new JSNullLiteral(instanceType), newExpression.Arguments.ToArray(),
                instanceType, false
            );
            if (jsr != null)
                return jsr;

            return newExpression;
        }

        private JSIndirectVariable MakeIndirectVariable (string name) {
            return new JSIndirectVariable(Variables, name, ThisMethodReference);
        }

        internal JSExpression DoMethodReplacement (
            JSMethod method, JSExpression thisExpression, 
            JSExpression[] arguments, bool @virtual, bool @static, bool explicitThis, bool suppressThisClone
        ) {
            var methodInfo = method.Method;

            PackedArrayUtil.CheckInvocationSafety(method.Method, arguments, TypeSystem);

            bool retry;
            do {
                retry = false;
                var metadata = methodInfo.Metadata;
                if (metadata != null) {
                    var jsr = HandleJSReplacement(
                        method.Reference, methodInfo, thisExpression, arguments,
                        method.Reference.ReturnType, explicitThis
                    );
                    if (jsr != null)
                        return jsr;

                    // Proxy method bodies can call other methods declared on the proxy
                    //  that are actually stand-ins for methods declared on the proxied type.
                    if (
                        metadata.HasAttribute("JSIL.Proxy.JSNeverReplace") &&
                        !TypeUtil.TypesAreEqual(method.Reference.DeclaringType, methodInfo.DeclaringType.Definition) &&
                        !methodInfo.DeclaringType.IsProxy
                    ) {
                        var proxyTypeInfo = TypeInfo.GetExisting(method.Reference.DeclaringType);
                        if ((proxyTypeInfo != null) && proxyTypeInfo.IsProxy) {
                            var originalMethod =
                                (from m in methodInfo.DeclaringType.Definition.Methods
                                 let mi = TypeInfo.GetMethod(m)
                                 where (mi != null) &&
                                    mi.NamedSignature.Equals(methodInfo.NamedSignature)
                                 select m).FirstOrDefault();

                            if (originalMethod != null) {
                                methodInfo = TypeInfo.GetMethod(originalMethod);
                                method = new JSMethod(originalMethod, methodInfo, method.MethodTypes, method.GenericArguments);
                                retry = true;
                            }
                        }
                    }
                }
            } while (retry);

            if (methodInfo.IsIgnored)
                return new JSIgnoredMemberReference(true, methodInfo, new[] { thisExpression }.Concat(arguments).ToArray());

            JSExpression result = DoNonJSILMethodReplacement(method, arguments);
            if (result != null) 
                return result;

            result = DoJSILMethodReplacement(
                method.Method.DeclaringType.FullName, 
                method.Method.Name, 
                method,
                method.GenericArguments,
                arguments
            );
            if (result != null)
                return result;

            result = Translate_PropertyCall(thisExpression, method, arguments, @virtual, @static);
            if (result == null) {
                if (@static)
                    result = JSInvocationExpression.InvokeStatic(method.Reference.DeclaringType, method, arguments);
                else if (explicitThis)
                    result = JSInvocationExpression.InvokeBaseMethod(method.Reference.DeclaringType, method, thisExpression, arguments);
                else
                    result = JSInvocationExpression.InvokeMethod(method.Reference.DeclaringType, method, thisExpression, arguments, suppressThisClone: suppressThisClone);
            }

            result = PackedArrayUtil.FilterInvocationResult(
                method.Reference, method.Method, 
                result, 
                TypeInfo, TypeSystem
            );

            return result;
        }

        internal JSExpression DoJSILBuiltinsMethodReplacement (
            string methodName,
            IEnumerable<TypeReference> genericArguments,
            JSExpression[] arguments,
            bool forDynamic
        ) {
            switch (methodName) {
                case "CreateNamedFunction`1": {
                    JSExpression closureArg = null;
                    if (arguments.Length > 3)
                        closureArg = arguments[3];

                    return JSIL.CreateNamedFunction(
                        genericArguments.First(), arguments[0], arguments[1], arguments[2], closureArg
                    );
                }

                case "Eval":
                    return JSInvocationExpression.InvokeStatic(
                        JS.eval, arguments
                    );

                case "IsTruthy":
                    return new JSUnaryOperatorExpression(
                        JSOperator.LogicalNot, 
                        new JSUnaryOperatorExpression(JSOperator.LogicalNot, arguments.First(), TypeSystem.Boolean), 
                        TypeSystem.Boolean
                    );

                case "IsFalsy":
                    return new JSUnaryOperatorExpression(JSOperator.LogicalNot, arguments.First(), TypeSystem.Boolean);

                case "get_This":
                    return MakeIndirectVariable("this");

                case "get_IsJavascript":
                    return new JSBooleanLiteral(true);
            }

            return null;
        }

        internal JSExpression DoJSILMethodReplacement (
            string typeName,
            string methodName,
            JSMethod method, 
            IEnumerable<TypeReference> genericArguments, 
            JSExpression[] arguments
        ) {
            switch (typeName) {
                case "JSIL.Builtins":
                    return DoJSILBuiltinsMethodReplacement(methodName, genericArguments, arguments, method == null);

                case "JSIL.Verbatim": {
                    if (
                        (methodName == "Expression") || methodName.StartsWith("Expression`")
                    ) {
                        var expression = arguments[0] as JSStringLiteral;
                        if (expression == null)
                            throw new InvalidOperationException("JSIL.Verbatim.Expression must recieve a string literal as an argument");

                        JSExpression commaFirstClause = null;
                        IDictionary<string, JSExpression> argumentsDict = null;

                        if (arguments.Length > 1) {
                            var argumentsExpression = arguments[1];
                            var argumentsArray = argumentsExpression as JSNewArrayExpression;

                            if (method == null || method.Method.Parameters[1].ParameterType is GenericParameter) {
                                // This call was made dynamically or through generic version of method, so the parameters are not an array.

                                argumentsDict = new Dictionary<string, JSExpression>();

                                for (var i = 0; i < (arguments.Length - 1); i++)
                                    argumentsDict.Add(String.Format("{0}", i), arguments[i + 1]);
                            } else if (argumentsArray == null) {
                                // The array is static so we need to pull elements out of it after assigning it a name.
                                // FIXME: Only handles up to 40 elements.
                                var argumentsExpressionType = argumentsExpression.GetActualType(TypeSystem);
                                var temporaryVariable = MakeTemporaryVariable(argumentsExpressionType);
                                var temporaryAssignment = new JSBinaryOperatorExpression(JSOperator.Assignment, temporaryVariable, argumentsExpression, argumentsExpressionType);

                                commaFirstClause = temporaryAssignment;

                                argumentsDict = new Dictionary<string, JSExpression>();

                                for (var i = 0; i < 40; i++)
                                    argumentsDict.Add(String.Format("{0}", i), new JSIndexerExpression(temporaryVariable, JSLiteral.New(i)));
                            } else {
                                var argumentsArrayExpression = argumentsArray.SizeOrArrayInitializer as JSArrayExpression;

                                if (argumentsArrayExpression == null)
                                    throw new NotImplementedException("Literal array must have values");

                                argumentsDict = new Dictionary<string, JSExpression>();

                                int i = 0;
                                foreach (var value in argumentsArrayExpression.Values) {
                                    argumentsDict.Add(String.Format("{0}", i), value);

                                    i += 1;
                                }
                            }
                        }

                        var verbatimLiteral = new JSVerbatimLiteral(
                            methodName, expression.Value, argumentsDict
                        );

                        if (commaFirstClause != null)
                            return new JSCommaExpression(commaFirstClause, verbatimLiteral);
                        else
                            return verbatimLiteral;
                    } else {
                        throw new NotImplementedException("Verbatim method not implemented: " + methodName);
                    }
                    break;
                }

                case "JSIL.JSGlobal": {
                    if (methodName == "get_Item") {
                        var expression = arguments[0] as JSStringLiteral;
                        if (expression != null)
                            return new JSDotExpression(
                                JSIL.GlobalNamespace, new JSStringIdentifier(expression.Value, TypeSystem.Object, true)
                            );
                        else
                            return new JSIndexerExpression(
                                JSIL.GlobalNamespace, arguments[0], TypeSystem.Object
                            );
                    } else {
                        throw new NotImplementedException("JSGlobal method not implemented: " + methodName);
                    }
                    break;
                }

                case "JSIL.JSLocal": {
                    if (methodName == "get_Item") {
                        var expression = arguments[0] as JSStringLiteral;
                        if (expression == null)
                            throw new InvalidOperationException("JSLocal must recieve a string literal as an index");

                        return new JSStringIdentifier(expression.Value, TypeSystem.Object, true);
                    } else {
                        throw new NotImplementedException("JSLocal method not implemented: " + methodName);
                    }
                    break;
                }

                case "JSIL.Services": {
                    if (methodName == "Get") {
                        if (arguments.Length != 2)
                            throw new InvalidOperationException("JSIL.Services.Get must receive two arguments");

                        var serviceName = arguments[0];
                        var shouldThrow = arguments[1];

                        return JSInvocationExpression.InvokeStatic(
                            new JSRawOutputIdentifier(TypeSystem.Object, "JSIL.Host.getService"),
                            new[] {
                                serviceName,
                                new JSUnaryOperatorExpression(JSOperator.LogicalNot, shouldThrow, TypeSystem.Boolean)
                            }, true
                        );
                    } else {
                        throw new NotImplementedException("JSIL.Services method not implemented: " + methodName);
                    }
                    break;
                }
            }

            return null;
        }

        internal JSExpression DoNonJSILMethodReplacement (JSMethod method, JSExpression[] arguments) {
            switch (method.Method.Member.FullName) {
                // Doing this replacement here enables more elimination of temporary variables
                case "System.Type System.Type::GetTypeFromHandle(System.RuntimeTypeHandle)":
                case "System.Reflection.MethodBase System.Reflection.MethodBase::GetMethodFromHandle(System.RuntimeMethodHandle)":
                case "System.Reflection.MethodBase System.Reflection.MethodBase::GetMethodFromHandle(System.RuntimeMethodHandle,System.RuntimeTypeHandle)":
                case "System.Reflection.FieldInfo System.Reflection.FieldInfo::GetFieldFromHandle(System.RuntimeFieldHandle)":
                case "System.Reflection.FieldInfo System.Reflection.FieldInfo::GetFieldFromHandle(System.RuntimeFieldHandle,System.RuntimeTypeHandle)":
                    return arguments.First();
            }
            return null;
        }

        protected JSExpression Translate_PropertyCall (
            JSExpression thisExpression, JSMethod method, JSExpression[] arguments, bool @virtual, bool @static
        ) {
            var propertyInfo = method.Method.DeclaringProperty;
            if (propertyInfo == null)
                return null;

            if (propertyInfo.IsIgnored)
                return new JSIgnoredMemberReference(true, propertyInfo, arguments);

            // JS provides no way to override [], so keep it as a regular method call
            if (propertyInfo.Member.IsIndexer())
                return null;

            var parms = method.Method.Metadata.GetAttributeParameters("JSIL.Meta.JSReplacement") ??
                propertyInfo.Metadata.GetAttributeParameters("JSIL.Meta.JSReplacement");

            if (parms != null) {
                var argsDict = new Dictionary<string, JSExpression>();

                argsDict["this"] = thisExpression;
                argsDict["typeof(this)"] = Translate_TypeOf(thisExpression.GetActualType(TypeSystem));

                foreach (var kvp in method.Method.Parameters.Zip(arguments, (p, v) => new { p.Name, Value = v })) {
                    argsDict.Add(kvp.Name, kvp.Value);
                }

                return new JSVerbatimLiteral(
                    method.Reference.Name, (string)parms[0].Value, argsDict, propertyInfo.ReturnType
                );
            }

            var thisType = thisExpression.GetActualType(TypeSystem);
            var propertyReplacement = DoPropertyReplacement(thisExpression, thisType, method, arguments, @virtual, @static);
            if (propertyReplacement != null)
                return propertyReplacement;

            Func<bool, JSExpression> generate = (tq) => {
                var actualThis = @static 
                    ? new JSType(method.Method.DeclaringType.Definition) 
                    : thisExpression;

                if (
                    (method.Reference.DeclaringType is GenericInstanceType) && 
                    !method.Reference.HasThis
                )
                    actualThis = new JSType(method.Reference.DeclaringType);

                if ((propertyInfo.Member.GetMethod != null) && (method.Method.Member.Name == propertyInfo.Member.GetMethod.Name)) {

                    return new JSPropertyAccess(
                        actualThis, new JSProperty(method.Reference, propertyInfo), 
                        false, tq, 
                        new JSType(method.Reference.DeclaringType), method, 
                        @virtual
                    );
                } else {

                    if (arguments.Length == 0) {
                        throw new InvalidOperationException(String.Format(
                            "The property setter '{0}' was invoked without arguments",
                            method
                        ));
                    }

                    return new JSBinaryOperatorExpression(
                        JSOperator.Assignment,
                        new JSPropertyAccess(
                            actualThis, new JSProperty(method.Reference, propertyInfo), 
                            true, tq, 
                            new JSType(method.Reference.DeclaringType), method, 
                            @virtual
                        ),
                        arguments[0], propertyInfo.ReturnType
                    );
                }
            };

            // Accesses to a base property should go through a regular method invocation, since
            //  javascript properties do not have a mechanism for base access
            if (method.Method.Member.HasThis) {

                if (
                    !TypeUtil.TypesAreEqual(
                        method.Method.DeclaringType.Definition, 
                        TypeUtil.GetTypeDefinition(thisType)
                    ) && !@virtual
                )
                    return generate(true);
            }

            return generate(false);
        }

        private JSExpression DoPropertyReplacement (
            JSExpression thisExpression, TypeReference thisType, 
            JSMethod method, JSExpression[] arguments, 
            bool @virtual, bool @static
        ) {
            TypeReference methodType = method.Reference.DeclaringType;

            if (
                TypeUtil.IsNullable(thisType) || 
                // HACK: When translating methods of Nullable, thisType is not a generic instance.
                TypeUtil.IsNullable(methodType)
            ) {
                switch (method.Method.Name) {
                    case "get_HasValue":
                        return JSIL.NullableHasValue(thisExpression);

                    case "get_Value":
                        return JSIL.ValueOfNullable(thisExpression);
                }
            }

            return null;
        }

        protected bool ContainsLabels (ILNode root) {
            var label = root.GetSelfAndChildrenRecursive<ILLabel>().FirstOrDefault();
            return label != null;
        }


        //
        // IL Node Types
        //

        protected JSBlockStatement TranslateBlock (IEnumerable<ILNode> children) {
            JSBlockStatement result, currentBlock;

            currentBlock = result = new JSBlockStatement();

            foreach (var node in children) {
                var label = node as ILLabel;
                var expr = node as ILExpression;
                var isGoto = (expr != null) && (expr.Code == ILCode.Br);

                if (label != null) {
                    currentBlock = new JSBlockStatement {
                        Label = label.Name
                    };

                    result.Statements.Add(currentBlock);

                    continue;
                } else if (isGoto) {
                    currentBlock.Statements.Add(new JSExpressionStatement(new JSGotoExpression(
                        ((ILLabel)expr.Operand).Name
                    )));
                } else {
                    var translated = TranslateStatement(node);

                    if (translated != null) {
                        // Hoist statements from child blocks up into parent blocks if they're unlabelled.
                        // This makes things easier for LabelGroupBuilder and makes it possible for 
                        //  TranslateNode to produce multiple statements where necessary without breaking 
                        //  things.
                        var subBlock = translated as JSBlockStatement;
                        if (
                            (subBlock != null) && 
                            (subBlock.Label == null) && 
                            (subBlock.GetType() == typeof(JSBlockStatement))
                        ) {
                            currentBlock.Statements.AddRange(subBlock.Statements);
                        } else {
                            currentBlock.Statements.Add(translated);
                        }
                    }
                }
            }

            return result;
        }

        protected JSStatement TranslateStatement (ILNode node) {
            var translated = TranslateNode(node as dynamic);

            var statement = translated as JSStatement;
            if (statement == null)
            {
                var expression = (JSExpression) translated;

                if (expression != null)
                    statement = new JSExpressionStatement(expression);
                else
                    Translator.WarningFormat("Null statement: {0}", node);
            }

            return statement;
        }

        private void AddSymbolInfo(ILNode node, JSNode expression)
        {
            if (Symbols != null)
            {
                var ilExpression = node as ILExpression;
                if (ilExpression != null && ilExpression.ILRanges.Any())
                {
                    var rangeFrom =
                        ilExpression.ILRanges.Min(item => item.From);
                    var symbolInfo = Symbols.Instructions
                                .OrderByDescending(item => item.Offset)
                                .Where(item => !(item.SequencePoint.StartLine == item.SequencePoint.EndLine && item.SequencePoint.StartColumn == item.SequencePoint.EndColumn))
                                .Where(item => item.Offset <= rangeFrom)
                                .Select(item => item.SequencePoint)
                                .FirstOrDefault();

                    if (symbolInfo != null)
                    {
                        expression.SymbolInfo = new SymbolInfo(new List<SequencePoint> { symbolInfo}, false);
                    }
                }
            }
        }

        public JSBlockStatement TranslateNode (ILBlock block) {
            return TranslateBlock(block.Body);
        }

        private JSVariableDeclarationStatement TranslateFixedInitializer (ILExpression initializer) {
            var toTranslate = initializer;

            // Detect weird pattern for fixed statements generated by Mono and compensate so that the
            //  ILAst nodes look like what the MS compiler generates.
            if (
                (toTranslate.Code == ILCode.Stloc) && 
                (toTranslate.Arguments[0].Code == ILCode.TernaryOp) &&
                (toTranslate.Arguments[0].Arguments[0].Code == ILCode.LogicAnd) &&
                (toTranslate.Arguments[0].Arguments[1].Code == ILCode.Ldelema)
            ) {
                var ldelema = toTranslate.Arguments[0].Arguments[1];
                ldelema.ExpectedType = initializer.ExpectedType ?? initializer.InferredType;

                toTranslate = new ILExpression(
                    ILCode.Stloc, initializer.Operand, ldelema
                ) {
                    ExpectedType = initializer.ExpectedType,
                    InferredType = initializer.InferredType
                };
            }

            var translated = TranslateNode(toTranslate);
            translated = JSReferenceExpression.Strip(translated);

            var boe = translated as JSBinaryOperatorExpression;

            if (boe == null)
                throw new NotImplementedException("Unhandled fixed initializer: " + initializer);

            return new JSVariableDeclarationStatement(boe);
        }

        public JSBlockStatement TranslateNode (ILFixedStatement fxd) {
            var block = TranslateNode(fxd.BodyBlock);

            for (var i = 0; i < fxd.Initializers.Count; i++) {
                var initializer = fxd.Initializers[i];

                var pinStatement = TranslateFixedInitializer(initializer);

                block.Statements.Insert(i, pinStatement);
            }

            return block;
        }

        static System.Reflection.MethodInfo[] GetNodeTranslators (ILCode code) {
            return NodeTranslatorCache.GetOrCreate(
                code, GetNodeTranslatorsUncached
            );
        }

        static object InvokeNodeTranslator (ILCode code, object thisReference, object[] arguments) {
            MethodBase boundMethod = null;
            var methods = GetNodeTranslators(code);

            if (methods != null) {
                if (methods.Length > 1) {
                    var bindingFlags = System.Reflection.BindingFlags.Instance |
                                System.Reflection.BindingFlags.InvokeMethod |
                                System.Reflection.BindingFlags.NonPublic;

                    var binder = Type.DefaultBinder;
                    object state;

                    try {
                        boundMethod = binder.BindToMethod(
                            bindingFlags, methods, ref arguments,
                            null, null, null, out state
                        );
                    } catch (Exception exc) {
                        throw new Exception(String.Format(
                            "Failed to bind to translator method for ILCode.{0}. Had {1} options:{2}{3}",
                            code, methods.Length,
                            Environment.NewLine,
                            String.Join(Environment.NewLine, (from m in methods select m.ToString()).ToArray())
                        ), exc);
                    }
                } else {
                    boundMethod = methods[0];
                }
            }

            if (boundMethod == null) {
                throw new MissingMethodException(
                    String.Format("Could not find a node translator for the node type '{0}'.", code)
                );
            }

            return boundMethod.Invoke(thisReference, arguments);
        }

        public JSExpression TranslateNode (ILExpression expression) {
            JSExpression finalResult = null;
            JSExpression result = null;

            if ((expression.InferredType != null) && TypeUtil.IsIgnoredType(expression.InferredType))
            {
                finalResult = new JSUntranslatableExpression(expression);
                goto END;
            }
            if ((expression.ExpectedType != null) && TypeUtil.IsIgnoredType(expression.ExpectedType))
            {
                finalResult = new JSUntranslatableExpression(expression);
                goto END;
            }
            try {
                object[] arguments;
                if (expression.Operand != null)
                    arguments = new object[] { expression, expression.Operand };
                else
                    arguments = new object[] { expression };

                var invokeResult = InvokeNodeTranslator(expression.Code, this, arguments);
                finalResult = result = invokeResult as JSExpression;

                if (result == null)
                    WarningFormatFunction("Instruction {0} did not produce a JS AST expression", expression);
            } catch (MissingMethodException) {
                string operandType = "";
                if (expression.Operand != null)
                    operandType = expression.Operand.GetType().FullName;

                WarningFormatFunction("Instruction NYI: {0} {1}", expression.Code, operandType);
                finalResult = new JSUntranslatableExpression(expression);
                goto END;
            } catch (TargetInvocationException tie) {
                if (tie.InnerException is AbortTranslation)
                    throw tie.InnerException;

                Translator.WarningFormat("Error occurred while translating node {0}: {1}", expression, tie.InnerException);
                throw;
            } catch (Exception exc) {
                Translator.WarningFormat("Error occurred while translating node {0}: {1}", expression, exc);
                throw;
            }

            if (
                (result != null) &&
                (expression.ExpectedType != null) &&
                (expression.InferredType != null) &&
                !TypeUtil.TypesAreAssignable(TypeInfo, expression.ExpectedType, expression.InferredType)
            ) {
                var expectedType = expression.ExpectedType;
                
                // HACK: Expected types inside of comparison expressions are wrong, so we need to suppress
                //  the casts they would normally generate sometimes.
                bool shouldAutoCast = AutoCastingState.Peek();

                if (
                    TypeUtil.IsIntPtr(TypeUtil.DereferenceType(expectedType)) ||
                    TypeUtil.IsIntPtr(TypeUtil.DereferenceType(expression.InferredType))
                ) {
                    // Never do autocasts to/from System.IntPtr since ILSpy's broken type inference generates it
                    shouldAutoCast = false;
                } else if (
                    !TypeUtil.IsReferenceType(expectedType) || 
                    !TypeUtil.IsReferenceType(expression.InferredType)
                ) {
                    // Comparisons between value types still need a cast.
                    shouldAutoCast = true;
                }

                // HACK: ILSpy improperly decompiles sequences like:
                // byte * px = ...;
                // *((A*)px) = new A()
                // into a cast of the form ((A&)px) = new A()
                if (
                    (expectedType is ByReferenceType) &&
                    (expression.InferredType is PointerType) &&
                    !TypeUtil.TypesAreEqual(expectedType.GetElementType(), expression.InferredType.GetElementType())
                ) {
                    expectedType = new PointerType(expectedType.GetElementType());
                }

                bool specialNullableCast = (
                    TypeUtil.IsNullable(expectedType) ||
                    TypeUtil.IsNullable(expression.InferredType) ||
                    (expression.Code == ILCode.ValueOf)
                );

                if (shouldAutoCast) {
                    if (specialNullableCast)
                    {
                        finalResult = new JSNullableCastExpression(result, new JSType(expectedType));
                        goto END;
                    }
                    else
                    {
                        finalResult= JSCastExpression.New(result, expectedType, TypeSystem, isCoercion: true);
                        goto END;
                    }
                } else {
                    // FIXME: Should this be JSChangeTypeExpression to preserve type information?
                    // I think not, because a lot of these ExpectedTypes are wrong.
                    goto END;
                }
            } else if (
                // HACK: Can't apply this to InitArray instructions because it breaks dynamic call sites.
                (expression.Code != ILCode.InitArray) &&

                (expression.ExpectedType != null) &&
                (expression.InferredType != null) &&
                (TypeUtil.IsArray(expression.InferredType)) &&
                expression.ExpectedType.FullName.StartsWith("System.Collections.") &&
                expression.ExpectedType.FullName.Contains(".IEnumerable")
            ) {
                // HACK: Workaround for the fact that JS array instances don't expose IEnumerable methods
                // This can go away if we introduce a CLRArray type and use that instead of JS arrays.

                finalResult = JSCastExpression.New(result, expression.ExpectedType, TypeSystem, isCoercion: false);
                goto END;
            }

            END:
            AddSymbolInfo(expression, finalResult);
            return finalResult;
        }

        public JSStatement TranslateNode (ILCondition condition) {
            JSStatement result = null;

            JSStatement falseBlock = null;
            if ((condition.FalseBlock != null) && (condition.FalseBlock.Body.Count > 0))
                falseBlock = TranslateNode(condition.FalseBlock);

            result = new JSIfStatement(
                TranslateNode(condition.Condition),
                TranslateNode(condition.TrueBlock),
                falseBlock
            );

            return result;
        }

        public JSSwitchCase TranslateSwitchCase (
            ILSwitch.CaseBlock block, TypeReference conditionType, 
            string exitLabelName, ref bool needExitLabel, out JSBlockStatement epilogue
        ) {
            JSExpression[] values = null;
            epilogue = null;

            if (block.Values != null) {
                if (conditionType.MetadataType == MetadataType.Char) {
                    values = (from v in block.Values select JSLiteral.New(Convert.ToChar(v))).ToArray();
                } else {
                    values = (from v in block.Values select JSLiteral.New(v)).ToArray();
                }
            }

            var temporaryBlock = new ILBlock(block.Body);
            temporaryBlock.EntryGoto = block.EntryGoto;

            var jsBlock = TranslateNode(temporaryBlock);

            var firstBlockChild = jsBlock.Children.OfType<JSStatement>().FirstOrDefault();

            // If a switch case contains labels, they might be the target of a goto.
            // In order to support this we need to move the entire switch case outside of the switch statement,
            //  and then replace the switch case itself with a goto that points to the new location of the case.
            if ((firstBlockChild != null) && (firstBlockChild.Label != null)) {
                var standinBlock = new JSBlockStatement(
                    new JSExpressionStatement(new JSGotoExpression(firstBlockChild.Label))
                );

                var switchBreaks = (from be in jsBlock.AllChildrenRecursive.OfType<JSBreakExpression>() where be.TargetLoop == null select be);

                foreach (var sb in switchBreaks)
                    jsBlock.ReplaceChildRecursive(sb, new JSNullExpression());

                needExitLabel = true;
                jsBlock.Statements.Add(new JSExpressionStatement(new JSGotoExpression(exitLabelName)));

                epilogue = jsBlock;
                return new JSSwitchCase(values, standinBlock, block.IsDefault);
            } else {
                return new JSSwitchCase(
                    values, jsBlock, block.IsDefault
                );
            }
        }

        public JSBlockStatement TranslateNode (ILSwitch swtch) {
            var condition = TranslateNode(swtch.Condition);
            var conditionType = condition.GetActualType(TypeSystem);
            var resultSwitch = new JSSwitchStatement(condition);

            JSBlockStatement epilogue;
            var result = new JSBlockStatement(resultSwitch);

            Blocks.Push(resultSwitch);

            bool needExitLabel = false;
            string exitLabelName = String.Format("$switchExit{0}", NextSwitchId++);

            foreach (var cb in swtch.CaseBlocks) {
                var previousNeedExitLabel = needExitLabel;

                resultSwitch.Cases.Add(TranslateSwitchCase(
                    cb, conditionType, exitLabelName, ref needExitLabel, out epilogue
                ));

                if (epilogue != null) {
                    if (needExitLabel != previousNeedExitLabel)
                        result.Statements.Add(new JSExpressionStatement(new JSGotoExpression(exitLabelName)));

                    result.Statements.Add(epilogue);
                }
            }

            Blocks.Pop();

            if (needExitLabel) {
                var exitLabel = new JSNoOpStatement();
                exitLabel.Label = exitLabelName;
                result.Statements.Add(exitLabel);
            }

            return result;
        }

        public JSTryCatchBlock TranslateNode (ILTryCatchBlock tcb) {
            var body = TranslateNode(tcb.TryBlock);
            JSVariable catchVariable = null;
            JSBlockStatement catchBlock = null;
            JSBlockStatement finallyBlock = null;

            if (tcb.CatchBlocks.Count > 0) {
                var pairs = new List<KeyValuePair<JSExpression, JSStatement>>();
                catchVariable = DeclareVariableInternal(new JSExceptionVariable(TypeSystem, ThisMethodReference));

                bool foundUniversalCatch = false;
                foreach (var cb in tcb.CatchBlocks) {
                    JSExpression pairCondition = null;

                    if (
                        (cb.ExceptionType.FullName == "System.Exception") ||
                        (cb.ExceptionType.FullName == "System.Object")
                    ) {
                        // Bad IL sometimes contains entirely meaningless catch clauses. It's best to just ignore them.
                        if (
                            (cb.Body.Count == 1) && (cb.Body[0] is ILExpression) &&
                            (((ILExpression)cb.Body[0]).Code == ILCode.Rethrow)
                        ) {
                            continue;
                        }

                        if (foundUniversalCatch) {
                            WarningFormatFunction("Found multiple catch-all catch clauses. Any after the first will be ignored.");
                            continue;
                        }

                        foundUniversalCatch = true;
                    } else {
                        if (foundUniversalCatch)
                            throw new NotImplementedException("Catch-all clause must be last");

                        pairCondition = new JSIsExpression(catchVariable, cb.ExceptionType);
                    }

                    var pairBody = TranslateBlock(cb.Body);

                    if (cb.ExceptionVariable != null) {
                        var excVariable = DeclareVariable(cb.ExceptionVariable, ThisMethodReference);

                        pairBody.Statements.Insert(
                            0, new JSVariableDeclarationStatement(new JSBinaryOperatorExpression(
                                JSOperator.Assignment, excVariable,
                                catchVariable, cb.ExceptionVariable.Type
                            ))
                        );
                    }

                    pairs.Add(new KeyValuePair<JSExpression, JSStatement>(
                        pairCondition, pairBody
                    ));
                }

                if (!foundUniversalCatch)
                    pairs.Add(new KeyValuePair<JSExpression,JSStatement>(
                        null, new JSExpressionStatement(new JSThrowExpression(catchVariable))
                    ));

                if ((pairs.Count == 1) && (pairs[0].Key == null))
                    catchBlock = new JSBlockStatement(
                        pairs[0].Value
                    );
                else
                    catchBlock = new JSBlockStatement(
                        JSIfStatement.New(pairs.ToArray())
                    );
            }

            if (tcb.FinallyBlock != null)
                finallyBlock = TranslateNode(tcb.FinallyBlock);

            if (tcb.FaultBlock != null) {
                if (catchBlock != null)
                    throw new Exception("A try block cannot have both a catch block and a fault block");

                catchVariable = DeclareVariableInternal(new JSExceptionVariable(TypeSystem, ThisMethodReference));
                catchBlock = new JSBlockStatement(TranslateBlock(tcb.FaultBlock.Body));

                catchBlock.Statements.Add(new JSExpressionStatement(new JSThrowExpression(catchVariable)));
            }

            return new JSTryCatchBlock(
                body, catchVariable, catchBlock, finallyBlock
            );
        }

        public JSWhileLoop TranslateNode (ILWhileLoop loop) {
            JSExpression condition;
            if (loop.Condition != null)
                condition = TranslateNode(loop.Condition);
            else
                condition = JSLiteral.New(true);

            var result = new JSWhileLoop(condition);
            result.Index = UnlabelledBlockCount++;
            Blocks.Push(result);

            var body = TranslateNode(loop.BodyBlock);

            Blocks.Pop();
            result.Statements.Add(body);
            return result;
        }


        //
        // MSIL Instructions
        //

        protected JSExpression Translate_Sizeof (ILExpression node, TypeReference type) {
            return new JSSizeOfExpression(new JSTypeOfExpression(type));
        }

        protected bool UnwrapValueOfExpression (ref JSExpression expression) {
            while (true) {
                var valueOf = expression as JSValueOfNullableExpression;
                if (valueOf != null) {
                    // Don't iterate.
                    expression = valueOf.Expression;
                    return true;
                }

                var cast = expression as JSCastExpression;
                if (cast != null) {
                    expression = cast.Expression;
                    continue;
                }

                var ncast = expression as JSNullableCastExpression;
                if (ncast != null) {
                    // HACK: We want to preserve the semantics here, but act as if we unwrapped successfully.
                    return true;
                }

                break;
            }

            return false;
        }

        // Represents an arithmetic or logic expression on nullable operands.
        // Inside are one or more ValueOf expressions.
        protected JSExpression Translate_NullableOf (ILExpression node) {
            var inner = TranslateNode(node.Arguments[0]);
            var innerType = inner.GetActualType(TypeSystem);

            var nullableType = new TypeReference("System", "Nullable`1", TypeSystem.Object.Module, TypeSystem.Object.Scope);
            var nullableGenericType = new GenericInstanceType(nullableType);
            nullableGenericType.GenericArguments.Add(innerType);

            var innerBoe = inner as JSBinaryOperatorExpression;

            if (innerBoe == null)
                return new JSUntranslatableExpression(node);

            var left = innerBoe.Left;
            var right = innerBoe.Right;
            JSExpression conditional = null;

            Func<JSExpression, JSBinaryOperatorExpression> makeNullCheck =
                (expr) => new JSBinaryOperatorExpression(
                    JSOperator.Equal, expr, new JSNullLiteral(nullableGenericType), TypeSystem.Boolean
                );

            var unwrappedLeft = UnwrapValueOfExpression(ref left);
            var unwrappedRight = UnwrapValueOfExpression(ref right);

            if (unwrappedLeft && unwrappedRight)
                conditional = new JSBinaryOperatorExpression(
                    JSOperator.LogicalOr, makeNullCheck(left), makeNullCheck(right), TypeSystem.Boolean
                );
            else if (unwrappedLeft)
                conditional = makeNullCheck(left);
            else if (unwrappedRight)
                conditional = makeNullCheck(right);
            else
                return new JSUntranslatableExpression(node);

            var arithmeticExpression = new JSBinaryOperatorExpression(
                innerBoe.Operator, left, right, nullableGenericType
            );
            var result = new JSTernaryOperatorExpression(
                conditional, new JSNullLiteral(innerBoe.ActualType), arithmeticExpression, nullableGenericType
            );

            return result;
        }

        // Acts as a barrier to prevent this expression from being combined with its parent(s).
        protected JSExpression Translate_Wrap (ILExpression node) {
            var inner = TranslateNode(node.Arguments[0]);

            var innerBoe = inner as JSBinaryOperatorExpression;

            if ((innerBoe != null) && (innerBoe.Operator is JSComparisonOperator)) {
                var left = innerBoe.Left;
                var right = innerBoe.Right;

                var unwrappedLeft = UnwrapValueOfExpression(ref left);
                var unwrappedRight = UnwrapValueOfExpression(ref right);

                if (!(unwrappedLeft || unwrappedRight))
                    return new JSUntranslatableExpression(node);

                return new JSBinaryOperatorExpression(
                    innerBoe.Operator, left, right, TypeSystem.Boolean
                );
            } else {
                return inner;
            }
        }

        // Represents a nullable operand in an arithmetic/logic expression that is wrapped by a NullableOf expression.
        protected JSExpression Translate_ValueOf (ILExpression node) {
            var inner = TranslateNode(node.Arguments[0]);
            var innerType = TypeUtil.DereferenceType(inner.GetActualType(TypeSystem));

            var innerTypeGit = innerType as GenericInstanceType;
            if (innerTypeGit != null) {
                if (inner is JSValueOfNullableExpression)
                    return inner;

                return new JSValueOfNullableExpression(inner);
            } else
                return new JSUntranslatableExpression(node);
        }

        protected JSExpression Translate_ComparisonOperator (ILExpression node, JSBinaryOperator op) {
            if (
                (node.Arguments[0].ExpectedType.FullName == "System.Boolean") &&
                (node.Arguments[1].ExpectedType.FullName == "System.Boolean") &&
                (node.Arguments[1].Code.ToString().Contains("Ldc_"))
            ) {
                // Comparison against boolean constant
                bool comparand = Convert.ToInt64(node.Arguments[1].Operand) != 0;
                bool checkEquality = (op == JSOperator.Equal);

                if (comparand != checkEquality)
                    return new JSUnaryOperatorExpression(
                        JSOperator.LogicalNot, TranslateNode(node.Arguments[0]), TypeSystem.Boolean
                    );
                else
                    return TranslateNode(node.Arguments[0]);
            } else if (
                (!node.Arguments[0].ExpectedType.IsValueType) &&
                (!node.Arguments[1].ExpectedType.IsValueType) &&
                (node.Arguments[0].ExpectedType == node.Arguments[1].ExpectedType) &&
                (node.Arguments[0].Code == ILCode.Isinst)
            ) {
                // The C# expression 'x is y' translates into roughly '(x is y) > null' in IL, 
                //  because there's no IL opcode for != and the IL isinst opcode returns object, not bool
                var value = TranslateNode(node.Arguments[0].Arguments[0]);
                var arg1 = TranslateNode(node.Arguments[1]);
                var nullLiteral = arg1 as JSNullLiteral;
                var targetType = (TypeReference)node.Arguments[0].Operand;

                var targetInfo = TypeInfo.Get(targetType);
                JSExpression checkTypeResult;

                if ((targetInfo != null) && targetInfo.IsIgnored)
                    checkTypeResult = JSLiteral.New(false);
                else
                    checkTypeResult = new JSIsExpression(
                        value, targetType
                    );

                if (nullLiteral != null) {
                    if (
                        (op == JSOperator.Equal) ||
                        (op == JSOperator.LessThanOrEqual) ||
                        (op == JSOperator.LessThan)
                    ) {
                        return new JSUnaryOperatorExpression(
                            JSOperator.LogicalNot, checkTypeResult, TypeSystem.Boolean
                        );
                    } else if (
                        (op == JSOperator.GreaterThan)
                    ) {
                        return checkTypeResult;
                    } else {
                        return new JSUntranslatableExpression(node);
                    }
                }
            }
            else if (
                (node.Arguments[0].ExpectedType == node.Arguments[1].ExpectedType) &&
                (node.Arguments[0].Code == ILCode.Ldnull || node.Arguments[1].Code == ILCode.Ldnull) &&
                (node.Arguments[0].Code != node.Arguments[1].Code)
                )
            {
                // x != null may be compiled as (x > null) or (null < x) in IL, 
                // all this expressions are equivalent, but objects in JS should be checked for not null only with !=.
                if ((op == JSOperator.GreaterThan && node.Arguments[1].Code == ILCode.Ldnull)
                    || (op == JSOperator.LessThan && (node.Arguments[0].Code == ILCode.Ldnull)))
                {
                    op = JSOperator.NotEqual;
                }
                // x == null may be compiled as (null >= x) or (x <= null) in IL, 
                // all this expressions are equivalent, but objects in JS should be checked for null only with ==.
                else if ((op == JSOperator.GreaterThanOrEqual && node.Arguments[0].Code == ILCode.Ldnull)
                         || (op == JSOperator.LessThanOrEqual && (node.Arguments[1].Code == ILCode.Ldnull)))
                {
                    op = JSOperator.Equal;
                }
            }

            return Translate_BinaryOp(node, op);
        }

        protected JSExpression Translate_Clt (ILExpression node) {
            return Translate_ComparisonOperator(node, JSOperator.LessThan);
        }

        protected JSExpression Translate_Cgt (ILExpression node) {
            return Translate_ComparisonOperator(node, JSOperator.GreaterThan);
        }

        protected JSExpression Translate_Ceq (ILExpression node) {
            return Translate_ComparisonOperator(node, JSOperator.Equal);
        }

        protected JSExpression Translate_Cne (ILExpression node) {
            return Translate_ComparisonOperator(node, JSOperator.NotEqual);
        }

        protected JSExpression Translate_Cle (ILExpression node) {
            return Translate_ComparisonOperator(node, JSOperator.LessThanOrEqual);
        }

        protected JSExpression Translate_Cge (ILExpression node) {
            return Translate_ComparisonOperator(node, JSOperator.GreaterThanOrEqual);
        }

        protected JSExpression Translate_CompoundAssignment (ILExpression node) {
            JSAssignmentOperator op = null;
            var translated = TranslateNode(node.Arguments[0]);
            if (translated is JSResultReferenceExpression)
                translated = ((JSResultReferenceExpression)translated).Referent;

            var overflowCheck = translated as JSOverflowCheckExpression;
            var boe = translated as JSBinaryOperatorExpression;
            var invocation = translated as JSInvocationExpression;

            if (overflowCheck != null) {
                boe = overflowCheck.Expression as JSBinaryOperatorExpression;
                invocation = overflowCheck.Expression as JSInvocationExpression;
            }

            JSBinaryOperatorExpression result = null;

            // HACK: Handle an incredibly weird pattern generated by ILSpy for compound assignments to nullables (issue #154)
            if (node.Arguments[0].Code == ILCode.NullableOf) {
                var ternary = translated as JSTernaryOperatorExpression;
                if (ternary != null) {
                    var trueNull = ternary.True as JSNullLiteral;
                    var newBoe = ternary.False as JSBinaryOperatorExpression;

                    if ((trueNull != null) && (newBoe != null)) {
                        var operandLhs = newBoe.Left;
                        operandLhs = JSReferenceExpression.Strip(operandLhs);

                        result = new JSBinaryOperatorExpression(
                            JSOperator.Assignment, 
                            DecomposeMutationOperators.MakeLhsForAssignment(operandLhs),
                            translated, translated.GetActualType(TypeSystem)
                        );
                        return result;
                    }
                }
            }

            switch (node.Arguments[0].Code) {
                case ILCode.Add:
                case ILCode.Add_Ovf:
                case ILCode.Add_Ovf_Un:
                    op = JSOperator.AddAssignment;
                    break;
                case ILCode.Sub:
                case ILCode.Sub_Ovf:
                case ILCode.Sub_Ovf_Un:
                    op = JSOperator.SubtractAssignment;
                    break;
                case ILCode.Mul:
                case ILCode.Mul_Ovf:
                case ILCode.Mul_Ovf_Un:
                    op = JSOperator.MultiplyAssignment;
                    break;
                case ILCode.Div:
                case ILCode.Div_Un:
                    op = JSOperator.DivideAssignment;
                    break;
                case ILCode.Rem:
                    op = JSOperator.RemainderAssignment;
                    break;
                case ILCode.Shl:
                    op = JSOperator.ShiftLeftAssignment;
                    break;
                case ILCode.Shr_Un:
                    op = JSOperator.ShiftRightUnsignedAssignment;
                    break;
                case ILCode.Shr:
                    op = JSOperator.ShiftRightAssignment;
                    break;
                case ILCode.And:
                    op = JSOperator.BitwiseAndAssignment;
                    break;
                case ILCode.Or:
                    op = JSOperator.BitwiseOrAssignment;
                    break;
                case ILCode.Xor:
                    op = JSOperator.BitwiseXorAssignment;
                    break;
                default:
                    if (boe != null) {
                        throw new NotImplementedException(node.Arguments[0].Code.ToString());
                    }
                    break;
            }

            if (boe != null) {
                var leftInvocation = boe.Left as JSInvocationExpression;
                JSExpression leftThisReference = null;
                ArrayType leftThisType = null;

                if (leftInvocation != null) {
                    leftThisReference = leftInvocation.ThisReference;
                    leftThisType = leftThisReference.GetActualType(TypeSystem) as ArrayType;
                }

                if (
                    (leftThisType != null) &&
                    (leftThisType.Rank > 1) &&
                    (leftInvocation.JSMethod != null) &&
                    (leftInvocation.JSMethod.Method.Name == "Get")
                ) {
                    var indices = leftInvocation.Arguments.ToArray();
                    return Translate_CompoundAssignment_MultidimensionalArray(node, boe, leftThisReference, indices);
                } else if (op == null) {
                    // Unimplemented compound operators, and operators with semantics that don't match JS, must be emitted normally
                    result = new JSBinaryOperatorExpression(
                        JSOperator.Assignment, 
                        DecomposeMutationOperators.MakeLhsForAssignment(boe.Left),
                        boe, boe.ActualType
                    );
                } else {
                    result = new JSBinaryOperatorExpression(
                        op, 
                        DecomposeMutationOperators.MakeLhsForAssignment(boe.Left), boe.Right, 
                        boe.ActualType
                    );
                }
            } else if ((invocation != null) && (invocation.Arguments[0] is JSReferenceExpression)) {
                // Some compound expressions involving structs produce a call instruction instead of a binary expression
                result = new JSBinaryOperatorExpression(
                    JSOperator.Assignment, invocation.Arguments[0],
                    invocation, invocation.GetActualType(TypeSystem)
                );
            } else if ((invocation != null) && invocation.JSMethod.Identifier.StartsWith("op_")) {
                // Binary operator using a custom operator method
                var lhs = DecomposeMutationOperators.MakeLhsForAssignment(invocation.Arguments[0]);

                result = new JSBinaryOperatorExpression(
                    JSOperator.Assignment, lhs,
                    invocation, invocation.GetActualType(TypeSystem)
                );
            } else {
                throw new NotImplementedException(String.Format("Compound assignments of this type not supported: '{0}'", node));
            }

            return result;
        }

        private JSTemporaryVariable MakeTemporaryVariable (TypeReference type) {
            var index = TemporaryVariableTypes.Count;
            TemporaryVariableTypes.Add(type);

            var id = string.Format("$temp{0:X2}", index);

            var result = new JSTemporaryVariable(id, type, ThisMethodReference);
            Variables.Add(id, result);
            return result;
        }

        private JSExpression Translate_CompoundAssignment_MultidimensionalArray (
            ILExpression node, JSBinaryOperatorExpression boe, JSExpression leftThisReference, JSExpression[] indices
        ) {
            // Compound assignments to elements of multidimensional arrays must be handled specially, because
            //  getting/setting elements of those arrays is actually a method call
            var returnType = boe.ActualType;
            var setter = new JSFakeMethod(
                "Set", returnType,
                (from i in indices select i.GetActualType(TypeSystem)).Concat(new[] { returnType }).ToArray(),
                MethodTypes
            );

            bool useCommaExpression = !indices.All(
                (e) => (e is JSVariable) || (e.IsConstant)
            );

            Func<JSExpression[], JSExpression, JSInvocationExpressionBase> makeSetter = (_indices, _value) => JSInvocationExpression.InvokeMethod(
                setter, leftThisReference, _indices.Concat(new[] { _value }).ToArray()
            );

            if (useCommaExpression) {
                // The indices aren't constant so we need to ensure we only evaluate them once.
                // We do this by temporarily caching them in a local and then wrapping that in a comma expression.
                var oldIndices = indices;
                indices = new JSExpression[oldIndices.Length];
                var initIndices = new JSExpression[oldIndices.Length];

                for (var i = 0; i < oldIndices.Length; i++) {
                    var indexType = oldIndices[i].GetActualType(TypeSystem);

                    indices[i] = MakeTemporaryVariable(indexType);
                    initIndices[i] = new JSBinaryOperatorExpression(
                        JSOperator.Assignment, indices[i], oldIndices[i], indexType
                    );

                    boe.ReplaceChildRecursive(oldIndices[i], indices[i]);
                }

                var resultVar = MakeTemporaryVariable(returnType);
                var newValueVar = MakeTemporaryVariable(returnType);
                var initNewValue = new JSBinaryOperatorExpression(
                    JSOperator.Assignment, newValueVar, boe, returnType
                );
                var initResult = new JSBinaryOperatorExpression(
                    JSOperator.Assignment, resultVar, makeSetter(indices, newValueVar), returnType
                );

                var commaExpression = new JSCommaExpression(
                    initIndices.Concat(new JSExpression[] { initNewValue, initResult, resultVar }).ToArray()
                );
                return commaExpression;
            } else {
                var resultVar = MakeTemporaryVariable(returnType);
                var initResult = new JSBinaryOperatorExpression(
                    JSOperator.Assignment, resultVar, makeSetter(indices, boe), returnType
                );
                var commaExpression = new JSCommaExpression(
                    new JSExpression[] { initResult, resultVar }
                );
                return commaExpression;
            }
        }

        protected JSTernaryOperatorExpression Translate_TernaryOp (ILExpression node) {
            var expectedType = node.ExpectedType;
            var inferredType = node.InferredType;

            var left = node.Arguments[1];
            var right = node.Arguments[2];

            // FIXME: ILSpy generates invalid type information for ternary operators.
            //  Detect invalid type information and replace it with less-invalid type information.
            if (
                (!TypeUtil.TypesAreEqual(left.ExpectedType, right.ExpectedType)) ||
                (!TypeUtil.TypesAreEqual(left.InferredType, right.InferredType))
            ) {
                left.ExpectedType = left.InferredType;
                right.ExpectedType = right.InferredType;
                inferredType = expectedType ?? TypeSystem.Object;
            }

            return new JSTernaryOperatorExpression(
                TranslateNode(node.Arguments[0]),
                TranslateNode(left),
                TranslateNode(right),
                inferredType
            );
        }

        private TypeReference DetermineOutputTypeForMultiply (JSExpression left, JSExpression right) {
            var leftType = left.GetActualType(TypeSystem);
            var rightType = right.GetActualType(TypeSystem);
            var leftIsIntegral = TypeUtil.IsIntegral(leftType);
            var rightIsIntegral = TypeUtil.IsIntegral(rightType);

            if (!leftIsIntegral || !rightIsIntegral)
                return null;

            var sizeofLeft = TypeUtil.SizeOfType(leftType);
            var sizeofRight = TypeUtil.SizeOfType(rightType);

            var largerType = (sizeofLeft > sizeofRight)
                ? leftType
                : rightType;

            if (TypeUtil.IsSigned(leftType) != TypeUtil.IsSigned(rightType)) {
                // Promote up to Int64 if signs don't match, just in case.
                return TypeSystem.Int64;
            } else if (TypeUtil.SizeOfType(largerType) < 4) {
                // Muls are always at least int32.
                if (TypeUtil.IsSigned(largerType).Value) {
                    return TypeSystem.Int32;
                } else {
                    return TypeSystem.UInt32;
                }
            } else {
                // Otherwise, return the largest type.
                return largerType;
            }
        }

        protected JSExpression Translate_Mul (ILExpression node) {
            var ilSpySays = node.ExpectedType ?? node.InferredType;
            if (TypeUtil.IsIntegral(ilSpySays)) {
                var left = TranslateNode(node.Arguments[0]);
                var right = TranslateNode(node.Arguments[1]);

                var outputType = DetermineOutputTypeForMultiply(
                    left, right
                );

                // FIXME: This may be too strict.
                // We do this to ensure that certain multiply operations don't erroneously get replaced with imul, like
                //  (int)(float * int)
                // If this is too strict then we will fail to use imul in scenarios where we should have. :(
                if (outputType != null) {
                    switch (outputType.FullName) {
                        case "System.UInt32":
                            return new JSUInt32MultiplyExpression(
                                left, right, TypeSystem
                            );

                        case "System.Int32":
                            return new JSInt32MultiplyExpression(
                                left, right, TypeSystem
                            );
                    }
                }
            }

            return Translate_BinaryOp(node, JSOperator.Multiply);
        }

        protected JSExpression Translate_Mul_Ovf (ILExpression node) {
            return JSOverflowCheckExpression.New(Translate_Mul(node), TypeSystem);
        }

        protected JSExpression Translate_Mul_Ovf_Un (ILExpression node) {
            return JSOverflowCheckExpression.New(Translate_Mul(node), TypeSystem);
        }

        protected JSExpression Translate_Div (ILExpression node) {
            return Translate_BinaryOp(node, JSOperator.Divide);
        }

        protected JSExpression Translate_Rem (ILExpression node) {
            return Translate_BinaryOp(node, JSOperator.Remainder);
        }

        protected JSExpression Translate_Add (ILExpression node) {
            return Translate_BinaryOp(node, JSOperator.Add);
        }

        protected JSExpression Translate_Add_Ovf (ILExpression node) {
            return JSOverflowCheckExpression.New(Translate_BinaryOp(node, JSOperator.Add), TypeSystem);
        }

        protected JSExpression Translate_Add_Ovf_Un (ILExpression node) {
            return JSOverflowCheckExpression.New(Translate_BinaryOp(node, JSOperator.Add), TypeSystem);
        }

        protected JSExpression Translate_Sub (ILExpression node) {
            return Translate_BinaryOp(node, JSOperator.Subtract);
        }

        protected JSExpression Translate_Sub_Ovf (ILExpression node) {
            return JSOverflowCheckExpression.New(Translate_BinaryOp(node, JSOperator.Subtract), TypeSystem);
        }

        protected JSExpression Translate_Sub_Ovf_Un (ILExpression node) {
            return JSOverflowCheckExpression.New(Translate_BinaryOp(node, JSOperator.Subtract), TypeSystem);
        }

        protected JSExpression Translate_Shl (ILExpression node) {
            return Translate_BinaryOp(node, JSOperator.ShiftLeft);
        }

        protected JSExpression Translate_Shr (ILExpression node) {
            return Translate_BinaryOp(node, JSOperator.ShiftRight);
        }

        protected JSExpression Translate_Shr_Un (ILExpression node) {
            return Translate_BinaryOp(node, JSOperator.ShiftRightUnsigned);
        }

        protected JSExpression Translate_And (ILExpression node) {
            return Translate_BinaryOp(node, JSOperator.BitwiseAnd);
        }

        protected JSExpression Translate_Or (ILExpression node) {
            return Translate_BinaryOp(node, JSOperator.BitwiseOr);
        }

        protected JSExpression Translate_Xor (ILExpression node) {
            return Translate_BinaryOp(node, JSOperator.BitwiseXor);
        }

        protected JSExpression Translate_Not (ILExpression node) {
            return Translate_UnaryOp(node, JSOperator.BitwiseNot);
        }

        protected JSExpression Translate_LogicOr (ILExpression node) {
            return Translate_BinaryOp(node, JSOperator.LogicalOr);
        }

        protected JSExpression Translate_LogicAnd (ILExpression node) {
            return Translate_BinaryOp(node, JSOperator.LogicalAnd);
        }

        protected JSExpression Translate_LogicNot (ILExpression node) {
            return Translate_UnaryOp(node, JSOperator.LogicalNot);
        }

        protected JSExpression Translate_Neg (ILExpression node) {
            return Translate_UnaryOp(node, JSOperator.Negation);
        }

        protected JSThrowExpression Translate_Rethrow (ILExpression node) {
            return new JSThrowExpression(new JSStringIdentifier(
                "$exception", new TypeReference("System", "Exception", TypeSystem.Object.Module, TypeSystem.Object.Scope), true
            ));
        }

        protected JSThrowExpression Translate_Throw (ILExpression node) {
            return new JSThrowExpression(TranslateNode(node.Arguments[0]));
        }

        protected JSExpression Translate_Endfinally (ILExpression node) {
            return JSExpression.Null;
        }

        protected JSBreakExpression Translate_LoopOrSwitchBreak (ILExpression node) {
            var result = new JSBreakExpression();

            if (Blocks.Count > 0) {
                var theLoop = Blocks.Peek() as JSLoopStatement;
                if (theLoop != null)
                    result.TargetLoop = theLoop.Index.Value;
            }

            return result;
        }

        protected JSContinueExpression Translate_LoopContinue (ILExpression node) {
            var result = new JSContinueExpression();

            if (Blocks.Count > 0) {
                var theLoop = Blocks.Peek() as JSLoopStatement;
                if (theLoop != null)
                    result.TargetLoop = theLoop.Index.Value;
            }

            return result;
        }

        protected JSReturnExpression Translate_Ret (ILExpression node) {
            if (node.Arguments.FirstOrDefault() != null) {
                var returnValue = TranslateNode(node.Arguments[0]);

                PackedArrayUtil.CheckReturnValue(TypeInfo.Get(ThisMethodReference) as Internal.MethodInfo, returnValue, TypeSystem);

                return new JSReturnExpression(returnValue);
            } else if (node.Arguments.Count == 0) {
                return new JSReturnExpression();
            } else {
                throw new NotImplementedException("Invalid return expression");
            }
        }

        protected JSVariable MapVariable (ILVariable variable) {
            JSVariable renamed, theVariable;

            var escapedName = JSParameter.MaybeEscapeName(variable.Name, true);
            bool isThis = (variable.OriginalParameter != null) && (variable.OriginalParameter.Index < 0);

            if (
                !isThis &&
                Variables.TryGetValue(escapedName, out theVariable)
            ) {
                // Handle cases where the variable's identifier must be escaped (like @this)
                return MakeIndirectVariable(theVariable.Name);
            } else {
                return MakeIndirectVariable(variable.Name);
            }
        }

        protected JSExpression Translate_Ldloc (ILExpression node, ILVariable variable) {
            JSExpression result = MapVariable(variable);

            var valueType = result.GetActualType(TypeSystem);
            var valueTypeInfo = TypeInfo.Get(valueType);
            if ((valueTypeInfo != null) && valueTypeInfo.IsIgnored)
                return new JSIgnoredTypeReference(true, valueType);

            if (
                TypeUtil.IsPointer(node.ExpectedType) &&
                TypeUtil.IsArray(valueType)
            ) {
                // HACK: ILSpy produces 'ldloc arr' expressions with an expected pointer type.
                //  Do the necessary magic here.
                return new JSPinExpression(result, JSIntegerLiteral.New(0), node.ExpectedType);
            }

            return result;
        }

        protected JSExpression Translate_Ldloca (ILExpression node, ILVariable variable) {
            return JSReferenceExpression.New(
                Translate_Ldloc(node, variable)
            );
        }

        protected JSExpression Translate_Stloc (ILExpression node, ILVariable variable) {
            // GetCallSite and CreateCallSite produce null expressions, so we want to ignore assignments containing them
            // TODO: We have nor more GetCallSite and CreateCallSite. Do we need this check?
            var value = TranslateNode(node.Arguments[0]);
            if ((value.IsNull) && !(value is JSUntranslatableExpression) && !(value is JSIgnoredExpression))
                return new JSNullExpression();

            var valueType = value.GetActualType(TypeSystem);

            JSVariable jsv = MapVariable(variable);

            if (jsv.IsReference) {
                if (
                    (valueType.IsByReference || valueType.IsPointer) &&
                    TypeUtil.TypesAreAssignable(TypeInfo, variable.Type, valueType)
                ) {

                } else {
                    JSExpression materializedValue;
                    if (!JSReferenceExpression.TryMaterialize(JSIL, value, out materializedValue))
                        WarningFormatFunction("Cannot store a non-reference into variable {0}: {1}", jsv, value);
                    else
                        value = materializedValue;
                }
            }

            // Assignment from a packed array field into a non-packed array variable needs to change
            //  the type of the variable so that it is also treated as a packed array.
            if (
                PackedArrayUtil.IsPackedArrayType(valueType) &&
                !PackedArrayUtil.IsPackedArrayType(variable.Type)
            ) {
                jsv = ChangeVariableType(jsv, valueType);
            }

            var valueTypeInfo = TypeInfo.Get(valueType);
            if ((valueTypeInfo != null) && valueTypeInfo.IsIgnored)
                return new JSIgnoredTypeReference(true, valueType);

            // FIXME: We're not getting the appropriate casts elsewhere in the pipeline for some reason...
            if (TypeUtil.IsPointer(variable.Type)) {
                if (!TypeUtil.IsPointer(valueType)) {
                    // The ldloc/ldloca with a pointer expected type should have pinned this...
                    //  or is it a native int, maybe?

                    // This will throw if it fails
                    value = AttemptPointerConversion(value, variable.Type);
                } else if (!TypeUtil.TypesAreEqual(variable.Type, valueType)) {
                    throw new InvalidOperationException("Expected matching pointer type on rhs");
                }
            }

            return new JSBinaryOperatorExpression(
                JSOperator.Assignment, DecomposeMutationOperators.MakeLhsForAssignment(jsv),
                value, valueType
            );
        }

        private JSVariable ChangeVariableType (JSVariable variable, TypeReference newType) {
            var existingVariable = Variables[variable.Identifier];
            JSVariable newVariable;

            if (existingVariable.IsParameter) {
                newVariable = new JSParameter(existingVariable.Name, newType, existingVariable.Function, false);
            } else {
                newVariable = new JSVariable(existingVariable.Name, newType, existingVariable.Function, existingVariable.DefaultValue);
            }

            Variables[variable.Identifier] = newVariable;

            return variable;
        }

        protected JSExpression Translate_Ldsfld (ILExpression node, FieldReference field) {
            var result = Translate_FieldAbstract(node, field, false);

            if (CopyOnReturn(field.FieldType))
                result = JSReferenceExpression.New(result);

            return result;
        }

        protected JSExpression Translate_Ldsflda (ILExpression node, FieldReference field) {
            var result = Translate_FieldAbstract(node, field, false);

            return new JSMemberReferenceExpression(result);
        }

        protected JSExpression Translate_Stsfld (ILExpression node, FieldReference field) {
            var lhs = DecomposeMutationOperators.MakeLhsForAssignment(Translate_FieldAbstract(node, field, true));
            var rhs = TranslateNode(node.Arguments[0]);

            if ((lhs is JSUntranslatableExpression) || (rhs is JSUntranslatableExpression))
                return new JSUntranslatableExpression(node);

            return new JSBinaryOperatorExpression(
                JSOperator.Assignment,
                lhs, rhs, rhs.GetActualType(TypeSystem)
            );
        }

        protected JSExpression Translate_FieldAbstract (ILExpression expr, FieldReference field, bool isWrite) {
            JSExpression thisExpression = null;
            ILExpression firstArgExpr;
            if (
                (expr.Code == ILCode.Ldsfld) ||
                (expr.Code == ILCode.Ldsflda) ||
                (expr.Code == ILCode.Stsfld)
            ) {
                var mr = (MemberReference)expr.Operand;
                thisExpression = new JSType(mr.DeclaringType);
            } else {
                firstArgExpr = expr.Arguments[0];
                var firstArg = TranslateNode(firstArgExpr);

                if (IsInvalidThisExpression(firstArgExpr)) {
                    if (!JSReferenceExpression.TryDereference(JSIL, firstArg, out thisExpression)) {
                        if (!firstArg.IsNull)
                            WarningFormatFunction("Accessing {0} without a reference as this.", field.FullName);

                        thisExpression = firstArg;
                    }
                } else {
                    thisExpression = firstArg;
                }
            }

            // GetCallSite and CreateCallSite produce null expressions, so we want to ignore field references containing them
            if (
                (thisExpression.IsNull) && 
                !(thisExpression is JSUntranslatableExpression) && 
                !(thisExpression is JSIgnoredExpression)
            )
                return new JSNullExpression();

            var fieldInfo = GetField(field);
            if (TypeUtil.IsIgnoredType(field.FieldType) || (fieldInfo == null) || fieldInfo.IsIgnored)
                return new JSIgnoredMemberReference(true, fieldInfo, thisExpression);

            JSExpression result = new JSFieldAccess(
                thisExpression,
                new JSField(field, fieldInfo),
                isWrite
            );

            return result;
        }

        protected JSExpression Translate_Ldfld (ILExpression node, FieldReference field) {
            var result = Translate_FieldAbstract(node, field, false);

            if (CopyOnReturn(field.FieldType))
                result = JSReferenceExpression.New(result);

            return result;
        }

        protected JSBinaryOperatorExpression Translate_Stfld (ILExpression node, FieldReference field) {
            var rhs = TranslateNode(node.Arguments[1]);

            return new JSBinaryOperatorExpression(
                JSOperator.Assignment,
                DecomposeMutationOperators.MakeLhsForAssignment(Translate_FieldAbstract(node, field, true)),
                rhs, rhs.GetActualType(TypeSystem)
            );
        }

        protected JSExpression Translate_Ldflda (ILExpression node, FieldReference field) {
            return new JSMemberReferenceExpression(Translate_FieldAbstract(node, field, false));
        }

        protected JSExpression Translate_Ldobj (ILExpression node, TypeReference type) {
            var reference = TranslateNode(node.Arguments[0]);
            JSExpression referent;

            if (reference == null)
                throw new InvalidDataException(String.Format(
                    "Failed to translate the target of a ldobj expression: {0}",
                    node.Arguments[0]
                ));

            var referenceType = reference.GetActualType(TypeSystem);
            if (referenceType.IsPointer) {
                return new JSReadThroughPointerExpression(reference, TypeUtil.GetElementType(referenceType, true));
            } else {
                if (!JSReferenceExpression.TryDereference(JSIL, reference, out referent))
                    WarningFormatFunction("unsupported reference type for ldobj: {0}", node.Arguments[0]);

                if ((referent != null) && TypeUtil.IsStruct(referent.GetActualType(TypeSystem)))
                    return reference;
                else if (referent != null)
                    return referent;
            }

            return new JSUntranslatableExpression(node);
        }

        protected JSExpression Translate_Ldind (ILExpression node) {
            return Translate_Ldobj(node, null);
        }

        protected JSExpression AttemptPointerConversion (JSExpression value, TypeReference targetType) {
            var childLiteral = value.SelfAndChildrenRecursive.OfType<JSLiteral>().FirstOrDefault();
            if (childLiteral != null) {
                // We special-case allowing 0 to be converted to a pointer.
                if ((childLiteral.Literal is Int64) && ((Int64)childLiteral.Literal == 0))
                    return new JSDefaultValueLiteral(targetType);
                else if ((childLiteral.Literal is Int32) && ((Int32)childLiteral.Literal == 0))
                    return new JSDefaultValueLiteral(targetType);
                else if ((childLiteral.Literal is Boolean) && ((Boolean)childLiteral.Literal == false))
                    return new JSDefaultValueLiteral(targetType);

                if (value is JSUntranslatableExpression)
                    return value;
            }

            return new JSUntranslatableExpression("Implicit conversion to pointer: " + value);
        }

        protected JSExpression Translate_Stobj (ILExpression node, TypeReference type) {
            var target = TranslateNode(node.Arguments[0]);
            var targetChangeType = target as JSChangeTypeExpression;
            var targetVariable = target as JSVariable;
            var value = TranslateNode(node.Arguments[1]);
            var valueType = value.GetActualType(TypeSystem);

            // Handle an assignment where the left hand side is a pointer or reference cast
            var targetType = target.GetActualType(TypeSystem);
            if (targetChangeType != null) {
                targetVariable = targetChangeType.Expression as JSVariable;
                targetType = targetVariable.GetActualType(TypeSystem);
            }

            if (targetType.IsPointer && !valueType.IsPointer)
                return new JSWriteThroughPointerExpression(target, value, valueType);

            if (targetVariable != null) {
                if (!targetVariable.IsReference)
                    WarningFormatFunction("unsupported target variable for stobj: {0}", node.Arguments[0]);

                if (!valueType.IsByReference) {
                    var neededValueType = ((ByReferenceType)targetType).ElementType;

                    // HACK: If you have a ref T* and you write into it, the necessary automatic casts won't happen
                    //  because ILSpy's type inference never figures out the appropriate types.
                    if (TypeUtil.IsPointer(neededValueType) && !TypeUtil.IsPointer(valueType)) {
                        value = AttemptPointerConversion(value, neededValueType);
                    }

                    return new JSWriteThroughReferenceExpression(targetVariable, value);
                }
            } else {
                JSExpression referent;
                if (!JSReferenceExpression.TryMaterialize(JSIL, target, out referent))
                    WarningFormatFunction("unsupported target expression for stobj: {0}", node.Arguments[0]);
                else {
                    return JSInvocationExpression.InvokeMethod(
                        new JSFakeMethod("set", valueType, new TypeReference[] { valueType }, MethodTypes),
                        referent, new JSExpression[] { value }, false
                    );
                }
            }

            return new JSBinaryOperatorExpression(
                JSOperator.Assignment, 
                DecomposeMutationOperators.MakeLhsForAssignment(target), value, 
                node.InferredType ?? node.ExpectedType ?? value.GetActualType(TypeSystem)
            );
        }

        protected JSExpression Translate_Stind (ILExpression node) {
            return Translate_Stobj(node, null);
        }

        protected JSExpression Translate_AddressOf (ILExpression node) {
            var referent = TranslateNode(node.Arguments[0]);

            var referentInvocation = referent as JSInvocationExpression;
            if (referentInvocation != null)
                return new JSResultReferenceExpression(referentInvocation);

            return JSReferenceExpression.New(referent);
        }

        protected JSExpression Translate_Arglist (ILExpression node) {
            return new JSUntranslatableExpression("Arglist");
        }

        protected JSExpression Translate_Localloc (ILExpression node) {
            var sizeInBytes = TranslateNode(node.Arguments[0]);

            return JSIL.StackAlloc(sizeInBytes, node.ExpectedType);
        }

        protected JSStringLiteral Translate_Ldstr (ILExpression node, string text) {
            return JSLiteral.New(text);
        }

        protected JSExpression Translate_Ldnull (ILExpression node) {
            return JSLiteral.Null(node.InferredType ?? node.ExpectedType);
        }

        protected JSExpression Translate_Ldftn (ILExpression node, MethodReference method) {
            var methodInfo = GetMethod(method);
            if (methodInfo == null)
                return new JSIgnoredMemberReference(true, null, new JSStringLiteral(method.FullName));

            return new JSMethodAccess(
                new JSType(method.DeclaringType),
                new JSMethod(method, methodInfo, MethodTypes),
                !method.HasThis,
                false
            );
        }

        protected JSExpression Translate_Ldvirtftn (ILExpression node, MethodReference method) {
            var methodInfo = GetMethod(method);
            if (methodInfo == null)
                return new JSIgnoredMemberReference(true, null, new JSStringLiteral(method.FullName));

            return new JSMethodAccess(
                new JSType(method.DeclaringType),
                new JSMethod(method, methodInfo, MethodTypes),
                false,
                true
            );
        }

        protected JSExpression Translate_Ldc_I4 (ILExpression node, int value) {
            return Translate_LoadIntegerConstant(node, value);
        }

        protected JSExpression Translate_Ldc_I8 (ILExpression node, long value) {
            return Translate_LoadIntegerConstant(node, value);
        }

        protected JSExpression Translate_Ldc_R4 (ILExpression node, float value) {
            return JSLiteral.New(value);
        }

        protected JSExpression Translate_Ldc_R8 (ILExpression node, double value) {
            return JSLiteral.New(value);
        }

        protected JSExpression Translate_Ldc_Decimal (ILExpression node, decimal value) {
            return JSLiteral.New(value);
        }

        protected JSExpression Translate_IntegerConstantCore (ILCode ilCode, long value, bool isUnsigned) {
            switch (ilCode) {
                case ILCode.Ldc_I4:
                    if (isUnsigned)
                        return new JSIntegerLiteral((uint)((int)value), typeof(uint));
                    else
                        return new JSIntegerLiteral(value, typeof(int));

                case ILCode.Ldc_I8:
                    if (isUnsigned)
                        return new JSIntegerLiteral(value, typeof(ulong));
                    else
                        return new JSIntegerLiteral(value, typeof(long));
            }

            return null;
        }

        protected JSExpression Translate_LoadIntegerConstant (ILExpression node, long value) {
            string typeName = null;
            var expressionType = node.InferredType ?? node.ExpectedType;
            TypeInfo typeInfo = null;
            if (expressionType != null) {
                typeName = expressionType.FullName;
                typeInfo = TypeInfo.Get(expressionType);
            }

            bool isUnsigned = false;
            if ((typeName != null) && typeName.StartsWith("System.UInt"))
                isUnsigned = true;

            if (
                (typeInfo != null) && 
                (typeInfo.EnumMembers != null) && (typeInfo.EnumMembers.Count > 0)
            ) {
                JSEnumLiteral enumLiteral = JSEnumLiteral.TryCreate(typeInfo, value);

                if (enumLiteral != null)
                    return enumLiteral;
                else {
                    var result = Translate_IntegerConstantCore(node.Code, value, isUnsigned);

                    if (result == null)
                        throw new NotImplementedException(String.Format(
                            "This form of enum constant loading is not implemented: {0}",
                            node
                        ));

                    var resultCast = JSCastExpression.New(result, expressionType, TypeSystem, true, true);
                    return resultCast;
                }
            } else if (typeName == "System.Boolean") {
                return JSLiteral.New(value != 0);
            } else if (typeName == "System.Char") {
                return JSLiteral.New((char)value);
            } else {
                var result = Translate_IntegerConstantCore(node.Code, value, isUnsigned);

                if (result == null)
                    throw new NotImplementedException(String.Format(
                        "This form of constant loading is not implemented: {0}",
                        node
                    ));

                return result;
            }
        }

        protected JSExpression Translate_Ldlen (ILExpression node) {
            var arg = TranslateNode(node.Arguments[0]);
            if (arg.IsNull)
                return arg;

            var argType = arg.GetActualType(TypeSystem);
            var argTypeDef = TypeUtil.GetTypeDefinition(argType);
            PropertyDefinition lengthProp = null;
            if (argTypeDef != null)
                lengthProp = (from p in argTypeDef.Properties where p.Name == "Length" select p).FirstOrDefault();

            if (lengthProp == null)
                return new JSUntranslatableExpression(String.Format("Retrieving the length of a type with no length property: {0}", argType.FullName));
            else {
                var getMethod = lengthProp.GetMethod;

                return Translate_CallGetter(node, CecilUtil.RebindMethod(getMethod, argType));
            }
        }

        protected JSExpression Translate_Ldelem (ILExpression node, TypeReference elementType) {
            return Translate_Ldelem(node, elementType, false);
        }

        private JSExpression Translate_Ldelem (ILExpression node, TypeReference elementType, bool getReference) {
            var expectedType = node.InferredType ?? node.ExpectedType;
            if (!getReference)
                expectedType = elementType ?? expectedType;

            var target = TranslateNode(node.Arguments[0]);
            if (target.IsNull || target is JSIgnoredMemberReference || target is JSIgnoredTypeReference)
                return target;

            var targetType = target.GetActualType(TypeSystem);
            var index = TranslateNode(node.Arguments[1]);

            JSExpression result;

            if (getReference) {
                if (TypeUtil.IsPointer(node.ExpectedType)) {
                    result = new JSPinExpression(target, index, node.ExpectedType);
                } else {
                    result = JSIL.NewElementReference(target, index);
                }
            } else if (PackedArrayUtil.IsPackedArrayType(targetType)) {
                result = PackedArrayUtil.GetItem(targetType, TypeInfo.Get(targetType), target, index, MethodTypes);
            } else {
                result = new JSIndexerExpression(
                    target, index,
                    expectedType
                );
            }

            if (CopyOnReturn(expectedType) && !getReference)
                result = JSReferenceExpression.New(result);

            return result;
        }

        protected JSExpression Translate_Ldelem (ILExpression node) {
            return Translate_Ldelem(node, null, false);
        }

        protected JSExpression Translate_Ldelema (ILExpression node, TypeReference elementType) {
            return Translate_Ldelem(node, elementType, true);
        }

        protected JSExpression Translate_Stelem (ILExpression node) {
            return Translate_Stelem(node, null);
        }

        protected JSExpression Translate_Stelem (ILExpression node, TypeReference elementType) {
            var expectedType = elementType ?? node.InferredType ?? node.ExpectedType;

            var target = TranslateNode(node.Arguments[0]);
            if (target.IsNull)
                return target;

            var targetType = target.GetActualType(TypeSystem);
            var index = TranslateNode(node.Arguments[1]);
            var rhs = TranslateNode(node.Arguments[2]);

            if (PackedArrayUtil.IsPackedArrayType(targetType)) {
                var targetGit = (GenericInstanceType)targetType;
                var targetTypeInfo = TypeInfo.Get(targetType);
                var setMethod = (JSIL.Internal.MethodInfo)targetTypeInfo.Members.First(
                    (kvp) => kvp.Key.Name == "set_Item"
                ).Value;

                var setMethodReference = CecilUtil.RebindMethod(
                    setMethod.Member, targetGit
                );

                return JSInvocationExpression.InvokeMethod(
                    new JSType(targetType),
                    new JSMethod(setMethodReference, setMethod, MethodTypes),
                    target,
                    new JSExpression[] {
                        index, rhs
                    }
                );
            } else {
                return new JSBinaryOperatorExpression(
                    JSOperator.Assignment,
                    new JSIndexerExpression(
                        target, index,
                        expectedType
                    ),
                    rhs, elementType ?? rhs.GetActualType(TypeSystem)
                );
            }
        }

        protected JSExpression Translate_NullCoalescing (ILExpression node) {
            return JSIL.Coalesce(
                TranslateNode(node.Arguments[0]),
                TranslateNode(node.Arguments[1]),
                node.InferredType ?? node.ExpectedType
            );
        }

        protected JSExpression Translate_Castclass (ILExpression node, TypeReference targetType) {
            if (TypeUtil.IsDelegateType(targetType) && TypeUtil.IsDelegateType(node.InferredType ?? node.ExpectedType)) {
                // TODO: We treat all delegate types as equivalent, so we can skip these casts for now
                return TranslateNode(node.Arguments[0]);
            }

            return JSCastExpression.New(
                TranslateNode(node.Arguments[0]),
                targetType,
                TypeSystem
            );
        }

        protected JSExpression Translate_Isinst (ILExpression node, TypeReference targetType) {
            var firstArg = TranslateNode(node.Arguments[0]);

            var targetInfo = TypeInfo.Get(targetType);
            if ((targetInfo != null) && targetInfo.IsIgnored)
                return new JSNullLiteral(targetType);

            var expectedType = node.ExpectedType ?? node.InferredType ?? targetType;

            if (targetType.IsValueType) {
                if ((expectedType.Name == "Object") && (expectedType.Namespace == "System")) {
                    return new JSTernaryOperatorExpression(
                        new JSIsExpression(firstArg, targetType),
                        firstArg, new JSNullLiteral(targetType),
                        targetType
                    );
                } else {
                    return new JSIsExpression(firstArg, targetType);
                }
            } else {
                return JSAsExpression.New(firstArg, targetType, TypeSystem);
            }
        }

        protected JSExpression Translate_Unbox(ILExpression node, TypeReference targetType)
        {
            var value = TranslateNode(node.Arguments[0]);

            var result = JSCastExpression.New(value, targetType, TypeSystem);

            return new JSNewBoxedVariable(result, targetType, true);
        }

        protected JSExpression Translate_Unbox_Any (ILExpression node, TypeReference targetType) {
            var value = TranslateNode(node.Arguments[0]);

            var result = JSCastExpression.New(value, targetType, TypeSystem);

            if (CopyOnReturn(targetType))
                return JSReferenceExpression.New(result);
            else
                return result;
        }

        protected JSExpression Translate_Conv (JSExpression value, TypeReference expectedType) {
            var currentType = value.GetActualType(TypeSystem);

            if (TypeUtil.TypesAreEqual(expectedType, currentType))
                return value;

            int currentDepth, expectedDepth;
            var currentDerefed = TypeUtil.FullyDereferenceType(currentType, out currentDepth);
            var expectedDerefed = TypeUtil.FullyDereferenceType(expectedType, out expectedDepth);

            // Handle assigning a value of type 'T&&' to a variable of type 'T&', etc.
            // 'AreTypesAssignable' will return false, because the types are not equivalent, but no cast is necessary.
            if (TypeUtil.TypesAreEqual(expectedDerefed, currentDerefed)) {
                if (currentDepth > expectedDepth) {
                    // If the current expression has more levels of reference than the target type, we must dereference
                    //  the current expression one or more times to strip off the reference levels.
                    var result = value;
                    JSExpression dereferenced;

                    while (currentDepth > expectedDepth) {
                        bool ok = JSReferenceExpression.TryDereference(JSIL, result, out dereferenced);
                        if (!ok)
                            break;

                        currentDepth -= 1;
                        result = dereferenced;
                    }

                    return result;
                } else {
                    return value;
                }
            }

            if (TypeUtil.IsDelegateType(expectedType) && TypeUtil.IsDelegateType(currentType))
                return value;

            if (TypeUtil.IsNumericOrEnum(currentType) && TypeUtil.IsNumericOrEnum(expectedType)) {
                return JSCastExpression.New(value, expectedType, TypeSystem);
            } else if (!TypeUtil.TypesAreAssignable(TypeInfo, expectedType, currentType)) {
                if (expectedType.FullName == "System.Boolean") {
                    if (TypeUtil.IsIntegral(currentType)) {
                        // i != 0 sometimes becomes (bool)i, so we want to generate the explicit form
                        return new JSBinaryOperatorExpression(
                            JSOperator.NotEqual, value, JSLiteral.New(0), TypeSystem.Boolean
                        );
                    } else if (!currentType.IsValueType) {
                        // We need to detect any attempts to cast object references to boolean and not generate a cast
                        //  for them, so that our logic in Translate_UnaryOp for detecting (x != null) will still work
                        return value;
                    }
                }

                // Never cast AnyType to another type since the implication is that the proxy author will ensure the correct
                //  type is returned.
                if (currentType.FullName == "JSIL.Proxy.AnyType")
                    return value;

                return JSCastExpression.New(value, expectedType, TypeSystem);
            } else
                return value;
        }

        protected JSExpression Translate_Conv (ILExpression node, TypeReference targetType, bool throwOnOverflow = false, bool fromUnsignedInputs = false) {
            var value = TranslateNode(node.Arguments[0]);
            var valueType = value.GetActualType(TypeSystem);

            if (
                !TypeUtil.TypesAreAssignable(TypeInfo, targetType, valueType) ||
                // HACK: Compensate for char not being assignable to int
                (
                    (targetType.FullName == "System.Char") ||
                    (valueType.FullName == "System.Char")
                )
            )
                value = Translate_Conv(value, targetType);

            if (throwOnOverflow)
                value = JSOverflowCheckExpression.New(value, TypeSystem);

            return value;
        }

        protected JSExpression Translate_Conv_I (ILExpression node) {
            return Translate_Conv(node, Context.CurrentModule.TypeSystem.NativeInt());
        }
        
        protected JSExpression Translate_Conv_I1 (ILExpression node) {
            return Translate_Conv(node, Context.CurrentModule.TypeSystem.SByte);
        }

        protected JSExpression Translate_Conv_I2 (ILExpression node) {
            return Translate_Conv(node, Context.CurrentModule.TypeSystem.Int16);
        }

        protected JSExpression Translate_Conv_I4 (ILExpression node) {
            return Translate_Conv(node, Context.CurrentModule.TypeSystem.Int32);
        }

        protected JSExpression Translate_Conv_I8 (ILExpression node) {
            return Translate_Conv(node, Context.CurrentModule.TypeSystem.Int64);
        }

        protected JSExpression Translate_Conv_Ovf_I (ILExpression node) {
            return Translate_Conv(node, Context.CurrentModule.TypeSystem.NativeInt(), true);
        }

        protected JSExpression Translate_Conv_Ovf_I_Un (ILExpression node) {
            return Translate_Conv(node, Context.CurrentModule.TypeSystem.NativeInt(), true, true);
        }

        protected JSExpression Translate_Conv_Ovf_I1 (ILExpression node) {
            return Translate_Conv(node, Context.CurrentModule.TypeSystem.SByte, true);
        }

        protected JSExpression Translate_Conv_Ovf_I1_Un (ILExpression node) {
            return Translate_Conv(node, Context.CurrentModule.TypeSystem.SByte, true, true);
        }

        protected JSExpression Translate_Conv_Ovf_I2 (ILExpression node) {
            return Translate_Conv(node, Context.CurrentModule.TypeSystem.Int16, true);
        }

        protected JSExpression Translate_Conv_Ovf_I2_Un (ILExpression node) {
            return Translate_Conv(node, Context.CurrentModule.TypeSystem.Int16, true, true);
        }

        protected JSExpression Translate_Conv_Ovf_I4 (ILExpression node) {
            return Translate_Conv(node, Context.CurrentModule.TypeSystem.Int32, true);
        }

        protected JSExpression Translate_Conv_Ovf_I4_Un (ILExpression node) {
            return Translate_Conv(node, Context.CurrentModule.TypeSystem.Int32, true, true);
        }

        protected JSExpression Translate_Conv_Ovf_I8 (ILExpression node) {
            return Translate_Conv(node, Context.CurrentModule.TypeSystem.Int64, true);
        }

        protected JSExpression Translate_Conv_Ovf_I8_Un (ILExpression node) {
            return Translate_Conv(node, Context.CurrentModule.TypeSystem.Int64, true, true);
        }

        protected JSExpression Translate_Conv_Ovf_U (ILExpression node) {
            return Translate_Conv(node, Context.CurrentModule.TypeSystem.NativeUInt(), true);
        }

        protected JSExpression Translate_Conv_Ovf_U_Un (ILExpression node) {
            return Translate_Conv(node, Context.CurrentModule.TypeSystem.NativeUInt(), true, true);
        }

        protected JSExpression Translate_Conv_Ovf_U1 (ILExpression node) {
            return Translate_Conv(node, Context.CurrentModule.TypeSystem.Byte, true);
        }

        protected JSExpression Translate_Conv_Ovf_U1_Un (ILExpression node) {
            return Translate_Conv(node, Context.CurrentModule.TypeSystem.Byte, true, true);
        }

        protected JSExpression Translate_Conv_Ovf_U2 (ILExpression node) {
            return Translate_Conv(node, Context.CurrentModule.TypeSystem.UInt16, true);
        }

        protected JSExpression Translate_Conv_Ovf_U2_Un (ILExpression node) {
            return Translate_Conv(node, Context.CurrentModule.TypeSystem.UInt16, true, true);
        }

        protected JSExpression Translate_Conv_Ovf_U4 (ILExpression node) {
            return Translate_Conv(node, Context.CurrentModule.TypeSystem.UInt32, true);
        }

        protected JSExpression Translate_Conv_Ovf_U4_Un (ILExpression node) {
            return Translate_Conv(node, Context.CurrentModule.TypeSystem.UInt32, true, true);
        }

        protected JSExpression Translate_Conv_Ovf_U8 (ILExpression node) {
            return Translate_Conv(node, Context.CurrentModule.TypeSystem.UInt64, true);
        }

        protected JSExpression Translate_Conv_Ovf_U8_Un (ILExpression node) {
            return Translate_Conv(node, Context.CurrentModule.TypeSystem.UInt64, true, true);
        }

        protected JSExpression Translate_Conv_R_Un (ILExpression node) {
            return Translate_Conv(node, Context.CurrentModule.TypeSystem.Double, false, true);
        }

        protected JSExpression Translate_Conv_R4 (ILExpression node) {
            return Translate_Conv(node, Context.CurrentModule.TypeSystem.Single);
        }

        protected JSExpression Translate_Conv_R8 (ILExpression node) {
            return Translate_Conv(node, Context.CurrentModule.TypeSystem.Double);
        }

        protected JSExpression Translate_Conv_U (ILExpression node) {
            return Translate_Conv(node, Context.CurrentModule.TypeSystem.NativeUInt());
        }

        protected JSExpression Translate_Conv_U1 (ILExpression node) {
            return Translate_Conv(node, Context.CurrentModule.TypeSystem.Byte);
        }

        protected JSExpression Translate_Conv_U2 (ILExpression node) {
            if ((node.ExpectedType != null) && (node.ExpectedType.MetadataType == MetadataType.Char))
                return Translate_Conv(node, Context.CurrentModule.TypeSystem.Char);
            else
                return Translate_Conv(node, Context.CurrentModule.TypeSystem.UInt16);
        }

        protected JSExpression Translate_Conv_U4 (ILExpression node) {
            return Translate_Conv(node, Context.CurrentModule.TypeSystem.UInt32);
        }

        protected JSExpression Translate_Conv_U8 (ILExpression node) {
            return Translate_Conv(node, Context.CurrentModule.TypeSystem.UInt64);
        }

        protected JSExpression Translate_Box (ILExpression node, TypeReference valueType) {
            var value = TranslateNode(node.Arguments[0]);
            var originalType = value.GetActualType(TypeSystem);
            var refenrence = JSReferenceExpression.New(value);
            // Hack, but I don't know how get System.Decimal reference.
            return _rawTypes.Contains(originalType) 
                ? new JSWrapExpression(refenrence, new JSType(originalType))
                : refenrence;
        }

        protected JSExpression Translate_Br (ILExpression node, ILLabel targetLabel) {
            return new JSGotoExpression(targetLabel.Name);
        }

        protected JSExpression Translate_Leave (ILExpression node, ILLabel targetLabel) {
            return new JSGotoExpression(targetLabel.Name);
        }

        protected JSExpression Translate_Newobj_Delegate (ILExpression node, MethodReference constructor, JSExpression[] arguments) {
            var thisArg = arguments[0];
            var methodRef = arguments[1];

            var methodDot = methodRef as JSDotExpressionBase;
            JSMethod methodMember = null;

            // Detect compiler-generated lambda methods
            if (methodDot != null) {
                methodMember = methodDot.Member as JSMethod;

                var ma = methodDot as JSMethodAccess;

                if (ma != null) {
                    if (ma.IsStatic) {
                        if (methodMember == null)
                            throw new InvalidDataException("Static invocation without a method");
                    } 
                }
            }

            return JSIL.NewDelegate(
                constructor.DeclaringType,
                thisArg, methodRef
            );
        }

        protected JSExpression Translate_Newobj (ILExpression node, MethodReference constructor) {
            var arguments = Translate(node.Arguments, constructor.Parameters, false);

            if (TypeUtil.IsDelegateType(constructor.DeclaringType)) {
                return Translate_Newobj_Delegate(node, constructor, arguments.ToArray());
            } else if (constructor.DeclaringType.IsArray)
            {
                var arrayType = (ArrayType) constructor.DeclaringType;
                if (!arrayType.IsVector)
                {
                    JSExpression[] dimensions;
                    if (arguments.Count == arrayType.Rank)
                    {
                        dimensions = new JSExpression[arrayType.Rank*2];
                        for (int i = 0; i < arrayType.Rank; i++)
                        {
                            dimensions[2*i] = new JSIntegerLiteral(0, typeof (int));
                            dimensions[2*i + 1] = arguments[i];
                        }
                    }
                    else
                    {
                        dimensions = arguments.ToArray();
                    }
                    return JSIL.NewMultidimensionalArray(TypeUtil.GetElementType(constructor.DeclaringType, true),
                        dimensions);
                }
                else
                {
                    return JSIL.NewArray(TypeUtil.GetElementType(constructor.DeclaringType, true), arguments[0]);
                }
            } else if (TypeUtil.IsNullable(constructor.DeclaringType)) {
                if (arguments.Count == 0)
                    return new JSNullLiteral(constructor.DeclaringType);
                else
                    return arguments[0];
            }

            var methodInfo = GetMethod(constructor);
            if ((methodInfo == null) || methodInfo.IsIgnored)
                return new JSIgnoredMemberReference(true, methodInfo, arguments.ToArray());

            var result = new JSNewExpression(
                constructor.DeclaringType, constructor, methodInfo, arguments.ToArray()
            );

            return Translate_ConstructorReplacement(constructor, methodInfo, result);
        }

        protected JSExpression Translate_DefaultValue (ILExpression node, TypeReference type) {
            return JSLiteral.DefaultValue(type);
        }

        protected JSNewArrayExpression Translate_Newarr (ILExpression node, TypeReference elementType) {
            return JSIL.NewArray(
                elementType,
                TranslateNode(node.Arguments[0])
            );
        }

        protected JSExpression Translate_InitArray (ILExpression node, TypeReference _arrayType) {
            int temp;
            var at = (ArrayType)TypeUtil.FullyDereferenceType(_arrayType, out temp);
            var initializer = new JSArrayExpression(at, Translate(node.Arguments).ToArray());

            int rank = at.Rank;

            // Really it is not true, but should work for most cases.
            bool isVector = rank == 1 && at.Dimensions[0].LowerBound.GetValueOrDefault(-1) == 0;

            if (TypeUtil.TypesAreEqual(TypeSystem.Object, at) && rank < 2)
                return initializer;
            else {
                if (!isVector) {
                    return JSIL.NewMultidimensionalArray(
                        at.ElementType, TypeUtil.GetArrayDimensions(at), initializer
                    );
                } else {
                    return JSIL.NewArray(
                        at.ElementType, initializer
                    );
                }
            }
        }

        protected JSExpression Translate_TypeOf (TypeReference type) {
            return new JSTypeOfExpression(type);
        }

        protected JSExpression Translate_Ldtoken (ILExpression node, TypeReference type) {
            return Translate_TypeOf(type);
        }

        protected JSExpression Translate_Ldtoken (ILExpression node, MethodReference method) {
            var methodInfo = GetMethod(method);
            return new JSMethodOfExpression(method, methodInfo, MethodTypes);
        }

        protected JSExpression Translate_Ldtoken (ILExpression node, FieldReference field) {
            var fieldInfo = GetField(field);
            return new JSFieldOfExpression(field, fieldInfo);
        }

        public static bool NeedsExplicitThis (
            TypeReference declaringType, TypeDefinition declaringTypeDef, TypeInfo declaringTypeInfo,
            bool isSelf, TypeReference thisReferenceType, JSIL.Internal.MethodInfo methodInfo
        ) {
            /*
             *  Use our type information to determine whether an invocation must be 
             *      performed using an explicit this reference, through an object's 
             *      prototype.
             *  The isSelf parameter is used to identify whether the method performing
             *      this invocation is a member of one of the involved types.
             *  
             *  (void (Base this)) (Base)
             *      Statically resolved call to self method.
             *      If the name is hidden in the type hierarchy, normal invoke is not ok.
             *  
             *  (void (Base this)) (Derived)
             *      Statically resolved call to base method via derived reference.
             *      If isSelf, normal invoke is only ok if the method is never redefined.
             *      (If the method is redefined, we could infinitely call ourselves.)
             *      If the method is virtual, normal invoke is ok.
             *      If the method is never hidden in the type hierarchy, normal is ok.
             *  
             *  (void (Interface this)) (Anything)
             *      Call to an interface method. Normal invoke is always OK!
             *  
             */

            // System.Array's prototype isn't accessible to us in JS, and we don't
            //     want to call through it anyway.
            if (
                (thisReferenceType is ArrayType) ||
                ((thisReferenceType.Name == "Array") && (thisReferenceType.Namespace == "System"))
            )
                return false;

            var sameThisReference = TypeUtil.TypesAreEqual(declaringTypeDef, thisReferenceType, true);

            var isInterfaceMethod = (declaringTypeDef != null) && (declaringTypeDef.IsInterface);

            if (isInterfaceMethod)
                return false;

            // HACK: A virtual method can be marked as sealed, in which case you *do* need to be explicit about calling it
            // FIXME: In #314, Cecil insists that List`1.Add is virtual, but it is clearly not. What the heck?
            if (methodInfo.IsSealed && !methodInfo.IsVirtual)
                return false;

            if (methodInfo.IsVirtual) {
                if (sameThisReference)
                    return false;
                else
                    return true;
            } else {
                if (sameThisReference && !isSelf)
                    return false;

                // If the method was defined in a generic class, overloaded dispatch won't be sufficient
                //  because of generic parameters.
                if (!declaringTypeDef.IsGenericInstance && !declaringTypeDef.HasGenericParameters) {
                    var definitionCount = declaringTypeInfo.MethodSignatures.GetDefinitionCountOf(methodInfo);

                    if (definitionCount < 2)
                        return false;
                }

                return true;
            }
        }

        protected JSExpression Translate_Call (ILExpression node, MethodReference method) {
            var methodInfo = GetMethod(method);
            if (methodInfo == null)
                return new JSIgnoredMemberReference(true, null, JSLiteral.New(method.FullName));

            var declaringType = TypeUtil.DereferenceType(method.DeclaringType);

            var declaringTypeDef = TypeUtil.GetTypeDefinition(declaringType);
            var declaringTypeInfo = TypeInfo.Get(declaringType);

            var arguments = Translate(node.Arguments, method.Parameters, method.HasThis);
            JSExpression thisExpression;

            bool explicitThis = false;
            bool suppressThisClone = false;

            if (method.HasThis) {
                var firstArg =  node.Arguments.First();

                if (IsInvalidThisExpression(firstArg)) {
                    if (!JSReferenceExpression.TryDereference(JSIL, arguments[0], out thisExpression))
                    {
                        if (arguments[0].IsNull)
                            thisExpression = arguments[0];
                        else
                            throw new InvalidOperationException(String.Format(
                                "The method '{0}' was invoked on a value type, but the this-reference was not a reference: {1}",
                                method, node.Arguments[0]
                            ));
                    } else {
                        suppressThisClone = arguments[0] is JSNewBoxedVariable &&
                                            ((JSNewBoxedVariable)arguments[0]).SuppressClone;
                    }
                } else {
                    thisExpression = arguments[0];
                }

                arguments = arguments.Skip(1).ToList();

                var thisReferenceType = thisExpression.GetActualType(TypeSystem);

                var isSelf = TypeUtil.TypesAreAssignable(
                    TypeInfo, thisReferenceType, ThisMethod.DeclaringType
                );

                explicitThis = NeedsExplicitThis(
                    declaringType, declaringTypeDef, declaringTypeInfo,
                    isSelf, thisReferenceType, methodInfo
                );
            } else {
                explicitThis = true;
                thisExpression = new JSNullExpression();
            }

            var result = DoMethodReplacement(
                new JSMethod(method, methodInfo, MethodTypes),
                thisExpression, arguments.ToArray(), false, 
                !method.HasThis, explicitThis || methodInfo.IsConstructor,
                suppressThisClone
            );

            if (CopyOnReturn(result.GetActualType(TypeSystem)))
                result = JSReferenceExpression.New(result);

            return result;
        }

        protected bool IsInvalidThisExpression (ILExpression thisNode) {
            if (thisNode.Code == ILCode.InitializedObject)
                return false;

            if (thisNode.InferredType == null)
                return false;

            var dereferenced = TypeUtil.DereferenceType(thisNode.InferredType);
            if ((dereferenced != null) && dereferenced.IsValueType)
                return true;

            return false;
        }

        protected JSExpression Translate_Callvirt (ILExpression node, MethodReference method) {
            var firstArg = node.Arguments[0];
            var translated = TranslateNode(firstArg);
            JSExpression thisExpression;

            if (IsInvalidThisExpression(firstArg)) {
                if (!JSReferenceExpression.TryDereference(JSIL, translated, out thisExpression)) {
                    if (translated.IsNull)
                        thisExpression = translated;
                    else
                        throw new InvalidOperationException(String.Format(
                            "The method '{0}' was invoked on a value type, but the this-reference was not a reference: {1}",
                            method, node.Arguments[0]
                        ));
                }
            } else {
                thisExpression = translated;
            }

            var translatedArguments = Translate(node.Arguments, method.Parameters, method.HasThis).Skip(1).ToArray();
            var methodInfo = GetMethod(method);

            if (methodInfo == null)
                return new JSIgnoredMemberReference(true, null, JSLiteral.New(method.FullName));

            var explicitThis = methodInfo.IsConstructor;
            if (!methodInfo.IsVirtual) {
                var declaringType = TypeUtil.DereferenceType(method.DeclaringType);

                var declaringTypeDef = TypeUtil.GetTypeDefinition(declaringType);
                var declaringTypeInfo = TypeInfo.Get(declaringType);

                var thisReferenceType = thisExpression.GetActualType(TypeSystem);

                var isSelf = TypeUtil.TypesAreAssignable(
                    TypeInfo, thisReferenceType, ThisMethod.DeclaringType
                );

                explicitThis = NeedsExplicitThis(
                    declaringType, declaringTypeDef, declaringTypeInfo,
                    isSelf, thisReferenceType, methodInfo
                );
            }

            var result = DoMethodReplacement(
               new JSMethod(method, methodInfo, MethodTypes), 
               thisExpression, translatedArguments, true, 
               false, explicitThis, false
            );

            if (CopyOnReturn(result.GetActualType(TypeSystem)))
                result = JSReferenceExpression.New(result);

            return result;
        }

        protected JSExpression Translate_CallGetter (ILExpression node, MethodReference getter) {
            var result = Translate_Call(node, getter);

            return result;
        }

        protected JSExpression Translate_CallSetter (ILExpression node, MethodReference setter) {
            return FilterSetterInvocation(Translate_Call(node, setter));
        }

        protected JSExpression Translate_CallvirtGetter (ILExpression node, MethodReference getter) {
            var result = Translate_Callvirt(node, getter);

            return result;
        }

        protected JSExpression FilterSetterInvocation (JSExpression invocation) {
            var ie = invocation as JSInvocationExpression;

            if (ie != null)
                return new JSPropertySetterInvocation(ie);
            else
                // Probably an assignment or something.
                return invocation;
        }

        protected JSExpression Translate_CallvirtSetter (ILExpression node, MethodReference setter) {
            return FilterSetterInvocation(Translate_Callvirt(node, setter));
        }

        protected JSUnaryOperatorExpression Translate_PostIncrement (ILExpression node, int arg) {
            if (Math.Abs(arg) != 1) {
                throw new NotImplementedException(String.Format(
                    "Unsupported form of post-increment: {0}", node
                ));
            }

            JSExpression target;
            if (!JSReferenceExpression.TryDereference(
                JSIL, TranslateNode(node.Arguments[0]), out target
            ))
                throw new InvalidOperationException("Postfix increment/decrement require a reference to operate on");

            if (arg == 1)
                return new JSUnaryOperatorExpression(
                    JSOperator.PostIncrement, target, target.GetActualType(TypeSystem)
                );
            else
                return new JSUnaryOperatorExpression(
                    JSOperator.PostDecrement, target, target.GetActualType(TypeSystem)
                );
        }

        protected void WarningFormatFunction (string format, params object[] args) {
            Translator.WarningFormatFunction(ThisMethodReference.Name, format, args);
        }
    }

    public class AbortTranslation : Exception {
        public AbortTranslation (string reason)
            : base(reason) {
        }
    }
}
