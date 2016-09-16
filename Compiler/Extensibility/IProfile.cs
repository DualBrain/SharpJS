﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace JSIL.Compiler.Extensibility {
    public interface IProfile : ICompilerExtension {
        bool IsAppropriateForSolution (SolutionBuilder.BuildResult buildResult);

        SolutionBuilder.BuildResult ProcessBuildResult (
            VariableSet variables, Configuration configuration, SolutionBuilder.BuildResult buildResult
        );
        Configuration GetConfiguration (
            Configuration defaultConfiguration
        );
        TranslationResultCollection Translate (
            VariableSet variables, AssemblyTranslator translator, 
            Configuration configuration, string assemblyPath, bool scanForProxies
        );
        void WriteOutputs (
            VariableSet variables, TranslationResultCollection result, string path, bool quiet
        );

        void RegisterPostprocessors (IEnumerable<IEmitterGroupFactory> emitters, Configuration configuration, string assemblyPath, string[] skippedAssemblies);
    }
}
