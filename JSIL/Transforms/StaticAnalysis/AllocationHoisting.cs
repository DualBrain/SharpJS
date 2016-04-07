﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using JSIL.Ast;
using JSIL.Internal;
using Mono.Cecil;

namespace JSIL.Transforms {
    public class HoistAllocations : StaticAnalysisJSAstVisitor {
        private struct VariableCacheKey {
            public readonly JSExpression Array;
            public readonly JSExpression Index;

            public VariableCacheKey (JSExpression array, JSExpression index) {
                Array = array;
                Index = index;
            }

            public override int GetHashCode () {
                return Array.GetHashCode() ^ Index.GetHashCode();
            }

            public override bool Equals (object obj) {
                if (obj is VariableCacheKey)
                    return Equals((VariableCacheKey)obj);
                else
                    return false;
            }

            public bool Equals (VariableCacheKey obj) {
                return Object.Equals(Array, obj.Array) &&
                    Object.Equals(Index, obj.Index);
            }
        }

        private struct Identifier {
            public readonly string Text;
            public readonly JSRawOutputIdentifier Object;

            public Identifier (string text, JSRawOutputIdentifier @object) {
                Text = text;
                Object = @object;
            }
        }

        private struct PendingDeclaration {
            public readonly string Name;
            public readonly TypeReference Type;
            public readonly JSExpression Expression;
            public readonly JSExpression DefaultValue;

            public PendingDeclaration (string name, TypeReference type, JSExpression expression, JSExpression defaultValue) {
                Name = name;
                Type = type;
                Expression = expression;
                DefaultValue = defaultValue;
            }
        }

        public readonly TypeSystem TypeSystem;
        public readonly MethodTypeFactory MethodTypes;

        private readonly List<PendingDeclaration> ToDeclare = 
            new List<PendingDeclaration>();

        private readonly Dictionary<VariableCacheKey, JSVariable> CachedHoistedVariables =
            new Dictionary<VariableCacheKey, JSVariable>();

        private FunctionAnalysis1stPass FirstPass = null;
        private int HoistedVariableCount = 0;

        private JSFunctionExpression Function;

        public HoistAllocations (
            QualifiedMemberIdentifier member, 
            IFunctionSource functionSource, 
            TypeSystem typeSystem,
            MethodTypeFactory methodTypes
        ) 
            : base (member, functionSource) {
            TypeSystem = typeSystem;
            MethodTypes = methodTypes;
        }

        public void VisitNode (JSFunctionExpression fn) {
            Function = fn;
            FirstPass = GetFirstPass(Function.Method.QualifiedIdentifier);

            VisitChildren(fn);

            if (ToDeclare.Count > 0) {
                int i = 0;

                var vds = new JSVariableDeclarationStatement();

                foreach (var pd in ToDeclare) {
                    vds.Declarations.Add(
                        new JSBinaryOperatorExpression(
                            JSOperator.Assignment, pd.Expression,
                            pd.DefaultValue ?? new JSDefaultValueLiteral(pd.Type),
                            pd.Type
                        )
                    );
                }

                fn.Body.Statements.Insert(i++, vds);

                InvalidateFirstPass();
            }
        }

        private JSVariable MakeTemporaryVariable (TypeReference type, out string id, JSExpression defaultValue = null) {
            string _id = id = string.Format("$hoisted{0:X2}", HoistedVariableCount++);
            MethodReference methodRef = null;
            if (Function.Method != null)
                methodRef = Function.Method.Reference;

            // So introspection knows about it
            var result = new JSVariable(id, type, methodRef);
            Function.AllVariables.Add(_id, result);
            ToDeclare.Add(new PendingDeclaration(id, type, result, defaultValue));

            return result;
        }

        bool DoesValueEscapeFromInvocation (JSInvocationExpression invocation, JSExpression argumentExpression, bool includeReturn) {
            if (
                (invocation != null) &&
                (invocation.JSMethod != null) &&
                (invocation.JSMethod.Reference != null)
            ) {
                var methodDef = invocation.JSMethod.Reference.Resolve();
                var secondPass = FunctionSource.GetSecondPass(invocation.JSMethod, Function.Method.QualifiedIdentifier);
                if ((secondPass != null) && (methodDef != null)) {
                    // HACK
                    var argumentIndex = invocation.Arguments.Select(
                        (a, i) => new { argument = a, index = i })
                        .FirstOrDefault((_) => _.argument.SelfAndChildrenRecursive.Contains(argumentExpression));

                    if (argumentIndex != null) {
                        var argumentName = methodDef.Parameters[argumentIndex.index].Name;

                        return secondPass.DoesVariableEscape(argumentName, includeReturn);
                    }
                } else if (secondPass != null) {
                    // HACK for methods that do not have resolvable references. In this case, if NONE of their arguments escape, we're probably still okay.
                    if (secondPass.GetNumberOfEscapingVariables(includeReturn) == 0)
                        return false;
                }
            }

            return true;
        }

