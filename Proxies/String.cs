﻿#pragma warning disable 0660
#pragma warning disable 0661

using System;
using System.Collections.Generic;
using JSIL.Meta;
using JSIL.Proxy;

namespace JSIL.Proxies {
    [JSProxy(
        typeof(String),
        JSProxyMemberPolicy.ReplaceDeclared
    )]
    public abstract class StringProxy {
        [JSExternal]
        [JSRuntimeDispatch]
        [JSIsPure]
        public static string Format (params AnyType[] arguments) {
            throw new InvalidOperationException();
        }

        [JSReplacement("JSIL.ConcatString.apply(null, $arguments)")]
        [JSIsPure]
        public static string Concat (params string[] arguments) {
            throw new InvalidOperationException();
        }

        [JSReplacement("JSIL.ConcatString.apply(null, $arguments)")]
        [JSIsPure]
        public static string Concat (params object[] arguments) {
            throw new InvalidOperationException();
        }

        [JSReplacement("String($value)")]
        [JSIsPure]
        public static string Concat (object value) {
            throw new InvalidOperationException();
        }

        [JSReplacement("JSIL.ConcatString($a, $b)")]
        [JSIsPure]
        public static string Concat (object a, object b) {
            throw new InvalidOperationException();
        }

        [JSReplacement("JSIL.ConcatString($a, $b)")]
        [JSIsPure]
        public static string Concat (string a, string b) {
            throw new InvalidOperationException();
        }

        [JSReplacement("JSIL.ConcatString($a, $b, $c)")]
        [JSIsPure]
        public static string Concat (object a, object b, object c) {
            throw new InvalidOperationException();
        }

        [JSReplacement("($a + $b + $c)")]
        [JSIsPure]
        public static string Concat (string a, string b, string c) {
            throw new InvalidOperationException();
        }

        [JSReplacement("JSIL.ConcatString($a, $b, $c, $d)")]
        [JSIsPure]
        public static string Concat (object a, object b, object c, object d) {
            throw new InvalidOperationException();
        }

        [JSReplacement("($a + $b + $c + $d)")]
        [JSIsPure]
        public static string Concat (string a, string b, string c, string d) {
            throw new InvalidOperationException();
        }

        [JSReplacement("$lhs + $ch")]
        [JSIsPure]
        public static string Concat (string lhs, char ch) {
            throw new InvalidOperationException();
        }

        [JSIsPure]
        [JSReplacement("JSIL.SplitString($this, $dividers)")]
        public abstract string[] Split (AnyType[] dividers);

        [JSIsPure]
        [JSReplacement("JSIL.SplitString($this, $dividers, $options)")]
        public abstract string[] Split (AnyType[] dividers, StringSplitOptions options);

        [JSIsPure]
        [JSReplacement("JSIL.JoinEnumerable($separator, $values)")]
        public static string Join<T> (string separator, IEnumerable<T> values) {
            throw new NotImplementedException();
        }

        [JSIsPure]
        [JSReplacement("JSIL.JoinStrings($separator, $values)")]
        public static string Join (string separator, params string[] values) {
            throw new NotImplementedException();
        }

        [JSIsPure]
        [JSReplacement("JSIL.JoinEnumerable($separator, $values)")]
        public static string Join (string separator, IEnumerable<string> values) {
            throw new NotImplementedException();
        }

        [JSChangeName("length")]
        [JSAlwaysAccessAsProperty]
        [JSNeverReplace]
        abstract public int Length { get; }

        [JSReplacement("$this[$index]")]
        [JSIsPure]
        abstract public char get_Chars (int index);

        [JSReplacement("$lhs == $rhs")]
        [JSIsPure]
        public static bool operator == (StringProxy lhs, StringProxy rhs) {
            throw new InvalidOperationException();
        }

        [JSReplacement("$lhs != $rhs")]
        [JSIsPure]
        public static bool operator != (StringProxy lhs, StringProxy rhs) {
            throw new InvalidOperationException();
        }

        [JSReplacement("$this == $rhs")]
        [JSIsPure]
        public bool Equals (StringProxy rhs) {
            throw new InvalidOperationException();
        }

        [JSReplacement("$this.toLowerCase()")]
        [JSIsPure]
        public string ToLower () {
            throw new InvalidOperationException();
        }

        // FIXME: Are ECMAScript strings always normalized?
        [JSReplacement("$this.toLowerCase()")]
        [JSIsPure]
        public string ToLowerInvariant() {
            throw new InvalidOperationException();
        }

        [JSReplacement("$this.toUpperCase()")]
        [JSIsPure]
        public string ToUpper () {
            throw new InvalidOperationException();
        }

        // FIXME: Are ECMAScript strings always normalized?
        [JSReplacement("$this.toUpperCase()")]
        [JSIsPure]
        public string ToUpperInvariant() {
            throw new InvalidOperationException();
        }

        // FIXME: Are ECMAScript strings always normalized?
        [JSReplacement("$this")]
        [JSIsPure]
        public string Normalize () {
            throw new InvalidOperationException();
        }
      
        [JSReplacement("System.String.StartsWith($this, $text)")]
        [JSIsPure]
        public bool StartsWith (string text) {
            throw new InvalidOperationException();
        }

        [JSReplacement("System.String.EndsWith($this, $text)")]
        [JSIsPure]
        public bool EndsWith (string text) {
            throw new InvalidOperationException();
        }

