﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using NUnit.Framework;

namespace JSIL.Tests {
    [TestFixture]
    public class FormattingTests : GenericTestFixture {
        // Type expression caching makes it hard to write these tests.
        protected override Translator.Configuration MakeConfiguration () {
            var configuration = base.MakeConfiguration();
            configuration.CodeGenerator.CacheTypeExpressions = false;
            return configuration;
        }

        [Test]
        public void ChainedElseIfs () {
            var generatedJs = GetJavascript(
                @"SpecialTestCases\ChainedElseIf.cs",
                "Two"
            );
            try {
                Assert.AreEqual(
                    4, generatedJs.Split(
                        new string[] { "else if" }, StringSplitOptions.RemoveEmptyEntries
                    ).Length
                );
            } catch {
                Console.WriteLine(generatedJs);

                throw;
            }
        }

        [Test]
        [FailsOnMono]
        public void StringSwitch () {
            var generatedJs = GetJavascript(
                @"SpecialTestCases\StringSwitch.cs",
                ""
            );
            try {
                Assert.IsFalse(generatedJs.Contains("(!text =="));
                Assert.IsTrue(generatedJs.Contains("(!(text =="));
            } catch {
                Console.WriteLine(generatedJs);

                throw;
            }
        }

        [Test]
        public void SwitchWithMultipleDefaults () {
            var generatedJs = GetJavascript(
                @"TestCases\ComplexSwitch.cs",
                "zero\r\none\r\ntwo or three\r\ntwo or three"
            );
            try {
                // TODO: The following will only work if switch statements with multiple default cases are collapsed into a single default case.

                /*
                Assert.IsFalse(generatedJs.Contains("__ = \"IL_"));
                Assert.IsFalse(generatedJs.Contains("case 1:"));
                 */

                Assert.IsTrue(generatedJs.Contains("default:"));
            } catch {
                Console.WriteLine(generatedJs);

                throw;
            }
        }

        [Test]
        [FailsOnMono]
        public void BigStringSwitch () {
            var generatedJs = GetJavascript(
                @"SpecialTestCases\BigStringSwitch.cs",
                ""
            );

            try {
                Assert.IsFalse(generatedJs.Contains(".TryGetValue"));

                Assert.IsTrue(generatedJs.Contains("for (var i = 0; i < (args.length | 0); i = "), "Was not a for loop with an increment");

                Assert.IsTrue(generatedJs.Contains("switch (text)"), "Didn't find switch (text)");
                Assert.IsTrue(generatedJs.Contains("case \"howdy\""), "Didn't find string cases");
            } catch {
                Console.WriteLine(generatedJs);

                throw;
            }
        }

        [Test]
        [FailsOnMono]
        public void RoslynBigStringSwitchWithStaticArray()
        {
            var generatedJs = GetJavascript(
                @"SpecialTestCases\RoslynBigStringSwitchWithStaticArray.cs"
            );

            try
            {
                Assert.IsFalse(generatedJs.Contains("StaticArrayInit"));
            }
            catch
            {
                Console.WriteLine(generatedJs);

                throw;
            }
        }

        [Test]
        public void StringConcat () {
            var generatedJs = GetJavascript(
                @"SpecialTestCases\StringConcat.cs",
                "abc\r\nde\r\nab5d"
            );
            try {
                Assert.AreEqual(
                    3,
                    generatedJs.Split(new String[] { "JSIL.ConcatString" }, StringSplitOptions.RemoveEmptyEntries).Length
                );
                Assert.IsFalse(generatedJs.Contains("WriteLine([\"a\", \"b\", 5, \"d\"])"));
            } catch {
                Console.WriteLine(generatedJs);

                throw;
            }
        }

        [Test]
        public void PostIncrement () {
            var generatedJs = GetJavascript(
                @"SpecialTestCases\PostIncrement.cs",
                "2\r\n3\r\n1\r\n0\r\n0\r\n0\r\n1",
                () => {
                    // Integer arithmetic hinting disables post-increment on ints.
                    var config = MakeConfiguration();
                    config.CodeGenerator.HintIntegerArithmetic = false;
                    return config;
                }
            );
            try {
                Assert.IsFalse(generatedJs.Contains("i + 1"));
                Assert.IsFalse(generatedJs.Contains("i - 1"));
                // FIXME: I don't actually remember why this assertion matters
                // Assert.IsFalse(generatedJs.Contains("this.Value + value"));
            } catch {
                Console.WriteLine(generatedJs);

                throw;
            }
        }

