﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Web.Script.Serialization;
using JSIL.Compiler.Extensibility;
using JSIL.Utilities;
using Microsoft.Build.Evaluation;
using Microsoft.Xna.Framework;

namespace JSIL.Compiler.Profiles {
    public class XNA4 : BaseProfile {
        public HashSet<string> ContentProjectsProcessed = new HashSet<string>();

        public override bool IsAppropriateForSolution (SolutionBuilder.BuildResult buildResult) {
            return buildResult.TargetFilesUsed.Any(
                (targetFile) => targetFile.Contains(@"XNA Game Studio\v4.0")
            );
        }

        public override Configuration GetConfiguration (Configuration defaultConfiguration) {
            var result = defaultConfiguration.Clone();

            result.FrameworkVersion = 4.0;
            result.Assemblies.Proxies.Add("%jsildirectory%/JSIL.Proxies.XNA4.dll");

            return (Configuration)result;
        }

        public override TranslationResult Translate (
            VariableSet variables, AssemblyTranslator translator, Configuration configuration, string assemblyPath, bool scanForProxies
        ) {
            var result = translator.Translate(assemblyPath, scanForProxies);

            PostProcessAllTranslatedAssemblies(configuration, assemblyPath, result);

            result.AddFile("Script", "XNA.Colors.js", new ArraySegment<byte>(Encoding.UTF8.GetBytes(
                Common.MakeXNAColors()
            )), 0);

            return result;
        }

        public override void ProcessSkippedAssembly (
            Configuration configuration, string assemblyPath, TranslationResult result
        ) {
            PostProcessAssembly(configuration, assemblyPath, result);
        }

        public override SolutionBuilder.BuildResult ProcessBuildResult (
            VariableSet variables, Configuration configuration, SolutionBuilder.BuildResult buildResult
        ) {
            Common.ProcessContentProjects(variables, configuration, buildResult, ContentProjectsProcessed);

            CopiedOutputGatherer.GatherFromProjectFiles(
                variables, configuration, buildResult
            );

            return base.ProcessBuildResult(variables, configuration, buildResult);
        }
    }
}
