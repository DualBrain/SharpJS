﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using JSIL.Ast;
using JSIL.Internal;
using Mono.Cecil;

namespace JSIL.Transforms {
    public class EliminatePointlessFinallyBlocks : StaticAnalysisJSAstVisitor {
        public readonly TypeSystem TypeSystem;
        public readonly ITypeInfoSource TypeInfo;

        public EliminatePointlessFinallyBlocks (QualifiedMemberIdentifier member, IFunctionSource functionSource, TypeSystem typeSystem, ITypeInfoSource typeInfo)
            : base (member, functionSource) {
            TypeSystem = typeSystem;
            TypeInfo = typeInfo;
        }

        protected bool IsEffectivelyConstant (JSExpression expression) {
            if (expression.IsConstant)
                return true;

            var invocation = expression as JSInvocationExpression;
            FunctionAnalysis2ndPass secondPass = null;
            if ((invocation != null) && (invocation.JSMethod != null)) {
                secondPass = GetSecondPass(invocation.JSMethod);

                if ((secondPass != null) && secondPass.IsPure)
                    return true;

                var methodName = invocation.JSMethod.Method.Name;
                if ((methodName == "IDisposable.Dispose") || (methodName == "Dispose")) {
                    var thisType = invocation.ThisReference.GetActualType(TypeSystem);

                    if (thisType != null) {
                        var typeInfo = TypeInfo.GetExisting(thisType);

                        if ((typeInfo != null) && typeInfo.Metadata.HasAttribute("JSIL.Meta.JSPureDispose"))
                            return true;
                    }
                }
            }

            return false;
        }

        public void VisitNode (JSTryCatchBlock tcb) {
            if ((tcb.Finally != null) && (tcb.Catch == null)) {
                do {
                    if (!tcb.Finally.Children.All((n) => n is JSExpressionStatement))
                        break;

                    var statements = tcb.Finally.Children.OfType<JSExpressionStatement>().ToArray();

                    if (statements.Any((es) => es.Expression.HasGlobalStateDependency))
                        break;

                    if (!statements.All((es) => IsEffectivelyConstant(es.Expression)))
                        break;

                    ParentNode.ReplaceChild(tcb, tcb.Body);
                    VisitReplacement(tcb.Body);
                    return;
                } while (false);
            }

            VisitChildren(tcb);
        }
    }
}