        // FIXME: We can't treat arrays as constant expressions, so this fails now.
        // [Test]
        public void EliminateSingleUseTemporaries () {
            var generatedJs = GetJavascript(
                @"SpecialTestCases\SingleUseTemporaries.cs",
                "a\r\nb\r\nc"
            );

            try {
                Assert.IsFalse(generatedJs.Contains("array = objs"));
                Assert.IsFalse(generatedJs.Contains("obj = array[i]"));
            } catch {
                Console.WriteLine(generatedJs);

                throw;
            }
        }

        [Test]
        [FailsOnMono]
        public void EliminateSingleUseExceptionTemporaries () {
            var generatedJs = GetJavascript(
                @"SpecialTestCases\SingleUseExceptionTemporaries.cs",
                "a\r\nb"
            );

            try {
                Assert.IsTrue(generatedJs.Contains("ex = $exception"));
            } catch {
                Console.WriteLine(generatedJs);

                throw;
            }
        }

        [Test]
        public void NestedInitialization () {
            var output = "a = 5, b = 7\r\na = 5, b = 7";
            GenericTest(
                @"SpecialTestCases\NestedInitialization.cs",
                output, output
            );
        }

        [Test]
        public void IfBooleanProperty () {
            var generatedJs = GetJavascript(
                @"SpecialTestCases\IfBooleanProperty.cs",
                "true\r\nfalse"
            );

            try {
                Assert.IsFalse(generatedJs.Contains("!!$thisType"));
                Assert.IsTrue(
                    generatedJs.Contains("!$thisType.P") ||
                    generatedJs.Contains("!$thisType.get_P()")
                );
            } catch {
                Console.WriteLine(generatedJs);

                throw;
            }
        }

        [Test]
        public void DisplayClassFieldNames () {
            var generatedJs = GetJavascript(
                @"SpecialTestCases\DisplayClassFieldNames.cs",
                "a()=x=1, y=y"
            );

            try {
                Assert.IsTrue(generatedJs.Contains(".x ="));
                Assert.IsTrue(generatedJs.Contains(".y ="));
            } catch {
                Console.WriteLine(generatedJs);

                throw;
            }
        }

        [Test]
        public void NewParentheses () {
            var generatedJs = GetJavascript(
                @"SpecialTestCases\NewParentheses.cs",
                "CustomType"
            );

            try {
                Assert.IsFalse(Regex.IsMatch(
                    generatedJs, 
                    @"\(new (\$asm([0-9A-F])*).CustomType"
                ));
                Assert.IsTrue(Regex.IsMatch(
                    generatedJs, 
                    @"new (\$asm([0-9A-F])*).CustomType\(\)"
                ));
            } catch {
                Console.WriteLine(generatedJs);

                throw;
            }
        }

        [Test]
        [FailsOnMono]
        public void EnumeratorClassLocalNames () {
            var generatedJs = GetJavascript(
                @"SpecialTestCases\EnumeratorClassLocalNames.cs",
                "0\r\n1\r\n2\r\n3\r\n4\r\n5\r\n6\r\n7\r\n8\r\n9"
            );

            try {
                // Welp.
                /*
                Assert.IsTrue(generatedJs.Contains("this.i"));
                Assert.IsTrue(generatedJs.Contains("this.$state"));
                Assert.IsTrue(generatedJs.Contains("this.$current"));
                Assert.IsFalse(generatedJs.Contains(".$li$g"));
                Assert.IsFalse(generatedJs.Contains(".$l$g1__state"));
                Assert.IsFalse(generatedJs.Contains(".$l$g2__current"));
                 */
                Assert.IsTrue(generatedJs.Contains(".$li$g"));
                Assert.IsTrue(generatedJs.Contains(".$l$g1__state"));
                Assert.IsTrue(generatedJs.Contains(".$l$g2__current"));
            } catch {
                Console.WriteLine(generatedJs);

                throw;
            }
        }

        [Test]
        public void RefParametersOnInstanceMethods () {
            var generatedJs = GetJavascript(
                @"SpecialTestCases\RefParametersOnInstanceMethods.cs",
                ".B = 0, i = 0\r\n.B = 1, i = 1\r\n.B = 3, i = 2"
            );

            try {
                Assert.IsTrue(generatedJs.Contains("ref */ i"));
                Assert.IsFalse(generatedJs.ToLower().Contains("unmaterialized"));
                Assert.IsTrue(generatedJs.Contains("instance.Method("));
            } catch {
                Console.WriteLine(generatedJs);

                throw;
            }
        }

