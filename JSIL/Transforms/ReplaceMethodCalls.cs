﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using ICSharpCode.Decompiler.ILAst;
using JSIL.Ast;
using JSIL.Internal;
using Mono.Cecil;

namespace JSIL.Transforms {
    public class ReplaceMethodCalls : JSAstVisitor {
        public readonly TypeSystem TypeSystem;
        public readonly JSILIdentifier JSIL;
        public readonly JSSpecialIdentifiers JS;
        public readonly MethodReference Method;

        private JSExpression _ResultReferenceReplacement = null;

        public ReplaceMethodCalls (
            MethodReference method, JSILIdentifier jsil, JSSpecialIdentifiers js, TypeSystem typeSystem
        ) {
            Method = method;
            JSIL = jsil;
            JS = js;
            TypeSystem = typeSystem;
        }

        public void VisitNode (JSPublicInterfaceOfExpression poe) {
            VisitChildren(poe);

            // Replace foo.__Type__.__PublicInterface__ with foo
            var innerTypeOf = poe.Inner as ITypeOfExpression;
            if (innerTypeOf != null) {
                var replacement = new JSType(innerTypeOf.Type);

                ParentNode.ReplaceChild(poe, replacement);
                VisitReplacement(replacement);
            }
        }

        public void VisitNode (JSInvocationExpression ie) {
            var type = ie.JSType;
            var method = ie.JSMethod;
            var thisExpression = ie.ThisReference;

            if (method != null) {
                if (
                    (type != null) &&
                    (type.Type.FullName == "System.Object")
                ) {
                    switch (method.Method.Member.Name) {
                        case ".ctor": {
                            var replacement = new JSNullExpression();
                            ParentNode.ReplaceChild(ie, replacement);
                            VisitReplacement(replacement);

                            return;
                        }

                        case "ReferenceEquals": {
                            var lhs = ie.Arguments[0];
                            var rhs = ie.Arguments[1];

                            var lhsType = lhs.GetActualType(TypeSystem);
                            var rhsType = rhs.GetActualType(TypeSystem);

                            JSNode replacement;

                            // Structs can never compare equal with ReferenceEquals
                            if (TypeUtil.IsStruct(lhsType) || TypeUtil.IsStruct(rhsType))
                                replacement = JSLiteral.New(false);
                            else
                                replacement = new JSBinaryOperatorExpression(
                                    JSOperator.Equal,
                                    lhs, rhs,
                                    TypeSystem.Boolean
                                );

                            ParentNode.ReplaceChild(ie, replacement);
                            VisitReplacement(replacement);

                            return;
                        }

                        case "GetType": {
                            JSNode replacement;

                            var thisType = JSExpression.DeReferenceType(thisExpression.GetActualType(TypeSystem), false);
                            if ((thisType is GenericInstanceType) && thisType.FullName.StartsWith("System.Nullable")) {
                                var git = (GenericInstanceType)thisType;

                                replacement = new JSTernaryOperatorExpression(
                                    new JSBinaryOperatorExpression(
                                        JSOperator.NotEqual,
                                        thisExpression, new JSNullLiteral(thisType),
                                        TypeSystem.Boolean
                                    ),
                                    new JSTypeOfExpression(git.GenericArguments[0]),
                                    JSIL.ThrowNullReferenceException(),
                                    TypeSystem.SystemType()
                                );
                            } else {
                                replacement = JSIL.GetTypeOf(thisExpression);
                            }

                            ParentNode.ReplaceChild(ie, replacement);
                            VisitReplacement(replacement);

                            return;
                        }
                    }
                } else if (
                    (type != null) &&
                    (type.Type.FullName == "System.ValueType")
                ) {
                    switch (method.Method.Member.Name) {
                        case "Equals": {
                            var replacement = JSIL.StructEquals(ie.ThisReference, ie.Arguments.First());
                            ParentNode.ReplaceChild(ie, replacement);
                            VisitReplacement(replacement);

                            return;
                        }
                    }
                } else if (
                    (type != null) &&
                    IsNullable(type.Type)
                ) {
                    var t = (type.Type as GenericInstanceType).GenericArguments[0];
                    var @null = JSLiteral.Null(t);
                    var @default = new JSDefaultValueLiteral(t);

                    switch (method.Method.Member.Name) {
                        case ".ctor":
                            JSExpression value;
                            if (ie.Arguments.Count == 0) {
                                value = @null;
                            } else {
                                value = ie.Arguments[0];
                            }

                            JSExpression replacementNode;

                            var readThroughReference = ie.ThisReference as JSReadThroughReferenceExpression;
                            if (readThroughReference != null)
                            {
                                replacementNode = new JSWriteThroughReferenceExpression(readThroughReference.Variable, value);
                            }
                            else
                            {
                                var readThroughPointer = ie.ThisReference as JSReadThroughPointerExpression;
                                if (readThroughPointer != null)
                                {
                                    replacementNode = new JSWriteThroughPointerExpression(readThroughPointer.Pointer, value,
                                        type.Type, readThroughPointer.OffsetInBytes);
                                }
                                else
                                {
                                    replacementNode = new JSBinaryOperatorExpression(JSOperator.Assignment, ie.ThisReference,
                                        value, type.Type);
                                }
                            }

                            ParentNode.ReplaceChild(ie, replacementNode);
                            VisitReplacement(replacementNode);

                            break;

                        case "GetValueOrDefault": {
                            var replacement = JSIL.ValueOfNullableOrDefault(
                                ie.ThisReference,
                                (ie.Arguments.Count == 0)
                                    ? @default
                                    : ie.Arguments[0]
                            );

                            if (ParentNode is JSResultReferenceExpression) {
                                // HACK: Replacing the invocation inside a result reference is incorrect, so we need to walk up the stack
                                //  and replace the result reference with the ternary instead.
                                _ResultReferenceReplacement = replacement;
                            } else {
                                ParentNode.ReplaceChild(ie, replacement);
                                VisitReplacement(replacement);
                            }

                            break;
                        }

                        case "Equals":
                            JSBinaryOperatorExpression equality = new JSBinaryOperatorExpression(JSOperator.Equal, ie.ThisReference, ie.Parameters.First().Value, type.Type);
                            ParentNode.ReplaceChild(ie, equality);
                            VisitReplacement(equality);
                            break;

                        default:
                            throw new NotImplementedException(method.Method.Member.FullName);
                    }

                    return;
                } else if (
                    (type != null) &&
                    TypeUtil.TypesAreEqual(TypeSystem.String, type.Type) &&
                    (method.Method.Name == "Concat")
                ) {
                    if (ie.Arguments.Count > 2) {
                        if (ie.Arguments.All(
                            (arg) => TypeUtil.TypesAreEqual(
                                TypeSystem.String, arg.GetActualType(TypeSystem)
                            )
                        )) {
                            var boe = JSBinaryOperatorExpression.New(
                                JSOperator.Add,
                                ie.Arguments,
                                TypeSystem.String
                            );

                            ParentNode.ReplaceChild(
                                ie,
                                boe
                            );

                            VisitReplacement(boe);
                        }
                    } else if (
                        // HACK: Fix for #239, only convert concat call into + if both sides are non-null literals
                        (ie.Arguments.Count == 2)
                    ) {
                        var lhs = ie.Arguments[0];
                        var rhs = ie.Arguments[1];

                        var isAddOk = (lhs is JSStringLiteral) && (rhs is JSStringLiteral);

                        var lhsType = TypeUtil.DereferenceType(lhs.GetActualType(TypeSystem));
                        if (!(
                            TypeUtil.TypesAreEqual(TypeSystem.String, lhsType) ||
                            TypeUtil.TypesAreEqual(TypeSystem.Char, lhsType)
                        )) {
                            lhs = JSInvocationExpression.InvokeMethod(lhsType, JS.toString, lhs, null);
                            isAddOk = true;
                        }

                        var rhsType = TypeUtil.DereferenceType(rhs.GetActualType(TypeSystem));
                        if (!(
                            TypeUtil.TypesAreEqual(TypeSystem.String, rhsType) ||
                            TypeUtil.TypesAreEqual(TypeSystem.Char, rhsType)
                        )) {
                            rhs = JSInvocationExpression.InvokeMethod(rhsType, JS.toString, rhs, null);
                            isAddOk = true;
                        }

                        if (isAddOk) {
                            var boe = new JSBinaryOperatorExpression(
                                JSOperator.Add, lhs, rhs, TypeSystem.String
                            );

                            ParentNode.ReplaceChild(
                                ie, boe
                            );

                            VisitReplacement(boe);
                        }
                    } else if (
                        TypeUtil.GetTypeDefinition(ie.Arguments[0].GetActualType(TypeSystem)).FullName == "System.Array"
                    ) {
                    } else {
                        var firstArg = ie.Arguments.FirstOrDefault();

                        ParentNode.ReplaceChild(
                            ie, firstArg
                        );

                        if (firstArg != null)
                            VisitReplacement(firstArg);
                    }
                    return;
                } else if (
                    TypeUtil.IsDelegateType(method.Reference.DeclaringType) &&
                    (method.Method.Name == "Invoke")
                ) {
                    var newIe = new JSDelegateInvocationExpression(
                        thisExpression, ie.GetActualType(TypeSystem), ie.Arguments.ToArray()
                    );
                    ParentNode.ReplaceChild(ie, newIe);

                    VisitReplacement(newIe);
                    return;
                } else if (
                    (method.Reference.DeclaringType.FullName == "System.Runtime.CompilerServices.RuntimeHelpers") &&
                    (method.Method.Name == "InitializeArray") &&
                    (method.Method.Parameters.Length == 2) &&
                    (method.Method.Parameters[0].ParameterType.FullName == "System.Array") &&
                    (method.Method.Parameters[1].ParameterType.FullName == "System.RuntimeFieldHandle")
                ) {
                    var array = ie.Arguments[0];
                    var arrayType = array.GetActualType(TypeSystem);
                    var field = ie.Arguments[1].SelfAndChildrenRecursive.OfType<JSField>().First();
                    var initializer = JSArrayExpression.UnpackArrayInitializer(arrayType, field.Field.Member.InitialValue);

                    var copy = JSIL.ShallowCopy(array, initializer, arrayType);
                    ParentNode.ReplaceChild(ie, copy);
                    VisitReplacement(copy);
                    return;
                } else if (
                    method.Reference.DeclaringType.FullName == "System.Reflection.Assembly"
                ) {
                    switch (method.Reference.Name) {
                        case "GetExecutingAssembly": {
                            var assembly = Method.DeclaringType.Module.Assembly;
                            var asmNode = new JSReflectionAssembly(assembly);
                            ParentNode.ReplaceChild(ie, asmNode);
                            VisitReplacement(asmNode);

                            return;
                        }
                    }
                }
            }

            VisitChildren(ie);
        }