        [JSReplacement("$this.trim()")]
        [JSIsPure]
        public string Trim () {
            throw new InvalidOperationException();
        }

        [JSReplacement("System.String.Compare($this, $rhs)")]
        [JSIsPure]
        public int CompareTo (string rhs) {
            throw new InvalidOperationException();
        }

        [JSReplacement("$this.indexOf($value)")]
        [JSIsPure]
        public int IndexOf (char value) {
            throw new InvalidOperationException();
        }

        [JSReplacement("$this.indexOf($value, $startIndex)")]
        [JSIsPure]
        public int IndexOf (char value, int startIndex) {
            throw new InvalidOperationException();
        }

        [JSReplacement("$this.indexOf($value)")]
        [JSIsPure]
        public int IndexOf (string value) {
            throw new InvalidOperationException();
        }

        [JSReplacement("$this.indexOf($value, $startIndex)")]
        [JSIsPure]
        public int IndexOf (string value, int startIndex) {
            throw new InvalidOperationException();
        }

        [JSReplacement("$this.lastIndexOf($value)")]
        [JSIsPure]
        public int LastIndexOf (char value) {
            throw new InvalidOperationException();
        }

        [JSReplacement("$this.lastIndexOf($value)")]
        [JSIsPure]
        public int LastIndexOf (string value) {
            throw new InvalidOperationException();
        }

        [JSReplacement("System.String.IndexOfAny($this, $chars)")]
        [JSIsPure]
        public int IndexOfAny (char[] chars) {
            throw new InvalidOperationException();
        }

        [JSReplacement("System.String.IndexOfAny($this, $chars, $startIndex)")]
        [JSIsPure]
        public int IndexOfAny(char[] chars, int startIndex)
        {
            throw new InvalidOperationException();
        }

        [JSReplacement("System.String.LastIndexOfAny($this, $chars)")]
        [JSIsPure]
        public int LastIndexOfAny (char[] chars) {
            throw new InvalidOperationException();
        }

        [JSReplacement("$this.substr($position)")]
        [JSIsPure]
        public string Substring (int position) {
            throw new InvalidOperationException();
        }

        [JSReplacement("$this.substr($position, $length)")]
        [JSIsPure]
        public string Substring (int position, int length) {
            throw new InvalidOperationException();
        }

        // FIXME
        /*
        [JSReplacement("$this.lastIndexOf($value, $startIndex)")]
        [JSIsPure]
        public int LastIndexOf (char value, int startIndex) {
            throw new InvalidOperationException();
        }

        [JSReplacement("$this.lastIndexOf($value, $startIndex)")]
        [JSIsPure]
        public int LastIndexOf (string value, int startIndex) {
            throw new InvalidOperationException();
        }
         */

        [JSReplacement("System.String.Replace($this, $oldText, $newText)")]
        [JSIsPure]
        public string Replace (string oldText, string newText) {
            throw new InvalidOperationException();
        }

        [JSReplacement("System.String.Replace($this, $oldChar, $newChar)")]
        [JSIsPure]
        public string Replace (char oldChar, char newChar) {
            throw new InvalidOperationException();
        }

        [JSReplacement("JSIL.StringToCharArray($this)")]
        [JSIsPure]
        public char[] ToCharArray () {
            throw new InvalidOperationException();
        }

        [JSReplacement("($this.indexOf($p) != -1)")]
        public bool Contains (string p) {
            throw new InvalidOperationException();
        }

        [JSReplacement("System.String.PadLeft($this, $length, ' ')")]
        [JSIsPure]
        public string PadLeft (int length) {
            throw new InvalidOperationException();
        }

        [JSReplacement("System.String.PadRight($this, $length, ' ')")]
        [JSIsPure]
        public string PadRight (int length) {
            throw new InvalidOperationException();
        }

        [JSReplacement("System.String.PadLeft($this, $length, $ch)")]
        [JSIsPure]
        public string PadLeft (int length, char ch) {
            throw new InvalidOperationException();
        }

        [JSReplacement("System.String.PadRight($this, $length, $ch)")]
        [JSIsPure]
        public string PadRight (int length, char ch) {
            throw new InvalidOperationException();
        }

        [JSReplacement("$this.substr(0, $startIndex)")]
        [JSIsPure]
        public string Remove (int startIndex) {
            throw new InvalidOperationException();
        }

        [JSReplacement("System.String.Remove($this, $startIndex, $count)")]
        [JSIsPure]
        public string Remove (int startIndex, int count) {
            throw new InvalidOperationException();
        }

        [JSReplacement("System.String.CopyTo($this, $sourceIndex, $destination, $destinationIndex, $count)")]
        public void CopyTo (int sourceIndex, char[] destination, int destinationIndex, int count) {
            throw new InvalidOperationException();
        }

        [JSReplacement("(System.String.Compare($this, $rhs, $comparison) == 0)")]
        [JSIsPure]
        public bool Equals(string rhs, StringComparison comparison) {
            throw new InvalidOperationException();
        }

        [JSReplacement("$lhs === $rhs")]
        [JSIsPure]
        public static bool Equals(string lhs, string rhs) {
            throw new InvalidOperationException();
        }

        [JSReplacement("(System.String.Compare($lhs, $rhs, $comparison) === 0)")]
        [JSIsPure]
        public static bool Equals(string lhs, string rhs, StringComparison comparison) {
            throw new InvalidOperationException();
        }
    }
}