        [Test]
        public void PrivateNames () {
            using (var test = MakeTest(@"SpecialTestCases\PrivateNames.cs"))
                test.Run();
        }

        [Test]
        public void ForLoops () {
            var generatedJs = GetJavascript(
                @"SpecialTestCases\ForLoops.cs",
                "0\r\n1\r\n2\r\n3\r\n4\r\n5\r\n6\r\n7\r\n8\r\n9\r\n5\r\n3\r\n1"
            );

            try {
                Assert.IsFalse(generatedJs.Contains("while"));
                Assert.AreEqual(4, generatedJs.Split(new string[] { "for (" }, StringSplitOptions.RemoveEmptyEntries).Length);
            } catch {
                Console.WriteLine(generatedJs);

                throw;
            }
        }

        [Test]
        public void OuterThisNotUsedForDelegateNew () {
            var generatedJs = GetJavascript(
                @"SpecialTestCases\OuterThisDelegateNew.cs",
                "PrintNumber(1)\r\nMyClass.PrintNumber(2)"
            );

            try {
                Assert.IsFalse(generatedJs.Contains("outer_this"));
            } catch {
                Console.WriteLine(generatedJs);

                throw;
            }
        }

        [Test]
        [FailsOnMono]
        public void FlagsEnumsWithZeroValues () {
            var generatedJs = GetJavascript(
                @"SpecialTestCases\FlagsEnumsWithZeroValues.cs",
                "B A\r\nB A"
            );
            try {
                Assert.IsFalse(generatedJs.Contains("| $asm01.Program.SimpleEnum.E"));
                Assert.IsFalse(generatedJs.Contains("| $asm01.Program.SimpleEnum.A"));
                Assert.IsFalse(generatedJs.Contains("$asm01.Program.SimpleEnum.E |"));
                Assert.IsFalse(generatedJs.Contains("$asm01.Program.SimpleEnum.A |"));
            } catch {
                Console.WriteLine(generatedJs);

                throw;
            }
        }

        [Test]
        public void DoLoops () {
            var generatedJs = GetJavascript(
                @"SpecialTestCases\DoLoops.cs",
                "0\r\n1\r\n2\r\n3\r\n4\r\n1\r\n16"
            );

            try {
                Assert.IsFalse(generatedJs.Contains("for ("), "A for loop failed conversion to a do-loop");
                Assert.AreEqual(3, generatedJs.Split(new string[] { "do {" }, StringSplitOptions.RemoveEmptyEntries).Length);
                Assert.AreEqual(3, generatedJs.Split(new string[] { "} while (" }, StringSplitOptions.RemoveEmptyEntries).Length);
                Assert.IsTrue(generatedJs.Contains("while (true)"));
                Assert.IsTrue(generatedJs.Contains("break $loop2"));
            } catch {
                Console.WriteLine(generatedJs);

                throw;
            }
        }

        [Test]
        public void UntranslatableGotos () {
            var generatedJs = GetJavascript(
                @"TestCases\UntranslatableGotoOutParameters.cs",
                ":"
            );

            try {
                Assert.IsFalse(generatedJs.Contains("JSIL.UntranslatableInstruction"), "A goto failed translation");
            } catch {
                Console.WriteLine(generatedJs);

                throw;
            }
        }

        [Test]
        [FailsOnMono]
        public void UntranslatableGotos2 () {
            var generatedJs = GetJavascript(
                @"TestCases\RepeatIterator.cs",
                "a\r\na\r\na\r\na\r\na"
            );

            try {
                Assert.IsFalse(generatedJs.Contains("JSIL.UntranslatableInstruction"), "A goto failed translation");

                var m = Regex.Match(
                    generatedJs,
                    @"if \(\(this.(\$li\$g5__1|i) \| 0\) \>\= \(this.(\$l\$g3__count|count) \| 0\)\) \{[^}]*\} else \{"
                );
                bool foundElse = (m != null) && m.Success;
                
                m = Regex.Match(
                    generatedJs,
                    @"if \(\(this.(\$li\$g5__1|i) \| 0\) \< \(this.(\$l\$g3__count|count) \| 0\)\) \{[^}]*\}"
                );
                bool foundIf = (m != null) && m.Success;

                Assert.IsTrue(foundElse || foundIf, "Looked for else or if");

                if (foundElse) {
                    Assert.IsTrue(m.Value.Contains("continue $labelgroup0;"), "If block true clause left empty when hoisting out label");
                } else {
                    Assert.IsTrue(m.Value.Contains("return "), "Return statement not in true clause");
                }
            } catch {
                Console.WriteLine(generatedJs);

                throw;
            }
        }

