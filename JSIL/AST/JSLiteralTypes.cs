﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using JSIL.Internal;
using Mono.Cecil;

namespace JSIL.Ast {
    public class JSDefaultValueLiteral : JSLiteralBase<TypeReference> {
        public class Token {
        }

        public int? CachedTypeIndex;

        public JSDefaultValueLiteral (TypeReference type)
            : base(type) {
            if (type == null)
                throw new ArgumentNullException("type");
        }

        public override object Literal {
            get {
                return new Token();
            }
        }

        public override TypeReference GetActualType (TypeSystem typeSystem) {
            return Value;
        }

        public override bool Equals (object obj) {
            var rhs = obj as JSDefaultValueLiteral;

            if (rhs != null) {
                return TypeUtil.TypesAreEqual(Value, rhs.Value);
            } else {
                return base.Equals(obj);
            }
        }

        public override bool IsConstant {
            get {
                return true;
            }
        }

        public override int GetHashCode() {
            return base.GetHashCode();
        }

        public override string ToString () {
            return String.Format("default({0})", Value.FullName);
        }
    }

    public class JSNullLiteral : JSLiteralBase<object> {
        public readonly TypeReference Type;

        public JSNullLiteral (TypeReference type)
            : base(null) {

            Type = type;
        }

        public override TypeReference GetActualType (TypeSystem typeSystem) {
            if (Type != null)
                return Type;
            else
                return typeSystem.Object;
        }

        public override string ToString () {
            return "null";
        }
    }

    public class JSBooleanLiteral : JSLiteralBase<bool> {
        public JSBooleanLiteral (bool value)
            : base(value) {
        }

        public override TypeReference GetActualType (TypeSystem typeSystem) {
            return typeSystem.Boolean;
        }
    }

    public class JSCharLiteral : JSLiteralBase<char> {
        public JSCharLiteral (char value)
            : base(value) {
        }

        public override TypeReference GetActualType (TypeSystem typeSystem) {
            return typeSystem.Char;
        }

        public override string ToString () {
            return Util.EscapeCharacter(Value, true);
        }
    }

    public class JSStringLiteral : JSLiteralBase<string> {
        public JSStringLiteral (string value)
            : base(value) {
        }

        public override TypeReference GetActualType (TypeSystem typeSystem) {
            return typeSystem.String;
        }

        public override string ToString () {
            return Util.EscapeString(Value, '"');
        }
    }

    public class JSIntegerLiteral : JSLiteralBase<long> {
        public readonly Type OriginalType;

        public JSIntegerLiteral (long value, Type originalType)
            : base(value) {

            OriginalType = originalType;
        }

        public override TypeReference GetActualType (TypeSystem typeSystem) {
            if (OriginalType != null) {
                switch (OriginalType.FullName) {
                    case "System.Byte":
                        return typeSystem.Byte;
                    case "System.SByte":
                        return typeSystem.SByte;
                    case "System.UInt16":
                        return typeSystem.UInt16;
                    case "System.Int16":
                        return typeSystem.Int16;
                    case "System.UInt32":
                        return typeSystem.UInt32;
                    case "System.Int32":
                        return typeSystem.Int32;
                    case "System.UInt64":
                        return typeSystem.UInt64;
                    case "System.Int64":
                        return typeSystem.Int64;
                    default:
                        throw new NotImplementedException(String.Format(
                            "Unsupported integer literal type: {0}",
                            OriginalType.FullName
                        ));
                }
            } else
                return typeSystem.Int64;
        }

        public override string ToString () {
            return String.Format("{0}", Value);
        }
    }

    public class JSNativeIntegerLiteral : JSLiteralBase<int> {
        public JSNativeIntegerLiteral (int value)
            : base(value) {
        }

        public override TypeReference GetActualType (TypeSystem typeSystem) {
            return typeSystem.NativeInt();
        }

        public override string ToString () {
            return String.Format("{0}", Value);
        }
    }

    public class JSEnumLiteral : JSLiteralBase<long> {
        public readonly TypeReference EnumType;
        public readonly string[] Names;

        public JSEnumLiteral (long rawValue, params EnumMemberInfo[] members)
            : base(rawValue) {

            EnumType = members.First().DeclaringType;
            Names = (from m in members select m.Name).OrderBy((s) => s).ToArray();
        }

        public override TypeReference GetActualType (TypeSystem typeSystem) {
            return EnumType;
        }

        public override string ToString () {
            return String.Format("<{0}>", String.Join(
                " | ", (from n in Names select String.Format("{0}.{1}", EnumType.Name, n)).ToArray()
            ));
        }

        public JSCachedType CachedEnumType {
            get;
            private set;
        }

        internal void SetCachedType (JSCachedType cachedType) {
            if (cachedType == null)
                return;

            if (CachedEnumType != null)
                throw new InvalidOperationException("Cached type already set");

            CachedEnumType = cachedType;
        }

