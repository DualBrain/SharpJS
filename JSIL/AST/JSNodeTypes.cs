﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using ICSharpCode.Decompiler.ILAst;
using JSIL.Ast.Enumerators;
using JSIL.Ast.Traversal;
using JSIL.Internal;
using JSIL.Transforms;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace JSIL.Ast {
    public abstract class JSNode {
        // Add your assembly to this list if you define custom node types.
        public static readonly List<Assembly> NodeAssemblies = new List<Assembly>();

        private static bool     AreTypeIdsAssigned = false;
        private static readonly Dictionary<Type, int> TypeIds = new Dictionary<Type, int>(new ReferenceComparer<Type>());

        private static int[][] _NodeSelfAndBaseTypeIds;
        private static Type[] _NodeTypes;

        public readonly int TypeId;
        public readonly JSNodeChildren Children;
        public readonly JSNodeChildren SelfAndChildren;
        public readonly JSNodeChildrenRecursive AllChildrenRecursive;
        public readonly JSNodeChildrenRecursive SelfAndChildrenRecursive;

        static JSNode () {
            NodeAssemblies.Add(typeof(JSNode).Assembly);
        }

        public static int[][] NodeSelfAndBaseTypeIds {
            get {
                EnsureTypeIdsAreAssigned();
                return _NodeSelfAndBaseTypeIds;
            }
        }

        public SymbolInfo SymbolInfo { get; set; }

        public static Type[] NodeTypes {
            get {
                EnsureTypeIdsAreAssigned();
                return _NodeTypes;
            }
        }

        private static IEnumerable<Type> TypesToWalk (Assembly assembly) {
            var myAssembly = typeof(JSNode).Assembly;
            if (myAssembly == assembly)
                return assembly.GetTypes();
            else
                return assembly.ExportedTypes;
        }

        public static void EnsureTypeIdsAreAssigned () {
            if (AreTypeIdsAssigned)
                return;

            AreTypeIdsAssigned = true;

            var tNode = typeof(JSNode);
            var typesToWalk = (
                    from a in NodeAssemblies
                    from t in TypesToWalk(a)
                    where tNode.IsAssignableFrom(t)
                    select t
                ).ToList();
            var nodeTypesById = new List<Type>();

            // Assign a unique ID to all node types in the type hierarchy
            foreach (var nodeType in typesToWalk) {
                var type = nodeType;

                while (type != null) {
                    if (!TypeIds.ContainsKey(type)) {
                        TypeIds.Add(type, nodeTypesById.Count);
                        nodeTypesById.Add(type);
                    }

                    if (type == tNode)
                        break;

                    type = type.BaseType;
                }
            }

            _NodeTypes = nodeTypesById.ToArray();
            _NodeSelfAndBaseTypeIds = new int[_NodeTypes.Length][];

            var ids = new List<int>();
            for (var i = 0; i < _NodeTypes.Length; i++) {
                ids.Clear();

                var type = _NodeTypes[i];
                while (type != null) {
                    ids.Add(GetTypeId(type));
                    if (type == tNode)
                        break;

                    type = type.BaseType;
                }

                _NodeSelfAndBaseTypeIds[i] = ids.ToArray();
            }

            JSExpression.Initialize();
            JSNodeTraversalData.Initialize();
        }

        public static int GetTypeId (Type nodeType) {
            EnsureTypeIdsAreAssigned();

            int result;
            if (!TypeIds.TryGetValue(nodeType, out result))
                throw new InvalidOperationException(string.Format("Node type '{0}' was loaded after JSNode initialization", nodeType.FullName));

            return result;
        }

        protected JSNode () {
            TypeId = GetTypeId(GetType());

            var td = JSNodeTraversalData.Get(TypeId);

            Children = new JSNodeChildren(this, td, false);
            SelfAndChildren = new JSNodeChildren(this, td, true);
            AllChildrenRecursive = new JSNodeChildrenRecursive(this, td, false);
            SelfAndChildrenRecursive = new JSNodeChildrenRecursive(this, td, true);
        }

        /// <summary>
        /// If true, the node should be treated as a null node without any actual impact on the output javascript.
        /// </summary>
        public virtual bool IsNull {
            get {
                return false;
            }
        }

        public abstract void ReplaceChild (JSNode oldChild, JSNode newChild);

        public virtual void ReplaceChildRecursive (JSNode oldChild, JSNode newChild) {
            ReplaceChild(oldChild, newChild);

            using (var e = Children.EnumeratorTemplate)
            while (e.MoveNext()) {
                var child = e.Current;

                if ((child != null) && (child != newChild))
                    child.ReplaceChildRecursive(oldChild, newChild);
            }
        }
    }

    public class SymbolInfo
    {
        public readonly IEnumerable<SequencePoint> SequencePoints;
        public readonly bool Inferred;

        public SymbolInfo(IEnumerable<SequencePoint> sequencePoints, bool inferred)
        {
            if (sequencePoints == null)
            {
                throw new ArgumentNullException("sequencePoints");
            }
            if (!sequencePoints.Any())
            {
                throw new ArgumentException("sequencePonts should not be empty", "sequencePoints");
            }

            SequencePoints = sequencePoints;
            Inferred = inferred;
        }
    }

    [JSAstIgnoreInheritedMembers]
    public abstract class JSExpression : JSNode {
        private static string[][] TypeToValueNames;

        public static readonly JSNullExpression Null = new JSNullExpression();

        [JSAstIgnore]
        protected readonly JSExpression[] Values;

        private readonly string[] _ActualValueNames;

        public static void Initialize () {
            TypeToValueNames = new string[NodeTypes.Length][];
        }

        protected JSExpression (params JSExpression[] values) {
            Values = values;

            foreach (var id in NodeSelfAndBaseTypeIds[TypeId]) {
                _ActualValueNames = TypeToValueNames[id];
                if (_ActualValueNames != null)
                    break;
            }
        }

        public override string ToString () {
            return String.Format(
                "{0}[{1}]", GetType().Name,
                String.Join(", ", (from v in Values select String.Concat(v)).ToArray())
            );
        }

        protected static void SetValueNames (Type nodeType, params string[] valueNames) {
            var id = JSNode.GetTypeId(nodeType);

            if (TypeToValueNames[id] != null)
                throw new InvalidOperationException("Value names already set for this node type");

            TypeToValueNames[id] = valueNames;
        }

        protected string GetValueName (int index) {
            if (_ActualValueNames == null)
                return null;

            if (index >= _ActualValueNames.Length)
                return _ActualValueNames[_ActualValueNames.Length - 1];
            else
                return _ActualValueNames[index];
        }

        // HACK: This function is invoked via reflection only!
        [JSAstTraverse(0)]
        static bool GetValue (JSNode parent, int index, out JSNode node, out string name) {
            JSExpression expr = (JSExpression)parent;
            var values = expr.Values;
            if (index >= values.Length) {
                node = null;
                name = null;
                return false;
            } else {
                node = values[index];
                name = expr.GetValueName(index) ?? "Values";

                return true;
            }
        }

        public virtual TypeReference GetActualType (TypeSystem typeSystem) {
            throw new NoExpectedTypeException(this);
        }

        public static TypeReference DeReferenceType (TypeReference type, bool once = false) {
            var brt = type as ByReferenceType;

            while (brt != null) {
                type = brt.ElementType;
                brt = type as ByReferenceType;

                if (once)
                    break;
            }

            return type;
        }

        public static TypeReference SubstituteTypeArgs (ITypeInfoSource typeInfo, TypeReference type, MemberReference member) {
            var gp = (type as GenericParameter);

            if (gp != null) {
                if (gp.Owner.GenericParameterType == GenericParameterType.Method) {
                    var ownerIdentifier = new MemberIdentifier(typeInfo, (MethodReference)gp.Owner);
                    var memberIdentifier = new MemberIdentifier(typeInfo, (dynamic)member);

                    if (!ownerIdentifier.Equals(memberIdentifier, typeInfo))
                        return type;

                    if (!(member is GenericInstanceMethod))
                        return type;
                } else {
                    var declaringType = member.DeclaringType.Resolve();
                    // FIXME: Is this right?
                    if (declaringType == null)
                        return type;

                    var ownerResolved = ((TypeReference)gp.Owner).Resolve();
                    // FIXME: Is this right?
                    if (ownerResolved == null)
                        return type;

                    var ownerIdentifier = new TypeIdentifier(ownerResolved);
                    var typeIdentifier = new TypeIdentifier(declaringType);

                    if (!ownerIdentifier.Equals(typeIdentifier))
                        return type;
                }
            }

            return TypeAnalysis.SubstituteTypeArgs(type, member);
        }

        public override void ReplaceChild (JSNode oldChild, JSNode newChild) {
            if (oldChild == null)
                throw new ArgumentNullException("oldChild");

            if (newChild == this)
                throw new InvalidOperationException("Infinite recursion");

            if ((newChild != null) && !(newChild is JSExpression))
                return;

            var expr = (JSExpression)newChild;

            for (int i = 0, c = Values.Length; i < c; i++) {
                if (Values[i] == oldChild)
                    Values[i] = expr;
            }
        }

        protected bool EqualsImpl (object obj, bool fieldsChecked) {
            if (this == obj)
                return true;
            else if (obj == null)
                return false;
            else if (obj.GetType() != GetType())
                return false;

            var rhs = (JSExpression)obj;
            if (Values.Length != rhs.Values.Length)
                return false;

            if ((Values.Length == 0) && (!fieldsChecked))
                throw new NotImplementedException(String.Format("Expressions of type {0} cannot be compared", GetType().Name));

            for (int i = 0, c = Values.Length; i < c; i++) {
                var lhsV = Values[i];
                var rhsV = rhs.Values[i];

                if (!lhsV.Equals(rhsV))
                    return false;
            }

            return true;
        }

        /// <summary>
        /// If true, this expression has at least one dependency on static (non-local) state.
        /// </summary>
        public virtual bool HasGlobalStateDependency {
            get {
                return Values.Any((v) => v.HasGlobalStateDependency);
            }
        }

        /// <summary>
        /// If true, this expression is constant and has no dependencies on local or global state.
        /// </summary>
        public virtual bool IsConstant {
            get {
                return false;
            }
        }

        /// <summary>
        /// If true, this expression is a valid assignment/mutation target
        /// </summary>
        public virtual bool IsLValue {
            get {
                return false;
            }
        }

        public override bool Equals (object obj) {
            return EqualsImpl(obj, false);
        }

        protected int GetHashCodeOfValues () {
            return Values.Length;
        }

        public override int GetHashCode () {
            // HACK: Can we do better?
            //  I think this has to return 0 since derived types may actually ignore Values[] in their Equals implementation.
            // return Values.Length;
            return 0;
        }
    }

    public abstract class JSIdentifier : JSExpression {
        protected readonly TypeReference _IdentifierType;

        protected JSIdentifier (TypeReference identifierType = null) {
            _IdentifierType = identifierType;
        }

        public override bool Equals (object obj) {
            var id = obj as JSIdentifier;

            if (id != null) {
                return String.Equals(Identifier, id.Identifier) &&
                    TypeUtil.TypesAreEqual(IdentifierType, id.IdentifierType) &&
                    EqualsImpl(obj, true);
            } else {
                return EqualsImpl(obj, true);
            }
        }

        public virtual TypeReference IdentifierType {
            get {
                return _IdentifierType;
            }
        }

        public override int GetHashCode () {
            return Identifier.GetHashCode();
        }

        public override TypeReference GetActualType (TypeSystem typeSystem) {
            if (_IdentifierType != null)
                return _IdentifierType;
            else
                return base.GetActualType(typeSystem);
        }

        public abstract string Identifier {
            get;
        }

        public override bool IsConstant {
            get {
                return false;
            }
        }

        public override string ToString () {
            return String.Format("<{0} '{1}'>", GetType().Name, Identifier);
        }

        public virtual JSLiteral ToLiteral () {
            return JSLiteral.New(Util.EscapeIdentifier(Identifier));
        }
    }

    public abstract class JSLiteral : JSExpression {
        internal JSLiteral (params JSExpression[] values)
            : base(values) {
        }

        public abstract object Literal {
            get;
        }

        public static JSAssemblyNameLiteral New (AssemblyDefinition value) {
            return new JSAssemblyNameLiteral(value);
        }

        public static JSTypeNameLiteral New (TypeReference value) {
            return new JSTypeNameLiteral(value);
        }

        public static JSStringLiteral New (string value) {
            return new JSStringLiteral(value);
        }

        public static JSBooleanLiteral New (bool value) {
            return new JSBooleanLiteral(value);
        }

        public static JSCharLiteral New (char value) {
            return new JSCharLiteral(value);
        }

        public static JSIntegerLiteral New (sbyte value) {
            return new JSIntegerLiteral(value, typeof(sbyte));
        }

        public static JSIntegerLiteral New (byte value) {
            return new JSIntegerLiteral(value, typeof(byte));
        }

        public static JSIntegerLiteral New (short value) {
            return new JSIntegerLiteral(value, typeof(short));
        }

        public static JSIntegerLiteral New (ushort value) {
            return new JSIntegerLiteral(value, typeof(ushort));
        }

        public static JSIntegerLiteral New (int value) {
            return new JSIntegerLiteral(value, typeof(int));
        }

        public static JSIntegerLiteral New (uint value) {
            return new JSIntegerLiteral(value, typeof(uint));
        }

        public static JSIntegerLiteral New (long value) {
            return new JSIntegerLiteral(value, typeof(long));
        }

        public static JSIntegerLiteral New (ulong value) {
            return new JSIntegerLiteral((long)value, typeof(ulong));
        }

        public static JSNumberLiteral New (float value) {
            return new JSNumberLiteral(value, typeof(float));
        }

        public static JSNumberLiteral New (double value) {
            return new JSNumberLiteral(value, typeof(double));
        }

        public static JSDecimalLiteral New (decimal value) {
            return new JSDecimalLiteral(value);
        }

        public static JSDefaultValueLiteral DefaultValue (TypeReference type) {
            return new JSDefaultValueLiteral(type);
        }

        new public static JSNullLiteral Null (TypeReference type) {
            return new JSNullLiteral(type);
        }
    }

    [JSAstIgnoreInheritedMembers]
    public abstract class JSLiteralBase<T> : JSLiteral {
        public readonly T Value;

        protected JSLiteralBase (T value) {
            Value = value;
        }

        public override object Literal {
            get {
                return this.Value;
            }
        }

        public override bool Equals (object obj) {
            if (obj == null)
                return false;

            if (GetType() != obj.GetType())
                return false;

            var rhs = (JSLiteralBase<T>)obj;
            var comparer = Comparer<T>.Default;

            return comparer.Compare(Value, rhs.Value) == 0;
        }

        public override int GetHashCode() {
            return GetType().GetHashCode() ^ Value.GetHashCode();
        }

        public override bool IsConstant {
            get {
                return true;
            }
        }

        public override string ToString () {
            return String.Format("<{0} {1}>", GetType().Name, Value);
        }
    }

    public abstract class JSStatement : JSNode {
        public static readonly JSNullStatement Null = new JSNullStatement();

        public string Label = null;
        public string OriginalLabel = null;

        protected virtual string PrependLabel (string text) {
            if (Label == null)
                return text;

            return String.Format("{0}: {1}", Label, text);
        }

        public override void ReplaceChild (JSNode oldChild, JSNode newChild) {
            if (oldChild == null)
                throw new ArgumentNullException("oldChild");

            throw new NotImplementedException(
                String.Format("Statements of type '{0}' do not support child replacement", GetType().Name)
            );
        }

        public bool IsControlFlow {
            get;
            internal set;
        }
    }

    public class NoExpectedTypeException : NotImplementedException {
        public NoExpectedTypeException (JSExpression node)
            : base(String.Format("Node of type {0} has no expected type: {1}", node.GetType().Name, node)) {
        }
    }
}