        [Test]
        public void SealedMethods () {
            var generatedJs = GetJavascript(
                @"TestCases\SealedMethods.cs",
                "Foo.Func1\r\nFoo.Func2\r\nFoo.Func1"
            );

            try {
                Assert.IsFalse(generatedJs.Contains("Foo.prototype.Func1.call"), "Func1 was called through the prototype with an explicit this");
                Assert.IsTrue(generatedJs.Contains("this.Func1"), "Func1 was not called on this");
            } catch {
                Console.WriteLine(generatedJs);

                throw;
            }
        }

        [Test]
        public void SealedMethods2 () {
            var output = "F1 F2 F1 F1 B1 B2 F2 F1 B3 B2 F2 F1";

            var generatedJs = GenericTest(
                @"TestCases\SealedMethods2.cs",
                output, output
            );

            try {
                Assert.IsTrue(generatedJs.Contains("Foo.prototype.Func1.call"), "Func1 was not called through the Foo prototype");
                Assert.IsTrue(generatedJs.Contains("Foo.prototype.Func2.call"), "Func2 was not called through the Foo prototype");
                Assert.IsTrue(generatedJs.Contains("this.Func2()"), "Func2 was not called through this");
                Assert.IsTrue(generatedJs.Contains("this.Func2()"), "Func2 was not called through this");

                Assert.IsTrue(generatedJs.Contains("test.Func1()"), "Func1 was not called directly on test");
                Assert.IsTrue(generatedJs.Contains("test.Func2()"), "Func2 was not called directly on test");

                Assert.IsTrue(generatedJs.Contains("test2.Func1()"), "Func1 was not called directly on test");
                Assert.IsTrue(generatedJs.Contains("test2.Func2()"), "Func2 was not called directly on test");
                Assert.IsTrue(generatedJs.Contains("test2.Func3()"), "Func3 was not called directly on test");
            } catch {
                Console.WriteLine(generatedJs);

                throw;
            }
        }

        [Test]
        public void UnderivedMethods () {
            var generatedJs = GetJavascript(
                @"TestCases\UnderivedMethods.cs",
                "Foo.Func1\r\nFoo.Func2\r\nFoo.Func1"
            );

            try {
                Assert.IsFalse(generatedJs.Contains("Foo.prototype.Func1.call"), "Func1 was called through the prototype with an explicit this");
                Assert.IsTrue(generatedJs.Contains("this.Func1"), "Func1 was not called on this");
            } catch {
                Console.WriteLine(generatedJs);

                throw;
            }
        }

        [Test]
        public void GenericMethodSignatures () {
            var generatedJs = GetJavascript(
                @"SpecialTestCases\GenericMethodSignatures.cs",
                "1"
            );

            try {
                Assert.IsTrue(generatedJs.Contains("\"!!0\", [\"!!0\"], [\"T\"]"));
            } catch {
                Console.WriteLine(generatedJs);

                throw;
            }
        }

        [Test]
        public void FastOverloadDispatch () {
            var output = "A()\r\nA(1)\r\nA(1, str)\r\nB()\r\nB(int 1)\r\nB(string str)";

            var generatedJs = GenericTest(
                @"SpecialTestCases\FastOverloadDispatch.cs",
                output, output
            );

            try {
                Assert.IsFalse(generatedJs.Contains("CallStatic($thisType, \"A\", "));
                Assert.IsTrue(generatedJs.Contains("$thisType.B();"));
                Assert.IsTrue(generatedJs.Contains("CallStatic($thisType, \"B\", "));
            } catch {
                Console.WriteLine(generatedJs);

                throw;
            }
        }