        internal static JSEnumLiteral TryCreate (TypeInfo enumTypeInfo, long value) {
            EnumMemberInfo[] enumMembers = null;
            if (enumTypeInfo.IsFlagsEnum) {
                if (value == 0) {
                    enumMembers = (
                        from em in enumTypeInfo.EnumMembers.Values
                        where em.Value == 0
                        select em
                    ).Take(1).ToArray();
                } else {
                    var allMatchingMembers = (
                        from em in enumTypeInfo.EnumMembers.Values
                        where (em.Value != 0) &&
                            ((value & em.Value) == em.Value)
                        select em
                    );

                    // For scenarios where a flags enum has an 'Any' value, don't duplicate the subflags.
                    var filteredMatchingMembers = (
                        from em in allMatchingMembers
                        where !allMatchingMembers.Any((om) => (Math.Abs(om.Value) > em.Value) && ((om.Value & em.Value) == em.Value))
                        select em
                    );

                    enumMembers = filteredMatchingMembers.ToArray();
                }
            } else {
                EnumMemberInfo em;
                if (enumTypeInfo.ValueToEnumMember.TryGetValue(value, out em))
                    enumMembers = new [] { em };
            }

            if ((enumMembers != null) && (enumMembers.Length > 0))
                return new JSEnumLiteral(value, enumMembers);

            return null;
        }
    }

    public class JSDecimalLiteral : JSLiteralBase<Decimal>
    {
        public JSDecimalLiteral(decimal value)
            : base(value)
        {
        }

        public override TypeReference GetActualType(TypeSystem typeSystem)
        {
            return new TypeReference(typeSystem.Double.Namespace, "Decimal", typeSystem.Double.Module,
                typeSystem.Double.Scope, true);
        }
    }

    public class JSNumberLiteral : JSLiteralBase<double> {
        public readonly Type OriginalType;

        public JSNumberLiteral (double value, Type originalType)
            : base(value) {

            OriginalType = originalType;
        }

        public override TypeReference GetActualType (TypeSystem typeSystem) {
            if (OriginalType != null) {
                switch (OriginalType.FullName) {
                    case "System.Single":
                        return typeSystem.Single;
                    case "System.Double":
                        return typeSystem.Double;
                    default:
                        throw new NotImplementedException(String.Format(
                            "Unsupported number literal type: {0}",
                            OriginalType.FullName
                        ));
                }
            } else
                return typeSystem.Double;
        }
    }

    public class JSTypeNameLiteral : JSLiteralBase<TypeReference> {
        public JSTypeNameLiteral (TypeReference value)
            : base(value) {
        }

        public override TypeReference GetActualType (TypeSystem typeSystem) {
            return typeSystem.String;
        }
    }

    public class JSAssemblyNameLiteral : JSLiteralBase<AssemblyDefinition> {
        public JSAssemblyNameLiteral (AssemblyDefinition value)
            : base(value) {
        }

        public override TypeReference GetActualType (TypeSystem typeSystem) {
            return typeSystem.String;
        }
    }

    public class JSVerbatimLiteral : JSLiteral {
        public readonly string Description;
        public readonly TypeReference Type;
        public readonly string Expression;
        public readonly IDictionary<string, JSExpression> Variables;
        public readonly bool IsConstantIfArgumentsAre;

        public JSVerbatimLiteral (string description, string expression, IDictionary<string, JSExpression> variables, TypeReference type = null, bool isConstantIfArgumentsAre = false)
            : base(GetValues(variables)) {

            Description = description;
            Type = type;
            Expression = expression;
            Variables = variables;
            IsConstantIfArgumentsAre = isConstantIfArgumentsAre;
        }

        protected static JSExpression[] GetValues (IDictionary<string, JSExpression> variables) {
            if (variables != null)
                return variables.Values.ToArray();
            else
                return new JSExpression[0];
        }

        public override object Literal {
            get { return Expression; }
        }

        public override bool IsConstant {
            get {
                if (!IsConstantIfArgumentsAre)
                    return false;
                else
                    return Variables.Values.All((v) => v.IsConstant);
            }
        }

        public override TypeReference GetActualType (TypeSystem typeSystem) {
            if (Type != null)
                return Type;
            else
                return typeSystem.Object;
        }

        public override void ReplaceChild (JSNode oldChild, JSNode newChild) {
            if (Variables != null)
            foreach (var key in Variables.Keys.ToArray()) {
                if (Variables[key] == oldChild)
                    Variables[key] = (JSExpression)newChild;
            }

            base.ReplaceChild(oldChild, newChild);
        }

        public override string ToString () {
            string variablesText = "";

            if (Variables != null)
                variablesText = String.Join(", ", (from kvp in Variables select String.Format("{0}={1}", kvp.Key, kvp.Value)).ToArray());

            return String.Format(
                "Verbatim {0} ({1})", Description,
                variablesText
            );
        }
    }

    public class JSPointerLiteral : JSLiteralBase<long> {
        public readonly TypeReference PointerType;

        public JSPointerLiteral (long value, TypeReference pointerType)
            : base (value) {

            PointerType = pointerType;
        }

        public override TypeReference GetActualType (TypeSystem typeSystem) {
            return PointerType;
        }
    }
}
