﻿using System;
using System.Reflection;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Common {
    public static class Util {
        public static string[] GetMemberNames<T> (Type type, BindingFlags flags) where T : MemberInfo {
            var members = type.GetMembers(flags);

            int count = 0;
            for (int i = 0; i < members.Length; i++) {
                if (members[i] is T)
                    count += 1;
            }

            var names = new string[count];
            for (int i = 0, j = 0; i < members.Length; i++) {
                T t = (members[i] as T);
                if ((object)t == null)
                    continue;

                names[j] = t.Name;
                j += 1;
            }

            Array.Sort(names);

            return names;
        }

        public static int AssertMembers<T> (Type type, BindingFlags flags, params string[] names) where T : MemberInfo {
            int result = 0;
            var methodNames = new List<string>(GetMemberNames<T>(type, flags));

            foreach (var name in names) {
                int count = methodNames.FindAll((n) => n == name).Count;

                if (count < 1)
                    Console.WriteLine("{0} not in members of {1}", name, type);

                result += count;
            }

            return result;
        }

        public static void ListMembers<T> (Type type, BindingFlags flags) where T : MemberInfo {
            var methodNames = GetMemberNames<T>(type, flags);

            Console.WriteLine();
            foreach (var methodName in methodNames)
                Console.WriteLine(methodName);
        }

        public static string[] GetTypeNames (Assembly asm, string filterRegex = null) {
            var types = asm.GetTypes();

            Regex regex = null;
            if (filterRegex != null)
                regex = new Regex(filterRegex, RegexOptions.ECMAScript);

            var result = new List<string>();
            for (int i = 0, l = types.Length; i < l; i++) {
                var fullName = types[i].FullName;
                if ((regex == null) || regex.IsMatch(fullName))
                    result.Add(fullName);
            }

            result.Sort();

            return result.ToArray();
        }

        public static void ListTypes (Assembly asm, string filterRegex = null) {
            var typeNames = GetTypeNames(asm, filterRegex);

            Console.WriteLine();
            foreach (var typeName in typeNames)
                Console.WriteLine(typeName);
        }

        public static void ListAttributes (MemberInfo member, bool inherit, Type attributeType = null) {
            object[] attributes;

            if (attributeType != null)
                attributes = member.GetCustomAttributes(attributeType, inherit);
            else
                attributes = member.GetCustomAttributes(inherit);

            WriteSorted(attributes);
        }

        public static void WriteSorted(object[] items)
        {
            string[] itemStrings = new string[items.Length];
            for (int i = 0; i < items.Length; i++)
            {
                itemStrings[i] = items[i].ToString();
            }
            Array.Sort(itemStrings);

            foreach (var item in itemStrings)
                Console.WriteLine(item);
        }

        public static void ListMethods (Type type, BindingFlags flags) {
            foreach (var method in type.GetMethods(flags)) {
                var argumentTypes = "";
                foreach (var parameter in method.GetParameters())
                    argumentTypes += parameter.ParameterType.Name;

                Console.WriteLine("{0} {1} ({2})", method.ReturnType.Name, method.Name, argumentTypes);
            }
        }

        public static void ListConstructors (Type type, BindingFlags flags) {
            foreach (var constructor in type.GetConstructors(flags)) {
                var argumentTypes = "";
                foreach (var parameter in constructor.GetParameters())
                    argumentTypes += parameter.ParameterType.Name;

                Console.WriteLine("({0})", argumentTypes);
            }
        }
    }

    public class AttributeA : Attribute {
    }

    public class AttributeB : Attribute {
        public readonly int Arg1;
        public readonly string Arg2;

        public AttributeB (int arg1, string arg2) {
            Arg1 = arg1;
            Arg2 = arg2;
        }

        public override string ToString () {
            return String.Format("AttributeB({0}, {1})", Arg1, Arg2);
        }
    }
}