        [Test]
        public void FastConstructorOverloadDispatch () {
            var output = "A()\r\nA(1)\r\nB()\r\nB(int 1)\r\nB(string s)";

            var generatedJs = GenericTest(
                @"SpecialTestCases\FastConstructorOverloadDispatch.cs",
                output, output
            );

            try {
                Assert.IsTrue(generatedJs.Contains(".Construct()"));
                Assert.IsTrue(generatedJs.Contains(".Construct(1)"));
                Assert.IsTrue(generatedJs.Contains(".Construct(\"s\")"));
                Assert.IsFalse(generatedJs.Contains("new A"));
                Assert.IsFalse(generatedJs.Contains("new B"));
            } catch {
                Console.WriteLine(generatedJs);

                throw;
            }
        }

        [Test]
        public void OverloadedGenericMethodSignatures () {
            var output = "IsNullOrEmpty with 1 parameters\r\nAny with one argument\r\nfalse";

            var typeInfo = MakeDefaultProvider();

            Action check = () => {
                var generatedJs = GenericTest(
                    @"SpecialTestCases\OverloadedGenericMethodSignatures.cs",
                    output, output, null, typeInfo
                );

                try {
                    Assert.IsTrue(generatedJs.Contains("function CommonExtensionMethodsSimple_Any$b1$00 (TSource, source)"));
                    Assert.IsTrue(generatedJs.Contains("function CommonExtensionMethodsSimple_Any$b1$01 (TSource, source, predicate)"));
                    Assert.IsTrue(generatedJs.Contains("function CommonExtensionMethodsSimple_Any$b1$00 (TSource, source)"));
                    Assert.IsTrue(generatedJs.Contains("function CommonExtensionMethodsSimple_Any$b1$01 (TSource, source, predicate)"));
                } catch {
                    Console.WriteLine(generatedJs);

                    throw;
                }
            };

            for (var i = 0; i < 3; i++)
                check();
        }

        [Test]
        public void OverloadedGenericMethodSignatures2 () {
            var output = "A2\r\nB";

            var typeInfo = MakeDefaultProvider();

            Action check = () => {
                var generatedJs = GenericTest(
                    @"SpecialTestCases\OverloadedGenericMethodSignatures2.cs",
                    output, output, null, typeInfo
                );

                try {
                    Assert.IsTrue(generatedJs.Contains("this.Test("), "this.Test was not direct-dispatched");
                    // FIXME: Is this right?
                    Assert.IsTrue(
                        generatedJs.Contains("Interface.Test2.Call(") ||
                        (
                            generatedJs.ContainsRegex(@"\$IM([0-9]*) = JSIL.Memoize\(\$asm([0-9]*).Interface.Test2\)") &&
                            generatedJs.ContainsRegex(@"\$IM([0-9]*)\(\).Call\(")
                        ), 
                    "test.Interface_Test2 was not direct-dispatched");
                } catch {
                    Console.WriteLine(generatedJs);

                    throw;
                }
            };

            for (var i = 0; i < 3; i++)
                check();
        }

        [Test]
        public void CustomObjectMethods () {
            var output = "";

            var generatedJs = GenericTest(
                @"SpecialTestCases\CustomObjectMethods.cs",
                output, output
            );

            try {
                Assert.IsFalse(generatedJs.Contains("JSIL.ObjectEquals("), "Base Object.Equals was used");
                Assert.IsFalse(generatedJs.Contains("System.ValueType.$Cast("), "Cast to struct was generated");
            } catch {
                Console.WriteLine(generatedJs);

                throw;
            }
        }

        [Test]
        public void CustomEqualsDispatch () {
            var output = "";

            var generatedJs = GenericTest(
                @"SpecialTestCases\CustomEqualsDispatch.cs",
                output, output
            );

            try {
                Assert.IsFalse(generatedJs.Contains(".CallVirtual"), "CallVirtual was used");
                Assert.IsFalse(generatedJs.Contains(".Call"), "Call was used");

                Assert.IsTrue(generatedJs.Contains("this.Equals("), "Equals was not invoked on this");
                Assert.IsTrue(generatedJs.Contains("a.Equals(b)"), "Equals was not invoked on a and b");
                Assert.IsFalse(generatedJs.Contains(".Object_Equals("), "Object_Equals was used");
            } catch {
                Console.WriteLine(generatedJs);

                throw;
            }
        }

        [Test]
        public void ReplaceConstructorAndFieldNames () {
            var generatedJs = GenericTest(
                @"SpecialTestCases\ReplaceConstructorAndFieldNames.cs",
                "Field = 1, Property = 2", "Field = 2, Property = 4"
            );

            try {
                Assert.IsFalse(generatedJs.Contains("ProxiedClassProxy$"));
            } catch {
                Console.WriteLine(generatedJs);

                throw;
            }
        }