        protected bool IsNullable (TypeReference type) {
            var git = TypeUtil.DereferenceType(type) as GenericInstanceType;

            return (git != null) && (git.Name == "Nullable`1");
        }

        public void VisitNode (JSDefaultValueLiteral dvl) {
            var expectedType = dvl.GetActualType(TypeSystem);

            if (
                IsNullable(expectedType)
            ) {
                ParentNode.ReplaceChild(
                    dvl, JSLiteral.Null(expectedType)
                );
            } else {
                VisitChildren(dvl);
            }
        }

        public void VisitNode (JSNewExpression ne) {
            var expectedType = ne.GetActualType(TypeSystem);
            if (
                IsNullable(expectedType)
            ) {
                if (ne.Arguments.Count == 0) {
                    ParentNode.ReplaceChild(
                        ne, JSLiteral.Null(expectedType)
                    );
                } else {
                    ParentNode.ReplaceChild(
                        ne, ne.Arguments[0]
                    );
                    VisitReplacement(ne.Arguments[0]);
                }
            } else {
                VisitChildren(ne);
            }
        }

        public void VisitNode (JSResultReferenceExpression rre) {
            VisitChildren(rre);

            if (_ResultReferenceReplacement != null) {
                var replacement = _ResultReferenceReplacement;
                _ResultReferenceReplacement = null;

                ParentNode.ReplaceChild(rre, replacement);
                VisitReplacement(replacement);
            }
        }
    }
}