        public void VisitNode (JSNewArrayElementReference naer) {
            var isInsideLoop = (Stack.Any((node) => node is JSLoopStatement));
            var parentPassByRef = ParentNode as JSPassByReferenceExpression;
            var parentInvocation = Stack.OfType<JSInvocationExpression>().FirstOrDefault();
            var doesValueEscape = DoesValueEscapeFromInvocation(parentInvocation, naer, true);

            if (
                isInsideLoop &&
                (parentPassByRef != null) &&
                (parentInvocation != null) &&
                !doesValueEscape
            ) {
                var replacement = CreateHoistedVariable(
                    (hoistedVariable) => JSInvocationExpression.InvokeMethod(                        
                        new JSFakeMethod("retarget", hoistedVariable.GetActualType(TypeSystem), new TypeReference[] { TypeSystem.Object, TypeSystem.Int32 }, MethodTypes), 
                        hoistedVariable, new JSExpression[] { naer.Array, naer.Index }
                    ), 
                    naer.GetActualType(TypeSystem),
                    naer.MakeUntargeted()
                );

                ParentNode.ReplaceChild(naer, replacement);
                VisitReplacement(replacement);
            }

            VisitChildren(naer);
        }

        public void VisitNode (JSNewPackedArrayElementProxy npaep) {
            var isInsideLoop = (Stack.Any((node) => node is JSLoopStatement));
            var parentInvocation = Stack.OfType<JSInvocationExpression>().FirstOrDefault();
            var doesValueEscape = (parentInvocation != null) && 
                DoesValueEscapeFromInvocation(parentInvocation, npaep, true);

            if (
                isInsideLoop &&
                (
                    (parentInvocation == null) ||
                    !doesValueEscape
                )
            ) {
                var replacement = CreateHoistedVariable(
                    (hoistedVariable) => 
                        new JSRetargetPackedArrayElementProxy(
                            hoistedVariable, npaep.Array, npaep.Index
                        ),
                    npaep.GetActualType(TypeSystem),
                    npaep.Array,
                    npaep.Index,
                    npaep.MakeUntargeted()
                );

                ParentNode.ReplaceChild(npaep, replacement);
                VisitReplacement(replacement);
            }

            VisitChildren(npaep);
        }

        public void VisitNode (JSNewExpression newexp) {
            var type = newexp.GetActualType(TypeSystem);

            var isStruct = TypeUtil.IsStruct(type);
            var isInsideLoop = (Stack.Any((node) => node is JSLoopStatement));
            var parentInvocation = ParentNode as JSInvocationExpression;
            var doesValueEscape = DoesValueEscapeFromInvocation(parentInvocation, newexp, false);

            if (isStruct && 
                isInsideLoop && 
                (parentInvocation != null) &&
                !doesValueEscape
            ) {
                var replacement = CreateHoistedVariable(
                    (hoistedVariable) => new JSCommaExpression(
                        JSInvocationExpression.InvokeMethod(
                            type, new JSMethod(newexp.ConstructorReference, newexp.Constructor, MethodTypes, null), hoistedVariable,
                            newexp.Arguments.ToArray(), false
                        ), 
                        hoistedVariable
                    ),
                    type
                );

                ParentNode.ReplaceChild(newexp, replacement);
                VisitReplacement(replacement);
            } else {
                VisitChildren(newexp);
            }
        }

        private JSExpression CreateHoistedVariable (
            Func<JSVariable, JSExpression> update, 
            TypeReference type,
            JSExpression defaultValue = null
        ) {
            if (update == null)
                throw new ArgumentNullException("update");

            string id;
            var hoistedVariable = MakeTemporaryVariable(type, out id, defaultValue);
            var replacement = update(hoistedVariable);
            return replacement;
        }

        private JSExpression CreateHoistedVariable (
            Func<JSVariable, JSExpression> update,
            TypeReference type,
            JSExpression array,
            JSExpression index,
            JSExpression defaultValue = null
        ) {
            Func<JSNode, bool> filter = (e) =>
                    (e is JSUnaryOperatorExpression) ||
                    (e is JSInvocationExpression);

            if (update == null)
                throw new ArgumentNullException("update");

            // If the array or index expressions are not simple, don't attempt to cache.
            if (
                (array == null) ||
                (index == null) ||
                index.AllChildrenRecursive.Any(filter) || 
                array.AllChildrenRecursive.Any(filter)
            )
                return CreateHoistedVariable(
                    update, type, defaultValue
                );

            var key = new VariableCacheKey(array, index);
            JSVariable hoistedVariable;

            if (!CachedHoistedVariables.TryGetValue(key, out hoistedVariable)) {
                string id;
                hoistedVariable = MakeTemporaryVariable(type, out id, defaultValue);
                CachedHoistedVariables[key] = hoistedVariable;
            }

            var replacement = update(hoistedVariable);
            return replacement;
        }
    }
}