        [Test]
        public void NoUnnecessaryCasts () {
            var testNames = new string[] {
                @"FailingTestCases\ArrayToString.cs",
                @"SimpleTestCases\CollectionInitializers.cs",
                @"TestCases\DictionaryInitializer2.cs",
            };

            RunComparisonTests(testNames, null, null, 
                (test) => false, 
                (csharp, js) => {
                    Assert.IsFalse(
                        js().Contains("JSIL.Cast("),
                        "JS output should not contain any casts"
                    );

                    Assert.IsFalse(
                        js().Contains("JSIL.TryCast("),
                        "JS output should not contain any casts"
                    );

                    Assert.IsFalse(
                        js().Contains(".$Cast"),
                        "JS output should not contain any casts"
                    );

                    Assert.IsFalse(
                        js().Contains(".$TryCast"),
                        "JS output should not contain any casts"
                    );
                }
            );
        }

        [Test]
        public void AutoPropertyEfficiency () {
            var output = "a=0 b=0 c=1";
            var generatedJs = GenericTest(
                @"SpecialTestCases\AutoPropertyEfficiency.cs",
                output, output
            );

            try {
                Assert.IsFalse(
                    generatedJs.Contains("instance.A") ||
                    generatedJs.Contains("instance.get_A")
                );
                Assert.IsTrue(
                    generatedJs.Contains("instance.B") ||
                    generatedJs.Contains("instance.get_B")
                );
                Assert.IsTrue(
                    generatedJs.Contains("instance.C") ||
                    generatedJs.Contains("instance.get_C")
                );
            } catch {
                Console.WriteLine(generatedJs);

                throw;
            }
        }

        [Test]
        public void PropertyIncrementEfficiency () {
            var output = "0\r\n2\r\n4\r\n4";
            var generatedJs = GenericTest(
                @"SpecialTestCases\PropertyIncrementEfficiency.cs",
                output, output
            );

            try {
                Assert.IsFalse(
                    generatedJs.Contains("instance.Value"), "Property accessed directly"
                );
                Assert.IsTrue(
                    generatedJs.Contains("instance.get_Value")
                );
                Assert.IsTrue(
                    generatedJs.Contains("instance.set_Value")
                );
            } catch {
                Console.WriteLine(generatedJs);

                throw;
            }
        }

        [Test]
        public void StructConstructorInvocationEfficiency () {
            var output = "ct=3, mc=(a=2 b=1)";
            var generatedJs = GenericTest(
                @"SpecialTestCases\StructCtorInvocation.cs",
                output, output
            );

            try {
                Assert.IsFalse(
                    generatedJs.Contains("CustomType.prototype._ctor"), "CustomType constructor invoked indirectly"
                );
                Assert.IsFalse(
                    generatedJs.Contains("CustomType();"), "CustomType instance constructed without arguments"
                );
            } catch {
                Console.WriteLine(generatedJs);

                throw;
            }
        }

        // Ignored because it's pretty slow and for some reason it never fails :/
        [Ignore]
        [Test]
        public void NoDoubleStructClonesWithThreadingEnabled () {
            var testPath = Path.GetFullPath(Path.Combine(ComparisonTest.TestSourceFolder, "AnalysisTestCases"));
            var testNames = Directory.GetFiles(testPath, "*.cs").Concat(Directory.GetFiles(testPath, "*.vb")).OrderBy((s) => s).ToArray();

            RunComparisonTests(testNames, null, null,
                (test) => false,
                (csharp, js) => {
                    Assert.IsFalse(
                        js().Contains("MemberwiseClone().MemberwiseClone()"),
                        "JS output should never contain a duplicate struct MemberwiseClone"
                    );
                },
                getConfiguration: () => {
                    var cfg = MakeConfiguration();
                    cfg.CodeGenerator.EnableThreadedTransforms = true;
                    return cfg;
                }
            );
        }

        [Test]
        public void InterfaceVariance () {
            var output = "";
            var generatedJs = GenericTest(
                @"SpecialTestCases\InterfaceVariance.cs",
                output, output
            );

            try {
                Assert.IsTrue(
                    generatedJs.Contains("\"U\").in()"), "B`1.U missing variance indicator"
                );
                Assert.IsTrue(
                    generatedJs.Contains("\"V\").out()"), "C`1.V missing variance indicator"
                );
                Assert.IsTrue(
                    generatedJs.Contains("\"in U\""), "U name missing variance indicator"
                );
                Assert.IsTrue(
                    generatedJs.Contains("\"out V\""), "V name missing variance indicator"
                );
            } catch {
                Console.WriteLine(generatedJs);

                throw;
            }
        }

        [Test]
        public void SpuriousIntegerHints () {
            var output = "0F0F\r\n7773";
            var generatedJs = GenericTest(
                @"SpecialTestCases\SpuriousIntegerHints.cs",
                output, output
            );

            try {
                Assert.IsFalse(
                    generatedJs.Contains(" & 15) | 0)"),
                    "A | 0 type hint surrounds a bitwise operator"
                );

                Assert.IsFalse(
                    generatedJs.Contains(")) | 0"),
                    "Double right paren before a truncation"
                );
            } catch {
                Console.WriteLine(generatedJs);

                throw;
            }
        }

        [Test]
        public void PointerTypesInSignatures () {
            var output = "";
            var generatedJs = GetJavascript(
                @"SpecialTestCases\PointerSignature.cs",
                output,
                makeConfiguration: () => {
                    var cfg = MakeConfiguration();
                    cfg.CodeGenerator.EnableUnsafeCode = true;
                    return cfg;
                }
            );

            try {
                Assert.IsTrue(
                    generatedJs.Contains("TypeRef(\"JSIL.Pointer\""),
                    "Pointer types not emitted in method signatures"
                );
            } catch {
                Console.WriteLine(generatedJs);

                throw;
            }

        }

        [Test]
        public void InitializeStructClone () {
            var output = "";
            var generatedJs = GetJavascript(
                @"SpecialTestCases\InitializeStructClone.cs",
                output
            );

            try {
                Assert.AreEqual(
                    generatedJs.IndexOf(".MemberwiseClone("),
                    generatedJs.LastIndexOf(".MemberwiseClone("),
                    "A struct was cloned more than once"
                );
            } catch {
                Console.WriteLine(generatedJs);

                throw;
            }
        }

        [Test]
        public void ClosedGenericSignatures () {
            var output = "";
            var generatedJs = GetJavascript(
                @"SpecialTestCases\ClosedGenericSignatures.cs",
                output,
                makeConfiguration: () => {
                    var cfg = MakeConfiguration();
                    cfg.UseThreads = false;
                    return cfg;
                }
            );

            try {
                Assert.IsFalse(
                    generatedJs.Contains("var $s00 = new JSIL.MethodSignature"),
                    "DrawBatch function has a locally cached method signature"
                );
            } catch {
                Console.WriteLine(generatedJs);

                throw;
            }
        }

        [Test]
        public void ClassMarkedWithMetaAttibutes()
        {
            var output = "";
            var generatedJs = GetJavascript(
                @"SpecialTestCases\ClassMarkedWithMetaAttibutes.cs",
                output
                );

            try
            {
                Assert.IsTrue(
                    generatedJs.Contains("function ClassThatShouldBeStubbed"),
                    "Class marked with JSStubOnly should be translated as stub");
                Assert.IsTrue(
                    generatedJs.Contains("$.ExternalMethod({Static:true , Public:true }, \"MethodInStubbedClass\""),
                    "Method inside class marked with JSStubOnly should have declaration as ExternalMethod");
                Assert.IsTrue(
                    generatedJs.Contains("JSIL.MakeExternalType(\"ClassThatShouldBeExternal\""),
                    "Class marked with JSExternal should be translated with MakeExternalType declaration");
                Assert.IsFalse(
                    generatedJs.Contains("MethodInExternalClass"),
                    "Method inside class marked with JSExternal should not be translated");
                Assert.IsFalse(
                    generatedJs.Contains("ClassThatShouldBeIgnored"),
                    "Class marked with JSIgnore should not be translated");
                Assert.IsFalse(
                    generatedJs.Contains("MethodInIgnoredClass"),
                    "Method inside class marked with JSIgnore should not be translated");
            }
            catch
            {
                Console.WriteLine(generatedJs);

                throw;
            }
        }

        [Test]
        public void InnerClassNameFormatting_Issue352()
        {
            var output = "";
            var generatedJs = GetJavascript(
                @"SpecialTestCases\InnerClassNameFormatting_Issue352.cs",
                output
                );

            try
            {
                Assert.IsTrue(
                    generatedJs.Contains("\"Program+InnerGenericClass`1\""),
                    "Inner class should be named in Outer+Inner format");
                Assert.IsTrue(
                    generatedJs.Contains("$.GenericParameter(\"T\")"),
                    "Generic parameter for inner class should reference class via $");
            }
            catch
            {
                Console.WriteLine(generatedJs);

                throw;
            }
        }

        [Test]
        public void PreventFastMethodDispatcherIfHideBase_Issue368()
        {
            var output = "";
            var generatedJs = GetJavascript(
                @"SpecialTestCases\PreventFastMethodDispatcherIfHideBase_Issue368.cs",
                output
                );

            try
            {
                Assert.IsFalse(
                    generatedJs.Contains("bas.Method();"),
                    "Base.Method should not used fast dispatcher as it may be hidden by Derived.Method");
                Assert.IsFalse(
                    generatedJs.Contains("bas.MethodWithParameter1();"),
                    "Base.MethodWithParameter1 should not used fast dispatcher as it may be hidden by Derived.MethodWithParameter1");
                Assert.IsFalse(
                    generatedJs.Contains("bas.MethodWithParameter2();"),
                    "Base.MethodWithParameter2 should not used fast dispatcher as it may be hidden by Derived.MethodWithParameter2");

                Assert.IsFalse(
                    generatedJs.Contains("derived.Method();"),
                    "Derived.Method should not used fast dispatcher as it is hidden by Base.Method");
                Assert.IsFalse(
                    generatedJs.Contains("derived.MethodWithParameter1();"),
                    "Derived.MethodWithParameter1 should not used fast dispatcher as it is hidden by Base.MethodWithParameter1");
                Assert.IsFalse(
                    generatedJs.Contains("derived.MethodWithParameter2();"),
                    "Derived.MethodWithParameter2 should not used fast dispatcher as it is hidden by Base.MethodWithParameter2");

                Assert.IsTrue(
                    generatedJs.Length - generatedJs.Replace("bas.AnotherMethod();", string.Empty).Length == "bas.AnotherMethod();".Length * 2,
                    "Base.AnotherMethod should use fast dispatcher");
                Assert.IsTrue(
                    generatedJs.Contains("derived.AnotherMethod();"),
                    "Base.AnotherMethod should use fast dispatcher even if called on Dervided instance");

            }
            catch
            {
                Console.WriteLine(generatedJs);

                throw;
            }
        }

        [Test]
        public void DoubleFloatCasts () {
            var output = "1.0 1.0\r\n2 2.5\r\n1 1.5\r\n10101010101.01010\r\ntruncated";
            var testFile = @"SpecialTestCases\DoubleFloatCasts.cs";
            GenericTest(testFile, output, output);

            var generatedJs = GetJavascript(testFile);

            try {
                Assert.IsFalse(generatedJs.Contains("$Cast"));
                Assert.IsTrue(generatedJs.Contains("Math.fround"));
            } catch {
                Console.WriteLine(generatedJs);

                throw;
            }
        }

        [Test]
        public void MgTextureReader () {
            var testFile = @"SpecialTestCases\MgTextureReader.cs";

            var generatedJs = GetJavascript(testFile);

            try {
                Assert.IsTrue(generatedJs.Contains("Math.imul(y, pitch) + Math.imul(x, bytesPerPixel)"));
                Assert.IsTrue(generatedJs.Contains("Math.imul(y, pitch) + Math.imul(x, 4)"));
                // TODO: Optimize out the double & here?
                Assert.IsTrue(generatedJs.Contains("((color >> 16) & 255 & 0xFF)"));
            } catch {
                Console.WriteLine(generatedJs);

                throw;
            }
        }

        [Test]
        public void MgDxtDecode () {
            var testFile = @"SpecialTestCases\MgDxtDecode.cs";

            var generatedJs = GetJavascript(testFile);

            try {
                Assert.IsTrue(generatedJs.Contains("var temp = ((Math.imul"), "ILSpy suppressed imul");
                // FIXME: I think there's another error here to test for.
            } catch {
                Console.WriteLine(generatedJs);

                throw;
            }
        }

        [Test]
        public void MgDelegateFieldNames () {
            var testFile = @"BinaryTestCases\MgFuseePackedVertices.exe";
            var generatedJs = GetJavascript(testFile);

            try {
                Assert.IsFalse(generatedJs.Contains("$thisType.$mg ==="), "Field names were truncated");
            } catch {
                Console.WriteLine(generatedJs);
            }
        }
    }
}
